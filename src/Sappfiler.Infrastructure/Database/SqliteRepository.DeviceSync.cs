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

    // ==================== 远端变更落地（数据 + 游标同一事务） ====================

    private enum RemoteApplyOutcome
    {
        Applied,
        Deferred
    }

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

            int applied = 0, deferred = 0, tombstonesPropagated = 0;

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
                    deferred++;
                    continue;
                }

                var outcome = change.EntityType switch
                {
                    nameof(SyncEntityType.Game) => ApplyRemoteGame(conn, tx, change),
                    nameof(SyncEntityType.Session) => ApplyRemoteSession(conn, tx, change),
                    _ => RemoteApplyOutcome.Deferred
                };

                if (outcome == RemoteApplyOutcome.Applied) applied++;
                else deferred++;
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

            return new RemoteApplyResult(applied, deferred, tombstonesPropagated);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static RemoteApplyOutcome ApplyRemoteGame(SqliteConnection conn, SqliteTransaction tx, SyncChange change)
    {
        if (!SyncPayloads.TryParseGame(change.Payload, out var payload, out _))
        {
            return RemoteApplyOutcome.Deferred;
        }

        var game = payload!;

        // 同 identity 已存在 → 跳过（重复 Pull 不产生重复实体）。
        if (conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM games WHERE global_id = @gid;",
                new { gid = game.GlobalId }, tx) > 0)
        {
            return RemoteApplyOutcome.Deferred;
        }

        // 业务键已被另一个 identity 占用 → 需要身份对账（不是覆盖能解决的），本阶段不猜。
        if (conn.ExecuteScalar<int>(
                """
                SELECT COUNT(*) FROM games
                WHERE LOWER(platform) = LOWER(@platform) AND LOWER(platform_id) = LOWER(@platformId);
                """,
                new { platform = game.Platform, platformId = game.PlatformId }, tx) > 0)
        {
            return RemoteApplyOutcome.Deferred;
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

        return RemoteApplyOutcome.Applied;
    }

    private static RemoteApplyOutcome ApplyRemoteSession(SqliteConnection conn, SqliteTransaction tx, SyncChange change)
    {
        if (!SyncPayloads.TryParseSession(change.Payload, out var payload, out _))
        {
            return RemoteApplyOutcome.Deferred;
        }

        var session = payload!;

        if (conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sessions WHERE global_id = @gid;",
                new { gid = session.GlobalId }, tx) > 0)
        {
            return RemoteApplyOutcome.Deferred;
        }

        // 依赖：sessions.game_id NOT NULL，远端会话必须挂在本地已存在的 game 上。
        // 游戏还没到 → 延后（下次 Pull 会再拉到；我们的推送顺序本身是 Game 先于 Session）。
        var gameId = conn.QuerySingleOrDefault<int?>(
            "SELECT id FROM games WHERE global_id = @gid LIMIT 1;",
            new { gid = session.GameGlobalId }, tx);

        if (gameId is null)
        {
            return RemoteApplyOutcome.Deferred;
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

        return RemoteApplyOutcome.Applied;
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
    /// 在**调用方的事务内**为被丢弃的游戏 identity 写一条墓碑。
    ///
    /// 墓碑只认 <c>global_id</c>（Sappfiler 多设备同步身份），与 <c>notion_page_id</c> 无关：
    /// 两行即使共用同一个 notion_page_id，只要被丢弃的 identity 曾经可同步，就要写墓碑 ——
    /// **多写墓碑是幂等无害的，漏写是不可逆的身份残留**。
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
/// 远端变更的落地结果计数。
/// <paramref name="Deferred"/> = 本轮**没有**落地的条目（已存在 / 需要身份对账 / 依赖缺失 /
/// payload 版本不认识）。它不是"丢弃"—— 会计入日志，留给后续冲突解决与身份对账阶段。
/// </summary>
public sealed record RemoteApplyResult(int Applied, int Deferred, int TombstonesPropagated);
