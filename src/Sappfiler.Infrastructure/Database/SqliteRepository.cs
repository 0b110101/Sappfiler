using System.Data;
using System.Text.Json;
using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

// partial：设备同步的存储层放在 SqliteRepository.DeviceSync.cs（新文件），
// 这样本文件与 feature/multi-backend-sync 分支的改动区域几乎不重叠，便于将来合并。
public partial class SqliteRepository : IDatabaseRepository, GameTimeTracker.Core.Notion.INotionIconMappingStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// 只用来做名字归一化的匹配器实例。
    /// ⚠️ <c>NormalizeTitle</c> 是**实例方法**（只有 <c>ExtractSteamAppId</c> 是静态的），
    /// 而它不碰任何实例状态，所以整个仓储共用一个就行 ——
    /// 不要每次调用都 new（构造函数会新建一个 HttpClient）。
    /// </summary>
    private static readonly GameTimeTracker.Core.Services.GameMatcher Matcher = new();

    static SqliteRepository()
    {
        // Dapper 默认不做 snake_case -> PascalCase 映射。
        // 本项目所有列名都是 snake_case（platform_id / notion_page_id / duration_minutes ...），
        // 不开这个开关，这些列会被静默读成 null / 0：
        //   games.platform_id   -> null  => 丢失 Steam AppID，总表匹配全部失败
        //   games.notion_page_id-> null  => 映射库全部显示「未绑定」
        //   daily_summary.duration_minutes / game_name -> 0 / null => 统计与热力图失真
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqliteRepository(string dbPath = "gametime.db")
    {
        var fullPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeDatabase();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void InitializeDatabase()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS games (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                platform TEXT NOT NULL,
                platform_id TEXT NOT NULL,
                name TEXT NOT NULL,
                executable TEXT NOT NULL,
                executable_path TEXT NOT NULL,
                notion_page_id TEXT,
                status TEXT DEFAULT 'active',
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                -- ⚠️ updated_at = "最后一次被任意写路径碰过"：监听循环**每 5 秒**都会经
                --    GetOrCreateGameAsync 的命令刷新它。**绝不可**用它做"重新激活"或
                --    "删除冲突"的仲裁依据 —— 那是 games.reactivated_at_utc 的职责
                --    （migration 016；判据见 SqliteRepository.DeviceSync.cs 的 LocalIntentWinsSql）。
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(platform, platform_id)
            );
            CREATE INDEX IF NOT EXISTS idx_games_exe ON games(executable, status);
            CREATE INDEX IF NOT EXISTS idx_games_notion ON games(notion_page_id);

            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                pid INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                start_time DATETIME NOT NULL,
                end_time DATETIME,
                last_heartbeat DATETIME NOT NULL,
                duration_seconds INTEGER DEFAULT 0,
                is_active INTEGER DEFAULT 1,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_sessions_active ON sessions(is_active);
            CREATE INDEX IF NOT EXISTS idx_sessions_game_time ON sessions(game_id, start_time);

            CREATE TABLE IF NOT EXISTS daily_summary (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                date TEXT NOT NULL,
                game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                duration_seconds INTEGER DEFAULT 0,
                duration_minutes INTEGER DEFAULT 0,
                session_count INTEGER DEFAULT 1,
                sync_status TEXT DEFAULT 'pending',
                notion_page_id TEXT,
                last_sync_at DATETIME,
                error_message TEXT,
                -- 这条记录在 Notion 上的标题（含时长后缀）与 page icon 的快照。
                -- 仅用于回刷时比对：拿它和"期望值"比较即可知道要不要 PATCH，
                -- 避免每轮同步都把所有历史记录重打一遍 Notion。
                notion_title TEXT,
                notion_icon_url TEXT,
                UNIQUE(date, game_id)
            );
            CREATE INDEX IF NOT EXISTS idx_daily_sync ON daily_summary(sync_status);
            CREATE INDEX IF NOT EXISTS idx_daily_date ON daily_summary(date);

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS game_catalog (
                page_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                aliases_json TEXT NOT NULL DEFAULT '[]',
                identifiers_json TEXT NOT NULL DEFAULT '[]',
                genres_json TEXT NOT NULL DEFAULT '[]',
                cover_url TEXT,
                icon_url TEXT,
                icon_type TEXT,
                last_synced_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            -- 删除留底：双向删除是物理删除，误判时靠这张表把数据找回。
            -- kind: 'game' | 'daily' | 'play-record'（用户在历史页删除某 (游戏, 会计日) 记录）
            -- source: 'app'（在程序里删）| 'notion'（对账发现远端已删）
            CREATE TABLE IF NOT EXISTS deleted_archive (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                source TEXT NOT NULL,
                reference TEXT,
                payload_json TEXT NOT NULL,
                deleted_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_deleted_archive_time ON deleted_archive(deleted_at);
        """;
        cmd.ExecuteNonQuery();

        try
        {
            using var alterCmd1 = conn.CreateCommand();
            alterCmd1.CommandText = "ALTER TABLE games ADD COLUMN cover_url TEXT;";
            alterCmd1.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterCmd2 = conn.CreateCommand();
            alterCmd2.CommandText = "ALTER TABLE game_catalog ADD COLUMN cover_url TEXT;";
            alterCmd2.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterCmd3 = conn.CreateCommand();
            alterCmd3.CommandText = "ALTER TABLE game_catalog ADD COLUMN icon_url TEXT;";
            alterCmd3.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterCmd4 = conn.CreateCommand();
            alterCmd4.CommandText = "ALTER TABLE game_catalog ADD COLUMN icon_type TEXT;";
            alterCmd4.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterCmd5 = conn.CreateCommand();
            alterCmd5.CommandText = "ALTER TABLE daily_summary ADD COLUMN notion_title TEXT;";
            alterCmd5.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterCmd6 = conn.CreateCommand();
            alterCmd6.CommandText = "ALTER TABLE daily_summary ADD COLUMN notion_icon_url TEXT;";
            alterCmd6.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterCmd7 = conn.CreateCommand();
            alterCmd7.CommandText = "ALTER TABLE game_catalog ADD COLUMN genres_json TEXT NOT NULL DEFAULT '[]';";
            alterCmd7.ExecuteNonQuery();
        }
        catch { }

        // 同步身份基础设施必须在去重**之前**就绪：
        //   · 去重要读 games.global_id —— 列不存在时 Dapper 的 SELECT * 会**静默忽略**（不抛异常）
        //     → 继承/墓碑逻辑无声失效，日志里也看不出来；
        //   · 去重要写 sync_tombstones —— 表不存在会直接抛异常 → 整次去重被外层 catch 跳过。
        // 返回 false = 没就绪 → 去重退回纯旧行为，绝不因同步能力缺失而让去重失效。
        var syncIdentityReady = DeviceSyncSchema.EnsurePrerequisitesForDeduplication(conn);

        // 启动时自动检查并合并同名/同总表关联的分裂游戏条目及同日时长记录
        DeduplicateGamesAndDailySummaries(conn, syncIdentityReady);

        // 多设备同步（Phase 1/2）：同步身份列 + 同步元数据表。
        // 实现**全部**在 DeviceSyncSchema.cs，这里只保留少量调用 —— 本文件与
        // feature/multi-backend-sync 分支都会改动，把改动面压到最小以免合并时冲突。
        DeviceSyncSchema.Ensure(conn);
    }


    private static string NormalizePageId(string? pageId)
        => string.IsNullOrWhiteSpace(pageId)
            ? string.Empty
            : pageId.Replace("-", string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// 自动合并重复游戏条目（同 notion_page_id 或同名），将 sessions 与 daily_summary 汇总迁移并清理冗余游戏行。
    ///
    /// 多设备同步（Phase 2b）在此基础上追加了 **identity 一致性**处理（冻结契约）：
    ///   ① 写入前拍快照 → ② sessions 迁移 → ③ daily_summary 合并 → ④ canonical global_id 继承
    ///   → ⑤ 删除重复行 → ⑥ 为被丢弃的同步身份写墓碑，**整段是一个事务**。
    /// 分组规则 / 评分 / 字段继承 / 合并口径 / 日志格式**全部保持原样**。
    ///
    /// <paramref name="syncIdentityReady"/> 为 false（同步基础设施未就绪）时，
    /// 本方法退回**纯旧行为** —— 绝不因为同步能力缺失而让去重失效。
    /// </summary>
    private void DeduplicateGamesAndDailySummaries(SqliteConnection conn, bool syncIdentityReady = false)
    {
        try
        {
            // 归档（已删除）的 Game 不参与启动去重的合并候选 —— 它只为历史 FK 存在。
            var allGames = conn.Query<GameRecord>(
                $"SELECT * FROM games WHERE {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate} ORDER BY id ASC;").ToList();
            if (allGames.Count <= 1) return;

            var groups = new List<List<GameRecord>>();
            var visited = new HashSet<int>();

            for (int i = 0; i < allGames.Count; i++)
            {
                var g1 = allGames[i];
                if (visited.Contains(g1.Id)) continue;

                var group = new List<GameRecord> { g1 };
                visited.Add(g1.Id);

                for (int j = i + 1; j < allGames.Count; j++)
                {
                    var g2 = allGames[j];
                    if (visited.Contains(g2.Id)) continue;

                    bool sameNotion = !string.IsNullOrEmpty(g1.NotionPageId) &&
                                      !string.IsNullOrEmpty(g2.NotionPageId) &&
                                      string.Equals(NormalizePageId(g1.NotionPageId), NormalizePageId(g2.NotionPageId), StringComparison.OrdinalIgnoreCase);

                    bool sameName = !string.IsNullOrWhiteSpace(g1.Name) &&
                                    !string.IsNullOrWhiteSpace(g2.Name) &&
                                    string.Equals(g1.Name.Trim(), g2.Name.Trim(), StringComparison.OrdinalIgnoreCase);

                    // 🛡️ 2026-10-11（身份模型审查）：**绝不按名称合并"强键可用且不相等"的两行**。
                    //
                    //    身份规则（GameIdentityRules）里"强键适用但不等"是 A 级冲突 ⇒ 一律阻断；
                    //    而启动去重过去只按「同 Notion 页面 或 同名」分组、没有这条守卫 ⇒ 两款**同名但
                    //    不同 AppID** 的游戏（本体 / 重制版 / 同平台两个发行条目）会被合并成一行，并把
                    //    sessions / daily_duration_adjustments / daily_summary 搬到另一款游戏名下
                    //    —— 事实错归属且不可逆（实测：两行 → 一行）。
                    //
                    //    同 Notion 页面的合并**不受影响**：页面本身就是跨设备共享的 Provider 身份。
                    var leftFacts = new GameIdentityFacts(g1.Platform, g1.PlatformId);
                    var rightFacts = new GameIdentityFacts(g2.Platform, g2.PlatformId);
                    var strongKeyConflict = GameIdentityRules.StrongKeyAvailable(leftFacts)
                                            && GameIdentityRules.StrongKeyAvailable(rightFacts)
                                            && !GameIdentityRules.StrongKeyEqual(leftFacts, rightFacts);

                    if (sameNotion || (sameName && !strongKeyConflict))
                    {
                        group.Add(g2);
                        visited.Add(g2.Id);
                    }
                }

                if (group.Count > 1)
                {
                    groups.Add(group);
                }
            }

            foreach (var group in groups)
            {
                int Score(GameRecord g)
                {
                    int s = 0;
                    if (!string.IsNullOrWhiteSpace(g.ExecutablePath)) s += 100;
                    // 强键判据走 GameIdentityRules 的唯一定义（避免 2b / 2e 两套启发式分叉）
                    if (GameIdentityRules.IsAuthoritativePlatformKey(g.Platform, g.PlatformId)) s += 50;
                    if (!string.IsNullOrWhiteSpace(g.NotionPageId)) s += 20;
                    if (!string.IsNullOrWhiteSpace(g.CoverUrl)) s += 10;
                    return s;
                }

                var canonical = group.OrderByDescending(Score).ThenBy(g => g.Id).First();
                var duplicates = group.Where(g => g.Id != canonical.Id).ToList();

                // ① 拍快照（本次去重的**决策依据**）：必须在任何写入之前。
                //    一旦 sessions 被搬到 canonical，dup 就不再"存在关联 sessions"，
                //    再用 SyncableGame 规则判定会把它误判成纯幽灵 → 漏写墓碑 → 云端 identity 永久残留。
                var snapshots = TakeDedupSnapshot(conn, group);

                string? bestNotionId = canonical.NotionPageId;
                string? bestCoverUrl = canonical.CoverUrl;
                string bestExe = canonical.Executable;
                string bestExePath = canonical.ExecutablePath;
                string bestPlatform = canonical.Platform;
                string bestPlatformId = canonical.PlatformId;

                foreach (var dup in duplicates)
                {
                    if (string.IsNullOrEmpty(bestNotionId) && !string.IsNullOrEmpty(dup.NotionPageId)) bestNotionId = dup.NotionPageId;
                    if (string.IsNullOrEmpty(bestCoverUrl) && !string.IsNullOrEmpty(dup.CoverUrl)) bestCoverUrl = dup.CoverUrl;
                    if (string.IsNullOrEmpty(bestExe) && !string.IsNullOrEmpty(dup.Executable)) bestExe = dup.Executable;
                    if (string.IsNullOrEmpty(bestExePath) && !string.IsNullOrEmpty(dup.ExecutablePath)) bestExePath = dup.ExecutablePath;
                    if (!GameIdentityRules.IsAuthoritativePlatformKey(bestPlatform, bestPlatformId) &&
                        GameIdentityRules.IsAuthoritativePlatformKey(dup.Platform, dup.PlatformId))
                    {
                        bestPlatform = dup.Platform;
                        bestPlatformId = dup.PlatformId;
                    }
                }

                // ④ canonical 的 global_id（冻结契约 1~4）：
                //    已有 → 永远保留（它是权威 identity）；没有 → 继承 duplicates 中**第一个**有效值；
                //    全组都没有 → 保持 NULL，交给下次启动的自愈回填补。
                //    ⚠️ 绝不做"删完重复行再给剩余行重新生成 UUID"。
                string? bestGlobalId = syncIdentityReady ? snapshots[canonical.Id].GlobalId : null;
                if (syncIdentityReady && string.IsNullOrWhiteSpace(bestGlobalId))
                {
                    foreach (var dup in duplicates)
                    {
                        var inherited = snapshots[dup.Id].GlobalId;
                        if (!string.IsNullOrWhiteSpace(inherited))
                        {
                            bestGlobalId = inherited;
                            break;
                        }
                    }
                }

                // 本次 group 的合并是**一个原子变更**（冻结契约第 7 条）：
                //   ② sessions → canonical、③ daily_summary 合并、④ canonical identity 继承、
                //   ⑤ DELETE duplicate games、⑥ 写 sync_tombstones。
                // 任一步抛异常 → using 未 Commit 即 Dispose = ROLLBACK，
                // 绝不会留下"sessions 已迁移 + games 已删除 + 墓碑没写"这种中间态。
                using var tx = conn.BeginTransaction();

                // ④-a 先把 identity 从"即将被合并掉的行"身上摘下来。
                //     ⚠️ 必需：唯一索引不允许同一个 global_id 同时挂在两行上。canonical 要继承的那个
                //     identity 此刻还在被合并行身上，若不先摘下，下一步的继承会直接撞
                //     `UNIQUE constraint failed: games.global_id` → 整组回滚（行为"安全"但永远合并不了）。
                //     摘除只是把状态先归零，任一步失败都会随事务回滚，identity 不会真的丢。
                if (syncIdentityReady && duplicates.Count > 0)
                {
                    conn.Execute("""
                        UPDATE games
                        SET global_id = NULL
                        WHERE id IN @dupIds
                          AND NULLIF(TRIM(global_id), '') IS NOT NULL;
                        """,
                        new { dupIds = duplicates.Select(d => d.Id).ToList() },
                        tx);
                }

                // global_id 列不存在时（同步基础设施未就绪）不碰它 —— 去重必须照常工作。
                var globalIdSetClause = syncIdentityReady ? "global_id = @bestGlobalId," : string.Empty;

                conn.Execute($"""
                    UPDATE games
                    SET notion_page_id = @bestNotionId,
                        cover_url = @bestCoverUrl,
                        executable = @bestExe,
                        executable_path = @bestExePath,
                        platform = @bestPlatform,
                        platform_id = @bestPlatformId,
                        {globalIdSetClause}
                        updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id;
                    """,
                    new { bestNotionId, bestCoverUrl, bestExe, bestExePath, bestPlatform, bestPlatformId, bestGlobalId, id = canonical.Id },
                    tx);

                foreach (var dup in duplicates)
                {
                    // ⚠️ 只做**数据搬运**（sessions + daily_summary），
                    //    由下面的代码单独决定"删行"与"写身份合并"。
                    //    2e 的身份对账复用**同一个**迁移口径，避免两套合并逻辑分叉。
                    MigrateGameDataToCanonical(conn, tx, canonical.Id, dup.Id);

                    // ⑤ 删除重复游戏行 → ⑥ 再记录"被淘汰的 identity 并入了谁"（顺序即冻结契约的 ⑤→⑥）。
                    //
                    // 淘汰集合 = {快照里有 global_id 的行} \ {canonical 最终持有的 global_id}，
                    // 且仅限**删除前 isSyncable == true** 的行（契约第 5 条）。
                    // 判定必须用**快照**而非当前库状态 —— 此刻该行的 sessions 已经搬走了。
                    // identity 只认 global_id，与 notion_page_id 无关（第 6 条）。
                    //
                    // ⚠️ **2e 语义修正（2026-10-03 用户裁定）**：这里写的是
                    // **identity supersession（身份合并）**，**不是删除墓碑**。
                    // 被淘汰的行与权威行其实是**同一个实体**、只是有两套 identity；
                    // 若写成墓碑，其它设备会理解为"该实体已被删除"从而删掉自己的数据 ——
                    // 把"合并"错误地表达成"删除"。两者的数据模型与同步语义必须严格区分：
                    //     真删除   → sync_tombstones
                    //     身份合并 → sync_identity_supersessions
                    var discarded = snapshots[dup.Id];

                    // 🛡️ 2026-10-14（孤儿会话收口）：与 2e 身份对账共用同一道守卫 —— 残留 ⇒ 抛异常 ⇒ 本事务回滚，不删行
            EnsureNoGameRefsOrThrow(conn, tx, dup.Id, "启动去重身份合并");
            conn.Execute("DELETE FROM games WHERE id = @dupId;", new { dupId = dup.Id }, tx);

                    if (syncIdentityReady &&
                        discarded.IsSyncable &&
                        !string.IsNullOrWhiteSpace(discarded.GlobalId) &&
                        !string.IsNullOrWhiteSpace(bestGlobalId) &&
                        !string.Equals(discarded.GlobalId, bestGlobalId, StringComparison.Ordinal))
                    {
                        // 🛡️ 2026-10-11（身份协议）：启动去重也走**统一入口** ——
                        //    ① 不再自行决定 canonical（改为等价类内最小 identity，与对账/确认/对端一致）；
                        //    ② 真正发生的映射**入队传播**（否则本机合并了、对端还留着两条身份）。
                        var dedupOutcome = EstablishIdentityLinkCore(conn, tx,
                            new[] { discarded.GlobalId, bestGlobalId },
                            SupersessionReasons.StartupDedupDuplicateIdentity);

                        EnqueueIdentityMappingsCore(conn, tx, dedupOutcome.MappingsChanged,
                            SupersessionReasons.StartupDedupDuplicateIdentity);

                        AppLog.Info($"[设备同步] 身份合并（非删除）：{discarded.GlobalId} → {dedupOutcome.CanonicalGlobalId}"
                            + $"（原「{dup.Name}」id={dup.Id}；传播 {dedupOutcome.MappingsChanged.Count} 条）");
                    }

                    AppLog.Info($"[维护] 自动合并重复游戏记录：「{canonical.Name}」(保留 id={canonical.Id}, 清理重复 id={dup.Id})");
                }

                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[维护] 游戏去重合并异常: {ex.Message}");
        }
    }

    // ----------------- Game Operations -----------------

    public async Task<GameRecord> GetOrCreateGameAsync(GameIdentity identity)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            // 1. Match by platform and platform_id
            var existing = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                "SELECT * FROM games WHERE LOWER(platform) = LOWER(@Platform) AND LOWER(platform_id) = LOWER(@PlatformId) "
                + $"AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate} "
                + "ORDER BY (CASE WHEN notion_page_id IS NOT NULL AND notion_page_id != '' THEN 0 ELSE 1 END), id ASC;",
                new { identity.Platform, identity.PlatformId });

            // 2. Fallback: match by executable path or executable name
            if (existing == null && (!string.IsNullOrEmpty(identity.ExecutablePath) || !string.IsNullOrEmpty(identity.Executable)))
            {
                existing = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                    $"""
                    SELECT * FROM games 
                    WHERE ((LOWER(executable_path) = LOWER(@ExecutablePath) AND @ExecutablePath != '') 
                       OR (LOWER(executable) = LOWER(@Executable) AND @Executable != ''))
                      AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate}
                    ORDER BY (CASE WHEN notion_page_id IS NOT NULL AND notion_page_id != '' THEN 0 ELSE 1 END), id ASC;
                    """,
                    new { identity.ExecutablePath, identity.Executable });
            }

            // 3. Fallback: match by Game Name (prevent creating duplicate game rows in SQLite)
            if (existing == null && !string.IsNullOrWhiteSpace(identity.Name))
            {
                existing = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                    $"""
                    SELECT * FROM games 
                    WHERE LOWER(name) = LOWER(@Name)
                      AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate}
                    ORDER BY (CASE WHEN notion_page_id IS NOT NULL AND notion_page_id != '' THEN 0 ELSE 1 END), (CASE WHEN executable_path != '' THEN 0 ELSE 1 END), id ASC;
                    """,
                    new { identity.Name });
            }

            // 4. 🛡️ 兜底（2026-10-12 实机事故修复）：同 (platform, platform_id) 的**已归档**行。
            //
            //    事故现场（用户真实库证据）：用户删除过某游戏（games.status='archived'），之后再次启动它 ——
            //    上面三步的 WHERE 都带 NormalGamePredicate（排除 archived）⇒ 全都查不到 ⇒ 走到 INSERT，
            //    而表级 UNIQUE(platform, platform_id) 仍被那条 archived 行占用 ⇒ 每 5 秒抛
            //    "UNIQUE constraint failed: games.platform, games.platform_id"，该游戏**永远无法再被识别/计时**。
            //
            //    语义裁定（与"删除一条历史记录 ≠ 删除游戏"一致）：
            //      **删除游戏 = 把它移出游戏库，不是永久禁止它产生新记录。**
            //      本机用户主动启动该游戏 = 新意图 ⇒ 复用**同一行身份**并重新激活：
            //        · id / global_id 全部保留 ⇒ 身份稳定，不会诞生第二条身份、不会重复计时；
            //        · 历史事实与删除排除（daily_play_exclusions）分毫不动 ⇒ 被删掉的那一天**不会复活**；
            //        · 墓碑（sync_tombstones）**不删** ⇒ 远端旧数据重放仍然被 GameDeletionGuard 挡下
            //          （"本机新意图"与"远端复活"是两条独立路径，不能混为一谈）。
            if (existing == null && !string.IsNullOrWhiteSpace(identity.PlatformId))
            {
                existing = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                    "SELECT * FROM games WHERE LOWER(platform) = LOWER(@Platform) "
                    + "AND LOWER(platform_id) = LOWER(@PlatformId) "
                    + $"AND NOT ({GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate}) "
                    + "ORDER BY id ASC;",
                    new { identity.Platform, identity.PlatformId });

                if (existing != null)
                {
                    // ⚠️ 2026-10-12（仲裁缺陷修复）：这里是全仓**唯一**的 archived→active 转换点，
                    //    因此也是**唯一**写 reactivated_at_utc 的地方 —— 它表达"用户此刻的重新激活意图"，
                    //    供远端 Game Delete 仲裁使用（见 LocalIntentWinsSql / migration 016）。
                    //    下面的通用更新块（existing != null 那段）**刻意不碰**这一列：那里的调用来自
                    //    5 秒监听循环，绝不能把"心跳"当成"用户意图"。
                    var reactivatedAt = UtcNowIso();

                    // ⚠️ 2026-10-12（再激活的传播）：本机状态与"我要把它传播出去"必须**同一事务**提交。
                    //    否则就会出现"本地已恢复、对端永远停在已删除"—— 真机实测的后果是对端既看不到该
                    //    游戏恢复，也**收不到本机之后产生的会话**（对端落 deletion_conflict 且永久挂起）。
                    bool queuedForSync;
                    using (var tx = conn.BeginTransaction())
                    {
                        await conn.ExecuteAsync(
                            "UPDATE games SET status = @active, reactivated_at_utc = @reactivatedAt, "
                            + "updated_at = CURRENT_TIMESTAMP WHERE id = @Id;",
                            new
                            {
                                active = GameTimeTracker.Core.DeviceSync.GameStatuses.Active,
                                reactivatedAt,
                                existing.Id
                            },
                            tx);

                        queuedForSync = await EnqueueGameReactivationCoreAsync(conn, tx, existing, reactivatedAt);

                        tx.Commit();
                    }

                    existing.Status = GameTimeTracker.Core.DeviceSync.GameStatuses.Active;

                    AppLog.Info($"[计时] 「{existing.Name}」此前被删除（已归档），本次启动重新启用**同一身份**"
                        + $"（id={existing.Id}，global_id 保持不变）；已删除的历史日期记录不会因此恢复。");

                    if (queuedForSync)
                    {
                        AppLog.Info($"[设备同步] 已入队「{existing.Name}」的重新启用变更（{reactivatedAt}）："
                            + "其他设备收到后会撤销本机此前传播过去的删除并恢复该游戏。");
                    }
                }
            }

            if (existing != null)
            {
                // ⚠️ 2026-10-12：本块由 **5 秒监听循环**高频调用（每次识别到在跑的游戏）。
                //    它**刻意不更新 reactivated_at_utc**：这里的 updated_at 只是"被碰过"的时间戳，
                //    与"用户重新激活意图"无关。远端 Delete 的仲裁一律看 reactivated_at_utc。
                // If existing has manual or generic platform, but identity has a real platform (e.g. steam, xbox), upgrade it!
                bool upgradePlatform = (existing.Platform == "manual" || existing.PlatformId.Length == 8 || existing.PlatformId.Length == 16) &&
                                       identity.Platform != "manual" && !string.IsNullOrEmpty(identity.PlatformId);

                string finalPlatform = upgradePlatform ? identity.Platform : existing.Platform;
                string finalPlatformId = upgradePlatform ? identity.PlatformId : existing.PlatformId;

                await conn.ExecuteAsync(
                    """
                    UPDATE games 
                    SET name = CASE WHEN name IS NULL OR name = '' THEN @Name ELSE name END,
                        executable = CASE WHEN @Executable != '' THEN @Executable ELSE executable END,
                        executable_path = CASE WHEN @ExecutablePath != '' THEN @ExecutablePath ELSE executable_path END, 
                        platform = @finalPlatform,
                        platform_id = @finalPlatformId,
                        updated_at = CURRENT_TIMESTAMP
                    WHERE id = @Id;
                    """,
                    new { identity.Name, identity.Executable, identity.ExecutablePath, finalPlatform, finalPlatformId, existing.Id });
                if (string.IsNullOrEmpty(existing.Name)) existing.Name = identity.Name;
                if (!string.IsNullOrEmpty(identity.Executable)) existing.Executable = identity.Executable;
                if (!string.IsNullOrEmpty(identity.ExecutablePath)) existing.ExecutablePath = identity.ExecutablePath;
                existing.Platform = finalPlatform;
                existing.PlatformId = finalPlatformId;
                return existing;
            }

            var id = await conn.ExecuteScalarAsync<int>(
                """
                INSERT INTO games (platform, platform_id, name, executable, executable_path, status)
                VALUES (@Platform, @PlatformId, @Name, @Executable, @ExecutablePath, 'active');
                SELECT last_insert_rowid();
                """,
                identity);

            return (await GetGameByIdAsync(id))!;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<GameRecord?> GetGameByIdAsync(int id)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GameRecord>(
            "SELECT * FROM games WHERE id = @id;", new { id });
    }

    public async Task<GameRecord?> GetGameByPlatformIdAsync(string platform, string platformId)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GameRecord>(
            "SELECT * FROM games WHERE platform = @platform AND platform_id = @platformId;",
            new { platform, platformId });
    }

    public async Task<GameRecord?> GetGameByPathOrExeAsync(string executablePath, string executable)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<GameRecord>(
            """
            SELECT * FROM games 
            WHERE (LOWER(executable_path) = LOWER(@executablePath) AND @executablePath != '') 
               OR (LOWER(executable) = LOWER(@executable) AND @executable != '') 
            ORDER BY (CASE WHEN notion_page_id IS NOT NULL AND notion_page_id != '' THEN 0 ELSE 1 END), id DESC;
            """,
            new { executablePath, executable });
    }

    public async Task UpdateGameNotionIdAsync(int gameId, string notionPageId)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE games SET notion_page_id = @notionPageId, updated_at = CURRENT_TIMESTAMP WHERE id = @gameId;",
                new { gameId, notionPageId });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateGameStatusAsync(int gameId, string status)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE games SET status = @status, updated_at = CURRENT_TIMESTAMP WHERE id = @gameId;",
                new { gameId, status });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<GameRecord>> GetAllGamesAsync()
    {
        using var conn = CreateConnection();
        // 「正常 Game」作用域：归档（用户已删除）的行不得出现在正常业务列表里（用户 2026-10-07 要求 5）。
        var list = await conn.QueryAsync<GameRecord>(
            $"SELECT * FROM games WHERE {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate} ORDER BY name;");
        return list.ToList();
    }

    public async Task<GameRecord?> GetGameByNotionIdAsync(string notionPageId)
    {
        if (string.IsNullOrWhiteSpace(notionPageId)) return null;
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<GameRecord>(
            "SELECT * FROM games WHERE notion_page_id = @notionPageId ORDER BY id ASC LIMIT 1;",
            new { notionPageId });
    }

    public async Task<IReadOnlyList<GameRecord>> GetPendingGamesAsync()
    {
        using var conn = CreateConnection();
        // 归档（已删除）的 Game 不允许再进入 Notion 待推送候选。
        var list = await conn.QueryAsync<GameRecord>(
            "SELECT * FROM games WHERE (notion_page_id IS NULL OR notion_page_id = '') "
            + $"AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate} ORDER BY created_at DESC;");
        return list.ToList();
    }

    /// <summary>
    /// 删除一个 Game 条目 —— ⚠️ **删除 Game ≠ 删除历史事实**（用户 2026-10-07 批准，C5 / C-2）。
    ///
    /// <para>
    /// 原实现是裸 <c>DELETE FROM games</c> ✗：而 <c>sessions</c> 与 <c>daily_summary</c> 的 FK
    /// 都是 <c>ON DELETE CASCADE</c>（且 <c>PRAGMA foreign_keys=ON</c>）⇒ 会**连带抹掉全部历史** ✗。
    /// </para>
    ///
    /// <para>
    /// 现在改为「历史归档」：保留 <c>games</c> 行（继续作为历史 Session / daily_summary 的 FK 目标），
    /// 只把 status 置为 <see cref="GameStatuses.Archived"/>；同时写删除墓碑（<c>sync_tombstones</c>），
    /// 表达"这个 Game 已被用户删除"这一事实。历史数据**一个字节都不改**。
    /// </para>
    /// </summary>
    public async Task DeleteGameAsync(int gameId)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var snapshot = conn.QuerySingleOrDefault<dynamic>(
                """
                SELECT id, global_id, name, platform, platform_id, executable, executable_path, notion_page_id
                FROM games WHERE id = @gameId;
                """,
                new { gameId }, tx);

            if (snapshot is null)
            {
                tx.Commit();
                return;
            }

            string? globalId = (string?)snapshot.global_id;
            globalId = string.IsNullOrWhiteSpace(globalId) ? null : globalId!.Trim();

            // 🛡️ 2026-10-11（删除传播修复）：没有 global_id 的本地行过去**既不起墓碑、也不入队**
            //    （旧条件是 globalId is not null），删除根本不具备跨设备能力 —— 其他设备上的旧记录
            //    可以直接把它复活。删除是**终态**，这里先补一个身份（同一事务），再走统一的
            //    「归档 + 墓碑 + Delete 入队」路径，保证不会出现"状态已改、删除事件没写"的半完成态。
            if (globalId is null)
            {
                globalId = Guid.NewGuid().ToString("N");
                await conn.ExecuteAsync(
                    "UPDATE games SET global_id = @globalId, updated_at = CURRENT_TIMESTAMP WHERE id = @gameId;",
                    new { globalId, gameId }, tx);
            }

            var archivedCount = await conn.ExecuteAsync(
                """
                UPDATE games
                SET status = @archived, reactivated_at_utc = NULL, updated_at = CURRENT_TIMESTAMP
                WHERE id = @gameId AND LOWER(COALESCE(status, '')) <> @archived;
                """,
                new { archived = GameTimeTracker.Core.DeviceSync.GameStatuses.Archived, gameId }, tx);

            // 🛡️ 2026-10-11（身份协议）：删除的是**逻辑实体**，不是某一个 identity ⇒
            //    等价类里**每个** identity 都要写墓碑。否则对端那套被并入的 identity 的旧 Create
            //    重放会绕过墓碑，只能靠"同平台同名"弱线索兜底（那正是要消除的不确定性）。
            //    幂等（UNIQUE(entity_type, entity_global_id)），类内只有自己时是空操作。
            var deleteClass = ReadIdentityClassCore(conn, tx, new[] { globalId });
            if (deleteClass.Count > 1)
            {
                WriteIdentityClassTombstonesCore(conn, tx, deleteClass, ReadLocalDeviceId(conn, tx));
            }

            // 墓碑 + Delete 变更只认 global_id（跨设备身份）；上面已保证删除时 global_id 非空。
            //
            // ⚠️ 2026-10-11（幂等修复）：**只有真的把行改成归档时才入队**。旧实现无条件入队，
            //    重复点删除会给同一条 identity 堆出多条 Delete 变更（实测第二次删除产生第 2 条），
            //    既放大推送量，也让"删除已传播"的判断失去意义。墓碑写入本身是幂等的。
            if (archivedCount > 0)
            {
                // ⚠️ 2026-10-12：**删除时刻只取一次**，墓碑与队列行共用同一个值 ——
                //    接收端拿它写本地墓碑的 deleted_at_utc，推送守卫也拿它判定"比远端墓碑更新"。
                var deletedAt = UtcNowIso();

                WriteGameTombstone(conn, tx, globalId, deletedAt);

                // 删除事实入队（同一个事务 ⇒ 与本地归档状态一起提交，不会出现"归档了但没传播"）。
                // Payload 沿用与 Create 相同的 Game 快照（含 schema_version，保证 push 侧可校验）；
                // 接收端在 Delete 分支只读 EntityType/EntityGlobalId，不会用 payload 回写任何字段。
                //
                // 🛡️ 2026-10-11（旧版本兼容）：**等价类内每个 identity 各发一条 Delete**。
                //    旧版本设备无法按等价类解析，只会用"来件的 global_id 精确命中本机行"；
                //    只发本机 identity 的话，对端那行带着另一个 identity 就归档不掉
                //    （表现为"本机已删、对端界面还在"）。
                //
                // ⚠️ 2026-10-12（反复删除收敛）：queue_id 带上**本次删除时刻** ⇒ 每次真实删除各发一条。
                //    只用 delete:{identity} 的话，"删除 → 重新启用 → 再删除"的第二次会被静默丢掉，
                //    对端永远不知道该再次归档（真机实测"反复删除时对端不收敛 + 会话延后"）。
                EnqueueIdentityClassDeletesCore(
                    conn, tx, deleteClass,
                    (string)snapshot.name, (string)snapshot.platform, (string)snapshot.platform_id,
                    (string)snapshot.executable, (string)snapshot.executable_path,
                    (string?)snapshot.notion_page_id,
                    deletedAt);
            }

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ----------------- 删除留底与对账支持 -----------------

    public async Task<GameDeletionImpact> GetGameDeletionImpactAsync(int gameId)
    {
        using var conn = CreateConnection();

        var sessions = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sessions WHERE game_id = @gameId;", new { gameId });

        var daily = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM daily_summary WHERE game_id = @gameId;", new { gameId });

        var synced = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM daily_summary
            WHERE game_id = @gameId
              AND coalesce(notion_page_id, '') <> ''
              AND sync_status <> 'error';
            """,
            new { gameId });

        var pageId = await conn.ExecuteScalarAsync<string?>(
            "SELECT notion_page_id FROM games WHERE id = @gameId;", new { gameId });

        return new GameDeletionImpact
        {
            SessionCount = sessions,
            DailyRecordCount = daily,
            SyncedDailyRecordCount = synced,
            GameNotionPageId = pageId ?? string.Empty
        };
    }

    public async Task<IReadOnlyList<DailySummary>> GetSyncedDailySummariesAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<DailySummary>(
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE coalesce(d.notion_page_id, '') <> ''
            ORDER BY d.date DESC;
            """);
        return rows.ToList();
    }

    public async Task<DailySummary?> GetDailySummaryByIdAsync(int id)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<DailySummary>(
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.id = @id
            LIMIT 1;
            """,
            new { id });
    }

    public async Task<int> DeleteDailySummariesAsync(IEnumerable<int> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return 0;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            // 参数化 IN 子句：Dapper 展开 IEnumerable
            return await conn.ExecuteAsync(
                "DELETE FROM daily_summary WHERE id IN @ids;", new { ids = list });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ArchiveDeletedAsync(string kind, string source, string reference, string payloadJson)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO deleted_archive (kind, source, reference, payload_json)
                VALUES (@kind, @source, @reference, @payloadJson);
                """,
                new { kind, source, reference, payloadJson });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> DeleteCatalogItemsNotInAsync(IEnumerable<string> pageIds)
    {
        var list = pageIds
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace("-", "").ToLowerInvariant())
            .Distinct()
            .ToList();

        // 空集合时直接返回：`IN ()` 在 SQLite 里是语法错误，
        // 而且"一条都没有"本身就说明远端结果不可信，不该清库。
        if (list.Count == 0) return 0;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            // 比较时统一去掉连字符：Notion 返回带连字符，本地存的可能是任意一种写法。
            return await conn.ExecuteAsync(
                """
                DELETE FROM game_catalog
                WHERE lower(replace(page_id, '-', '')) NOT IN @ids;
                """,
                new { ids = list });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ----------------- Sessions -----------------

    public async Task<GameSession> CreateSessionAsync(int gameId, int pid, string processName, DateTime startTime)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            var id = await conn.ExecuteScalarAsync<int>(
                """
                INSERT INTO sessions (game_id, pid, process_name, start_time, last_heartbeat, duration_seconds, is_active)
                VALUES (@gameId, @pid, @processName, @startTime, @startTime, 0, 1);
                SELECT last_insert_rowid();
                """,
                new { gameId, pid, processName, startTime = startTime.ToString("yyyy-MM-dd HH:mm:ss") });

            return new GameSession
            {
                Id = id,
                GameId = gameId,
                Pid = pid,
                ProcessName = processName,
                StartTime = startTime,
                LastHeartbeat = startTime,
                DurationSeconds = 0,
                IsActive = true
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<GameSession>> GetActiveSessionsAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>("SELECT * FROM sessions WHERE is_active = 1;");
        var list = new List<GameSession>();
        foreach (var r in rows)
        {
            list.Add(new GameSession
            {
                Id = (int)r.id,
                GameId = (int)r.game_id,
                Pid = (int)r.pid,
                ProcessName = (string)r.process_name,
                StartTime = DateTime.Parse((string)r.start_time),
                EndTime = r.end_time != null ? DateTime.Parse((string)r.end_time) : null,
                LastHeartbeat = DateTime.Parse((string)r.last_heartbeat),
                DurationSeconds = (int)r.duration_seconds,
                IsActive = (long)r.is_active == 1
            });
        }
        return list;
    }

    public async Task UpdateSessionHeartbeatAsync(int sessionId, DateTime heartbeatTime, int durationSeconds)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE sessions 
                SET last_heartbeat = @heartbeat, duration_seconds = @durationSeconds
                WHERE id = @sessionId;
                """,
                new { sessionId, heartbeat = heartbeatTime.ToString("yyyy-MM-dd HH:mm:ss"), durationSeconds });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task EndSessionAsync(int sessionId, DateTime endTime, int durationSeconds)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE sessions 
                SET end_time = @endTime, duration_seconds = @durationSeconds, is_active = 0
                WHERE id = @sessionId;
                """,
                new { sessionId, endTime = endTime.ToString("yyyy-MM-dd HH:mm:ss"), durationSeconds });

            // 只有当会话时长 >= 60 秒（1分钟）时才计入有效游玩次数；低于 60 秒的微小启动忽略
            if (durationSeconds >= 60)
            {
                var sessionRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT game_id, substr(start_time, 1, 10) AS session_date FROM sessions WHERE id = @sessionId;",
                    new { sessionId });
                if (sessionRow != null)
                {
                    int gameId = (int)sessionRow.game_id;
                    string date = (string)sessionRow.session_date;
                    var validSessionsCount = await conn.ExecuteScalarAsync<int>(
                        """
                        SELECT COUNT(*) FROM sessions 
                        WHERE game_id = @gameId AND substr(start_time, 1, 10) = @date AND duration_seconds >= 60;
                        """,
                        new { gameId, date });

                    if (validSessionsCount > 0)
                    {
                        // 🔒 2026-10-11（口径统一，count-rule-v1）：遗留结束路径也走唯一规则，
                        //    不再使用"日历日计数 + manualBonus（daily > 会话和 + 60 ⇒ +1）"的旧公式
                        //    —— 该启发式既不可重算，也会制造无会话支撑的伪场次。
                        ApplyUnifiedSessionCount(conn, null, gameId, date, ReadDailyCutoffHour(conn, null));
                    }
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>僵尸会话收尾时的只读快照（<c>last_heartbeat</c> 即"最后一次已 flush 的时刻"）。</summary>
    public sealed class StaleSessionRow
    {
        public int Id { get; set; }
        public string LastHeartbeat { get; set; } = "";
        public int DurationSeconds { get; set; }
    }

    /// <summary>
    /// 把"卡住的活跃会话"（心跳超过阈值没更新）收尾。
    ///
    /// <para>
    /// 收尾语义与**正常结束路径同源**（走 <see cref="EndSessionWithOutboxAsync"/>，不另写一套）：
    /// <list type="bullet">
    ///   <item>写 <c>end_time = last_heartbeat</c>、<c>is_active = 0</c>（与旧行为一致）；</item>
    ///   <item>补 identity：<c>global_id / device_id / started_at_utc / ended_at_utc / time_precision</c>；</item>
    ///   <item>与会话结束**同事务**地产生 Session(+Game) 同步事实 —— 否则这局会变成"本地有历史、
    ///         Device Sync 永远不知道"的孤儿（用户 2026-10-08 裁定：Session 是 immutable historical fact，
    ///         崩溃/被杀/异常关机导致的收尾同样必须产出同步事实）；</item>
    ///   <item><b>尾段 daily 增量为 0</b>：心跳路径已经把时长 flush 到 <c>last_heartbeat</c>，
    ///         这里传空增量 → 不重算、不重复计入（与旧行为逐字一致）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ⚠️ 兜底：若同步事实写不进去（如事务/磁盘异常），**绝不让会话留在 active**（那正是"永远挂着"）——
    /// 退化为最小收尾（<c>is_active = 0</c> + <c>end_time = last_heartbeat</c>）；
    /// 缺的那条同步事实由「历史 Outbox Gap Repair」（<c>RepairOutboxGapsAsync</c>）在后续启动时补上。
    /// <b>本地计时永远优先于同步。</b>
    /// </para>
    ///
    /// 幂等：只处理 <c>is_active = 1 AND last_heartbeat &lt; threshold</c> 的行，重复调用不会重复入队。
    /// </summary>
    public async Task CleanupStaleSessionsAsync(TimeSpan staleThreshold)
    {
        // ① 只取"要收尾哪些会话"（短锁）。不能持着写锁去调 EndSessionWithOutboxAsync —— 它会自己再拿一次锁。
        List<StaleSessionRow> stale;
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            var thresholdTime = DateTime.Now.Subtract(staleThreshold).ToString("yyyy-MM-dd HH:mm:ss");
            stale = (await conn.QueryAsync<StaleSessionRow>(
                """
                SELECT id             AS Id,
                       last_heartbeat AS LastHeartbeat,
                       duration_seconds AS DurationSeconds
                FROM sessions
                WHERE is_active = 1 AND last_heartbeat < @thresholdTime
                ORDER BY id ASC;
                """,
                new { thresholdTime })).ToList();
        }
        finally
        {
            _writeLock.Release();
        }

        foreach (var row in stale)
        {
            var endTime = DateTime.Parse(row.LastHeartbeat);   // 与旧行为一致：结束时间取最后一次心跳
            var duration = Math.Max(0, row.DurationSeconds);   // 心跳已写过的时长，原样保留

            var result = await EndSessionWithOutboxAsync(
                row.Id, endTime, duration, Array.Empty<DailyDurationDelta>());

            if (result.SessionEnded) continue;

            // 同步事实没写成 → 仍然收尾（绝不留下永远 active 的会话），缺口交给 Gap Repair
            AppLog.Warn($"[设备同步] 僵尸会话 {row.Id} 的同步事实未写成，退化为最小收尾（缺口待 Gap Repair 补）");
            await ForceCloseSessionAsync(row.Id, endTime);
        }
    }

    /// <summary>
    /// 最小收尾（旧行为）：只写 <c>is_active = 0</c> + <c>end_time</c>。
    /// ⚠️ 只在"带 outbox 的完整收尾"失败时使用 —— 它会留下没有同步事实的缺口，
    /// 必须由 <c>RepairOutboxGapsAsync</c> 补齐（这也是 Gap Repair 存在的理由之一）。
    /// </summary>
    private async Task ForceCloseSessionAsync(int sessionId, DateTime endTime)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE sessions
                SET is_active = 0, end_time = @endTime
                WHERE id = @sessionId AND is_active = 1;
                """,
                new { sessionId, endTime = endTime.ToString("yyyy-MM-dd HH:mm:ss") });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ----------------- Daily Summaries -----------------

    public async Task AddSessionDurationToDailyAsync(string date, int gameId, int durationSeconds)
    {
        if (durationSeconds <= 0) return;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM daily_summary WHERE date = @date AND game_id = @gameId;",
                new { date, gameId });

            if (existing != null)
            {
                var newSecs = (int)existing.duration_seconds + durationSeconds;
                var newMins = newSecs / 60;
                await conn.ExecuteAsync(
                    """
                    UPDATE daily_summary
                    SET duration_seconds = @newSecs, duration_minutes = @newMins, sync_status = 'pending'
                    WHERE id = @id;
                    """,
                    new { id = existing.id, newSecs, newMins });
            }
            else
            {
                var mins = durationSeconds / 60;
                await conn.ExecuteAsync(
                    """
                    INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
                    VALUES (@date, @gameId, @durationSeconds, @mins, 0, 'pending');
                    """,
                    new { date, gameId, durationSeconds, mins });
            }

            // 🔒 2026-10-11（口径统一，count-rule-v1）：心跳路径**不再自己推算场次**
            //    （旧实现是 `duration_minutes=0 且新值>0 就置 1` 的启发式，会制造无会话支撑的伪场次）。
            //    场次一律由唯一规则按事实重算：≥60s 且起始会计日匹配的会话数 + 声明/残差 bonus。
            ApplyUnifiedSessionCount(conn, null, gameId, date, ReadDailyCutoffHour(conn, null));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DailySummary>> GetDailySummariesByDateAsync(string date)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            $"""
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.date = @date
              AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicateFor("g")}
            ORDER BY d.duration_minutes DESC;
            """,
            new { date });

        return rows.Select(MapDailySummary).ToList();
    }

    public async Task<IReadOnlyList<DailySummary>> GetDailySummariesRangeAsync(string startDate, string endDate)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            $"""
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.date >= @startDate AND d.date <= @endDate AND d.duration_seconds > 0
              AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicateFor("g")}
            ORDER BY d.date ASC;
            """,
            new { startDate, endDate });

        return rows.Select(MapDailySummary).ToList();
    }

    public async Task<int> GetConsecutiveStreakDaysAsync(DateTime referenceDate, int cutoffHour = 24)
    {
        using var conn = CreateConnection();
        var dates = (await conn.QueryAsync<string>(
            $"""
            SELECT date
            FROM daily_summary
            -- 存在性用秒：<60 秒的一天也算"玩过"，否则连续游玩会断链
            WHERE duration_seconds > 0
              AND game_id IN (SELECT id FROM games WHERE {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate})
            GROUP BY date
            ORDER BY date DESC;
            """)).ToHashSet();

        if (dates.Count == 0) return 0;

        var today = AccountingDateHelper.GetAccountingDate(referenceDate, cutoffHour);
        int streak = 0;
        var checkDate = today;

        if (dates.Contains(checkDate.ToString("yyyy-MM-dd")))
        {
            while (dates.Contains(checkDate.ToString("yyyy-MM-dd")))
            {
                streak++;
                checkDate = checkDate.AddDays(-1);
            }
        }
        else
        {
            checkDate = checkDate.AddDays(-1);
            while (dates.Contains(checkDate.ToString("yyyy-MM-dd")))
            {
                streak++;
                checkDate = checkDate.AddDays(-1);
            }
        }

        return streak;
    }

    public async Task<IReadOnlyDictionary<int, string>> GetEarliestPlayDatesAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            $"""
            SELECT game_id, MIN(date) AS first_date
            FROM daily_summary
            -- 首次游玩日期同样按秒判定（30 秒的首次会话也是"首次游玩"）
            WHERE duration_seconds > 0
              AND game_id IN (SELECT id FROM games WHERE {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate})
            GROUP BY game_id;
            """);

        var result = new Dictionary<int, string>();
        foreach (var r in rows)
        {
            if (r.game_id != null && r.first_date != null)
            {
                result[(int)r.game_id] = (string)r.first_date;
            }
        }
        return result;
    }

    /// <summary>
    /// 待上传的每日汇总（推送到 Notion 的候选）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里必须**排除 `unmapped`**，不能只写 `!= 'synced'`。
    ///
    /// `unmapped` 的语义是"已推到每日时长表，但游戏还没绑定总表"——
    /// 也就是说**这一条已经推完了**，只是没写 relation。
    /// 若把它当成待上传，会出现：推送 → 置 unmapped → 下轮 `unmapped != 'synced'` 又命中
    /// → 再 PATCH → 再置 unmapped …… **每轮同步把所有未绑定记录重打一遍，永远不停**。
    /// 用户刚配好 Notion、还没绑游戏的那段时间最容易撞上，且随天数线性变慢。
    ///
    /// 排除之后不会漏推：时长一旦增加，`AddSessionDurationToDailyAsync` 会把状态改回
    /// `pending`（拉取路径在"本地领先"时也会置 `pending`），自然重新进入本查询。
    /// </remarks>
    public async Task<IReadOnlyList<DailySummary>> GetPendingDailySummariesAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            $"""
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id, g.notion_page_id AS game_notion_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.sync_status NOT IN ('synced', 'unmapped') AND d.duration_minutes > 0
              AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicateFor("g")}
            ORDER BY d.date ASC;
            """);

        return rows.Select(MapDailySummary).ToList();
    }

    public async Task<IReadOnlyList<DailySummary>> GetDailySummariesByGameIdAsync(int gameId)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id, g.notion_page_id AS game_notion_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.game_id = @gameId
            ORDER BY d.date ASC;
            """,
            new { gameId });

        return rows.Select(MapDailySummary).ToList();
    }

    public async Task<IReadOnlyList<DailySummary>> GetUnmappedDailySummariesAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id, g.notion_page_id AS game_notion_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE g.status = 'active'
              AND (d.sync_status = 'unmapped' OR (g.notion_page_id IS NOT NULL AND g.notion_page_id != '' AND d.sync_status != 'synced'))
              AND d.duration_minutes > 0
            ORDER BY d.date ASC;
            """);

        return rows.Select(MapDailySummary).ToList();
    }

    public async Task UpdateDailySyncStatusAsync(int id, string syncStatus, string? notionPageId = null, string? errorMessage = null)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE daily_summary
                SET sync_status = @syncStatus, 
                    notion_page_id = COALESCE(@notionPageId, notion_page_id),
                    last_sync_at = CURRENT_TIMESTAMP,
                    error_message = @errorMessage
                WHERE id = @id;
                """,
                new { id, syncStatus, notionPageId, errorMessage });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DailySummary>> GetRecentDailyRecordsAsync(int limit = 10)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            $"""
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            LEFT JOIN (
                SELECT game_id, substr(end_time, 1, 10) AS play_date, MAX(end_time) AS last_end
                FROM sessions
                WHERE end_time IS NOT NULL
                GROUP BY game_id, substr(end_time, 1, 10)
            ) s ON s.game_id = d.game_id AND s.play_date = d.date
            WHERE d.duration_seconds > 0
              AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicateFor("g")}
            ORDER BY d.date DESC, COALESCE(s.last_end, '') DESC, d.id DESC
            LIMIT @limit;
            """,
            new { limit });

        return rows.Select(MapDailySummary).ToList();
    }

    /// <summary>
    /// 判定一个日期是否属于历史日期（早于 7 天前）。
    /// ⚠️ 2026-10-13 起，它**不再**决定"是否允许回刷时长/是否标记 pending"（那与日期无关，只取决于
    /// 本地是否存在尚未同步的变动）；现在它只用于手工**声明**的取值口径：历史日期优先采用 Notion 侧声明。
    /// </summary>
    private static bool IsHistoricalDate(string dateStr)
    {
        if (DateTime.TryParse(dateStr, out var dt))
        {
            return dt.Date < DateTime.Today.AddDays(-7);
        }
        return false;
    }

    /// <summary>
    /// <b>本地统一事实存储</b>（2026-10-10，用户裁定）：确保"承载导入事实的游戏"**既有 identity、又已进入 outbox**。
    ///
    /// <para>
    /// 两步缺一不可：
    /// <list type="number">
    ///   <item>没有 <c>global_id</c> ⇒ 这条 adjustment 会被入队判据跳过（只落库、不进 Device Sync）；</item>
    ///   <item>只补身份不推 <c>Game</c> ⇒ 对端落地时解析不到 <c>game_global_id</c>，永久 <c>dependency_missing</c>。
    ///         真实事故（Notion 手工新增记录换机后同步不过去）正是这两步一起缺。</item>
    /// </list>
    /// Notion Pull 期间新建的游戏**没有任何会话**，因此既有的 Gap Repair Game 类目
    /// （要求"本机产生过会话"）不会覆盖它 —— 这条路径必须自己保证。
    /// </para>
    ///
    /// <para>幂等：身份只补空值；队列行按"任何状态已存在即跳过" + 与 Gap Repair **同源**的确定性 <c>queue_id</c>。</para>
    /// </summary>
    private async Task EnsureGameSyncIdentityAndOutboxAsync(SqliteConnection conn, GameRecord game)
    {
        DeviceSyncSchema.AssignMissingGameGlobalIds(conn);
        DeviceSyncSchema.AssignGameGlobalIdIfMissing(conn, game.Id);

        var globalId = await conn.ExecuteScalarAsync<string?>(
            "SELECT global_id FROM games WHERE id = @id;", new { id = game.Id });

        if (string.IsNullOrWhiteSpace(globalId)) return;

        // ⚠️ 设备同步从未启用（或更老的库）时 sync_queue 可能还不存在 ——
        //    "补 outbox"绝不能把整条导入链拖垮（那会表现为"Notion 记录同步失败"且被静默吞掉）。
        if (await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_queue';") == 0)
        {
            return;
        }

        if (await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sync_queue WHERE entity_type = @type AND entity_global_id = @gid;",
                new { type = nameof(SyncEntityType.Game), gid = globalId }) > 0)
        {
            return;   // 任何状态（Pending/InFlight/Completed/Failed）都算"已在队列"，绝不重复入队
        }

        var fresh = await conn.QuerySingleOrDefaultAsync<GameRecord>(
                        "SELECT * FROM games WHERE id = @id;", new { id = game.Id })
                    ?? game;

        using var tx = conn.BeginTransaction();

        await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
        {
            QueueId = SyncHashing.BackfillQueueId(nameof(SyncEntityType.Game), globalId!),
            EntityType = nameof(SyncEntityType.Game),
            EntityGlobalId = globalId!,
            Operation = SyncOperation.Create,
            BaseVersion = 1,
            Payload = SyncPayloadFactory.Game(
                globalId!, fresh.Name, fresh.Platform, fresh.PlatformId,
                fresh.Executable, fresh.ExecutablePath, fresh.NotionPageId,
                coverBlobHash: TryRegisterLocalCoverBlob(conn, tx, fresh.Platform, fresh.PlatformId)),
            CreatedAtUtc = UtcNowIso(),
            Status = SyncQueueStatus.Pending
        });

        tx.Commit();
    }

    public async Task<int> SyncDailyRecordFromNotionAsync(NotionDailyRecordItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Date)) return 0;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();

            // 1. Resolve GameRecord
            GameRecord? game = null;
            if (!string.IsNullOrEmpty(item.GameMasterPageId))
            {
                game = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                    "SELECT * FROM games WHERE notion_page_id = @pageId "
                    + $"AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicateFor("games")} LIMIT 1;",
                    new { pageId = item.GameMasterPageId });

                if (game == null)
                {
                    // 目录缓存必须手工映射：identifiers_json / aliases_json 是 JSON 文本列，
                    // Dapper 的 QueryAsync<NotionGameCatalogItem> 映射不到 List<string> 属性，
                    // 「游戏标识」会永远是空列表 —— 导入时只能用随机 id，
                    // 结果就是"本地已有"和"运行中检测到"的同一款游戏变成两条记录。
                    var catRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
                        "SELECT * FROM game_catalog WHERE page_id = @pageId LIMIT 1;",
                        new { pageId = item.GameMasterPageId });

                    NotionGameCatalogItem? catItem = catRow == null
                        ? null
                        : new NotionGameCatalogItem
                        {
                            PageId = (string)catRow.page_id,
                            Name = (string)catRow.name,
                            Aliases = System.Text.Json.JsonSerializer
                                .Deserialize<List<string>>((string)catRow.aliases_json) ?? new(),
                            Identifiers = System.Text.Json.JsonSerializer
                                .Deserialize<List<string>>((string)catRow.identifiers_json) ?? new(),
                            CoverUrl = (string?)catRow.cover_url,
                            LastSyncedAt = ParseDateTime(catRow.last_synced_at)
                        };

                    if (catItem != null)
                    {
                        var platform = "steam";
                        var platformId = Guid.NewGuid().ToString("N")[..8];
                        if (catItem.Identifiers.Count > 0)
                        {
                            var ident = catItem.Identifiers[0];
                            if (ident.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
                            {
                                platform = "steam";
                                platformId = ident.Substring(6);
                            }
                            else if (ident.StartsWith("xbox:", StringComparison.OrdinalIgnoreCase))
                            {
                                platform = "xbox";
                                platformId = ident.Substring(5);
                            }
                        }
                        else
                        {
                            // 总表多数条目没填「游戏标识」，但封面常来自 Steam CDN
                            // （.../steam/apps/<appid>/...）。用真实 AppID 建行，
                            // 之后进程监控才能按 (platform, platform_id) 认到同一个游戏；
                            // 否则随机 id 会让"本地已有"和"运行中检测到"变成两条记录。
                            var coverAppId = GameTimeTracker.Core.Services.GameMatcher.ExtractSteamAppId(catItem.CoverUrl);
                            if (!string.IsNullOrEmpty(coverAppId))
                            {
                                platform = "steam";
                                platformId = coverAppId;
                            }
                        }

                        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                        // 唯一约束是 (platform, platform_id)：用户可能已有同 AppID 的本地行
                        // （比如之前手动注册过、或另一条总表条目解析出同一 AppID）。
                        // 必须复用而不是撞 UNIQUE —— SQLite Error 19 就是这么来的。
                        game = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                            "SELECT * FROM games WHERE platform = @platform AND platform_id = @platformId "
                            + $"AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate} LIMIT 1;",
                            new { platform, platformId });

                        if (game == null)
                        {
                            // 优先查找同名已有游戏行（不限是否已绑定，避免分身）
                            game = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                                "SELECT * FROM games WHERE LOWER(name) = LOWER(@name) "
                                + $"AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate} "
                                + "ORDER BY (CASE WHEN executable_path != '' THEN 0 ELSE 1 END), id ASC LIMIT 1;",
                                new { name = catItem.Name })
                                ?? await FindUnboundGameByNameAsync(conn, catItem.Name);

                            if (game != null && string.IsNullOrEmpty(game.NotionPageId) && !string.IsNullOrEmpty(item.GameMasterPageId))
                            {
                                await conn.ExecuteAsync("UPDATE games SET notion_page_id = @notionPageId WHERE id = @id;",
                                    new { notionPageId = item.GameMasterPageId, id = game.Id });
                                game.NotionPageId = item.GameMasterPageId;
                            }
                        }

                        if (game == null)
                        {
                            // 🛡️ 2026-10-11（已删除游戏复活修复）：上面的查找全部带 NormalGamePredicate
                            //    （排除 archived）⇒ 用户已删除的游戏查不到就 **新建一条 active 行**
                            //    ⇒ 同一款游戏被"另一个身份入口"复活。先找**已归档**的同款游戏：
                            //    命中则复用它（导入事实挂在归档行上、界面不显示），绝不新建 active。
                            var archivedId = FindArchivedGameForImport(conn, platform, platformId, item.GameMasterPageId, catItem.Name);
                            if (archivedId is not null)
                            {
                                game = await conn.QuerySingleOrDefaultAsync<GameRecord>(
                                    "SELECT * FROM games WHERE id = @id;", new { id = archivedId.Value });
                            }
                        }

                        if (game == null)
                        {
                            // 🛡️ 2026-10-11（身份模型审查）：弱线索（同平台同名）**不得**升级为强身份。
                            //    既不新建 active 副本（否则已删除游戏会以"另一个身份"复活），
                            //    也不复用别的归档行（会把导入事实挂到错误的实体上）。
                            //    只报告并跳过本条导入 —— 事实仍在 Notion 源侧，身份确认后可重新导入，不会丢失。
                            var weakArchived = FindArchivedGamesByPlatformAndNameWeak(conn, platform, catItem.Name);
                            if (weakArchived.Count > 0)
                            {
                                AppLog.Warn($"[Notion 导入] 「{catItem.Name}」存在同平台同名归档行 "
                                    + $"{string.Join(",", weakArchived)}，但身份未建立关联 ⇒ 跳过本条导入，等人工裁定");
                                return 0;
                            }
                        }

                        if (game == null)
                        {
                            var newId = await conn.ExecuteScalarAsync<int>(
                                """
                                INSERT INTO games (platform, platform_id, name, executable, executable_path, notion_page_id, status, created_at, updated_at)
                                VALUES (@platform, @platformId, @name, '', '', @notionPageId, 'active', @now, @now);
                                SELECT last_insert_rowid();
                                """,
                                new { platform, platformId, name = catItem.Name, notionPageId = item.GameMasterPageId, now });

                            game = await conn.QuerySingleOrDefaultAsync<GameRecord>("SELECT * FROM games WHERE id = @newId;", new { newId });
                        }
                    }
                }
            }

            if (game == null && !string.IsNullOrEmpty(item.GameTitle))
            {
                game = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                    "SELECT * FROM games WHERE LOWER(name) = LOWER(@title) "
                    + $"AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate} "
                    + "ORDER BY (CASE WHEN executable_path != '' THEN 0 ELSE 1 END), id ASC LIMIT 1;",
                    new { title = item.GameTitle })
                    ?? await FindUnboundGameByNameAsync(conn, item.GameTitle);

                if (game != null && string.IsNullOrEmpty(game.NotionPageId) && !string.IsNullOrEmpty(item.GameMasterPageId))
                {
                    await conn.ExecuteAsync("UPDATE games SET notion_page_id = @notionPageId WHERE id = @id;",
                        new { notionPageId = item.GameMasterPageId, id = game.Id });
                    game.NotionPageId = item.GameMasterPageId;
                }
            }

            if (game == null)
            {
                // 🛡️ 2026-10-11（已删除游戏复活修复）：同 4.6.1 —— 已删除游戏的同名 / 同 notion 页面记录
                //    不得通过本入口复活成 active 行，复用归档行即可（历史事实与状态都保留）。
                var archivedManualId = FindArchivedGameForImport(conn, "manual", null, item.GameMasterPageId, item.GameTitle);
                if (archivedManualId is not null)
                {
                    game = await conn.QuerySingleOrDefaultAsync<GameRecord>(
                        "SELECT * FROM games WHERE id = @id;", new { id = archivedManualId.Value });
                }
            }

            if (game == null)
            {
                // 🛡️ 同 4.6.1：同平台同名的归档行只能作为**弱线索报告**，不能复用、也不能据此新建 active。
                var weakArchivedManual = FindArchivedGamesByPlatformAndNameWeak(conn, "manual", item.GameTitle);
                if (weakArchivedManual.Count > 0)
                {
                    AppLog.Warn($"[Notion 导入] 「{item.GameTitle}」存在同平台同名归档行 "
                        + $"{string.Join(",", weakArchivedManual)}，但身份未建立关联 ⇒ 跳过本条导入，等人工裁定");
                    return 0;
                }
            }

            if (game == null)
            {
                var title = !string.IsNullOrEmpty(item.GameTitle) ? item.GameTitle : "未知游戏";
                var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                var platformId = Guid.NewGuid().ToString("N")[..8];
                // 这两个空串不能省：executable / executable_path 都是 NOT NULL，
                // 之前只给了 executable，漏掉 executable_path 会让整条 INSERT 报
                // SQLite Error 19，而调用方是 catch{} 吞掉的 —— 表现为"从 Notion 拉记录"静默失败。
                // 留空也正好让「按 exe 注册手动游戏」的循环跳过这些本地并不存在的游戏。
                var newId = await conn.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO games (platform, platform_id, name, executable, executable_path, notion_page_id, status, created_at, updated_at)
                    VALUES ('manual', @platformId, @title, '', '', @notionPageId, 'active', @now, @now);
                    SELECT last_insert_rowid();
                    """,
                    new { platformId, title, notionPageId = item.GameMasterPageId, now });

                game = await conn.QuerySingleOrDefaultAsync<GameRecord>("SELECT * FROM games WHERE id = @newId;", new { newId });
            }

            if (game == null) return 0;

            // ---- 本地优先（2026-10-10，用户裁定）：Pull 期间新建 / 尚无身份的 Game 必须**当场**拿到同步身份 ----
            // 入队要求 game.global_id 非空（DailyAdjustments.cs 的入队判据），所以身份必须在
            // "写 adjustment 并入队"**之前**就绪，否则这条导入事实只会落库、进不了 Device Sync。
            // 复用**唯一实现**（只补"可同步资格"且 global_id 仍为空的行；幂等 ⇒ 不会重复分配身份）。
            await EnsureGameSyncIdentityAndOutboxAsync(conn, game);

            // If game had no notion_page_id but this item has it, link it
            if (string.IsNullOrEmpty(game.NotionPageId) && !string.IsNullOrEmpty(item.GameMasterPageId))
            {
                await conn.ExecuteAsync(
                    "UPDATE games SET notion_page_id = @notionPageId WHERE id = @id;",
                    new { notionPageId = item.GameMasterPageId, id = game.Id });
            }

            // 2. Check if daily_summary exists
            int targetGameId = game.Id;
            string? rawTitle = string.IsNullOrWhiteSpace(item.RawTitle) ? null : item.RawTitle;
            bool isHistorical = IsHistoricalDate(item.Date);

            // ① 优先按 notion_page_id 精确查找（同一个 Notion 页面的拉取更新）
            var rowByPage = await conn.QueryFirstOrDefaultAsync<DailySummaryRow>(
                """
                SELECT id AS Id, date AS Date, game_id AS GameId, duration_seconds AS DurationSeconds, 
                       duration_minutes AS DurationMinutes, notion_page_id AS NotionPageId, sync_status AS SyncStatus 
                FROM daily_summary 
                WHERE notion_page_id = @pageId
                LIMIT 1;
                """,
                new { pageId = item.PageId });

            // ② 查询目标 (date, game_id) 是否在本地已存在行
            var rowByDateAndGame = await conn.QueryFirstOrDefaultAsync<DailySummaryRow>(
                """
                SELECT id AS Id, date AS Date, game_id AS GameId, duration_seconds AS DurationSeconds, 
                       duration_minutes AS DurationMinutes, notion_page_id AS NotionPageId, sync_status AS SyncStatus 
                FROM daily_summary 
                WHERE date = @date AND game_id = @targetGameId
                LIMIT 1;
                """,
                new { date = item.Date, targetGameId });

            // ───────────── v2：Notion 日时长是**增量事实**，不是整行的权威值 ─────────────
            //
            // 语义（2026-10-08 用户批准冻结）：
            //     daily_summary(game, 统计日) = Σ 会话派生秒数 + Σ adjustments(该 game 该日, **全部设备相加**)
            //
            // Notion 给的"当日总时长"里减去本地会话已经算过的部分，差额才是**手工/Notion 增量**；
            // 把它记成一条本机 adjustment（source=notion，identity 稳定 ⇒ 重复导入幂等）。
            // 这样"对端没有 Notion 也能拿到这段时长"是靠 Device Sync 的 v2 实体实现的，
            // **不是**把这个 daily 行当成远端状态推过去（那正是以前做不到跨设备的原因）。
            var derivedSecondsForDate = SumSessionDerivedSeconds(
                conn, null, targetGameId, item.Date, ReadDailyCutoffHour(conn, null));

            // canonical seconds（2026-10-09）：Notion 侧只有 0.01h（≈36 秒）分辨率，
            // **外部输入只能一次换算成秒**（round(hours × 3600)），绝不再经"小时→分钟→秒"中转，
            // 否则 7.79h 会先被截成 467 分钟、再换回去变成 7.78h。
            // 老数据/测试构造的 item 没有 DurationSeconds 时，退回分钟 × 60。
            var notionSeconds = item.DurationSeconds > 0
                ? item.DurationSeconds
                : item.DurationMinutes * 60;

            DailySummaryRow? existing = rowByPage ?? rowByDateAndGame;
            var existingDailySeconds = existing?.DurationSeconds ?? 0;

            bool inflatedByMinuteUnitBugForDay =
                item.DurationUnitIsMinutes &&
                item.DurationMinutes > 0 &&
                !string.IsNullOrEmpty(existing?.NotionPageId) &&
                existing!.DurationSeconds >= notionSeconds;

            // 该 (设备, 游戏, 日, notion) 声明的稳定 identity。
            var targetGameGlobalId = conn.ExecuteScalar<string?>(
                "SELECT global_id FROM games WHERE id = @id;", new { id = targetGameId });

            var notionStatementId = DailyAdjustmentGlobalId.For(
                ReadLocalDeviceId(conn, null),
                string.IsNullOrWhiteSpace(targetGameGlobalId) ? $"local-game-{targetGameId}" : targetGameGlobalId!,
                item.Date,
                DailyAdjustmentSources.Notion);

            // 读取本机这条声明 + **上次观察到的远端声明快照**（NULL = 还没有可靠快照）
            var existingNotionRow = conn.QuerySingleOrDefault<dynamic>(
                """
                SELECT duration_seconds AS DurationSeconds, observed_remote_seconds AS ObservedRemoteSeconds
                FROM daily_duration_adjustments WHERE adjustment_global_id = @id;
                """,
                new { id = notionStatementId });

            int? existingNotionStatement = existingNotionRow is null
                ? null
                : (int?)existingNotionRow.DurationSeconds;

            int? observedRemoteSeconds = existingNotionRow is null
                ? null
                : (int?)existingNotionRow.ObservedRemoteSeconds;

            // 🔒 2026-09-19 QA 事故规则：同一天**已绑定另一个** Notion 页面（2.2h 与 2.4h 两个条目）
            //    → 后到的重复页面绝不叠加、也不冲掉已收录的权威时长（否则两边 pageId 串号 + 循环累加）。
            var isDuplicatePageForSameDay =
                existingNotionStatement is not null
                && !string.IsNullOrEmpty(existing?.NotionPageId)
                && !string.Equals(existing!.NotionPageId, item.PageId, StringComparison.Ordinal);

            if (!isDuplicatePageForSameDay)
            {
                // ⚠️ 必须**排除本机自己的这一条声明**：它是"要被改写的目标"，不是"别人的事实"。
                //    否则更新会退化成"把增量加到旧值上"（45min → 60min 会变成 15min）。
                var ownStatementSeconds = existingNotionStatement ?? 0;
                var otherAdjustments = SumAdjustments(conn, null, targetGameId, item.Date) - ownStatementSeconds;

                // Notion 给的是"当天总时长" ⇒ 本机声明应为"扣除会话派生与其它设备事实之后"的余额。
                var notionStatementSeconds = Math.Max(
                    0, notionSeconds - derivedSecondsForDate - otherAdjustments);

                // 行内"人工声明"的下限 = 行值 − 会话派生 − 其它设备事实，也就是本机此前
                // 记在这一行上的人工秒数（含旧版本直接写行的值）。
                // ⚠️ 这不是"要清理的脏数据"：本地领先远端时必须靠它守住已记录的时长，
                //    否则每轮 Pull 都会把本地的 41 分钟洗成 Notion 上的旧 12 分钟。
                var localManualFloorSeconds = Math.Max(
                    0, existingDailySeconds - derivedSecondsForDate - otherAdjustments);

                // 历史日期 / 膨胀修正：完全尊崇 Notion 权威（允许把本地旧值改小）；
                // 近期日期：**绝不回退**（本地领先时不接受远端旧值）。
                //
                // 🔒 例外（2026-10-09，9/19 QA 规则）：远端**根本没有可用时长**（属性缺失 / 0 / 坏值）
                //    ⇒ 一律保留本地已记录的秒数，绝不拿 0 去覆盖 —— 历史行也不例外。
                //    "Notion 没有值" 不是"时长为 0"的证据。
                // ✅ 2026-10-10（用户裁定 §6）：**同一 identity 的声明以 Notion 为权威**（增与减都收敛）——
                //    但只有在"这一行完全由事实构成"时才适用，否则远端旧值会把本地进度吃掉。
                //
                //    判据 = `localManualFloorSeconds == 0`，即 **行值 == Σ 会话派生 + Σ 声明**：
                //      · 为 0 ⇒ 行里没有"无事实依据的秒数" ⇒ 用户可以放心改大/改小，本地跟着收敛；
                //      · > 0 ⇒ 本地还压着无法解释的进度（QA app(2).log：记录创建后 Notion 停在 12 min、
                //              本地一路涨到 41 min）⇒ 维持既有"本地领先不回退"，并把状态留 pending 以便回刷。
                //    历史日期 / 分钟膨胀修正本来就走 Notion 权威，不受此分支影响。
                // ⚠️ 2026-10-10 结论（本批次实测）：**"远端比本地低"这一状态无法区分两种成因** ——
                //    · 远端陈旧（QA app(2).log：Notion 停在 12 min、本地涨到 41 min，必须保住本地并回刷）；
                //    · 用户主动下调（§6：1200→1000 应该收敛到 1000）。
                //    两者在库里完全同形（row > 远端声明），本地没有"上一次远端声明"的持久化信号可供比对，
                //    因此**按本批次明确要求保留既有语义**：本地领先时不接受远端旧值（并留 pending 以便回刷）。
                //    §6 的"主动下调收敛"需要新增一个持久化的"上次远端声明快照"才能安全实现 —— 见报告"未完成项"。
                // ✅ 2026-10-10：**基于快照的变化检测** —— 只有"本次远端值与上次观察到的快照不同"才认定为
                //    用户改动了这条声明（增、减都接受并收敛）；快照缺失或未变化 ⇒ 维持既有"本地领先不回退"。
                //    ⚠️ 这是**轮询式**检测：程序两次观察之间发生的多次修改只能看到最后一次的净效果，
                //       更进一步的历史我们无法还原（不宣称能解决）。
                var remoteDeclarationChanged =
                    observedRemoteSeconds is int previousObserved
                    && Math.Abs(previousObserved - notionSeconds) > 1;   // 容差 1 秒，吸收两边整秒截断

                var statementSeconds = notionSeconds <= 0
                    ? localManualFloorSeconds
                    : remoteDeclarationChanged || isHistorical || inflatedByMinuteUnitBugForDay
                        ? notionStatementSeconds
                        : Math.Max(localManualFloorSeconds, notionStatementSeconds);

                await UpsertLocalAdjustmentWithOwnTransactionAsync(
                    conn, targetGameId, item.Date, statementSeconds, DailyAdjustmentSources.Notion,
                    observedRemoteSeconds: notionSeconds);
            }

            // ⚠️ 派生行（daily_summary）由**本方法自己**按同一公式（Σ 会话派生 + Σ 声明）写入 ——
            //    这里是它唯一的写入者。不要在这里再调 RecomputeDailySummaryForGameDate：
            //    那会先建行、随后本方法的 INSERT 撞 UNIQUE(date, game_id)（实测）。

            // 该行最终值 = 会话派生 + 全部设备/来源的 adjustments（**相加**，不是 max、不是 LWW）
            var authoritativeSeconds = derivedSecondsForDate
                + SumAdjustments(conn, null, targetGameId, item.Date);

            // 计算权威时长与同步状态
            // 🚨 关键修复：必须检查 Notion 页面端是否真正建立了关联 (item.GameMasterPageId)。
            // 如果 Notion 端未关联（虽然本地根据游戏名识别出了 game），状态必须设为 "unmapped"，
            // 这样 BackfillRelationsAsync 才能检测到并向 Notion 回写关联属性并标记为"已绑定"。
            bool isBoundOnNotion = !string.IsNullOrEmpty(item.GameMasterPageId);
            // 行值一律 = 会话派生 + Σ adjustments（见上面的 v2 段）。
            int finalSeconds = authoritativeSeconds;
            int finalMinutes = authoritativeSeconds / 60;
            string newStatus = isBoundOnNotion ? "synced" : "unmapped";

            if (existing != null)
            {
                int existingMinutes = existing.DurationMinutes;

                if (inflatedByMinuteUnitBugForDay)
                {
                    // 膨胀修正：新模型下"该行的正确值"已经由 facts 推出（派生 + adjustments），
                    // 不再需要把 notion 的分钟数直接写回去 —— 这里只补日志与状态。
                    newStatus = isBoundOnNotion ? "synced" : "unmapped";
                    AppLog.Warn(
                        $"[同步] 修正历史时长膨胀：「{item.GameTitle}」{item.Date} " +
                        $"本地 {existingMinutes} 分钟 → {finalMinutes} 分钟（旧版把分钟当成了小时）");
                }
                else
                {
                    // ⚖️ 2026-10-13 规则变更：状态**不再**看"记录是否早于 7 天"，只看
                    //    **是否存在尚未同步的本地变动** —— 本地行值确实高于 Notion 侧的值 ⇒ pending（进入推送队列）；
                    //    否则 synced/unmapped（推送侧据此跳过，不写远端）。
                    //    （原实现把历史日期一律置 synced ⇒ 历史记录上的真实未同步变动会被永久丢弃、永不回刷。）
                    // ⚠️ 比较基准是**本次算出的行值**（facts + 声明），与 Notion 侧的现值比较：
                    //    行值确实高于远端 ⇒ 还有未同步的变动 → pending；否则 synced/unmapped。
                    //    （用拉取前的旧值会误判"用户在 Notion 主动下调"这类场景 ⇒ 把已收敛的行又标成 pending。）
                    //    不写成 `existingMinutes` 的另一个原因：每轮 Pull 都必须能重新发现真正的待上传记录
                    //    （QA app(2).log 事故：被洗成 synced 后回刷永远查不到）。
                    newStatus = (finalMinutes > item.DurationMinutes) ? "pending" : (isBoundOnNotion ? "synced" : "unmapped");
                }
            }

            // ── 情况 1: 本地已有该 Notion 页面的记录 (rowByPage != null) ───────────
            if (rowByPage != null)
            {
                int oldGameId = rowByPage.GameId;

                // 🔒 2026-10-11（migration 011 兼容，定点修复）：这条每日记录从 oldGameId 搬到
                //    targetGameId 时，**oldGameId 上的时长声明必须一起搬走** —— 否则：
                //    ① 它变成谁也认领不了的孤儿声明（既不进统计、也不参与同步）；
                //    ② migration 011 的物理删除守卫会因此挡住幽灵行的清理（守卫要求三张事实表都空）。
                //    与 `MigrateGameDataToCanonical` 的搬移语义保持一致。
                if (oldGameId != targetGameId)
                {
                    await conn.ExecuteAsync(
                        "UPDATE daily_duration_adjustments SET game_id = @targetId WHERE game_id = @oldId;",
                        new { targetId = targetGameId, oldId = oldGameId });
                }

                // 如果目标 (item.Date, targetGameId) 已经有另一行（例如用户在 Notion 改了日期或关系，撞上了已有行）：
                if (rowByDateAndGame != null && rowByDateAndGame.Id != rowByPage.Id)
                {
                    // 🚨 冲突安全合并：绝不触发 UNIQUE constraint 崩溃！
                    // 把权威数据合入 rowByDateAndGame，并删除旧日期的旧行 rowByPage
                    await conn.ExecuteAsync(
                        """
                        UPDATE daily_summary
                        SET notion_page_id = @pageId,
                            duration_seconds = @finalSeconds,
                            duration_minutes = @finalMinutes,
                            notion_title = COALESCE(@title, notion_title),
                            sync_status = @newStatus,
                            last_sync_at = CURRENT_TIMESTAMP
                        WHERE id = @targetId;

                        DELETE FROM daily_summary WHERE id = @oldId;
                        """,
                        new
                        {
                            pageId = item.PageId,
                            finalSeconds,
                            finalMinutes,
                            title = rawTitle,
                            newStatus,
                            targetId = rowByDateAndGame.Id,
                            oldId = rowByPage.Id
                        });

                    if (oldGameId != targetGameId)
                    {
                        await TryDropGhostGameAsync(conn, oldGameId);
                    }
                    return rowByDateAndGame.Id;
                }
                else
                {
                    // 目标槽位无碰撞：直接更新 date 与 game_id（完整支持用户在 Notion 修改日期或绑定！）
                    await conn.ExecuteAsync(
                        """
                        UPDATE daily_summary
                        SET date = @date,
                            game_id = @targetGameId,
                            duration_seconds = @finalSeconds,
                            duration_minutes = @finalMinutes,
                            notion_title = COALESCE(@title, notion_title),
                            sync_status = @newStatus,
                            last_sync_at = CURRENT_TIMESTAMP
                        WHERE id = @id;
                        """,
                        new
                        {
                            date = item.Date,
                            targetGameId,
                            finalSeconds,
                            finalMinutes,
                            title = rawTitle,
                            newStatus,
                            id = rowByPage.Id
                        });

                    if (oldGameId != targetGameId)
                    {
                        await TryDropGhostGameAsync(conn, oldGameId);
                    }
                    return rowByPage.Id;
                }
            }

            // ── 情况 2: rowByPage == null，但目标 (item.Date, targetGameId) 已经存在行 ──
            if (rowByDateAndGame != null)
            {
                // 如果该行尚未绑定 Notion 页面（如心跳刚生成），认领绑定：
                if (string.IsNullOrEmpty(rowByDateAndGame.NotionPageId))
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE daily_summary
                        SET notion_page_id = @pageId,
                            duration_seconds = @finalSeconds,
                            duration_minutes = @finalMinutes,
                            notion_title = COALESCE(@title, notion_title),
                            sync_status = @newStatus,
                            last_sync_at = CURRENT_TIMESTAMP
                        WHERE id = @id;
                        """,
                        new
                        {
                            pageId = item.PageId,
                            finalSeconds,
                            finalMinutes,
                            title = rawTitle,
                            newStatus,
                            id = rowByDateAndGame.Id
                        });
                    return rowByDateAndGame.Id;
                }
                else
                {
                    // 同一天同一个游戏在 Notion 录入了多个页面：跳过覆盖，杜绝主键崩溃与循环累加
                    AppLog.Warn(
                        $"[同步] 检测到同一天同一游戏存在多个 Notion 页面：「{item.GameTitle}」{item.Date} " +
                        $"(已收录 pageId={rowByDateAndGame.NotionPageId}, 跳过同日重复页面 pageId={item.PageId})。请在 Notion 中核对并修正日期或合并。");
                    return rowByDateAndGame.Id;
                }
            }

            // ── 情况 3: 既没有该页面记录，目标 (date, game_id) 也是空的：全新插入 ────
            var insertedId = await conn.ExecuteScalarAsync<int>(
                """
                INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status, notion_page_id, notion_title, last_sync_at)
                VALUES (@date, @targetGameId, @finalSeconds, @finalMinutes, 1, @newStatus, @pageId, @title, CURRENT_TIMESTAMP);
                SELECT last_insert_rowid();
                """,
                new
                {
                    date = item.Date,
                    targetGameId,
                    finalSeconds,
                    finalMinutes,
                    newStatus,
                    pageId = item.PageId,
                    title = rawTitle
                });

            return insertedId;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 把某条 Notion 每日记录页面的标题与图标快照回写到本地
    /// （Pull 与推送后记录"我们所知远端现状"）。
    ///
    /// 只改 notion_title / notion_icon_url 两个快照字段 —— 不碰 sync_status，
    /// 否则会把"本地时长领先、正等上传"的 pending 状态洗掉。
    /// 返回受影响行数（0 表示本地没有这条记录）。
    /// </summary>
    /// <remarks>
    /// 2026-10-13 起普通同步**不再**回刷历史标题（自动回刷链路已移除），
    /// 快照只作为"远端现状"的本地记录；历史标题的批量规范化只走设置页手工入口。
    /// </remarks>
    public async Task<int> UpdateDailyRecordFromNotionAsync(string notionPageId, string title, string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(notionPageId)) return 0;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();

            // 连字符不敏感：不能假设传入值与 notion_page_id 的存储形式一致（见
            // GetCatalogItemByPageIdAsync 的同类说明）。写错了不会报错、只会静默 0 行，
            // 表现成"回刷后快照没落库 → 下一轮又判定需要回刷 → 每轮都白打一次 Notion"。
            var normalized = notionPageId.Replace("-", "");
            return await conn.ExecuteAsync(
                """
                UPDATE daily_summary
                SET notion_title = @title,
                    notion_icon_url = @iconUrl
                WHERE notion_page_id = @notionPageId
                   OR REPLACE(notion_page_id, '-', '') = @normalized;
                """,
                new { notionPageId, normalized, title, iconUrl });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DailySummary>> GetTopGamesByDateAsync(string date, int limit = 5)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            $"""
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.date = @date AND d.duration_seconds > 0
              AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicateFor("g")}
            ORDER BY d.duration_seconds DESC
            LIMIT @limit;
            """,
            new { date, limit });

        return rows.Select(MapDailySummary).ToList();
    }

    /// <summary>
    /// 从 Dapper 的 dynamic 行里安全取一个可能不存在的列。
    /// </summary>
    /// <remarks>
    /// 存在的理由：`notion_title` 是后加的列，用 ALTER TABLE 补。
    /// 不是所有查询都 SELECT 它（有的写死列名），Dapper 的 DynamicRow 访问不存在的属性会抛异常 ——
    /// 而 MapDailySummary 是所有每日汇总查询的公共出口，一处漏掉就会让整条查询挂掉。
    /// </remarks>
    private static string? TryGetString(dynamic row, string column)
    {
        try
        {
            var dict = (IDictionary<string, object>)row;
            return dict.TryGetValue(column, out var v) ? v as string : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime ParseDateTime(object? value)
    {
        if (value == null || value is DBNull) return DateTime.UtcNow;
        if (value is DateTime dt) return dt;
        var s = value.ToString();
        if (string.IsNullOrWhiteSpace(s)) return DateTime.UtcNow;
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var result)
            ? result
            : (DateTime.TryParse(s, out var fallback) ? fallback : DateTime.UtcNow);
    }

    private static DateTime? ParseDateTimeNullable(object? value)
    {
        if (value == null || value is DBNull) return null;
        if (value is DateTime dt) return dt;
        var s = value.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var result)
            ? result
            : (DateTime.TryParse(s, out var fallback) ? fallback : null);
    }

    private static DailySummary MapDailySummary(dynamic r)
    {
        return new DailySummary
        {
            Id = (int)r.id,
            Date = (string)r.date,
            GameId = (int)r.game_id,
            DurationSeconds = (int)r.duration_seconds,
            DurationMinutes = (int)r.duration_minutes,
            SessionCount = (int)r.session_count,
            SyncStatus = (string)r.sync_status,
            NotionPageId = (string?)r.notion_page_id,
            LastSyncAt = ParseDateTimeNullable(r.last_sync_at),
            ErrorMessage = (string?)r.error_message,
            NotionTitle = TryGetString(r, "notion_title"),
            NotionIconUrl = TryGetString(r, "notion_icon_url"),
            GameName = (string)r.game_name,
            Platform = (string)r.platform,
            PlatformId = (string)r.platform_id
        };
    }

    /// <summary>
    /// 按**归一化名字**在本地找一条尚未绑定的游戏行。
    ///
    /// 用途：把 Notion 侧解析出来的官方名（总表条目名）对到本地已有的游戏上，
    /// 避免因为写法差异（标点、大小写、罗马数字、年份/版本后缀、时长后缀）重复建行。
    ///
    /// ⚠️ 只匹配**未绑定**的行。已绑定的行各有归属，不能被抢。
    ///
    /// 为什么必须有这一步（2026-09-19 用户反馈）：
    /// 用户手写的每日记录标题是「黑旗10.1h 通关」这种形态，
    /// 而本地游戏很可能是「Assassin's Creed IV Black Flag」或「刺客信条4：黑旗」，
    /// 两者用精确同名比对永远对不上 —— 于是每次拉取都新建一行，
    /// 老行永远停在「未绑定」，用户看到的就是「映射库里一堆未绑定」
    /// 和「待处理」上不断上涨的数字。
    /// 而 relation 指向的总表条目名是权威的，用它去认就有落点。
    /// </summary>
    private static async Task<GameRecord?> FindUnboundGameByNameAsync(SqliteConnection conn, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var target = Matcher.NormalizeTitle(name);
        if (string.IsNullOrEmpty(target)) return null;

        // ⚠️ 2026-10-11：必须排除已归档（用户已删除）的行，否则"未绑定同名兜底"会把
        //    已删除的游戏重新当成活动实体复用（Notion 导入路径的复活向量）。
        var unbound = await conn.QueryAsync<GameRecord>(
            "SELECT * FROM games WHERE coalesce(notion_page_id, '') = '' "
            + $"AND {GameTimeTracker.Core.DeviceSync.GameStatuses.NormalGamePredicate};");

        // 两边都要剥一次时长后缀再比。
        // 幽灵行的名字是**旧版本程序**写进去的（那时 StripSuffix 还认不出
        // 「致命躯壳2.2h」这种紧贴写法），所以库里存的就是带后缀的原串；
        // 而这次解析出来的名字已经被剥离过。只比一边永远对不上。
        foreach (var g in unbound)
        {
            if (Matches(g.Name) || Matches(g.Executable)) return g;
        }

        return null;

        bool Matches(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var stripped = GameTimeTracker.Infrastructure.Notion.DailyRecordTitle.StripSuffix(raw);
            return Matcher.NormalizeTitle(stripped) == target;
        }
    }

    /// <summary>
    /// 清掉一个「幽灵游戏行」：从 Notion 导入时因为读不到 relation 而凭空建出来的行。
    /// 只有它确实**空掉**之后才删，四个条件缺一不可：
    ///   · 未绑定总表（有关系的行有归属，不能动）
    ///   · 没有可执行文件（有 exe 的说明是「添加游戏」加进来的本机游戏，不能动）
    ///   · 没有每日记录
    ///   · 没有会话记录
    /// 删除前先写 deleted_archive —— 那是以后排查"记录怎么没了"的唯一线索。
    ///
    /// ⚠️ 必须在**已持有 _writeLock** 的调用方内部使用，所以这里直接收 SqliteConnection，
    /// 不复用 public 的 ArchiveDeletedAsync（它会再取一次信号量 → 死锁）。
    /// </summary>
    private static async Task TryDropGhostGameAsync(SqliteConnection conn, int gameId)
    {
        var game = await conn.QueryFirstOrDefaultAsync<GameRecord>(
            "SELECT * FROM games WHERE id = @id;", new { id = gameId });
        if (game is null) return;

        if (!string.IsNullOrEmpty(game.NotionPageId)) return;
        if (!string.IsNullOrEmpty(game.Executable) || !string.IsNullOrEmpty(game.ExecutablePath)) return;

        // 🛡️ 2026-10-14（孤儿会话收口）：三张事实表逐一核对，且**不再静默 return** ——
        //    任何一张仍有引用就保留游戏行，并把"为什么没清掉"写进 Info 日志（原实现直接 return，
        //    排查时看不出是哪个引用挡住了清理）。
        var dailyRefs = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM daily_summary WHERE game_id = @id;", new { id = gameId });
        var sessionRefs = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sessions WHERE game_id = @id;", new { id = gameId });

        // ⚠️ 2026-10-11：adjustment 也是**持久化事实**（它没有外键，物理删 games 不会连带删它，
        //    但会把它变成谁也认领不了的孤儿）。migration 011 的守卫已把"仍有 adjustment 的 games 行"
        //    也纳入保护 ⇒ 幽灵行判定必须同样要求"无 adjustment"，否则这条合法清理会被守卫挡住。
        var adjustmentRefs = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM daily_duration_adjustments WHERE game_id = @id;", new { id = gameId });

        if (dailyRefs > 0 || sessionRefs > 0 || adjustmentRefs > 0)
        {
            AppLog.Info($"[同步] 保留疑似幽灵游戏行「{game.Name}」(id={gameId})：仍被事实引用 —— "
                        + $"daily_summary={dailyRefs}、sessions={sessionRefs}、daily_duration_adjustments={adjustmentRefs}"
                        + "；物理删除会留下悬挂引用（孤儿），等这些事实被迁移/清理后再重新评估");
            return;
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO deleted_archive (kind, source, reference, payload_json)
            VALUES ('game', 'ghost', @reference, @payload);
            """,
            new
            {
                reference = game.Name,
                payload = JsonSerializer.Serialize(new
                {
                    game.Id,
                    game.Platform,
                    game.PlatformId,
                    game.Name,
                    reason = "未绑定 + 无记录 + 无可执行文件，判定为导入期产生的幽灵行（2026-09-19 自动清理）"
                })
            });

        await conn.ExecuteAsync("DELETE FROM games WHERE id = @id;", new { id = gameId });

        AppLog.Info($"[同步] 清理幽灵游戏行「{game.Name}」(id={gameId})：未绑定、无记录、无可执行文件");
    }

    // ----------------- Notion Game Master Cache -----------------

    public async Task<IReadOnlyList<NotionGameCatalogItem>> GetCatalogItemsAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>("SELECT * FROM game_catalog;");
        var list = new List<NotionGameCatalogItem>();
        foreach (var r in rows)
        {
            List<string> aliases = new();
            List<string> identifiers = new();
            List<string> genres = new();
            var aJson = TryGetString(r, "aliases_json");
            if (!string.IsNullOrWhiteSpace(aJson))
            {
                try { aliases = JsonSerializer.Deserialize<List<string>>(aJson) ?? new List<string>(); } catch { }
            }
            var iJson = TryGetString(r, "identifiers_json");
            if (!string.IsNullOrWhiteSpace(iJson))
            {
                try { identifiers = JsonSerializer.Deserialize<List<string>>(iJson) ?? new List<string>(); } catch { }
            }
            var gJson = TryGetString(r, "genres_json");
            if (!string.IsNullOrWhiteSpace(gJson))
            {
                try { genres = JsonSerializer.Deserialize<List<string>>(gJson) ?? new List<string>(); } catch { }
            }

            list.Add(new NotionGameCatalogItem
            {
                PageId = (string)r.page_id,
                Name = (string)r.name,
                Aliases = aliases,
                Identifiers = identifiers,
                Genres = genres,
                CoverUrl = (string?)r.cover_url,
                IconUrl = (string?)r.icon_url,
                IconType = (string?)r.icon_type,
                LastSyncedAt = ParseDateTime(r.last_synced_at)
            });
        }
        return list;
    }

    public async Task<NotionGameCatalogItem?> GetCatalogItemByPageIdAsync(string pageId)
    {
        using var conn = CreateConnection();

        // 连字符不敏感匹配：Notion 的 page id 有时带连字符（8-4-4-4-12）有时不带，
        // 而 games.notion_page_id 与 game_catalog.page_id 的来源路径不同，
        // 不能假设两边形式一致。只做精确匹配的话，不一致时会静默返回 null，
        // 表现为"每日记录用了进程名而不是总表名、图标也没了"——很难查。
        // （EnsureMasterPageIconsAsync 里手工 Replace("-","") 就是踩过这个坑的痕迹。）
        var normalized = pageId.Replace("-", "");
        var r = await conn.QueryFirstOrDefaultAsync<dynamic>(
            """
            SELECT * FROM game_catalog
            WHERE page_id = @pageId
               OR REPLACE(page_id, '-', '') = @normalized
            LIMIT 1;
            """,
            new { pageId, normalized });

        if (r == null) return null;

        List<string> aliases = new();
        List<string> identifiers = new();
        List<string> genres = new();
        var aJson = TryGetString(r, "aliases_json");
        if (!string.IsNullOrWhiteSpace(aJson))
        {
            try { aliases = JsonSerializer.Deserialize<List<string>>(aJson) ?? new List<string>(); } catch { }
        }
        var iJson = TryGetString(r, "identifiers_json");
        if (!string.IsNullOrWhiteSpace(iJson))
        {
            try { identifiers = JsonSerializer.Deserialize<List<string>>(iJson) ?? new List<string>(); } catch { }
        }
        var gJson = TryGetString(r, "genres_json");
        if (!string.IsNullOrWhiteSpace(gJson))
        {
            try { genres = JsonSerializer.Deserialize<List<string>>(gJson) ?? new List<string>(); } catch { }
        }

        return new NotionGameCatalogItem
        {
            PageId = (string)r.page_id,
            Name = (string)r.name,
            Aliases = aliases,
            Identifiers = identifiers,
            Genres = genres,
            CoverUrl = (string?)r.cover_url,
            IconUrl = (string?)r.icon_url,
            IconType = (string?)r.icon_type,
            LastSyncedAt = ParseDateTime(r.last_synced_at)
        };
    }

    public async Task UpsertCatalogItemsAsync(IEnumerable<NotionGameCatalogItem> items)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            foreach (var item in items)
            {
                var aliasesJson = JsonSerializer.Serialize(item.Aliases);
                var identsJson = JsonSerializer.Serialize(item.Identifiers);
                var genresJson = JsonSerializer.Serialize(item.Genres);
                await conn.ExecuteAsync(
                    """
                    INSERT INTO game_catalog (page_id, name, aliases_json, identifiers_json, genres_json, cover_url, icon_url, icon_type, last_synced_at)
                    VALUES (@PageId, @Name, @aliasesJson, @identsJson, @genresJson, @CoverUrl, @IconUrl, @IconType, CURRENT_TIMESTAMP)
                    ON CONFLICT(page_id) DO UPDATE SET
                        name = excluded.name,
                        aliases_json = excluded.aliases_json,
                        identifiers_json = excluded.identifiers_json,
                        genres_json = excluded.genres_json,
                        cover_url = excluded.cover_url,
                        icon_url = excluded.icon_url,
                        icon_type = excluded.icon_type,
                        last_synced_at = CURRENT_TIMESTAMP;
                    """,
                    new { item.PageId, item.Name, aliasesJson, identsJson, genresJson, item.CoverUrl, item.IconUrl, item.IconType },
                    transaction: tx);
            }
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearCatalogCacheAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync("DELETE FROM game_catalog;");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<Dictionary<string, List<string>>> GetGameGenresMapAsync()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT g.name AS GameName, c.genres_json AS GenresJson
                FROM games g
                JOIN game_catalog c ON (
                    (g.notion_page_id IS NOT NULL AND g.notion_page_id != '' AND (g.notion_page_id = c.page_id OR REPLACE(g.notion_page_id, '-', '') = REPLACE(c.page_id, '-', '')))
                    OR (LOWER(TRIM(g.name)) = LOWER(TRIM(c.name)))
                )
                WHERE c.genres_json IS NOT NULL AND c.genres_json != '' AND c.genres_json != '[]';
                """);

            foreach (var r in rows)
            {
                string? gameName = TryGetString(r, "GameName");
                string? gJson = TryGetString(r, "GenresJson");
                if (!string.IsNullOrWhiteSpace(gameName) && !string.IsNullOrWhiteSpace(gJson))
                {
                    try
                    {
                        var genres = JsonSerializer.Deserialize<List<string>>(gJson);
                        if (genres != null && genres.Count > 0)
                        {
                            map[gameName.Trim()] = genres;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return map;
    }


    // ----------------- Settings -----------------

    public async Task<string?> GetSettingAsync(string key)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = @key;", new { key });
    }

    /// <summary>
    /// 按 <c>sessions.process_name</c> 统计次数 —— 本体裁决的历史证据。
    /// **只读**：不写库、不改 schema（2026-10-07 C2）。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetSessionProcessCountsAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<(string ProcessName, int Cnt)>(
            "SELECT process_name AS ProcessName, COUNT(*) AS Cnt FROM sessions GROUP BY process_name;");
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, cnt) in rows)
        {
            if (!string.IsNullOrWhiteSpace(name)) map[name] = cnt;
        }
        return map;
    }

    /// <summary>
    /// 删除一个 settings 键。用于「封面 → FileUpload 映射」失效时清掉旧值，让下次同步重新上传。
    /// </summary>
    public async Task DeleteSettingAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync("DELETE FROM settings WHERE key = @key;", new { key });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SetSettingAsync(string key, string value)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO settings (key, value, updated_at)
                VALUES (@key, @value, CURRENT_TIMESTAMP)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = CURRENT_TIMESTAMP;
                """,
                new { key, value });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private sealed class DailySummaryRow
    {
        public int Id { get; set; }
        public string Date { get; set; } = "";
        public int GameId { get; set; }
        public int DurationSeconds { get; set; }
        public int DurationMinutes { get; set; }
        public int SessionCount { get; set; } = 1;
        public string? NotionPageId { get; set; }
        public string? SyncStatus { get; set; }
        public string? NotionTitle { get; set; }
        public string? NotionIconUrl { get; set; }
    }

    private sealed class DailyDurationCheck
    {
        public int duration_seconds { get; set; }
        public int session_count { get; set; }
    }
}

