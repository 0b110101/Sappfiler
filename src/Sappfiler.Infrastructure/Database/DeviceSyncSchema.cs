using System.Globalization;
using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// 多设备同步的表结构与一次性迁移（Phase 1）。
///
/// **设计约束**（本分支要与 `feature/multi-backend-sync` 共存、将来都合并进 main）：
///   · 所有 DDL 集中在**本文件**，<c>SqliteRepository.InitializeDatabase()</c> 里只加**一行调用** ——
///     把"两边都改同一个方法"的冲突面压缩到那一行。
///   · 只做**追加**：不重建表、不改主键、不改现有列的语义、不动 daily_summary。
///   · 全部语句幂等，可重复执行；任何失败都**不得影响现有本地功能**。
///
/// **本阶段不做的事**：不同步 daily_summary、不碰 Cloudflare/WebDAV/UI、不写任何网络代码。
/// </summary>
internal static class DeviceSyncSchema
{
    /// <summary>
    /// 在 <c>InitializeDatabase()</c> 末尾调用（此时 games/sessions/settings/... 已建好）。
    /// </summary>
    public static void Ensure(SqliteConnection conn)
    {
        try
        {
            EnsureSyncTables(conn);

            // migration 016（2026-10-12）：**专用重新激活意图时间戳** games.reactivated_at_utc。
            //
            // ⚠️ 刻意放在本方法**最前面**（编号不代表落地先后，见 migration 004 的注记）：
            //    临界写路径（GetOrCreateGameAsync 写入 / 远端 Delete 仲裁）会直接引用该列，
            //    而 Ensure 整体包在一个 try/catch 里 —— 任何**更早**步骤失败都会静默跳过后续步骤。
            //    放在最前可保证"只要 schema_migrations 存在就一定会尝试建列"，
            //    把"列缺失导致计时写失败"的风险降到最低。
            //
            // 背景（真机验收发现的仲裁缺陷）：远端 Game Delete 的"本机更新意图优先"判据原先
            // 直接复用 games.updated_at，而**监听循环每 5 秒**都会经 GetOrCreateGameAsync 刷新该字段
            // ⇒ 只要游戏在本机运行着，updated_at 就永远"很新"，任何更早的远端 Delete 都无法归档它；
            // 该 Delete 的游标已越过、不会重发 ⇒ 两端状态可能长期分歧。
            //
            // 修复：意图归意图、心跳归心跳。本列**只在真正 archived→active 时**写入，
            // 后台心跳一律不碰 ⇒ 仲裁不再受监听循环影响。
            if (!IsMigrationApplied(conn, 16))
            {
                EnsureGameReactivationTimestamp(conn);
            }

            EnsureSessionsSyncColumns(conn);

            // migration 002：games.global_id。
            // DDL 的主要调用点其实在「启动去重**之前**」（见 SqliteRepository.InitializeDatabase），
            // 这里再调一次只是保证幂等完备 —— 将来那行若被挪走，也不会漏建列。
            EnsureGamesIdentityColumn(conn);
            BackfillGameSyncIdentity(conn);

            // migration 003：Deferred 台账补 payload/operation/device_id + resolved 状态（2e 重处理的输入）。
            EnsureDeferredReprocessingColumns(conn);

            EnsureLocalDevice(conn);

            // migration 005（schema v2）：日时长调整表 + 一次性历史差值折算。
            // ⚠️ 必须在 EnsureLocalDevice 之后（要靠本机 device_id 生成稳定 identity）。
            EnsureDailyDurationAdjustments(conn);

            // migration 006（schema v2）：内容寻址的封面/图标 blob（远端 {根}/blobs/<sha256>）。
            EnsureBlobTables(conn);

            // migration 007（2026-10-09）：历史调整的语义分类（historical_residual / notion_external）。
            EnsureHistoricalAdjustmentSources(conn);

            // migration 009（2026-10-11）：修复 migration 008 误把类型当列名造成的真实库缺列
            // （observed_remote_seconds 从未存在 ⇒ Notion 同步 41/41 全失败）。
            EnsureDailyAdjustmentSnapshotColumnRepair(conn);

            // migration 010（2026-10-11）：games 物理删除守卫（禁止级联丢历史）。
            if (!IsMigrationApplied(conn, 10))
            {
                EnsureGamesPhysicalDeleteGuard(conn);
            }

            // migration 011（2026-10-11）：守卫扩展 —— 把「只有 Adjustment 的 games 行」也纳入。
            if (!IsMigrationApplied(conn, 11))
            {
                EnsureGamesPhysicalDeleteGuardWithAdjustments(conn);
            }

            // migration 012（2026-10-11）：场次迁移台账（count-rule-v1）。
            if (!IsMigrationApplied(conn, 12))
            {
                EnsureDailyCountMigrationLedger(conn);
            }

            // migration 013（2026-10-11）：台账补 signals 列（诊断信号与迁移动作分离后需要留痕）。
            //
            // ⚠️ 升级路径：已跑过 012 的库（台账表已存在、已有历史行）**不会**重跑 012，
            //    这里只做一次幂等 ADD COLUMN，既有行保持原样（signals 为 NULL，读取侧用 COALESCE 兜底）。
            if (!IsMigrationApplied(conn, 13))
            {
                AddColumnIfMissing(conn, "daily_count_migrations", "signals", "TEXT");

                // 🛡️ 只有列**确实存在**才登记 13：AddColumnIfMissing 会吞掉 ALTER 失败（只记 Warn），
                //    若无条件登记就会出现"迁移号已应用、列却不存在"的静默状态 —— 之后写 signals 必失败。
                if (ColumnExists(conn, "daily_count_migrations", "signals"))
                {
                    RegisterMigration(conn, 13);
                }
                else
                {
                    AppLog.Warn("[设备同步] daily_count_migrations.signals 缺失，migration 013 不登记（下次启动重试）");
                }
            }

            // migration 014（2026-10-11）：**身份映射归一化**。
            //
            // 旧版本可能留下反向/环状映射（A: gB→gA 与 B: gA→gB 互相传播），以及
            // "canonical 指向别处"的非法行。这里按确定性裁决（等价类内最小 global_id）把每个
            // 连通分量重写成**星形**，删除非法行，并把真正变化的映射入队传播。
            //
            // ⚠️ 只改 sync_identity_supersessions 与 sync_queue，**不触碰任何游玩事实**
            //    （会话 / 声明 / 日统计一个字节都不动）；无 DDL ⇒ 旧库天然兼容、可重复执行。
            if (!IsMigrationApplied(conn, 14))
            {
                try
                {
                    var rewrittenMappings = SqliteRepository.NormalizeGameIdentityMappingsCore(conn);
                    if (rewrittenMappings > 0)
                    {
                        AppLog.Info($"[设备同步] migration 014：归一化 {rewrittenMappings} 条身份映射"
                            + "（canonical = 等价类内最小 identity，已入队传播）");
                    }

                    RegisterMigration(conn, 14);
                }
                catch (Exception ex)
                {
                    // 归一化失败不能阻止程序启动；下次启动会重试（幂等）。
                    AppLog.Warn($"[设备同步] migration 014（身份映射归一化）失败，本次跳过：{ex.Message}");
                }
            }

            // migration 015（2026-10-11）：**每日游玩分片排除事实**表。
            //
            // 背景：历史页面的"删除一条 (游戏, 会计日) 记录"必须删掉**该会计日的时长分片**，
            // 而 sessions 是整段事实、其秒数按结算点拆到最多两个会计日 ⇒ 删除粒度无法用
            // "整条 Session 墓碑"表达（会连带删掉另一个会计日的有效时长）。
            // 本表是这条删除意图的持久化事实，并作为新的同步实体跨设备传播。
            //
            // ⚠️ 只**追加**一张新表（不改任何现有表/列/数据）：旧库升级时表为空 ⇒ 统计口径不变。
            EnsureDailyPlayExclusions(conn);
        }
        catch (Exception ex)
        {
            // 迁移失败绝不能让程序起不来：同步是附加能力，本地计时/统计必须照常工作。
            AppLog.Error("[设备同步] 表结构初始化失败，本次跳过（本地功能不受影响）", ex);
        }
    }

    /// <summary>
    /// migration 015（2026-10-11）：<c>daily_play_exclusions</c> —— **每日游玩分片排除事实**。
    ///
    /// <para>
    /// 一行 = "某条会话（<c>session_global_id</c>）在某个会计日（<c>statistical_date</c>）上的
    /// 时长分片不计入统计"。统计的唯一实现（<c>SumSessionDerivedSeconds</c> 与场次口径）
    /// 会跳过这些分片；删除之后**新产生**的会话（新 global_id）永不受影响。
    /// </para>
    ///
    /// <para>幂等：<c>CREATE TABLE IF NOT EXISTS</c>；只改结构、不碰任何数据行。</para>
    /// </summary>
    private static void EnsureDailyPlayExclusions(SqliteConnection conn)
    {
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS daily_play_exclusions (
                exclusion_global_id TEXT PRIMARY KEY,
                game_id             INTEGER NOT NULL,
                statistical_date    TEXT NOT NULL,
                session_global_id   TEXT NOT NULL,
                device_id           TEXT NOT NULL,
                created_at_utc      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_daily_play_exclusions_game_date
                ON daily_play_exclusions(game_id, statistical_date);
            CREATE INDEX IF NOT EXISTS idx_daily_play_exclusions_session
                ON daily_play_exclusions(session_global_id);
            """);

        RegisterMigration(conn, 15);
    }

    /// <summary>
    /// migration 016（2026-10-12）：<c>games.reactivated_at_utc</c> —— <b>专用重新激活意图时间戳</b>。
    ///
    /// <para>
    /// 语义：**用户把一款已删除（archived）的游戏重新启动**的那一刻（UTC，ISO8601 带 <c>Z</c>，
    /// 与 <c>created_at_utc</c> / <c>deleted_at_utc</c> 同一个 <c>UtcNowIso()</c> 约定）。
    /// 它是"本机较新意图"的唯一判据，供远端 Game Delete 的仲裁使用。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 与 <c>updated_at</c> 的分工（**这是本次修复的核心**）：
    /// <list type="bullet">
    ///   <item><c>updated_at</c> = "最后一次被任何写路径碰过"，**后台监听循环每 5 秒就会刷新它**，
    ///         因此**绝不能**用它做重新激活/删除冲突的仲裁；</item>
    ///   <item><c>reactivated_at_utc</c> = "用户最后一次真正重新激活"，只有真实状态转换才写、
    ///         本机删除时清空、后台心跳绝不触碰。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 升级兼容：<c>NULL</c> 表示"从未重新激活过"，判据侧一律按 <c>1970-01-01</c> 处理
    /// ⇒ 等价于修复前的既有行为（无条件归档），**绝不因为缺值而削弱删除语义**。
    /// 旧库升级后全为 NULL ⇒ 行为与升级前逐字一致。
    /// </para>
    ///
    /// <para>幂等：<c>ADD COLUMN</c> 前先判存在；只改结构、不碰任何数据行。</para>
    /// </summary>
    private static void EnsureGameReactivationTimestamp(SqliteConnection conn)
    {
        // games 表由 SqliteRepository.InitializeDatabase 先建好（本方法在其末尾执行）。
        // 极端情况（合成 schema / 表尚未建立）⇒ 不登记迁移、下次启动重试，绝不抛。
        if (conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'games';") == 0)
        {
            AppLog.Warn("[设备同步] games 表尚不存在，migration 016 不登记（下次启动重试）");
            return;
        }

        AddColumnIfMissing(conn, "games", "reactivated_at_utc", "TEXT");

        // 与 migration 013 同一纪律：只有列**确实存在**才登记，
        // 否则会出现"迁移号已应用、列却不存在"的静默状态 —— 之后 GetOrCreateGameAsync 必失败。
        if (ColumnExists(conn, "games", "reactivated_at_utc"))
        {
            RegisterMigration(conn, 16);
        }
        else
        {
            AppLog.Warn("[设备同步] games.reactivated_at_utc 缺失，migration 016 不登记（下次启动重试）");
        }
    }

    /// <summary>
    /// 启动**去重之前**必须就绪的部分：同步元数据表（去重要写 <c>sync_tombstones</c>）、
    /// <c>games.global_id</c>（去重要读它来决定 identity 是继承还是丢弃）、
    /// 本机设备身份（墓碑要记 <c>device_id</c>）。
    ///
    /// ⚠️ 为什么单独开一个入口：默认的 <see cref="Ensure"/> 跑在去重**之后**，而：
    ///   · 去重需要读 <c>games.global_id</c> —— 列不存在时 Dapper 的 <c>SELECT *</c> 会**静默忽略**
    ///     （不抛异常）→ 继承/墓碑逻辑无声失效，日志里也看不出来；
    ///   · 去重需要写 <c>sync_tombstones</c> —— 表不存在会直接抛异常 → 整次去重被外层 catch 跳过。
    ///
    /// 全部幂等，且**失败不抛**：返回 false 表示同步身份基础设施没准备好，
    /// 此时去重必须退回"纯旧行为"（不继承 global_id、不写墓碑），绝不能让去重本身失效。
    /// </summary>
    public static bool EnsurePrerequisitesForDeduplication(SqliteConnection conn)
    {
        try
        {
            EnsureSyncTables(conn);
            EnsureGamesIdentityColumn(conn);
            EnsureLocalDevice(conn);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 去重前置的同步基础设施未就绪，本次不处理 identity/墓碑（去重照常进行）：{ex.Message}");
            return false;
        }
    }

    /// <summary>创建同步元数据表（全部 IF NOT EXISTS，很轻，每次启动都跑没负担）。</summary>
    private static void EnsureSyncTables(SqliteConnection conn)
    {
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version        INTEGER PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS devices (
                device_id        TEXT PRIMARY KEY,
                device_name      TEXT NOT NULL DEFAULT 'Windows Computer',
                platform         TEXT NOT NULL DEFAULT 'windows',
                app_version      TEXT NOT NULL DEFAULT '',
                created_at_utc   TEXT NOT NULL,
                last_seen_at_utc TEXT,
                last_sync_at_utc TEXT,
                status           INTEGER NOT NULL DEFAULT 0,
                is_this_device   INTEGER NOT NULL DEFAULT 0
            );

            -- Outbox：只存"还没成功送出去"的变更；收到 ACK 后置 completed 并定期清理。
            -- payload 是**变更发生那一刻的快照**，不是推送时再去业务表读。
            CREATE TABLE IF NOT EXISTS sync_queue (
                queue_id          TEXT PRIMARY KEY,
                entity_type       TEXT NOT NULL,
                entity_global_id  TEXT NOT NULL,
                operation         INTEGER NOT NULL,
                base_version      INTEGER NOT NULL DEFAULT 0,
                payload           TEXT NOT NULL DEFAULT '',
                created_at_utc    TEXT NOT NULL,
                retry_count       INTEGER NOT NULL DEFAULT 0,
                next_retry_at_utc TEXT,
                status            INTEGER NOT NULL DEFAULT 0,
                last_error        TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_sync_queue_status ON sync_queue(status, next_retry_at_utc);
            CREATE INDEX IF NOT EXISTS idx_sync_queue_entity ON sync_queue(entity_type, entity_global_id);

            -- 删除墓碑：独立于 deleted_archive（后者是本机删除审计，语义不同）。
            CREATE TABLE IF NOT EXISTS sync_tombstones (
                tombstone_id      TEXT PRIMARY KEY,
                entity_type       TEXT NOT NULL,
                entity_global_id  TEXT NOT NULL,
                device_id         TEXT NOT NULL,
                deleted_at_utc    TEXT NOT NULL,
                created_at_utc    TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_sync_tombstones_entity
                ON sync_tombstones(entity_type, entity_global_id);

            -- Identity Supersession（2e 冻结语义）：表达"这个身份**等同于**那个身份"。
            --
            -- ⚠️ 与 sync_tombstones（= 这个实体被**删除**了）严格区分，**两者不得混用**：
            --      真删除      → sync_tombstones（对端收到后删除本地副本）
            --      身份合并    → sync_identity_supersessions（对端收到后把引用**重定向**到 canonical）
            --    用墓碑表达身份合并，会让对端把"合并"误解成"删除"从而丢数据。
            --    → 删除与身份合并在数据模型和同步语义上必须严格区分（用户 2026-10-03 裁定）。
            CREATE TABLE IF NOT EXISTS sync_identity_supersessions (
                supersession_id      TEXT PRIMARY KEY,
                entity_type          TEXT NOT NULL,
                superseded_global_id TEXT NOT NULL,
                canonical_global_id  TEXT NOT NULL,
                device_id            TEXT NOT NULL,
                reason               TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_sync_supersede_entity
                ON sync_identity_supersessions(entity_type, superseded_global_id);

            -- 每个 Backend 独立维护游标（切换后端时绝不复用）。
            CREATE TABLE IF NOT EXISTS sync_state (
                backend          TEXT NOT NULL,
                account_id       TEXT NOT NULL,
                cursor           TEXT NOT NULL DEFAULT '',
                last_sync_at_utc TEXT,
                last_error       TEXT,
                PRIMARY KEY (backend, account_id)
            );

            -- Deferred 台账（2c 冻结语义）：远端变更"本轮没落地、但游标已经越过它"时留痕。
            -- ⚠️ Deferred ≠ Completed。游标前进只说明"服务端那一批消费过了"，
            -- 不代表这条数据在本机同步成功；只计数不落库就等于静默丢数据。
            -- 这里保留足够信息让后续阶段（2e：身份/冲突解决）能重新处理。
            CREATE TABLE IF NOT EXISTS sync_deferred_changes (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                change_id         TEXT NOT NULL,
                backend           TEXT NOT NULL DEFAULT '',
                entity_type       TEXT NOT NULL,
                entity_global_id  TEXT NOT NULL,
                reason            TEXT NOT NULL,
                schema_version    INTEGER NOT NULL DEFAULT 0,
                first_seen_at_utc TEXT NOT NULL,
                last_seen_at_utc  TEXT NOT NULL,
                seen_count        INTEGER NOT NULL DEFAULT 1
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_sync_deferred_change
                ON sync_deferred_changes(change_id);
            CREATE INDEX IF NOT EXISTS idx_sync_deferred_entity
                ON sync_deferred_changes(entity_type, entity_global_id);
            """);

        // migration 004：Identity Supersession 台账（2e 冻结语义）。
        // ⚠️ 它先于 migration 003 落地，是因为 **2b 的语义修正依赖它**
        //    （去重淘汰的重复行要从"写墓碑"改成"写 supersession"）。
        //    编号只是登记用，不代表落地先后。
        RegisterMigration(conn, 4);
    }

    /// <summary>
    /// migration 005（schema v2）：<c>daily_duration_adjustments</c> —— **日时长调整**。
    ///
    /// <para>
    /// 背景：<c>daily_summary.duration_seconds</c> 一直是"会话派生 + 手工/Notion 录入"的**混合值**，
    /// 库里没有任何字段能把两者分开 ⇒ 手工时长根本无法跨设备同步（对端只会把它当成会话总时长，
    /// 于是不是重复累加就是被 max 掉）。v2 把"手工/Notion 那一部分"提升成**独立不可变事实**：
    /// <code>
    /// daily_summary(game, 统计日) = Σ 会话派生秒数 + Σ adjustments(该 game 该日, **全部设备相加**)
    /// </code>
    /// </para>
    ///
    /// <para>
    /// ⚠️ 迁移只 **INSERT adjustment 行**，绝不改 <c>daily_summary</c> ——
    /// 那部分时长已经在 daily 里了，再加一次就是双倍。重跑幂等：identity 稳定 +
    /// <c>ON CONFLICT DO NOTHING</c>；且**整段折算只跑一次**（由 <c>schema_migrations</c> 守卫）——
    /// 否则"从对端收到的 adjustment 已经加进 daily"之后会被再折算成一条本机事实，回推造成双计。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 跨统计日会话：差值里的"会话派生部分"必须用与运行时**同一份**跨日拆分口径
    /// （<see cref="GameTimeTracker.Core.Services.GameSessionManager.ComputeDailyDeltas"/>）
    /// 逐会话按会计日折算，**绝不能**把一条跨日会话的整段 duration 记到某一个日期上。
    /// </para>
    /// </summary>
    private static void EnsureDailyDurationAdjustments(SqliteConnection conn)
    {
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS daily_duration_adjustments (
                adjustment_global_id TEXT PRIMARY KEY,
                game_id              INTEGER NOT NULL,
                statistical_date     TEXT NOT NULL,
                device_id            TEXT NOT NULL,
                duration_seconds     INTEGER NOT NULL,
                source               TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL,
                -- migration 008（2026-10-10）：**上次观察到的远端声明秒数**（外部声明变更检测用）。
                -- NULL = 还没有可靠快照（旧库、或尚未成功解析过该声明）⇒ 一律按"未观察到变化"处理。
                observed_remote_seconds INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_daily_adjustments_game_date
                ON daily_duration_adjustments(game_id, statistical_date);
            """);

        if (!IsMigrationApplied(conn, 5))
        {
            var migrated = MigrateLegacyDailyAdjustments(conn);
            if (migrated > 0)
            {
                AppLog.Info($"[设备同步] v2 迁移：已把 {migrated} 条历史手工/Notion 日时长折算为 DailyAdjustment（daily_summary 未改动）");
            }
        }

        RegisterMigration(conn, 5);

        // migration 008（2026-10-10）：外部声明变更检测的快照列。
        // ⚠️ 快照**按声明身份**存储（同一行的 game/日期/来源天然绑定），因此不可能串用别的声明；
        //    NULL 表示"还没有可靠快照"，判定侧一律按"未观察到变化"处理（保持既有本地领先保护）。
        // ⚠️ 2026-10-11：本行曾因为 AddColumnIfMissing 的 DDL 拼接缺陷在真实库建出名为 INTEGER 的列
        //    （真列 observed_remote_seconds 从未存在）；API 已修，误建列由 migration 009 清理。
        AddColumnIfMissing(conn, "daily_duration_adjustments", "observed_remote_seconds", "INTEGER");
        RegisterMigration(conn, 8);
    }

    /// <summary>
    /// migration 009（2026-10-11 事故修复）：确保 <c>daily_duration_adjustments.observed_remote_seconds</c> 真实存在。
    ///
    /// <para>
    /// 实机证据：旧 <c>AddColumnIfMissing</c> 把第 4 个参数（类型）直接拼进 <c>ADD COLUMN</c>，
    /// 于是真实库得到的是一列**名为 <c>INTEGER</c>**、无类型、全 NULL 的列，而
    /// <c>observed_remote_seconds</c> 从未被建出来。后果是 <c>SyncDailyRecordFromNotionAsync</c>
    /// 每一条都抛 <c>no such column</c>（真实日志：<c>已从每日时长表同步 0 条记录，41 条失败（… no such column: observed_remote_seconds）</c>）
    /// ⇒ Notion 手工录入的数据进不了本地，自然也不可能同步到另一台设备。
    /// </para>
    ///
    /// <para>
    /// 本迁移幂等、可重复运行，且**只改表结构、不碰任何一行数据**：
    /// ① 删掉误建的 <c>INTEGER</c> 列（仅当整列 NULL）；② 补齐真列。老库/新库都能自愈。
    /// </para>
    /// </summary>
    private static void EnsureDailyAdjustmentSnapshotColumnRepair(SqliteConnection conn)
    {
        DropMislabelledIntegerColumn(conn);
        AddColumnIfMissing(conn, "daily_duration_adjustments", "observed_remote_seconds", "INTEGER");
        RegisterMigration(conn, 9);
    }

    /// <summary>
    /// migration 010（2026-10-11，A/B 实机对差根因）：<b>games 物理删除守卫</b>。
    ///
    /// <para>
    /// 背景：<c>sessions.game_id</c> / <c>daily_summary.game_id</c> 声明了
    /// <c>REFERENCES games(id) ON DELETE CASCADE</c>，且每个连接都开 <c>PRAGMA foreign_keys=ON</c>。
    /// 历史版本用**裸 <c>DELETE FROM games</c>** 删除游戏（见 <c>DeleteGameAsync</c> 的注释自证），
    /// 于是真实历史会话被连带物理抹掉 —— 实测 A 端 Ficket(789s)/Bad House Demo(482s+5s) 的会话
    /// 就是这么消失的，而另一端仍保留，造成两端事实永久分裂。
    /// </para>
    ///
    /// <para>
    /// 本迁移加一个 <c>BEFORE DELETE</c> 触发器：**只要该 games 行还有会话或每日统计，就 ABORT**，
    /// 强制所有删除走"归档 + 删除墓碑"（<c>DeleteGameAsync</c>）。
    /// 兼容既有合法物理删除路径：启动去重 / 2e 身份合并在删行前已把
    /// <c>sessions</c> + <c>daily_summary</c> + <c>daily_duration_adjustments</c> 搬到 canonical（无历史 ⇒ 放行）；
    /// 幽灵行清理本身就要求"无 daily 且无 session"（放行）。
    /// </para>
    ///
    /// <para>幂等：<c>CREATE TRIGGER IF NOT EXISTS</c>；只改结构、不碰任何数据行。</para>
    /// </summary>
    private static void EnsureGamesPhysicalDeleteGuard(SqliteConnection conn)
    {
        conn.Execute(
            """
            CREATE TRIGGER IF NOT EXISTS games_no_physical_delete_with_history
            BEFORE DELETE ON games
            WHEN EXISTS (SELECT 1 FROM sessions WHERE game_id = OLD.id)
              OR EXISTS (SELECT 1 FROM daily_summary WHERE game_id = OLD.id)
            BEGIN
                SELECT RAISE(ABORT, 'games 行仍有历史事实，禁止物理删除：请改用归档 + 删除墓碑');
            END;
            """);

        RegisterMigration(conn, 10);
    }

    /// <summary>
    /// migration 011（2026-10-11）：<b>物理删除守卫扩展</b> —— 把 <c>daily_duration_adjustments</c>
    /// 也计入"历史事实"。
    ///
    /// <para>
    /// 事实核对（先查再改）：<c>daily_duration_adjustments.game_id</c> 是
    /// <c>INTEGER NOT NULL</c>，**没有 <c>REFERENCES</c>、没有外键**（见 <see cref="EnsureDailyDurationAdjustments"/>），
    /// 因此物理删除 <c>games</c> 行**不会**级联删除声明 —— 也就是说这里**不存在数据丢失路径**。
    /// 但物理删除会把声明变成**孤儿**（谁也认领不了、既不进统计也不参与同步，只能靠审计发现）：
    /// 实测库里那条 <c>game_id=165</c> 的 0 秒声明就是这么来的。
    /// </para>
    ///
    /// <para>
    /// 因此这一步的目的不是"防丢数据"，而是**把事实表的生命周期也统一到"归档 + 墓碑"**：
    /// 只要该 games 行还挂着会话、每日统计**或声明**，就禁止物理删除。
    /// 兼容既有合法清理路径：身份合并 / 启动去重会先把 sessions + daily_summary +
    /// <c>daily_duration_adjustments</c> 全部搬到 canonical（无历史 ⇒ 放行）；
    /// 幽灵行清理要求"三张事实表都没有记录"（放行）。
    /// </para>
    ///
    /// <para>
    /// <b>重复执行安全</b>：先 <c>DROP TRIGGER IF EXISTS</c> 再 <c>CREATE TRIGGER</c>，
    /// 即使 <c>schema_migrations</c> 里已经登记过 011（或该行丢失）也不会出错，且不会留下重复触发器。
    /// </para>
    /// </summary>
    private static void EnsureGamesPhysicalDeleteGuardWithAdjustments(SqliteConnection conn)
    {
        conn.Execute(
            """
            DROP TRIGGER IF EXISTS games_no_physical_delete_with_history;

            CREATE TRIGGER games_no_physical_delete_with_history
            BEFORE DELETE ON games
            WHEN EXISTS (SELECT 1 FROM sessions WHERE game_id = OLD.id)
              OR EXISTS (SELECT 1 FROM daily_summary WHERE game_id = OLD.id)
              OR EXISTS (SELECT 1 FROM daily_duration_adjustments WHERE game_id = OLD.id)
            BEGIN
                SELECT RAISE(ABORT, 'games 行仍有历史事实（会话/每日统计/时长声明），禁止物理删除：请改用归档 + 删除墓碑');
            END;
            """);

        RegisterMigration(conn, 11);
    }

    /// <summary>
    /// migration 012（2026-10-11）：<b>场次迁移台账</b> <c>daily_count_migrations</c>。
    ///
    /// <para>
    /// 旧场次计数（count-rule-v1 之前的 manualBonus / 默认值残留）迁移时必须可追溯：
    /// 记录规则版本、清单指纹、前后的场次与秒数、分类与原因、应用时间。
    /// 秒数**不参与**迁移（before/after seconds 相同，仅为对账留痕）。
    /// </para>
    ///
    /// <para>幂等：<c>CREATE TABLE IF NOT EXISTS</c>；只建结构，不碰任何数据行。</para>
    /// </summary>
    private static void EnsureDailyCountMigrationLedger(SqliteConnection conn)
    {
        conn.Execute(
            """
            CREATE TABLE IF NOT EXISTS daily_count_migrations (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                rule_version   TEXT NOT NULL,
                plan_hash      TEXT NOT NULL,
                game_id        INTEGER NOT NULL,
                game_global_id TEXT,
                date           TEXT NOT NULL,
                category       TEXT NOT NULL,
                before_count   INTEGER NOT NULL,
                after_count    INTEGER NOT NULL,
                before_seconds INTEGER NOT NULL,
                after_seconds  INTEGER NOT NULL,
                reason         TEXT,
                applied_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_daily_count_migrations_plan ON daily_count_migrations(plan_hash);
            CREATE INDEX IF NOT EXISTS idx_daily_count_migrations_date ON daily_count_migrations(date, game_id);
            """);

        RegisterMigration(conn, 12);
    }

    /// <summary>
    /// 删除 migration 008 误建的、名为 <c>INTEGER</c> 的列。
    ///
    /// ⚠️ 只在"列存在且整列为 NULL"时删除：一旦有人往里写过数据就停手并记 Warn
    /// （宁可留一个冗余列，也绝不丢数据）。若运行环境的 SQLite 不支持 DROP COLUMN 同样只记 Warn ——
    /// 冗余列不影响任何查询：库内对该表的语句全部使用显式列名，没有 <c>SELECT *</c>。
    /// </summary>
    private static void DropMislabelledIntegerColumn(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "daily_duration_adjustments", "INTEGER")) return;

        var nonNull = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM daily_duration_adjustments WHERE \"INTEGER\" IS NOT NULL;");

        if (nonNull > 0)
        {
            AppLog.Warn($"[设备同步] 误建列 daily_duration_adjustments.\"INTEGER\" 有 {nonNull} 行非空数据 —— 不删除，需人工确认");
            return;
        }

        try
        {
            conn.Execute("ALTER TABLE daily_duration_adjustments DROP COLUMN \"INTEGER\";");
            AppLog.Info("[设备同步] 已清理 migration 008 误建的冗余列 daily_duration_adjustments.\"INTEGER\"（整列为 NULL）");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 清理误建列失败（冗余列无害，不影响同步）：{ex.Message}");
        }
    }

    /// <summary>
    /// migration 006（schema v2）：**内容寻址**的封面/图标 blob。
    ///
    /// <para>
    /// 远端布局：<c>{同步目录}/blobs/&lt;sha256&gt;</c>（二进制，独立通道）。
    /// JSON batch 里**只保存 hash 引用**，绝不嵌入 Base64 图片 —— 否则 batch 会随图片体积膨胀，
    /// 而且"同一张图被两个游戏引用"会被存两份。内容寻址天然保证"相同内容只存一份"。
    /// </para>
    ///
    /// <para>
    /// <see cref="sync_blobs"/> = 本机已知道内容的 blob（含上传状态，供幂等上传）；
    /// <see cref="sync_blob_refs"/> = 实体 → blob 的引用（含"待补齐 / 可重试"状态）：
    /// 下载失败**只**改这张表，绝不影响 Game/Session/Adjustment 的事实落地。
    /// </para>
    /// </summary>
    private static void EnsureBlobTables(SqliteConnection conn)
    {
        conn.Execute("""
            -- 本机已知内容的 blob（内容寻址；uploaded=0 ⇒ 还没推到远端）
            CREATE TABLE IF NOT EXISTS sync_blobs (
                sha256          TEXT PRIMARY KEY,
                size            INTEGER NOT NULL,
                local_path      TEXT,
                uploaded        INTEGER NOT NULL DEFAULT 0,
                upload_attempts INTEGER NOT NULL DEFAULT 0,
                last_error      TEXT,
                created_at_utc  TEXT NOT NULL
            );

            -- 实体 → blob 引用：status 0 = 待补齐（可重试），1 = 就绪（本地已有该内容）
            CREATE TABLE IF NOT EXISTS sync_blob_refs (
                entity_type      TEXT NOT NULL,
                entity_global_id TEXT NOT NULL,
                kind             TEXT NOT NULL,
                sha256           TEXT NOT NULL,
                status           INTEGER NOT NULL DEFAULT 0,
                attempts         INTEGER NOT NULL DEFAULT 0,
                last_error       TEXT,
                updated_at_utc   TEXT NOT NULL,
                PRIMARY KEY (entity_type, entity_global_id, kind)
            );
            CREATE INDEX IF NOT EXISTS idx_sync_blob_refs_pending ON sync_blob_refs(status, attempts);
            """);

        RegisterMigration(conn, 6);
    }

    /// <summary>
    /// 历史残差的**来源判定**（2026-10-09 用户裁定：不能笼统叫 <c>legacy</c>）。
    ///
    /// <para>规则（只用本库可自证的事实，绝不猜）：</para>
    /// <list type="number">
    ///   <item>该 (game, 统计日) **已有会话派生秒数** ⇒ 差额是"会话之外另行声明的时间" → <c>notion_external</c>
    ///         （真实案例：10/6 Halo 会话 4163s，Notion 侧另有 2029s）。</item>
    ///   <item>**没有任何会话**，且统计日**早于本库最早会话日** ⇒ 当时应用没有会话能力，值只可能来自 Notion
    ///         → <c>notion_external</c>（真实案例：9/13 Deathloop 7.78h，而最早会话是 9/17）。</item>
    ///   <item>其余（没有任何会话，但日期落在"有会话能力"的时间窗内）⇒ 会话曾存在、现已不可恢复
    ///         → <c>historical_residual</c>（真实案例：9/23 Deathloop 7860s / Helldivers 120s）。</item>
    /// </list>
    /// <para>
    /// ⚠️ 第 3 类里可能混进"用户其实是在 Notion 手填"的日子（例如 9/22 Deathloop 1h）——
    /// 本库无法区分，故一律标 <c>historical_residual</c> 并在报告里如实说明，绝不为了好看而猜成别的。
    /// </para>
    /// </summary>
    private static string ClassifyHistoricalAdjustmentSource(
        int sessionDerivedSeconds, string statisticalDate, string? earliestSessionDate)
    {
        if (sessionDerivedSeconds > 0) return DailyAdjustmentSources.NotionExternal;

        if (!string.IsNullOrWhiteSpace(earliestSessionDate)
            && string.CompareOrdinal(statisticalDate, earliestSessionDate) < 0)
        {
            return DailyAdjustmentSources.NotionExternal;
        }

        return DailyAdjustmentSources.HistoricalResidual;
    }

    /// <summary>
    /// migration 007（2026-10-09 裁定）：把 v2 早期只标了 <c>legacy</c> 的历史调整，
    /// 按 <see cref="ClassifyHistoricalAdjustmentSource"/> 改写成
    /// <c>historical_residual</c> / <c>notion_external</c>。
    ///
    /// <para>
    /// ⚠️ **只改 <c>source</c> 与随之变化的 identity（identity 里含 source），绝不改 duration** ——
    /// 用户明确要求"不重新解释历史数据"。这些行从未被播种过（未入队、未推送），所以换 identity 不影响任何对端。
    /// 幂等：只处理 <c>source = 'legacy'</c> 的行；且目标 identity 已存在时跳过（绝不覆盖别人的事实）。
    /// </para>
    /// </summary>
    private static void EnsureHistoricalAdjustmentSources(SqliteConnection conn)
    {
        var deviceId = conn.ExecuteScalar<string?>(
            "SELECT value FROM settings WHERE key = @key;",
            new { key = DeviceSyncConstants.SettingKeyDeviceId });

        var earliestSessionDate = conn.ExecuteScalar<string?>(
            """
            SELECT MIN(substr(start_time, 1, 10)) FROM sessions
            WHERE NULLIF(TRIM(COALESCE(start_time, '')), '') IS NOT NULL;
            """);

        var cutoffHour = SqliteRepository.ReadDailyCutoffHour(conn, null);

        var legacyRows = conn.Query<LegacyAdjustmentRow>(
            """
            SELECT game_id AS GameId, statistical_date AS Date,
                   adjustment_global_id AS GlobalId, device_id AS DeviceId
            FROM daily_duration_adjustments
            WHERE source = @legacy;
            """,
            new { legacy = DailyAdjustmentSources.Legacy }).ToList();

        var reclassified = 0;

        foreach (var row in legacyRows)
        {
            var gameGlobalId = conn.ExecuteScalar<string?>(
                "SELECT global_id FROM games WHERE id = @id;", new { id = row.GameId });

            if (string.IsNullOrWhiteSpace(gameGlobalId)) continue;

            var known = SqliteRepository.SumSessionDerivedSeconds(
                conn, null, row.GameId, row.Date, cutoffHour);

            var source = ClassifyHistoricalAdjustmentSource(known, row.Date, earliestSessionDate);
            var newGlobalId = DailyAdjustmentGlobalId.For(
                (string.IsNullOrWhiteSpace(row.DeviceId) ? deviceId : row.DeviceId) ?? string.Empty,
                gameGlobalId!,
                row.Date,
                source);

            if (string.Equals(newGlobalId, row.GlobalId, StringComparison.Ordinal)) continue;

            var collision = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
                new { id = newGlobalId });

            if (collision > 0) continue;   // 目标 identity 已有事实 → 停手，交给人看（不吞不覆盖）

            conn.Execute(
                """
                UPDATE daily_duration_adjustments
                SET adjustment_global_id = @newId, source = @source
                WHERE adjustment_global_id = @oldId;
                """,
                new { newId = newGlobalId, source, oldId = row.GlobalId });

            reclassified++;
        }

        if (reclassified > 0)
        {
            AppLog.Info($"[设备同步] v2 迁移：已把 {reclassified} 条历史调整按语义重新分类（historical_residual / notion_external，未改动任何时长）");
        }

        RegisterMigration(conn, 7);
    }

    private sealed class LegacyAdjustmentRow
    {
        public long GameId { get; set; }
        public string Date { get; set; } = "";
        public string GlobalId { get; set; } = "";
        public string? DeviceId { get; set; }
    }

    private static bool IsMigrationApplied(SqliteConnection conn, int version)
        => conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM schema_migrations WHERE version = @version;", new { version }) > 0;

    /// <summary>
    /// 一次性折算：<c>adjustment = max(0, daily.duration_seconds − Σ 会话派生秒数(该 game 该会计日))</c>。
    ///
    /// 只折算 **≥ 1 分钟** 的差值：跨日拆分与秒级截断会在两侧留下 1~2 秒噪声，
    /// 把噪声变成"会被跨设备相加的事实"会让统计缓慢漂移。
    /// 真实案例都远大于该阈值（9/23 Deathloop 7860s、Helldivers 120s、10/6 Halo ≈2029s）。
    /// </summary>
    private static int MigrateLegacyDailyAdjustments(SqliteConnection conn)
    {
        var deviceId = conn.ExecuteScalar<string?>(
            "SELECT value FROM settings WHERE key = @key;",
            new { key = DeviceSyncConstants.SettingKeyDeviceId });

        if (string.IsNullOrWhiteSpace(deviceId)) return 0;

        // 与运行时同一份结算口径读取（唯一实现）。
        var cutoffHour = SqliteRepository.ReadDailyCutoffHour(conn, null);

        var rows = conn.Query<LegacyDailyRow>("""
            SELECT ds.game_id AS GameId, ds.date AS Date, ds.duration_seconds AS DailySeconds,
                   g.global_id AS GameGlobalId
            FROM daily_summary ds
            JOIN games g ON g.id = ds.game_id
            WHERE ds.duration_seconds > 0
              AND NULLIF(TRIM(g.global_id), '') IS NOT NULL;
            """).ToList();

        if (rows.Count == 0) return 0;

        // 本库最早"有会话可查"的日期：早于它的统计日**不可能**是本机算出来的（那时应用还没有会话数据）。
        var earliestSessionDate = conn.ExecuteScalar<string?>(
            """
            SELECT MIN(substr(start_time, 1, 10)) FROM sessions
            WHERE NULLIF(TRIM(COALESCE(start_time, '')), '') IS NOT NULL;
            """);

        var inserted = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.GameGlobalId)) continue;

            // 会话派生秒数：**与运行时同一份**跨日拆分口径（唯一实现在仓储侧，绝不在这里重写一遍）。
            var known = SqliteRepository.SumSessionDerivedSeconds(conn, null, row.GameId, row.Date, cutoffHour);
            var delta = row.DailySeconds - known;

            if (delta < 60) continue;

            var source = ClassifyHistoricalAdjustmentSource(known, row.Date, earliestSessionDate);

            var globalId = DailyAdjustmentGlobalId.For(
                deviceId!, row.GameGlobalId!, row.Date, source);

            inserted += conn.Execute("""
                INSERT INTO daily_duration_adjustments
                    (adjustment_global_id, game_id, statistical_date, device_id, duration_seconds, source, created_at_utc)
                VALUES (@globalId, @gameId, @date, @deviceId, @duration, @source, @now)
                ON CONFLICT(adjustment_global_id) DO NOTHING;
                """,
                new
                {
                    globalId,
                    gameId = row.GameId,
                    date = row.Date,
                    deviceId,
                    duration = delta,
                    source,
                    now = UtcNowIso()
                });
        }

        return inserted;
    }

    private sealed class LegacyDailyRow
    {
        public long GameId { get; set; }
        public string Date { get; set; } = "";
        public int DailySeconds { get; set; }
        public string? GameGlobalId { get; set; }
    }

    /// <summary>登记一次迁移（幂等）。</summary>
    private static void RegisterMigration(SqliteConnection conn, int version)
    {
        conn.Execute("""
            INSERT INTO schema_migrations (version, applied_at_utc) VALUES (@version, @now)
            ON CONFLICT(version) DO NOTHING;
            """, new { version, now = UtcNowIso() });
    }

    /// <summary>
    /// 给 <c>sessions</c> 追加同步身份列。**不重建表、不改主键**（id 继续做本机内部键）。
    ///
    /// ⚠️ SQLite 的两条硬限制（照通常写法会直接失败，实测确认）：
    ///   1. <c>ALTER TABLE ADD COLUMN</c> **不能**加"NOT NULL 且无默认值"的列；
    ///   2. 也**不能**通过 ADD COLUMN 加 UNIQUE —— 唯一性只能在事后建 UNIQUE INDEX。
    ///   所以这里的做法是：列加成可空 → 回填 → 再建唯一索引；"非空"由应用层保证。
    /// </summary>
    private static void EnsureSessionsSyncColumns(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(sessions);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1)); // 列名
            }
        }

        void AddColumn(string name, string ddl)
        {
            if (existing.Contains(name)) return;
            try
            {
                conn.Execute($"ALTER TABLE sessions ADD COLUMN {ddl};");
            }
            catch (Exception ex)
            {
                // 与项目既有惯例一致：加列失败视为"已存在"，但留日志便于排查。
                AppLog.Warn($"[设备同步] 追加 sessions.{name} 失败（可能已存在）：{ex.Message}");
            }
        }

        AddColumn("global_id", "global_id TEXT");
        AddColumn("device_id", "device_id TEXT");
        AddColumn("version", "version INTEGER NOT NULL DEFAULT 1");
        AddColumn("deleted_at_utc", "deleted_at_utc TEXT");
        AddColumn("started_at_utc", "started_at_utc TEXT");
        AddColumn("ended_at_utc", "ended_at_utc TEXT");
        AddColumn("time_precision", "time_precision TEXT NOT NULL DEFAULT 'exact'");

        conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_sessions_global_id ON sessions(global_id);");

        BackfillSessionSyncFields(conn);
    }

    /// <summary>
    /// 一次性回填：给历史 sessions 生成 <c>global_id</c>、归属本机设备、并把本地时间串近似换算成 UTC。
    ///
    /// ⚠️ **准确性限制（必须如实告知用户，不要假装历史时间很准）**：
    ///   · 既有实现只保存"墙上时间"，**从未保存过时区信息**；这里只能按
    ///     <c>TimeZoneInfo.Local</c> 的**逐日期**偏移（含 DST 规则）反推 → 历史 UTC 是**近似值**。
    ///   · 因此历史行标记 <c>time_precision = 'estimated'</c>；迁移之后新写入的行才是 <c>'exact'</c>。
    ///   · 另外：现有 duration 是 5 秒心跳**增量累加**出来的，start/end 本身就不精确 ——
    ///     这是既有设计的性质，不是本次迁移引入的（心跳周期 5 秒保持不变）。
    ///
    /// 性能与自愈：**不用"只跑一次"的标记**，而是每次启动都只补"缺同步字段"的行。
    ///   · 查询条件是 <c>global_id IS NULL OR ...</c>，可以走 <c>idx_sessions_global_id</c>，
    ///     即使在几十万行的库上也只碰缺失的那部分；
    ///   · 更重要的是**自愈**：Phase 1 还没有接线业务写入路径，此期间新产生的会话
    ///     也会缺 global_id；被"一次性标记"跳过的话它们将**永远无法同步**。
    ///     （2026-10-03 就是被回归测试抓到的：标记置位后，构造仓储之后写入的会话补不上。）
    /// </summary>
    private static void BackfillSessionSyncFields(SqliteConnection conn)
    {
        var deviceId = EnsureLocalDevice(conn);

        var rows = conn.Query<LegacySessionRow>("""
            SELECT id                AS Id,
                   start_time        AS StartTime,
                   end_time          AS EndTime,
                   global_id         AS GlobalId
            FROM sessions
            WHERE global_id      IS NULL OR global_id      = ''
               OR device_id      IS NULL OR device_id      = ''
               OR started_at_utc IS NULL OR started_at_utc = ''
            """).ToList();

        if (rows.Count > 0)
        {
            using var tx = conn.BeginTransaction();
            foreach (var row in rows)
            {
                conn.Execute("""
                    UPDATE sessions
                    SET global_id      = @globalId,
                        device_id      = COALESCE(NULLIF(device_id, ''), @deviceId),
                        started_at_utc = COALESCE(NULLIF(started_at_utc, ''), @startedUtc),
                        ended_at_utc   = CASE
                                             WHEN NULLIF(ended_at_utc, '') IS NULL AND @endedUtc IS NOT NULL
                                             THEN @endedUtc
                                             ELSE ended_at_utc
                                         END,
                        time_precision = @precision
                    WHERE id = @id;
                    """,
                    new
                    {
                        globalId = string.IsNullOrWhiteSpace(row.GlobalId) ? NewId() : row.GlobalId,
                        deviceId,
                        startedUtc = LocalToUtcIso(row.StartTime),
                        endedUtc = LocalToUtcIso(row.EndTime),
                        precision = TimePrecision.Estimated,
                        id = row.Id
                    },
                    tx);
            }
            tx.Commit();

            AppLog.Info($"[设备同步] 已为 {rows.Count} 条历史会话回填 global_id / device_id / UTC 时间（time_precision=estimated）");
        }

        conn.Execute("""
            INSERT INTO schema_migrations (version, applied_at_utc) VALUES (1, @now)
            ON CONFLICT(version) DO NOTHING;
            """, new { now = UtcNowIso() });
    }

    /// <summary>
    /// migration 002（DDL 部分）：<c>games.global_id</c> —— 跨设备的 Game identity。
    ///
    /// ⚠️ **必须在 <c>DeduplicateGamesAndDailySummaries</c> 之前调用**：
    /// 去重要读 global_id 才能决定"继承还是丢弃 + 要不要写墓碑"，
    /// 而 Dapper 的 <c>SELECT *</c> 在列不存在时**静默忽略**（不抛异常）→ 逻辑会无声失效。
    ///
    /// SQLite 兼容性同 sessions（见 <see cref="EnsureSessionsSyncColumns"/>）：
    /// ADD COLUMN 不能加"NOT NULL 且无默认值"的列、也不能加 UNIQUE，
    /// 所以做法是「列可空 → 事后建唯一索引 → 非空由应用层保证」。
    /// SQLite 的唯一索引允许多个 NULL，所以幽灵行可以一直保持 NULL。
    /// </summary>
    public static void EnsureGamesIdentityColumn(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "games", "global_id"))
        {
            try
            {
                conn.Execute("ALTER TABLE games ADD COLUMN global_id TEXT;");
            }
            catch (Exception ex)
            {
                // 与项目既有惯例一致：加列失败视为"已存在"，但留日志便于排查。
                AppLog.Warn($"[设备同步] 追加 games.global_id 失败（可能已存在）：{ex.Message}");
            }
        }

        try
        {
            conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_games_global_id ON games(global_id);");
        }
        catch (Exception ex)
        {
            // ⚠️ 索引失败**绝不能**拖累回填：否则 games 永远拿不到身份，而且没人会察觉。
            AppLog.Warn($"[设备同步] 创建 idx_games_global_id 失败（不影响回填）：{ex.Message}");
        }
    }

    /// <summary>
    /// migration 002（回填部分）：**只给"可同步"的游戏**分配 <c>global_id</c>。
    ///
    /// 为什么只给可同步的：纯幽灵行（无 exe、无 session）永远不该进入同步体系，
    /// 给它们分配身份只会制造无意义的云端墓碑 —— 这就是"纯幽灵行不污染云端"的机制本身。
    /// 判定式复用 <see cref="GameSyncEligibility.SqlPredicate"/>，不另写一份。
    ///
    /// 为什么"每次启动补齐"而不是"一次性标记"：与 sessions 的回填同款**自愈** ——
    /// 期间新出现的可同步游戏（用户手加 exe / Notion 拉取 / 库扫描发现）也必须拿到身份，
    /// 一次性标记会让它们**永远**拿不到 global_id（Phase 1 就是被回归测试抓到过这个坑）。
    ///
    /// 幂等：只更新 <c>global_id</c> 仍为空的行 → 重复执行不会产生第二套 UUID。
    /// 注：不动 <c>updated_at</c> —— 这不是用户可见的修改，没必要平白搅动其他逻辑。
    /// </summary>
    private static void BackfillGameSyncIdentity(SqliteConnection conn)
    {
        int assigned;
        using (var tx = conn.BeginTransaction())
        {
            assigned = AssignMissingGameGlobalIds(conn, tx);
            tx.Commit();
        }

        if (assigned > 0)
        {
            AppLog.Info($"[设备同步] 已为 {assigned} 个可同步游戏分配 global_id");
        }

        conn.Execute("""
            INSERT INTO schema_migrations (version, applied_at_utc) VALUES (2, @now)
            ON CONFLICT(version) DO NOTHING;
            """, new { now = UtcNowIso() });
    }

    /// <summary>
    /// 给「可同步资格为真、但 <c>global_id</c> 仍为空」的游戏补 identity —— **唯一实现**。
    ///
    /// <para>
    /// 两个调用方共用它，避免"启动自愈一套、Gap Repair 另一套"导致判据漂移：
    /// <list type="bullet">
    ///   <item><see cref="BackfillGameSyncIdentity"/>：每次启动的 schema 自愈（自建事务）；</item>
    ///   <item>历史 Outbox Gap Repair：在它自己的事务里调用（因此这里接受可选事务，不再另开）。</item>
    /// </list>
    /// </para>
    ///
    /// 幂等：只更新 <c>global_id</c> 仍为空的行 → 重复执行不会产生第二套 UUID；
    /// 只给**可同步**的游戏分配（判据复用 <see cref="GameSyncEligibility.SqlPredicate"/>），
    /// 纯幽灵行依旧拿不到身份。
    /// </summary>
    /// <returns>补了几个。</returns>
    public static int AssignMissingGameGlobalIds(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        var ids = conn.Query<int>($"""
            SELECT games.id
            FROM games
            WHERE NULLIF(TRIM(global_id), '') IS NULL
              AND {GameSyncEligibility.SqlPredicate};
            """, transaction: tx).ToList();

        foreach (var id in ids)
        {
            conn.Execute("""
                UPDATE games
                SET global_id = @globalId
                WHERE id = @id AND NULLIF(TRIM(global_id), '') IS NULL;
                """,
                new { globalId = NewId(), id },
                tx);
        }

        return ids.Count;
    }

    /// <summary>
    /// 给**指定的一个**游戏补 identity（只补 <c>global_id</c> 仍为空的行；幂等 ⇒ 不会重复分配）。
    ///
    /// <para>
    /// 用于"该游戏**即将承载外部导入的独立事实**"的场合（本地统一事实存储原则，2026-10-10）：
    /// 这类游戏可能既无 exe 也无会话（Notion Pull 期间新建的幽灵行），因此不满足
    /// <see cref="GameSyncEligibility.SqlPredicate"/>；但如果它承载了导入事实，
    /// 那份事实就必须能同步出去，否则会永久留在本机。
    /// </para>
    /// </summary>
    /// <returns>补了几个（0 = 本来就有，无需分配）。</returns>
    public static int AssignGameGlobalIdIfMissing(
        SqliteConnection conn, long gameId, SqliteTransaction? tx = null)
        => conn.Execute(
            """
            UPDATE games
            SET global_id = @globalId
            WHERE id = @id AND NULLIF(TRIM(global_id), '') IS NULL;
            """,
            new { globalId = NewId(), id = gameId },
            tx);

    /// <summary>
    /// migration 003（2e）：让 Deferred 台账**能在离线状态下被重新处理**。
    ///
    /// ⚠️ 之前台账只记了"为什么延后"，**没记"延后的那个远端对象到底是什么"**。
    ///    而那条远端对象从未落地到本地库，2e 无法凭本地状态重建它
    ///    （platform / platform_id / executable / executable_path / name / notion_page_id 全都不在本地）。
    ///    → **不补 payload，2e 一条也重处理不了**（阻塞性缺陷，用户 2026-10-03 裁定）。
    ///
    /// payload 冻结规则：必须是**当次 Pull 收到的原始/规范化同步 payload 快照**；
    /// 重处理时**不得**重新去远端拉，也**不得**按本地当前状态重新构造。
    ///
    /// 另外补 <c>resolved_at_utc</c> / <c>resolution</c>：
    /// **Resolved ≠ Completed** —— 它只表示"这条 Deferred 已经过 Identity Reconciliation 并有明确结论"，
    /// 审计记录必须保留（不删除）。
    /// </summary>
    public static void EnsureDeferredReprocessingColumns(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "sync_deferred_changes", "operation", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(conn, "sync_deferred_changes", "device_id", "TEXT");
        AddColumnIfMissing(conn, "sync_deferred_changes", "payload", "TEXT");
        AddColumnIfMissing(conn, "sync_deferred_changes", "resolved_at_utc", "TEXT");
        AddColumnIfMissing(conn, "sync_deferred_changes", "resolution", "TEXT");

        RegisterMigration(conn, 3);
    }

    /// <summary>
    /// 幂等追加一列（失败只记 Warn，与项目既有惯例一致）。
    ///
    /// ⚠️ 2026-10-11 事故修复（实机 41/41 同步失败）：<paramref name="columnTypeDefinition"/> 只能是
    /// **类型/默认值**，列名由 <paramref name="column"/> 提供。此前本方法把类型定义直接拼进 DDL
    /// （<c>ADD COLUMN {columnDdl}</c>），migration 008 传入 <c>"INTEGER"</c> 便建出了一列**名为
    /// <c>INTEGER</c>** 的列，而真正的 <c>observed_remote_seconds</c> 从未存在 ——
    /// 于是 Notion 同步每一条都抛 <c>no such column: observed_remote_seconds</c>。
    /// 列名与类型必须分开传，绝不允许再拼进列名。
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string columnTypeDefinition)
    {
        if (ColumnExists(conn, table, column)) return;

        try
        {
            conn.Execute($"ALTER TABLE {table} ADD COLUMN {column} {columnTypeDefinition};");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 追加 {table}.{column} 失败（可能已存在）：{ex.Message}");
        }
    }

    /// <summary>列是否存在（用于幂等 ALTER TABLE）。</summary>
    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 取得（必要时生成）本机设备 ID，并保证 <c>devices</c> 表里有本机那一行。
    /// device_id 一经生成**永不改变**（用户只能改 device_name）。
    /// </summary>
    public static string EnsureLocalDevice(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        var existing = conn.QuerySingleOrDefault<string>(
            "SELECT value FROM settings WHERE key = @k",
            new { k = DeviceSyncConstants.SettingKeyDeviceId }, tx);

        var deviceId = string.IsNullOrWhiteSpace(existing) ? NewId() : existing!;

        conn.Execute("""
            INSERT INTO settings (key, value) VALUES (@k, @v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = CURRENT_TIMESTAMP;
            """, new { k = DeviceSyncConstants.SettingKeyDeviceId, v = deviceId }, tx);

        var now = UtcNowIso();
        var name = string.IsNullOrWhiteSpace(Environment.MachineName) ? "Windows Computer" : Environment.MachineName;

        // 已存在则只刷新"最后出现时间"与本机标记；**不覆盖用户改过的 device_name**。
        conn.Execute("""
            INSERT INTO devices (device_id, device_name, platform, app_version,
                                 created_at_utc, last_seen_at_utc, status, is_this_device)
            VALUES (@id, @name, 'windows', '', @now, @now, 0, 1)
            ON CONFLICT(device_id) DO UPDATE SET
                last_seen_at_utc = excluded.last_seen_at_utc,
                is_this_device   = 1;
            """, new { id = deviceId, name, now }, tx);

        return deviceId;
    }

    /// <summary>
    /// 本地"墙上时间"字符串 → ISO-8601 UTC。
    /// 用 TimeZoneInfo 的**逐日期**偏移（比"统一用当前偏移"准：历史行落在夏令时期间也能算对）；
    /// 遇到春季 DST 跳变造成的"不存在时刻"则退回按偏移近似，绝不抛异常。
    /// </summary>
    internal static string? LocalToUtcIso(string? localTime)
    {
        if (string.IsNullOrWhiteSpace(localTime)) return null;

        if (!DateTime.TryParse(localTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        try
        {
            if (!TimeZoneInfo.Local.IsInvalidTime(local))
            {
                return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // 落到下面的近似分支
        }

        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return local.Subtract(offset).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static string UtcNowIso()
        => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>生成新的同步实体 ID（UUIDv4，N 格式：32 位十六进制，无连字符）。</summary>
    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>回填查询用的行（只读这几列，避免把 sessions 整表载入内存）。</summary>
    private sealed class LegacySessionRow
    {
        public long Id { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string? GlobalId { get; set; }
    }
}
