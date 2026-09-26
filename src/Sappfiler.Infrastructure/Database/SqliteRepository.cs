using System.Data;
using System.Text.Json;
using Dapper;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

public class SqliteRepository : IDatabaseRepository
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
            -- kind: 'game' | 'daily'   source: 'app'（在程序里删）| 'notion'（对账发现远端已删）
            CREATE TABLE IF NOT EXISTS deleted_archive (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                source TEXT NOT NULL,
                reference TEXT,
                payload_json TEXT NOT NULL,
                deleted_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_deleted_archive_time ON deleted_archive(deleted_at);

            CREATE TABLE IF NOT EXISTS sync_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                daily_summary_id INTEGER NOT NULL REFERENCES daily_summary(id) ON DELETE CASCADE,
                provider TEXT NOT NULL,
                remote_id TEXT,
                status TEXT NOT NULL DEFAULT 'pending',
                retry_count INTEGER NOT NULL DEFAULT 0,
                last_sync_at DATETIME,
                error_message TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(daily_summary_id, provider)
            );
            CREATE INDEX IF NOT EXISTS idx_sync_records_status ON sync_records(provider, status);
            CREATE INDEX IF NOT EXISTS idx_sync_records_daily ON sync_records(daily_summary_id);

            CREATE TABLE IF NOT EXISTS game_mappings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                provider TEXT NOT NULL,
                remote_id TEXT NOT NULL,
                remote_name TEXT,
                last_verified DATETIME,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(game_id, provider)
            );
            CREATE INDEX IF NOT EXISTS idx_game_mappings_provider ON game_mappings(provider, remote_id);
            CREATE INDEX IF NOT EXISTS idx_game_mappings_game ON game_mappings(game_id);

            CREATE TABLE IF NOT EXISTS provider_configs (
                provider TEXT PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 0,
                config_json TEXT NOT NULL DEFAULT '{}',
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
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

        try
        {
            using var alterCmd8 = conn.CreateCommand();
            alterCmd8.CommandText = "ALTER TABLE game_mappings ADD COLUMN remote_locator TEXT;";
            alterCmd8.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterCmd9 = conn.CreateCommand();
            alterCmd9.CommandText = "ALTER TABLE game_mappings ADD COLUMN match_type TEXT;";
            alterCmd9.ExecuteNonQuery();
        }
        catch { }

        try
        {
            using var alterCmd10 = conn.CreateCommand();
            alterCmd10.CommandText = "ALTER TABLE game_mappings ADD COLUMN match_confidence REAL;";
            alterCmd10.ExecuteNonQuery();
        }
        catch { }

        // 平滑数据迁移：将已有的 Notion 数据导入多后端新表（支持历史数据无缝升级）
        try
        {
            using var migCmd = conn.CreateCommand();
            migCmd.CommandText = """
                INSERT OR IGNORE INTO game_mappings (game_id, provider, remote_id, remote_name, created_at)
                SELECT id, 'notion', notion_page_id, name, created_at
                FROM games
                WHERE notion_page_id IS NOT NULL AND TRIM(notion_page_id) != '';

                INSERT OR IGNORE INTO sync_records (daily_summary_id, provider, remote_id, status, last_sync_at, created_at)
                SELECT id, 'notion', notion_page_id, COALESCE(sync_status, 'pending'), last_sync_at, created_at
                FROM daily_summary;
            """;
            migCmd.ExecuteNonQuery();
        }
        catch { }

        // 启动时自动检查并合并同名/同总表关联的分裂游戏条目及同日时长记录
        DeduplicateGamesAndDailySummaries(conn);
    }


    private static string NormalizePageId(string? pageId)
        => string.IsNullOrWhiteSpace(pageId)
            ? string.Empty
            : pageId.Replace("-", string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// 自动合并重复游戏条目（同 notion_page_id 或同名），将 sessions 与 daily_summary 汇总迁移并清理冗余游戏行。
    /// </summary>
    private void DeduplicateGamesAndDailySummaries(SqliteConnection conn)
    {
        try
        {
            var allGames = conn.Query<GameRecord>("SELECT * FROM games ORDER BY id ASC;").ToList();
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

                    if (sameNotion || sameName)
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
                    if (!string.IsNullOrWhiteSpace(g.Platform) && g.Platform != "manual" && g.PlatformId.Length != 8 && g.PlatformId.Length != 16) s += 50;
                    if (!string.IsNullOrWhiteSpace(g.NotionPageId)) s += 20;
                    if (!string.IsNullOrWhiteSpace(g.CoverUrl)) s += 10;
                    return s;
                }

                var canonical = group.OrderByDescending(Score).ThenBy(g => g.Id).First();
                var duplicates = group.Where(g => g.Id != canonical.Id).ToList();

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
                    if ((bestPlatform == "manual" || bestPlatformId.Length == 8 || bestPlatformId.Length == 16) &&
                        (!string.IsNullOrEmpty(dup.Platform) && dup.Platform != "manual" && dup.PlatformId.Length != 8 && dup.PlatformId.Length != 16))
                    {
                        bestPlatform = dup.Platform;
                        bestPlatformId = dup.PlatformId;
                    }
                }

                conn.Execute("""
                    UPDATE games
                    SET notion_page_id = @bestNotionId,
                        cover_url = @bestCoverUrl,
                        executable = @bestExe,
                        executable_path = @bestExePath,
                        platform = @bestPlatform,
                        platform_id = @bestPlatformId,
                        updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id;
                    """,
                    new { bestNotionId, bestCoverUrl, bestExe, bestExePath, bestPlatform, bestPlatformId, id = canonical.Id });

                foreach (var dup in duplicates)
                {
                    conn.Execute("UPDATE sessions SET game_id = @canonicalId WHERE game_id = @dupId;",
                        new { canonicalId = canonical.Id, dupId = dup.Id });

                    var dupSummaries = conn.Query<DailySummaryRow>(
                        "SELECT id AS Id, date AS Date, game_id AS GameId, duration_seconds AS DurationSeconds, duration_minutes AS DurationMinutes, session_count AS SessionCount, sync_status AS SyncStatus, notion_page_id AS NotionPageId, notion_title AS NotionTitle, notion_icon_url AS NotionIconUrl FROM daily_summary WHERE game_id = @dupId;",
                        new { dupId = dup.Id }).ToList();

                    foreach (var ds in dupSummaries)
                    {
                        var targetRow = conn.QueryFirstOrDefault<DailySummaryRow>(
                            "SELECT id AS Id, date AS Date, game_id AS GameId, duration_seconds AS DurationSeconds, duration_minutes AS DurationMinutes, session_count AS SessionCount, sync_status AS SyncStatus, notion_page_id AS NotionPageId, notion_title AS NotionTitle, notion_icon_url AS NotionIconUrl FROM daily_summary WHERE date = @date AND game_id = @canonicalId LIMIT 1;",
                            new { date = ds.Date, canonicalId = canonical.Id });

                        if (targetRow != null)
                        {
                            int combinedSecs = targetRow.DurationSeconds + ds.DurationSeconds;
                            int combinedMins = combinedSecs / 60;
                            int combinedSessions = targetRow.SessionCount + ds.SessionCount;
                            string? finalPageId = !string.IsNullOrEmpty(targetRow.NotionPageId) ? targetRow.NotionPageId : ds.NotionPageId;
                            string? finalTitle = !string.IsNullOrEmpty(targetRow.NotionTitle) ? targetRow.NotionTitle : ds.NotionTitle;
                            string? finalIcon = !string.IsNullOrEmpty(targetRow.NotionIconUrl) ? targetRow.NotionIconUrl : ds.NotionIconUrl;
                            string finalStatus = (targetRow.SyncStatus == "synced" && ds.SyncStatus == "synced") ? "synced" : "pending";

                            conn.Execute("""
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
                                new { combinedSecs, combinedMins, combinedSessions, finalPageId, finalTitle, finalIcon, finalStatus, targetId = targetRow.Id, dupDailyId = ds.Id });
                        }
                        else
                        {
                            conn.Execute("UPDATE daily_summary SET game_id = @canonicalId WHERE id = @id;",
                                new { canonicalId = canonical.Id, id = ds.Id });
                        }
                    }

                    conn.Execute("DELETE FROM games WHERE id = @dupId;", new { dupId = dup.Id });
                    AppLog.Info($"[维护] 自动合并重复游戏记录：「{canonical.Name}」(保留 id={canonical.Id}, 清理重复 id={dup.Id})");
                }
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
                "SELECT * FROM games WHERE LOWER(platform) = LOWER(@Platform) AND LOWER(platform_id) = LOWER(@PlatformId) ORDER BY (CASE WHEN notion_page_id IS NOT NULL AND notion_page_id != '' THEN 0 ELSE 1 END), id ASC;",
                new { identity.Platform, identity.PlatformId });

            // 2. Fallback: match by executable path or executable name
            if (existing == null && (!string.IsNullOrEmpty(identity.ExecutablePath) || !string.IsNullOrEmpty(identity.Executable)))
            {
                existing = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                    """
                    SELECT * FROM games 
                    WHERE (LOWER(executable_path) = LOWER(@ExecutablePath) AND @ExecutablePath != '') 
                       OR (LOWER(executable) = LOWER(@Executable) AND @Executable != '')
                    ORDER BY (CASE WHEN notion_page_id IS NOT NULL AND notion_page_id != '' THEN 0 ELSE 1 END), id ASC;
                    """,
                    new { identity.ExecutablePath, identity.Executable });
            }

            // 3. Fallback: match by Game Name (prevent creating duplicate game rows in SQLite)
            if (existing == null && !string.IsNullOrWhiteSpace(identity.Name))
            {
                existing = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                    """
                    SELECT * FROM games 
                    WHERE LOWER(name) = LOWER(@Name)
                    ORDER BY (CASE WHEN notion_page_id IS NOT NULL AND notion_page_id != '' THEN 0 ELSE 1 END), (CASE WHEN executable_path != '' THEN 0 ELSE 1 END), id ASC;
                    """,
                    new { identity.Name });
            }

            if (existing != null)
            {
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
                """
                UPDATE games SET notion_page_id = @notionPageId, updated_at = CURRENT_TIMESTAMP WHERE id = @gameId;
                INSERT INTO game_mappings (game_id, provider, remote_id, remote_name, last_verified, created_at)
                SELECT id, 'notion', @notionPageId, name, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM games WHERE id = @gameId
                ON CONFLICT(game_id, provider) DO UPDATE SET remote_id = excluded.remote_id, last_verified = CURRENT_TIMESTAMP;
                """,
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
        var list = await conn.QueryAsync<GameRecord>("SELECT * FROM games ORDER BY name;");
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
        var list = await conn.QueryAsync<GameRecord>(
            "SELECT * FROM games WHERE notion_page_id IS NULL OR notion_page_id = '' ORDER BY created_at DESC;");
        return list.ToList();
    }

    public async Task DeleteGameAsync(int gameId)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync("DELETE FROM games WHERE id = @gameId;", new { gameId });
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
                        // 检查 daily_summary 是否存在从 Notion 或外部手动记录的时长（即未包含在 sessions 明细里的时长）
                        var localSessionsTotalSecs = await conn.ExecuteScalarAsync<int>(
                            """
                            SELECT COALESCE(SUM(duration_seconds), 0) FROM sessions 
                            WHERE game_id = @gameId AND substr(start_time, 1, 10) = @date;
                            """,
                            new { gameId, date });

                        var dailyRow = await conn.QuerySingleOrDefaultAsync<DailyDurationCheck>(
                            "SELECT duration_seconds, session_count FROM daily_summary WHERE game_id = @gameId AND date = @date LIMIT 1;",
                            new { gameId, date });

                        int manualBonusSessions = 0;
                        if (dailyRow != null && dailyRow.duration_seconds > localSessionsTotalSecs + 60)
                        {
                            // 存在外部/Notion 手动录入的时长（未通过本地进程捕获），计入该手动游玩次数
                            manualBonusSessions = 1;
                        }

                        int finalSessionCount = validSessionsCount + manualBonusSessions;

                        await conn.ExecuteAsync(
                            """
                            UPDATE daily_summary 
                            SET session_count = @finalSessionCount 
                            WHERE game_id = @gameId AND date = @date;
                            """,
                            new { gameId, date, finalSessionCount });
                    }
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CleanupStaleSessionsAsync(TimeSpan staleThreshold)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            var thresholdTime = DateTime.Now.Subtract(staleThreshold).ToString("yyyy-MM-dd HH:mm:ss");
            await conn.ExecuteAsync(
                """
                UPDATE sessions 
                SET is_active = 0, end_time = last_heartbeat
                WHERE is_active = 1 AND last_heartbeat < @thresholdTime;
                """,
                new { thresholdTime });
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
                    SET duration_seconds = @newSecs, duration_minutes = @newMins,
                        session_count = CASE WHEN duration_minutes = 0 AND @newMins > 0 THEN 1 ELSE session_count END,
                        sync_status = 'pending'
                    WHERE id = @id;
                    """,
                    new { id = existing.id, newSecs, newMins });
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
                    new { date, gameId, durationSeconds, mins, sessionCount });
            }
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
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.date = @date
            ORDER BY d.duration_minutes DESC;
            """,
            new { date });

        return rows.Select(MapDailySummary).ToList();
    }

    public async Task<IReadOnlyList<DailySummary>> GetDailySummariesRangeAsync(string startDate, string endDate)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.date >= @startDate AND d.date <= @endDate AND d.duration_minutes > 0
            ORDER BY d.date ASC;
            """,
            new { startDate, endDate });

        return rows.Select(MapDailySummary).ToList();
    }

    public async Task<int> GetConsecutiveStreakDaysAsync(DateTime referenceDate, int cutoffHour = 24)
    {
        using var conn = CreateConnection();
        var dates = (await conn.QueryAsync<string>(
            """
            SELECT date
            FROM daily_summary
            WHERE duration_minutes > 0
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
            """
            SELECT game_id, MIN(date) AS first_date
            FROM daily_summary
            WHERE duration_minutes > 0
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

    public async Task<GameAggregateStats> GetAggregateStatsAsync(int gameId)
    {
        using var conn = CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT COALESCE(SUM(duration_seconds), 0) AS TotalSeconds,
                   MAX(date) AS LastPlayedDate,
                   COALESCE(SUM(session_count), 0) AS TotalSessions
            FROM daily_summary
            WHERE game_id = @gameId;
            """,
            new { gameId });

        if (row == null)
        {
            return new GameAggregateStats(0, null, 0);
        }

        long totalSeconds = row.TotalSeconds != null ? Convert.ToInt64(row.TotalSeconds) : 0;
        string? lastPlayed = row.LastPlayedDate != null ? Convert.ToString(row.LastPlayedDate) : null;
        int totalSessions = row.TotalSessions != null ? Convert.ToInt32(row.TotalSessions) : 0;
        double totalHours = Math.Round(totalSeconds / 3600.0, 1);

        return new GameAggregateStats(totalHours, lastPlayed, totalSessions);
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
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id, g.notion_page_id AS game_notion_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.sync_status NOT IN ('synced', 'unmapped') AND d.duration_minutes > 0
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

                INSERT INTO sync_records (daily_summary_id, provider, remote_id, status, error_message, last_sync_at, created_at)
                VALUES (@id, 'notion', @notionPageId, @syncStatus, @errorMessage, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                ON CONFLICT(daily_summary_id, provider) DO UPDATE SET
                    remote_id = COALESCE(excluded.remote_id, sync_records.remote_id),
                    status = excluded.status,
                    error_message = excluded.error_message,
                    last_sync_at = CURRENT_TIMESTAMP;
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
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            LEFT JOIN (
                SELECT game_id, substr(end_time, 1, 10) AS play_date, MAX(end_time) AS last_end
                FROM sessions
                WHERE end_time IS NOT NULL
                GROUP BY game_id, substr(end_time, 1, 10)
            ) s ON s.game_id = d.game_id AND s.play_date = d.date
            WHERE d.duration_minutes > 0
            ORDER BY d.date DESC, COALESCE(s.last_end, '') DESC, d.id DESC
            LIMIT @limit;
            """,
            new { limit });

        return rows.Select(MapDailySummary).ToList();
    }

    /// <summary>
    /// 判定一个日期是否属于历史日期（早于 7 天前）。
    /// 历史日期在 Notion 上的单次时长为唯一权威数据，Pull 时绝不判定 localAhead，绝不标记 pending。
    /// </summary>
    private static bool IsHistoricalDate(string dateStr)
    {
        if (DateTime.TryParse(dateStr, out var dt))
        {
            return dt.Date < DateTime.Today.AddDays(-7);
        }
        return false;
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
                    "SELECT * FROM games WHERE notion_page_id = @pageId LIMIT 1;",
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
                            "SELECT * FROM games WHERE platform = @platform AND platform_id = @platformId LIMIT 1;",
                            new { platform, platformId });

                        if (game == null)
                        {
                            // 优先查找同名已有游戏行（不限是否已绑定，避免分身）
                            game = await conn.QueryFirstOrDefaultAsync<GameRecord>(
                                "SELECT * FROM games WHERE LOWER(name) = LOWER(@name) ORDER BY (CASE WHEN executable_path != '' THEN 0 ELSE 1 END), id ASC LIMIT 1;",
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
                    "SELECT * FROM games WHERE LOWER(name) = LOWER(@title) ORDER BY (CASE WHEN executable_path != '' THEN 0 ELSE 1 END), id ASC LIMIT 1;",
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

            // 计算权威时长与同步状态
            // 🚨 关键修复：必须检查 Notion 页面端是否真正建立了关联 (item.GameMasterPageId)。
            // 如果 Notion 端未关联（虽然本地根据游戏名识别出了 game），状态必须设为 "unmapped"，
            // 这样 BackfillRelationsAsync 才能检测到并向 Notion 回写关联属性并标记为"已绑定"。
            bool isBoundOnNotion = !string.IsNullOrEmpty(item.GameMasterPageId);
            int finalMinutes = item.DurationMinutes;
            int finalSeconds = item.DurationMinutes * 60;
            string newStatus = isBoundOnNotion ? "synced" : "unmapped";

            DailySummaryRow? existing = rowByPage ?? rowByDateAndGame;
            if (existing != null)
            {
                int existingMinutes = existing.DurationMinutes;
                int existingSeconds = existing.DurationSeconds;

                bool inflatedByMinuteUnitBug =
                    item.DurationUnitIsMinutes &&
                    item.DurationMinutes > 0 &&
                    !string.IsNullOrEmpty(existing.NotionPageId) &&
                    existingMinutes >= item.DurationMinutes * 60;

                if (inflatedByMinuteUnitBug)
                {
                    finalMinutes = item.DurationMinutes;
                    finalSeconds = item.DurationMinutes * 60;
                    newStatus = isBoundOnNotion ? "synced" : "unmapped";
                    AppLog.Warn(
                        $"[同步] 修正历史时长膨胀：「{item.GameTitle}」{item.Date} " +
                        $"本地 {existingMinutes} 分钟 → {finalMinutes} 分钟（旧版把分钟当成了小时）");
                }
                else if (isHistorical)
                {
                    // 🚨 历史日期：Notion 上的时长为唯一权威！
                    finalMinutes = item.DurationMinutes;
                    finalSeconds = item.DurationMinutes * 60;
                    newStatus = isBoundOnNotion ? "synced" : "unmapped";
                }
                else
                {
                    finalMinutes = Math.Max(existingMinutes, item.DurationMinutes);
                    finalSeconds = Math.Max(existingSeconds, item.DurationMinutes * 60);
                    newStatus = (existingMinutes > item.DurationMinutes) ? "pending" : (isBoundOnNotion ? "synced" : "unmapped");
                }
            }

            // ── 情况 1: 本地已有该 Notion 页面的记录 (rowByPage != null) ───────────
            if (rowByPage != null)
            {
                int oldGameId = rowByPage.GameId;

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
    /// 把某条 Notion 每日记录页面的标题与图标快照回写到本地，用于回刷时比对
    /// （见 <c>NotionSyncService.RefreshDailyTitlesFromMasterAsync</c>）。
    ///
    /// 只改 notion_title / notion_icon_url 两个快照字段 —— 不碰 sync_status，
    /// 否则会把"本地时长领先、正等上传"的 pending 状态洗掉。
    /// 返回受影响行数（0 表示本地没有这条记录）。
    /// </summary>
    /// <remarks>
    /// 两个快照必须**一起**写：只更新标题的话，图标快照会永远停在旧值，
    /// 下一轮就会因为"图标不一致"再 PATCH 一次，退化成每轮都打 Notion。
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
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            WHERE d.date = @date AND d.duration_minutes > 0
            ORDER BY d.duration_minutes DESC
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

        var unbound = await conn.QueryAsync<GameRecord>(
            "SELECT * FROM games WHERE coalesce(notion_page_id, '') = '';");

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

        if (await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM daily_summary WHERE game_id = @id;", new { id = gameId }) > 0) return;
        if (await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sessions WHERE game_id = @id;", new { id = gameId }) > 0) return;

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

    // ----------------- Multi-Backend Provider Mappings & Records -----------------

    public async Task<GameMappingRecord?> GetGameMappingAsync(int gameId, string provider)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GameMappingRecord>(
            """
            SELECT id AS Id, game_id AS GameId, provider AS Provider, remote_id AS RemoteId,
                   remote_locator AS RemoteLocator, remote_name AS RemoteName,
                   match_type AS MatchType, match_confidence AS MatchConfidence,
                   last_verified AS LastVerified, created_at AS CreatedAt
            FROM game_mappings
            WHERE game_id = @gameId AND provider = @provider;
            """,
            new { gameId, provider });
    }

    public async Task<IReadOnlyList<GameMappingRecord>> GetGameMappingsAsync(int gameId)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<GameMappingRecord>(
            """
            SELECT id AS Id, game_id AS GameId, provider AS Provider, remote_id AS RemoteId,
                   remote_locator AS RemoteLocator, remote_name AS RemoteName,
                   match_type AS MatchType, match_confidence AS MatchConfidence,
                   last_verified AS LastVerified, created_at AS CreatedAt
            FROM game_mappings
            WHERE game_id = @gameId;
            """,
            new { gameId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<GameMappingRecord>> GetAllGameMappingsAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<GameMappingRecord>(
            """
            SELECT id AS Id, game_id AS GameId, provider AS Provider, remote_id AS RemoteId,
                   remote_locator AS RemoteLocator, remote_name AS RemoteName,
                   match_type AS MatchType, match_confidence AS MatchConfidence,
                   last_verified AS LastVerified, created_at AS CreatedAt
            FROM game_mappings;
            """);
        return rows.ToList();
    }

    public async Task UpsertGameMappingAsync(
        int gameId,
        string provider,
        string remoteId,
        string? remoteName = null,
        string? remoteLocator = null,
        string? matchType = null,
        double? matchConfidence = null)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO game_mappings (game_id, provider, remote_id, remote_locator, remote_name, match_type, match_confidence, last_verified, created_at)
                VALUES (@gameId, @provider, @remoteId, @remoteLocator, @remoteName, @matchType, @matchConfidence, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                ON CONFLICT(game_id, provider) DO UPDATE SET
                    remote_id = excluded.remote_id,
                    remote_locator = COALESCE(excluded.remote_locator, game_mappings.remote_locator),
                    remote_name = COALESCE(excluded.remote_name, game_mappings.remote_name),
                    match_type = COALESCE(excluded.match_type, game_mappings.match_type),
                    match_confidence = COALESCE(excluded.match_confidence, game_mappings.match_confidence),
                    last_verified = CURRENT_TIMESTAMP;
                """,
                new { gameId, provider, remoteId, remoteLocator, remoteName, matchType, matchConfidence });

            if (string.Equals(provider, "notion", StringComparison.OrdinalIgnoreCase))
            {
                await conn.ExecuteAsync(
                    "UPDATE games SET notion_page_id = @remoteId, updated_at = CURRENT_TIMESTAMP WHERE id = @gameId;",
                    new { gameId, remoteId });
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteGameMappingAsync(int gameId, string provider)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM game_mappings WHERE game_id = @gameId AND provider = @provider;",
                new { gameId, provider });

            if (string.Equals(provider, "notion", StringComparison.OrdinalIgnoreCase))
            {
                await conn.ExecuteAsync(
                    "UPDATE games SET notion_page_id = NULL, updated_at = CURRENT_TIMESTAMP WHERE id = @gameId;",
                    new { gameId });
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<SyncRecordItem?> GetSyncRecordAsync(int dailySummaryId, string provider)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<SyncRecordItem>(
            """
            SELECT id AS Id, daily_summary_id AS DailySummaryId, provider AS Provider,
                   remote_id AS RemoteId, status AS Status, retry_count AS RetryCount,
                   last_sync_at AS LastSyncAt, error_message AS ErrorMessage, created_at AS CreatedAt
            FROM sync_records
            WHERE daily_summary_id = @dailySummaryId AND provider = @provider;
            """,
            new { dailySummaryId, provider });
    }

    public async Task<IReadOnlyList<SyncRecordItem>> GetSyncRecordsForDailyAsync(int dailySummaryId)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<SyncRecordItem>(
            """
            SELECT id AS Id, daily_summary_id AS DailySummaryId, provider AS Provider,
                   remote_id AS RemoteId, status AS Status, retry_count AS RetryCount,
                   last_sync_at AS LastSyncAt, error_message AS ErrorMessage, created_at AS CreatedAt
            FROM sync_records
            WHERE daily_summary_id = @dailySummaryId;
            """,
            new { dailySummaryId });
        return rows.ToList();
    }

    public async Task UpsertSyncRecordAsync(int dailySummaryId, string provider, string status, string? remoteId = null, string? errorMessage = null)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO sync_records (daily_summary_id, provider, remote_id, status, error_message, last_sync_at, created_at)
                VALUES (@dailySummaryId, @provider, @remoteId, @status, @errorMessage, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                ON CONFLICT(daily_summary_id, provider) DO UPDATE SET
                    remote_id = COALESCE(excluded.remote_id, sync_records.remote_id),
                    status = excluded.status,
                    error_message = excluded.error_message,
                    last_sync_at = CURRENT_TIMESTAMP,
                    retry_count = CASE WHEN excluded.status = 'error' THEN sync_records.retry_count + 1 ELSE sync_records.retry_count END;
                """,
                new { dailySummaryId, provider, remoteId, status, errorMessage });

            if (string.Equals(provider, "notion", StringComparison.OrdinalIgnoreCase))
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE daily_summary
                    SET sync_status = @status,
                        notion_page_id = COALESCE(@remoteId, notion_page_id),
                        last_sync_at = CURRENT_TIMESTAMP,
                        error_message = @errorMessage
                    WHERE id = @dailySummaryId;
                    """,
                    new { dailySummaryId, status, remoteId, errorMessage });
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DailySummary>> GetPendingSummariesForProviderAsync(string provider)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT d.*, g.name AS game_name, g.platform, g.platform_id, g.notion_page_id AS game_notion_id
            FROM daily_summary d
            JOIN games g ON d.game_id = g.id
            LEFT JOIN sync_records sr ON d.id = sr.daily_summary_id AND sr.provider = @provider
            WHERE d.duration_minutes > 0
              AND (sr.id IS NULL OR sr.status NOT IN ('synced', 'unmapped'))
            ORDER BY d.date ASC;
            """,
            new { provider });

        return rows.Select(MapDailySummary).ToList();
    }

    public async Task<ProviderConfigItem?> GetProviderConfigAsync(string provider)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<ProviderConfigItem>(
            """
            SELECT provider AS Provider, enabled AS Enabled,
                   config_json AS ConfigJson, updated_at AS UpdatedAt
            FROM provider_configs
            WHERE provider = @provider;
            """,
            new { provider });
    }

    public async Task<IReadOnlyList<ProviderConfigItem>> GetAllProviderConfigsAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<ProviderConfigItem>(
            """
            SELECT provider AS Provider, enabled AS Enabled,
                   config_json AS ConfigJson, updated_at AS UpdatedAt
            FROM provider_configs;
            """);
        return rows.ToList();
    }

    public async Task SetProviderConfigAsync(string provider, bool enabled, string configJson)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO provider_configs (provider, enabled, config_json, updated_at)
                VALUES (@provider, @enabled, @configJson, CURRENT_TIMESTAMP)
                ON CONFLICT(provider) DO UPDATE SET
                    enabled = excluded.enabled,
                    config_json = excluded.config_json,
                    updated_at = CURRENT_TIMESTAMP;
                """,
                new { provider, enabled = enabled ? 1 : 0, configJson });
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

