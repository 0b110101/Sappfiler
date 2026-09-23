using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using Microsoft.Data.Sqlite;
using GameTimeTracker.Infrastructure.Database;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 多游戏同时运行的会话管理：GameSessionManager 按 Pid 维护会话字典，
/// 天然支持并发游玩 —— 心跳/落库/结束互不干扰。这里锁定该行为。
/// </summary>
public class MultiSessionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;
    private readonly GameSessionManager _manager;

    public MultiSessionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gtmulti_{Guid.NewGuid():N}.db");
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
    }

    private async Task<GameRecord> MakeGameAsync(string name, string platformId)
        => await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", platformId, name, $"{name}.exe", @$"C:\{name}.exe"));

    [Fact]
    public async Task TwoGamesRunning_BothSessionsTrackedIndependently()
    {
        var a = await MakeGameAsync("游戏A", "3001");
        var b = await MakeGameAsync("游戏B", "3002");

        var baseTime = new DateTime(2026, 9, 18, 12, 0, 0);
        await _manager.StartSessionAsync(a, new DetectedProcess(111, "a.exe", @"C://a.exe", "游戏A"), baseTime);
        await _manager.StartSessionAsync(b, new DetectedProcess(222, "b.exe", @"C://b.exe", "游戏B"), baseTime);

        var active = _manager.GetActiveSessions();
        active.Should().HaveCount(2, "两个游戏同时运行时应存在两个活动会话");
        active.Select(s => s.GameId).Should().BeEquivalentTo(new[] { a.Id, b.Id });
    }

    [Fact]
    public async Task Heartbeats_AccumulatePerGame()
    {
        var a = await MakeGameAsync("游戏A", "3011");
        var b = await MakeGameAsync("游戏B", "3012");

        var start = new DateTime(2026, 9, 18, 12, 0, 0);
        await _manager.StartSessionAsync(a, new DetectedProcess(111, "a.exe", @"C://a.exe", "游戏A"), start);
        await _manager.StartSessionAsync(b, new DetectedProcess(222, "b.exe", @"C://b.exe", "游戏B"), start);

        // 各自心跳 5 分钟（同一时刻，互不干扰）
        await _manager.HeartbeatSessionAsync(111, start.AddMinutes(5));
        await _manager.HeartbeatSessionAsync(222, start.AddMinutes(5));

        var today = (await _repo.GetDailySummariesByDateAsync("2026-09-18")).ToDictionary(d => d.GameId, d => d);
        today[a.Id].DurationMinutes.Should().Be(5, "游戏 A 的时长独立累加");
        today[b.Id].DurationMinutes.Should().Be(5, "游戏 B 的时长独立累加");
    }

    [Fact]
    public async Task EndingOneGame_DoesNotAffectTheOther()
    {
        var a = await MakeGameAsync("游戏A", "3021");
        var b = await MakeGameAsync("游戏B", "3022");

        var start = new DateTime(2026, 9, 18, 12, 0, 0);
        await _manager.StartSessionAsync(a, new DetectedProcess(111, "a.exe", @"C://a.exe", "游戏A"), start);
        await _manager.StartSessionAsync(b, new DetectedProcess(222, "b.exe", @"C://b.exe", "游戏B"), start);

        await _manager.EndSessionAsync(111, start.AddMinutes(10));

        _manager.GetActiveSessions().Should().ContainSingle("A 退出后 B 仍应保持活动")
            .Which.GameId.Should().Be(b.Id);
    }

    [Fact]
    public async Task MultiProcessGame_GroupsByGameId_AndMaintainsDeterministicOrder()
    {
        var gameA = await MakeGameAsync("游戏A", "3031");
        var gameB = await MakeGameAsync("游戏B", "3032");

        var startA1 = new DateTime(2026, 9, 18, 12, 0, 0);
        var startB = new DateTime(2026, 9, 18, 12, 1, 0);
        var startA2 = new DateTime(2026, 9, 18, 12, 2, 0); // 游戏 A 的子进程后启动

        await _manager.StartSessionAsync(gameA, new DetectedProcess(101, "a_launcher.exe", @"C://a_launcher.exe", "游戏A 启动器"), startA1);
        await _manager.StartSessionAsync(gameB, new DetectedProcess(201, "b.exe", @"C://b.exe", "游戏B"), startB);
        await _manager.StartSessionAsync(gameA, new DetectedProcess(102, "a_game.exe", @"C://a_game.exe", "游戏A 主进程"), startA2);

        // 多进程同一游戏（如启动器 + 子进程）合并为一个游戏会话，杜绝时长成倍虚增
        var sessions = _manager.GetActiveSessions();
        sessions.Should().HaveCount(2, "同游戏的多个进程应合并为一个游戏会话，防止时长被重复计算");

        _manager.GetTrackedPids().Should().BeEquivalentTo(new[] { 101, 102, 201 });
        _manager.IsPidActive(101).Should().BeTrue();
        _manager.IsPidActive(102).Should().BeTrue();
        _manager.IsPidActive(201).Should().BeTrue();

        var sessionA = sessions.First(s => s.GameId == gameA.Id);
        sessionA.StartTime.Should().Be(startA1, "游戏A的启动时间应为最早拉起的启动器时间");

        // 启动器 101 退出后，游戏 A 的主进程 102 还在跑，会话应保持活跃
        await _manager.EndSessionAsync(101, startA2.AddMinutes(5));
        _manager.IsPidActive(101).Should().BeFalse();
        _manager.IsPidActive(102).Should().BeTrue();
        _manager.IsGameActive(gameA.Id).Should().BeTrue();

        // 当最后一个进程 102 也退出后，游戏 A 会话才真正结束
        await _manager.EndSessionAsync(102, startA2.AddMinutes(30));
        _manager.IsGameActive(gameA.Id).Should().BeFalse();
        _manager.IsGameActive(gameB.Id).Should().BeTrue();
    }
}
