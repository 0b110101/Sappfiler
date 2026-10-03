using System.Net;
using System.Text;
using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.Notion;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 2j 验收：<b>Notion ↔ 本地 换机恢复</b>（「已忽略」跨机一致）。
///
/// 修复的真实行为错误（用户 2026-10-03 拍板）：
/// <code>
/// PC A：游戏 X 设为 ignored → 总表「已忽略」= true
/// PC B：换机导入 → 游戏 X 若被恢复成 active，就会被**重新计时**
/// </code>
/// 这条链路一旦通过，就等于钉死"换机以后又开始给我计时"。
///
/// 三条设计语义（详见附录 M）：
///   ① **属性探测**：总表没有「已忽略」属性时整段跳过 → 老用户行为零变化；
///   ② **三态严格区分**：null（没有属性）／true（用户勾了）／false（用户没勾）；
///   ③ **ignored 单向优先**：只做 Notion true → 本地 ignored，Notion false **不**反向覆盖本地
///      （否则"本地刚忽略、写回未生效"会被下一轮拉取抹掉）。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支；已按用户要求纳入 Git 跟踪。
/// </summary>
public class NotionCrossDeviceRecoveryTests : IDisposable
{
    private const string MasterPageId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly string _dbPath;
    private readonly SqliteRepository _repo;
    private readonly FakeNotionClient _client = new();
    private readonly TrackerConfig _config = new()
    {
        NotionToken = "ntn_test",
        GameDatabaseId = "game-db",
        DailyDatabaseId = "daily-db"
    };
    private readonly NotionSyncService _sync;

    public NotionCrossDeviceRecoveryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gt2j_{Guid.NewGuid():N}.db");
        _repo = new SqliteRepository(_dbPath);
        _sync = new NotionSyncService(_repo, _client, _config);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    // ============================ 工具 ============================

    private void SeedMasterItem(bool? ignored, string name = "游戏X", string pageId = MasterPageId)
        => _client.GameMasterItems.Add(new NotionGameCatalogItem
        {
            PageId = pageId,
            Name = name,
            IsIgnored = ignored
        });

    private void SeedDailyRecord(string gameTitle = "游戏X", int minutes = 155, string date = "2026-10-01",
        string masterPageId = MasterPageId)
        => _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = Guid.NewGuid().ToString("N"),      // 32 位十六进制，与真实 page id 同形
            Date = date,
            GameTitle = gameTitle,
            DurationMinutes = minutes,
            GameMasterPageId = masterPageId
        });

    private async Task<int> InsertBoundGameAsync(string name, string notionPageId, string status = "active")
    {
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("manual", Guid.NewGuid().ToString("N")[..8], name, string.Empty, string.Empty));
        await _repo.UpdateGameNotionIdAsync(game.Id, notionPageId);
        if (status != "active")
        {
            await _repo.UpdateGameStatusAsync(game.Id, status);
        }
        return game.Id;
    }

    private async Task<string> StatusOfAsync(int gameId)
        => (await _repo.GetGameByIdAsync(gameId))!.Status;

    /// <summary>模拟启动链：目录刷新 → 自动关联 → Pull → **Pull 之后再应用一次忽略**。</summary>
    private async Task<int> RunStartupChainAsync()
    {
        await _sync.RefreshGameCatalogCacheAsync();
        await _sync.AutoLinkGamesFromCatalogAsync();
        await _sync.PullDailyRecordsFromNotionAsync();
        return await _sync.ApplyCatalogIgnoredStateAsync();
    }

    // ============ 核心：换机端到端 ============

    [Fact]
    public async Task FreshMachine_ImportsIgnoredGame_AsIgnored_AndKeepsItsHistory()
    {
        // 总表里游戏 X 被标记「已忽略」，且带着一条历史记录
        SeedMasterItem(ignored: true);
        SeedDailyRecord(minutes: 155);

        var applied = await RunStartupChainAsync();

        applied.Should().Be(1, "换机后必须把总表的忽略状态落到本地");

        var game = (await _repo.GetAllGamesAsync()).Should().ContainSingle().Subject;
        game.Name.Should().Be("游戏X");
        game.Status.Should().Be("ignored", "否则换机后这个游戏会重新开始计时 —— 这正是要修的行为");
        game.NotionPageId.Should().Be(MasterPageId, "忽略状态不该影响绑定关系");

        // 忽略 ≠ 删数据：历史仍然完整恢复
        var summaries = await _repo.GetDailySummariesByDateAsync("2026-10-01");
        summaries.Should().ContainSingle();
        summaries[0].DurationMinutes.Should().Be(155);
        summaries[0].GameId.Should().Be(game.Id);
    }

    [Fact]
    public async Task WhenPropertyAbsent_IgnoreSyncIsSkipped_AndOldBehaviorIsIntact()
    {
        // 老用户的总表：没有「已忽略」属性（IsIgnored 全为 null）
        SeedMasterItem(ignored: null);
        SeedDailyRecord(minutes: 42);

        var applied = await RunStartupChainAsync();

        applied.Should().Be(0, "没有这个属性就整段跳过");
        var game = (await _repo.GetAllGamesAsync()).Should().ContainSingle().Subject;
        game.Status.Should().Be("active", "老用户行为必须零变化");

        // 反向：也不允许往没有该属性的库里写
        var pushed = await _sync.PushGameIgnoredStateAsync(game.Id, true);
        pushed.Should().BeFalse();
        _client.IgnoredWrites.Should().BeEmpty("属性不存在时绝不能写 Notion");
    }

    [Fact]
    public async Task LocalIgnore_IsPushedToNotion()
    {
        SeedMasterItem(ignored: false);
        await _sync.RefreshGameCatalogCacheAsync();       // 让客户端探测到属性存在
        _client.SupportsIgnoredProperty = true;
        var gameId = await InsertBoundGameAsync("游戏X", MasterPageId);

        var ok = await _sync.PushGameIgnoredStateAsync(gameId, true);

        ok.Should().BeTrue();
        _client.IgnoredWrites.Should().Equal((MasterPageId, true));

        // 恢复也要能写回
        await _sync.PushGameIgnoredStateAsync(gameId, false);
        _client.IgnoredWrites.Should().HaveCount(2);
        _client.IgnoredWrites[1].Ignored.Should().BeFalse();
    }

    [Fact]
    public async Task PushFailure_DoesNotChangeLocalStatus()
    {
        _client.SupportsIgnoredProperty = true;
        _client.FailIgnoredWrite = true;
        var gameId = await InsertBoundGameAsync("游戏X", MasterPageId, status: "ignored");

        var ok = await _sync.PushGameIgnoredStateAsync(gameId, true);

        ok.Should().BeFalse("写回失败只记日志");
        (await StatusOfAsync(gameId)).Should().Be("ignored", "本地状态已经生效，绝不能因为写回失败而回退");
        _client.IgnoredWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task NotionFalse_NeverRevivesLocallyIgnoredGame()
    {
        // 本地已忽略，但 Notion 上是 false（例如写回还没生效 / 用户在他机未改）
        SeedMasterItem(ignored: false);
        var gameId = await InsertBoundGameAsync("游戏X", MasterPageId, status: "ignored");

        var applied = await RunStartupChainAsync();

        applied.Should().Be(0);
        (await StatusOfAsync(gameId)).Should().Be("ignored", "Notion 的 false 不得反向覆盖本地（单向优先）");
    }

    [Fact]
    public async Task UnboundGame_CannotBePushed_AndDoesNotThrow()
    {
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("manual", "abcd1234", "没有绑定总表的游戏", "x.exe", @"E:\x\x.exe"));

        var ok = await _sync.PushGameIgnoredStateAsync(game.Id, true);

        ok.Should().BeFalse("未绑定总表就没有可写的地方");
        _client.IgnoredWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyIgnored_OnlyAffectsTheMatchingNotionPage()
    {
        SeedMasterItem(ignored: true, pageId: MasterPageId, name: "被忽略的游戏");
        SeedMasterItem(ignored: false, pageId: "cccccccccccccccccccccccccccccccc", name: "正常游戏");

        var ignoredGameId = await InsertBoundGameAsync("被忽略的游戏", MasterPageId);
        var normalGameId = await InsertBoundGameAsync("正常游戏", "cccccccccccccccccccccccccccccccc");

        await _sync.RefreshGameCatalogCacheAsync();

        (await StatusOfAsync(ignoredGameId)).Should().Be("ignored");
        (await StatusOfAsync(normalGameId)).Should().Be("active", "绝不能误伤其他游戏");
    }

    [Fact]
    public async Task ApplyIgnored_IsIdempotent_AndNeverTouchesDailyOrBinding()
    {
        SeedMasterItem(ignored: true);
        SeedDailyRecord(minutes: 90);
        var applied1 = await RunStartupChainAsync();
        applied1.Should().Be(1);

        var game = (await _repo.GetAllGamesAsync()).Single();
        var before = (await _repo.GetDailySummariesByDateAsync("2026-10-01")).Single();

        // 再跑两轮：状态已一致 → 不再写入，且时长/绑定/日期一字不动
        await _sync.RefreshGameCatalogCacheAsync();
        (await _sync.ApplyCatalogIgnoredStateAsync()).Should().Be(0, "已经忽略过就不该重复标记");

        (await StatusOfAsync(game.Id)).Should().Be("ignored");
        var after = (await _repo.GetDailySummariesByDateAsync("2026-10-01")).Single();
        after.DurationMinutes.Should().Be(before.DurationMinutes);
        after.SessionCount.Should().Be(before.SessionCount);
        after.Date.Should().Be(before.Date);
        (await _repo.GetGameByIdAsync(game.Id))!.NotionPageId.Should().Be(MasterPageId);
    }

    // ============ 真实 NotionClient 的三态解析（用桩 HttpClient，不联网）============

    [Fact]
    public async Task MasterQuery_ParsesIgnoredCheckbox_AndDistinguishesAbsenceFromFalse()
    {
        // ① 属性存在且被勾选 → true，且探测结果为"支持"
        var withTrue = new NotionClient("t", new HttpClient(new StubHandler(MasterQueryJson(true))));
        var itemsTrue = await withTrue.QueryGameMasterAsync("game-db");
        itemsTrue.Should().ContainSingle();
        itemsTrue[0].IsIgnored.Should().BeTrue();
        withTrue.SupportsGameIgnoredProperty.Should().BeTrue();

        // ② 属性存在但未勾选 → false（与 null 必须区分）
        var withFalse = new NotionClient("t", new HttpClient(new StubHandler(MasterQueryJson(false))));
        (await withFalse.QueryGameMasterAsync("game-db"))[0].IsIgnored.Should().BeFalse();
        withFalse.SupportsGameIgnoredProperty.Should().BeTrue();

        // ③ 属性不存在 → null（= 老用户库），并且探测为"不支持"
        var withoutProp = new NotionClient("t", new HttpClient(new StubHandler(MasterQueryJson(null))));
        (await withoutProp.QueryGameMasterAsync("game-db"))[0].IsIgnored.Should().BeNull(
            "「没有这个属性」和「用户没勾」语义完全不同");
        withoutProp.SupportsGameIgnoredProperty.Should().BeFalse();
    }

    private static string MasterQueryJson(bool? ignored)
    {
        // 用最朴素的拼接构造 JSON：raw string + 插值在 `{`/`}` 上极易出错（真实踩过一次）。
        var ignoredProp = ignored is null
            ? string.Empty
            : ",\"已忽略\":{\"type\":\"checkbox\",\"checkbox\":" + (ignored.Value ? "true" : "false") + "}";

        return "{\"results\":[{\"id\":\"" + MasterPageId + "\",\"properties\":{"
             + "\"游戏名称\":{\"type\":\"title\",\"title\":[{\"plain_text\":\"游戏X\"}]}"
             + ignoredProp
             + "}}],\"has_more\":false}";
    }

    /// <summary>
    /// 桩 HttpClient：按请求路径分流 —— 数据库 query 返回总表页（has_more=false，天然终止分页），
    /// 其他（如 schema GET）返回最小 schema。**不依赖调用次序**，避免"第一次调用是谁"的猜测。
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            var body = uri.Contains("/query", StringComparison.Ordinal)
                ? _json
                : """{"properties":{"游戏名称":{"type":"title"}}}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
