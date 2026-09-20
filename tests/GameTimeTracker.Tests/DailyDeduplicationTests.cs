using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.Notion;
using GameTimeTracker.Infrastructure.Platforms;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

public class DailyDeduplicationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;
    private readonly FakeNotionClient _client;
    private readonly TrackerConfig _config;
    private readonly NotionSyncService _sync;

    public DailyDeduplicationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gametime_dedup_test_{Guid.NewGuid():N}.db");
        _repo = new SqliteRepository(_dbPath);
        _client = new FakeNotionClient();
        _config = new TrackerConfig
        {
            NotionToken = "fake-token",
            DailyDatabaseId = "fake-daily-db",
            GameDatabaseId = "fake-game-db"
        };
        _sync = new NotionSyncService(_repo, _client, _config);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public void RegisterKnownGame_PreservesRealPlatformAndPlatformId()
    {
        var lib = new GameLibraryManager();
        lib.RegisterKnownGame("steam", "1781260", "Promenade", @"E:\SteamLibrary\steamapps\common\Promenade\Promenade.exe");

        var detected = lib.DetectGame(new DetectedProcess(
            Pid: 1234,
            ProcessName: "Promenade",
            ExecutablePath: @"E:\SteamLibrary\steamapps\common\Promenade\Promenade.exe",
            WindowTitle: "Promenade"
        ));

        detected.Should().NotBeNull();
        detected!.Platform.Should().Be("steam");
        detected.PlatformId.Should().Be("1781260");
        detected.Name.Should().Be("Promenade");
    }

    [Fact]
    public async Task StartupDeduplication_MergesDuplicateGamesAndCombinesDailySummaries()
    {
        // 1. 手动构造类似用户数据库中的分裂场景：两个 Promenade 游戏行
        int game1Id, game2Id;
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();

            // game 1: 历史 Notion 拉取生成的无 exe 游戏行 (id=152)
            cmd.CommandText = """
                INSERT INTO games (platform, platform_id, name, executable, executable_path, notion_page_id, status)
                VALUES ('steam', '27c5c2e6', 'Promenade', '', '', '3dec4ab2-6378-8125-8e31-d743c1220867', 'active');
                SELECT last_insert_rowid();
            """;
            game1Id = Convert.ToInt32(cmd.ExecuteScalar());

            // game 2: 本地进程扫描发现的带真实 exe 游戏行 (id=154)
            cmd.CommandText = """
                INSERT INTO games (platform, platform_id, name, executable, executable_path, notion_page_id, status)
                VALUES ('steam', '1781260', 'Promenade', 'Promenade.exe', 'E:\SteamLibrary\steamapps\common\Promenade\Promenade.exe', '3dec4ab2-6378-8125-8e31-d743c1220867', 'active');
                SELECT last_insert_rowid();
            """;
            game2Id = Convert.ToInt32(cmd.ExecuteScalar());

            // 给 game 1 添加会话与当日记录
            cmd.CommandText = $"""
                INSERT INTO sessions (game_id, pid, process_name, start_time, last_heartbeat, duration_seconds, is_active)
                VALUES ({game1Id}, 1001, 'Promenade', '2026-09-20 00:10:00', '2026-09-20 00:16:00', 360, 0);

                INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status, notion_page_id, notion_title)
                VALUES ('2026-09-20', {game1Id}, 360, 6, 1, 'synced', 'page-prom-1', 'Promenade · 0.1 h');
            """;
            cmd.ExecuteNonQuery();

            // 给 game 2 添加会话与当日记录（如用户后续游玩产生的 105 秒）
            cmd.CommandText = $"""
                INSERT INTO sessions (game_id, pid, process_name, start_time, last_heartbeat, duration_seconds, is_active)
                VALUES ({game2Id}, 22336, 'Promenade', '2026-09-20 01:40:00', '2026-09-20 01:42:00', 105, 0);

                INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status, notion_page_id)
                VALUES ('2026-09-20', {game2Id}, 105, 1, 1, 'pending', null);
            """;
            cmd.ExecuteNonQuery();
        }

        // 2. 模拟重启：重新实例化 SqliteRepository 触发 InitializeDatabase 中的自动去重
        var reloadedRepo = new SqliteRepository(_dbPath);

        // 3. 验证：
        // 游戏行应只剩 1 个，且为具有真实可执行文件路径的权威项
        var games = await reloadedRepo.GetAllGamesAsync();
        var promenadeGames = games.Where(g => g.Name == "Promenade").ToList();
        promenadeGames.Should().HaveCount(1);

        var canonicalGame = promenadeGames.Single();
        canonicalGame.Id.Should().Be(game2Id, "优先保留有真实可执行文件路径的项");
        canonicalGame.PlatformId.Should().Be("1781260");
        canonicalGame.ExecutablePath.Should().Be(@"E:\SteamLibrary\steamapps\common\Promenade\Promenade.exe");
        canonicalGame.NotionPageId.Should().Be("3dec4ab2-6378-8125-8e31-d743c1220867");

        // 当日 daily_summary 应自动合并为 1 行，时长相加：360 + 105 = 465 秒 (7 分钟)
        var summaries = await reloadedRepo.GetDailySummariesByDateAsync("2026-09-20");
        var promSummaries = summaries.Where(s => s.GameId == canonicalGame.Id).ToList();
        promSummaries.Should().HaveCount(1);

        var mergedSummary = promSummaries.Single();
        mergedSummary.DurationSeconds.Should().Be(465);
        mergedSummary.DurationMinutes.Should().Be(7);
        mergedSummary.SessionCount.Should().Be(2);
        mergedSummary.NotionPageId.Should().Be("page-prom-1", "应继承已有页面的 notion_page_id");

        // 会话全部迁移到 canonicalGame
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM sessions WHERE game_id = {canonicalGame.Id}";
            var sessionCount = Convert.ToInt32(cmd.ExecuteScalar());
            sessionCount.Should().Be(2);
        }
    }

    [Fact]
    public async Task Pull_WhenMultipleNotionPagesExistForSameGameAndDate_MergesDurationAndArchivesDuplicates()
    {
        // 1. 设置 Notion 总表
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-promenade",
            Name = "Promenade",
            IconType = "external",
            IconUrl = "https://example.com/prom.ico"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        // 2. 远端 Notion 每日时长表中存在 3 条同日同游戏记录（即用户实际遇到的情况：0.1h, 0.3h, 0.17h）
        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "page-prom-1",
            Date = "2026-09-20",
            GameTitle = "Promenade",
            RawTitle = "Promenade · 0.1 h",
            DurationMinutes = 6,
            GameMasterPageId = "master-promenade"
        });
        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "page-prom-2",
            Date = "2026-09-20",
            GameTitle = "Promenade",
            RawTitle = "Promenade · 0.3 h",
            DurationMinutes = 18,
            GameMasterPageId = "master-promenade"
        });
        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "page-prom-3",
            Date = "2026-09-20",
            GameTitle = "Promenade",
            RawTitle = "Promenade · 0.17 h",
            DurationMinutes = 10,
            GameMasterPageId = "master-promenade"
        });

        // 3. 执行从 Notion 拉取同步
        int pulled = await _sync.PullDailyRecordsFromNotionAsync();
        pulled.Should().BeGreaterThan(0);

        // 4. 验证远端行为：
        // 应该归档了 2 个重复页面，保留时长最大的权威页面 page-prom-2 (18 min)
        _client.ArchivedPageIds.Should().HaveCount(2);
        _client.ArchivedPageIds.Should().Contain("page-prom-1");
        _client.ArchivedPageIds.Should().Contain("page-prom-3");

        // 权威页面应被更新为合计总时长：6 + 18 + 10 = 34 分钟 (~0.57h)
        _client.UpdatedPages.Should().Contain(u =>
            u.PageId == "page-prom-2" && u.DurationMinutes == 34);

        // 5. 验证本地数据库：只有 1 条每日汇总，时长为 34 分钟
        var summaries = await _repo.GetDailySummariesByDateAsync("2026-09-20");
        summaries.Should().HaveCount(1);
        summaries[0].DurationMinutes.Should().Be(34);
        summaries[0].NotionPageId.Should().Be("page-prom-2");
    }

    [Fact]
    public async Task SyncPending_WhenRemoteAlreadyHasPageForGameAndDate_DoesNotCreateDuplicate()
    {
        // 1. 设置 Notion 总表
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-promenade",
            Name = "Promenade"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        // 2. 远端已有一条记录
        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "page-prom-remote",
            Date = "2026-09-20",
            GameTitle = "Promenade",
            DurationMinutes = 20,
            GameMasterPageId = "master-promenade"
        });

        // 3. 本地有一条尚未绑定 NotionPageId 的待推送记录（例如本地先玩了 25 分钟）
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "1781260", "Promenade", "Promenade.exe", @"E:\Promenade.exe"));
        await _repo.AddSessionDurationToDailyAsync("2026-09-20", game.Id, 25 * 60);

        // 此时本地 notion_page_id 为空，sync_status 为 pending
        var local = (await _repo.GetDailySummariesByDateAsync("2026-09-20")).Single();
        local.NotionPageId.Should().BeNull();
        local.SyncStatus.Should().Be("pending");

        // 4. 执行上传同步
        int synced = await _sync.SyncPendingDailyRecordsAsync();
        synced.Should().BeGreaterThan(0);

        // 5. 验证：绝对不能调用 CreateDailyRecordAsync 新建页面！
        _client.CreatedDailyRecordTitles.Should().BeEmpty("远端已存在该游戏当天的页面，应更新而非新建");

        // 应该更新远端已有页面
        _client.UpdatedPages.Should().Contain(u => u.PageId == "page-prom-remote");

        // 本地记录应认领绑定该页面
        var localAfter = (await _repo.GetDailySummariesByDateAsync("2026-09-20")).Single();
        localAfter.NotionPageId.Should().Be("page-prom-remote");
        localAfter.SyncStatus.Should().Be("synced");
    }
}



