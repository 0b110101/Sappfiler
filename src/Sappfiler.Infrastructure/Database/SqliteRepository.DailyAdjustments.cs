using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// 日时长调整（DailyDurationAdjustment，schema v2）的存储层。
///
/// <para>
/// 语义（2026-10-08 用户批准并冻结）：
/// <code>
/// daily_summary(game, 统计日) = Σ 会话派生秒数 + Σ adjustments(该 game 该日, 全部设备相加)
/// </code>
/// · 同一统计日同一游戏的 adjustment **相加**（A +30min、B +20min ⇒ +50min），**不取 max、不做 LWW**；
/// · adjustment 是**设备产生的历史事实**（identity 稳定 = device + game + 日 + source）；
/// · 它**不改写任何 Session 事实**，也不把 <c>daily_summary</c> 变成跨设备实体。
/// </para>
///
/// <para>
/// ⚠️ 关于"同 identity 再次到达"：对 Session，事实不可变 ⇒ 同 id 不同事实只能 Blocked；
///    adjustment 的语义是"这台设备对这一天追加了多少秒"，因此**同 id 不同值 = 该设备修正了自己的声明**，
///    按差值收敛（并把差值同步到 daily）—— 这样 Σ adjustments 恒等于各设备声明的和，
///    重复 Pull 幂等、修正也能收敛，不会留下永久陈旧的对端数字。
/// </para>
/// </summary>
public partial class SqliteRepository
{
    /// <summary>
    /// <b>唯一重算入口</b>：把 (game, 统计日) 的派生行设为
    /// <c>Σ 会话派生秒数 + Σ 当前有效 Adjustment declarations</c>（§6）。
    ///
    /// <para>
    /// 为什么用"设"而不是"加"：adjustment 是**独立事实**，派生行只是缓存 ——
    /// 用事实重算天然幂等（同一事实重复到达、乱序到达、修正，都得到同一个结果），
    /// 不会因"上一次加了哪一笔"的记账漂移而重复累加。
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item>不动 Session、不改 <c>session_count</c>（保持既有日历日语义）；</item>
    ///   <item>不产生同步事件、不入队、不改事实表；</item>
    ///   <item>无事实且无行 ⇒ 不建空行；有事实无行 ⇒ 建行（<c>&lt;60s</c> 的日子也必须可见）；</item>
    ///   <item>可重复运行，结果稳定。</item>
    /// </list>
    /// </summary>
    /// <param name="preservedResidualSeconds">
    /// **受保护残差**（2026-10-11 审查新增）：调用方在**改动事实之前**量到的
    /// "已有 daily 值 − 当时的事实值"，代表"既无会话也无声明支撑的历史值"（C 类）。
    /// 重算会把这段残差原样带上，避免静默抹掉来源不明的历史时长；撤销/下调仍能精确生效，
    /// 因为被撤掉的那一段属于"算得出来的部分"，不在残差里。默认 0 = 不保护（等于旧行为）。
    /// </param>
    internal static void RecomputeDailySummaryForGameDate(
        SqliteConnection conn, SqliteTransaction? tx, int gameId, string statisticalDate, int cutoffHour,
        int preservedResidualSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(statisticalDate)) return;

        var total = ComputeEffectiveDailySeconds(conn, tx, gameId, statisticalDate, cutoffHour);
        var minutes = total / 60;

        // 🛡️ C 类保护（2026-10-11 审查）：**重算只允许抹掉它算得出来的那部分**。
        //    库里存在"既无会话、也无声明支撑"的历史值（例如 Notion 直接写入 daily、从未落成声明的行）。
        //    若在这里静默按事实覆盖，这些值会被无声抹掉 —— 那是不可逆的数据语义变更。
        //    因此：把"已有值 − 本次改动前的事实值"当作**受保护残差**保留下来（并留日志待人工裁定）。
        //    ⚠️ 残差是在调用方**改动事实之前**算出来的（调用方通过 preservedResidualSeconds 传入）；
        //       于是撤销/下调仍能精确生效（被撤掉的那一段属于"算得出来的部分"，不在残差里）。
        if (preservedResidualSeconds > 0)
        {
            total += preservedResidualSeconds;
            minutes = total / 60;
        }

        if (!DailyRowExists(conn, tx, gameId, statisticalDate))
        {
            if (total <= 0) return;

            conn.Execute(
                """
                INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
                VALUES (@date, @gameId, @total, @minutes, 0, 'pending');
                """,
                new { date = statisticalDate, gameId, total, minutes },
                tx);
            return;
        }

        conn.Execute(
            """
            UPDATE daily_summary
            SET duration_seconds = @total,
                duration_minutes = @minutes,
                sync_status      = 'pending'
            WHERE date = @date AND game_id = @gameId;
            """,
            new { total, minutes, date = statisticalDate, gameId },
            tx);
    }

    /// <summary>
    /// <b>有效日时长（唯一公式）</b>：
    /// <c>Σ 会话派生秒数 + Σ 当前有效 Adjustment declarations</c>。
    /// </summary>
    internal static int ComputeEffectiveDailySeconds(
        SqliteConnection conn, SqliteTransaction? tx, int gameId, string statisticalDate, int cutoffHour)
        => SumSessionDerivedSeconds(conn, tx, gameId, statisticalDate, cutoffHour)
           + SumAdjustments(conn, tx, gameId, statisticalDate);

    // ============================ 远端落地 ============================

    /// <summary>
    /// 落一条远端日时长调整。返回 <c>null</c> = 已落地；否则是延后原因码。
    ///
    /// ⚠️ 与远端会话落地**共用** <see cref="ApplyDailyDurationDelta"/>（同一条日统计口径，
    /// 不允许出现第二套"+秒数进 daily"的实现）。
    /// </summary>
    private static (string? Reason, int SchemaVersion) ApplyRemoteDailyAdjustment(
        SqliteConnection conn, SqliteTransaction tx, SyncChange change)
    {
        if (!SyncPayloads.TryParseDailyAdjustment(change.Payload, out var payload, out var schemaVersion, out _))
        {
            return (ClassifyParseFailure(schemaVersion), Math.Max(schemaVersion, 0));
        }

        var adjustment = payload!;

        // 依赖：必须挂在本地已存在的 game 上（与 Session 完全一致的口径）。
        // ⚠️ 必须走**别名解析**（2026-10-11 事故修复）：游戏 global_id 是每台机器随机生成的
        //    （Guid.NewGuid()），两台机器各自拥有同一款游戏时，对端的 identity 会被 2e 身份对账
        //    并入本机权威 id（保留本机 global_id）。不解析别名 ⇒ 这条外部声明
        //    **永久 dependency_missing**，即"Notion 手工录入的数据没有同步到另一台设备"。
        var gameId = ResolveLocalGameIdByGlobalId(conn, tx, adjustment.GameGlobalId);

        if (gameId is null)
        {
            return (DeferredReasons.DependencyMissing, schemaVersion);
        }

        // 与远端会话同口径：Game 已删除（历史归档）⇒ "事实 vs 删除"冲突，保持 Deferred，不静默挂上、
        // 也不丢弃 payload（§2：仅在现有冻结的 deletion/conflict 语义允许时才落地）。
        var gameStatus = conn.ExecuteScalar<string?>(
            "SELECT status FROM games WHERE id = @id;", new { id = gameId.Value }, tx);

        if (GameTimeTracker.Core.DeviceSync.GameStatuses.IsArchived(gameStatus))
        {
            return (DeferredReasons.DeletionConflict, schemaVersion);
        }

        // ---- 声明撤销（2026-10-11，schema 3）----
        // 语义：**产生该声明的设备把它撤回了**（典型：用户在 Notion 删除了对应每日记录页面）。
        // identity 里含 设备+游戏+统计日+来源 ⇒ 只撤回精确的那一条，绝不误伤别的来源/日期/设备。
        // 动作：删掉本地该 identity 的声明，然后**从事实重算** daily_summary（派生缓存可以因此变小）。
        // ⚠️ **绝不碰 Session**：页面被删 ≠ 没玩过，会话是独立事实。
        // 幂等：本机没有这条声明时同样只重算一次，结果完全一致；重复 Pull 不会重复扣减。
        // 🛡️ C 类保护：在**改动任何事实之前**量出"已有 daily 值 − 当时的事实值"= 受保护残差
        //    （既无会话、也无声明支撑的历史值）。撤销与修正都会把它原样带上，绝不静默抹掉。
        var residualBeforeChange = 0;
        var existingDailyBefore = conn.ExecuteScalar<int?>(
            "SELECT duration_seconds FROM daily_summary WHERE date = @d AND game_id = @g;",
            new { d = adjustment.StatisticalDate, g = gameId.Value }, tx) ?? 0;

        if (existingDailyBefore > 0)
        {
            var factsBefore = ComputeEffectiveDailySeconds(
                conn, tx, gameId.Value, adjustment.StatisticalDate, ReadDailyCutoffHour(conn, tx));
            residualBeforeChange = Math.Max(0, existingDailyBefore - factsBefore);

            if (residualBeforeChange > 0)
            {
                AppLog.Warn($"[设备同步] {adjustment.StatisticalDate} 该游戏行有 {residualBeforeChange} 秒"
                    + "既无会话也无声明支撑（来源不明的历史值）—— 重算时原样保留，待人工裁定");
            }
        }

        if (adjustment.Revoked)
        {
            // 🛡️ 乱序保护（2026-10-11 审查）：**比本机现存声明更新的重新录入不得被旧撤销误删**。
            //    撤销与声明由**同一台设备**产生（identity 内含 device_id）⇒ 比较的是同一口时钟，安全。
            //    典型场景：撤销(t1) → 用户重新录入(t2>t1) → 游标回卷把撤销(t1)再放一遍。
            var localCreatedAt = conn.ExecuteScalar<string?>(
                "SELECT created_at_utc FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
                new { id = adjustment.GlobalId }, tx);

            if (!string.IsNullOrWhiteSpace(localCreatedAt)
                && string.CompareOrdinal(localCreatedAt, adjustment.CreatedAtUtc) > 0)
            {
                AppLog.Info($"[设备同步] 收到过期撤销（{adjustment.CreatedAtUtc} < 本机 {localCreatedAt}）"
                    + $"：保留较新的声明 {adjustment.StatisticalDate} {adjustment.Source}");
                return (null, schemaVersion);   // 已处理（不算 Deferred），状态不变
            }

            conn.Execute(
                "DELETE FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
                new { id = adjustment.GlobalId }, tx);

            // 🛡️ 2026-10-11（撤销乱序持久化）：撤销必须**持久化**（墓碑），否则游标回卷 / Backfill
            //    重放一份**旧于撤销**的声明时会把它重新加回来（撤销被还原）。
            //    墓碑的 deleted_at_utc = 该撤销的 created_at_utc ⇒ 更新的重新录入仍可生效。
            AddTombstoneCore(conn, tx, new SyncTombstone
            {
                TombstoneId = Guid.NewGuid().ToString("N"),
                EntityType = nameof(SyncEntityType.DailyAdjustment),
                EntityGlobalId = adjustment.GlobalId,
                DeviceId = adjustment.DeviceId,
                DeletedAtUtc = adjustment.CreatedAtUtc,
                CreatedAtUtc = UtcNowIso()
            });

            // ⚠️ 同一 identity 的**陈旧 Deferred 快照**必须一起收敛掉：

            //    否则当初"依赖缺失"时留下的旧 payload（正数声明）会在下一次 2e 重处理时被重放，
            //    把刚撤销的声明又加回来（撤销被还原）。撤销是这条 identity 的**最新状态**，
            //    所以它们的结论就是"已撤销"。
            conn.Execute(
                """
                UPDATE sync_deferred_changes
                SET resolved_at_utc = @now, resolution = 'revoked'
                WHERE entity_type = @type AND entity_global_id = @id AND resolved_at_utc IS NULL;
                """,
                new { now = UtcNowIso(), type = nameof(SyncEntityType.DailyAdjustment), id = adjustment.GlobalId }, tx);

            RecomputeDailySummaryForGameDate(
                conn, tx, gameId.Value, adjustment.StatisticalDate, ReadDailyCutoffHour(conn, tx),
                preservedResidualSeconds: residualBeforeChange);

            return (null, schemaVersion);
        }

        var existing = conn.QuerySingleOrDefault<int?>(
            "SELECT duration_seconds FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
            new { id = adjustment.GlobalId }, tx);

        if (existing is null)
        {
            conn.Execute(
                """
                INSERT INTO daily_duration_adjustments
                    (adjustment_global_id, game_id, statistical_date, device_id, duration_seconds, source, created_at_utc)
                VALUES (@globalId, @gameId, @date, @deviceId, @duration, @source, @createdAtUtc);
                """,
                new
                {
                    globalId = adjustment.GlobalId,
                    gameId = gameId.Value,
                    date = adjustment.StatisticalDate,
                    deviceId = adjustment.DeviceId,
                    duration = adjustment.DurationSeconds,
                    source = adjustment.Source,
                    createdAtUtc = string.IsNullOrWhiteSpace(adjustment.CreatedAtUtc)
                        ? UtcNowIso()
                        : adjustment.CreatedAtUtc
                },
                tx);

            // 新事实落地：增量累加（保留行里既有的会话派生部分；无行时该实现会建行 ⇒
            // "无 Session、无既有 daily 行"的日期同样能 materialize）。
            ApplyDailyDurationDelta(conn, tx, adjustment.StatisticalDate, gameId.Value, adjustment.DurationSeconds);
            return (null, schemaVersion);
        }

        // 已存在：只在"该设备修正了自己的声明"时收敛（差值为 0 ⇒ 完全幂等，什么都不做）。
        var delta = adjustment.DurationSeconds - existing.Value;
        if (delta == 0) return (null, schemaVersion);

        conn.Execute(
            """
            UPDATE daily_duration_adjustments
            SET duration_seconds = @duration,
                -- 远端修正同样是"该 identity 的一次修订" ⇒ 刷新修订时间（供撤销乱序保护比较）
                created_at_utc = COALESCE(NULLIF(@createdAt, ''), created_at_utc)
            WHERE adjustment_global_id = @id;
            """,
            new
            {
                duration = adjustment.DurationSeconds,
                createdAt = adjustment.CreatedAtUtc,
                id = adjustment.GlobalId
            },
            tx);

        // 修正（同一 identity 的新声明）：派生行 = **由事实重算**
        //   = Σ 会话派生秒数 + Σ 当前有效 declarations（+ 受保护残差）。
        //   增（1000→1200）、减（1200→1000）、重复 Pull 全部走这一条路径 ⇒
        //   结果只取决于事实，既不累加、也不残留旧值（不再用 max(...) 掩盖计算差异）。
        RecomputeDailySummaryForGameDate(
            conn, tx, gameId.Value, adjustment.StatisticalDate, ReadDailyCutoffHour(conn, tx),
            preservedResidualSeconds: residualBeforeChange);

        _ = delta;
        return (null, schemaVersion);
    }

    // ============================ 本地产生（Notion / 手工） ============================

    /// <summary>
    /// 记录一条**本机产生**的日时长调整（Notion 导入 / 手工录入的增量）。
    ///
    /// 做三件事（同一事务）：
    /// <list type="number">
    ///   <item>按稳定 identity upsert 本机这条 adjustment（同一天同一来源只保留一条，重复导入不叠加）；</item>
    ///   <item>把**增量差**（新值 − 旧值）累加进 <c>daily_summary</c>（复用会话落地那一份实现）；</item>
    ///   <item>入队一条 v2 同步事实（若该 game 有 global_id），让对端在**没有 Notion** 的情况下也能拿到这段时间。</item>
    /// </list>
    /// ⚠️ 绝不覆盖整条 <c>daily_summary</c>：行值始终是"会话派生 + Σ adjustments"的派生结果。
    /// </summary>
    /// <returns>本次写入的 adjustment 秒数（未写入/非法输入返回 0）。</returns>
    public async Task<int> RecordDailyAdjustmentAsync(
        int gameId, string statisticalDate, int durationSeconds, string source)
    {
        if (durationSeconds <= 0 || string.IsNullOrWhiteSpace(statisticalDate)) return 0;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var written = await UpsertLocalAdjustmentCoreAsync(
                conn, tx, gameId, statisticalDate, durationSeconds, source, createDailyRowIfMissing: true);

            tx.Commit();
            return written;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 在**调用方的事务内**写入本机的一条日时长调整（Notion 导入与手工录入共用这一份实现）。
    ///
    /// 语义：按稳定 identity upsert 本机这一条声明（同一天同一来源只有一条，重复导入不叠加），
    /// 只把**增量差**（新值 − 旧值）累加进 <c>daily_summary</c>，并入队一条 v2 同步事实。
    /// </summary>
    /// <returns>写入后的 adjustment 秒数。</returns>
    /// <param name="createDailyRowIfMissing">
    /// daily 行不存在时是否顺手建它。Notion 导入路径传 <c>false</c>：它紧接着会**自己**用
    /// "会话派生 + Σ adjustments"算出整行的值并写库（若这里先 INSERT 一行，那边就会撞
    /// <c>daily_summary(date, game_id)</c> 的唯一约束）。手工录入路径传 <c>true</c>。
    /// </param>
    private static async Task<int> UpsertLocalAdjustmentCoreAsync(
        SqliteConnection conn, SqliteTransaction tx,
        int gameId, string statisticalDate, int durationSeconds, string source,
        bool createDailyRowIfMissing,
        int? observedRemoteSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(statisticalDate)) return 0;

        var resolvedSource = string.IsNullOrWhiteSpace(source) ? DailyAdjustmentSources.Manual : source;

        var game = conn.QuerySingleOrDefault<dynamic>(
            "SELECT id, global_id FROM games WHERE id = @gameId;", new { gameId }, tx);

        if (game is null) return 0;

        var localDeviceId = ReadLocalDeviceId(conn, tx);

        string? gameGlobalId = game.global_id as string;
        if (string.IsNullOrWhiteSpace(gameGlobalId)) gameGlobalId = null;

        // identity 稳定：无 global_id 时用本机 device + 本地 id 兜底（仍能本地去重；不参与同步）。
        var globalId = DailyAdjustmentGlobalId.For(
            localDeviceId,
            gameGlobalId ?? $"local-game-{gameId}",
            statisticalDate,
            resolvedSource);

        var existing = conn.QuerySingleOrDefault<int?>(
            "SELECT duration_seconds FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
            new { id = globalId }, tx);

        var newValue = Math.Max(0, durationSeconds);
        var delta = newValue - (existing ?? 0);

        // 绝不新增"0 秒声明"噪音行（历史遗留的 0 值行保留不动，但不再写入新的）：
        // Notion 那边把时长清空/缺失时，"本机声明 = 0" 不该变成一条会被同步出去的事实。
        if (existing is null && newValue <= 0) return 0;

        if (delta == 0 && existing is not null) return newValue;   // 完全幂等：同样的值不重写、不重复入队

        var now = UtcNowIso();

        if (existing is null)
        {
            conn.Execute(
                """
                INSERT INTO daily_duration_adjustments
                    (adjustment_global_id, game_id, statistical_date, device_id, duration_seconds, source, created_at_utc,
                     observed_remote_seconds)
                VALUES (@globalId, @gameId, @date, @deviceId, @duration, @source, @now, @observed);
                """,
                new
                {
                    globalId,
                    gameId,
                    date = statisticalDate,
                    deviceId = localDeviceId,
                    duration = newValue,
                    source = resolvedSource,
                    now,
                    observed = observedRemoteSeconds
                },
                tx);
        }
        else
        {
            // 快照只在调用方**确实观察到**远端值时推进（null = 本次没有观察结果，保持原快照）。
            conn.Execute(
                """
                UPDATE daily_duration_adjustments
                SET duration_seconds = @duration,
                    -- ⚠️ 2026-10-11：重写声明时**刷新 created_at_utc**（= 该 identity 的最近修订时间）。
                    --    撤销的乱序保护靠它区分"较新的重新录入"与"迟到的旧撤销"。
                    created_at_utc = @createdAt,
                    observed_remote_seconds = COALESCE(@observed, observed_remote_seconds)
                WHERE adjustment_global_id = @id;
                """,
                new { duration = newValue, createdAt = now, observed = observedRemoteSeconds, id = globalId },
                tx);
        }

        // ⚠️ 只把**差值**累加进 daily（绝不整行覆盖）——口径与远端落地完全一致。
        if (delta > 0)
        {
            if (createDailyRowIfMissing || DailyRowExists(conn, tx, gameId, statisticalDate))
            {
                ApplyDailyDurationDelta(conn, tx, statisticalDate, gameId, delta);
            }
        }
        else if (delta < 0 && DailyRowExists(conn, tx, gameId, statisticalDate))
        {
            // 负修正（用户把手工时长改小了）：直接按差值回落，绝不新建行、绝不落到负数。
            // ⚠️ 不能用 ApplyDailyDurationDelta —— 它对 <=0 的增量是"直接忽略"，会让 daily 永远偏高。
            conn.Execute(
                """
                UPDATE daily_summary
                SET duration_seconds = MAX(0, duration_seconds + @delta),
                    duration_minutes = MAX(0, duration_seconds + @delta) / 60,
                    sync_status      = 'pending'
                WHERE date = @date AND game_id = @gameId;
                """,
                new { delta, date = statisticalDate, gameId },
                tx);
        }

        // 只有"能被对端理解"的实体才入队：
        //   · 没有 game identity → 只留本地（绝不发明 identity）；
        //   · 值为 0（例如 Notion 侧把手工时长清空了）→ 不产生同步事实（payload 只接受正增量）。
        if (gameGlobalId is not null && newValue > 0)
        {
            // 🔒 2026-10-11：依赖必须先于被依赖者入队（否则对端永久 dependency_missing）。
            await EnsureGameOutboxRowForDependencyAsync(conn, tx, gameId, gameGlobalId);

            await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
            {
                QueueId = Guid.NewGuid().ToString("N"),
                EntityType = nameof(SyncEntityType.DailyAdjustment),
                EntityGlobalId = globalId,
                Operation = existing is null ? SyncOperation.Create : SyncOperation.Update,
                BaseVersion = 1,
                Payload = SyncPayloadFactory.DailyAdjustment(
                    globalId, gameGlobalId, statisticalDate, localDeviceId,
                    newValue, resolvedSource, now),
                CreatedAtUtc = now,
                Status = SyncQueueStatus.Pending
            });
        }

        return newValue;
    }

    /// <summary>
    /// 在调用方已持有的连接上开一个小事务写入 adjustment（供 Notion 导入路径使用 ——
    /// 它已经持有写锁，**不能**再调 <see cref="RecordDailyAdjustmentAsync"/>，那会自死锁）。
    /// </summary>
    private static async Task<int> UpsertLocalAdjustmentWithOwnTransactionAsync(
        SqliteConnection conn, int gameId, string statisticalDate, int durationSeconds, string source,
        int? observedRemoteSeconds = null)
    {
        using var tx = conn.BeginTransaction();
        var written = await UpsertLocalAdjustmentCoreAsync(
            conn, tx, gameId, statisticalDate, durationSeconds, source,
            createDailyRowIfMissing: false, observedRemoteSeconds: observedRemoteSeconds);
        tx.Commit();
        return written;
    }

    /// <summary>
    /// <b>撤回本机某条外部声明</b>（2026-10-11）。
    ///
    /// <para>
    /// 触发场景：本机（重新）确认"这条声明的外部来源已经不存在了" —— 目前唯一调用者是
    /// <c>ReconcileNotionDeletionsAsync</c>（Notion 页面被删/归档，且已通过"远端集合完整 +
    /// <c>IsPageDeletedAsync</c> 二次确权"两道证据）。
    /// </para>
    ///
    /// <para>
    /// 做三件事，全部在**一个事务**内：
    /// <list type="number">
    ///   <item>删除本机该 identity（设备+游戏+统计日+来源）的声明行；</item>
    ///   <item>按事实重算 <c>daily_summary</c>（<b>可以变小</b>，这是撤销应有的效果）；</item>
    ///   <item>把撤销作为**同一 identity** 的同步事实入队（<c>revoked = true</c>），传播到对端。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ⚠️ 幂等：本机没有这条声明 ⇒ 直接 no-op 返回 false（绝不入队、绝不"凭空扣减"）；
    ///    队列里任何状态已有该 identity ⇒ 不再重复入队。
    /// ⚠️ 只接受 <c>source</c> 精确匹配：撤 Notion 声明绝不动同一天同游戏的手工声明。
    /// </para>
    /// </summary>
    public async Task<bool> RevokeLocalDailyAdjustmentAsync(int gameId, string statisticalDate, string source)
    {
        if (string.IsNullOrWhiteSpace(statisticalDate) || string.IsNullOrWhiteSpace(source)) return false;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var deviceId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM settings WHERE key = @k;",
                new { k = DeviceSyncConstants.SettingKeyDeviceId }, tx);

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                deviceId = DeviceSyncSchema.EnsureLocalDevice(conn, tx);
            }

            var gameGlobalId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT global_id FROM games WHERE id = @id;", new { id = gameId }, tx);

            if (string.IsNullOrWhiteSpace(gameGlobalId))
            {
                tx.Commit();   // 没有跨设备身份 ⇒ 这条声明本来就不参与同步，无需撤销
                return false;
            }

            var globalId = DailyAdjustmentGlobalId.For(deviceId, gameGlobalId!, statisticalDate, source);

            var exists = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
                new { id = globalId }, tx);

            if (exists == 0)
            {
                tx.Commit();   // 幂等：没有这条声明 ⇒ 什么都不做
                return false;
            }

            // 🛡️ C 类保护：**必须在删除声明之前**量残差（此时"事实"里还包含这条声明，
            //    于是残差正好是"既无会话也无声明支撑的历史值"，撤销的 600 秒仍能精确消失）。
            var existingDaily = await conn.ExecuteScalarAsync<int?>(
                "SELECT duration_seconds FROM daily_summary WHERE date = @d AND game_id = @g;",
                new { d = statisticalDate, g = gameId }, tx) ?? 0;

            var residual = existingDaily > 0
                ? Math.Max(0, existingDaily - ComputeEffectiveDailySeconds(
                    conn, tx, gameId, statisticalDate, ReadDailyCutoffHour(conn, tx)))
                : 0;

            var revocationAtUtc = UtcNowIso();

            await RevokeAdjustmentCoreAsync(
                conn, tx, gameId, statisticalDate, source,
                globalId, gameGlobalId!, deviceId!, residual, declarationExists: true);

            tx.Commit();
            AppLog.Info($"[设备同步] 已撤回外部声明：{statisticalDate} game={gameId} source={source}"
                + $"（identity={globalId[..8]}…，daily 已按事实重算，撤销已入队）");
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 撤销一条外部声明的**核心**（调用方已持有写锁与事务）：删声明（若在）→ 写墓碑 → 按事实重算 →
    /// 把撤销变更**入队或就地改写**（原声明已推成功 ⇒ 必须追加一条新变更，否则对端永久多算）。
    /// 抽出来是为了让「删除对账」能在**同一事务**里完成"撤回 + 事实层删除"（原子性），**不另写一套机制**。
    /// </summary>
    internal static async Task RevokeAdjustmentCoreAsync(
        SqliteConnection conn, SqliteTransaction tx,
        int gameId, string statisticalDate, string source,
        string globalId, string gameGlobalId, string deviceId,
        int preservedResidualSeconds, bool declarationExists)
    {
        var revocationAtUtc = UtcNowIso();

        if (declarationExists)
        {
            await conn.ExecuteAsync(
                "DELETE FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
                new { id = globalId }, tx);

            // 🛡️ 2026-10-11（撤销乱序持久化）：本机撤销同样要落墓碑 —— 语义与远端撤销一致：
            //    旧于本次撤销的声明（历史 batch / Backfill 重放 / Notion 重新导入的旧快照）
            //    会被墓碑挡下；用户之后在 Notion 重新录入的**更新**声明仍可生效。
            AddTombstoneCore(conn, tx, new SyncTombstone
            {
                TombstoneId = Guid.NewGuid().ToString("N"),
                EntityType = nameof(SyncEntityType.DailyAdjustment),
                EntityGlobalId = globalId,
                DeviceId = deviceId,
                DeletedAtUtc = revocationAtUtc,
                CreatedAtUtc = revocationAtUtc
            });

            RecomputeDailySummaryForGameDate(
                conn, tx, gameId, statisticalDate, ReadDailyCutoffHour(conn, tx),
                preservedResidualSeconds: preservedResidualSeconds);
        }

        var queueType = nameof(SyncEntityType.DailyAdjustment);
        var revocationPayload = SyncPayloads.Serialize(new DailyAdjustmentSyncPayload
        {
            SchemaVersion = DeviceSyncConstants.CurrentSchemaVersion,
            Revoked = true,
            GlobalId = globalId,
            GameGlobalId = gameGlobalId,
            StatisticalDate = statisticalDate,
            DeviceId = deviceId,
            DurationSeconds = 0,
            Source = source,
            CreatedAtUtc = UtcNowIso()
        });

        // ⚠️ 关键：原声明**已经推成功**（队列行 Completed）时，绝不能因为"队列里已有这个 identity"就跳过 ——
        //    那样撤销永远不会被推出去（对端永久多算，正是本次实机事故）。此时必须**追加一条新变更**，
        //    因为推送按 rowid 排序 ⇒ 对端必然是"先收到声明、再收到撤销"，顺序天然正确。
        //    只有"还没发出去"的变更才就地改写成撤销（最新状态覆盖，不新增行）。
        var pendingQueueId = await conn.QuerySingleOrDefaultAsync<string>(
            """
            SELECT queue_id FROM sync_queue
            WHERE entity_type = @type AND entity_global_id = @gid AND status <> @completed
            LIMIT 1;
            """,
            new { type = queueType, gid = globalId, completed = (int)SyncQueueStatus.Completed }, tx);

        if (!string.IsNullOrEmpty(pendingQueueId))
        {
            await conn.ExecuteAsync(
                """
                UPDATE sync_queue
                SET payload = @payload, created_at_utc = @now,
                    retry_count = 0, next_retry_at_utc = NULL, last_error = NULL
                WHERE queue_id = @id;
                """,
                new { payload = revocationPayload, now = UtcNowIso(), id = pendingQueueId }, tx);
        }
        else
        {
            var now = UtcNowIso();

            await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
            {
                QueueId = SyncHashing.BackfillQueueId(queueType, globalId),
                EntityType = queueType,
                EntityGlobalId = globalId,
                Operation = SyncOperation.Create,
                BaseVersion = 2,
                Payload = revocationPayload,
                CreatedAtUtc = now,
                Status = SyncQueueStatus.Pending
            });
        }
    }

    /// <summary>
    /// 在**调用方事务内**对某来源执行撤回（删除对账专用）。
    /// <paramref name="enqueueWhenAbsent"/>=true 时：**本机声明已不存在也照样写墓碑并把撤销入队** ——
    /// 对端可能仍持有它自己那条（identity 含设备维度），不广播就会让它永久多算；
    /// 这正是 <c>exists == 0</c> 设计边界的收口。
    /// </summary>
    internal async Task<bool> RevokeAdjustmentInTxCoreAsync(
        SqliteConnection conn, SqliteTransaction tx, int gameId, string statisticalDate, string source,
        bool enqueueWhenAbsent)
    {
        if (string.IsNullOrWhiteSpace(statisticalDate) || string.IsNullOrWhiteSpace(source)) return false;

        var deviceId = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = @k;",
            new { k = DeviceSyncConstants.SettingKeyDeviceId }, tx);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = DeviceSyncSchema.EnsureLocalDevice(conn, tx);
        }

        var gameGlobalId = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT global_id FROM games WHERE id = @id;", new { id = gameId }, tx);
        if (string.IsNullOrWhiteSpace(gameGlobalId)) return false;

        var globalId = DailyAdjustmentGlobalId.For(deviceId, gameGlobalId!, statisticalDate, source);
        var declarationExists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
            new { id = globalId }, tx) > 0;
        if (!declarationExists && !enqueueWhenAbsent) return false;

        // 与公共方法同口径：残差必须在删声明**之前**量（撤销的秒数仍能精确消失）。
        var existingDaily = await conn.ExecuteScalarAsync<int?>(
            "SELECT duration_seconds FROM daily_summary WHERE date = @d AND game_id = @g;",
            new { d = statisticalDate, g = gameId }, tx) ?? 0;
        var residual = existingDaily > 0
            ? Math.Max(0, existingDaily - ComputeEffectiveDailySeconds(
                conn, tx, gameId, statisticalDate, ReadDailyCutoffHour(conn, tx)))
            : 0;

        await RevokeAdjustmentCoreAsync(
            conn, tx, gameId, statisticalDate, source,
            globalId, gameGlobalId!, deviceId!, residual, declarationExists);
        return true;
    }

    /// <summary>
    /// 「Notion 页面已确认删除」的**原子**本地处理（单连接 + 单事务 + 写锁一次）：
    /// ① 对 <c>Notion</c> / <c>NotionExternal</c> 两个来源**无条件**写墓碑并广播撤销
    /// （本机声明已不存在也照做 ⇒ 对端不会永久多算）；
    /// ② 复用既有事实层删除：作废该会计日的会话分片 + 删派生行 + 记录级入队；
    /// 任一步失败 ⇒ 整笔回滚（墓碑 / 队列项 / 会话作废 / 派生行删除 **要么全有要么全无**）。
    /// </summary>
    public async Task<PlayRecordDeletionResult> ApplyRemoteDeletedDailyAsync(int dailySummaryId)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var locator = conn.QuerySingleOrDefault<PlayRecordLocator>(
                "SELECT game_id AS GameId, date AS Date FROM daily_summary WHERE id = @id LIMIT 1;",
                new { id = dailySummaryId }, tx);
            if (locator is null)
            {
                tx.Commit();
                return new PlayRecordDeletionResult { Note = "记录不存在（可能已被删除）" };
            }

            foreach (var source in new[]
            {
                GameTimeTracker.Core.DeviceSync.DailyAdjustmentSources.Notion,
                GameTimeTracker.Core.DeviceSync.DailyAdjustmentSources.NotionExternal
            })
            {
                await RevokeAdjustmentInTxCoreAsync(
                    conn, tx, (int)locator.GameId, locator.Date, source, enqueueWhenAbsent: true);
            }

            var result = await DeletePlayRecordFactsCoreAsync(conn, tx, (int)locator.GameId, locator.Date);
            tx.Commit();
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static bool DailyRowExists(
        SqliteConnection conn, SqliteTransaction? tx, int gameId, string statisticalDate)
        => conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM daily_summary WHERE date = @date AND game_id = @gameId;",
            new { date = statisticalDate, gameId }, tx) > 0;

    /// <summary>某游戏某统计日上**全部设备/来源**的 adjustment 秒数之和。</summary>
    internal static int SumAdjustments(
        SqliteConnection conn, SqliteTransaction? tx, long gameId, string statisticalDate)
        => conn.ExecuteScalar<int>(
            """
            SELECT COALESCE(SUM(duration_seconds), 0) FROM daily_duration_adjustments
            WHERE game_id = @gameId AND statistical_date = @date;
            """,
            new { gameId, date = statisticalDate }, tx);

    // ============================ 读 ============================

    /// <summary>某游戏某统计日上**全部设备**的 adjustment 秒数之和（最终统计口径的一半）。</summary>
    public async Task<int> SumAdjustmentsAsync(int gameId, string statisticalDate)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(SUM(duration_seconds), 0) FROM daily_duration_adjustments
            WHERE game_id = @gameId AND statistical_date = @date;
            """,
            new { gameId, date = statisticalDate });
    }

    /// <summary>读 adjustment 台账（诊断 / 测试）。</summary>
    public async Task<IReadOnlyList<DailyAdjustmentRow>> GetDailyAdjustmentsAsync(string? statisticalDate = null)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<DailyAdjustmentRow>(
            """
            SELECT adjustment_global_id AS AdjustmentGlobalId, game_id AS GameId,
                   statistical_date AS StatisticalDate, device_id AS DeviceId,
                   duration_seconds AS DurationSeconds, source AS Source, created_at_utc AS CreatedAtUtc
            FROM daily_duration_adjustments
            WHERE (@date IS NULL OR statistical_date = @date)
            ORDER BY statistical_date ASC, game_id ASC;
            """,
            new { date = statisticalDate });

        return rows.ToList();
    }

    // ============================ 共用口径 ============================

    /// <summary>
    /// 某游戏在某**会计日**上的"会话派生秒数"。
    ///
    /// ⚠️ 这是跨日拆分的**唯一实现**（与本机心跳/结束路径、远端会话落地共用
    /// <see cref="GameTimeTracker.Core.Services.GameSessionManager.ComputeDailyDeltas"/>）：
    /// 跨结算点的会话会被精确拆到两个日期上 —— 绝不能把整段 duration 记到一个日期。
    /// 被"僵尸清理"提前收尾的会话按 <c>last_heartbeat</c> 截断（与它写进 daily 的口径一致）。
    ///
    /// <para>
    /// ⚠️ 2026-10-11：必须**跳过被作废的会话分片**（<c>daily_play_exclusions</c>）——
    /// 用户删除某会计日记录后，该日上那条会话的分片不再计入；同一会话在**别的**会计日的分片
    /// 不受影响（这是"按日删除"能与"整段会话"共存的关键）。
    /// 凡绕过本方法自己循环 <c>ComputeDailyDeltas</c> 的实现，都必须自行应用同一份排除集。
    /// </para>
    /// </summary>
    internal static int SumSessionDerivedSeconds(
        SqliteConnection conn, SqliteTransaction? tx, long gameId, string statisticalDate, int cutoffHour)
    {
        var excluded = LoadExcludedSessionGlobalIds(conn, tx, gameId, statisticalDate);

        // ⚠️ 只读 canonical 事实：`sessions.duration_seconds` 是秒数唯一来源，
        //    结束时刻由 `start_time + duration_seconds` 推出。
        //    这样本机（心跳/结束）、远端落地、重算三条路径对**同一份 Session facts**
        //    必然得到完全相同的统计日分片（不再依赖 end_time/last_heartbeat 的墙上时间，
        //    那会在远端落地或不一致的历史行上算出 0）。
        var sessions = conn.Query<SessionSpanRow>(
            """
            SELECT start_time AS StartTime, duration_seconds AS DurationSeconds,
                   COALESCE(global_id, '') AS GlobalId
            FROM sessions
            WHERE game_id = @gameId;
            """,
            new { gameId }, tx);

        var total = 0;
        foreach (var s in sessions)
        {
            // 该会话在本会计日的分片已被用户显式删除 ⇒ 不计入（其他日期不受影响）。
            if (excluded.Count > 0 && !string.IsNullOrEmpty(s.GlobalId) && excluded.Contains(s.GlobalId)) continue;

            if (!TryParseWallClock(s.StartTime, out var start)) continue;

            var span = Math.Max(0, s.DurationSeconds);
            if (span <= 0) continue;

            var end = start.AddSeconds(span);

            foreach (var delta in GameTimeTracker.Core.Services.GameSessionManager.ComputeDailyDeltas(
                         start, end, span, cutoffHour))
            {
                if (string.Equals(delta.Date, statisticalDate, StringComparison.Ordinal))
                {
                    total += delta.DurationSeconds;
                }
            }
        }

        return total;
    }

    internal static bool TryParseWallClock(string? text, out DateTime value)
        => DateTime.TryParseExact(
            text, "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out value);

    private sealed class SessionSpanRow
    {
        public string? StartTime { get; set; }
        public int DurationSeconds { get; set; }
        public string GlobalId { get; set; } = "";
    }
}

/// <summary>日时长调整台账行（读模型）。</summary>
public sealed class DailyAdjustmentRow
{
    public string AdjustmentGlobalId { get; set; } = "";
    public long GameId { get; set; }
    public string StatisticalDate { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public int DurationSeconds { get; set; }
    public string Source { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}
