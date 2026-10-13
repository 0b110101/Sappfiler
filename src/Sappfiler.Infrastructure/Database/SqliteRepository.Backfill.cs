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
    ///   · Session：<c>global_id</c> 非空、其游戏也有 <c>global_id</c>，**且已经结束（is_active = 0）**
    ///     —— 与 <see cref="GetBackfillSessionsAsync"/> 的口径完全一致（正在进行中的会话不算候选）；
    ///   · **归属**：Game 要求"本机产生过它的会话"、Session 要求 <c>device_id = 本机</c> ——
    ///     从对端落地的历史事实不算本机候选（否则会整批回推成 already_exists_local）。
    /// </summary>
    public async Task<(int Games, int Sessions)> CountBackfillCandidatesAsync()
    {
        using var conn = CreateConnection();
        var localDeviceId = await GetLocalDeviceIdAsync();

        var games = await conn.ExecuteScalarAsync<int>(
            $"""
             SELECT COUNT(*) FROM games
             WHERE NULLIF(TRIM(games.global_id), '') IS NOT NULL
               AND {GameSyncEligibility.SqlPredicate}
               AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate}
               AND (
                     NOT EXISTS (SELECT 1 FROM sessions s2 WHERE s2.game_id = games.id)
                     OR EXISTS (SELECT 1 FROM sessions s3
                                WHERE s3.game_id = games.id AND s3.device_id = @localDeviceId));
             """,
            new { localDeviceId });

        var sessions = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sessions s
            JOIN games g ON g.id = s.game_id
            WHERE NULLIF(TRIM(s.global_id), '') IS NOT NULL
              AND NULLIF(TRIM(g.global_id), '') IS NOT NULL
              AND s.is_active = 0
              AND s.device_id = @localDeviceId;
            """,
            new { localDeviceId });

        return (games, sessions);
    }

    /// <summary>取一批可播种的 Game（按 <c>id</c> 升序，续跑靠 <paramref name="afterId"/>）。</summary>
    public async Task<IReadOnlyList<BackfillGameRow>> GetBackfillGamesAsync(long afterId, int limit)
    {
        using var conn = CreateConnection();
        var localDeviceId = await GetLocalDeviceIdAsync();

        var rows = await conn.QueryAsync<BackfillGameRow>(
            $"""
             SELECT id, global_id, name, platform, platform_id, executable, executable_path, notion_page_id
             FROM games
             WHERE id > @afterId
               AND NULLIF(TRIM(global_id), '') IS NOT NULL
               AND {GameSyncEligibility.SqlPredicate}
               AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate}
               AND (
                     NOT EXISTS (SELECT 1 FROM sessions s2 WHERE s2.game_id = games.id)
                     OR EXISTS (SELECT 1 FROM sessions s3
                                WHERE s3.game_id = games.id AND s3.device_id = @localDeviceId))
             ORDER BY id ASC
             LIMIT @limit;
             """,
            new { afterId, limit, localDeviceId });

        return rows.ToList();
    }

    /// <summary>
    /// 取一批可播种的 Session（按 <c>id</c> 升序）。
    ///
    /// ⚠️ <b>只取"本机产生"的会话</b>（<c>s.device_id = 本机 device_id</c>）：从对端**落地**的会话
    /// 保留其**原产设备** id，它们在对端本来就有；把它们当成本机候选再播种/回推，
    /// 只会让对端整批判成 <c>already_exists_local</c>（永久 Deferred）并淹掉真正的新数据 ——
    /// 这是 2026-10-08 第二次双机验收的真实事故（B 把从 A 收到的 77 条全部回推）。
    /// 与「历史 Outbox Gap Repair」的判据**完全一致**（同一原则：只有本机产生的事实，
    /// 才允许由本机历史修复机制重新进入自己的 outbox）。
    ///
    /// ⚠️ <b>只取已经结束的会话</b>（<c>s.is_active = 0</c>）：正在进行的会话还没定型 ——
    /// 它的 <c>duration_seconds</c> 只是"到目前为止"、<c>ended_at_utc</c> 还没写。
    /// 一旦把它导出成"部分事实"，会话真正结束时还会为**同一个 global_id** 产生一条事实不同的变更，
    /// 对端只能得到 <c>AlreadyExistsLocal</c> / 拒绝改写历史（永久 Deferred）——
    /// 用户 2026-10-08 裁定：**未结束的会话绝不进 Device Sync**，只有结束路径
    /// （`EndSessionWithOutboxAsync`，is_active=0 与最终事实同事务写入）才能产生同步事实。
    ///
    /// ⚠️ 为什么不额外要求 <c>ended_at_utc IS NOT NULL</c>：<c>ended_at_utc</c> <b>不属于</b> Session 的
    /// 四项不可变事实（device_id / started_at_utc / duration_seconds / game identity，
    /// 见 <c>SessionIdentityRules.SessionFacts</c>）；而被"僵尸清理"收尾的会话
    /// （<c>CleanupStaleSessionsAsync</c> 只写 <c>end_time = last_heartbeat</c> + <c>is_active = 0</c>）
    /// 会出现 is_active=0 但 ended_at_utc 为空的行，加上该条件会让它们**永远无法同步**
    /// （schema 自愈的查询条件同样不含 ended_at_utc，不会回填这些行）。<c>is_active = 0</c>
    /// 已经足够表达"这条会话不会再产生新的事实"。
    /// </summary>
    public async Task<IReadOnlyList<BackfillSessionRow>> GetBackfillSessionsAsync(long afterId, int limit)
    {
        using var conn = CreateConnection();
        var localDeviceId = await GetLocalDeviceIdAsync();

        var rows = await conn.QueryAsync<BackfillSessionRow>(
            """
            SELECT s.id, s.global_id, s.device_id, s.process_name, s.started_at_utc, s.ended_at_utc,
                   s.duration_seconds, s.time_precision, g.global_id AS game_global_id
            FROM sessions s
            JOIN games g ON g.id = s.game_id
            WHERE s.id > @afterId
              AND s.is_active = 0
              AND s.device_id = @localDeviceId
              AND NULLIF(TRIM(s.global_id), '') IS NOT NULL
              AND NULLIF(TRIM(g.global_id), '') IS NOT NULL
            ORDER BY s.id ASC
            LIMIT @limit;
            """,
            new { afterId, limit, localDeviceId });

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
