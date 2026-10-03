using Dapper;
using FluentAssertions;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// Phase 2d 护栏：结束会话的**原子接线**
/// （daily_summary + sessions + sync_queue 必须在同一个 SQLite 事务里）。
///
/// 四组必须证明的事：
///   1. 正常路径：三者一起提交，Outbox 里有且只有一条 Session 变更。
///   2. **queue 里的 payload 快照 == 提交后的最终 session / game identity**
///      （直接抓"先 snapshot、后补 identity"这种极隐蔽的问题）。
///   3. 任意一步失败 → 三者**全部回滚**，且会话仍能被既有恢复机制接住。
///   4. **5 秒心跳绝不进入 outbox**；结束之后才产生同步事实。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public class DeviceSyncEndSessionAtomicTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;

    public DeviceSyncEndSessionAtomicTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sappfiler_endatom_{Guid.NewGuid():N}.db");
        _repo = new SqliteRepository(_dbPath);
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

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        conn.Open();
        return conn;
    }

    private T Scalar<T>(string sql, object? param = null)
    {
        using var conn = Open();
        return conn.ExecuteScalar<T>(sql, param)!;
    }

    private void Exec(string sql)
    {
        using var conn = Open();
        conn.Execute(sql);
    }

    private async Task<GameRecord> MakeGameAsync(string name, string platformId)
        => await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", platformId, name, $"{name}.exe", @$"E:\games\{name}.exe"));

    // ============================ 1. 正常路径 ============================

    [Fact]
    public async Task EndSession_CommitsDailySessionAndOutboxTogether()
    {
        var game = await MakeGameAsync("Atomic", "atomic-1");
        var start = new DateTime(2026, 10, 3, 20, 0, 0);
        var session = await _repo.CreateSessionAsync(game.Id, 555, "Atomic.exe", start);

        var end = start.AddSeconds(1800);
        var result = await _repo.EndSessionWithOutboxAsync(session.Id, end, 1800,
            new[] { new DailyDurationDelta("2026-10-03", 1800) });

        result.SessionEnded.Should().BeTrue();
        result.OutboxEnqueued.Should().BeTrue();

        // sessions
        Scalar<int>("SELECT is_active FROM sessions WHERE id = @id;", new { id = session.Id }).Should().Be(0);
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE id = @id;", new { id = session.Id }).Should().Be(1800);
        Scalar<string>("SELECT ended_at_utc FROM sessions WHERE id = @id;", new { id = session.Id })
            .Should().NotBeNullOrWhiteSpace("ended_at_utc 必须在结束事件确定时写入");
        Scalar<string>("SELECT started_at_utc FROM sessions WHERE id = @id;", new { id = session.Id })
            .Should().NotBeNullOrWhiteSpace();

        // identity 在"实体定型"这一刻补齐
        Scalar<string>("SELECT global_id FROM sessions WHERE id = @id;", new { id = session.Id }).Should().NotBeNullOrWhiteSpace();
        Scalar<string>("SELECT device_id FROM sessions WHERE id = @id;", new { id = session.Id }).Should().NotBeNullOrWhiteSpace();
        Scalar<string>("SELECT global_id FROM games WHERE id = @id;", new { id = game.Id }).Should().NotBeNullOrWhiteSpace();

        // daily_summary（尾段增量）
        Scalar<int>("SELECT duration_seconds FROM daily_summary WHERE date = '2026-10-03' AND game_id = @g;", new { g = game.Id })
            .Should().Be(1800);

        // sync_queue（有且只有一条 Session 变更）
        Scalar<int>("SELECT COUNT(*) FROM sync_queue;").Should().Be(1);
        Scalar<string>("SELECT entity_type FROM sync_queue;").Should().Be(nameof(SyncEntityType.Session));
        Scalar<string>("SELECT status FROM sync_queue;").Should().Be("0", "刚入队必须是 Pending");
    }

    // ============ 2. 【用户点名】payload 快照 == 提交后的最终 identity ============

    [Fact]
    public async Task OutboxSnapshot_MatchesCommittedSessionAndGameIdentity()
    {
        var game = await MakeGameAsync("Snapshot", "snapshot-1");
        var start = new DateTime(2026, 10, 3, 21, 15, 30);
        var session = await _repo.CreateSessionAsync(game.Id, 777, "Snapshot.exe", start);

        var end = start.AddSeconds(1234);
        await _repo.EndSessionWithOutboxAsync(session.Id, end, 1234,
            new[] { new DailyDurationDelta("2026-10-03", 1234) });

        using var conn = Open();

        var sessionRow = conn.QuerySingle<(string? GlobalId, string? DeviceId, string? EndedAtUtc, int Duration, int GameId)>(
            "SELECT global_id, device_id, ended_at_utc, duration_seconds, game_id FROM sessions WHERE id = @id;",
            new { id = session.Id });

        var gameGlobalId = conn.ExecuteScalar<string>(
            "SELECT global_id FROM games WHERE id = @g;", new { g = sessionRow.GameId });

        var payloadJson = conn.ExecuteScalar<string>("SELECT payload FROM sync_queue;");
        SyncPayloads.TryParseSession(payloadJson, out var payload, out _, out _).Should().BeTrue();
        var p = payload!;

        // ⚠️ 这一组断言专门抓"先生成 payload、再补 global_id"的隐蔽错误：
        //    queue 里引用的 identity / duration / ended_at 必须与**提交后**的库状态逐字一致。
        p.GlobalId.Should().Be(sessionRow.GlobalId, "session.global_id 必须等于 payload.session_global_id");
        p.GameGlobalId.Should().Be(gameGlobalId, "session.game_id → games.global_id 必须等于 payload.game_global_id");
        p.DurationSeconds.Should().Be(sessionRow.Duration);
        p.EndedAtUtc.Should().Be(sessionRow.EndedAtUtc);
        p.DeviceId.Should().Be(sessionRow.DeviceId);
        p.ProcessName.Should().Be("Snapshot.exe");

        p.GlobalId.Should().NotBeNullOrWhiteSpace().And.NotBe("00000000-0000-0000-0000-000000000000");
        p.GameGlobalId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExistingGlobalIds_AreNeverRegenerated()
    {
        var game = await MakeGameAsync("KeepId", "keepid-1");
        var session = await _repo.CreateSessionAsync(game.Id, 888, "KeepId.exe", new DateTime(2026, 10, 3, 22, 0, 0));

        // 模拟"已经有 identity"（例如 Phase 1 迁移时已分配）
        Exec($"UPDATE sessions SET global_id = 'keep-session-id' WHERE id = {session.Id};");
        Exec($"UPDATE games SET global_id = 'keep-game-id' WHERE id = {game.Id};");

        await _repo.EndSessionWithOutboxAsync(session.Id, new DateTime(2026, 10, 3, 22, 10, 0), 600,
            new[] { new DailyDurationDelta("2026-10-03", 600) });

        Scalar<string>("SELECT global_id FROM sessions WHERE id = @id;", new { id = session.Id })
            .Should().Be("keep-session-id", "已存在的 identity 绝不能被重新生成");
        Scalar<string>("SELECT global_id FROM games WHERE id = @id;", new { id = game.Id })
            .Should().Be("keep-game-id");

        using var conn = Open();
        SyncPayloads.TryParseSession(conn.ExecuteScalar<string>("SELECT payload FROM sync_queue;"), out var payload, out _, out _);
        payload!.GlobalId.Should().Be("keep-session-id");
        payload.GameGlobalId.Should().Be("keep-game-id");
    }

    // ============================ 3. 任一步失败 → 整体回滚 ============================

    [Fact]
    public async Task EndSession_WhenOutboxWriteFails_RollsBackDailyAndSessionToo()
    {
        var game = await MakeGameAsync("Rollback", "rollback-1");
        var start = new DateTime(2026, 10, 3, 23, 0, 0);
        var session = await _repo.CreateSessionAsync(game.Id, 999, "Rollback.exe", start);

        // 注入：让事务的**最后一步**（写 sync_queue）失败。
        // 若没有事务，就会出现"daily 已写 + 会话已结束 + outbox 没写"的中间态。
        Exec("""
            CREATE TRIGGER abort_queue_insert BEFORE INSERT ON sync_queue
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        var result = await _repo.EndSessionWithOutboxAsync(session.Id, start.AddSeconds(900), 900,
            new[] { new DailyDurationDelta("2026-10-03", 900) });

        result.SessionEnded.Should().BeFalse("失败要如实返回，不能假装成功");
        result.OutboxEnqueued.Should().BeFalse();

        Scalar<int>("SELECT is_active FROM sessions WHERE id = @id;", new { id = session.Id })
            .Should().Be(1, "整体回滚：会话必须仍是活跃状态，交给恢复机制处理");
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE id = @id;", new { id = session.Id })
            .Should().Be(0, "会话的结束写入也必须回滚");
        Scalar<string>("SELECT ended_at_utc FROM sessions WHERE id = @id;", new { id = session.Id })
            .Should().BeNullOrWhiteSpace();
        Scalar<int>("SELECT COUNT(*) FROM daily_summary;").Should().Be(0, "daily_summary 同样必须回滚");
        Scalar<int>("SELECT COUNT(*) FROM sync_queue;").Should().Be(0);
    }

    [Fact]
    public async Task AfterRollback_SessionIsStillRecoverable_AndLocalTimingStillWorks()
    {
        var game = await MakeGameAsync("Recover", "recover-1");
        var start = DateTime.Now.AddMinutes(-10);
        var session = await _repo.CreateSessionAsync(game.Id, 1234, "Recover.exe", start);

        Exec("""
            CREATE TRIGGER abort_queue_insert BEFORE INSERT ON sync_queue
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        await _repo.EndSessionWithOutboxAsync(session.Id, start.AddSeconds(300), 300,
            new[] { new DailyDurationDelta(AccountingDateHelper.GetAccountingDateString(start, 24), 300) });

        // ① 会话仍被既有恢复机制看得见（GetActiveSessionsAsync = 启动恢复的入口）
        var active = await _repo.GetActiveSessionsAsync();
        active.Should().ContainSingle().Which.Id.Should().Be(session.Id, "否则这局游戏就永远挂着/丢了");

        // ② 既有的僵尸清理仍能把"卡住的会话"收尾（会话行不会永远 active）
        await _repo.CleanupStaleSessionsAsync(TimeSpan.Zero);
        Scalar<int>("SELECT is_active FROM sessions WHERE id = @id;", new { id = session.Id }).Should().Be(0);

        // ③ 本地计时路径完全不受影响（同步能力绝不反过来害本地）
        using (var conn = Open())
        {
            conn.Execute("DROP TRIGGER abort_queue_insert;");
        }

        var date = AccountingDateHelper.GetAccountingDateString(start, 24);
        await _repo.AddSessionDurationToDailyAsync(date, game.Id, 120);
        Scalar<int>("SELECT duration_seconds FROM daily_summary WHERE date = @d AND game_id = @g;", new { d = date, g = game.Id })
            .Should().Be(120);
    }

    // ============ 4. 心跳不入队；结束才入队（端到端，走 GameSessionManager） ============

    [Fact]
    public async Task HeartbeatPath_NeverEnqueuesOutbox_UntilSessionEnds()
    {
        var repo = _repo;
        var manager = new GameSessionManager(repo);
        var game = await MakeGameAsync("Heartbeat", "heartbeat-1");

        var t0 = new DateTime(2026, 10, 3, 20, 0, 0);
        await manager.StartSessionAsync(game, new DetectedProcess(4242, "Heartbeat.exe", @"E:\games\Heartbeat.exe", "Heartbeat"), t0);

        await manager.HeartbeatSessionAsync(4242, t0.AddSeconds(5));
        await manager.HeartbeatSessionAsync(4242, t0.AddSeconds(10));
        await manager.HeartbeatSessionAsync(4242, t0.AddSeconds(15));

        Scalar<int>("SELECT COUNT(*) FROM sync_queue;")
            .Should().Be(0, "5 秒心跳绝不能进 outbox —— 不能每 5 秒同步一次云端");
        Scalar<int>("SELECT COUNT(*) FROM daily_summary;")
            .Should().Be(1, "心跳仍然照常落本地 daily_summary");

        // 结束之后才产生同步事实
        await manager.EndSessionAsync(4242, t0.AddSeconds(45));

        Scalar<int>("SELECT COUNT(*) FROM sync_queue;").Should().Be(1, "结束之后才入队");
        Scalar<string>("SELECT entity_type FROM sync_queue;").Should().Be(nameof(SyncEntityType.Session));
        Scalar<int>("SELECT duration_seconds FROM sessions LIMIT 1;").Should().Be(45);
        Scalar<int>("SELECT duration_seconds FROM daily_summary LIMIT 1;").Should().Be(45);
    }

    // ============ 5. 跨日结算点：同事务写两天 ============

    [Fact]
    public async Task EndSession_CrossingCutoff_WritesBothDaysInTheSameTransaction()
    {
        // 纯函数：末段增量跨过 00:00 结算点 → 拆成两笔，归属日期正确
        var deltas = GameSessionManager.ComputeDailyDeltas(
            new DateTime(2026, 10, 3, 23, 59, 30),
            new DateTime(2026, 10, 4, 0, 0, 30),
            deltaSeconds: 60,
            cutoffHour: 24);

        deltas.Should().HaveCount(2, "跨越结算点必须精确拆成两笔");
        deltas[0].Date.Should().Be("2026-10-03");
        deltas[0].DurationSeconds.Should().Be(30);
        deltas[1].Date.Should().Be("2026-10-04");
        deltas[1].DurationSeconds.Should().Be(30);

        // 未跨结算点 → 单笔
        GameSessionManager.ComputeDailyDeltas(
            new DateTime(2026, 10, 3, 10, 0, 0),
            new DateTime(2026, 10, 3, 10, 5, 0),
            deltaSeconds: 300,
            cutoffHour: 24)
            .Should().ContainSingle().Which.Date.Should().Be("2026-10-03");

        // 增量 <= 0 → 不产生任何写入
        GameSessionManager.ComputeDailyDeltas(DateTime.Now, DateTime.Now, 0, 24).Should().BeEmpty();

        // 真正落库：两天的 daily_summary 在同一事务里一起写下
        var game = await MakeGameAsync("Midnight", "midnight-1");
        var session = await _repo.CreateSessionAsync(game.Id, 4321, "Midnight.exe", new DateTime(2026, 10, 3, 23, 58, 0));

        await _repo.EndSessionWithOutboxAsync(session.Id, new DateTime(2026, 10, 4, 0, 0, 30), 150, deltas);

        Scalar<int>("SELECT duration_seconds FROM daily_summary WHERE date = '2026-10-03' AND game_id = @g;", new { g = game.Id })
            .Should().Be(30);
        Scalar<int>("SELECT duration_seconds FROM daily_summary WHERE date = '2026-10-04' AND game_id = @g;", new { g = game.Id })
            .Should().Be(30);
        Scalar<int>("SELECT COUNT(*) FROM sync_queue;").Should().Be(1);
    }

    // ============ 6. 会话不存在时不留垃圾 ============

    [Fact]
    public async Task EndSession_UnknownSession_IsANoOp()
    {
        var result = await _repo.EndSessionWithOutboxAsync(999999, DateTime.Now, 10,
            new[] { new DailyDurationDelta("2026-10-03", 10) });

        result.SessionEnded.Should().BeFalse();
        Scalar<int>("SELECT COUNT(*) FROM sync_queue;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM daily_summary;").Should().Be(0, "不该为一个不存在的会话留下每日记录");
    }
}
