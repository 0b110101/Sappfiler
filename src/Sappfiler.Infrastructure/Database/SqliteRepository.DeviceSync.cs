using System.Globalization;
using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// 设备同步的本地存储层（Outbox / 墓碑 / 同步游标 / 设备）。
///
/// **为什么单独一个文件**：`SqliteRepository.cs` 与 `feature/multi-backend-sync` 分支都会改动，
/// 把同步相关代码全部放进这个**新增**的 partial 文件，可让两分支的合并冲突面几乎为零
/// （主文件只动了一个 `partial` 关键字）。
///
/// ⚠️ **同步是旁路能力**：这里的方法都不得反过来影响本地计时/心跳的可靠性。
///    网络失败、后端异常一律不许让本地记录变少。
/// </summary>
public partial class SqliteRepository
{
    // ============================ Outbox ============================

    /// <summary>
    /// 把一条变更写入 Outbox（<c>payload</c> 是**变更发生那一刻的快照**）。
    ///
    /// 单独入队只适用于"没有业务表事务可搭车"的场景；
    /// 会话结束这类必须与业务写入**同事务**的，走一体化方法（如 <c>EndSessionWithOutboxAsync</c>）。
    /// </summary>
    public async Task EnqueueOutboxAsync(SyncQueueItem item)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await EnqueueOutboxCoreAsync(conn, null, item);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Outbox 的核心写入，供"同事务"场景复用（<paramref name="tx"/> 为 null 即自动提交）。</summary>
    private static async Task EnqueueOutboxCoreAsync(SqliteConnection conn, SqliteTransaction? tx, SyncQueueItem item)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO sync_queue
                (queue_id, entity_type, entity_global_id, operation, base_version,
                 payload, created_at_utc, retry_count, next_retry_at_utc, status, last_error)
            VALUES
                (@QueueId, @EntityType, @EntityGlobalId, @Operation, @BaseVersion,
                 @Payload, @CreatedAtUtc, @RetryCount, @NextRetryAtUtc, @Status, @LastError);
            """,
            new
            {
                item.QueueId,
                item.EntityType,
                item.EntityGlobalId,
                Operation = (int)item.Operation,
                item.BaseVersion,
                item.Payload,
                item.CreatedAtUtc,
                item.RetryCount,
                item.NextRetryAtUtc,
                Status = (int)item.Status,
                item.LastError
            },
            tx);
    }

    /// <summary>
    /// 取出待推送的变更。按 <c>rowid</c> 升序 = **入队顺序**：
    /// 同一秒内写入多条时 <c>created_at_utc</c> 分辨不出先后，而顺序对同步语义有影响
    /// （同一实体的 Create 必须先于后续变更）。rowid 是 SQLite 内建的单调插入序号。
    /// </summary>
    public async Task<IReadOnlyList<SyncQueueItem>> GetPendingOutboxAsync(int limit = SyncOutboxPolicy.PushBatchSize)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<SyncQueueItem>(
            """
            SELECT * FROM sync_queue
            WHERE status = @pending
              AND (next_retry_at_utc IS NULL OR next_retry_at_utc <= @now)
            ORDER BY rowid ASC
            LIMIT @limit;
            """,
            new { pending = (int)SyncQueueStatus.Pending, now = UtcNowIso(), limit });

        return rows.ToList();
    }

    /// <summary>标记为已完成（**只有收到服务器 ACK 之后**才允许调用）。</summary>
    public async Task<int> MarkOutboxCompletedAsync(IReadOnlyList<string> queueIds)
    {
        if (queueIds.Count == 0) return 0;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            return await conn.ExecuteAsync(
                """
                UPDATE sync_queue
                SET status = @completed, last_error = NULL, next_retry_at_utc = NULL
                WHERE queue_id IN @ids;
                """,
                new { completed = (int)SyncQueueStatus.Completed, ids = queueIds });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 记录一次推送失败：retry_count+1、写入错误、按退避档位安排下次重试。
    /// 状态回到 Pending —— "等一会儿再试"和"放弃"是两回事；
    /// **同步失败绝不能让本地待发记录消失**。
    /// </summary>
    public async Task RecordOutboxFailureAsync(string queueId, string error, TimeSpan retryDelay)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE sync_queue
                SET retry_count       = retry_count + 1,
                    last_error        = @error,
                    next_retry_at_utc = @next,
                    status            = @pending
                WHERE queue_id = @queueId;
                """,
                new
                {
                    queueId,
                    error,
                    next = UtcNowIso(DateTime.UtcNow.Add(retryDelay)),
                    pending = (int)SyncQueueStatus.Pending
                });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 标记为**终态失败（死信）**：服务端明确拒绝且重试没有意义
    /// （典型情形：该实体云端已有墓碑，再推多少次都会被拒）。
    /// 不会再被 <see cref="GetPendingOutboxAsync"/> 取出重试，但记录留在库里便于排查。
    /// </summary>
    public async Task<int> MarkOutboxFailedAsync(string queueId, string error)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            return await conn.ExecuteAsync(
                """
                UPDATE sync_queue
                SET status = @failed, last_error = @error, next_retry_at_utc = NULL
                WHERE queue_id = @queueId;
                """,
                new { queueId, error, failed = (int)SyncQueueStatus.Failed });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 启动时调用：把上次进程被杀/崩溃时停在 InFlight 的记录放回 Pending。
    /// 之所以敢无条件重置：推送以 <c>entity_global_id</c> 为幂等键，重复推送不会产生重复数据。
    /// </summary>
    public async Task<int> ResetInFlightOutboxAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            return await conn.ExecuteAsync(
                """
                UPDATE sync_queue
                SET status = @pending, next_retry_at_utc = NULL
                WHERE status = @inFlight;
                """,
                new { pending = (int)SyncQueueStatus.Pending, inFlight = (int)SyncQueueStatus.InFlight });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>清理"已完成且已过保留期"的 Outbox 记录（Outbox 不是历史表）。</summary>
    public async Task<int> PruneCompletedOutboxAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            return await conn.ExecuteAsync(
                """
                DELETE FROM sync_queue
                WHERE status = @completed AND created_at_utc < @cutoff;
                """,
                new
                {
                    completed = (int)SyncQueueStatus.Completed,
                    cutoff = UtcNowIso(DateTime.UtcNow - SyncOutboxPolicy.CompletedRetention)
                });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 把待推送的记录标记为 InFlight（推送状态机 <c>Pending → InFlight → ACK → Completed</c>）。
    /// 只动仍是 Pending 的行 —— 避免把已经被别的循环改过的记录覆盖掉。
    /// </summary>
    public async Task<int> MarkOutboxInFlightAsync(IReadOnlyList<string> queueIds)
    {
        if (queueIds.Count == 0) return 0;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            return await conn.ExecuteAsync(
                """
                UPDATE sync_queue
                SET status = @inFlight
                WHERE queue_id IN @ids AND status = @pending;
                """,
                new
                {
                    ids = queueIds,
                    inFlight = (int)SyncQueueStatus.InFlight,
                    pending = (int)SyncQueueStatus.Pending
                });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ==================== 结束会话：本地写入 + Outbox 原子化（Phase 2d） ====================

    private const string SessionSyncColumns = """
        SELECT id             AS Id,
               game_id        AS GameId,
               process_name   AS ProcessName,
               start_time     AS StartTime,
               duration_seconds AS DurationSeconds,
               global_id      AS GlobalId,
               device_id      AS DeviceId,
               started_at_utc AS StartedAtUtc,
               ended_at_utc   AS EndedAtUtc,
               time_precision AS TimePrecision
        FROM sessions
        """;

    private sealed class SessionSyncRow
    {
        public int Id { get; set; }
        public int GameId { get; set; }
        public string? ProcessName { get; set; }
        public string? StartTime { get; set; }
        public int DurationSeconds { get; set; }
        public string? GlobalId { get; set; }
        public string? DeviceId { get; set; }
        public string? StartedAtUtc { get; set; }
        public string? EndedAtUtc { get; set; }
        public string? TimePrecision { get; set; }
    }

    /// <summary>
    /// 结束会话，并把"这一次结束"涉及的全部本地写入与同步 Outbox 放进**同一个事务**。
    ///
    /// 事务内顺序（用户 2026-10-03 冻结，不要随意调整）：
    /// <code>
    /// BEGIN
    ///   → 确保 game.global_id
    ///   → 确保 session.global_id / device_id
    ///   → 写 started_at_utc / ended_at_utc
    ///   → 落尾段 daily_summary（跨结算点可能是两笔）
    ///   → 重算 session_count
    ///   → 结束 session
    ///   → 用**事务内最终确定的数据**生成 immutable payload 快照
    ///   → 写 sync_queue
    /// COMMIT
    /// </code>
    ///
    /// 三条铁律：
    ///   1. **已存在的 global_id 绝不重新生成**（换 identity 等于换实体）。
    ///   2. payload 必须来自**事务内最终确定**的数据 —— 不能先把 payload 生成好再回头补 identity，
    ///      否则 queue 里会存下 NULL / 旧 identity，以后再也对不上。
    ///   3. 任一步失败 → 整体 ROLLBACK；会话仍由既有恢复机制
    ///      （<c>CleanupStaleSessionsAsync</c> + 启动时的活跃会话恢复）接住，
    ///      **绝不因为同步能力而丢掉本地计时**。
    ///
    /// ⚠️ 心跳路径**不经过这里**：运行中的会话只落本地 daily_summary，结束之后才产生同步事实。
    /// </summary>
    public async Task<EndSessionOutboxResult> EndSessionWithOutboxAsync(
        int sessionId,
        DateTime endTime,
        int durationSeconds,
        IReadOnlyList<DailyDurationDelta> dailyDeltas)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var row = await conn.QuerySingleOrDefaultAsync<SessionSyncRow>(
                SessionSyncColumns + " WHERE id = @sessionId;", new { sessionId }, tx);

            if (row is null)
            {
                // 会话不存在：什么都不做（调用方照常触发 SessionEnded）。
                return new EndSessionOutboxResult(false, false, null, null, durationSeconds, null);
            }

            // ---- 本机稳定设备身份：device_id 一经生成永不改变，也**不会因电脑改名而变** ----
            var deviceId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM settings WHERE key = @k;",
                new { k = DeviceSyncConstants.SettingKeyDeviceId }, tx);

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                deviceId = DeviceSyncSchema.EnsureLocalDevice(conn, tx);
            }

            // ---- 确保 game.global_id：可同步的游戏必须拥有 identity（已有则绝不重新生成）----
            var gameIsSyncable = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM games WHERE id = @gameId AND {GameSyncEligibility.SqlPredicate};",
                new { gameId = row.GameId }, tx) > 0;

            if (gameIsSyncable)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE games SET global_id = @globalId
                    WHERE id = @gameId AND NULLIF(TRIM(global_id), '') IS NULL;
                    """,
                    new { globalId = Guid.NewGuid().ToString("N"), gameId = row.GameId }, tx);
            }

            // ---- session 的同步字段：在"实体定型"这一刻补齐（会话结束之后它不再变化）----
            var sessionGlobalId = string.IsNullOrWhiteSpace(row.GlobalId)
                ? Guid.NewGuid().ToString("N")
                : row.GlobalId!;
            var sessionDeviceId = string.IsNullOrWhiteSpace(row.DeviceId) ? deviceId! : row.DeviceId!;
            var startedAtUtc = string.IsNullOrWhiteSpace(row.StartedAtUtc)
                ? DeviceSyncSchema.LocalToUtcIso(row.StartTime) ?? string.Empty
                : row.StartedAtUtc!;

            // ended_at_utc 必须在"结束事件确定"的这一刻写入。
            var endTimeText = endTime.ToString("yyyy-MM-dd HH:mm:ss");
            var endedAtUtc = DeviceSyncSchema.LocalToUtcIso(endTimeText)
                ?? endTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

            var timePrecision = string.IsNullOrWhiteSpace(row.TimePrecision)
                ? TimePrecision.Exact
                : row.TimePrecision!;

            // ---- 尾段 daily_summary（逐字复刻 AddSessionDurationToDailyAsync 的语义）----
            foreach (var delta in dailyDeltas)
            {
                await ApplyDailyDurationDeltaAsync(conn, tx, delta.Date, row.GameId, delta.DurationSeconds);
            }

            // ---- 结束会话 + 同步字段落地（一条 UPDATE，避免"会话已结束但 identity 还没写"的中间态）----
            await conn.ExecuteAsync(
                """
                UPDATE sessions
                SET end_time         = @endTime,
                    duration_seconds = @durationSeconds,
                    is_active        = 0,
                    global_id        = @globalId,
                    device_id        = @deviceId,
                    started_at_utc   = @startedAtUtc,
                    ended_at_utc     = @endedAtUtc,
                    time_precision   = @timePrecision
                WHERE id = @sessionId;
                """,
                new
                {
                    sessionId,
                    endTime = endTimeText,
                    durationSeconds,
                    globalId = sessionGlobalId,
                    deviceId = sessionDeviceId,
                    startedAtUtc,
                    endedAtUtc,
                    timePrecision
                },
                tx);

            // ---- session_count 重算（逐字复刻 EndSessionAsync 的既有口径）----
            await RecomputeSessionCountAsync(conn, tx, sessionId, durationSeconds);

            // ---- 用**事务内最终确定**的数据生成不可变 payload 快照 ----
            // 特意从库里读回（而不是复用上面的局部变量）：这样 payload 与"最终提交的状态"天然一致，
            // 任何"忘记同步某个字段"都会在这一步暴露，而不是悄悄写进队列。
            var finalSession = await conn.QuerySingleAsync<SessionSyncRow>(
                SessionSyncColumns + " WHERE id = @sessionId;", new { sessionId }, tx);

            var gameGlobalId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT global_id FROM games WHERE id = @gameId;", new { gameId = finalSession.GameId }, tx);

            var enqueued = false;

            if (gameIsSyncable
                && !string.IsNullOrWhiteSpace(finalSession.GlobalId)
                && !string.IsNullOrWhiteSpace(gameGlobalId))
            {
                var payload = SyncPayloads.Serialize(new SessionSyncPayload
                {
                    GlobalId = finalSession.GlobalId!,
                    GameGlobalId = gameGlobalId!,
                    DeviceId = finalSession.DeviceId ?? string.Empty,
                    ProcessName = finalSession.ProcessName ?? string.Empty,
                    StartedAtUtc = finalSession.StartedAtUtc ?? string.Empty,
                    EndedAtUtc = finalSession.EndedAtUtc,
                    DurationSeconds = finalSession.DurationSeconds,
                    TimePrecision = string.IsNullOrWhiteSpace(finalSession.TimePrecision)
                        ? TimePrecision.Exact
                        : finalSession.TimePrecision!
                });

                await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
                {
                    QueueId = Guid.NewGuid().ToString("N"),
                    EntityType = nameof(SyncEntityType.Session),
                    EntityGlobalId = finalSession.GlobalId!,
                    Operation = SyncOperation.Create,
                    BaseVersion = 1,
                    Payload = payload,
                    CreatedAtUtc = UtcNowIso(),
                    Status = SyncQueueStatus.Pending
                });

                enqueued = true;
            }
            // 注：game 不可同步时（理论上不会发生——会话本身就让游戏具备同步资格）依旧完成本地结束，
            // 只是不产生同步事实。**本地计时永远优先于同步。**

            tx.Commit();

            return new EndSessionOutboxResult(
                true, enqueued, finalSession.GlobalId, gameGlobalId,
                finalSession.DurationSeconds, finalSession.EndedAtUtc);
        }
        catch (Exception ex)
        {
            // 事务失败：using 未 Commit 即 Dispose = 整体 ROLLBACK（daily / sessions / sync_queue 三者一致回滚）。
            // ⚠️ 这里**不回抛**：会话行保持 is_active = 1，既有的僵尸清理 / 启动恢复会把它接住；
            //    绝不让一次本地写入失败升级成监听循环的异常。
            AppLog.Error("[设备同步] 结束会话的原子写入失败（已整体回滚，会话仍可被恢复机制处理）", ex);
            return new EndSessionOutboxResult(false, false, null, null, durationSeconds, null);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 把一笔增量落进 daily_summary。**逐字复刻** <see cref="AddSessionDurationToDailyAsync"/> 的语义
    /// （含 session_count 的 CASE 口径与 <c>sync_status = 'pending'</c>），只是改成在调用方的事务里执行。
    /// ⚠️ 不要在这里"顺手优化"口径 —— 它写出的值必须与心跳路径完全一致。
    /// </summary>
    private static async Task ApplyDailyDurationDeltaAsync(
        SqliteConnection conn, SqliteTransaction tx, string date, int gameId, int durationSeconds)
    {
        if (durationSeconds <= 0) return;

        var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, duration_seconds FROM daily_summary WHERE date = @date AND game_id = @gameId;",
            new { date, gameId }, tx);

        if (existing != null)
        {
            var newSecs = (int)existing.duration_seconds + durationSeconds;
            var newMins = newSecs / 60;

            await conn.ExecuteAsync(
                """
                UPDATE daily_summary
                SET duration_seconds = @newSecs,
                    duration_minutes = @newMins,
                    session_count    = CASE WHEN duration_minutes = 0 AND @newMins > 0 THEN 1 ELSE session_count END,
                    sync_status      = 'pending'
                WHERE id = @id;
                """,
                new { id = (long)existing.id, newSecs, newMins },
                tx);
        }
        else
        {
            var mins = durationSeconds / 60;
            var sessionCount = mins > 0 ? 1 : 0;

            await conn.ExecuteAsync(
                """
                INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
                VALUES (@date, @gameId, @durationSeconds, @mins, @sessionCount, 'pending');
                """,
                new { date, gameId, durationSeconds, mins, sessionCount },
                tx);
        }
    }

    /// <summary>
    /// 重算当日有效场次。**逐字复刻** <see cref="EndSessionAsync"/> 的既有口径：
    /// 用 <c>substr(start_time,1,10)</c>（**日历日**，不是会计日）分组、只统计 ≥60 秒的会话、
    /// 以及对"外部/Notion 手工录入时长"的 manualBonus 判定。
    /// ⚠️ 这段口径是既有行为，不要"顺手修正"（改动会波及 Notion 同步的场次数字）。
    /// </summary>
    private static async Task RecomputeSessionCountAsync(
        SqliteConnection conn, SqliteTransaction tx, int sessionId, int durationSeconds)
    {
        // 只有当会话时长 >= 60 秒时才计入有效游玩次数；低于 60 秒的微小启动忽略。
        if (durationSeconds < 60) return;

        var sessionRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT game_id, substr(start_time, 1, 10) AS session_date FROM sessions WHERE id = @sessionId;",
            new { sessionId }, tx);

        if (sessionRow is null) return;

        int gameId = (int)sessionRow.game_id;
        string date = (string)sessionRow.session_date;

        var validSessionsCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sessions
            WHERE game_id = @gameId AND substr(start_time, 1, 10) = @date AND duration_seconds >= 60;
            """,
            new { gameId, date }, tx);

        if (validSessionsCount <= 0) return;

        var localSessionsTotalSecs = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(SUM(duration_seconds), 0) FROM sessions
            WHERE game_id = @gameId AND substr(start_time, 1, 10) = @date;
            """,
            new { gameId, date }, tx);

        var dailyRow = await conn.QuerySingleOrDefaultAsync<DailyDurationCheck>(
            "SELECT duration_seconds, session_count FROM daily_summary WHERE game_id = @gameId AND date = @date LIMIT 1;",
            new { gameId, date }, tx);

        int manualBonusSessions = 0;
        if (dailyRow != null && dailyRow.duration_seconds > localSessionsTotalSecs + 60)
        {
            // 存在外部 / Notion 手动录入的时长（未通过本地进程捕获），计入该手动游玩次数
            manualBonusSessions = 1;
        }

        int finalSessionCount = validSessionsCount + manualBonusSessions;

        await conn.ExecuteAsync(
            """
            UPDATE daily_summary SET session_count = @finalSessionCount
            WHERE game_id = @gameId AND date = @date;
            """,
            new { gameId, date, finalSessionCount }, tx);
    }

    // ==================== 远端变更落地（数据 + 游标同一事务） ====================

    /// <summary>
    /// 把远端变更应用到本地。
    ///
    /// ⚠️ **本阶段刻意不实现"通用 LWW"**：Game / Session / GameMapping / Tombstone 的冲突语义
    /// 完全不同，一句 <c>if (remote.updated_at &gt; local.updated_at)</c> 套到所有实体上，
    /// 只会把真正的冲突解决问题藏起来。所以这里的规则是保守的：
    ///   · 本地已有同 identity 的实体 → **跳过**（不覆盖）；
    ///   · 业务键（platform + platform_id）已被**另一个** identity 占用 → 跳过（这需要身份对账，
    ///     不是简单覆盖能解决的）；
    ///   · 不认识的实体类型 / payload schema → 跳过；
    ///   · 本地已有墓碑 → 跳过（墓碑优先，旧数据不能把已删除实体复活）。
    /// 被跳过的条目全部计入 <c>Deferred</c> 并写日志 —— **不静默丢弃**，
    /// 留给后续"身份对账 / 冲突解决"阶段处理。
    ///
    /// 为什么游标一定要和落地在**同一个事务**里：
    /// 若先推进游标再写数据，崩溃就会出现"云端认为已消费、本地却没写"的**永久丢数据**。
    /// 现在的语义是：崩溃只会导致下次重复拉取同一批，而落地是幂等的（按 global_id 判存在）。
    /// </summary>
    public async Task<RemoteApplyResult> ApplyRemoteChangesWithCursorAsync(
        string backend,
        string accountId,
        IReadOnlyList<SyncChange> changes,
        string? nextCursor,
        string localDeviceId,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            int applied = 0, tombstonesPropagated = 0;
            var deferredItems = new List<(SyncChange Change, string Reason, int SchemaVersion)>();

            foreach (var change in changes)
            {
                var alreadyTombstoned = conn.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sync_tombstones WHERE entity_type = @type AND entity_global_id = @gid;",
                    new { type = change.EntityType, gid = change.EntityGlobalId }, tx) > 0;

                if (change.Operation == SyncOperation.Delete)
                {
                    // 删除同步：把墓碑落到本地（幂等 —— 同一实体只会有一条墓碑）。
                    AddTombstoneCore(conn, tx, new SyncTombstone
                    {
                        TombstoneId = Guid.NewGuid().ToString("N"),
                        EntityType = change.EntityType,
                        EntityGlobalId = change.EntityGlobalId,
                        DeviceId = string.IsNullOrWhiteSpace(change.DeviceId) ? localDeviceId : change.DeviceId,
                        DeletedAtUtc = string.IsNullOrWhiteSpace(change.CreatedAtUtc) ? UtcNowIso() : change.CreatedAtUtc,
                        CreatedAtUtc = UtcNowIso()
                    });

                    tombstonesPropagated++;
                    continue;
                }

                // 墓碑优先：已删除的实体，旧的 Create/Update 一律不落地。
                if (alreadyTombstoned)
                {
                    deferredItems.Add((change, DeferredReasons.TombstonedLocal,
                        SyncPayloads.TryReadSchemaVersion(change.Payload)));
                    continue;
                }

                var (reason, schemaVersion) = change.EntityType switch
                {
                    nameof(SyncEntityType.Game) => ApplyRemoteGame(conn, tx, change),
                    nameof(SyncEntityType.Session) => ApplyRemoteSession(conn, tx, change),
                    _ => (DeferredReasons.UnsupportedEntityType,
                          SyncPayloads.TryReadSchemaVersion(change.Payload))
                };

                if (reason is null) applied++;
                else deferredItems.Add((change, reason, schemaVersion));
            }

            // Deferred 台账：在游标越过它们之前先留痕（与游标同一个事务 → 绝不会"游标走了却没记上"）。
            // ⚠️ Deferred ≠ Completed：这些条目**没有**在本机同步成功，后续阶段（2e）必须能重新处理。
            // 重复发现同一 change_id 时保留 first_seen、刷新 last_seen 并累加 seen_count。
            // ⚠️ 台账必须连同**当次 Pull 收到的原始 payload 快照**一起保存（migration 003 / 2e 冻结）：
            //    被延后的远端对象从未落地到本地库，离线重处理时无法凭本地状态重建它
            //    （platform / platform_id / executable / executable_path / name 全都不在本地）。
            //    重处理时**不得**重新去远端拉，也**不得**按本地当前状态重新构造。
            foreach (var item in deferredItems)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO sync_deferred_changes
                        (change_id, backend, entity_type, entity_global_id, operation, device_id, payload,
                         reason, schema_version, first_seen_at_utc, last_seen_at_utc, seen_count)
                    VALUES
                        (@ChangeId, @Backend, @EntityType, @EntityGlobalId, @Operation, @DeviceId, @Payload,
                         @Reason, @SchemaVersion, @now, @now, 1)
                    ON CONFLICT(change_id) DO UPDATE SET
                        reason            = excluded.reason,
                        last_seen_at_utc  = excluded.last_seen_at_utc,
                        seen_count        = sync_deferred_changes.seen_count + 1,
                        -- 2c 时期的历史行没有 payload：若再次遇到同一条变更，把"事实"补上
                        payload           = COALESCE(NULLIF(sync_deferred_changes.payload, ''), excluded.payload),
                        operation         = excluded.operation,
                        device_id         = COALESCE(sync_deferred_changes.device_id, excluded.device_id);
                    """,
                    new
                    {
                        ChangeId = item.Change.ChangeId,
                        Backend = backend,
                        EntityType = item.Change.EntityType,
                        EntityGlobalId = item.Change.EntityGlobalId,
                        Operation = (int)item.Change.Operation,
                        DeviceId = item.Change.DeviceId,
                        Payload = item.Change.Payload,
                        Reason = item.Reason,
                        SchemaVersion = item.SchemaVersion,
                        now = UtcNowIso()
                    },
                    tx);
            }

            // 游标与数据同事务提交 —— 这一行必须在同一个 tx 里，不能提前。
            await conn.ExecuteAsync(
                """
                INSERT INTO sync_state (backend, account_id, cursor, last_sync_at_utc, last_error)
                VALUES (@backend, @accountId, @cursor, @now, NULL)
                ON CONFLICT(backend, account_id) DO UPDATE SET
                    cursor           = excluded.cursor,
                    last_sync_at_utc = excluded.last_sync_at_utc,
                    last_error       = NULL;
                """,
                new { backend, accountId, cursor = nextCursor ?? string.Empty, now = UtcNowIso() },
                tx);

            tx.Commit();

            return new RemoteApplyResult(
                applied,
                deferredItems.Count,
                tombstonesPropagated,
                deferredItems.Select(i => i.Reason).Distinct(StringComparer.Ordinal).ToList());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 落一条远端 Game。返回 <c>null</c> = 已落地；否则是**延后原因码**（见 DeferredReasons）。
    ///
    /// ⚠️ 这里刻意**不做通用 LWW**：已存在同 identity 不覆盖；业务键被别的 identity 占用时不合并、不新建
    /// —— 那属于 **2e：Identity / Conflict Resolution**，必须结合
    /// global_id → { Notion Page ID / Obsidian path / SiYuan BlockId } 一起设计，
    /// 不能让 2c 的同步引擎偷偷替用户做业务冲突决策。
    /// </summary>
    private static (string? Reason, int SchemaVersion) ApplyRemoteGame(
        SqliteConnection conn, SqliteTransaction tx, SyncChange change)
    {
        if (!SyncPayloads.TryParseGame(change.Payload, out var payload, out var schemaVersion, out _))
        {
            return (ClassifyParseFailure(schemaVersion), Math.Max(schemaVersion, 0));
        }

        var game = payload!;

        // 同 identity 已存在 → 跳过（重复 Pull 不产生重复实体）。
        if (conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM games WHERE global_id = @gid;",
                new { gid = game.GlobalId }, tx) > 0)
        {
            return (DeferredReasons.AlreadyExistsLocal, schemaVersion);
        }

        // 业务键已被另一个 identity 占用 → 需要身份对账（不是覆盖能解决的），本阶段不猜。
        if (conn.ExecuteScalar<int>(
                """
                SELECT COUNT(*) FROM games
                WHERE LOWER(platform) = LOWER(@platform) AND LOWER(platform_id) = LOWER(@platformId);
                """,
                new { platform = game.Platform, platformId = game.PlatformId }, tx) > 0)
        {
            return (DeferredReasons.BusinessKeyTakenByOtherIdentity, schemaVersion);
        }

        conn.Execute(
            """
            INSERT INTO games (platform, platform_id, name, executable, executable_path, status, global_id)
            VALUES (@platform, @platformId, @name, @executable, @executablePath, 'active', @globalId);
            """,
            new
            {
                platform = game.Platform,
                platformId = game.PlatformId,
                name = game.Name,
                executable = game.Executable,
                executablePath = game.ExecutablePath,
                globalId = game.GlobalId
            },
            tx);

        return (null, schemaVersion);
    }

    private static (string? Reason, int SchemaVersion) ApplyRemoteSession(
        SqliteConnection conn, SqliteTransaction tx, SyncChange change)
    {
        if (!SyncPayloads.TryParseSession(change.Payload, out var payload, out var schemaVersion, out _))
        {
            return (ClassifyParseFailure(schemaVersion), Math.Max(schemaVersion, 0));
        }

        var session = payload!;

        if (conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sessions WHERE global_id = @gid;",
                new { gid = session.GlobalId }, tx) > 0)
        {
            return (DeferredReasons.AlreadyExistsLocal, schemaVersion);
        }

        // 依赖：sessions.game_id NOT NULL，远端会话必须挂在本地已存在的 game 上。
        // 游戏还没到 → 延后（下次 Pull 会再拉到；我们的推送顺序本身是 Game 先于 Session）。
        var gameId = conn.QuerySingleOrDefault<int?>(
            "SELECT id FROM games WHERE global_id = @gid LIMIT 1;",
            new { gid = session.GameGlobalId }, tx);

        if (gameId is null)
        {
            return (DeferredReasons.DependencyMissing, schemaVersion);
        }

        conn.Execute(
            """
            INSERT INTO sessions (game_id, pid, process_name, start_time, end_time, last_heartbeat,
                                  duration_seconds, is_active, global_id, device_id,
                                  started_at_utc, ended_at_utc, time_precision)
            VALUES (@gameId, 0, @processName, @startTime, @endTime, @startTime,
                    @durationSeconds, 0, @globalId, @deviceId,
                    @startedAtUtc, @endedAtUtc, @precision);
            """,
            new
            {
                gameId = gameId.Value,
                processName = session.ProcessName,
                startTime = session.StartedAtUtc,
                endTime = session.EndedAtUtc,
                durationSeconds = session.DurationSeconds,
                globalId = session.GlobalId,
                deviceId = session.DeviceId,
                startedAtUtc = session.StartedAtUtc,
                endedAtUtc = session.EndedAtUtc,
                precision = session.TimePrecision
            },
            tx);

        return (null, schemaVersion);
    }

    /// <summary>解析失败时区分"版本不认识"与"payload 坏掉"——两者的后续处理方式不同。</summary>
    private static string ClassifyParseFailure(int schemaVersion)
        => schemaVersion >= 0 && schemaVersion != DeviceSyncConstants.CurrentSchemaVersion
            ? DeferredReasons.UnsupportedSchemaVersion
            : DeferredReasons.InvalidPayload;

    /// <summary>
    /// 读取 Deferred 台账（供诊断界面与 <b>2e</b> 的重新处理机制使用）。
    /// 这里只提供"读"，本阶段**不实现**任何自动修复策略。
    /// </summary>
    public async Task<IReadOnlyList<DeferredSyncChange>> GetDeferredChangesAsync(int limit = 200)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<DeferredSyncChange>(
            """
            SELECT * FROM sync_deferred_changes
            ORDER BY last_seen_at_utc DESC, id DESC
            LIMIT @limit;
            """,
            new { limit });

        return rows.ToList();
    }

    // ==================== Game 身份对账（2e 步骤 5） ====================

    /// <summary>对账时"删除"类变更不归它管（那是墓碑路径的职责）。</summary>
    private const string ReconcileSkipDelete = "delete_handled_by_tombstone_path";

    private sealed class GameReconcileRow
    {
        public int Id { get; set; }
        public string? Platform { get; set; }
        public string? PlatformId { get; set; }
        public string? Executable { get; set; }
        public string? ExecutablePath { get; set; }
        public string? Name { get; set; }
        public string? NotionPageId { get; set; }
        public string? Status { get; set; }
        public string? GlobalId { get; set; }
    }

    /// <summary>
    /// 把一个**明确的远端 Game 载荷**安全收敛到本地 canonical Game（2e 步骤 5）。
    ///
    /// <code>
    /// Remote Game B → IdentityRuleEngine → Matched(A)
    ///    → 落库层第二道 ignored 防线（canonical 状态已变 ignored → BLOCK）
    ///    → Notion Page ID 冲突检查（不同页面 → Deferred，**绝不自动选一个**）
    ///    → 保留本地 A：canonical 的 global_id **以本地为准**
    ///      （绝不因为远端 payload 看起来"更新"就覆盖本地）
    ///    → 记录 B → A 的 sync_identity_supersessions
    ///    → **不写 deletion tombstone**
    ///    → **不动 A 的 executable / executable_path**（保护 GetGameByPathOrExeAsync 监听链）
    ///    → **不覆盖 A 的 Notion Page ID**（Provider identity ≠ Sappfiler global_id）
    /// </code>
    ///
    /// ⚠️ 与 <c>DeduplicateGamesAndDailySummaries</c> 的关键差别：后者会把 best 值
    /// （exe / path / notion / platform）继承到权威行；**身份对账刻意不这么做** ——
    /// 它只回答"A 与 B 是不是同一个实体"，不改写本地运行识别所依赖的 exe/path，也不替用户选 Provider 身份。
    ///
    /// ⚠️ 本方法**只处理一条**远端变更（一个事务一条，失败互相隔离）。
    /// 批量消费 Deferred 台账属于步骤 7，职责上分开。
    /// </summary>
    public async Task<GameReconcileOutcome> ReconcileRemoteGameAsync(
        SyncChange remoteChange,
        Func<string, string>? normalizeName = null,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (remoteChange.Operation == SyncOperation.Delete)
            {
                // 删除走正常的墓碑路径（2c 的 Apply），**不是**身份对账。
                return new GameReconcileOutcome(GameReconcileResult.Deferred, null, null,
                    IdentityMatchLevel.None, ReconcileSkipDelete);
            }

            if (!SyncPayloads.TryParseGame(remoteChange.Payload, out var remote, out _, out _))
            {
                return new GameReconcileOutcome(GameReconcileResult.InvalidPayload, null, null,
                    IdentityMatchLevel.None, DeferredReasons.InvalidPayload);
            }

            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            // 幂等：这个远端 identity 是否已经被处理过？
            var alreadyResolved = await conn.QuerySingleOrDefaultAsync<string>(
                """
                SELECT canonical_global_id FROM sync_identity_supersessions
                WHERE entity_type = @type AND superseded_global_id = @gid LIMIT 1;
                """,
                new { type = nameof(SyncEntityType.Game), gid = remoteChange.EntityGlobalId }, tx);

            if (!string.IsNullOrWhiteSpace(alreadyResolved))
            {
                return new GameReconcileOutcome(GameReconcileResult.AlreadyKnown, null, alreadyResolved,
                    IdentityMatchLevel.None, IdentityDecisionReasons.StrongKeyEqual);
            }

            var allGames = (await conn.QueryAsync<GameReconcileRow>("SELECT * FROM games;", null, tx)).ToList();

            // 远端 identity 在本地的那一行（如果有）—— 它是"自己"，不能当成"另一个候选"
            var selfRow = allGames.FirstOrDefault(g =>
                string.Equals(g.GlobalId, remoteChange.EntityGlobalId, StringComparison.Ordinal));

            var candidates = allGames
                .Where(g => selfRow is null || g.Id != selfRow.Id)
                .Select(g => new LocalGameCandidate(
                    g.Id,
                    new GameIdentityFacts(g.Platform, g.PlatformId, g.Executable, g.ExecutablePath, g.Name, g.NotionPageId),
                    string.Equals(g.Status, "ignored", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var decision = GameIdentityRules.Decide(
                new GameIdentityFacts(remote!.Platform, remote.PlatformId, remote.Executable,
                    remote.ExecutablePath, remote.Name, remote.NotionPageId),
                candidates,
                normalizeName);

            if (decision.Kind != IdentityDecisionKind.Matched)
            {
                var result = decision.Kind switch
                {
                    IdentityDecisionKind.NoLocalCandidate => GameReconcileResult.NoLocalCandidate,
                    IdentityDecisionKind.Blocked => GameReconcileResult.Blocked,
                    _ => GameReconcileResult.Deferred
                };

                return new GameReconcileOutcome(result, null, null, decision.Level, decision.Reason);
            }

            var canonical = allGames.First(g => g.Id == decision.CanonicalLocalId);

            // ③ 落库层的**第二道 ignored 防线**：这里**重新读一次库**（而不是复用上面的快照），
            //    因为"纯函数正确 ≠ 落库路径永远不会被绕过" —— 状态可能在判定之后被改动。
            var canonicalStatusFresh = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT status FROM games WHERE id = @id;", new { id = canonical.Id }, tx);

            if (string.Equals(canonicalStatusFresh, "ignored", StringComparison.OrdinalIgnoreCase))
            {
                return new GameReconcileOutcome(GameReconcileResult.Blocked, canonical.Id, canonical.GlobalId,
                    decision.Level, IdentityDecisionReasons.LocalIgnored);
            }

            // ⑤ Notion Page ID 是 **Provider identity**，不是 Sappfiler global_id。
            //    双方绑定了不同页面 → 延后（**绝不**因为"看起来是同一个游戏"就替用户选一个）。
            if (GameIdentityRules.NotionPageConflicts(
                    new GameIdentityFacts(NotionPageId: canonical.NotionPageId),
                    new GameIdentityFacts(NotionPageId: remote!.NotionPageId)))
            {
                return new GameReconcileOutcome(GameReconcileResult.Deferred, canonical.Id, canonical.GlobalId,
                    decision.Level, IdentityDecisionReasons.NotionPageConflict);
            }

            // ① canonical 的 identity：**本地有就永远保留**；本地没有才继承远端的
            //    （沿用 2b 契约第 3 条"没有则继承第一个有效值"，避免多造一个 identity）。
            var canonicalGlobalId = canonical.GlobalId;
            var inherited = false;

            if (string.IsNullOrWhiteSpace(canonicalGlobalId))
            {
                canonicalGlobalId = remoteChange.EntityGlobalId;
                inherited = true;

                await conn.ExecuteAsync(
                    """
                    UPDATE games SET global_id = @gid
                    WHERE id = @id AND NULLIF(TRIM(global_id), '') IS NULL;
                    """,
                    new { gid = canonicalGlobalId, id = canonical.Id }, tx);
            }

            // 形态 2：远端 identity 在本地**也已经落地成一行** → 折叠进 canonical。
            // 只迁移 sessions / daily_summary 并删掉这行冗余；
            // ⚠️ **不动** canonical 的 executable / executable_path / Notion Page ID。
            if (selfRow is not null && selfRow.Id != canonical.Id)
            {
                MigrateGameDataToCanonical(conn, tx, canonical.Id, selfRow.Id);
                await conn.ExecuteAsync("DELETE FROM games WHERE id = @id;", new { id = selfRow.Id }, tx);
            }

            // ② 只记录"这两套 identity 是同一个实体"，**绝不写 deletion tombstone**。
            var superseded = string.Equals(canonicalGlobalId, remoteChange.EntityGlobalId, StringComparison.Ordinal)
                ? null
                : remoteChange.EntityGlobalId;

            if (superseded is not null)
            {
                AddSupersessionCore(conn, tx, new SyncIdentitySupersession
                {
                    SupersessionId = Guid.NewGuid().ToString("N"),
                    EntityType = nameof(SyncEntityType.Game),
                    SupersededGlobalId = superseded,
                    CanonicalGlobalId = canonicalGlobalId!,
                    DeviceId = ReadLocalDeviceId(conn, tx),
                    Reason = ReasonForLevel(decision.Level),
                    CreatedAtUtc = UtcNowIso()
                });
            }

            tx.Commit();

            return new GameReconcileOutcome(
                inherited ? GameReconcileResult.IdentityContinued : GameReconcileResult.Merged,
                canonical.Id, canonicalGlobalId, decision.Level, ReasonForLevel(decision.Level));
        }
        catch (Exception ex)
        {
            AppLog.Error("[设备同步] Game 身份对账失败（已整体回滚，本地数据不变）", ex);
            return new GameReconcileOutcome(GameReconcileResult.Deferred, null, null,
                IdentityMatchLevel.None, DeferredReasons.InvalidPayload);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string ReasonForLevel(IdentityMatchLevel level) => level switch
    {
        IdentityMatchLevel.StrongKey => SupersessionReasons.ReconcileStrongKey,
        IdentityMatchLevel.ExecutablePath => SupersessionReasons.ReconcileExecutablePath,
        IdentityMatchLevel.ExecutableName => SupersessionReasons.ReconcileExecutableName,
        IdentityMatchLevel.NormalizedName => SupersessionReasons.ReconcileNormalizedName,
        _ => SupersessionReasons.ReconcileStrongKey
    };

    private static string ReadLocalDeviceId(SqliteConnection conn, SqliteTransaction? tx)
        => conn.QuerySingleOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = @k;",
            new { k = DeviceSyncConstants.SettingKeyDeviceId }, tx).GetAwaiter().GetResult() ?? string.Empty;

    /// <summary>
    /// 把重复行的会话与每日记录迁移到权威行 —— **只做数据搬运**，不删行、不写墓碑/身份合并。
    ///
    /// 启动去重与 2e 的身份对账共用这一份口径（避免两套合并逻辑分叉）。
    /// 逐字保留原 <c>DeduplicateGamesAndDailySummaries</c> 的合并语义：
    /// 时长相加、场次相加、Notion 字段"非空优先"、<c>sync_status</c> 仅两侧都 synced 才 synced。
    /// </summary>
    private static void MigrateGameDataToCanonical(
        SqliteConnection conn, SqliteTransaction tx, int canonicalGameId, int duplicateGameId)
    {
        conn.Execute("UPDATE sessions SET game_id = @canonicalId WHERE game_id = @dupId;",
            new { canonicalId = canonicalGameId, dupId = duplicateGameId }, tx);

        var dupSummaries = conn.Query<DailySummaryRow>(
            "SELECT id AS Id, date AS Date, game_id AS GameId, duration_seconds AS DurationSeconds, duration_minutes AS DurationMinutes, session_count AS SessionCount, sync_status AS SyncStatus, notion_page_id AS NotionPageId, notion_title AS NotionTitle, notion_icon_url AS NotionIconUrl FROM daily_summary WHERE game_id = @dupId;",
            new { dupId = duplicateGameId }, tx).ToList();

        foreach (var ds in dupSummaries)
        {
            var targetRow = conn.QueryFirstOrDefault<DailySummaryRow>(
                "SELECT id AS Id, date AS Date, game_id AS GameId, duration_seconds AS DurationSeconds, duration_minutes AS DurationMinutes, session_count AS SessionCount, sync_status AS SyncStatus, notion_page_id AS NotionPageId, notion_title AS NotionTitle, notion_icon_url AS NotionIconUrl FROM daily_summary WHERE date = @date AND game_id = @canonicalId LIMIT 1;",
                new { date = ds.Date, canonicalId = canonicalGameId }, tx);

            if (targetRow != null)
            {
                int combinedSecs = targetRow.DurationSeconds + ds.DurationSeconds;
                int combinedMins = combinedSecs / 60;
                int combinedSessions = targetRow.SessionCount + ds.SessionCount;
                string? finalPageId = !string.IsNullOrEmpty(targetRow.NotionPageId) ? targetRow.NotionPageId : ds.NotionPageId;
                string? finalTitle = !string.IsNullOrEmpty(targetRow.NotionTitle) ? targetRow.NotionTitle : ds.NotionTitle;
                string? finalIcon = !string.IsNullOrEmpty(targetRow.NotionIconUrl) ? targetRow.NotionIconUrl : ds.NotionIconUrl;
                string finalStatus = (targetRow.SyncStatus == "synced" && ds.SyncStatus == "synced") ? "synced" : "pending";

                conn.Execute(
                    """
                    UPDATE daily_summary
                    SET duration_seconds = @combinedSecs,
                        duration_minutes = @combinedMins,
                        session_count = @combinedSessions,
                        notion_page_id = @finalPageId,
                        notion_title = @finalTitle,
                        notion_icon_url = @finalIcon,
                        sync_status = @finalStatus
                    WHERE id = @targetId;

                    DELETE FROM daily_summary WHERE id = @dupDailyId;
                    """,
                    new { combinedSecs, combinedMins, combinedSessions, finalPageId, finalTitle, finalIcon, finalStatus, targetId = targetRow.Id, dupDailyId = ds.Id },
                    tx);
            }
            else
            {
                conn.Execute("UPDATE daily_summary SET game_id = @canonicalId WHERE id = @id;",
                    new { canonicalId = canonicalGameId, id = ds.Id }, tx);
            }
        }
    }

    // ==================== Deferred 台账重处理（2e 步骤 7） ====================

    /// <summary>
    /// 重处理 Deferred 台账里**尚未解决**的条目。
    ///
    /// <code>
    /// 取 unresolved（resolved_at_utc IS NULL）
    ///   → 从**台账 payload 快照**重建当时的远端变更（不重新拉取、不按本地当前状态重造）
    ///   → 重新执行**现有**规则引擎 / 身份对账
    ///   → 有明确结论 → 写 resolved_at_utc + resolution（**不删除台账行**）
    ///   → 仍无法确定 → 保持 unresolved，**本批次结束**
    /// </code>
    ///
    /// ⚠️ 三条硬约束（用户 2026-10-03 冻结）：
    ///   ① **只重跑既有规则**，绝不引入第二套冲突规则 —— 不做 max duration、不做"最新胜"、
    ///      不做本地/远端优先、不自动选 Notion Page、不自动覆盖 exe/path、不自动复活 ignored。
    ///   ② **不做无限循环**：单次调用只跑**一遍**且受 <paramref name="batchLimit"/> 限制；
    ///      仍 unresolved 的条目留在台账里等下一次调用（启动时）再试，**不在这里自旋**。
    ///   ③ **不删除台账行**：resolved 只写 <c>resolved_at_utc</c> + <c>resolution</c>，
    ///      这样才能长期回答"为什么曾经被延后 / 什么时候重处理 / 最后怎么解决"。
    ///
    /// ⚠️ 本方法**不接应用生命周期**：2e 只交付"判断 / 收敛 / 记录"能力，
    ///   `startup → SyncEngine → Push/Pull → Apply → Reconcile` 的接线属于集成阶段。
    ///
    /// ⚠️ 锁：本方法内部**不持有** <c>_writeLock</c> 去调用对账（那些方法各自会取锁），
    ///   否则会自死锁。
    /// </summary>
    public async Task<ReconciliationSummary> ReconcileDeferredChangesAsync(
        int batchLimit = SyncOutboxPolicy.ReconcileBatchSize,
        CancellationToken cancellationToken = default)
    {
        var pending = new List<DeferredSyncChange>();

        using (var conn = CreateConnection())
        {
            pending = (await conn.QueryAsync<DeferredSyncChange>(
                """
                SELECT * FROM sync_deferred_changes
                WHERE resolved_at_utc IS NULL
                ORDER BY id ASC
                LIMIT @limit;
                """,
                new { limit = batchLimit })).ToList();
        }

        var resolutions = new List<string>();
        int resolved = 0, stillDeferred = 0, skippedTombstoned = 0, noPayload = 0, unsupported = 0;

        foreach (var row in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 没有 payload 快照 → 永远无法重处理：收敛掉，否则每次启动都白跑。
            if (string.IsNullOrWhiteSpace(row.Payload))
            {
                await MarkDeferredResolvedAsync(row.ChangeId, ReconciliationResolutions.LegacyNoPayload);
                resolutions.Add(ReconciliationResolutions.LegacyNoPayload);
                noPayload++;
                continue;
            }

            // 本地已有墓碑 = 用户删过这个实体。**绝不**在这里尝试收敛或落地 —— 那是"复活已删除实体"。
            if (string.Equals(row.Reason, DeferredReasons.TombstonedLocal, StringComparison.Ordinal))
            {
                skippedTombstoned++;
                continue;
            }

            string? resolution = row.EntityType switch
            {
                nameof(SyncEntityType.Game) => await ReconcileOneDeferredGameAsync(row, cancellationToken),
                nameof(SyncEntityType.Session) => await ReconcileOneDeferredSessionAsync(row, cancellationToken),
                _ => null   // GameMapping / 未知类型：2e 不覆盖 → 保持 unresolved
            };

            if (resolution is null)
            {
                if (row.EntityType is not (nameof(SyncEntityType.Game) or nameof(SyncEntityType.Session)))
                {
                    unsupported++;
                }
                else
                {
                    stillDeferred++;
                }
                continue;
            }

            await MarkDeferredResolvedAsync(row.ChangeId, resolution);
            resolutions.Add(resolution);
            resolved++;
        }

        if (resolved > 0 || stillDeferred > 0 || skippedTombstoned > 0 || noPayload > 0)
        {
            AppLog.Info($"[设备同步] Deferred 重处理：扫描 {pending.Count}、已解决 {resolved}、"
                + $"仍待处理 {stillDeferred}、跳过（本地已墓碑）{skippedTombstoned}、"
                + $"无 payload {noPayload}、不支持的类型 {unsupported}");
        }

        return new ReconciliationSummary(
            pending.Count, resolved, stillDeferred, skippedTombstoned, noPayload, unsupported, resolutions);
    }

    /// <summary>从台账行**重建**当时的远端变更（唯一输入是台账快照）。</summary>
    private static SyncChange BuildChangeFromLedger(DeferredSyncChange row) => new()
    {
        ChangeId = row.ChangeId,
        EntityType = row.EntityType,
        EntityGlobalId = row.EntityGlobalId,
        Operation = row.Operation,
        Payload = row.Payload ?? string.Empty,
        DeviceId = row.DeviceId ?? string.Empty,
        // 台账不存 CreatedAtUtc（对账不需要它）；审计要看时间请用 first_seen_at_utc。
        CreatedAtUtc = row.FirstSeenAtUtc
    };

    private async Task<string?> ReconcileOneDeferredGameAsync(
        DeferredSyncChange row, CancellationToken cancellationToken)
    {
        var outcome = await ReconcileRemoteGameAsync(BuildChangeFromLedger(row), null, cancellationToken);

        return outcome.Result switch
        {
            GameReconcileResult.Merged => ReconciliationResolutions.MergedLocalIdentity,
            // 本地原本没有 identity、继承了远端的 —— 对台账而言同样是"身份已并入本地权威行"。
            GameReconcileResult.IdentityContinued => ReconciliationResolutions.MergedLocalIdentity,
            GameReconcileResult.AlreadyKnown => ReconciliationResolutions.RedirectedToCanonical,
            GameReconcileResult.Blocked when outcome.Reason == IdentityDecisionReasons.LocalIgnored
                => ReconciliationResolutions.LocalIgnoredProtected,
            GameReconcileResult.Blocked => ReconciliationResolutions.BlockedStrongKeyConflict,
            GameReconcileResult.InvalidPayload => ReconciliationResolutions.LegacyNoPayload,
            // Deferred / NoLocalCandidate：结论不明确（或本地根本没有候选）→ 保持 unresolved。
            _ => null
        };
    }

    private async Task<string?> ReconcileOneDeferredSessionAsync(
        DeferredSyncChange row, CancellationToken cancellationToken)
    {
        var outcome = await ReconcileRemoteSessionAsync(BuildChangeFromLedger(row), cancellationToken);

        if (outcome.Result == SessionReconcileResult.NoLocalCandidate
            && string.Equals(row.Reason, DeferredReasons.DependencyMissing, StringComparison.Ordinal))
        {
            // 当初因为"依赖的游戏还没到"没落地；现在依赖可能已就绪。
            // ⚠️ 这里用的是 **2c 的既有落地规则**（ApplyRemoteSession 一字未改），
            //    不是第二套冲突解决 —— 只是把当初做不成的那一步再试一次。
            var applied = await TryApplySingleChangeAsync(BuildChangeFromLedger(row), cancellationToken);
            return applied ? ReconciliationResolutions.DependencyResolved : null;
        }

        return outcome.Result switch
        {
            SessionReconcileResult.Converged => ReconciliationResolutions.Converged,
            SessionReconcileResult.AlreadyKnown => ReconciliationResolutions.AlreadyPresent,
            SessionReconcileResult.Blocked => ReconciliationResolutions.BlockedImmutableFactMismatch,
            SessionReconcileResult.InvalidPayload => ReconciliationResolutions.LegacyNoPayload,
            _ => null
        };
    }

    /// <summary>
    /// 用 **2c 的既有落地规则**尝试落地单独一条远端变更（**不动游标** —— 游标早就越过它了）。
    ///
    /// 只服务于 Deferred 重处理：当初因为依赖缺失没能落地的条目，现在依赖可能已就绪。
    /// 规则与 2c 完全一致（墓碑优先 → 类型分派），**没有新增任何冲突语义**。
    /// </summary>
    private async Task<bool> TryApplySingleChangeAsync(SyncChange change, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var alreadyTombstoned = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sync_tombstones WHERE entity_type = @type AND entity_global_id = @gid;",
                new { type = change.EntityType, gid = change.EntityGlobalId }, tx) > 0;

            if (alreadyTombstoned) return false;

            var (reason, _) = change.EntityType switch
            {
                nameof(SyncEntityType.Game) => ApplyRemoteGame(conn, tx, change),
                nameof(SyncEntityType.Session) => ApplyRemoteSession(conn, tx, change),
                _ => (DeferredReasons.UnsupportedEntityType, 0)
            };

            if (reason is not null) return false;

            tx.Commit();
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 记录处理结论。只写**尚未解决**的行（<c>WHERE resolved_at_utc IS NULL</c>），
    /// 保证已有结论**永不**被覆盖 —— 台账是审计资料。
    /// </summary>
    private async Task MarkDeferredResolvedAsync(string changeId, string resolution)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE sync_deferred_changes
                SET resolved_at_utc = @now, resolution = @resolution
                WHERE change_id = @changeId AND resolved_at_utc IS NULL;
                """,
                new { changeId, resolution, now = UtcNowIso() });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ==================== Session 身份对账（2e 步骤 6） ====================

    private sealed class SessionReconcileRow
    {
        public int Id { get; set; }
        public string? GlobalId { get; set; }
        public string? DeviceId { get; set; }
        public string? StartedAtUtc { get; set; }
        public int DurationSeconds { get; set; }
        public string? GameGlobalId { get; set; }
    }

    /// <summary>
    /// 把一个**明确的远端 Session 载荷**与本地历史事实做身份收敛（2e 步骤 6）。
    ///
    /// <code>
    /// 同 global_id                    → 幂等无操作（AlreadyKnown）
    /// 同 global_id 但事实不一致        → Blocked（拒绝改写历史）
    /// 不同 global_id + 四项事实一致    → 只写一条 supersession（Converged）
    /// 其它（含任一项事实不一致/多候选） → Deferred
    /// </code>
    ///
    /// **四项不可变事实**：`device_id` + `started_at_utc` + `duration_seconds` + game identity。
    ///
    /// ⚠️ **刻意不做**（用户明令禁止）：
    ///   ① LWW / "ended_at 谁新谁对" / "created_at 谁新谁对"；
    ///   ② duration 取最大；
    ///   ③ 本地 / 远端设备优先级；
    ///   ④ 进程名相似度、"看起来像同一局"就合并。
    ///   —— Session 是**历史事实**，不是配置对象。
    ///
    /// ⚠️ **完全不触碰**：`sessions` 行的任何字段（尤其 duration）、`daily_summary`
    ///   （它目前仍是**本地派生数据**，本步骤绝不把它升级成参与竞争裁决的跨设备实体）、
    ///   `sync_tombstones`、`sync_queue`，以及 2d 的 `EndSessionWithOutboxAsync` 原子路径。
    ///
    /// ⚠️ 与 Game 对账一样：**一个事务处理一条**；批量消费 Deferred 台账属于步骤 7。
    /// </summary>
    public async Task<SessionReconcileOutcome> ReconcileRemoteSessionAsync(
        SyncChange remoteChange,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (remoteChange.Operation == SyncOperation.Delete)
            {
                return new SessionReconcileOutcome(SessionReconcileResult.Deferred, null, null, ReconcileSkipDelete);
            }

            if (!SyncPayloads.TryParseSession(remoteChange.Payload, out var payload, out _, out _))
            {
                return new SessionReconcileOutcome(SessionReconcileResult.InvalidPayload, null, null,
                    DeferredReasons.InvalidPayload);
            }

            var remoteFacts = new SessionFacts(
                payload!.GlobalId, payload.DeviceId, payload.StartedAtUtc,
                payload.DurationSeconds, payload.GameGlobalId);

            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            // 幂等：这个远端 identity 是否已经收敛过？
            var alreadyResolved = await conn.QuerySingleOrDefaultAsync<string>(
                """
                SELECT canonical_global_id FROM sync_identity_supersessions
                WHERE entity_type = @type AND superseded_global_id = @gid LIMIT 1;
                """,
                new { type = nameof(SyncEntityType.Session), gid = remoteChange.EntityGlobalId }, tx);

            if (!string.IsNullOrWhiteSpace(alreadyResolved))
            {
                return new SessionReconcileOutcome(SessionReconcileResult.AlreadyKnown, null, alreadyResolved,
                    SessionIdentityDecisionReasons.SameImmutableFact);
            }

            // 候选筛选键 = **device_id + started_at_utc**（一条会话的"时空坐标"），外加"它自己的 global_id 投影"。
            //
            // ⚠️ 这里刻意**不**把 duration / game 放进筛选条件：它们是**确认键**。
            //    · device+start 不匹配 → 那是**另一条历史事实** → 不加载 → NoLocalCandidate（交给正常落地路径）。
            //    · device+start 匹配但 duration / game 不同 → **疑似同一条事实、但事实互相冲突** → Deferred（不裁决）。
            //
            // ⚠️ 更不能把筛选放宽成"同一设备的所有会话"：那样**每一条新会话**都会因为与本地其它会话不等价
            //    而被判成 Deferred，远端时长将永远落不了地（这是本节最危险的一个坑）。
            var rows = (await conn.QueryAsync<SessionReconcileRow>(
                """
                SELECT s.id             AS Id,
                       s.global_id      AS GlobalId,
                       s.device_id      AS DeviceId,
                       s.started_at_utc AS StartedAtUtc,
                       s.duration_seconds AS DurationSeconds,
                       g.global_id      AS GameGlobalId
                FROM sessions s
                LEFT JOIN games g ON g.id = s.game_id
                WHERE s.global_id = @gid
                   OR (s.device_id = @device AND s.started_at_utc = @started);
                """,
                new
                {
                    gid = remoteChange.EntityGlobalId,
                    device = payload.DeviceId,
                    started = payload.StartedAtUtc
                },
                tx)).ToList();

            var candidates = rows
                .Select(r => new LocalSessionCandidate(
                    r.Id, new SessionFacts(r.GlobalId, r.DeviceId, r.StartedAtUtc, r.DurationSeconds, r.GameGlobalId)))
                .ToList();

            var decision = SessionIdentityRules.Decide(remoteFacts, candidates);

            switch (decision.Kind)
            {
                case SessionIdentityDecisionKind.NoLocalCandidate:
                    return new SessionReconcileOutcome(SessionReconcileResult.NoLocalCandidate, null, null, decision.Reason);

                case SessionIdentityDecisionKind.SameIdentity:
                    return new SessionReconcileOutcome(SessionReconcileResult.AlreadyKnown,
                        decision.CanonicalLocalId, remoteFacts.GlobalId, decision.Reason);

                case SessionIdentityDecisionKind.Blocked:
                    return new SessionReconcileOutcome(SessionReconcileResult.Blocked, null, null, decision.Reason);

                case SessionIdentityDecisionKind.Deferred:
                    return new SessionReconcileOutcome(SessionReconcileResult.Deferred, null, null, decision.Reason);
            }

            // EquivalentFact → **只写一条 supersession**：
            // 不改 any sessions 字段、不删行、不写墓碑、不碰 daily_summary。
            var canonical = rows.First(r => r.Id == decision.CanonicalLocalId);

            if (string.IsNullOrWhiteSpace(canonical.GlobalId))
            {
                // 本地这一条还没有 identity → 不在这里凭空造 identity（那是 Phase 1 回填 / 2d 的职责）
                return new SessionReconcileOutcome(SessionReconcileResult.Deferred, canonical.Id, null,
                    SessionIdentityDecisionReasons.InsufficientImmutableFact);
            }

            AddSupersessionCore(conn, tx, new SyncIdentitySupersession
            {
                SupersessionId = Guid.NewGuid().ToString("N"),
                EntityType = nameof(SyncEntityType.Session),
                SupersededGlobalId = remoteChange.EntityGlobalId,
                CanonicalGlobalId = canonical.GlobalId,
                DeviceId = ReadLocalDeviceId(conn, tx),
                Reason = SupersessionReasons.ReconcileSessionSameFact,
                CreatedAtUtc = UtcNowIso()
            });

            tx.Commit();

            return new SessionReconcileOutcome(SessionReconcileResult.Converged,
                canonical.Id, canonical.GlobalId, decision.Reason);
        }
        catch (Exception ex)
        {
            AppLog.Error("[设备同步] Session 身份对账失败（已整体回滚，历史事实不变）", ex);
            return new SessionReconcileOutcome(SessionReconcileResult.Deferred, null, null,
                SessionIdentityDecisionReasons.ImmutableFactMismatch);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ==================== Identity Supersession（身份合并 ≠ 删除） ====================

    /// <summary>
    /// 写入一条身份合并记录（幂等：`UNIQUE(entity_type, superseded_global_id)`，
    /// 重复判定只刷新 canonical 与依据，不新增行）。
    ///
    /// ⚠️ 这是"身份合并"，**不是删除**；真删除走 <see cref="AddTombstoneAsync"/>。
    /// 两者语义必须严格区分（用户 2026-10-03 裁定）。
    /// </summary>
    public async Task AddIdentitySupersessionAsync(SyncIdentitySupersession supersession)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            AddSupersessionCore(conn, null, supersession);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void AddSupersessionCore(
        SqliteConnection conn, SqliteTransaction? tx, SyncIdentitySupersession supersession)
    {
        conn.Execute(
            """
            INSERT INTO sync_identity_supersessions
                (supersession_id, entity_type, superseded_global_id, canonical_global_id,
                 device_id, reason, created_at_utc)
            VALUES
                (@SupersessionId, @EntityType, @SupersededGlobalId, @CanonicalGlobalId,
                 @DeviceId, @Reason, @CreatedAtUtc)
            ON CONFLICT(entity_type, superseded_global_id) DO UPDATE SET
                canonical_global_id = excluded.canonical_global_id,
                reason              = excluded.reason;
            """,
            supersession, tx);
    }

    /// <summary>
    /// 把一个 identity 解析到它的 canonical（若它已被判定为"与另一个身份是同一实体"）。
    /// 返回 null = 这个 identity 没有被合并过，它就是它自己。
    /// </summary>
    public async Task<string?> ResolveCanonicalGlobalIdAsync(string entityType, string globalId)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<string>(
            """
            SELECT canonical_global_id FROM sync_identity_supersessions
            WHERE entity_type = @entityType AND superseded_global_id = @globalId
            LIMIT 1;
            """,
            new { entityType, globalId });
    }

    public async Task<IReadOnlyList<SyncIdentitySupersession>> GetIdentitySupersessionsAsync(int limit = 500)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<SyncIdentitySupersession>(
            "SELECT * FROM sync_identity_supersessions ORDER BY created_at_utc ASC, rowid ASC LIMIT @limit;",
            new { limit });

        return rows.ToList();
    }

    /// <summary>
    /// 在**调用方的事务内**记录一次"去重导致的身份合并"。
    /// 由启动去重（被淘汰的重复行）与 2e 的对账共用。
    /// </summary>
    private static void WriteGameSupersession(
        SqliteConnection conn, SqliteTransaction? tx,
        string supersededGlobalId, string canonicalGlobalId, string reason)
    {
        var deviceId = ReadLocalDeviceId(conn, tx);

        AddSupersessionCore(conn, tx, new SyncIdentitySupersession
        {
            SupersessionId = Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Game),
            SupersededGlobalId = supersededGlobalId,
            CanonicalGlobalId = canonicalGlobalId,
            DeviceId = deviceId,
            Reason = reason,
            CreatedAtUtc = UtcNowIso()
        });
    }

    // ============================ 删除墓碑 ============================

    /// <summary>
    /// 写入删除墓碑（同一实体只保留一条，靠 UNIQUE(entity_type, entity_global_id) 兜底）。
    /// 它与 <c>deleted_archive</c>（本机删除审计）**是两件事**，删除时两张表都要写。
    /// </summary>
    public async Task AddTombstoneAsync(SyncTombstone tombstone)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            AddTombstoneCore(conn, null, tombstone);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 墓碑的核心写入，供"同事务"场景复用（<paramref name="tx"/> 为 null 即自动提交）。
    ///
    /// 用同步实现而非 async：启动去重必须在**它自己的那个事务里**写墓碑，不能另开连接、另起事务。
    /// （Microsoft.Data.Sqlite 的 async 命令内部就是同步执行的，行为等价，已有测试覆盖。）
    /// </summary>
    private static void AddTombstoneCore(SqliteConnection conn, SqliteTransaction? tx, SyncTombstone tombstone)
    {
        conn.Execute(
            """
            INSERT INTO sync_tombstones
                (tombstone_id, entity_type, entity_global_id, device_id, deleted_at_utc, created_at_utc)
            VALUES
                (@TombstoneId, @EntityType, @EntityGlobalId, @DeviceId, @DeletedAtUtc, @CreatedAtUtc)
            ON CONFLICT(entity_type, entity_global_id) DO UPDATE SET
                deleted_at_utc = excluded.deleted_at_utc;
            """,
            tombstone,
            tx);
    }

    public async Task<IReadOnlyList<SyncTombstone>> GetTombstonesAsync(int limit = SyncOutboxPolicy.PullBatchSize)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<SyncTombstone>(
            """
            SELECT * FROM sync_tombstones
            ORDER BY deleted_at_utc ASC, rowid ASC
            LIMIT @limit;
            """,
            new { limit });

        return rows.ToList();
    }

    /// <summary>清理超过保留期的墓碑。⚠️ 不得短于 <see cref="SyncOutboxPolicy.TombstoneRetention"/>。</summary>
    public async Task<int> PruneTombstonesAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            return await conn.ExecuteAsync(
                "DELETE FROM sync_tombstones WHERE deleted_at_utc < @cutoff;",
                new { cutoff = UtcNowIso(DateTime.UtcNow - SyncOutboxPolicy.TombstoneRetention) });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ============================ 同步游标 ============================

    /// <summary>读取某个 Backend 的同步游标。切换 Backend 时**绝不复用**另一套游标。</summary>
    public async Task<SyncStateRecord?> GetSyncStateAsync(string backend, string accountId)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<SyncStateRecord>(
            "SELECT * FROM sync_state WHERE backend = @backend AND account_id = @accountId;",
            new { backend, accountId });
    }

    public async Task SaveSyncStateAsync(SyncStateRecord state)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO sync_state (backend, account_id, cursor, last_sync_at_utc, last_error)
                VALUES (@Backend, @AccountId, @Cursor, @LastSyncAtUtc, @LastError)
                ON CONFLICT(backend, account_id) DO UPDATE SET
                    cursor           = excluded.cursor,
                    last_sync_at_utc = excluded.last_sync_at_utc,
                    last_error       = excluded.last_error;
                """,
                state);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ============================ 设备 ============================

    /// <summary>本机设备 ID（不存在则立刻生成并落库）。device_id 一经生成永不改变。</summary>
    public async Task<string> GetLocalDeviceIdAsync()
    {
        using (var conn = CreateConnection())
        {
            var existing = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM settings WHERE key = @k;",
                new { k = DeviceSyncConstants.SettingKeyDeviceId });

            if (!string.IsNullOrWhiteSpace(existing)) return existing!;
        }

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            return DeviceSyncSchema.EnsureLocalDevice(conn);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>设备列表：本机在最前，其余按最后出现时间倒序。</summary>
    public async Task<IReadOnlyList<DeviceRecord>> GetDevicesAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<DeviceRecord>(
            """
            SELECT * FROM devices
            ORDER BY is_this_device DESC, COALESCE(last_seen_at_utc, created_at_utc) DESC;
            """);

        return rows.ToList();
    }

    /// <summary>修改设备显示名。⚠️ 只改 device_name —— 设备身份永远看 device_id。</summary>
    public async Task RenameDeviceAsync(string deviceId, string newName)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE devices SET device_name = @newName WHERE device_id = @deviceId;",
                new { deviceId, newName });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ==================== 启动去重的 identity 支撑 ====================

    /// <summary>
    /// 启动去重的**决策快照**：必须在任何写入之前采集。
    ///
    /// 为什么不能等做完再判定：现有去重顺序是「sessions 迁到 canonical → DELETE duplicate games」，
    /// 一旦 sessions 被搬走，那个 dup 就**不再"存在关联 sessions"**，再用 SyncableGame 规则
    /// 重新判定会把它误判成纯幽灵 → 漏写墓碑 → 云端 identity 永久残留（C3 明令禁止）。
    /// </summary>
    private sealed record DedupGameSnapshot(int Id, string? GlobalId, bool IsSyncable);

    /// <summary>快照里用到的 sessions 计数行。</summary>
    private sealed class GameSessionCount
    {
        public int GameId { get; set; }
        public int Cnt { get; set; }
    }

    /// <summary>
    /// 按 group 一次性查出每行的 sessions 数量并合成快照（不逐行往返查库）。
    /// 判定式复用 <see cref="GameSyncEligibility.IsSyncable"/>，与 DB 侧 SQL 是同一套规则。
    /// </summary>
    private static Dictionary<int, DedupGameSnapshot> TakeDedupSnapshot(SqliteConnection conn, List<GameRecord> group)
    {
        var ids = group.Select(g => g.Id).ToList();

        var counts = conn.Query<GameSessionCount>(
                "SELECT game_id AS GameId, COUNT(*) AS Cnt FROM sessions WHERE game_id IN @ids GROUP BY game_id;",
                new { ids })
            .ToDictionary(r => r.GameId, r => r.Cnt);

        return group.ToDictionary(
            g => g.Id,
            g => new DedupGameSnapshot(
                g.Id,
                string.IsNullOrWhiteSpace(g.GlobalId) ? null : g.GlobalId,
                GameSyncEligibility.IsSyncable(
                    g.Executable,
                    g.ExecutablePath,
                    counts.TryGetValue(g.Id, out var c) ? c : 0)));
    }

    /// <summary>
    /// 在**调用方的事务内**为被删除的游戏 identity 写一条墓碑。
    ///
    /// ⚠️ **当前无调用方（2e 起）**：原调用方是启动去重的"淘汰重复行"，
    /// 那其实属于**身份合并**（两套 identity 指向同一实体），已改为写
    /// <c>sync_identity_supersessions</c>。**保留本方法**是给"真删除"路径用的
    /// （`DeleteGameAsync` 目前还没有写墓碑，双向删除阶段接入时必须走这里，
    /// 而不是 supersession）—— 不要因为"暂时没人调用"就删掉它。
    ///
    /// 墓碑只认 <c>global_id</c>（Sappfiler 多设备同步身份），与 <c>notion_page_id</c> 无关。
    /// device_id 取本机（Phase 1 的 EnsureLocalDevice 已保证存在）。
    /// </summary>
    private static void WriteGameTombstone(SqliteConnection conn, SqliteTransaction? tx, string gameGlobalId)
    {
        var deviceId = conn.QuerySingleOrDefault<string>(
            "SELECT value FROM settings WHERE key = @k;",
            new { k = DeviceSyncConstants.SettingKeyDeviceId }, tx) ?? string.Empty;

        var now = UtcNowIso();

        AddTombstoneCore(conn, tx, new SyncTombstone
        {
            TombstoneId = Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Game),
            EntityGlobalId = gameGlobalId,
            DeviceId = deviceId,
            DeletedAtUtc = now,
            CreatedAtUtc = now
        });
    }

    // ============================ 私有工具 ============================

    private static string UtcNowIso() => UtcNowIso(DateTime.UtcNow);

    private static string UtcNowIso(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// 远端变更的落地结果。
///
/// <paramref name="Deferred"/> = 本轮**没有**落地的条目数（已存在 / 需要身份对账 / 依赖缺失 /
/// payload 版本不认识 / 本地有墓碑）。
///
/// ⚠️ <b>Deferred ≠ Completed</b>（用户 2026-10-03 冻结）：
/// 游标会越过这些条目，但它们**并没有在本机同步成功**。
/// 它们已写入 <c>sync_deferred_changes</c> 台账（含 change_id / entity / reason / schema_version /
/// 首次与最近一次发现时间），留给 <b>2e：Identity / Conflict Resolution</b> 重新处理。
/// 绝不能因为游标前进就把它们当成成功 —— 那是永久静默丢数据。
/// </summary>
public sealed record RemoteApplyResult(
    int Applied,
    int Deferred,
    int TombstonesPropagated,
    IReadOnlyList<string> DeferredReasons);

/// <summary>Game 身份对账的结果类别。</summary>
public enum GameReconcileResult
{
    /// <summary>远端 identity 已并入本地 canonical，并记录了 supersession。</summary>
    Merged,

    /// <summary>canonical 原本没有 identity → 继承了远端的（同一实体延续，无需 supersession）。</summary>
    IdentityContinued,

    /// <summary>幂等：该远端 identity 已被处理过。</summary>
    AlreadyKnown,

    /// <summary>阻断（强键冲突 / 命中本地 ignored）—— 确定性结论，不是待办。</summary>
    Blocked,

    /// <summary>延后（多候选 / Notion 页面冲突 / 数据不足），留给下次。</summary>
    Deferred,

    /// <summary>本地没有候选行（不合并，由调用方按正常落地规则处理）。</summary>
    NoLocalCandidate,

    /// <summary>payload 不合法或 schema 不认识。</summary>
    InvalidPayload
}

/// <summary>
/// Game 身份对账结果。
/// <paramref name="Level"/> 是命中的身份键级别（A/B/C/D），<paramref name="Reason"/> 是原因码。
/// </summary>
public sealed record GameReconcileOutcome(
    GameReconcileResult Result,
    int? CanonicalLocalId,
    string? CanonicalGlobalId,
    IdentityMatchLevel Level,
    string Reason);

/// <summary>Session 身份对账结果类别。</summary>
public enum SessionReconcileResult
{
    /// <summary>不同 global_id 但四项不可变事实一致 → 已收敛（只写了 supersession）。</summary>
    Converged,

    /// <summary>幂等：同 global_id 或该 identity 已收敛过。</summary>
    AlreadyKnown,

    /// <summary>拒绝改写历史（同 global_id 却报了不同事实）。</summary>
    Blocked,

    /// <summary>延后（事实不一致 / 多候选 / 信息不足）。</summary>
    Deferred,

    /// <summary>本地无候选（不归本机制管）。</summary>
    NoLocalCandidate,

    /// <summary>payload 不合法或 schema 不认识。</summary>
    InvalidPayload
}

/// <summary>Session 身份对账结果。</summary>
public sealed record SessionReconcileOutcome(
    SessionReconcileResult Result,
    int? CanonicalLocalId,
    string? CanonicalGlobalId,
    string Reason);

/// <summary>
/// Deferred 台账重处理结果（2e 步骤 7）。
/// <paramref name="StillDeferred"/> 的条目**保持 unresolved**，等下一次调用再试 —— 不在这里自旋。
/// </summary>
public sealed record ReconciliationSummary(
    int Scanned,
    int Resolved,
    int StillDeferred,
    int SkippedTombstoned,
    int NoPayload,
    int UnsupportedType,
    IReadOnlyList<string> Resolutions);
