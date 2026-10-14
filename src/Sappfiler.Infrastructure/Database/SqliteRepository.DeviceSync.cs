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
                // 「已删除（archived）」的 Game 不得再进入同步候选（用户 2026-10-07 要求：不许出现
                // "UI 看不到但后台仍当活跃 Game"的半完成状态）。
                $"SELECT COUNT(*) FROM games WHERE id = @gameId AND {GameSyncEligibility.SqlPredicate} "
                + $"AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicateFor("games")};",
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
                ApplyDailyDurationDelta(conn, tx, delta.Date, row.GameId, delta.DurationSeconds);
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
            RecomputeSessionCount(conn, tx, sessionId, durationSeconds);

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
                // ---- ① **先**补一条 Game（最小身份快照），再入队 Session（2h 设计 v2 §B.2 冻结）----
                //
                // 为什么必须在这里补：远端落地 Session 要求本地已有该 game_global_id，
                // 否则永远是 Deferred(dependency_missing)。而"启用同步之后才第一次玩的新游戏"
                // 不会被 backfill 覆盖（backfill 早已 done），少这一步就会导致对端**永久收不到**这局。
                //
                // 顺序保证：同一事务内先 Game 后 Session + 取待发按 rowid 升序 → 同一批里 Game 在前 ✓
                // 去重：本机队列里若已有该 Game 的变更就不再补（重复也无害，但没必要）。
                var gameAlreadyQueued = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM sync_queue WHERE entity_type = @type AND entity_global_id = @gid;",
                    new { type = nameof(SyncEntityType.Game), gid = gameGlobalId }, tx) > 0;

                if (!gameAlreadyQueued)
                {
                    var gameRow = await conn.QuerySingleAsync<BackfillGameRow>(
                        """
                        SELECT id, global_id, name, platform, platform_id, executable, executable_path, notion_page_id
                        FROM games WHERE id = @gameId;
                        """,
                        new { gameId = finalSession.GameId }, tx);

                    await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
                    {
                        QueueId = Guid.NewGuid().ToString("N"),
                        EntityType = nameof(SyncEntityType.Game),
                        EntityGlobalId = gameGlobalId!,
                        Operation = SyncOperation.Create,
                        BaseVersion = 1,
                        Payload = SyncPayloadFactory.Game(
                            gameRow.GlobalId!, gameRow.Name, gameRow.Platform, gameRow.PlatformId,
                            gameRow.Executable, gameRow.ExecutablePath, gameRow.NotionPageId,
                            // v2：本地有封面就带上内容 hash（没有则为空 —— 绝不制造假 blob）
                            coverBlobHash: TryRegisterLocalCoverBlob(conn, tx, gameRow.Platform, gameRow.PlatformId)),
                        CreatedAtUtc = UtcNowIso(),
                        Status = SyncQueueStatus.Pending
                    });
                }

                // ---- ② 再入队 Session ----
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
    ///
    /// ⚠️ 同步（非 async）：远端会话落地路径 <c>ApplyRemoteSession</c> 本身是同步的，
    /// 而同一条 SQL 只能有**一份**实现（否则口径必然漂移）。SQLite + Dapper 在此处
    /// 全程同步执行，语义与原先的 async 版本完全一致。
    /// </summary>
    private static void ApplyDailyDurationDelta(
        SqliteConnection conn, SqliteTransaction tx, string date, int gameId, int durationSeconds)
    {
        if (durationSeconds <= 0) return;

        var existing = conn.QuerySingleOrDefault<dynamic>(
            "SELECT id, duration_seconds FROM daily_summary WHERE date = @date AND game_id = @gameId;",
            new { date, gameId }, tx);

        if (existing != null)
        {
            var newSecs = (int)existing.duration_seconds + durationSeconds;
            var newMins = newSecs / 60;

            conn.Execute(
                """
                UPDATE daily_summary
                SET duration_seconds = @newSecs,
                    duration_minutes = @newMins,
                    sync_status      = 'pending'
                WHERE id = @id;
                """,
                new { id = (long)existing.id, newSecs, newMins },
                tx);
        }
        else
        {
            var mins = durationSeconds / 60;

            conn.Execute(
                """
                INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
                VALUES (@date, @gameId, @durationSeconds, @mins, 0, 'pending');
                """,
                new { date, gameId, durationSeconds, mins },
                tx);
        }

        // 🔒 2026-10-11（口径统一，count-rule-v1）：增量路径只落秒数；场次一律由唯一规则重算
        //    （旧实现的 `duration_minutes=0 且新值>0 就置 1` 已废弃）。
        ApplyUnifiedSessionCount(conn, tx, gameId, date, ReadDailyCutoffHour(conn, tx));
    }

    /// <summary>
    /// 重算当日有效场次（**统一口径的唯一实现**见
    /// <see cref="ComputeSessionCountForAccountingDate"/>）：
    /// ≥60 秒的会话按**起始时刻的会计日**计数，再加上「声明/残差 ≥60s 计 1 次」。
    ///
    /// <para>
    /// 🔧 2026-10-11（A/B 实机对差根因修复）：旧实现有两处缺陷 ——
    /// ① 用 <c>substr(start_time,1,10)</c>（**日历日**）分组，cutoff ≠ 24 时会把跨结算点的会话
    ///    计到错误的日期；② 只在"带 delta 的结束路径"被调用，**僵尸/陈旧收尾传空增量** ⇒
    ///    该 (game, 日) 的场次永远停留在 INSERT 默认值 1（实测 A 端 9/19、9/20 的 1 vs 3 就是这么来的）。
    /// 现在两处都按会计日口径统一，并由 daily 一致性审计兜底。
    /// </para>
    ///
    /// ⚠️ 同步（非 async）：远端会话落地路径 <c>ApplyRemoteSession</c> 本身是同步的，
    /// 而同一条口径只能有**一份**实现。SQLite + Dapper 在此处全程同步执行。
    /// </summary>
    private static void RecomputeSessionCount(
        SqliteConnection conn, SqliteTransaction tx, int sessionId, int durationSeconds)
    {
        // 只有当会话时长 >= 60 秒时才计入有效游玩次数；低于 60 秒的微小启动忽略。
        if (durationSeconds < 60) return;

        var row = conn.QuerySingleOrDefault<SessionFactRow>(
            """
            SELECT game_id AS GameId, start_time AS StartTime, duration_seconds AS DurationSeconds
            FROM sessions WHERE id = @sessionId;
            """,
            new { sessionId }, tx);

        if (row is null) return;

        if (!TryParseWallClock(row.StartTime, out var start))
        {
            // 旧口径会静默跳过（并留下一个错误场次）。这里必须可见：交给 daily 一致性审计报告。
            AppLog.Warn($"[日统计] 会话 {sessionId} 的 start_time 无法解析，场次计数跳过（由 daily 一致性审计报告）");
            return;
        }

        var cutoffHour = ReadDailyCutoffHour(conn, tx);
        var statisticalDate = GameTimeTracker.Core.Services.AccountingDateHelper.GetAccountingDateString(start, cutoffHour);

        ApplyUnifiedSessionCount(conn, tx, row.GameId, statisticalDate, cutoffHour);
    }

    /// <summary>
    /// 按统一场次口径重写某 (game, 统计日) 的 <c>session_count</c>（行不存在则不动）。
    /// </summary>
    private static void ApplyUnifiedSessionCount(
        SqliteConnection conn, SqliteTransaction? tx, long gameId, string statisticalDate, int cutoffHour,
        int preservedResidualSeconds = 0)
    {
        var exists = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM daily_summary WHERE game_id = @gameId AND date = @date;",
            new { gameId, date = statisticalDate }, tx) > 0;

        if (!exists) return;

        var count = ComputeSessionCountForAccountingDate(
            conn, tx, gameId, statisticalDate, cutoffHour, preservedResidualSeconds);

        conn.Execute(
            """
            UPDATE daily_summary SET session_count = @count
            WHERE game_id = @gameId AND date = @date;
            """,
            new { count, gameId, date = statisticalDate }, tx);
    }

    // ==================== 历史 Outbox Gap Repair（用户 2026-10-08 裁定） ====================

    /// <summary>
    /// <b>历史 Outbox Gap Repair</b>：只给"本地已经有、但 Device Sync 不知道"的历史事实**补建 outbox**，
    /// **不重扫历史、不重跑 Backfill、不重置 <c>backfill_state.done</c>**。
    ///
    /// <para>
    /// 为什么需要它：<c>sessions</c> 里的历史事实有两条**不经过结束路径**的来源 ——
    /// ① 旧版本（不含 Device Sync 代码）结束的会话；② 崩溃/被杀/异常关机后由启动自愈补了 identity、
    /// 但当时播种早已 <c>done = true</c>。它们"本地有、云端没有"，且 Backfill 永远不会再发现它们。
    /// </para>
    ///
    /// <para>
    /// 判据（每一类都要求"该实体的 change **不在** <c>sync_queue</c> 里"，**任何状态**都算"在"）：
    /// <list type="bullet">
    ///   <item><b>Game</b>：具备同步资格 + 状态正常 + <b>该 Game 至少有一条"本机产生"的会话</b>
    ///         → 缺 <c>global_id</c> 先按既有 identity 规则补齐
    ///         （复用 <c>DeviceSyncSchema.AssignMissingGameGlobalIds</c>，与启动自愈同一实现），
    ///         然后补一条 Game Create；</item>
    ///   <item><b>Session</b>：已结束（<c>is_active = 0</c>）、<c>global_id</c> 非空、其 Game 也有
    ///         <c>global_id</c>，且 <c>device_id = 本机</c> → 补一条 Session Create（payload 取自库里的最终事实）。</item>
    /// </list>
    /// Game 先入队、Session 后入队（rowid 顺序 = 推送顺序），保证对端落地 Session 时 Game identity 可解析。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>必须是"本机产生"的事实</b>（<c>sessions.device_id = 本机 device_id</c>）——
    /// 这是 2026-10-08 第二次双机验收抓到的缺陷：从对端**落地的**历史事实同样是
    /// "有 global_id、本地没有队列行"，但它们在**对端本来就有**，再推一遍只会让对端把整批
    /// 判成 <c>already_exists_local</c>（永久 Deferred），同时把"本机真正的新数据"淹在里面。
    /// <c>device_id</c> 是 Session 的不可变事实之一（"产生这条会话的设备"），因此它就是
    /// "这条事实归谁"的权威判据；落地的会话保留原产设备 id，不会被误认成本机事实。
    /// </para>
    ///
    /// <para>
    /// 语义保证：
    /// <list type="bullet">
    ///   <item><b>不重复上传</b>：已经有队列行（含 Completed / 已推送的）的事实天然被 anti-join 跳过；</item>
    ///   <item><b>不改 immutable facts</b>：除"给可同步游戏补 identity"这一条既有规则外，
    ///         只 INSERT <c>sync_queue</c>；sessions 的字段一个都不动；</item>
    ///   <item><b>幂等</b>：判据本身 + 确定性 <c>queue_id</c>（与播种同源）⇒ 重复执行不新增；</item>
    ///   <item><b>有界</b>：每类单遍最多 <paramref name="limit"/> 条，不自旋（没补完的下次启动接着补）；</item>
    ///   <item><b>原子</b>：全部在一个事务里，任何一步失败整体回滚 —— 宁可不补，也不留半截队列。</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task<OutboxGapRepairResult> RepairOutboxGapsAsync(
        int limit = SyncOutboxPolicy.GapRepairBatchSize, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) limit = SyncOutboxPolicy.GapRepairBatchSize;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            // ---- ① Game identity：与启动自愈同一实现 ----
            var identityAssigned = DeviceSyncSchema.AssignMissingGameGlobalIds(conn, tx);

            // ---- 本机设备身份（= 哪些事实"归本机"，见方法注释）----
            var localDeviceId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM settings WHERE key = @k;",
                new { k = DeviceSyncConstants.SettingKeyDeviceId }, tx);

            if (string.IsNullOrWhiteSpace(localDeviceId))
            {
                localDeviceId = DeviceSyncSchema.EnsureLocalDevice(conn, tx);
            }

            // ---- ② Game 缺口（只处理"本机产生过往返事实"的 Game）----
            var games = (await conn.QueryAsync<BackfillGameRow>($"""
                SELECT games.id, games.global_id, games.name, games.platform, games.platform_id,
                       games.executable, games.executable_path, games.notion_page_id
                FROM games
                WHERE NULLIF(TRIM(games.global_id), '') IS NOT NULL
                  AND {GameSyncEligibility.SqlPredicate}
                  AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate}
                  AND EXISTS (
                        SELECT 1 FROM sessions s2
                        WHERE s2.game_id = games.id AND s2.device_id = @localDeviceId)
                  AND NOT EXISTS (
                        SELECT 1 FROM sync_queue q
                        WHERE q.entity_type = @gameType AND q.entity_global_id = games.global_id)
                ORDER BY games.id ASC
                LIMIT @limit;
                """,
                new { gameType = nameof(SyncEntityType.Game), limit, localDeviceId }, tx)).ToList();

            foreach (var game in games)
            {
                await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
                {
                    QueueId = SyncHashing.BackfillQueueId(nameof(SyncEntityType.Game), game.GlobalId),
                    EntityType = nameof(SyncEntityType.Game),
                    EntityGlobalId = game.GlobalId,
                    Operation = SyncOperation.Create,
                    BaseVersion = 1,
                    Payload = SyncPayloadFactory.Game(
                        game.GlobalId, game.Name, game.Platform, game.PlatformId,
                        game.Executable, game.ExecutablePath, game.NotionPageId,
                        // v2：本地封面 → 内容 hash（同一张图只会有一个 blob）
                        coverBlobHash: TryRegisterLocalCoverBlob(conn, tx, game.Platform, game.PlatformId)),
                    CreatedAtUtc = UtcNowIso(),
                    Status = SyncQueueStatus.Pending
                });
            }

            // ---- ③ Session 缺口（只要**本机产生**的会话；从对端落地的会话归对端，绝不回推）----
            var sessions = (await conn.QueryAsync<BackfillSessionRow>(
                """
                SELECT s.id, s.global_id, s.device_id, s.process_name, s.started_at_utc, s.ended_at_utc,
                       s.duration_seconds, s.time_precision, g.global_id AS game_global_id
                FROM sessions s
                JOIN games g ON g.id = s.game_id
                WHERE s.is_active = 0
                  AND s.device_id = @localDeviceId
                  AND NULLIF(TRIM(s.global_id), '') IS NOT NULL
                  AND NULLIF(TRIM(g.global_id), '') IS NOT NULL
                  AND NOT EXISTS (
                        SELECT 1 FROM sync_queue q
                        WHERE q.entity_type = @sessionType AND q.entity_global_id = s.global_id)
                ORDER BY s.id ASC
                LIMIT @limit;
                """,
                new { sessionType = nameof(SyncEntityType.Session), limit, localDeviceId }, tx)).ToList();

            foreach (var session in sessions)
            {
                await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
                {
                    QueueId = SyncHashing.BackfillQueueId(nameof(SyncEntityType.Session), session.GlobalId),
                    EntityType = nameof(SyncEntityType.Session),
                    EntityGlobalId = session.GlobalId,
                    Operation = SyncOperation.Create,
                    BaseVersion = 1,
                    Payload = SyncPayloadFactory.Session(
                        session.GlobalId, session.GameGlobalId, session.DeviceId ?? string.Empty,
                        session.ProcessName ?? string.Empty, session.StartedAtUtc ?? string.Empty,
                        session.EndedAtUtc, session.DurationSeconds,
                        session.TimePrecision ?? TimePrecision.Exact),
                    CreatedAtUtc = UtcNowIso(),
                    Status = SyncQueueStatus.Pending
                });
            }

            // ---- ④ DailyAdjustment 缺口（2026-10-10）----
            // 判据与 ②③ **完全一致**：只处理"本机产生"的声明（device_id = 本机）、
            // 其 Game 已有 global_id、且 sync_queue 里**任何状态**都没有该 entity_global_id 的行
            // （Pending / InFlight / Completed / Failed 都算"已入队" ⇒ 不重复创建，也不绕过既有重试/死信策略）。
            // ⚠️ 这里**不**再嵌 `GameSyncEligibility.SqlPredicate`：它引用的是无别名的 `games`，
            //    而本查询用了别名 `g`（会在 SQLite 里解析失败）。资格判定已经由上面 ① 的
            //    `AssignMissingGameGlobalIds` 统一保证 —— 带导入事实的游戏现在也算"可同步"，
            //    所以走到这里时 `g.global_id` 必然已就绪（与 ③ Session 类同口径：只要求 game 有身份）。
            var adjustments = (await conn.QueryAsync<AdjustmentGapRow>("""
                SELECT a.adjustment_global_id AS AdjustmentGlobalId, a.statistical_date AS StatisticalDate,
                       a.device_id AS DeviceId, a.duration_seconds AS DurationSeconds,
                       a.source AS Source, a.created_at_utc AS CreatedAtUtc,
                       g.global_id AS GameGlobalId
                FROM daily_duration_adjustments a
                JOIN games g ON g.id = a.game_id
                WHERE NULLIF(TRIM(a.adjustment_global_id), '') IS NOT NULL
                  AND a.duration_seconds > 0
                  AND a.device_id = @localDeviceId
                  AND NULLIF(TRIM(g.global_id), '') IS NOT NULL
                  AND NOT EXISTS (
                        SELECT 1 FROM sync_queue q
                        WHERE q.entity_type = @adjustmentType
                          AND q.entity_global_id = a.adjustment_global_id)
                ORDER BY a.rowid ASC
                LIMIT @limit;
                """,
                new
                {
                    adjustmentType = nameof(SyncEntityType.DailyAdjustment),
                    limit,
                    localDeviceId
                }, tx)).ToList();

            // ---- ④-a 依赖 Game 补齐（2026-10-11 事故修复）----
            // ② Game 缺口要求"该 Game 至少有一条**本机产生**的会话"；而 Notion 手工录入 / 导入出来的游戏
            // 可能**一条会话都没有**。旧实现只补 Adjustment、不补 Game ⇒ 对端永远解析不到 game_global_id
            // （永久 dependency_missing）—— 实机事故："Notion 手工录入的数据没有同步到另一台设备"。
            // 因此这里必须**先**把依赖 Game 补进队列：同一事务、先 INSERT ⇒ rowid 顺序 = 推送顺序，
            // Game 一定排在引用它的 Adjustment **之前**。判据与 ② 逐字一致，只是去掉"有本机会话"这一条。
            var repairedDependencyGames = 0;
            var dependencyGameGlobalIds = adjustments
                .Select(a => a.GameGlobalId)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var dependencyGlobalId in dependencyGameGlobalIds)
            {
                if (games.Exists(g => string.Equals(g.GlobalId, dependencyGlobalId, StringComparison.Ordinal)))
                {
                    continue;   // ② 这一轮已经补过
                }

                if (await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM sync_queue WHERE entity_type = @type AND entity_global_id = @gid;",
                        new { type = nameof(SyncEntityType.Game), gid = dependencyGlobalId }, tx) > 0)
                {
                    continue;   // 任何状态都算"已入队"，绝不重复创建（也不绕过既有重试/死信策略）
                }

                // 🛡️ 2026-10-11（身份协议兼容核查）：按**等价类**解析，而不是精确匹配 global_id。
                //    被引用的 identity 可能是本机某一行的"别名"（canonical 在别处）—— 精确匹配会
                //    静默跳过，导致该 Game 的 Create 永远补不进队列，对端永远学不到这款游戏。
                var dependencyLocalId = ResolveLocalGameIdByGlobalId(conn, tx, dependencyGlobalId);

                var dependencyGame = dependencyLocalId is null
                    ? null
                    : (await conn.QueryAsync<BackfillGameRow>($"""
                        SELECT games.id, games.global_id, games.name, games.platform, games.platform_id,
                               games.executable, games.executable_path, games.notion_page_id
                        FROM games
                        WHERE games.id = @id
                          AND {GameSyncEligibility.SqlPredicate}
                          AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate}
                        LIMIT 1;
                        """,
                        new { id = dependencyLocalId.Value }, tx)).FirstOrDefault();

                if (dependencyGame is null)
                {
                    continue;   // 不合格 / 已删除 / 本地不存在 ⇒ 绝不凭空造一个 Game
                }

                await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
                {
                    QueueId = SyncHashing.BackfillQueueId(nameof(SyncEntityType.Game), dependencyGame.GlobalId),
                    EntityType = nameof(SyncEntityType.Game),
                    EntityGlobalId = dependencyGame.GlobalId,
                    Operation = SyncOperation.Create,
                    BaseVersion = 1,
                    Payload = SyncPayloadFactory.Game(
                        dependencyGame.GlobalId, dependencyGame.Name, dependencyGame.Platform,
                        dependencyGame.PlatformId, dependencyGame.Executable, dependencyGame.ExecutablePath,
                        dependencyGame.NotionPageId,
                        coverBlobHash: TryRegisterLocalCoverBlob(
                            conn, tx, dependencyGame.Platform, dependencyGame.PlatformId)),
                    CreatedAtUtc = UtcNowIso(),
                    Status = SyncQueueStatus.Pending
                });

                repairedDependencyGames++;
            }

            foreach (var adjustment in adjustments)
            {
                await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
                {
                    QueueId = SyncHashing.BackfillQueueId(
                        nameof(SyncEntityType.DailyAdjustment), adjustment.AdjustmentGlobalId),
                    EntityType = nameof(SyncEntityType.DailyAdjustment),
                    EntityGlobalId = adjustment.AdjustmentGlobalId,
                    Operation = SyncOperation.Create,
                    BaseVersion = 1,
                    Payload = SyncPayloadFactory.DailyAdjustment(
                        adjustment.AdjustmentGlobalId, adjustment.GameGlobalId,
                        adjustment.StatisticalDate, adjustment.DeviceId ?? string.Empty,
                        adjustment.DurationSeconds, adjustment.Source,
                        string.IsNullOrWhiteSpace(adjustment.CreatedAtUtc) ? UtcNowIso() : adjustment.CreatedAtUtc),
                    CreatedAtUtc = UtcNowIso(),
                    Status = SyncQueueStatus.Pending
                });
            }

            tx.Commit();

            var result = new OutboxGapRepairResult(identityAssigned, games.Count, sessions.Count)
            {
                AdjustmentsEnqueued = adjustments.Count,
                DependencyGamesEnqueued = repairedDependencyGames
            };

            if (identityAssigned > 0 || result.Total > 0)
            {
                AppLog.Info($"[设备同步] 历史 Outbox 缺口修复：补 Game identity {identityAssigned}、"
                    + $"补 Game 变更 {games.Count}、补依赖 Game 变更 {repairedDependencyGames}、"
                    + $"补 Session 变更 {sessions.Count}、补 Adjustment 变更 {adjustments.Count}"
                    + "（只补缺口，不重扫历史）");
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 历史 Outbox 缺口修复失败（整体回滚，下次启动再试）：{ex.GetType().Name}");
            return OutboxGapRepairResult.Nothing;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Gap Repair 用的 Adjustment 缺口行（只读投影）。</summary>
    private sealed class AdjustmentGapRow
    {
        public string AdjustmentGlobalId { get; set; } = string.Empty;
        public string StatisticalDate { get; set; } = string.Empty;
        public string? DeviceId { get; set; }
        public int DurationSeconds { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? CreatedAtUtc { get; set; }
        public string GameGlobalId { get; set; } = string.Empty;
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

                    // ⚠️ 2026-10-07（C5）：收到远端 Delete 时，除了落墓碑，还必须把本地该 Game
                    // 置为「历史归档」—— 否则在接收端这个 Game 仍以**正常身份**出现在列表/候选里
                    // （与"删除事实"矛盾）。只改 status：历史 Session / daily_summary 一律原样保留。
                    // 幂等：已经是 archived 的行不重复写。
                    if (string.Equals(change.EntityType, nameof(SyncEntityType.Game), StringComparison.Ordinal))
                    {
                        var archivedStatus = GameTimeTracker.Core.DeviceSync.GameStatuses.Archived;

                        // ⓿ 🛡️ 2026-10-11（身份协议）：删除的是**逻辑实体**，不是某一个 identity。
                        //    · 等价类内**每个** identity 都写墓碑 ⇒ 旧 Create/Update 的重放被确定性挡下
                        //      （不再依赖"同平台同名"这类弱线索）；
                        //    · 等价类内**全部**本机行一起归档 ⇒ 身份分裂留下的副本也一并消失。
                        var deleteClass = ReadIdentityClassCore(conn, tx, new[] { change.EntityGlobalId });

                        // 记录"本次之前就已经有墓碑"的 identity —— 用于判断是否需要替别名补发 Delete（见下）。
                        var preTombstoned = deleteClass.Count == 0
                            ? new HashSet<string>(StringComparer.Ordinal)
                            : conn.Query<string>(
                                """
                                SELECT entity_global_id FROM sync_tombstones
                                WHERE entity_type = @type AND entity_global_id IN @members;
                                """,
                                new { type = nameof(SyncEntityType.Game), members = deleteClass }, tx)
                              .ToHashSet(StringComparer.Ordinal);

                        // ⚠️ 2026-10-12（本机更新意图优先）：远端 Delete 不得**无条件**覆盖本机较新的
                        //    重新激活意图（用户删了游戏又重新启动它 ⇒ reactivated_at_utc 被写入）。
                        //    先统计会被保护的行数（**诊断用**），归档语句内部按同一条 LocalIntentWinsSql
                        //    跳过它们 —— 两处判据必须逐字同源，否则日志会与实际归档行为不一致。
                        var deletionAtUtc = (change.CreatedAtUtc ?? string.Empty).Trim();
                        var protectedByLocalIntent = deletionAtUtc.Length == 0
                            ? 0
                            : conn.ExecuteScalar<int>(
                                $"""
                                SELECT COUNT(*) FROM games
                                WHERE global_id IN @members
                                  AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate}
                                  AND julianday(COALESCE(reactivated_at_utc, '1970-01-01 00:00:00')) > julianday(@deletionAtUtc);
                                """,
                                new { members = deleteClass, deletionAtUtc }, tx);

                        long affected = ArchiveIdentityClassRowsCore(conn, tx, deleteClass, deletionAtUtc);

                        if (protectedByLocalIntent > 0)
                        {
                            AppLog.Info($"[设备同步] 收到远端 Game 删除 {change.EntityGlobalId}，但本机有 "
                                + $"{protectedByLocalIntent} 行在删除之后又被本机更新过（例如重新启动游戏 ⇒ 重新激活），"
                                + "按「本机更新意图优先」保留其状态、未归档；墓碑仍照常写入。");
                        }

                        if (deleteClass.Count > 1)
                        {
                            WriteIdentityClassTombstonesCore(conn, tx, deleteClass,
                                string.IsNullOrWhiteSpace(change.DeviceId) ? localDeviceId : change.DeviceId);
                        }

                        // 🛡️ 2026-10-11（旧版本 / 多设备兼容）：如果这次删除把**别的 identity**（等价类别名）也归档了，
                        //    就替它们各补发一条 Delete —— 只认"精确 global_id"的对端（例如旧版本设备、
                        //    或持有另一个 identity 的第三台设备）才能同样归档，否则会留下"界面已删、对端还在"。
                        //    只广播"本次之前没有墓碑"的 identity（多轮交换后自然终止，不会 ping-pong）。
                        if (affected > 0 && deleteClass.Count > 1)
                        {
                            var announce = deleteClass
                                .Where(id => !string.Equals(id, change.EntityGlobalId, StringComparison.Ordinal)
                                             && !preTombstoned.Contains(id))
                                .ToList();

                            if (announce.Count > 0)
                            {
                                var snapshotRow = conn.QueryFirstOrDefault<GameReconcileRow>(
                                    "SELECT * FROM games WHERE global_id IN @members ORDER BY id ASC LIMIT 1;",
                                    new { members = deleteClass }, tx);

                                if (snapshotRow is not null)
                                {
                                    // 2026-10-12：带上**本次收到的删除时刻** ⇒ 别名 Delete 也按删除事件唯一，
                                    // 反复删除/重启用时不会因为"队列里已有同名 id"被静默丢掉。
                                    EnqueueIdentityClassDeletesCore(conn, tx, announce,
                                        snapshotRow.Name ?? string.Empty, snapshotRow.Platform ?? string.Empty,
                                        snapshotRow.PlatformId ?? string.Empty, snapshotRow.Executable ?? string.Empty,
                                        snapshotRow.ExecutablePath ?? string.Empty, snapshotRow.NotionPageId,
                                        deletionAtUtc);
                                }
                            }
                        }

                        // ① 精确命中：本机 global_id == 墓碑 identity
                        // ② 别名命中：该 identity 已被身份对账并入本机 canonical
                        //    （两台机器各自随机生成 global_id，不算别名就永远匹配不到 ⇒ 删除静默失效）
                        //    ⚠️ 只在⓿没命中任何行时才走（否则会重复归档同一行，且弱线索兜底会误伤同名游戏）。
                        var resolvedByAlias = affected > 0
                            ? null
                            : ResolveLocalGameIdByGlobalId(conn, tx, change.EntityGlobalId);

                        if (affected == 0 && resolvedByAlias is not null)
                        {
                            affected = await conn.ExecuteAsync(
                                $"""
                                UPDATE games
                                SET status = @archived, updated_at = CURRENT_TIMESTAMP
                                WHERE id = @id AND LOWER(COALESCE(status, '')) <> @archived
                                {LocalIntentWinsSql};
                                """,
                                new { archived = archivedStatus, id = resolvedByAlias.Value, deletionAtUtc }, tx);
                        }
                        else if (affected == 0)
                        {
                            affected = await conn.ExecuteAsync(
                                $"""
                                UPDATE games
                                SET status = @archived, updated_at = CURRENT_TIMESTAMP
                                WHERE global_id = @gid AND LOWER(COALESCE(status, '')) <> @archived
                                {LocalIntentWinsSql};
                                """,
                                new { archived = archivedStatus, gid = change.EntityGlobalId, deletionAtUtc }, tx);
                        }

                        // ③ 强键兜底：identity 不匹配（历史遗留/identity 分裂）时，用 wire payload 的
                        //    (platform, platform_id) 找到同一款游戏。没有这一步，对端会永远保留一条
                        //    "界面上已删除、本地仍活动"的游戏（实测 B 端 Ficket 就是这个形态）。
                        if (affected == 0)
                        {
                            var payloadPlatform = conn.ExecuteScalar<string?>(
                                "SELECT json_extract(@p, '$.platform');", new { p = change.Payload }, tx);
                            var payloadPlatformId = conn.ExecuteScalar<string?>(
                                "SELECT json_extract(@p, '$.platform_id');", new { p = change.Payload }, tx);

                            if (!string.IsNullOrWhiteSpace(payloadPlatform) && !string.IsNullOrWhiteSpace(payloadPlatformId))
                            {
                                affected = await conn.ExecuteAsync(
                                    $"""
                                    UPDATE games
                                    SET status = @archived, updated_at = CURRENT_TIMESTAMP
                                    WHERE LOWER(platform) = LOWER(@platform)
                                      AND LOWER(platform_id) = LOWER(@platformId)
                                      AND LOWER(COALESCE(status, '')) <> @archived
                                    {LocalIntentWinsSql};
                                    """,
                                    new { archived = archivedStatus, platform = payloadPlatform, platformId = payloadPlatformId, deletionAtUtc }, tx);
                            }
                        }

                        // ④ 身份分裂兜底（2026-10-11）：两台设备各自随机生成 platform_id（manual 行）时，
                        //    ③ 的强键也对不上 ⇒ 接收端会永远保留一条"界面上还在"的已删除游戏（实测形态）。
                        //    这里再用 wire payload 的 notion_page_id（跨设备共享身份）与"同平台同名"各找一次。
                        //    仍然只改 status：历史 Session / daily_summary 一个字节都不动。
                        if (affected == 0)
                        {
                            var payloadNotionPageId = conn.ExecuteScalar<string?>(
                                "SELECT json_extract(@p, '$.notion_page_id');", new { p = change.Payload }, tx);

                            if (!string.IsNullOrWhiteSpace(payloadNotionPageId))
                            {
                                affected = await conn.ExecuteAsync(
                                    $"""
                                    UPDATE games
                                    SET status = @archived, updated_at = CURRENT_TIMESTAMP
                                    WHERE notion_page_id = @npid
                                      AND LOWER(COALESCE(status, '')) <> @archived
                                    {LocalIntentWinsSql};
                                    """,
                                    new { archived = archivedStatus, npid = payloadNotionPageId, deletionAtUtc }, tx);
                            }
                        }

                        if (affected == 0)
                        {
                            // ④（2026-10-11 身份模型审查）**弱线索只报告、绝不归档**。
                            //
                            //    名称不是实体身份：同平台同名可能是另一款游戏（demo 与正式条目、同平台两个
                            //    发行条目、用户后来重新添加的同款）。在没有 supersession alias 或强业务键的
                            //    情况下"顺手归档同名的行"，等于凭名字猜身份 —— 会把无关游戏从界面上抹掉、
                            //    并让它的历史事实挂到错误的删除语义上（不可逆）。
                            //
                            //    因此这里只做两件事：① 明确留痕；② 把"删除无法确认目标"记进既有延后台账
                            //    （确定性 change_id ⇒ 重复 Delete 幂等），等人工建立身份关联后再传播。
                            var payloadPlatformForName = conn.ExecuteScalar<string?>(
                                "SELECT json_extract(@p, '$.platform');", new { p = change.Payload }, tx);
                            var payloadName = conn.ExecuteScalar<string?>(
                                "SELECT json_extract(@p, '$.name');", new { p = change.Payload }, tx);
                            var weakCandidates = FindLocalGamesByPlatformAndName(conn, tx, payloadPlatformForName, payloadName);

                            AppLog.Warn($"[设备同步] 收到 Game 删除墓碑 {change.EntityGlobalId}，"
                                + "但本机没有身份匹配（identity / 强业务键 / notion_page_id 都没命中）：墓碑已保存，"
                                + "该 identity 的旧 Create/Update 不会再落地；"
                                + (weakCandidates.Count > 0
                                    ? $"存在同平台同名候选 {string.Join(",", weakCandidates)} —— 名称不足以确认身份，"
                                      + "不归档、只记录冲突等人工裁定"
                                    : "本机也没有同平台同名候选"));

                            if (weakCandidates.Count > 0)
                            {
                                deferredItems.Add((new SyncChange
                                {
                                    // 确定性 change_id：重复收到同一条 Delete 只更新 seen_count，不堆行。
                                    ChangeId = $"identity-review:{change.EntityType}:{change.EntityGlobalId}",
                                    // 独立 entity_type ⇒ 延后对账分派器保持 unresolved（复核项，不是实体变更）。
                                    EntityType = IdentityReviewEntityType,
                                    EntityGlobalId = change.EntityGlobalId,
                                    Operation = change.Operation,
                                    BaseVersion = change.BaseVersion,
                                    Payload = change.Payload,
                                    DeviceId = change.DeviceId,
                                    CreatedAtUtc = change.CreatedAtUtc ?? ""
                                }, DeferredReasons.PossibleDeletedGameIdentityConflict, 0));
                            }
                        }
                    }

                    // 🛡️ 2026-10-11（记录删除跨设备）：**会话删除必须同样作用于本机事实**。
                    //    旧实现只落墓碑、不删本机会话 ⇒ 对端删了一条游玩记录，本机仍把它计入统计
                    //    （"界面已删、统计照旧"）。删除后立刻按剩余事实重算该会计日的派生行。
                    if (string.Equals(change.EntityType, nameof(SyncEntityType.Session), StringComparison.Ordinal))
                    {
                        RemoveLocalSessionByGlobalIdCore(conn, tx, change.EntityGlobalId);
                    }

                    tombstonesPropagated++;
                    continue;
                }

                // 墓碑优先：已删除的实体，旧的 Create/Update 一律不落地。
                if (alreadyTombstoned)
                {
                    // 🛡️ 2026-10-11（撤销乱序持久化）：**声明撤销**是可被"更新的重新录入"覆盖的状态 ——
                    //    Notion 里为同一 (设备, 游戏, 统计日, 来源) 重新录入会产生**同一个 identity**，
                    //    因此只有**旧于撤销**的陈旧声明（游标回卷 / Backfill 重放 / 历史快照）必须被挡下；
                    //    更新的声明要放行（并清掉墓碑），否则用户重新录入的时长永远进不来。
                    if (string.Equals(change.EntityType, nameof(SyncEntityType.DailyAdjustment), StringComparison.Ordinal))
                    {
                        var revokedAt = conn.ExecuteScalar<string?>(
                            "SELECT deleted_at_utc FROM sync_tombstones WHERE entity_type = @type AND entity_global_id = @gid LIMIT 1;",
                            new { type = change.EntityType, gid = change.EntityGlobalId }, tx);

                        var incomingAt = conn.ExecuteScalar<string?>(
                            "SELECT json_extract(@p, '$.created_at_utc');", new { p = change.Payload }, tx);

                        if (!string.IsNullOrWhiteSpace(revokedAt)
                            && !string.IsNullOrWhiteSpace(incomingAt)
                            && string.CompareOrdinal(incomingAt, revokedAt) > 0)
                        {
                            conn.Execute(
                                "DELETE FROM sync_tombstones WHERE entity_type = @type AND entity_global_id = @gid;",
                                new { type = change.EntityType, gid = change.EntityGlobalId }, tx);
                        }
                        else
                        {
                            deferredItems.Add((change, DeferredReasons.TombstonedLocal,
                                SyncPayloads.TryReadSchemaVersion(change.Payload)));
                            continue;
                        }
                    }
                    else if (string.Equals(change.EntityType, nameof(SyncEntityType.Game), StringComparison.Ordinal))
                    {
                        // 🛡️ 2026-10-12（**再激活的传播**）：对端可以**撤销自己的删除**。
                        //
                        // 场景（真机实测，两端数据可复现）：B 删除游戏 G（写墓碑 + 发 Delete）⇒ A 收到后
                        // 归档 G 并落墓碑；之后用户又在 B 上**重新启动** G（GetOrCreateGameAsync 重新激活）
                        // ⇒ B 入队一条 Game Create（信封时间 = 重新启用那一刻）。这一条到达 A 时**必须**
                        // 能解除 A 的删除，否则 A 永远停在"已删除"：既看不到 G 恢复，也**收不到 B 之后
                        // 产生的会话**（A 端落 deletion_conflict 且永久挂起 —— 实测有 2 条）。
                        //
                        // 判据与 DailyAdjustment 完全同构：**只有比墓碑更新的**那条才放行；
                        // 旧于墓碑的 Create（游标回卷 / Backfill 重放 / 历史快照）照旧被挡 ——
                        // "远端旧数据把已删除的游戏复活"这条底线不变。
                        var tombstonedAt = conn.ExecuteScalar<string?>(
                            "SELECT deleted_at_utc FROM sync_tombstones WHERE entity_type = @type AND entity_global_id = @gid LIMIT 1;",
                            new { type = change.EntityType, gid = change.EntityGlobalId }, tx);

                        var incomingAt = (change.CreatedAtUtc ?? string.Empty).Trim();

                        // ⚠️ 时间用 julianday() 归一化：两侧可能是 ISO+Z / 空格分隔 / 带毫秒，
                        //    直接比较字符串在格式不同时会给出错误结论。
                        //    任一侧缺失 ⇒ 不放行（保守：绝不因为"没有时间信息"就复活已删除的游戏）。
                        var revokes = !string.IsNullOrWhiteSpace(incomingAt)
                                      && conn.ExecuteScalar<int>(
                                          """
                                          SELECT CASE WHEN julianday(@incoming) > julianday(COALESCE(@tombstoned, ''))
                                                      THEN 1 ELSE 0 END;
                                          """,
                                          new { incoming = incomingAt, tombstoned = tombstonedAt }, tx) > 0;

                        if (!revokes)
                        {
                            deferredItems.Add((change, DeferredReasons.TombstonedLocal,
                                SyncPayloads.TryReadSchemaVersion(change.Payload)));
                            continue;
                        }

                        conn.Execute(
                            "DELETE FROM sync_tombstones WHERE entity_type = @type AND entity_global_id = @gid;",
                            new { type = change.EntityType, gid = change.EntityGlobalId }, tx);

                        // 取消归档**整个身份等价类**的本机行（身份分裂留下的副本也要一起恢复）。
                        var revivedClass = ReadIdentityClassCore(conn, tx, new[] { change.EntityGlobalId });
                        var revived = revivedClass.Count == 0
                            ? 0
                            : conn.Execute(
                                $"""
                                UPDATE games
                                SET status = @active, updated_at = CURRENT_TIMESTAMP
                                WHERE global_id IN @members
                                  AND LOWER(COALESCE(status, '')) = @archived;
                                """,
                                new
                                {
                                    active = GameTimeTracker.Core.DeviceSync.GameStatuses.Active,
                                    archived = GameTimeTracker.Core.DeviceSync.GameStatuses.Archived,
                                    members = revivedClass
                                }, tx);

                        AppLog.Info($"[设备同步] 收到远端「重新启用」{change.EntityGlobalId}（变更时间 {incomingAt} "
                            + $"晚于本机墓碑 {tombstonedAt}）⇒ 已撤销本机删除：清除墓碑、恢复 {revived} 行到 active；"
                            + "该游戏此前被延后的会话会在下次启动的账目对账里自动落地。");

                        if (revived > 0)
                        {
                            // 本机已有该身份的行 ⇒ 取消归档就够了，不必再走"落地 Create"
                            // （否则只会多一条无意义的 Deferred）。
                            applied++;
                            continue;
                        }

                        // 本机没有该身份的行（例如删除来自第三台设备）⇒ 清墓碑后按正常落地规则插入。
                    }
                    else
                    {
                        deferredItems.Add((change, DeferredReasons.TombstonedLocal,
                            SyncPayloads.TryReadSchemaVersion(change.Payload)));
                        continue;
                    }
                }

                var (reason, schemaVersion) = change.EntityType switch
                {
                    nameof(SyncEntityType.Game) => ApplyRemoteGame(conn, tx, change),
                    nameof(SyncEntityType.Session) => ApplyRemoteSession(conn, tx, change),
                    // v2：日时长调整（手工/Notion 日时长的跨设备事实）。
                    nameof(SyncEntityType.DailyAdjustment) => ApplyRemoteDailyAdjustment(conn, tx, change),
                    // 2026-10-11（身份协议）：跨设备身份关联本身也是同步实体 ——
                    // 它是"删除能传播、Session/声明能落到同一逻辑实体"的前提。
                    // 落地时**复算** canonical（因此乱序/重复/并发到达都收敛，不会 ping-pong）。
                    nameof(SyncEntityType.GameIdentity) => ApplyRemoteGameIdentity(conn, tx, change),
                    // 2026-10-11（记录删除跨设备）：**每日游玩分片排除事实** ——
                    // 删除粒度是 (game, 会计日, session)；不能用整条 Session 的墓碑表达，
                    // 否则会连带删掉同一会话在另一个会计日的有效时长。
                    nameof(SyncEntityType.DailyPlayExclusion) => ApplyRemoteDailyPlayExclusion(conn, tx, change),
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
    /// <summary>
    /// 落地一条远端身份关联（<see cref="SyncEntityType.GameIdentity"/>）。
    /// 由 <c>SqliteRepository.GameIdentityProtocol.cs</c> 实现：复算 canonical → 折叠本机重复行 →
    /// 必要时把复算结果回传（"一处确认、两端收敛"）。冻结规则冲突时与其它实体一样进 Deferred。
    /// </summary>
    private static (string? Reason, int SchemaVersion) ApplyRemoteGameIdentity(
        SqliteConnection conn, SqliteTransaction tx, SyncChange change)
    {
        var reason = TryApplyRemoteIdentityMapping(conn, tx, change, out var schemaVersion);
        return (reason, Math.Max(schemaVersion, 0));
    }

    private static (string? Reason, int SchemaVersion) ApplyRemoteGame(
        SqliteConnection conn, SqliteTransaction tx, SyncChange change)
    {
        if (!SyncPayloads.TryParseGame(change.Payload, out var payload, out var schemaVersion, out _))
        {
            return (ClassifyParseFailure(schemaVersion), Math.Max(schemaVersion, 0));
        }

        var game = payload!;

        // 🛡️ 2026-10-11（已删除游戏复活修复）：**落地前先判定本机是否已删除这同一款游戏**。
        //    断点：墓碑只按精确 global_id 匹配，而两台设备各自随机生成 global_id（manual 行的
        //    platform_id 也是本机随机）⇒ 旧 Create 既躲过调用方的墓碑检查、也躲过下面的业务键检查，
        //    直接 INSERT 成 'active' ⇒ 已删除的游戏复活。
        //    命中（墓碑 / global_id / (platform,platform_id) / notion_page_id / 同平台同名）时：
        //      · 绝不新建 active 行；
        //      · 强身份命中 ⇒ 固化墓碑（该 identity 以后在"墓碑优先"处被稳定挡下）；
        //      · 一律判 deletion_conflict **延后留痕**（沿用既有冲突机制：不猜、不丢、可人工裁定）。
        var deletion = CheckLocalGameDeletion(conn, tx, game.GlobalId, game.Platform, game.PlatformId,
            game.NotionPageId, game.Name);
        if (deletion.Deleted)
        {
            if (deletion.StrongIdentity)
            {
                AdoptGameDeletionTombstone(conn, tx, game.GlobalId, change.DeviceId, deletion.Evidence);
            }

            return (DeferredReasons.DeletionConflict, schemaVersion);
        }

        // 弱线索（同平台同名、platform_id 分歧）：**只发现冲突，不判删除**。
        //   · 不新建 active 行（否则已删除游戏会以"另一个身份"悄悄复活）；
        //   · 不归档任何本地行、不写墓碑（名称不是身份，绝不能凭名字动别的游戏）；
        //   · 记入延后台账等人工确认身份关系（沿用既有冲突机制：不猜、不丢）。
        if (deletion.HasWeakNameCandidate)
        {
            AppLog.Warn($"[设备同步] 远端 Game {game.GlobalId}（{game.Name}）与本机归档行 "
                + $"{string.Join(",", deletion.WeakNameCandidates)} 同平台同名但 platform_id 分歧"
                + $"（{deletion.Evidence}）⇒ 身份未建立关联，暂不落地、只记录冲突");
            return (DeferredReasons.PossibleDeletedGameIdentityConflict, schemaVersion);
        }

        // 同 identity —— 或**同一个身份等价类**里的任何一行 —— 已存在 ⇒ 跳过（重复 Pull 不产生重复实体）。
        //
        // 🛡️ 2026-10-11（身份协议修复）：身份关联建立后，对端仍会推它自己那套 identity 的 Create；
        //    只比对 `games.global_id = 远端 identity` 会在本机**再造一行副本**（实测：manual 同名游戏
        //    在两台设备上各留一行 ⇒ 删除只影响一行）。按等价类判断即可确定性挡住。
        var incomingClass = ReadIdentityClassCore(conn, tx, new[] { game.GlobalId });
        if (ReadLocalRowsInIdentityClassCore(conn, tx, incomingClass).Count > 0)
        {
            return (DeferredReasons.AlreadyExistsLocal, schemaVersion);
        }

        // 业务键已被另一个 identity 占用 → 需要身份对账（不是覆盖能解决的），本阶段不猜。
        //
        // ⚠️ 只认**正常（非归档）**行：
        //   · 归档行 = 用户已删除，已由上面的删除闸门处理（deletion_conflict），
        //     不能再报成"身份冲突"（那是另一个语义，会误导人工裁定）；
        //   · platform_id 为空/缺失时 `LOWER(NULL) = LOWER(x)` 恒不成立 ⇒ 旧写法的匹配会静默漏判，
        //     这里用 COALESCE 归一后显式要求非空。
        if (conn.ExecuteScalar<int>(
                $"""
                SELECT COUNT(*) FROM games
                WHERE LOWER(platform) = LOWER(@platform)
                  AND LOWER(COALESCE(platform_id, '')) = LOWER(COALESCE(@platformId, ''))
                  AND LOWER(COALESCE(platform_id, '')) <> ''
                  AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate};
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

        // v2：载荷带了封面/图标的内容 hash ⇒ 登记"待补齐"引用（本地已有该内容则直接就绪）。
        // ⚠️ 这一步**只写引用表**，图片下载由服务层在同步轮次之后尽力而为 ——
        //    图片缺失（404 / 无网络）绝不能影响 Game 事实已经落地这个结论。
        RegisterRemoteBlobRef(conn, tx, nameof(SyncEntityType.Game), game.GlobalId, BlobKinds.Cover, game.CoverBlobHash!);
        RegisterRemoteBlobRef(conn, tx, nameof(SyncEntityType.Game), game.GlobalId, BlobKinds.Icon, game.IconBlobHash!);

        return (null, schemaVersion);
    }

    /// <summary>
    /// 读取本机跨日结算小时（settings.daily_cutoff_hour，缺省/非法时 24）。
    /// 远端会话落地时必须按**本机**口径归属统计日，因此要在同一事务里读本机设置。
    /// </summary>
    // ⚠️ tx 允许为空：一次性历史迁移（DeviceSyncSchema migration 005）在**没有**事务的情况下
    //    也要读它，以保证"日时长调整"与"会话派生"用同一个结算口径。
    internal static int ReadDailyCutoffHour(SqliteConnection conn, SqliteTransaction? tx)
    {
        var raw = conn.ExecuteScalar<string?>(
            "SELECT value FROM settings WHERE key = 'daily_cutoff_hour';", transaction: tx);

        return int.TryParse(raw, out var value)
            ? GameTimeTracker.Core.Services.AccountingDateHelper.NormalizeCutoffHour(value)
            : 24;
    }

    /// <summary>把本库既定的本地墙上时间字符串（<c>yyyy-MM-dd HH:mm:ss</c>）解析为 <see cref="DateTime"/>。</summary>
    private static bool TryParseLocalWallClock(string text, out DateTime value)
        => DateTime.TryParseExact(
            text, "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out value);

    /// <summary>
    /// <b>入队前的依赖保证</b>（2026-10-11）：任何**依赖 Game** 的实体（Session / DailyAdjustment）
    /// 在入队之前，必须保证它依赖的 Game 也有 outbox 行，且 rowid 更小（= 推送顺序在前）。
    ///
    /// <para>
    /// 为什么必须有：对端落地 Session / DailyAdjustment 时按 <c>game_global_id</c> 找本地 Game，
    /// 找不到就 <c>dependency_missing</c>（延后，游标已越过）。若本机先推"依赖"、后推（甚至不推）"Game"，
    /// 对端就只能靠启动时的 Deferred 重处理补救 —— 实机事故就是"Notion 手工录入换机后同步不过去"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 与缺口修复 ② 的差别：② 要求"该 Game 至少有一条本机会话"（因为是"历史事实回补"），
    /// 这里**不要求**——承载导入事实的游戏（Notion 手工录入、没有本机会话）也必须能被对端解析到，
    /// 否则它上面的外部声明永远落不了地。判据只要求：有 global_id + 状态正常。
    /// 幂等：队列里任何状态已有该 Game ⇒ 直接返回。
    /// </para>
    /// </summary>
    private static async Task EnsureGameOutboxRowForDependencyAsync(
        SqliteConnection conn, SqliteTransaction tx, int gameId, string? gameGlobalId)
    {
        if (string.IsNullOrWhiteSpace(gameGlobalId)) return;

        if (await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sync_queue WHERE entity_type = @type AND entity_global_id = @gid;",
                new { type = nameof(SyncEntityType.Game), gid = gameGlobalId }, tx) > 0)
        {
            return;   // 任何状态都算"已入队"
        }

        var game = await conn.QuerySingleOrDefaultAsync<BackfillGameRow>(
            $"""
             SELECT games.id, games.global_id, games.name, games.platform, games.platform_id,
                    games.executable, games.executable_path, games.notion_page_id
             FROM games
             WHERE games.id = @gameId
               AND NULLIF(TRIM(games.global_id), '') IS NOT NULL
               AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate};
             """,
            new { gameId }, tx);

        if (game is null) return;   // 已删除 / 无身份 ⇒ 绝不凭空造 Game

        await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
        {
            QueueId = SyncHashing.BackfillQueueId(nameof(SyncEntityType.Game), game.GlobalId),
            EntityType = nameof(SyncEntityType.Game),
            EntityGlobalId = game.GlobalId,
            Operation = SyncOperation.Create,
            BaseVersion = 1,
            Payload = SyncPayloadFactory.Game(
                game.GlobalId, game.Name, game.Platform, game.PlatformId,
                game.Executable, game.ExecutablePath, game.NotionPageId,
                coverBlobHash: TryRegisterLocalCoverBlob(conn, tx, game.Platform, game.PlatformId)),
            CreatedAtUtc = UtcNowIso(),
            Status = SyncQueueStatus.Pending
        });
    }

    /// <summary>
    /// 按 <c>global_id</c> 解析本地 Game —— **并沿 2e 身份合并台账解析别名**。
    ///
    /// <para>
    /// 为什么必须有别名解析（2026-10-11 实机事故）：<c>games.global_id</c> 是**每台机器随机生成**的
    /// （<c>Guid.NewGuid()</c>）。两台机器各自拥有同一款游戏（同 platform + platform_id）时会得到
    /// **两个不同的 global_id**：对端落地 Game 时被判成 <c>business_key_taken_by_other_identity</c>（延后），
    /// 随后 2e 身份对账把对端 identity 记入 <c>sync_identity_supersessions</c>、
    /// **保留本机 global_id 为权威**（见 <c>ReconcileRemoteGameAsync</c>）。
    /// 若这里只查 <c>games.global_id = @gid</c>，对端载荷里带的"被并入的" global_id 就永远查不到 Game
    /// ⇒ Session / DailyAdjustment **永久 dependency_missing**（重启多少次都好不了），
    /// 实机表现正是"Notion 手工录入的记录换机后同步不过去"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 这里只做**别名解析**，不做任何身份决策：解析不到就返回 null，让调用方照旧
    /// <c>DependencyMissing</c>（绝不猜、绝不凭空造 Game）。
    /// </para>
    /// </summary>
    private static int? ResolveLocalGameIdByGlobalId(
        SqliteConnection conn, SqliteTransaction? tx, string gameGlobalId)
    {
        // 直接命中：本机这一行就是这个 identity（最常见路径，零额外查询）。
        var direct = conn.QuerySingleOrDefault<int?>(
            "SELECT id FROM games WHERE global_id = @gid LIMIT 1;",
            new { gid = gameGlobalId }, tx);

        if (direct is not null) return direct;

        if (string.IsNullOrWhiteSpace(gameGlobalId)) return null;

        // 🛡️ 2026-10-11（身份协议修复）：按**等价类**解析，而不是只看一跳 supersession。
        //
        //    根因：canonical 未必在本机存在（本机那一行的 identity 可能就是被并入的一方，
        //    canonical 那一行在对端）⇒ 旧实现（一跳 + 要求本机存在 canonical 行）解析失败，
        //    于是远端 Session / 声明 / 删除的落地全线失效。
        //
        //    现在：先在映射图上做**双向**闭包求等价类（顺带吃掉历史反向/环状映射），
        //    归一到类 canonical，再按「canonical 行 → 类内任意一行」回退定位本机实体。
        var classMembers = ReadIdentityClassCore(conn, tx, new[] { gameGlobalId });
        if (classMembers.Count == 0) return null;

        var canonical = GameIdentityRules.ElectCanonical(classMembers);
        if (canonical is not null)
        {
            var byCanonical = conn.QuerySingleOrDefault<int?>(
                "SELECT id FROM games WHERE global_id = @gid LIMIT 1;",
                new { gid = canonical }, tx);

            if (byCanonical is not null) return byCanonical;
        }

        return conn.QuerySingleOrDefault<int?>(
            "SELECT id FROM games WHERE global_id IN @members ORDER BY id ASC LIMIT 1;",
            new { members = classMembers }, tx);
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
        // ⚠️ 2026-10-11 事故修复：走**别名解析** —— 远端 game_global_id 可能已被 2e 身份对账
        //    并入本机权威 identity，只查 games.global_id 会永久解析不到。
        var gameId = ResolveLocalGameIdByGlobalId(conn, tx, session.GameGlobalId);

        if (gameId is null)
        {
            // E1（2026-10-07 用户批准）：区分「依赖还没到」与「Game 已经被删除」。
            // 后者说明来源设备在离线期间为**已删除的 Game** 继续产生了会话事实：
            // 删除事实与已发生的会话事实都要保留 ⇒ 归入 DeletionConflict（不复活、不丢、不改写），
            // 交由用户显式恢复处理；**不能**当作 DependencyMissing 反复重试（会被静默复活 ✗）。
            var gameTombstoned = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sync_tombstones WHERE entity_type = @type AND entity_global_id = @gid;",
                new { type = nameof(SyncEntityType.Game), gid = session.GameGlobalId }, tx) > 0;

            if (gameTombstoned)
            {
                return (DeferredReasons.DeletionConflict, schemaVersion);
            }

            return (DeferredReasons.DependencyMissing, schemaVersion);
        }

        // E1（2026-10-07 用户批准）：Game 已被删除（**历史归档**，C5 语义）时，
        // 这条远端会话属于「删除事实 vs 已发生的会话事实」的冲突 —— 不能静默挂到已删除的 Game 上，
        // 也不能丢弃/改写它。两侧事实都保留 ⇒ 记 DeletionConflict，等用户显式恢复。
        var gameStatus = conn.ExecuteScalar<string?>(
            "SELECT status FROM games WHERE id = @id;", new { id = gameId.Value }, tx);

        if (GameTimeTracker.Core.DeviceSync.GameStatuses.IsArchived(gameStatus))
        {
            return (DeferredReasons.DeletionConflict, schemaVersion);
        }

        // ⚠️ start_time / end_time / last_heartbeat 在本库有**既定约定**：写**本地墙上时间**
        //（心跳、结束会话、迁移写的都是它；所有 substr(start_time,1,10) 的按日分组都依赖它）。
        // 远端会话必须遵守同一约定，否则跨 UTC 日界时同一场游戏会在两台机器上落到**不同日期**，
        // 且既有的按日聚合与场次计数会算错。UTC 真相完好保留在 started_at_utc / ended_at_utc。
        var startTime = ToLocalWallClock(session.StartedAtUtc);
        var endTime = string.IsNullOrWhiteSpace(session.EndedAtUtc) ? null : ToLocalWallClock(session.EndedAtUtc);

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
                startTime,
                endTime,
                durationSeconds = session.DurationSeconds,
                globalId = session.GlobalId,
                deviceId = session.DeviceId,
                startedAtUtc = session.StartedAtUtc,
                endedAtUtc = session.EndedAtUtc,
                precision = session.TimePrecision
            },
            tx);

        // ⚠️⚠️ 关键（2026-10-05 用户拍板修复的缺陷）：远端会话**只落 sessions 是不够的**。
        // 界面（首页「最近记录」/ 历史 / 热力图 / 统计）全部读 daily_summary，
        // 而 daily_summary 的既有写入方只有"本机事件"。缺了这一步就会出现
        // 「同步成功、落地 62 条，但接收端界面上一条数据都看不到」——
        // 对用户来说就是"同步没意义"。
        //
        // 口径：daily_summary **不是同步实体**，只是 sessions 的**本地派生统计**
        //（仍不上传、不下载、不进 wire、不改 ISyncBackend/Engine）。这里逐字复用
        // 本机结束会话那条路径的既有函数，不新造聚合规则、也不做 sessions→daily_summary 全量重算
        //（全量重算会抹掉 Notion/手工录入的时长 ✗）。
        //
        // 幂等：只有**真正 INSERT 了新会话**才会走到这里（上面 global_id 已存在就提前 return）
        // → 重复 Pull 不会把时长翻倍。
        if (session.DurationSeconds > 0 && startTime.Length >= 10)
        {
            // ⚠️ 统计日归属必须与本机路径**完全一致**（2026-10-07 用户批准修复的 C2）。
            // 原实现用 startTime[..10]（本地**日历日**）把整段时长塞进一天，而本机
            // 「心跳 + 结束会话」路径走的是 GameSessionManager.ComputeDailyDeltas →
            // AccountingDateHelper（按 settings.daily_cutoff_hour 结算，跨分界点还会拆成两笔）。
            // 两者口径不同 ⇒ cutoff ≠ 24 时，同一条 Session 在本机记 A 日、远端落地记 B 日 ✗。
            //
            // 修法：**逐字复用同一个纯函数**（不新造规则、不改跨日算法、不改 immutable fact）。
            // 远端会话此前没有任何心跳落库，所以整段区间就是 [start, start + duration]，
            // 与本机 heartbeat+end 两段拼起来的归属结果一致。
            if (TryParseLocalWallClock(startTime, out var startLocal))
            {
                var cutoffHour = ReadDailyCutoffHour(conn, tx);

                foreach (var delta in GameTimeTracker.Core.Services.GameSessionManager.ComputeDailyDeltas(
                             startLocal,
                             startLocal.AddSeconds(session.DurationSeconds),
                             session.DurationSeconds,
                             cutoffHour))
                {
                    // ⚠️ 2026-10-11（乱序保护）：该分片可能已被本机/对端显式删除
                    //    （排除事实先于会话到达）⇒ 绝不把它累加回 daily_summary，否则删除被复活。
                    if (IsSessionSliceExcluded(conn, tx, session.GlobalId, delta.Date)) continue;

                    ApplyDailyDurationDelta(conn, tx, delta.Date, gameId.Value, delta.DurationSeconds);
                }
            }
            else
            {
                // 防御性回退：墙上时间不可解析时维持旧口径（日历日），绝不因此丢时长。
                if (!IsSessionSliceExcluded(conn, tx, session.GlobalId, startTime[..10]))
                {
                    ApplyDailyDurationDelta(conn, tx, startTime[..10], gameId.Value, session.DurationSeconds);
                }
            }

            var newSessionId = conn.ExecuteScalar<int>("SELECT last_insert_rowid();", transaction: tx);
            RecomputeSessionCount(conn, tx, newSessionId, session.DurationSeconds);
        }

        return (null, schemaVersion);
    }

    /// <summary>
    /// 把 payload 里的 UTC 时刻换算成**本地墙上时间**（<c>yyyy-MM-dd HH:mm:ss</c>），
    /// 供 <c>sessions.start_time/end_time/last_heartbeat</c> 使用。
    ///
    /// 理由见调用处：这三列在本库是**本地墙上时间**，混入 UTC 会让"同一场游戏"
    /// 在两台机器上落到不同日期。解析失败时原样返回（宁可显示怪，也不丢数据）。
    /// </summary>
    private static string ToLocalWallClock(string? isoUtc)
    {
        if (string.IsNullOrWhiteSpace(isoUtc)) return string.Empty;

        return DateTimeOffset.TryParse(
            isoUtc,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            : isoUtc;
    }

    /// <summary>
    /// 解析失败时区分"版本不认识"与"payload 坏掉"——两者的后续处理方式不同。
    /// ⚠️ v2 起必须用 <see cref="DeviceSyncConstants.IsSupportedSchemaVersion"/> 判断：
    ///    仍被支持的 v1 载荷若解不开，那是 payload 本身坏了（InvalidPayload），
    ///    而不是"版本不认识"（否则会把正常混版本场景误记成版本问题）。
    /// </summary>
    private static string ClassifyParseFailure(int schemaVersion)
        => schemaVersion >= 0 && !DeviceSyncConstants.IsSupportedSchemaVersion(schemaVersion)
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

            // 归档（用户已删除）的行不参与身份对账候选 —— 它们的存在只为历史 FK。
            // 复用"已删除实体"的墓碑优先规则拦陈旧 Create，因此排除后不会导致 Game 复活。
            var allGames = (await conn.QueryAsync<GameReconcileRow>(
                $"SELECT * FROM games WHERE {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate};",
                null, tx)).ToList();

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

            // ② 记录"这两套 identity 是同一个实体"，**绝不写 deletion tombstone**。
            //
            // 🛡️ 2026-10-11（身份协议根因修复）：旧实现写的是"**本地保留自己的 identity**、
            //    给远端 identity 记一条指向本地的映射"。两台设备各自对账就会写出相反映射
            //    （A: gB→gA、B: gA→gB），传播后即成环 ⇒ 解析/删除口径两端不一致。
            //    现在统一走协议入口：canonical = **等价类内最小的 global_id**（纯函数，
            //    两台设备独立复算必然一致），并把真正发生变化的映射**入队传播**。
            var linkOutcome = EstablishIdentityLinkCore(
                conn, tx, new[] { canonicalGlobalId, remoteChange.EntityGlobalId },
                ReasonForLevel(decision.Level));

            EnqueueIdentityMappingsCore(conn, tx, linkOutcome.MappingsChanged, ReasonForLevel(decision.Level));

            tx.Commit();

            return new GameReconcileOutcome(
                inherited ? GameReconcileResult.IdentityContinued : GameReconcileResult.Merged,
                canonical.Id, linkOutcome.CanonicalGlobalId, decision.Level, ReasonForLevel(decision.Level));
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

        // 🔧 2026-10-11（A/B 实机对差根因修复）：`daily_duration_adjustments` **没有外键**
        //    （它只声明 `game_id INTEGER NOT NULL`）⇒ 合并时若忘了搬它，被删掉的 dup 行
        //    会留下**孤儿声明**（实测：删掉 game 165 后留下 1 条 0 秒声明），
        //    而孤儿声明既不进 stats、也不参与同步，只能靠人工发现。
        //    与 sessions / daily_summary 同处一个事务，搬家是幂等的纯 UPDATE（主键是 adjustment_global_id）。
        conn.Execute("UPDATE daily_duration_adjustments SET game_id = @canonicalId WHERE game_id = @dupId;",
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
                string? finalPageId = !string.IsNullOrEmpty(targetRow.NotionPageId) ? targetRow.NotionPageId : ds.NotionPageId;
                string? finalTitle = !string.IsNullOrEmpty(targetRow.NotionTitle) ? targetRow.NotionTitle : ds.NotionTitle;
                string? finalIcon = !string.IsNullOrEmpty(targetRow.NotionIconUrl) ? targetRow.NotionIconUrl : ds.NotionIconUrl;
                string finalStatus = (targetRow.SyncStatus == "synced" && ds.SyncStatus == "synced") ? "synced" : "pending";

                conn.Execute(
                    """
                    UPDATE daily_summary
                    SET duration_seconds = @combinedSecs,
                        duration_minutes = @combinedMins,
                        notion_page_id = @finalPageId,
                        notion_title = @finalTitle,
                        notion_icon_url = @finalIcon,
                        sync_status = @finalStatus
                    WHERE id = @targetId;

                    DELETE FROM daily_summary WHERE id = @dupDailyId;
                    """,
                    new { combinedSecs, combinedMins, finalPageId, finalTitle, finalIcon, finalStatus, targetId = targetRow.Id, dupDailyId = ds.Id },
                    tx);
            }
            else
            {
                conn.Execute("UPDATE daily_summary SET game_id = @canonicalId WHERE id = @id;",
                    new { canonicalId = canonicalGameId, id = ds.Id }, tx);
            }

            // 🔒 2026-10-11（口径统一，count-rule-v1）：合并**不再把两侧场次相加**
            //    （相加会把同一场游玩重复计次）。合并完秒数后，按 canonical 自己的事实重算场次。
            ApplyUnifiedSessionCount(conn, tx, canonicalGameId, ds.Date, ReadDailyCutoffHour(conn, tx));
        }

        // 🛡️ 2026-10-14（孤儿会话收口）：搬家完成后**必须**确认旧 id 已无任何事实引用 ——
        //    调用方在同一事务里紧接着 `DELETE FROM games`：若这里仍有残留却放行，
        //    sessions / daily_summary 会被 FK **级联静默删掉**（历史丢失），
        //    adjustments（无外键）则会变成谁也认领不了的孤儿。
        //    残留 ⇒ 抛异常 ⇒ 调用方 `using tx` 回滚整个合并，本地数据保持原样（可安全重试）。
        EnsureNoGameRefsOrThrow(conn, tx, duplicateGameId, "身份合并");
    }

    /// <summary>
    /// 物理删除 <c>games</c> 行**之前**的最后一道守卫：三张事实表都不得再引用该 id。
    /// 有残留 ⇒ 抛异常（由调用方事务回滚），绝不静默删行。
    /// <c>internal</c> 以便测试直接验证"发现残留必须阻止删除"。
    /// </summary>
    internal static void EnsureNoGameRefsOrThrow(SqliteConnection conn, SqliteTransaction? tx, int gameId, string context)
    {
        var daily = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM daily_summary WHERE game_id = @id;", new { id = gameId }, tx);
        var sessions = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sessions WHERE game_id = @id;", new { id = gameId }, tx);
        var adjustments = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM daily_duration_adjustments WHERE game_id = @id;", new { id = gameId }, tx);
        if (daily > 0 || sessions > 0 || adjustments > 0)
        {
            throw new InvalidOperationException(
                $"[{context}] 拒绝物理删除 games 行 id={gameId}：仍被事实引用"
                + $"（daily_summary={daily}、sessions={sessions}、daily_duration_adjustments={adjustments}）"
                + "；删除会留下悬挂引用或级联抹掉历史，请先迁移/清理这些事实");
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
    /// <summary>
    /// Deferred 重处理**可处理实体类型**的**唯一真相**（2026-10-11 提取）。
    ///
    /// <para>
    /// ⚠️ 以下三处逻辑必须使用同一份列表，任何新增同步实体**只改这里**：
    /// <list type="number">
    ///   <item>调度（分类型配额 + 剩余名额共享 + 优先排序）；</item>
    ///   <item><c>unsupported</c> / <c>UnsupportedPending</c> 分类；</item>
    ///   <item>类型分派（switch 仍是显式映射，新增类型时必须与本列表同时更新 ——
    ///         这是编译期穷尽性的代价，已由本常量 + 契约测试兜住）。</item>
    /// </list>
    /// 此前这三处各自内联了一份字面量列表，是"必然漂移"的写法。
    /// </para>
    /// </summary>
    private static readonly string[] ReconcilableEntityTypes =
    {
        nameof(SyncEntityType.Game),
        nameof(SyncEntityType.Session),
        nameof(SyncEntityType.DailyAdjustment),
        nameof(SyncEntityType.DailyPlayExclusion)
    };

    /// <summary>
    /// <b>「本机重新激活意图优先」</b>的 SQL 条件（2026-10-12）：远端 Game Delete **不得归档**
    /// 那些"在本次删除发生之后又被本机**真正重新激活**过"的行 —— 典型场景是用户删了游戏、
    /// 之后又重新启动它（<c>GetOrCreateGameAsync</c> 复用同一身份并重新激活）。
    ///
    /// <para>
    /// ⚠️ <b>判据必须是 <c>games.reactivated_at_utc</c>（migration 016），不能是 <c>updated_at</c>。</b>
    /// 历史缺陷：<c>updated_at</c> 会被监听循环每 5 秒刷新，导致**只要游戏在运行**
    /// 就永久拒绝一切远端 Delete（而该 Delete 的游标已越过、不会重发）⇒ 两端长期分歧。
    /// <c>reactivated_at_utc</c> 只在真实 archived→active 转换时写入、本机删除时清空、
    /// 后台心跳绝不触碰，因此是稳定且单调的"用户意图"。
    /// </para>
    ///
    /// <para>
    /// 时间统一用 <c>julianday()</c> 归一化后比较：两侧都可能是 ISO8601 带 <c>Z</c>
    ///（<c>reactivated_at_utc</c> / <c>created_at_utc</c>），也可能来自 <c>CURRENT_TIMESTAMP</c>
    /// （<c>YYYY-MM-DD HH:MM:SS</c>）—— 直接做字符串比较会失效。带毫秒的输入同样被正确解析。
    /// 归一化到**秒**精度：同一秒内的两个事件视为"不更晚"（保守，倾向允许归档）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <c>@deletionAtUtc</c> 为 NULL / 空串时条件恒真 ⇒ 保持既有行为（无条件归档）：
    /// 绝不因为"缺少时间信息"就把删除语义削弱。
    /// ⚠️ <c>reactivated_at_utc</c> 为 NULL（从未重新激活 / 旧库升级后）⇒ 按 <c>1970-01-01</c> 处理
    /// ⇒ 同样等价于既有行为，旧库行为逐字不变。
    /// ⚠️ 本条件**只影响归档方向**：墓碑照常写入，因此"远端旧 Create 复活"仍然被挡。
    /// </para>
    /// </summary>
    private const string LocalIntentWinsSql =
        " AND (@deletionAtUtc IS NULL OR @deletionAtUtc = '' "
        + "      OR julianday(COALESCE(reactivated_at_utc, '1970-01-01 00:00:00')) <= julianday(@deletionAtUtc))";

    public async Task<ReconciliationSummary> ReconcileDeferredChangesAsync(
        int batchLimit = SyncOutboxPolicy.ReconcileBatchSize,
        CancellationToken cancellationToken = default)
        => await ReconcileDeferredChangesCoreAsync(batchLimit, cancellationToken, depth: 0);

    /// <summary>
    /// <see cref="ReconcileDeferredChangesAsync"/> 的实际实现（带**有界**收敛轮次）。
    ///
    /// ⚠️ 2026-10-11：同一轮最多再收敛 2 次（<paramref name="depth"/> 限制，绝不无限自旋；
    /// 没有任何进展立刻停手）。理由：一条 Deferred 的依赖常常在同一次调用内**刚刚**就绪 ——
    /// 例如对端 Game 的身份在本轮被 2e 并入本机权威 id（<c>sync_identity_supersessions</c>），
    /// 而引用它的 DailyAdjustment 台账行 id 更小、已经先被处理过 ⇒ 单遍扫描只能等"下一次启动"。
    /// 语义仍是"仅启动触发"，只是让一次启动就收敛完。
    /// </summary>
    private async Task<ReconciliationSummary> ReconcileDeferredChangesCoreAsync(
        int batchLimit, CancellationToken cancellationToken, int depth)
    {
        var pending = new List<DeferredSyncChange>();
        var unsupportedPendingByType = new Dictionary<string, int>(StringComparer.Ordinal);
        int unsupportedPending;

        // ⚠️ 公平调度（2026-10-11 第二轮）：**分类型配额 + 剩余名额共享**（有界，不新增迁移）。
        //
        // 背景：只按 id 升序取前 N 条时，"同一类型里依赖永久缺失的老记录"会把批次占满
        // （ReconcileOneDeferredSessionAsync 在依赖缺失时返回 null ⇒ 行保持 unresolved），
        // 把**其他类型**里可恢复的记录反复挤出扫描窗口。
        //
        // 方案（三步，全程有界 ≤ batchLimit）：
        //   ① 每个可处理类型各取一份**基础配额**（batchLimit / 类型数，至少 1）⇒ 类型间公平；
        //   ② 配额没用满（某类型候选不足）⇒ **补足查询**把剩余名额按原优先级（可处理类型优先、
        //      再 id 升序）发出去 ⇒ 绝不浪费容量；
        //   ③ 未知实体 / 身份复核项只在补足阶段填充、**只计数、绝不清理**（可能来自未来版本）。
        using (var conn = CreateConnection())
        {
            var quota = Math.Max(1, batchLimit / ReconcilableEntityTypes.Length);
            var selected = new List<DeferredSyncChange>();
            var selectedIds = new HashSet<long>();

            // ① 分类型基础配额
            //    ⚠️ 严格不超上限：batchLimit 小于类型数时（例如 2 < 4），"每类至少一次"不可能同时成立，
            //       此时按列表顺序分配剩余名额，**优先保证上限不被突破**。
            foreach (var entityType in ReconcilableEntityTypes)
            {
                var take = Math.Min(quota, batchLimit - selected.Count);
                if (take <= 0) break;

                var rows = await conn.QueryAsync<DeferredSyncChange>(
                    """
                    SELECT * FROM sync_deferred_changes
                    WHERE resolved_at_utc IS NULL AND entity_type = @entityType
                    ORDER BY id ASC
                    LIMIT @take;
                    """,
                    new { entityType, take });

                foreach (var row in rows)
                {
                    if (selectedIds.Add(row.Id)) selected.Add(row);
                }
            }

            // ② 剩余名额共享（候选不足时补足；已选中的用 NOT IN 排除）
            if (selected.Count < batchLimit)
            {
                var remaining = batchLimit - selected.Count;

                var rows = selectedIds.Count == 0
                    ? await conn.QueryAsync<DeferredSyncChange>(
                        """
                        SELECT * FROM sync_deferred_changes
                        WHERE resolved_at_utc IS NULL
                        ORDER BY CASE WHEN entity_type IN @types THEN 0 ELSE 1 END, id ASC
                        LIMIT @limit;
                        """,
                        new { types = ReconcilableEntityTypes, limit = remaining })
                    : await conn.QueryAsync<DeferredSyncChange>(
                        """
                        SELECT * FROM sync_deferred_changes
                        WHERE resolved_at_utc IS NULL AND id NOT IN @ids
                        ORDER BY CASE WHEN entity_type IN @types THEN 0 ELSE 1 END, id ASC
                        LIMIT @limit;
                        """,
                        new { ids = selectedIds, types = ReconcilableEntityTypes, limit = remaining });

                foreach (var row in rows)
                {
                    if (selectedIds.Add(row.Id)) selected.Add(row);
                }
            }

            // 处理顺序按 id 升序：结果与顺序无关，但可预测、便于诊断。
            pending = selected.OrderBy(r => r.Id).ToList();

            // ③ 可观测性：仍未解决的"非重处理类型"**按类型**计数
            //    （总数由分类计数求和得到 —— 单一来源，避免两次查询不一致）。
            //    ⚠️ 不做清理、不做放弃：它们可能是未来版本实体 / 本版本尚未实现的实体 / 身份复核项。
            var byType = await conn.QueryAsync<DeferredTypeCountRow>(
                """
                SELECT entity_type AS EntityType, COUNT(*) AS Count
                FROM sync_deferred_changes
                WHERE resolved_at_utc IS NULL AND entity_type NOT IN @types
                GROUP BY entity_type
                ORDER BY COUNT(*) DESC, entity_type ASC;
                """,
                new { types = ReconcilableEntityTypes });

            foreach (var row in byType)
            {
                if (string.IsNullOrWhiteSpace(row.EntityType)) continue;
                unsupportedPendingByType[row.EntityType] = row.Count;
            }

            unsupportedPending = unsupportedPendingByType.Values.Sum();
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
                // v2：日时长调整的依赖是 Game —— Game 晚到后必须能用台账 payload 正常落地（§4）。
                nameof(SyncEntityType.DailyAdjustment) => await ReconcileOneDeferredDailyAdjustmentAsync(row, cancellationToken),
                // 2026-10-11：**每日游玩分片排除事实**。
                // 这是"旧版本设备已经收到、当时不认识"的删除事实的**唯一恢复路径**：
                // 游标早已越过它，Pull 不会再给第二次 ⇒ 只能靠台账 payload 快照重放。
                nameof(SyncEntityType.DailyPlayExclusion) => await ReconcileOneDeferredDailyPlayExclusionAsync(row, cancellationToken),
                _ => null   // GameMapping / 未知类型：2e 不覆盖 → 保持 unresolved
            };

            if (resolution is null)
            {
                if (!ReconcilableEntityTypes.Contains(row.EntityType, StringComparer.Ordinal))
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

        if (resolved > 0 || stillDeferred > 0 || skippedTombstoned > 0 || noPayload > 0 || unsupportedPending > 0)
        {
            var byTypeText = unsupportedPendingByType.Count == 0
                ? "无"
                : string.Join("、", unsupportedPendingByType.Select(kv => $"{kv.Key}×{kv.Value}"));

            AppLog.Info($"[设备同步] Deferred 重处理：扫描 {pending.Count}、已解决 {resolved}、"
                + $"仍待处理 {stillDeferred}、跳过（本地已墓碑）{skippedTombstoned}、"
                + $"无 payload {noPayload}、本轮不支持的类型 {unsupported}、"
                + $"台账中未解决的非重处理类型 {unsupportedPending} 条（{byTypeText}）"
                + " —— 这**不代表**同步失败或数据损坏：它们可能是未来版本实体、"
                + "本版本尚未实现的实体，或身份复核流程；台账与 payload 全部原样保留");
        }

        var summary = new ReconciliationSummary(
            pending.Count, resolved, stillDeferred, skippedTombstoned, noPayload, unsupported,
            unsupportedPending, unsupportedPendingByType, resolutions);

        // 依赖可能在本轮**刚刚**就绪（典型：对端 Game 身份刚被并入本机权威 id）⇒ 再收敛一遍。
        // ⚠️ 有界：只有"本轮确实解决了一些、且仍有待处理"才重跑，最多 2 次额外；无进展立即停手。
        if (summary.StillDeferred > 0 && summary.Resolved > 0 && depth < 2)
        {
            var again = await ReconcileDeferredChangesCoreAsync(batchLimit, cancellationToken, depth + 1);

            return summary with
            {
                Scanned = summary.Scanned + again.Scanned,
                Resolved = summary.Resolved + again.Resolved,
                StillDeferred = again.StillDeferred,
                SkippedTombstoned = summary.SkippedTombstoned + again.SkippedTombstoned,
                NoPayload = summary.NoPayload + again.NoPayload,
                UnsupportedType = summary.UnsupportedType + again.UnsupportedType,
                // 台账总数在同一轮内不变 ⇒ 取最新一轮的读数即可（不累加）。
                UnsupportedPending = again.UnsupportedPending,
                UnsupportedPendingByType = again.UnsupportedPendingByType,
                Resolutions = summary.Resolutions.Concat(again.Resolutions).ToList()
            };
        }

        if (summary.StillDeferred > 0)
        {
            await LogUnresolvedDeferredAsync(cancellationToken);
        }

        return summary;
    }

    /// <summary>
    /// 把**仍未解决**的 Deferred 逐条打出来（类型 / identity / 原因 / 来源设备 / 首次出现 + payload 片段）。
    ///
    /// ⚠️ 纯诊断：不改变任何状态。实机日志此前只有一行汇总（"Deferred N 条：reason"），
    /// 无法回答"到底哪几条、缺什么依赖"—— 这是用户 2026-10-11 明确要求补齐的可诊断性。
    /// </summary>
    private async Task LogUnresolvedDeferredAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var conn = CreateConnection();
            var rows = (await conn.QueryAsync<DeferredSyncChange>(
                """
                SELECT * FROM sync_deferred_changes
                WHERE resolved_at_utc IS NULL
                ORDER BY id ASC
                LIMIT 10;
                """)).ToList();

            foreach (var row in rows)
            {
                AppLog.Warn($"[设备同步] 仍未解决：{row.EntityType} {row.EntityGlobalId}"
                    + $" reason={row.Reason} operation={row.Operation} 来源设备={row.DeviceId}"
                    + $" 首次={row.FirstSeenAtUtc} payload={TrimForLog(row.Payload, 160)}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 打印未解决 Deferred 明细失败（不影响同步）：{ex.GetType().Name}");
        }

        static string TrimForLog(string? text, int max)
            => string.IsNullOrEmpty(text) ? "<无>" : (text.Length <= max ? text : text[..max] + "…");
    }

    /// <summary>Deferred 台账"未解决的非重处理类型"按类型计数（可观测性，不参与业务判断）。</summary>
    private sealed class DeferredTypeCountRow
    {
        public string EntityType { get; set; } = "";
        public int Count { get; set; }
    }

    /// <summary>
    /// Deferred 重处理：<b>日时长调整</b>（§4）。只使用台账里保存的 payload；
    /// 依赖（Game）已就绪才落地，否则继续保持 unresolved（绝不丢 payload，也绝不假装成功）。
    /// </summary>
    private async Task<string?> ReconcileOneDeferredDailyAdjustmentAsync(
        DeferredSyncChange row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var change = BuildChangeFromLedger(row);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var (reason, _) = ApplyRemoteDailyAdjustment(conn, tx, change);

            if (reason is not null)
            {
                tx.Rollback();
                return null;   // 依赖仍未就绪 / 删除冲突未解决 ⇒ 保持 unresolved
            }

            tx.Commit();
            return "applied";
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Deferred 重处理：<b>每日游玩分片排除事实</b>（2026-10-11）。
    ///
    /// <para>
    /// 这是"旧版本设备**曾经收到、但当时不认识**"的删除事实的**唯一恢复路径**：
    /// 那个版本的 Pull 游标早已越过它（deferred 与游标在同一事务提交），Pull 不会再给第二次；
    /// 只能靠台账里保存的 **payload 快照**原样重放。
    /// </para>
    ///
    /// <para>
    /// 规则与 2c 完全一致（<see cref="ApplyRemoteDailyPlayExclusion"/> 一字未改）：
    /// 依赖（Game identity）就绪 ⇒ 落地并按剩余事实重算该会计日；否则保持 unresolved
    /// （绝不丢 payload、绝不假装成功、绝不把"没落地"写成 resolved）。
    /// </para>
    /// </summary>
    private async Task<string?> ReconcileOneDeferredDailyPlayExclusionAsync(
        DeferredSyncChange row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var change = BuildChangeFromLedger(row);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var (reason, _) = ApplyRemoteDailyPlayExclusion(conn, tx, change);

            if (reason is not null)
            {
                tx.Rollback();
                return null;   // 依赖仍未就绪 / 目标游戏尚未落地 ⇒ 保持 unresolved
            }

            tx.Commit();
            return "applied";
        }
        finally
        {
            _writeLock.Release();
        }
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
            && await IsDeferredSessionRetryableAsync(row))
        {
            // 当初因为"依赖的游戏还没到"或"游戏已被删除（archived）"没落地；现在可能已就绪。
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
    /// 这条 Deferred Session 台账现在**是否值得再试一次**（2026-10-12）。
    ///
    /// <para>
    /// · <c>dependency_missing</c>：始终可重试（依赖的游戏可能已晚到）—— 与既有行为一致；
    /// · <c>deletion_conflict</c>：**仅当本机该游戏已不再是 archived** 时才重试。
    ///   场景：用户删过这款游戏（本机 archived），对端的事实被延后；之后用户又**重新启动**了它
    ///   （<c>GetOrCreateGameAsync</c> 复用同一身份并重新激活）⇒ 本机状态已经变化，
    ///   这条台账可以安全地重新评估。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 刻意**不**把 deletion_conflict 一律改成自动重试：那会让"已删除"语义失效，造成复活。
    /// ⚠️ 仍然删除中的条目保持 unresolved —— 不无限重试、不静默丢弃，原因与 payload 快照全部保留。
    /// </para>
    /// </summary>
    private async Task<bool> IsDeferredSessionRetryableAsync(DeferredSyncChange row)
    {
        if (string.Equals(row.Reason, DeferredReasons.DependencyMissing, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(row.Reason, DeferredReasons.DeletionConflict, StringComparison.Ordinal))
        {
            return false;
        }

        var change = BuildChangeFromLedger(row);
        if (!SyncPayloads.TryParseSession(change.Payload, out var payload, out _, out _) || payload is null)
        {
            return false;
        }

        using var conn = CreateConnection();
        var stillArchived = conn.ExecuteScalar<int>(
            $"""
            SELECT COUNT(*) FROM games
            WHERE global_id = @gid
              AND LOWER(COALESCE(status, '')) = @archived;
            """,
            new
            {
                gid = payload.GameGlobalId,
                archived = GameTimeTracker.Core.DeviceSync.GameStatuses.Archived
            }) > 0;

        return !stillArchived;
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
            // 🛡️ 2026-10-15（用户裁定 · 方案 1）：**删除类墓碑永久保留**（零协议改动、无 schema 变更）。
            //    「用户删除了记录，不能因为某台设备离线很久而重新出现」——当前协议**没有**记录"对端已消费"
            //    的证据（对端水位只说明我们见过它，不说明它取走过墓碑；墓碑本身也不参与传输、是本地防线），
            //    所以拿"30 天年龄"清理会造成：离线超过 30 天的设备上线后把已删数据当新数据重新上传。
            //    本方法**精准限定**为：只清理"非删除类"墓碑 —— 删除类按 entity_type 明确排除在白名单外，
            //    绝不误伤；写墓碑的全部调用点（Game / Session / DailyAdjustment / DailyPlayExclusion …）都在白名单里。
            //    ⚠️ 其它清理逻辑（尤其已完成 Outbox 的 7 天策略 PruneCompletedOutboxAsync）**保持不变**。
            using var conn = CreateConnection();
            return await conn.ExecuteAsync(
                """
                DELETE FROM sync_tombstones
                WHERE deleted_at_utc < @cutoff
                  AND entity_type IN (
                      ''
                  );
                """,
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
    private static void WriteGameTombstone(
        SqliteConnection conn, SqliteTransaction? tx, string gameGlobalId, string? deletedAtUtc = null)
    {
        var deviceId = conn.QuerySingleOrDefault<string>(
            "SELECT value FROM settings WHERE key = @k;",
            new { k = DeviceSyncConstants.SettingKeyDeviceId }, tx) ?? string.Empty;

        // ⚠️ 2026-10-12：允许调用方传入删除时刻 —— 它必须与该次删除的 Delete 队列行的信封时间**完全一致**，
        //    否则接收端写下的 deleted_at_utc 与"撤销判定"用的时间会错位。
        var now = string.IsNullOrWhiteSpace(deletedAtUtc) ? UtcNowIso() : deletedAtUtc!.Trim();

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

    /// <summary>
    /// 入队一条 Game <c>Create</c>，表达「本机**重新启用了**这个已删除的游戏」（2026-10-12）。
    ///
    /// <para>
    /// <b>为什么必须入队</b>：用户删了游戏、之后又在本机重新启动它（<c>GetOrCreateGameAsync</c> 复用同一
    /// 身份并重新激活）—— 在此之前这只改本机状态、**不发任何变更**，于是对端永远停在"已删除"：
    /// 实测症状是对端既看不到它恢复，也**收不到本机之后产生的会话**（对端落 <c>deletion_conflict</c>
    /// 且永久挂起）。
    /// </para>
    ///
    /// <para>
    /// <b>接收端如何解释它</b>：本 Create 的信封时间（<c>CreatedAtUtc</c>）**晚于**对端墓碑的
    /// <c>deleted_at_utc</c> ⇒ 对端按"删除被撤销"处理（清墓碑 + 取消归档，见
    /// <c>ApplyRemoteChangesWithCursorAsync</c> 的墓碑分支）；旧于墓碑的 Create 照旧被挡。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <c>queue_id</c> 必须**每次转换唯一**（这里带一个随机后缀）：每次真实转换都是新事件，
    /// 各发一条才能让对端每次都收到"撤销"。
    /// <b>不能用确定性 id</b>：本方法所在的 archived→active 分支每次转换只可能走到一次
    /// （转换后该行不再是 archived，分支不可达），所以不需要靠 id 做幂等；
    /// 而确定性 id（含秒精度时间戳）会让**同一秒内的两次转换静默合并成一条** ——
    /// 场景：重新启用(t) → 再次删除(t) → 再次重新启用(t) 时，第三次会被去重丢掉，对端永远停在已删除。
    /// </para>
    /// </summary>
    internal static async Task<bool> EnqueueGameReactivationCoreAsync(
        SqliteConnection conn, SqliteTransaction? tx, GameRecord game, string reactivatedAt)
    {
        var identity = (game.GlobalId ?? string.Empty).Trim();
        if (identity.Length == 0) return false;   // 无跨设备身份 ⇒ 无从传播（本机也没有身份可分派）

        var queueId = $"reactivate:{identity}:{Guid.NewGuid():N}";

        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_queue WHERE queue_id = @id;", new { id = queueId }, tx) > 0;
        if (exists) return false;

        await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
        {
            QueueId = queueId,
            EntityType = nameof(SyncEntityType.Game),
            EntityGlobalId = identity,
            Operation = SyncOperation.Create,
            BaseVersion = 1,
            // 载荷沿用与 Create/Delete 相同的"最小身份快照"（形状不变、schema_version 不变）。
            Payload = SyncPayloadFactory.Game(
                identity, game.Name ?? string.Empty, game.Platform ?? string.Empty,
                game.PlatformId ?? string.Empty, game.Executable ?? string.Empty,
                game.ExecutablePath ?? string.Empty, game.NotionPageId),
            CreatedAtUtc = reactivatedAt,
            Status = SyncQueueStatus.Pending
        });

        return true;
    }

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
    // 台账里**仍未解决**的"非重处理类型"总数（GameMapping / 身份复核项 / 未来版本实体）。
    // 纯可观测性：这些行**只保留、不清理**（它们可能来自未来版本，删掉就是永久丢数据）。
    // ⚠️ 它**不代表**同步失败或数据损坏。
    int UnsupportedPending,
    // 同一个数字的**按类型**分类（类型 → 未解决条数，降序）；总数 = 各项之和。
    IReadOnlyDictionary<string, int> UnsupportedPendingByType,
    IReadOnlyList<string> Resolutions);
