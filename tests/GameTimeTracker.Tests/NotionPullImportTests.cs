using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.Notion;
using Dapper;
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
        _client.CreatedDailyRecordIcons.Should().ContainSingle()
            .Which.Should().Be("https://example.com/icon.png", "顺带把总表的 page icon 也设到这条记录上");
    }

    [Fact]
    public async Task SyncPending_KeepsProcessName_WhenGameIsNotBound()
    {
        // 未绑定 → relation 无从读起，只能保持进程名，也不该乱猜一个图标。
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "2002", "UnboundGame", "u.exe", @"C:\u.exe"));
        await _repo.AddSessionDurationToDailyAsync("2026-09-25", game.Id, 600);

        await _sync.SyncPendingDailyRecordsAsync();

        _client.CreatedDailyRecordTitles.Should().ContainSingle()
            .Which.Should().Be("UnboundGame", "未绑定总表时没有 relation 可读，只能退回进程名");
        _client.CreatedDailyRecordIcons.Should().ContainSingle()
            .Which.Should().BeNull("未绑定时不该凭空写一个图标");
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

        // 🚨 回刷**只许改呈现**（标题 / 图标），绝不许写「单次时长」。
        //    Notion 上的历史数值是权威，拿本地缓存覆盖它就是毁灭性错误 ——
        //    2026-09-19 一次运行改写了 QA 的 786 条记录（「2.2h」被改成了本地那份值）。
        _client.UpdatedPages.Should().BeEmpty("回刷绝不能写「单次时长」属性");

        // 走的是"只写标题"的通道
        _client.TitleOnlyUpdates.Should().ContainSingle()
            .Which.Item2.Should().Be("新名字 · 0.7 h",
                "只把游戏名换成总表的名字，原标题里的时长文本原样保留（不能用本地值重拼）");
        _client.UpdatedIcons.Should().ContainSingle()
            .Which.Should().Be("https://example.com/b.png", "换过的图标要一起写下去");
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

        // 🚨 即便"没有原标题可参考、只能按本地时长拼标题"这种情况，
        //    也**不能**把时长写进 Notion 的数值属性。
        _client.UpdatedPages.Should().BeEmpty("回刷绝不能写「单次时长」属性");

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

    // ================= 未绑定游戏：照常推送，但不该无限重推 =================

    [Fact]
    public async Task SyncPending_PushesUnboundRecord_ToDailyTableWithoutRelation()
    {
        // 用户的设计：本地为主。游戏没绑定总表也要照常推到每日时长表，
        // 只是不带 relation、绑定状态写「未绑定」。
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "4001", "未绑定的游戏", "u.exe", @"C:\u.exe"));
        await _repo.AddSessionDurationToDailyAsync("2026-10-01", game.Id, 30 * 60);

        var pushed = await _sync.SyncPendingDailyRecordsAsync();

        pushed.Should().Be(1, "未绑定也要推送");
        _client.CreatedDailyRecordTitles.Should().ContainSingle()
            .Which.Should().Be("未绑定的游戏");

        var row = (await _repo.GetDailySummariesByDateAsync("2026-10-01")).Single();
        row.NotionPageId.Should().NotBeNullOrEmpty("记录已经建到 Notion 上了");
        row.SyncStatus.Should().Be("unmapped", "状态是「已推送但未绑定」，不是 synced");
    }

    [Fact]
    public async Task SyncPending_DoesNotRepushUnboundRecord_EveryRound()
    {
        // 性能护栏：unmapped 曾被视为"待上传"（因为筛选条件是 != 'synced'），
        // 于是每轮同步都把这条记录重新 PATCH 一遍，永远不停。
        // 用户刚配好 Notion、还没绑游戏的那段时间最容易撞上，且随天数线性变慢。
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "4002", "不该重推", "r.exe", @"C:\r.exe"));
        await _repo.AddSessionDurationToDailyAsync("2026-10-02", game.Id, 30 * 60);

        await _sync.SyncPendingDailyRecordsAsync();   // 第一轮：正常推送

        // 第二轮、第三轮：时长没变，不应该再打 Notion
        _client.UpdatedPages.Clear();
        (await _sync.SyncPendingDailyRecordsAsync()).Should().Be(0, "时长没变就不该重推");
        (await _sync.SyncPendingDailyRecordsAsync()).Should().Be(0, "第三轮同样不该重推");
        _client.UpdatedPages.Should().BeEmpty("一次 PATCH 都不该发");
    }

    [Fact]
    public async Task SyncPending_RepushesUnboundRecord_WhenDurationGrows()
    {
        // 不重推的前提是"没变化"。时长涨了就必须重新推上去，
        // 否则未绑定游戏的时长会永远停在第一次的值。
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "4003", "时长会涨", "g.exe", @"C:\g.exe"));
        await _repo.AddSessionDurationToDailyAsync("2026-10-03", game.Id, 30 * 60);

        await _sync.SyncPendingDailyRecordsAsync();

        // 又玩了 15 分钟 → 状态应被改回 pending
        await _repo.AddSessionDurationToDailyAsync("2026-10-03", game.Id, 15 * 60);

        var row = (await _repo.GetDailySummariesByDateAsync("2026-10-03")).Single();
        row.DurationMinutes.Should().Be(45);
        row.SyncStatus.Should().Be("pending", "时长增加必须把状态改回待上传");

        var pushed = await _sync.SyncPendingDailyRecordsAsync();

        pushed.Should().Be(1, "时长变了要重新推");
        _client.UpdatedPages.Should().ContainSingle()
            .Which.DurationMinutes.Should().Be(45, "推上去的是新时长");
    }

    // ================= 同一天多个条目（跨日游玩未改日期）与回填防篡改保障 =================

    [Fact]
    public async Task Pull_WhenMultipleEntriesOnSameDateForSameGame_DoesNotOverwritePageIdNorCrash()
    {
        // 🚨 2026-09-19 QA 事故真实复现场景：
        // 跨日游玩忘记改前一天日期，导致 Notion 里同一天同一个游戏有两个条目（一条 2.2h，一条 2.4h）。
        // 本地 daily_summary 受 UNIQUE(date, game_id) 限制，绝不能让后一条直接把前一条的 notion_page_id 冲掉，
        // 否则两边 pageId 串号，回刷时会把 2.4h 覆盖写到 2.2h 的页面上。
        var item1 = new NotionDailyRecordItem
        {
            PageId = "page-valheim-1",
            Date = "2026-09-17",
            GameTitle = "英灵神殿",
            RawTitle = "英灵神殿 2.2h",
            DurationMinutes = 132,
            DurationUnitIsMinutes = false,
            GameMasterPageId = "master-valheim"
        };
        var item2 = new NotionDailyRecordItem
        {
            PageId = "page-valheim-2",
            Date = "2026-09-17",
            GameTitle = "英灵神殿",
            RawTitle = "英灵神殿 · 2.4 h",
            DurationMinutes = 144,
            DurationUnitIsMinutes = false,
            GameMasterPageId = "master-valheim"
        };

        // 第一条入库
        var id1 = await _repo.SyncDailyRecordFromNotionAsync(item1);
        id1.Should().BeGreaterThan(0);

        var record1 = (await _repo.GetDailySummariesByDateAsync("2026-09-17")).Single();
        record1.NotionPageId.Should().Be("page-valheim-1");
        record1.NotionTitle.Should().Be("英灵神殿 2.2h", "新入库时应记录原始标题快照");

        // 第二条同天入库：不应抛 UNIQUE constraint 异常，也不应冲掉已绑定的 pageId
        var id2 = await _repo.SyncDailyRecordFromNotionAsync(item2);
        id2.Should().Be(id1);

        var recordAfterBoth = (await _repo.GetDailySummariesByDateAsync("2026-09-17")).Single();
        recordAfterBoth.NotionPageId.Should().Be("page-valheim-1", "绝不能被第二条冲掉 pageId 导致串号");
        recordAfterBoth.DurationMinutes.Should().Be(132, "遇到同日重复页面时应跳过叠加，保持已收录的权威时长，杜绝多轮同步循环累加");
    }

    [Fact]
    public async Task BackfillRelations_DoesNotOverwriteRemoteDuration_AndPreservesOriginalSuffix()
    {
        // 🚨 回填总表关系时，只改 relation / 状态 / 标题呈现，绝不改 Notion 上的单次时长数值
        _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = "master-valheim-bf", Name = "英灵神殿", IconType = "external", IconUrl = "https://example.com/v.png"
        });
        await _sync.RefreshGameCatalogCacheAsync();

        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("manual", "manual:valheim-bf", "英灵神殿", "valheim.exe", @"C:\valheim.exe"));

        // 本地存着 144 分钟（2.4h），但 Notion 上的标题是带 2.2h 的手写记录
        var inserted = await _repo.SyncDailyRecordFromNotionAsync(new NotionDailyRecordItem
        {
            PageId = "page-valheim-bf",
            Date = "2026-09-16",
            GameTitle = "英灵神殿",
            RawTitle = "英灵神殿 2.2h",
            DurationMinutes = 132,
            DurationUnitIsMinutes = false,
            GameMasterPageId = null // 尚未关联总表
        });

        // 将该记录状态置为 unmapped，游戏绑定总表，触发回填
        await _repo.UpdateDailySyncStatusAsync(inserted, "unmapped");
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-valheim-bf");

        await _sync.BackfillRelationsAsync();

        // 断言：绝不能写「单次时长」数值属性
        _client.UpdatedPages.Should().BeEmpty("回填 relation 时绝不允许写 Notion 的单次时长");

        // 断言：写出的标题保留了原始的时长后缀 2.2h，没有用本地的 2.4h 重拼
        _client.UpdatedTitles.Should().ContainSingle()
            .Which.Should().Be("英灵神殿 2.2h", "回填标题必须保留原标题中的时长文本");
    }

    [Fact]
    public async Task Pull_WhenMigratingGhostGameRecordToMasterGameThatAlreadyHasRecordOnSameDate_MergesSafelyWithoutUniqueCrash()
    {
        // 🚨 2026-09-19 QA 最新报错真实复现：
        // 幽灵记录要迁移到正规游戏，但正规游戏当天已有一条汇总（例如 Steam 玩过或多条合并）。
        // 旧代码盲目 UPDATE daily_summary SET game_id = targetGameId，直接触发：
        // SQLite Error 19: 'UNIQUE constraint failed: daily_summary.date, daily_summary.game_id'.
        
        // 1. 创建正规游戏并关联总表
        var masterGame = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "892970", "英灵神殿", "valheim.exe", @"C:\valheim.exe"));
        await _repo.UpdateGameNotionIdAsync(masterGame.Id, "master-valheim-mig");

        // 2. 正规游戏在当天已有一条记录（60 分钟）
        await _repo.AddSessionDurationToDailyAsync("2026-09-17", masterGame.Id, 60 * 60);

        // 3. 历史上存在一个幽灵游戏（如「英灵神殿2.4h」），且它也在当天有一条记录（pageId = page-ghost-1）
        var ghostGame = await _repo.GetOrCreateGameAsync(
            new GameIdentity("manual", "manual:valheim-ghost", "英灵神殿2.4h", "", ""));
        var ghostRecordId = await _repo.SyncDailyRecordFromNotionAsync(new NotionDailyRecordItem
        {
            PageId = "page-ghost-1",
            Date = "2026-09-17",
            GameTitle = "英灵神殿2.4h",
            RawTitle = "英灵神殿2.4h",
            DurationMinutes = 144,
            DurationUnitIsMinutes = false,
            GameMasterPageId = null // 最初未关联
        });

        // 4. QA 在 Notion 关联了总表，重新拉取这条 page-ghost-1 记录（此时带上了 GameMasterPageId）
        var updateItem = new NotionDailyRecordItem
        {
            PageId = "page-ghost-1",
            Date = "2026-09-17",
            GameTitle = "英灵神殿",
            RawTitle = "英灵神殿 · 2.4 h",
            DurationMinutes = 144,
            DurationUnitIsMinutes = false,
            GameMasterPageId = "master-valheim-mig" // 关联到正规游戏
        };

        // 执行同步：绝不能报 SQLite Error 19，且两行合并为一行
        var finalId = await _repo.SyncDailyRecordFromNotionAsync(updateItem);
        finalId.Should().BeGreaterThan(0);

        // 当天应该只有一条英灵神殿的记录，且时长取较大值（144分钟）
        var summaries = await _repo.GetDailySummariesByDateAsync("2026-09-17");
        summaries.Should().ContainSingle();
        summaries.Single().GameId.Should().Be(masterGame.Id);
        summaries.Single().DurationMinutes.Should().Be(144);
    }

    [Fact]
    public async Task SyncPendingDailyRecords_WhenHistoricalRecordHasPendingStatus_DoesNotCallUpdateDailyRecord_AndMarksSyncedInDb()
    {
        // 🚨 核心守护测试：历史日期（如 2025-01-03）无论何种原因在本地变为 pending，
        // 同步时绝不能调用 UpdateDailyRecordAsync 去改写 Notion 上的单次时长数值，
        // 并应直接在本地自愈为 synced。
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("manual", "manual:sons-forest", "森林之子", "forest.exe", @"C:\forest.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-forest");

        // 插入一条 2025-01-03 的历史记录，模拟异常带有 pending 状态
        var summaryId = await _repo.SyncDailyRecordFromNotionAsync(new NotionDailyRecordItem
        {
            PageId = "page-forest-history",
            Date = "2025-01-03",
            GameTitle = "森林之子",
            RawTitle = "森林之子 · 6.3 h",
            DurationMinutes = 378,
            DurationUnitIsMinutes = false,
            GameMasterPageId = "master-forest"
        });

        // 模拟本地因某种原因被标记为 pending
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.ExecuteAsync("UPDATE daily_summary SET sync_status = 'pending' WHERE id = @summaryId;", new { summaryId });
        }

        // 执行推送同步
        await _sync.SyncPendingDailyRecordsAsync();

        // 断言：UpdateDailyRecordAsync 绝不能被调用（UpdatedPages 为空）
        _client.UpdatedPages.Should().BeEmpty("历史记录绝不允许向 Notion 回推写时长");

        // 断言：本地状态已自动自愈为 synced
        var records = await _repo.GetDailySummariesByDateAsync("2025-01-03");
        records.Single().SyncStatus.Should().Be("synced", "历史记录应直接在本地自愈为 synced");
    }

    [Fact]
    public async Task SyncDailyRecordFromNotion_WhenHistoricalRecord_NeverMarksPendingOrLocalAhead_AlwaysTakesRemoteValue()
    {
        // 🚨 核心守护测试：如果 QA 在 Notion 上手动把时长修改回 6.3h（378分钟），
        // 即使本地 SQLite 原本因 Bug 存了膨胀的时长（如 756分钟），
        // 拉取时也必须 100% 尊崇 Notion 权威数值，将其重置为 378 分钟，且状态置为 synced（绝不 localAhead）
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("manual", "manual:sons-forest-2", "森林之子", "forest.exe", @"C:\forest.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-forest-2");

        var summaryId = await _repo.SyncDailyRecordFromNotionAsync(new NotionDailyRecordItem
        {
            PageId = "page-forest-history-2",
            Date = "2025-01-03",
            GameTitle = "森林之子",
            RawTitle = "森林之子 · 12.6 h",
            DurationMinutes = 756,
            DurationUnitIsMinutes = false,
            GameMasterPageId = "master-forest-2"
        });

        // 模拟 QA 在 Notion 将其改回 6.3h（378分钟）后重新拉取
        await _repo.SyncDailyRecordFromNotionAsync(new NotionDailyRecordItem
        {
            PageId = "page-forest-history-2",
            Date = "2025-01-03",
            GameTitle = "森林之子",
            RawTitle = "森林之子 · 6.3 h",
            DurationMinutes = 378,
            DurationUnitIsMinutes = false,
            GameMasterPageId = "master-forest-2"
        });

        var records = await _repo.GetDailySummariesByDateAsync("2025-01-03");
        var record = records.Single();
        record.DurationMinutes.Should().Be(378, "历史记录应完全尊崇 Notion 远端权威数值，绝不能用 Math.Max 取本地旧值");
        record.SyncStatus.Should().Be("synced", "历史记录绝不能被判定为 localAhead 或 pending");
    }

    [Fact]
    public async Task SyncDailyRecordFromNotion_WhenUserChangesDateInNotion_UpdatesDateInDatabase()
    {
        // 🧪 测试：用户在 Notion 中将一条记录的日期由 2026-09-16 改为 2026-09-17
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "123456", "英灵神殿", "valheim.exe", @"C:\valheim.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-valheim");

        // 首次拉取：2026-09-16
        await _repo.SyncDailyRecordFromNotionAsync(new NotionDailyRecordItem
        {
            PageId = "page-valheim-1",
            Date = "2026-09-16",
            GameTitle = "英灵神殿",
            RawTitle = "英灵神殿 · 2.2 h",
            DurationMinutes = 132,
            DurationUnitIsMinutes = false,
            GameMasterPageId = "master-valheim"
        });

        (await _repo.GetDailySummariesByDateAsync("2026-09-16")).Should().HaveCount(1);
        (await _repo.GetDailySummariesByDateAsync("2026-09-17")).Should().BeEmpty();

        // 用户在 Notion 将日期修改为 2026-09-17
        await _repo.SyncDailyRecordFromNotionAsync(new NotionDailyRecordItem
        {
            PageId = "page-valheim-1",
            Date = "2026-09-17",
            GameTitle = "英灵神殿",
            RawTitle = "英灵神殿 · 2.4 h",
            DurationMinutes = 144,
            DurationUnitIsMinutes = false,
            GameMasterPageId = "master-valheim"
        });

        // 验证：旧日期的记录已移动至新日期，旧日期应为空，新日期应有且仅有1条
        (await _repo.GetDailySummariesByDateAsync("2026-09-16")).Should().BeEmpty("旧日期的记录应已移走或删除");
        var newRecords = await _repo.GetDailySummariesByDateAsync("2026-09-17");
        newRecords.Should().HaveCount(1);
        newRecords[0].Date.Should().Be("2026-09-17");
        newRecords[0].DurationMinutes.Should().Be(144);
        newRecords[0].NotionPageId.Should().Be("page-valheim-1");
    }

    [Fact]
    public async Task SyncDailyRecordFromNotion_WhenUserChangesDateAndTargetSlotAlreadyExists_MergesSafelyWithoutUniqueViolation()
    {
        // 🧪 核心安全测试：用户在 Notion 将日期改为 2026-09-17，但本地 2026-09-17 本来就有一条该游戏的未绑定或老记录
        // 必须执行安全合并更新，且删除旧日期的孤儿行，绝不能触发 UNIQUE constraint failed: daily_summary.date, daily_summary.game_id 崩溃
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "123456", "英灵神殿", "valheim.exe", @"C:\valheim.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-valheim");

        // 1. 在 2026-09-16 存在 page-valheim-old
        await _repo.SyncDailyRecordFromNotionAsync(new NotionDailyRecordItem
        {
            PageId = "page-valheim-old",
            Date = "2026-09-16",
            GameTitle = "英灵神殿",
            RawTitle = "英灵神殿 · 2.2 h",
            DurationMinutes = 132,
            DurationUnitIsMinutes = false,
            GameMasterPageId = "master-valheim"
        });

        // 2. 本地在 2026-09-17 已存在一条记录（例如本地游玩记录，尚未绑定 pageId）
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.ExecuteAsync(@"
                INSERT INTO daily_summary (game_id, date, duration_seconds, duration_minutes, session_count, sync_status)
                VALUES (@GameId, @Date, 1800, 30, 1, 'pending');",
                new { GameId = game.Id, Date = "2026-09-17" });
        }

        // 确认 16 日和 17 日各有一条
        (await _repo.GetDailySummariesByDateAsync("2026-09-16")).Should().HaveCount(1);
        (await _repo.GetDailySummariesByDateAsync("2026-09-17")).Should().HaveCount(1);

        // 3. 用户在 Notion 把 page-valheim-old 的日期改为 2026-09-17，触发碰撞
        Func<Task> act = async () =>
        {
            await _repo.SyncDailyRecordFromNotionAsync(new NotionDailyRecordItem
            {
                PageId = "page-valheim-old",
                Date = "2026-09-17",
                GameTitle = "英灵神殿",
                RawTitle = "英灵神殿 · 2.4 h",
                DurationMinutes = 144,
                DurationUnitIsMinutes = false,
                GameMasterPageId = "master-valheim"
            });
        };

        // 绝不允许抛出任何异常（特别是 SQLite Error 19 UNIQUE 约束异常）
        await act.Should().NotThrowAsync();

        // 验证：16 日孤儿行被删除，17 日槽位正确合并为 authoritative 的 Notion 数据
        (await _repo.GetDailySummariesByDateAsync("2026-09-16")).Should().BeEmpty("16日的旧孤儿行必须被删除");
        var records17 = await _repo.GetDailySummariesByDateAsync("2026-09-17");
        records17.Should().HaveCount(1, "17日应只有合并后的一条记录");
        records17[0].NotionPageId.Should().Be("page-valheim-old");
        records17[0].DurationMinutes.Should().Be(144);
    }

    [Fact]
    public async Task BackfillRelations_WhenRemoteRecordIsUnbound_ShouldUpdateBindingStatusToUnbound()
    {
        // 模拟远端有一条未关联总表、且尚未设置「绑定状态」的每日记录
        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "daily-unbound-1",
            Date = "2026-09-18",
            GameTitle = "独立游戏Demo",
            DurationMinutes = 60,
            GameMasterPageId = null,
            Status = null
        });

        // 1. 先拉取
        await _sync.PullDailyRecordsFromNotionAsync();

        // 2. 执行回填链路
        await _sync.BackfillRelationsAsync();

        // 断言：应当调用 UpdateDailyBindingStatusAsync 将其更新为「未绑定」
        _client.BindingStatusUpdates.Should().Contain(u => u.PageId == "daily-unbound-1" && u.Status == "未绑定");
    }
}


