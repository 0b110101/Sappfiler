using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// 历史播种（backfill）与幂等入队所需的**最小**仓储扩展（2h-5）。
///
/// ⚠️ 边界（2h 设计 v2 §C 冻结）：
///   · 这里**只 SELECT** <c>games</c>/<c>sessions</c>，并**只 INSERT</c> <c>sync_queue</c>；
///   · 绝不 UPDATE sessions / games / daily_summary；绝不触碰 Notion 路径；
///   · 唯一新增的写入路径是"按确定性 queue_id 幂等入队"（<c>ON CONFLICT DO NOTHING</c>），
///     不改任何既有方法的语义。
/// </summary>
public partial class SqliteRepository
{
    // ============================ 幂等入队 ============================

    /// <summary>
    /// 按 <c>queue_id</c> 幂等入队：已存在同样的 <c>queue_id</c> 时**不产生第二行**。
    /// 返回是否真的插入了（false = 早就有了）。
    ///
    /// 播种用它就能做到"重复执行完全无害"（配合确定性 queue_id）。
    /// </summary>
    public async Task<bool> EnqueueOutboxIfAbsentAsync(SyncQueueItem item)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            return await EnqueueOutboxIfAbsentCoreAsync(conn, null, item);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>幂等入队的核心（可参与调用方事务）。</summary>
    private static async Task<bool> EnqueueOutboxIfAbsentCoreAsync(
        SqliteConnection conn, SqliteTransaction? tx, SyncQueueItem item)
    {
        var affected = await conn.ExecuteAsync(
            """
            INSERT INTO sync_queue
                (queue_id, entity_type, entity_global_id, operation, base_version,
                 payload, created_at_utc, retry_count, next_retry_at_utc, status, last_error)
            VALUES
                (@QueueId, @EntityType, @EntityGlobalId, @Operation, @BaseVersion,
                 @Payload, @CreatedAtUtc, @RetryCount, @NextRetryAtUtc, @Status, @LastError)
            ON CONFLICT(queue_id) DO NOTHING;
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

        return affected > 0;
    }

    // ============================ 播种候选 ============================

    /// <summary>
    /// 统计"可同步事实"数量（用于启用前的"预计上传 N 条"确认提示）。
    ///
    /// 范围（2b/2f 冻结规则，一处定义）：
    ///   · Game：<see cref="GameSyncEligibility.SqlPredicate"/> 为真 **且** 已有 <c>global_id</c>
    ///     （没有 identity 的游戏无法构造合法变更 —— 播种**不生成**新 identity，
    ///     因为播种只允许 SELECT + INSERT sync_queue）；
    ///   · Session：<c>global_id</c> 非空，且其游戏也有 <c>global_id</c>。
    /// </summary>
    public async Task<(int Games, int Sessions)> CountBackfillCandidatesAsync()
    {
        using var conn = CreateConnection();

        var games = await conn.ExecuteScalarAsync<int>(
            $"""
             SELECT COUNT(*) FROM games
             WHERE NULLIF(TRIM(games.global_id), '') IS NOT NULL
               AND {GameSyncEligibility.SqlPredicate}
               AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate};
             """);

        var sessions = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sessions s
            JOIN games g ON g.id = s.game_id
            WHERE NULLIF(TRIM(s.global_id), '') IS NOT NULL
              AND NULLIF(TRIM(g.global_id), '') IS NOT NULL;
            """);

        return (games, sessions);
    }

    /// <summary>取一批可播种的 Game（按 <c>id</c> 升序，续跑靠 <paramref name="afterId"/>）。</summary>
    public async Task<IReadOnlyList<BackfillGameRow>> GetBackfillGamesAsync(long afterId, int limit)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<BackfillGameRow>(
            $"""
             SELECT id, global_id, name, platform, platform_id, executable, executable_path, notion_page_id
             FROM games
             WHERE id > @afterId
               AND NULLIF(TRIM(global_id), '') IS NOT NULL
               AND {GameSyncEligibility.SqlPredicate}
               AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate}
             ORDER BY id ASC
             LIMIT @limit;
             """,
            new { afterId, limit });

        return rows.ToList();
    }

    /// <summary>取一批可播种的 Session（按 <c>id</c> 升序）。</summary>
    public async Task<IReadOnlyList<BackfillSessionRow>> GetBackfillSessionsAsync(long afterId, int limit)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<BackfillSessionRow>(
            """
            SELECT s.id, s.global_id, s.device_id, s.process_name, s.started_at_utc, s.ended_at_utc,
                   s.duration_seconds, s.time_precision, g.global_id AS game_global_id
            FROM sessions s
            JOIN games g ON g.id = s.game_id
            WHERE s.id > @afterId
              AND NULLIF(TRIM(s.global_id), '') IS NOT NULL
              AND NULLIF(TRIM(g.global_id), '') IS NOT NULL
            ORDER BY s.id ASC
            LIMIT @limit;
            """,
            new { afterId, limit });

        return rows.ToList();
    }

    /// <summary>本地待发/在途变更条数（UI 的"待同步"计数）。</summary>
    public async Task<int> CountPendingOutboxAsync()
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_queue WHERE status IN (@pending, @inFlight);",
            new { pending = (int)SyncQueueStatus.Pending, inFlight = (int)SyncQueueStatus.InFlight });
    }

    /// <summary>
    /// 最近一次成功同步落地的时刻（读 <c>sync_state.last_sync_at_utc</c>）。
    /// 该值由"数据 + 游标同一事务"写下游标时一并写入，因此是**可信的高水位时间**。
    /// </summary>
    public async Task<string?> GetLastSyncAtUtcAsync()
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT MAX(last_sync_at_utc) FROM sync_state WHERE NULLIF(TRIM(last_sync_at_utc), '') IS NOT NULL;");
    }

    /// <summary>死信条数（永久拒绝的变更，需要人工关注）。</summary>
    public async Task<int> CountFailedOutboxAsync()
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_queue WHERE status = @failed;",
            new { failed = (int)SyncQueueStatus.Failed });
    }

    // ============================ 行模型 ============================

    /// <summary>播种用的 Game 行（只读快照）。</summary>
    public sealed class BackfillGameRow
    {
        public long Id { get; set; }
        public string GlobalId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Platform { get; set; } = "";
        public string PlatformId { get; set; } = "";
        public string Executable { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string? NotionPageId { get; set; }
    }

    /// <summary>播种用的 Session 行（只读快照）。</summary>
    public sealed class BackfillSessionRow
    {
        public long Id { get; set; }
        public string GlobalId { get; set; } = "";
        public string? DeviceId { get; set; }
        public string? ProcessName { get; set; }
        public string? StartedAtUtc { get; set; }
        public string? EndedAtUtc { get; set; }
        public int DurationSeconds { get; set; }
        public string? TimePrecision { get; set; }
        public string GameGlobalId { get; set; } = "";
    }
}
