using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.Notion;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 双向删除的回归测试。
/// 之前的实际行为是：程序删游戏 → Notion 完全不动；Notion 删记录 → 本地照旧，
/// 而且被删掉的游戏还会被下一轮 Pull 凭空"复活"。
/// </summary>
public class NotionDeletionSyncTests : IDisposable
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

    public NotionDeletionSyncTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gttest_{Guid.NewGuid():N}.db");
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

    // ---------------- helpers ----------------

    private async Task<GameRecord> SeedGameAsync(
        string name = "不思议迷宫", string platform = "steam", string platformId = "3112010")
        => await _repo.GetOrCreateGameAsync(
            new GameIdentity(platform, platformId, name, name + ".exe", $@"C:\games\{name}.exe"));

    private async Task<int> SeedDailyAsync(
        int gameId, string date, int minutes, string? notionPageId, string status = "synced")
    {
        await _repo.AddSessionDurationToDailyAsync(date, gameId, minutes * 60);
        var row = (await _repo.GetDailySummariesByDateAsync(date)).Single(d => d.GameId == gameId);
        if (notionPageId != null || status != "pending")
        {
            await _repo.UpdateDailySyncStatusAsync(row.Id, status, notionPageId);
        }
        return row.Id;
    }

    private int Scalar(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---------------- Notion 删 → 本地跟着删 ----------------

    [Fact]
    public async Task Reconcile_DeletesLocalRecord_WhenNotionPageIsGone()
    {
        var game = await SeedGameAsync();
        var keptId = await SeedDailyAsync(game.Id, "2026-09-02", 30, "page-keep");
        var goneId = await SeedDailyAsync(game.Id, "2026-09-01", 45, "page-gone");

        // 远端只剩一条：page-gone 已不在
        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "page-keep",
            Date = "2026-09-02",
            GameTitle = game.Name,
            DurationMinutes = 30
        });

        var result = await _sync.ReconcileNotionDeletionsAsync();

        result.DeletedDailyRecords.Should().Be(1);
        (await _repo.GetDailySummaryByIdAsync(goneId)).Should().BeNull();
        (await _repo.GetDailySummaryByIdAsync(keptId)).Should().NotBeNull();

        // 物理删除前必须留下档
        Scalar("SELECT COUNT(*) FROM deleted_archive WHERE kind='daily' AND source='notion'").Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_KeepsPendingRecord_ThatWasNeverPushed()
    {
        var game = await SeedGameAsync();
        var pendingId = await SeedDailyAsync(game.Id, "2026-09-03", 20, null, status: "pending");

        _client.DailyRecords.Add(new NotionDailyRecordItem { PageId = "page-other", Date = "2026-09-02" });

        var result = await _sync.ReconcileNotionDeletionsAsync();

        result.DeletedDailyRecords.Should().Be(0);
        (await _repo.GetDailySummaryByIdAsync(pendingId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Reconcile_SkipsEntirely_WhenRemoteReturnsNothing()
    {
        var game = await SeedGameAsync();
        var id = await SeedDailyAsync(game.Id, "2026-09-01", 45, "page-gone");

        _client.DailyRecords.Clear(); // 远端返回空集：通常意味着权限/网络异常

        var result = await _sync.ReconcileNotionDeletionsAsync();

        result.Skipped.Should().BeTrue();
        (await _repo.GetDailySummaryByIdAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Reconcile_DeletesGame_WhenMasterEntryVanishedFromCatalog()
    {
        var live = await SeedGameAsync("仍然在总表里", "steam", "111");
        var dead = await SeedGameAsync("总表里已经删掉了", "steam", "222");
        await _repo.UpdateGameNotionIdAsync(dead.Id, "master-dead");
        await SeedDailyAsync(dead.Id, "2026-09-01", 60, "page-dead");

        // 总表快照里只剩 live 那一条
        _client.GameMasterItems.Add(new NotionGameCatalogItem { PageId = "master-live", Name = live.Name });

        await _sync.RefreshGameCatalogCacheAsync();
        var result = await _sync.ReconcileNotionDeletionsAsync();

        result.DeletedGames.Should().Be(1);
        (await _repo.GetGameByIdAsync(dead.Id)).Should().BeNull();
        (await _repo.GetGameByIdAsync(live.Id)).Should().NotBeNull();
        Scalar("SELECT COUNT(*) FROM deleted_archive WHERE kind='game'").Should().Be(1);
    }

    [Fact]
    public async Task RefreshCatalog_PurgesEntriesNoLongerInNotion()
    {
        await _repo.UpsertCatalogItemsAsync(new[]
        {
            new NotionGameCatalogItem { PageId = "page_bg3", Name = "旧测试残留" },
            new NotionGameCatalogItem { PageId = "master-live", Name = "真实条目" }
        });

        _client.GameMasterItems.Add(new NotionGameCatalogItem { PageId = "master-live", Name = "真实条目" });

        await _sync.RefreshGameCatalogCacheAsync();

        var catalog = await _repo.GetCatalogItemsAsync();
        catalog.Should().ContainSingle();
        catalog[0].PageId.Should().Be("master-live");
    }

    // ---------------- 防复活 ----------------

    [Fact]
    public async Task Pull_DoesNotResurrectGame_WhenMasterEntryIsGone()
    {
        // 总表里只有 A；B 的总表条目已被删除，但 B 的每日记录还留在每日表
        _client.GameMasterItems.Add(new NotionGameCatalogItem { PageId = "master-a", Name = "还在的游戏" });
        await _sync.RefreshGameCatalogCacheAsync();

        var gamesBefore = (await _repo.GetAllGamesAsync()).Count;

        _client.DailyRecords.Add(new NotionDailyRecordItem
        {
            PageId = "orphan-daily",
            Date = "2026-09-01",
            GameTitle = "已被删除的游戏",
            DurationMinutes = 30,
            GameMasterPageId = "master-gone"
        });

        await _sync.PullDailyRecordsFromNotionAsync();

        (await _repo.GetAllGamesAsync()).Count.Should().Be(gamesBefore, "总表里已不存在的游戏不该被拉回来");
    }

    // ---------------- 程序删 → Notion 跟着归档 ----------------

    [Fact]
    public async Task DeleteDailyRecordEverywhere_ArchivesNotionPageAndRemovesLocalRow()
    {
        var game = await SeedGameAsync();
        var date = "2026-09-05";
        var id = await SeedDailyAsync(game.Id, date, 55, "page-x");

        var result = await _sync.DeleteDailyRecordEverywhereAsync(id);

        result.NotionDailyRecordsArchived.Should().Be(1);
        _client.ArchivedPageIds.Should().Contain("page-x");
        (await _repo.GetDailySummaryByIdAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteGameEverywhere_ArchivesAllNotionRecordsAndMasterEntry()
    {
        var game = await SeedGameAsync();
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-1");
        await SeedDailyAsync(game.Id, "2026-09-04", 10, "page-1");
        await SeedDailyAsync(game.Id, "2026-09-05", 20, "page-2");
        await SeedDailyAsync(game.Id, "2026-09-06", 30, null, status: "pending");

        var result = await _sync.DeleteGameEverywhereAsync(game.Id, deleteMasterEntry: true);

        result.NotionDailyRecordsArchived.Should().Be(2);
        result.MasterEntryArchived.Should().BeTrue();
        result.LocalDailyRecordsDeleted.Should().Be(3);
        _client.ArchivedPageIds.Should().BeEquivalentTo(new[] { "page-1", "page-2", "master-1" });

        (await _repo.GetGameByIdAsync(game.Id)).Should().BeNull();
        Scalar("SELECT COUNT(*) FROM daily_summary WHERE game_id = " + game.Id).Should().Be(0);
        Scalar("SELECT COUNT(*) FROM deleted_archive WHERE kind='game' AND source='app'").Should().Be(1);
    }

    [Fact]
    public async Task DeleteGameEverywhere_KeepsMasterEntry_WhenNotRequested()
    {
        var game = await SeedGameAsync();
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-keep");

        var result = await _sync.DeleteGameEverywhereAsync(game.Id, deleteMasterEntry: false);

        result.MasterEntryArchived.Should().BeFalse();
        _client.ArchivedPageIds.Should().NotContain("master-keep");
        (await _repo.GetGameByIdAsync(game.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteGameEverywhere_SkipsNotion_WhenNotConfigured()
    {
        var unconfigured = new TrackerConfig { NotionToken = "", GameDatabaseId = "", DailyDatabaseId = "" };
        var sync = new NotionSyncService(_repo, _client, unconfigured);

        var game = await SeedGameAsync();
        var result = await sync.DeleteGameEverywhereAsync(game.Id, deleteMasterEntry: true);

        result.NotionSkipped.Should().BeTrue();
        _client.ArchivedPageIds.Should().BeEmpty();
        (await _repo.GetGameByIdAsync(game.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteDailyRecordEverywhere_ReportsFailure_WhenNotionArchiveThrows()
    {
        var game = await SeedGameAsync();
        var id = await SeedDailyAsync(game.Id, "2026-09-05", 55, "page-fail");
        _client.FailArchive = true;

        var result = await _sync.DeleteDailyRecordEverywhereAsync(id);

        result.NotionDailyRecordsFailed.Should().Be(1);
        result.Errors.Should().NotBeEmpty();
        // 本地仍按用户意图删除，但失败必须能报出来（不能像以前那样静默吞掉）
        (await _repo.GetDailySummaryByIdAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_DoesNotDeleteLocalRecord_WhenNotionSecondaryCheckConfirmsPageExists()
    {
        // 模拟 2026-09-19 QA 事故：
        // 游戏刚结束推送到 Notion，全量 Query 因 Notion 检索延迟未返回该记录，
        // 但通过二次确权检测到 Notion 页面依然健在，绝不能误删本地记录！
        var game = await SeedGameAsync("法老马赛克");
        var mosaicId = await SeedDailyAsync(game.Id, "2026-09-19", 35, "page-pharaoh-mosaic");

        // 远端全量返回空或其它页（模拟延时），不含 page-pharaoh-mosaic
        _client.DailyRecords.Add(new NotionDailyRecordItem { PageId = "page-other", Date = "2026-09-18" });

        // 但向 Notion 单独查询该 page 时，Notion 确认它存在且未归档
        _client.ActiveExistingPageIds.Add("page-pharaoh-mosaic");

        var result = await _sync.ReconcileNotionDeletionsAsync();

        result.DeletedDailyRecords.Should().Be(0);
        var record = await _repo.GetDailySummaryByIdAsync(mosaicId);
        record.Should().NotBeNull();
        record!.Date.Should().Be("2026-09-19");
    }
}
