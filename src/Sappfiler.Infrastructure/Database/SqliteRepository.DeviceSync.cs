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
