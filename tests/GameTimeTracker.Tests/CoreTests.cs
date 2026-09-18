using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Database;
using Xunit;

namespace GameTimeTracker.Tests;

public class GameMatcherTests
{
    private readonly GameMatcher _matcher = new();

    [Theory]
    [InlineData("Baldur's Gate 3", "baldurs gate 3")]
    [InlineData("The Witcher III: Wild Hunt", "the witcher 3 wild hunt")]
    [InlineData("Final Fantasy VII Rebirth", "final fantasy 7 rebirth")]
    [InlineData("Grand Theft Auto V", "grand theft auto 5")]
    [InlineData("Monster Hunter: World", "monster hunter world")]
    // 末尾括号里的年份要剥掉（总表「别名」常写成这样，而进程名不带）
    [InlineData("Valheim (2020)", "valheim")]
    [InlineData("Doom (2016)", "doom")]
    [InlineData("Prey [2017]", "prey")]
    // 版本后缀也要剥掉（总表常写全称，进程名只有主名）
    [InlineData("Valheim Deluxe Edition", "valheim")]
    [InlineData("Skyrim Special Edition", "skyrim")]
    [InlineData("Elden Ring GOTY Edition", "elden ring")]
    [InlineData("Cyberpunk 2077 Ultimate Edition", "cyberpunk 2077")]
    // 组合形式（年份 + 版本，顺序不定）也要处理
    [InlineData("Hollow Knight (2017) Deluxe Edition", "hollow knight")]
    // 但裸年份不能剥 —— 那是游戏名的组成部分，剥了会让不同代互相误匹配
    [InlineData("Football Manager 2024", "football manager 2024")]
    [InlineData("F1 2023", "f1 2023")]
    [InlineData("NBA 2K24", "nba 2k24")]
    // ⚠️ 以下绝不能剥 —— 它们是游戏名的一部分，不是版本标记
    //    （放进来会让「Doom」匹配上「Doom Eternal」这类不同游戏）
    [InlineData("Doom Eternal", "doom eternal")]
    [InlineData("Final Fantasy VII Remake", "final fantasy 7 remake")]
    [InlineData("Final Fantasy VII Rebirth", "final fantasy 7 rebirth")]
    public void NormalizeTitle_ShouldNormalizeProperly(string input, string expected)
    {
        var result = _matcher.NormalizeTitle(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void MatchGame_ShouldMatchAliasWithTrailingYear()
    {
        // QA 实测场景（2026-09-18）：总表主名称是中文「英灵神殿」，
        // 别名写成「Valheim (2020)」，而正在玩的进程名是 Valheim。
        // 修复前归一化结果是 "valheim 2020"，精确匹配失败、
        // 模糊相似度只有 73.7% 卡在 80% 阈值下 → 候选为空 → 被判定成"新游戏"。
        var catalog = new List<NotionGameCatalogItem>
        {
            new() { PageId = "p1", Name = "英灵神殿", Aliases = new() { "Valheim (2020)" } }
        };

        var matches = _matcher.MatchGame("Valheim", catalog);

        matches.Should().NotBeEmpty("别名带年份后缀也必须能匹配上");
        matches[0].PageId.Should().Be("p1");
        matches[0].MatchType.Should().Be("normalized", "剥掉年份后应命中归一化匹配");
    }

    [Fact]
    public void MatchGame_ShouldMatchAliasWithEditionSuffix()
    {
        var catalog = new List<NotionGameCatalogItem>
        {
            new() { PageId = "p1", Name = "Skyrim Special Edition" }
        };

        var matches = _matcher.MatchGame("Skyrim", catalog);

        matches.Should().NotBeEmpty("版本后缀不应妨碍匹配");
        matches[0].PageId.Should().Be("p1");
    }

    [Fact]
    public void MatchGame_MustNotConflateDifferentGames()
    {
        // 归一化剥噪音是为了"同一款游戏的不同写法能对上"，
        // **不能**变成"不同游戏被混为一谈"。这几组是典型的危险组合：
        //   Doom / Doom Eternal          —— 副标题不是版本标记
        //   Portal / Portal 2            —— 数字序号不是年份
        //   FM 2023 / FM 2024            —— 裸年份是名字的一部分
        var catalog = new List<NotionGameCatalogItem>
        {
            new() { PageId = "doom-eternal", Name = "Doom Eternal" },
            new() { PageId = "portal-2", Name = "Portal 2" },
            new() { PageId = "fm2024", Name = "Football Manager 2024" },
            new() { PageId = "ff7r", Name = "Final Fantasy VII Rebirth" },
        };

        _matcher.MatchGame("Doom", catalog)
            .Should().NotContain(c => c.PageId == "doom-eternal", "Doom ≠ Doom Eternal");
        _matcher.MatchGame("Portal", catalog)
            .Should().NotContain(c => c.PageId == "portal-2", "Portal ≠ Portal 2");
        _matcher.MatchGame("Football Manager 2023", catalog)
            .Should().NotContain(c => c.PageId == "fm2024", "不同年份是不同代游戏");
        _matcher.MatchGame("Final Fantasy VII Remake", catalog)
            .Should().NotContain(c => c.PageId == "ff7r", "Remake ≠ Rebirth");
    }

    [Fact]
    public void MatchGame_ShouldMatchByIdentifier()
    {
        var catalog = new List<NotionGameCatalogItem>
        {
            new() { PageId = "p1", Name = "博德之门3", Identifiers = new() { "steam:1086940" } },
            new() { PageId = "p2", Name = "怪物猎人：世界", Identifiers = new() { "steam:582010" } }
        };

        var matches = _matcher.MatchGame("Baldur's Gate 3", catalog, steamAppId: "1086940");
        matches.Should().NotBeEmpty();
        matches[0].PageId.Should().Be("p1");
        matches[0].MatchType.Should().Be("identifier_match");
        matches[0].Score.Should().Be(100.0);
    }

    [Fact]
    public void MatchGame_ShouldMatchByFuzzyScore()
    {
        var catalog = new List<NotionGameCatalogItem>
        {
            new() { PageId = "p1", Name = "Monster Hunter: World", Aliases = new() { "怪物猎人：世界" } },
            new() { PageId = "p2", Name = "Grand Theft Auto V", Aliases = new() { "GTA 5" } }
        };

        var matches = _matcher.MatchGame("Monster Hunter World", catalog);
        matches.Should().NotBeEmpty();
        matches[0].PageId.Should().Be("p1");
        matches[0].Score.Should().BeGreaterThan(90.0);
    }
}

public class DailyAggregatorTests
{
    [Fact]
    public void FormatDuration_ShouldFormatCorrectly()
    {
        // 时长单位已改为小时（1 位小数）：41m → 0.7h，135m → 2.3h
        DailyAggregator.FormatDuration(380).Should().Be("6.3h");
        DailyAggregator.FormatDuration(45).Should().Be("0.8h");
        DailyAggregator.FormatDuration(120).Should().Be("2h");
        DailyAggregator.FormatDuration(0).Should().Be("0h");
    }


    [Fact]
    public void Aggregate_ShouldCalculateTodayAndStreakProperly()
    {
        var refDate = new DateTime(2026, 9, 17);
        var summaries = new List<DailySummary>
        {
            new() { Date = "2026-09-15", GameId = 1, DurationMinutes = 120, GameName = "Game A" },
            new() { Date = "2026-09-16", GameId = 1, DurationMinutes = 180, GameName = "Game A" },
            new() { Date = "2026-09-17", GameId = 1, DurationMinutes = 380, GameName = "Game A" },
            new() { Date = "2026-09-17", GameId = 2, DurationMinutes = 60, GameName = "Game B" }
        };

        var stats = DailyAggregator.Aggregate(refDate, summaries);
        stats.TodayMinutes.Should().Be(440); // 380 + 60
        stats.TodayDurationText.Should().Be("7.3h");
        stats.StreakDays.Should().Be(3); // 15th, 16th, 17th consecutive
        stats.HeatmapDays.Should().NotBeEmpty();
        stats.TodayTopGames.Should().HaveCount(2);
        stats.TodayTopGames[0].GameName.Should().Be("Game A");
        stats.ActivityHeatmap.Should().NotBeNull();
        stats.ActivityHeatmap.Cells.Should().HaveCount(364); // 52 x 7 = 364 days
    }

    [Theory]
    [InlineData(0, 500, 0)]      // 0 min -> Level 0
    [InlineData(10, 0, 0)]       // max is 0 -> Level 0 (protects divide by zero)
    [InlineData(50, 500, 1)]     // 10% -> Level 1 (1-20%)
    [InlineData(150, 500, 2)]    // 30% -> Level 2 (21-40%)
    [InlineData(250, 500, 3)]    // 50% -> Level 3 (41-60%)
    [InlineData(350, 500, 4)]    // 70% -> Level 4 (61-80%)
    [InlineData(450, 500, 5)]    // 90% -> Level 5 (81-100%)
    [InlineData(500, 500, 5)]    // 100% -> Level 5
    public void CalculateHeatmapLevel_MaxRelative_ShouldGradeAccurately(int minutes, int maxMinutes, int expectedLevel)
    {
        DailyAggregator.CalculateHeatmapLevel(minutes, maxMinutes).Should().Be(expectedLevel);
    }

    [Fact]
    public void GenerateActivityHeatmap_ShouldReturnStrictly84DaysAnd12Columns()
    {
        // Reference: 2026-09-17 (Thursday)
        var refDate = new DateTime(2026, 9, 17);
        var summaries = new List<DailySummary>
        {
            new() { Date = "2026-09-17", GameId = 1, DurationMinutes = 380, GameName = "Baldur's Gate 3" },
            new() { Date = "2026-09-17", GameId = 2, DurationMinutes = 60, GameName = "GTA 5" },
            new() { Date = "2026-09-15", GameId = 1, DurationMinutes = 120, GameName = "Baldur's Gate 3" }
        };

        var heatmap = DailyAggregator.GenerateActivityHeatmap(refDate, summaries, 12);
        heatmap.Cells.Should().HaveCount(84); // 12 weeks * 7 days
        heatmap.TotalDays.Should().Be(84);
        heatmap.MaxMinutes.Should().Be(440); // 380 + 60

        // Week 1 Sunday should be exactly 11 weeks before this week's Sunday (2026-09-13 - 77 days = 2026-06-28)
        var firstCell = heatmap.Cells.First();
        firstCell.Date.ToString("yyyy-MM-dd").Should().Be("2026-06-28");
        firstCell.DayOfWeek.Should().Be(0); // Sunday
        firstCell.WeekIndex.Should().Be(0);

        // Week 12 Saturday should be 2026-09-19
        var lastCell = heatmap.Cells.Last();
        lastCell.Date.ToString("yyyy-MM-dd").Should().Be("2026-09-19");
        lastCell.DayOfWeek.Should().Be(6); // Saturday
        lastCell.WeekIndex.Should().Be(11);

        // Verify Thursday 2026-09-17 (Today)
        var todayCell = heatmap.Cells.FirstOrDefault(c => c.Date.ToString("yyyy-MM-dd") == "2026-09-17");
        todayCell.Should().NotBeNull();
        todayCell!.IsToday.Should().BeTrue();
        todayCell.IsFuture.Should().BeFalse();
        todayCell.DurationMinutes.Should().Be(440);
        todayCell.Level.Should().Be(5); // 100% of max -> Level 5
        todayCell.TooltipText.Should().Contain("9月17日");
        todayCell.TooltipText.Should().Contain("7.3h");
        todayCell.TooltipText.Should().Contain("Baldur's Gate 3  6.3h");
        todayCell.TooltipText.Should().Contain("GTA 5  1h");

        // Verify Friday 2026-09-18 (Future day in current week)
        var futureCell = heatmap.Cells.FirstOrDefault(c => c.Date.ToString("yyyy-MM-dd") == "2026-09-18");
        futureCell.Should().NotBeNull();
        futureCell!.IsFuture.Should().BeTrue();
        futureCell.Level.Should().Be(0);
        futureCell.TooltipText.Should().BeEmpty();
    }

    [Fact]
    public void GenerateActivityHeatmap_ShouldReturn52WeeksAnd364Days()
    {
        var refDate = new DateTime(2026, 9, 17);
        var summaries = new List<DailySummary>
        {
            new() { Date = "2026-09-17", GameId = 1, DurationMinutes = 100, GameName = "Test Game" }
        };

        var heatmap = DailyAggregator.GenerateActivityHeatmap(refDate, summaries, 52);
        heatmap.Cells.Should().HaveCount(364); // 52 * 7
        heatmap.TotalDays.Should().Be(364);
        heatmap.MonthMarkers.Should().NotBeEmpty();
        heatmap.MonthMarkers.Count.Should().BeInRange(11, 14); // Covers approximately 12 months
    }
}

public class DatabaseAndSessionTests
{
    [Fact]
    public async Task SqliteRepository_ShouldPerformCrudCleanly()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gametime_test_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new SqliteRepository(dbPath);

            // 1. Create game
            var identity = new GameIdentity("steam", "1086940", "Baldur's Gate 3", "bg3.exe", @"C:\Steam\bg3.exe");
            var game = await repo.GetOrCreateGameAsync(identity);
            game.Id.Should().BeGreaterThan(0);
            game.Name.Should().Be("Baldur's Gate 3");

            // 2. Add daily duration
            await repo.AddSessionDurationToDailyAsync("2026-09-17", game.Id, 3600); // 1h
            var summaries = await repo.GetDailySummariesByDateAsync("2026-09-17");
            summaries.Should().HaveCount(1);
            summaries[0].DurationMinutes.Should().Be(60);
            summaries[0].SyncStatus.Should().Be("pending");

            // 3. Upsert catalog items
            await repo.UpsertCatalogItemsAsync(new[]
            {
                new NotionGameCatalogItem { PageId = "page_1", Name = "博德之门3", Identifiers = new() { "steam:1086940" } }
            });
            var catalog = await repo.GetCatalogItemsAsync();
            catalog.Should().HaveCount(1);
            catalog[0].Name.Should().Be("博德之门3");

            // 4. Test GetDailySummariesByGameIdAsync
            var gameSummaries = await repo.GetDailySummariesByGameIdAsync(game.Id);
            gameSummaries.Should().HaveCount(1);
            gameSummaries[0].GameId.Should().Be(game.Id);

            // 5. Test GetUnmappedDailySummariesAsync
            await repo.UpdateDailySyncStatusAsync(summaries[0].Id, "unmapped");
            var unmapped = await repo.GetUnmappedDailySummariesAsync();
            unmapped.Should().HaveCount(1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task GameSessionManager_MidnightSplit_ShouldSplitAcrossDays()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gametime_midnight_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new SqliteRepository(dbPath);
            var manager = new GameSessionManager(repo);

            var game = await repo.GetOrCreateGameAsync(new GameIdentity("steam", "100", "Night Game", "night.exe", @"C:\night.exe"));

            // Start session at 23:45 on Sept 16
            var start = new DateTime(2026, 9, 16, 23, 45, 0);
            var process = new DetectedProcess(1234, "night.exe", @"C:\night.exe", "Night Game");
            var session = await manager.StartSessionAsync(game, process, start);

            // Heartbeat at 00:15 on Sept 17 (30 mins total: 15 mins in Sept 16, 15 mins in Sept 17)
            var heartbeat = new DateTime(2026, 9, 17, 0, 15, 0);
            await manager.HeartbeatSessionAsync(process.Pid, heartbeat);

            // Verify Sept 16 summary
            var summaries16 = await repo.GetDailySummariesByDateAsync("2026-09-16");
            summaries16.Should().HaveCount(1);
            summaries16[0].DurationSeconds.Should().Be(15 * 60);
            summaries16[0].DurationMinutes.Should().Be(15);

            // Verify Sept 17 summary
            var summaries17 = await repo.GetDailySummariesByDateAsync("2026-09-17");
            summaries17.Should().HaveCount(1);
            summaries17[0].DurationSeconds.Should().Be(15 * 60);
            summaries17[0].DurationMinutes.Should().Be(15);

            // End session at 00:30 on Sept 17 (another 15 mins into Sept 17)
            var end = new DateTime(2026, 9, 17, 0, 30, 0);
            await manager.EndSessionAsync(process.Pid, end);

            var updated17 = await repo.GetDailySummariesByDateAsync("2026-09-17");
            updated17[0].DurationSeconds.Should().Be(30 * 60);
            updated17[0].DurationMinutes.Should().Be(30);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}

public class ProcessFilterTests
{
    [Theory]
    [InlineData("msedge.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData("msedgewebview2.exe", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\120.0.0.0\msedgewebview2.exe")]
    [InlineData("WeChatAppEx.exe", @"C:\Users\User\AppData\Roaming\Tencent\WeChat\XPlugin\Plugins\RadiumWMPF\WeChatAppEx.exe")]
    [InlineData("WorkBuddy.exe", @"C:\Program Files\WorkBuddy\WorkBuddy.exe")]
    [InlineData("YoudaoDictHelper.exe", @"C:\Program Files\Youdao\Dict\YoudaoDictHelper.exe")]
    [InlineData("Weixin.exe", @"C:\Program Files\Tencent\WeChat\Weixin.exe")]
    [InlineData("Antigravity.exe", @"C:\Users\User\AppData\Local\Programs\Antigravity\Antigravity.exe")]
    [InlineData("Clash Party.exe", @"C:\Program Files\Clash Party\Clash Party.exe")]
    [InlineData("MailClient.exe", @"C:\Program Files (x86)\eM Client\MailClient.exe")]
    [InlineData("GameInput.exe", @"C:\Program Files\WindowsApps\Microsoft.GamingServices_1.0.0.0_x64__8wekyb3d8bbwe\GameInput.exe")]
    [InlineData("nvcontainer.exe", @"C:\Program Files\NVIDIA Corporation\nvcontainer.exe")]
    [InlineData("MSPCManagerService.exe", @"C:\Program Files\Microsoft PC Manager\MSPCManagerService.exe")]
    [InlineData("svchost.exe", @"C:\Windows\System32\svchost.exe")]
    [InlineData("explorer.exe", @"C:\Windows\explorer.exe")]
    public void ProcessFilter_ShouldBlacklistNonGameProcesses(string procName, string exePath)
    {
        GameTimeTracker.Infrastructure.Process.ProcessFilter.IsBlacklisted(procName, exePath).Should().BeTrue();
    }

    [Theory]
    [InlineData("bg3.exe", @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3\bin\bg3.exe")]
    [InlineData("MonsterHunterWorld.exe", @"E:\Games\Steam\steamapps\common\Monster Hunter World\MonsterHunterWorld.exe")]
    public void ProcessFilter_ShouldNotBlacklistRealGames(string procName, string exePath)
    {
        GameTimeTracker.Infrastructure.Process.ProcessFilter.IsBlacklisted(procName, exePath).Should().BeFalse();
    }
}

public class ZombieSessionTests
{
    [Fact]
    public async Task CleanupZombieSessionsAsync_ShouldEndSessionsForDeadProcesses()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_zombie_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new SqliteRepository(dbPath);
            var manager = new GameSessionManager(repo);

            var game = await repo.GetOrCreateGameAsync(new GameIdentity("steam", "99999", "Dead Game", "deadgame.exe", @"C:\Games\deadgame.exe"));
            var deadProcess = new DetectedProcess(999999, "deadgame.exe", @"C:\Games\deadgame.exe", "Dead Game");

            await manager.StartSessionAsync(game, deadProcess);
            manager.GetActiveSessions().Should().HaveCount(1);

            await manager.CleanupZombieSessionsAsync();

            manager.GetActiveSessions().Should().BeEmpty();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}

public class GameRecordNotificationTests
{
    [Fact]
    public void GameRecord_PropertyChanged_ShouldNotifyWhenNotionPageIdChanges()
    {
        var game = new GameRecord { Id = 1, Name = "Test Game" };
        var changedProps = new List<string>();
        game.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        game.NotionPageId = "page-123";

        changedProps.Should().Contain(nameof(GameRecord.NotionPageId));
        changedProps.Should().Contain(nameof(GameRecord.IsNotionBound));
        changedProps.Should().Contain(nameof(GameRecord.NotionStatusText));
        game.IsNotionBound.Should().BeTrue();
        game.NotionStatusText.Should().Be("● 已绑定");

        changedProps.Clear();
        game.NotionPageId = null;
        game.IsNotionBound.Should().BeFalse();
        game.NotionStatusText.Should().Be("● 未绑定");
    }
}

public class CoverCacheSplashTests
{
    [Fact]
    public void CoverCacheService_GetSplashBackgroundPath_ShouldDetectSplashImage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_splash_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var exePath = Path.Combine(tempDir, "game.exe");
            File.WriteAllText(exePath, "fake exe");
            var splashPath = Path.Combine(tempDir, "SplashScreenImage.png");
            File.WriteAllBytes(splashPath, new byte[4096]);

            var coverService = new GameTimeTracker.Infrastructure.Covers.CoverCacheService();
            var detected = coverService.GetSplashBackgroundPath(exePath);

            detected.Should().Be(splashPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

public class CoverCacheIdempotencyTests
{
    /// <summary>
    /// 回归防护：ExtractAndSaveExecutableIcon 必须幂等。
    ///
    /// 该方法会【同步】触发 CoverDownloaded，而事件处理里会重新走
    /// 数据刷新 → RefreshHeroCardAsync → 再次调用本方法。
    /// 若「已存在封面」的判定失败，就会无限递归并栈溢出直接崩溃进程
    /// （实测异常码 0xC00000FD）。这里锁定两条不变量：
    /// ① 第二次及以后调用不得再触发事件；② 返回值始终为 true。
    /// </summary>
    [Fact]
    public void ExtractAndSaveExecutableIcon_ShouldBeIdempotent_AndRaiseEventOnlyOnce()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"test_cover_{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);
        try
        {
            var service = new GameTimeTracker.Infrastructure.Covers.CoverCacheService(cacheDir);
            int raised = 0;
            service.CoverDownloaded += (_, _) => raised++;

            // 用系统自带的 notepad.exe 作为稳定的图标来源
            var exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            File.Exists(exe).Should().BeTrue("该测试依赖 notepad.exe 提供图标");

            service.ExtractAndSaveExecutableIcon(exe, "manual", "cover-regression").Should().BeTrue();
            var coverPath = service.GetCoverPath("manual", "cover-regression");
            File.Exists(coverPath).Should().BeTrue();
            new FileInfo(coverPath).Length.Should().BeGreaterThan(0);

            raised.Should().Be(1, "首次提取后应恰好触发一次 CoverDownloaded");

            for (int i = 0; i < 5; i++)
            {
                service.ExtractAndSaveExecutableIcon(exe, "manual", "cover-regression").Should().BeTrue();
            }

            raised.Should().Be(1, "已存在封面时不得再次触发事件，否则会形成无限递归");
        }
        finally
        {
            try { Directory.Delete(cacheDir, true); } catch { }
        }
    }
}

public class NotionTwoWaySyncTests
{
    [Fact]
    public async Task SyncDailyRecordFromNotionAsync_ShouldInsertAndMatchGame()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_notion_sync_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new SqliteRepository(dbPath);
            var game = await repo.GetOrCreateGameAsync(new GameIdentity("steam", "12345", "My Test Game", "test.exe", @"C:\test.exe"));
            await repo.UpdateGameNotionIdAsync(game.Id, "master-page-123");

            var notionItem = new NotionDailyRecordItem
            {
                PageId = "daily-page-999",
                GameTitle = "My Test Game",
                Date = "2026-09-17",
                DurationMinutes = 45,
                GameMasterPageId = "master-page-123"
            };

            var id = await repo.SyncDailyRecordFromNotionAsync(notionItem);
            id.Should().BeGreaterThan(0);

            var summaries = await repo.GetDailySummariesByDateAsync("2026-09-17");
            summaries.Should().HaveCount(1);
            summaries[0].DurationMinutes.Should().Be(45);
            summaries[0].DurationSeconds.Should().Be(45 * 60);
            summaries[0].SyncStatus.Should().Be("synced");
            summaries[0].NotionPageId.Should().Be("daily-page-999");

            // Test updating with a larger duration
            notionItem.DurationMinutes = 60;
            var updatedId = await repo.SyncDailyRecordFromNotionAsync(notionItem);
            updatedId.Should().Be(id);

            var updatedSummaries = await repo.GetDailySummariesByDateAsync("2026-09-17");
            updatedSummaries[0].DurationMinutes.Should().Be(60);
            updatedSummaries[0].DurationSeconds.Should().Be(60 * 60);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}

