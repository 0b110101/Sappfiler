using System.Data;
using System.Text.Json;
using Dapper;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

public class SqliteRepository : IDatabaseRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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

            if (existing != null)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE games 
                    SET name = CASE WHEN name IS NULL OR name = '' THEN @Name ELSE name END,
                        executable = CASE WHEN @Executable != '' THEN @Executable ELSE executable END,
                        executable_path = CASE WHEN @ExecutablePath != '' THEN @ExecutablePath ELSE executable_path END, 
                        updated_at = CURRENT_TIMESTAMP
                    WHERE id = @Id;
                    """,
                    new { identity.Name, identity.Executable, identity.ExecutablePath, existing.Id });
                if (string.IsNullOrEmpty(existing.Name)) existing.Name = identity.Name;
                if (!string.IsNullOrEmpty(identity.Executable)) existing.Executable = identity.Executable;
                if (!string.IsNullOrEmpty(identity.ExecutablePath)) existing.ExecutablePath = identity.ExecutablePath;
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
                    VALUES (@date, @gameId, @durationSeconds, @mins, 1, 'pending');
                    """,
                    new { date, gameId, durationSeconds, mins });
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
            WHERE d.date >= @startDate AND d.date <= @endDate
            ORDER BY d.date ASC;
            """,
            new { startDate, endDate });

        return rows.Select(MapDailySummary).ToList();
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
                            LastSyncedAt = DateTime.Parse((string)catRow.last_synced_at)
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
                            // 再用**总表条目名**认一条尚未绑定的本地行。
                            // 这是「Notion 里手动补了 relation，程序里仍显示未绑定」的解法：
                            // 记录标题是用户手写的（常带「2.2h」「通关」等噪音），
                            // 但 relation 指向的总表条目名是权威的官方名。
                            game = await FindUnboundGameByNameAsync(conn, catItem.Name);
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
                    "SELECT * FROM games WHERE LOWER(name) = LOWER(@title) LIMIT 1;",
                    new { title = item.GameTitle })
                    // 精确同名找不到时按归一化再认一次
                    // （消掉标点、大小写、罗马数字、年份/版本后缀、时长后缀的差异）
                    ?? await FindUnboundGameByNameAsync(conn, item.GameTitle);
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
            var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, duration_seconds, duration_minutes, notion_page_id, sync_status 
                FROM daily_summary 
                WHERE notion_page_id = @pageId OR (date = @date AND game_id = @gameId)
                LIMIT 1;
                """,
                new { pageId = item.PageId, date = item.Date, gameId = game.Id });

            if (existing != null)
            {
                int existingMinutes = (int)existing.duration_minutes;
                int existingSeconds = (int)existing.duration_seconds;
                int finalMinutes = Math.Max(existingMinutes, item.DurationMinutes);
                int finalSeconds = Math.Max(existingSeconds, item.DurationMinutes * 60);

                // 本地时长领先（Notion 还停在旧值）时必须保留 pending，
                // 否则每轮 Pull 都会把心跳刚标记的 pending 洗成 synced，
                // 推送永远查不到待上传记录 —— Notion 时长就停在首次创建时的值。
                //
                // ⚠️ 必须**按分钟比**，不能用秒：
                //    duration_minutes 是 duration_seconds / 60 的整数除法结果
                //    （见 AddSessionDurationToDailyAsync），所以秒总比分钟多出 <60 的余数。
                //    用 existingSeconds > item.DurationMinutes * 60 的话，
                //    910 秒(15分) vs 远端 15 分会判成"本地领先" →
                //    但推上去的还是同样的 15 分钟 → 数值不变 → 下一轮再次判领先 → **永远重推**。
                //    按分钟比则 15 == 15，正确判为已同步。
                //
                // 而 push 出去的就是 duration_minutes，所以"本地是否领先"本就该用分钟衡量。
                bool localAhead = existingMinutes > item.DurationMinutes;

                await conn.ExecuteAsync(
                    """
                    UPDATE daily_summary
                    SET duration_seconds = @finalSeconds,
                        duration_minutes = @finalMinutes,
                        notion_page_id = @pageId,
                        notion_title = COALESCE(@title, notion_title),
                        sync_status = @newStatus,
                        last_sync_at = CURRENT_TIMESTAMP
                    WHERE id = @id;
                    """,
                    new
                    {
                        finalSeconds,
                        finalMinutes,
                        pageId = item.PageId,
                        // Pull 拿到的标题就是远端现状 —— 记下来，回刷时用它比对即可跳过无变化的记录。
                        // 但要防呆：有些路径只给 pageId 不给标题（GameTitle 是被剥过时长后缀的裸名），
                        // 那种情况必须传 null 保住旧快照，否则会把「不思议迷宫 · 0.7 h」写成裸名，
                        // 下一轮回刷就会误判为"不一致"而反复 PATCH。
                        title = string.IsNullOrWhiteSpace(item.RawTitle) ? null : item.RawTitle,
                        newStatus = localAhead ? "pending" : "synced",
                        id = (int)existing.id
                    });

                return (int)existing.id;
            }
            else
            {
                var insertedId = await conn.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status, notion_page_id, last_sync_at)
                    VALUES (@date, @gameId, @seconds, @minutes, 1, 'synced', @pageId, CURRENT_TIMESTAMP);
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        date = item.Date,
                        gameId = game.Id,
                        seconds = item.DurationMinutes * 60,
                        minutes = item.DurationMinutes,
                        pageId = item.PageId
                    });

                return insertedId;
            }
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
            LastSyncAt = r.last_sync_at != null ? DateTime.Parse((string)r.last_sync_at) : null,
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

        var target = GameTimeTracker.Core.Services.GameMatcher.NormalizeTitle(name);
        if (string.IsNullOrEmpty(target)) return null;

        var unbound = await conn.QueryAsync<GameRecord>(
            "SELECT * FROM games WHERE coalesce(notion_page_id, '') = '';");

        foreach (var g in unbound)
        {
            if (string.IsNullOrWhiteSpace(g.Name)) continue;

            // 名字与可执行文件都试：有些本地行的 name 是进程名，exe 才是真名（或反之）
            if (GameTimeTracker.Core.Services.GameMatcher.NormalizeTitle(g.Name) == target) return g;
            if (!string.IsNullOrWhiteSpace(g.Executable) &&
                GameTimeTracker.Core.Services.GameMatcher.NormalizeTitle(g.Executable) == target) return g;
        }

        return null;
    }

    // ----------------- Notion Game Master Cache -----------------

    public async Task<IReadOnlyList<NotionGameCatalogItem>> GetCatalogItemsAsync()
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<dynamic>("SELECT * FROM game_catalog;");
        var list = new List<NotionGameCatalogItem>();
        foreach (var r in rows)
        {
            var aliases = JsonSerializer.Deserialize<List<string>>((string)r.aliases_json) ?? new();
            var identifiers = JsonSerializer.Deserialize<List<string>>((string)r.identifiers_json) ?? new();
            list.Add(new NotionGameCatalogItem
            {
                PageId = (string)r.page_id,
                Name = (string)r.name,
                Aliases = aliases,
                Identifiers = identifiers,
                CoverUrl = (string?)r.cover_url,
                IconUrl = (string?)r.icon_url,
                IconType = (string?)r.icon_type,
                LastSyncedAt = DateTime.Parse((string)r.last_synced_at)
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
        var aliases = JsonSerializer.Deserialize<List<string>>((string)r.aliases_json) ?? new();
        var identifiers = JsonSerializer.Deserialize<List<string>>((string)r.identifiers_json) ?? new();
        return new NotionGameCatalogItem
        {
            PageId = (string)r.page_id,
            Name = (string)r.name,
            Aliases = aliases,
            Identifiers = identifiers,
            CoverUrl = (string?)r.cover_url,
            IconUrl = (string?)r.icon_url,
            IconType = (string?)r.icon_type,
            LastSyncedAt = DateTime.Parse((string)r.last_synced_at)
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
                await conn.ExecuteAsync(
                    """
                    INSERT INTO game_catalog (page_id, name, aliases_json, identifiers_json, cover_url, icon_url, icon_type, last_synced_at)
                    VALUES (@PageId, @Name, @aliasesJson, @identsJson, @CoverUrl, @IconUrl, @IconType, CURRENT_TIMESTAMP)
                    ON CONFLICT(page_id) DO UPDATE SET
                        name = excluded.name,
                        aliases_json = excluded.aliases_json,
                        identifiers_json = excluded.identifiers_json,
                        cover_url = excluded.cover_url,
                        icon_url = excluded.icon_url,
                        icon_type = excluded.icon_type,
                        last_synced_at = CURRENT_TIMESTAMP;
                    """,
                    new { item.PageId, item.Name, aliasesJson, identsJson, item.CoverUrl, item.IconUrl, item.IconType },
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
}
