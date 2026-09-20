using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

public class AccountingDateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;
    private readonly GameSessionManager _manager;

    public AccountingDateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gt_cutoff_{Guid.NewGuid():N}.db");
        _repo = new SqliteRepository(_dbPath);
        _manager = new GameSessionManager(_repo);
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

    [Theory]
    [InlineData(24, 0)]
    [InlineData(25, 1)]
    [InlineData(26, 2)]
    [InlineData(27, 3)]
    [InlineData(28, 4)]
    [InlineData(29, 5)]
    [InlineData(30, 6)]
    [InlineData(4, 4)]    // 容错 4 点换算为 28 点 -> offset 4
    [InlineData(50, 0)]   // 越界回退 24 点 -> offset 0
    public void AccountingDateHelper_GetOffsetHours_CalculatesCorrectly(int cutoff, int expectedOffset)
    {
        AccountingDateHelper.GetOffsetHours(cutoff).Should().Be(expectedOffset);
    }

    [Fact]
    public void AccountingDateHelper_CalculatesDateCorrectly_For28HourCutoff()
    {
        // 设置 28 点跨日（次日 04:00）
        // 2026-09-17 01:30 游玩 -> 仍属于 2026-09-16
        var dt1 = new DateTime(2026, 9, 17, 1, 30, 0);
        AccountingDateHelper.GetAccountingDateString(dt1, 28).Should().Be("2026-09-16");

        // 2026-09-17 03:59:59 游玩 -> 仍属于 2026-09-16
        var dt2 = new DateTime(2026, 9, 17, 3, 59, 59);
        AccountingDateHelper.GetAccountingDateString(dt2, 28).Should().Be("2026-09-16");

        // 2026-09-17 04:00:00 游玩 -> 跨入 2026-09-17
        var dt3 = new DateTime(2026, 9, 17, 4, 0, 0);
        AccountingDateHelper.GetAccountingDateString(dt3, 28).Should().Be("2026-09-17");

        // 2026-09-17 23:30:00 游玩 -> 属于 2026-09-17
        var dt4 = new DateTime(2026, 9, 17, 23, 30, 0);
        AccountingDateHelper.GetAccountingDateString(dt4, 28).Should().Be("2026-09-17");
    }

    [Fact]
    public void AccountingDateHelper_GetNextCutoffBoundary_ReturnsExactBoundary()
    {
        // 2026-09-17 02:30, cutoff=28（归属 2026-09-16） -> 下一个分界点是 2026-09-17 04:00:00
        var dt = new DateTime(2026, 9, 17, 2, 30, 0);
        var boundary = AccountingDateHelper.GetNextCutoffBoundary(dt, 28);
        boundary.Should().Be(new DateTime(2026, 9, 17, 4, 0, 0));
    }

    [Fact]
    public async Task SessionManager_With28HourCutoff_LateNightPlay_AttributedToPreviousDay()
    {
        _manager.DailyCutoffHour = 28; // 凌晨 4 点跨日
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "777", "黑神话：悟空", "b1.exe", @"C:\b1.exe"));

        // 凌晨 01:00 开始游玩，03:00 结束（游玩 2 小时 = 7200 秒）
        var start = new DateTime(2026, 9, 17, 1, 0, 0);
        var proc = new DetectedProcess(999, "b1.exe", @"C:\b1.exe", "黑神话：悟空");
        await _manager.StartSessionAsync(game, proc, start);

        // 心跳到 02:00
        await _manager.HeartbeatSessionAsync(999, start.AddHours(1));
        // 结束于 03:00
        await _manager.EndSessionAsync(999, start.AddHours(2));

        // 验证：全部时长归入 2026-09-16，2026-09-17 应为空！
        var day16 = await _repo.GetDailySummariesByDateAsync("2026-09-16");
        var day17 = await _repo.GetDailySummariesByDateAsync("2026-09-17");

        day16.Should().HaveCount(1);
        day16[0].DurationMinutes.Should().Be(120);
        day17.Should().BeEmpty("设置28点跨日时，凌晨3点前的游玩绝不能出现在17日");
    }

    [Fact]
    public async Task SessionManager_With28HourCutoff_CrossingBoundary_SplitsCorrectly()
    {
        _manager.DailyCutoffHour = 28; // 凌晨 4 点跨日
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "888", "艾尔登法环", "elden.exe", @"C:\elden.exe"));

        // 凌晨 03:45 开始游玩，04:15 结束（总计 30 分钟）
        // 03:45 ~ 04:00 (15 分钟) 应归属 2026-09-16
        // 04:00 ~ 04:15 (15 分钟) 应归属 2026-09-17
        var start = new DateTime(2026, 9, 17, 3, 45, 0);
        var proc = new DetectedProcess(8888, "elden.exe", @"C:\elden.exe", "艾尔登法环");
        await _manager.StartSessionAsync(game, proc, start);

        await _manager.EndSessionAsync(8888, new DateTime(2026, 9, 17, 4, 15, 0));

        var day16 = await _repo.GetDailySummariesByDateAsync("2026-09-16");
        var day17 = await _repo.GetDailySummariesByDateAsync("2026-09-17");

        day16.Should().HaveCount(1);
        day16[0].DurationMinutes.Should().Be(15, "04:00 之前的 15 分钟归属前一日");

        day17.Should().HaveCount(1);
        day17[0].DurationMinutes.Should().Be(15, "04:00 之后的 15 分钟归属后一日");
    }

    [Fact]
    public async Task SessionManager_WithDefault24HourCutoff_MidnightCrossingSplitsCorrectly()
    {
        _manager.DailyCutoffHour = 24; // 默认标准跨日
        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "999", "赛博朋克2077", "cyber.exe", @"C:\cyber.exe"));

        // 23:40 开始游玩，次日 00:20 结束（总计 40 分钟）
        var start = new DateTime(2026, 9, 16, 23, 40, 0);
        var proc = new DetectedProcess(7777, "cyber.exe", @"C:\cyber.exe", "赛博朋克2077");
        await _manager.StartSessionAsync(game, proc, start);

        await _manager.EndSessionAsync(7777, new DateTime(2026, 9, 17, 0, 20, 0));

        var day16 = await _repo.GetDailySummariesByDateAsync("2026-09-16");
        var day17 = await _repo.GetDailySummariesByDateAsync("2026-09-17");

        day16.Should().HaveCount(1);
        day16[0].DurationMinutes.Should().Be(20, "午夜前的 20 分钟归属 16 日");

        day17.Should().HaveCount(1);
        day17[0].DurationMinutes.Should().Be(20, "午夜后的 20 分钟归属 17 日");
    }
}
