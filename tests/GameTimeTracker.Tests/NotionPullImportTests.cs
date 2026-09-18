using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.Notion;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 「Notion → 本地」的导入路径。
/// 这条路径曾经**完全不通**：`SyncDailyRecordFromNotionAsync` 建游戏行时漏了
/// `executable_path`（该列 NOT NULL），整条 INSERT 报 SQLite Error 19，
/// 而调用方是空 catch{} —— 表现成"同步成功但数据没进来"。
/// </summary>
public class NotionPullImportTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;
    private readonly FakeNotionClient _client = new();
    private readonly NotionSyncService _sync;
    private readonly TrackerConfig _config = new()
    {
        NotionToken = "ntn_test",
        GameDatabaseId = "game-db",
        DailyDatabaseId = "daily-db"
    };

    public NotionPullImportTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gtpull_{Guid.NewGuid():N}.db");
        _repo = new SqliteRepository(_dbPath);
        _sync = new NotionSyncService(_repo, _client, _config);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Pull_CreatesGameFromCatalog_WhenGameIsNotLocalYet()
    {
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-new",
            Name = "总表里已有的游戏",
            Identifiers = new() { "steam:1234567" }
        });
        await _sync.RefreshGameCatalogCacheAsync();

        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "daily-new",
            Date = "2026-09-20",
            GameTitle = "总表里已有的游戏",
            DurationMinutes = 12,
            GameMasterPageId = "master-new"
        });

        var synced = await _sync.PullDailyRecordsFromNotionAsync();

        synced.Should().Be(1, "单条失败不能再被静默吞掉");
        var game = (await _repo.GetAllGamesAsync()).Single(g => g.Name == "总表里已有的游戏");
        game.NotionPageId.Should().Be("master-new");
        game.Platform.Should().Be("steam");
        game.PlatformId.Should().Be("1234567", "有「游戏标识」时要直接用它，才能和运行中检测到的同一款游戏对上");
    }

    [Fact]
    public async Task Pull_UsesCoverUrlAppId_WhenIdentifiersAreEmpty()
    {
        // 总表多数条目没填「游戏标识」，只有封面来自 Steam CDN
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-cover",
            Name = "封面能推出 AppID 的游戏",
            CoverUrl = "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1347970/library_hero.jpg"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "daily-cover",
            Date = "2026-09-20",
            GameTitle = "封面能推出 AppID 的游戏",
            DurationMinutes = 5,
            GameMasterPageId = "master-cover"
        });

        await _sync.PullDailyRecordsFromNotionAsync();

        var game = (await _repo.GetAllGamesAsync()).Single(g => g.Name == "封面能推出 AppID 的游戏");
        game.PlatformId.Should().Be("1347970");
    }

    [Fact]
    public async Task Pull_CreatesManualGame_WhenRecordHasNoMasterPage()
    {
        _client.GameMasterItems.Add(new NotionGameCatalogItem { PageId = "master-other", Name = "别的游戏" });
        await _sync.RefreshGameCatalogCacheAsync();

        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "daily-unbound",
            Date = "2026-09-21",
            GameTitle = "只有标题的记录",
            DurationMinutes = 3
        });

        var synced = await _sync.PullDailyRecordsFromNotionAsync();

        synced.Should().Be(1);
        var game = (await _repo.GetAllGamesAsync()).Single(g => g.Name == "只有标题的记录");
        // 本地并没有这款游戏，所以不能凭空造一个 exe 路径出来
        game.ExecutablePath.Should().BeEmpty();
        game.Executable.Should().BeEmpty();
    }

    [Fact]
    public async Task Pull_ImportsEveryRecord_WithoutSilentFailures()
    {
        _client.GameMasterItems.Add(new NotionGameCatalogItem { PageId = "master-a", Name = "游戏A" });
        _client.GameMasterItems.Add(new NotionGameCatalogItem { PageId = "master-b", Name = "游戏B" });
        await _sync.RefreshGameCatalogCacheAsync();

        _client.DailyRecords.Add(new NotionDailyRecordItem
        { PageId = "p1", Date = "2026-09-20", GameTitle = "游戏A", DurationMinutes = 1, GameMasterPageId = "master-a" });
        _client.DailyRecords.Add(new NotionDailyRecordItem
        { PageId = "p2", Date = "2026-09-21", GameTitle = "游戏A", DurationMinutes = 2, GameMasterPageId = "master-a" });
        _client.DailyRecords.Add(new NotionDailyRecordItem
        { PageId = "p3", Date = "2026-09-20", GameTitle = "游戏B", DurationMinutes = 3, GameMasterPageId = "master-b" });
        _client.DailyRecords.Add(new NotionDailyRecordItem
        { PageId = "p4", Date = "2026-09-21", GameTitle = "游戏B", DurationMinutes = 4, GameMasterPageId = "master-b" });

        var synced = await _sync.PullDailyRecordsFromNotionAsync();

        synced.Should().Be(4);
        (await _repo.GetAllGamesAsync()).Should().HaveCount(2, "同一条目的多条记录不该重复建游戏");
        (await _repo.GetDailySummariesByDateAsync("2026-09-20")).Should().HaveCount(2);
        (await _repo.GetDailySummariesByDateAsync("2026-09-21")).Should().HaveCount(2);
    }

    [Fact]
    public async Task SyncPending_SkipsUpload_WhenPullFails()
    {
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "999", "待上传的游戏", "g.exe", @"C:\g.exe"));
        await _repo.AddSessionDurationToDailyAsync("2026-09-22", game.Id, 600);

        _client.FailQueryDailyRecords = true;

        var pushed = await _sync.SyncPendingDailyRecordsAsync();

        pushed.Should().Be(0, "拿不到 Notion 现状时不该上传：那条记录可能早就在 Notion 里了");
        _client.CreatedDailyRecordTitles.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncPending_UploadsPendingRecord_WhenPullSucceeds()
    {
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "1000", "要上传的游戏", "h.exe", @"C:\h.exe"));
        await _repo.AddSessionDurationToDailyAsync("2026-09-23", game.Id, 600);

        var pushed = await _sync.SyncPendingDailyRecordsAsync();

        pushed.Should().Be(1);
        _client.CreatedDailyRecordTitles.Should().ContainSingle();
    }

    [Fact]
    public async Task Pull_KeepsPending_WhenLocalDurationIsAhead()
    {
        // QA 实测场景（app(2).log）：记录创建后 Notion 停在 12 min，本地一路涨到 41 min。
        // 每轮 Pull 都把本地 pending 洗成 synced → 推送永远查不到待上传记录 → Notion 永远 12 min。
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "1001", "Master Key", "m.exe", @"C:\m.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-mk");
        await _repo.AddSessionDurationToDailyAsync("2026-09-18", game.Id, 41 * 60);
        // 模拟首次创建成功：已有 notion_page_id 且 synced
        var daily = (await _repo.GetDailySummariesByDateAsync("2026-09-18")).Single();
        await _repo.UpdateDailySyncStatusAsync(daily.Id, "synced", "daily-mk");

        // Notion 上还是旧的 12 min
        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "daily-mk",
            Date = "2026-09-18",
            GameTitle = "Master Key",
            DurationMinutes = 12,
            GameMasterPageId = "master-mk"
        });

        await _sync.PullDailyRecordsFromNotionAsync();

        var after = (await _repo.GetDailySummariesByDateAsync("2026-09-18")).Single();
        after.DurationMinutes.Should().Be(41, "本地时长领先时不应被远端旧值覆盖");
        after.SyncStatus.Should().Be("pending", "本地领先时必须保留待上传状态，否则推送永远查不到这条记录");

        // 推送链路应能把它推出去（Update 已有页面）
        var pushed = await _sync.SyncPendingDailyRecordsAsync();
        pushed.Should().Be(1, "已有 notion_page_id 的记录走 Update 路径");
        _client.UpdatedPages.Should().Contain(("daily-mk", 41));
    }
}
