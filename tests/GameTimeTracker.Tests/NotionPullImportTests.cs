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

    // ================= 需求一：每日记录的「游戏名称」用总表名 + 总表 page icon =================

    [Fact]
    public async Task SyncPending_UsesMasterTableName_WhenGameIsBound()
    {
        // 本地进程名是英文，用户在总表里改成了中文名 —— 推上去的标题必须用中文名。
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-cn",
            Name = "不思议迷宫",
            IconUrl = "https://example.com/icon.png",
            IconType = "external"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "2001", "Gumballs", "g.exe", @"C:\g.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-cn");
        await _repo.AddSessionDurationToDailyAsync("2026-09-24", game.Id, 42 * 60);

        var pushed = await _sync.SyncPendingDailyRecordsAsync();

        pushed.Should().Be(1);
        _client.CreatedDailyRecordTitles.Should().ContainSingle()
            .Which.Should().Be("不思议迷宫", "已绑定时要用总表里的名字，而不是本地进程名");
    }

    [Fact]
    public async Task SyncPending_KeepsProcessName_WhenGameIsNotBound()
    {
        // 未绑定 → relation 无从读起，只能保持进程名。
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "2002", "UnboundGame", "u.exe", @"C:\u.exe"));
        await _repo.AddSessionDurationToDailyAsync("2026-09-25", game.Id, 600);

        await _sync.SyncPendingDailyRecordsAsync();

        _client.CreatedDailyRecordTitles.Should().ContainSingle()
            .Which.Should().Be("UnboundGame", "未绑定总表时没有 relation 可读，只能退回进程名");
    }

    // ================= 需求二：总表改名 / 补 icon 后回刷已同步记录 =================

    [Fact]
    public async Task RefreshTitles_UpdatesSyncedRecord_WhenMasterNameChanged()
    {
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-rn", Name = "旧名字", IconType = "external", IconUrl = "https://example.com/a.png"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "3001", "ProcName", "p.exe", @"C:\p.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-rn");
        await _repo.AddSessionDurationToDailyAsync("2026-09-26", game.Id, 42 * 60);

        var daily = (await _repo.GetDailySummariesByDateAsync("2026-09-26")).Single();
        await _repo.UpdateDailySyncStatusAsync(daily.Id, "synced", "daily-rn");
        // 远端快照还是旧名字 + 旧图标
        await _repo.UpdateDailyRecordFromNotionAsync("daily-rn", "旧名字 · 0.7 h", "https://example.com/a.png");

        // 用户在总表里改名 + 换图标，下一轮目录刷新会带下新值
        _client.GameMasterItems.Clear();
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-rn", Name = "新名字", IconType = "external", IconUrl = "https://example.com/b.png"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        var refreshed = await _sync.RefreshDailyTitlesFromMasterAsync();

        refreshed.Should().Be(1, "总表改名后已同步的记录要被回刷");
        _client.UpdatedTitles.Should().Contain("新名字", "回刷要把总表的新名字写回每日记录标题");
    }

    [Fact]
    public async Task RefreshTitles_IsNoOp_WhenNothingChanged()
    {
        // 这条用例是本需求的性能护栏：只要"总表有条目设了图标"就每轮 PATCH 全部历史记录，
        // 记录数会随天数线性增长 —— 必须证明真的无变化时一次请求都不发。
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-same", Name = "不变的游戏", IconType = "external", IconUrl = "https://example.com/c.png"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "3002", "Same", "s.exe", @"C:\s.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-same");
        await _repo.AddSessionDurationToDailyAsync("2026-09-27", game.Id, 42 * 60);

        var daily = (await _repo.GetDailySummariesByDateAsync("2026-09-27")).Single();
        await _repo.UpdateDailySyncStatusAsync(daily.Id, "synced", "daily-same");
        // 标题与图标快照都已是目标状态（42 min → 0.7 h）。
        // 图标快照必须一起给：只给标题的话 iconChanged 恒为 true，这条用例就会失败 ——
        // 那正是这个缺陷曾经存在过的证据。
        await _repo.UpdateDailyRecordFromNotionAsync(
            "daily-same", "不变的游戏 · 0.7 h", "https://example.com/c.png");

        var before = _client.UpdatedPages.Count;
        var refreshed = await _sync.RefreshDailyTitlesFromMasterAsync();

        refreshed.Should().Be(0, "标题和图标都没变就不该再打 Notion");
        _client.UpdatedPages.Count.Should().Be(before, "无事可做时一次 PATCH 都不该发");
    }

    [Fact]
    public async Task RefreshTitles_Backfills_WhenSnapshotMissing()
    {
        // 首轮：记录早就 synced 了，但本地没有 notion_title 快照（老版本升级上来的库）。
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-old", Name = "老库游戏", IconType = "external", IconUrl = "https://example.com/d.png"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "3003", "OldDb", "o.exe", @"C:\o.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-old");
        await _repo.AddSessionDurationToDailyAsync("2026-09-28", game.Id, 42 * 60);

        var daily = (await _repo.GetDailySummariesByDateAsync("2026-09-28")).Single();
        await _repo.UpdateDailySyncStatusAsync(daily.Id, "synced", "daily-old");
        // 刻意不写 notion_title，模拟升级场景

        var refreshed = await _sync.RefreshDailyTitlesFromMasterAsync();

        refreshed.Should().Be(1, "没有快照的老记录应被判为需要回刷一次");
        var after = (await _repo.GetDailySummariesByDateAsync("2026-09-28")).Single();
        after.NotionTitle.Should().Be("老库游戏 · 0.7 h", "回刷后要落标题快照，后续轮次才会变成空操作");
        after.NotionIconUrl.Should().Be("https://example.com/d.png", "图标快照必须一起落库，否则下轮又因图标不一致再 PATCH");

        // 第二轮应当无事可做
        _client.UpdatedPages.Clear();
        (await _sync.RefreshDailyTitlesFromMasterAsync()).Should().Be(0, "快照补齐后不应反复 PATCH");
    }

    [Fact]
    public async Task SyncPending_RecordsSnapshot_SoBackRefreshDoesNotRepatch()
    {
        // 推送成功后必须立刻把"远端现状"记进快照。不记的话本地就是明知故犯地错：
        // 刚把图标写上去，快照还写着"没有图标"，下一轮回刷会为这条记录多做一次多余 PATCH。
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-snap", Name = "快照游戏", IconType = "external", IconUrl = "https://example.com/e.png"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "3004", "Snap", "n.exe", @"C:\n.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-snap");
        await _repo.AddSessionDurationToDailyAsync("2026-09-29", game.Id, 42 * 60);

        await _sync.SyncPendingDailyRecordsAsync();

        var pushed = (await _repo.GetDailySummariesByDateAsync("2026-09-29")).Single();
        pushed.NotionTitle.Should().Be("快照游戏 · 0.7 h", "推送后应立刻落下标题快照");
        pushed.NotionIconUrl.Should().Be("https://example.com/e.png", "推送后应立刻落下图标快照");

        // 紧接着的回刷应该无事可做
        _client.UpdatedPages.Clear();
        (await _sync.RefreshDailyTitlesFromMasterAsync()).Should().Be(0, "推送已落快照，回刷不该再重复 PATCH");
    }
}
