using Dapper;
using FluentAssertions;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.DeviceSync;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 2e 步骤 6 验收（持久化层）：**Session 身份收敛**。
///
/// 逐条对应用户给的 12 条验收重点。
/// 一句话原则：**Session 是历史事实** —— 收敛只能"记录两套 identity 指的是同一条事实"，
/// 绝不允许改写、删除、或用 LWW/取最大值去裁决。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public class DeviceSyncSessionReconciliationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;

    public DeviceSyncSessionReconciliationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sappfiler_sessrec_{Guid.NewGuid():N}.db");
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

    private int InsertGame(string name, string? globalId)
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>("""
            INSERT INTO games (platform, platform_id, name, executable, executable_path, status, global_id)
            VALUES ('steam', @pid, @name, @name, @path, 'active', @globalId);
            SELECT last_insert_rowid();
            """,
            new { pid = "pid-" + name, name, path = @"E:\g\" + name + ".exe", globalId });
    }

    private int InsertSession(int gameId, string globalId, string device, string startedUtc,
        int duration, string processName = "foo.exe")
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>("""
            INSERT INTO sessions (game_id, pid, process_name, start_time, end_time, last_heartbeat,
                                  duration_seconds, is_active, global_id, device_id,
                                  started_at_utc, ended_at_utc, time_precision)
            VALUES (@gameId, 1234, @processName, '2026-10-01 10:00:00', '2026-10-01 10:10:00', '2026-10-01 10:10:00',
                    @duration, 0, @globalId, @device, @startedUtc, @endedUtc, 'exact');
            SELECT last_insert_rowid();
            """,
            new
            {
                gameId,
                globalId,
                device,
                startedUtc,
                duration,
                processName,
                endedUtc = startedUtc
            });
    }

    private void InsertDaily(int gameId, string date, int seconds)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
            VALUES (@date, @gameId, @seconds, @minutes, 1, 'pending');
            """, new { date, gameId, seconds, minutes = seconds / 60 });
    }

    private static SyncChange RemoteSession(string globalId, string gameGlobalId, string deviceId,
        string startedUtc, int duration, SyncOperation operation = SyncOperation.Create)
        => new()
        {
            ChangeId = "chg-" + Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Session),
            EntityGlobalId = globalId,
            Operation = operation,
            Payload = SyncPayloads.Serialize(new SessionSyncPayload
            {
                GlobalId = globalId,
                GameGlobalId = gameGlobalId,
                DeviceId = deviceId,
                ProcessName = "foo.exe",
                StartedAtUtc = startedUtc,
                EndedAtUtc = startedUtc,
                DurationSeconds = duration,
                TimePrecision = TimePrecision.Exact
            }),
            DeviceId = deviceId,
            CreatedAtUtc = "2026-10-03T00:00:00Z"
        };

    // ==================== 1. 同 global_id 严格幂等 ====================

    [Fact]
    public async Task SameGlobalId_IsStrictlyIdempotent_AndWritesNothing()
    {
        var gameId = InsertGame("Foo", "game-G");
        InsertSession(gameId, "s-1", "device-A", "2026-10-01T10:00:00Z", 600);

        var outcome = await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-1", "game-G", "device-A", "2026-10-01T10:00:00Z", 600));

        outcome.Result.Should().Be(SessionReconcileResult.AlreadyKnown);
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sync_tombstones;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sync_queue;").Should().Be(0);
    }

    [Fact]
    public async Task SameGlobalId_WithDifferentFacts_IsBlocked_AndDoesNotRewriteHistory()
    {
        var gameId = InsertGame("Foo", "game-G");
        var sessionId = InsertSession(gameId, "s-1", "device-A", "2026-10-01T10:00:00Z", 600);

        // 同一个 identity 却报了 900 秒 → 拒绝改写历史
        var outcome = await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-1", "game-G", "device-A", "2026-10-01T10:00:00Z", 900));

        outcome.Result.Should().Be(SessionReconcileResult.Blocked);
        outcome.Reason.Should().Be(SessionIdentityDecisionReasons.ImmutableFactMismatch);

        // ⑦ 已完成的 session 的 duration 绝不能被改
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE id = @id;", new { id = sessionId })
            .Should().Be(600);
    }

    // ============ 2/5/6/7. 不同 global_id + 四项一致 → 只产生 supersession ============

    [Fact]
    public async Task EquivalentFact_ProducesSupersessionOnly_NothingElseIsTouched()
    {
        var gameId = InsertGame("Foo", "game-G");
        var sessionId = InsertSession(gameId, "s-local", "device-A", "2026-10-01T10:00:00Z", 600);
        InsertDaily(gameId, "2026-10-01", 600);

        var before = ReadSessionRow(sessionId);

        // 另一台设备对**同一局**（同 device + 同起止 + 同时长 + 同 game）记下了另一个 identity
        var outcome = await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-remote", "game-G", "device-A", "2026-10-01T10:00:00Z", 600));

        outcome.Result.Should().Be(SessionReconcileResult.Converged);
        outcome.CanonicalLocalId.Should().Be(sessionId);
        outcome.CanonicalGlobalId.Should().Be("s-local");

        // ② 只产生一条 supersession
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(1);
        Scalar<string>("SELECT superseded_global_id FROM sync_identity_supersessions;").Should().Be("s-remote");
        Scalar<string>("SELECT canonical_global_id FROM sync_identity_supersessions;").Should().Be("s-local");
        Scalar<string>("SELECT entity_type FROM sync_identity_supersessions;").Should().Be(nameof(SyncEntityType.Session));

        // ⑤ 没有 session 墓碑
        Scalar<int>("SELECT COUNT(*) FROM sync_tombstones;").Should().Be(0);

        // ⑥ 没有 DELETE 本地 canonical session
        Scalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(1);
        Scalar<int>("SELECT COUNT(*) FROM sessions WHERE id = @id;", new { id = sessionId }).Should().Be(1);

        // ⑦ 完成态事实逐字未变（duration / ended_at 等）
        ReadSessionRow(sessionId).Should().Be(before, "收敛不得改写任何历史事实");

        // ⑪ daily_summary 是本地派生数据，绝不参与跨设备竞争裁决
        Scalar<int>("SELECT duration_seconds FROM daily_summary;").Should().Be(600);
        Scalar<int>("SELECT COUNT(*) FROM daily_summary;").Should().Be(1);

        // ⑫ 不碰 2d 的原子路径 / 不产生任何待发变更
        Scalar<int>("SELECT COUNT(*) FROM sync_queue;").Should().Be(0);
    }

    private string ReadSessionRow(int sessionId)
    {
        using var conn = Open();
        return conn.QuerySingle<string>("""
            SELECT COALESCE(global_id,'') || '|' || COALESCE(device_id,'') || '|' ||
                   COALESCE(started_at_utc,'') || '|' || duration_seconds || '|' ||
                   COALESCE(ended_at_utc,'') || '|' || COALESCE(process_name,'')
            FROM sessions WHERE id = @id;
            """, new { id = sessionId });
    }

    // ============ 3/4. 任一项不一致 → Deferred；不得有隐藏裁决 ============

    [Theory]
    [InlineData(3700, "game-G", "duration 更大（不得取最大）")]
    [InlineData(300, "game-G", "duration 更小（不得取最新）")]
    [InlineData(600, "game-OTHER", "game identity 不同")]
    public async Task ConfirmedAsSuspectButFactMismatch_IsDeferred_AndNothingIsWritten(
        int duration, string gameGlobalId, string because)
    {
        var gameId = InsertGame("Foo", "game-G");
        InsertSession(gameId, "s-local", "device-A", "2026-10-01T10:00:00Z", 600);

        // device + started 相同（= 已确认是"疑似同一条事实"），但 duration / game 不同
        var outcome = await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-remote", gameGlobalId, "device-A", "2026-10-01T10:00:00Z", duration));

        outcome.Result.Should().Be(SessionReconcileResult.Deferred, because);
        outcome.Reason.Should().Be(SessionIdentityDecisionReasons.ImmutableFactMismatch, because);

        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0, "延后不得写任何记录");
        Scalar<int>("SELECT COUNT(*) FROM sync_tombstones;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(1);
        Scalar<int>("SELECT duration_seconds FROM sessions;").Should().Be(600, "绝不能被远端值覆盖");
    }

    [Fact]
    public async Task DifferentDeviceOrStart_IsNotEvenACandidate_SoItAppliesNormally()
    {
        // device_id / started_at_utc 是**候选筛选键**（一条会话的时空坐标）：
        // 不匹配 ⇒ 那是**另一条历史事实** ⇒ 交给正常落地路径（NoLocalCandidate），而不是"延后"。
        //
        // ⚠️ 若这里判成 Deferred，远端**每一条新会话**都会被无限延后 → 远端时长永远落不了地。
        //    这正是本步骤最容易写错的地方。
        var gameId = InsertGame("Foo", "game-G");
        InsertSession(gameId, "s-local", "device-A", "2026-10-01T10:00:00Z", 600);

        var otherDevice = await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-remote", "game-G", "device-B", "2026-10-01T10:00:00Z", 600));
        otherDevice.Result.Should().Be(SessionReconcileResult.NoLocalCandidate, "另一台设备的会话是另一条事实");

        var otherStart = await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-remote-2", "game-G", "device-A", "2026-10-01T11:00:00Z", 600));
        otherStart.Result.Should().Be(SessionReconcileResult.NoLocalCandidate, "同一设备的另一时刻也是另一条事实");

        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(1);
    }

    // ==================== 8/9. 重复收到幂等 / 跨设备收敛 ====================

    [Fact]
    public async Task RepeatedReceipt_IsIdempotent_AndCrossDeviceSessionConverges()
    {
        var gameId = InsertGame("Foo", "game-G");
        InsertSession(gameId, "s-deviceA", "device-A", "2026-10-01T10:00:00Z", 600);

        // 设备 B 对同一局记下了另一个 identity
        var change = RemoteSession("s-deviceB", "game-G", "device-A", "2026-10-01T10:00:00Z", 600);

        (await _repo.ReconcileRemoteSessionAsync(change)).Result.Should().Be(SessionReconcileResult.Converged);
        (await _repo.ReconcileRemoteSessionAsync(change)).Result.Should().Be(SessionReconcileResult.AlreadyKnown);
        (await _repo.ReconcileRemoteSessionAsync(change)).Result.Should().Be(SessionReconcileResult.AlreadyKnown);

        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(1, "重复收敛不得堆积");
        Scalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(1);
    }

    // ==================== 10. Deferred 台账保存完整远端 session 快照 ====================

    [Fact]
    public async Task DeferredSessionLedger_KeepsFullRemoteSessionSnapshot()
    {
        // 远端会话引用了一个本地还没有的游戏 → 2c 的落地路径会延后它，并写入台账
        var repo = _repo;
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        mock.SeedRemoteChange(nameof(SyncEntityType.Session), "s-orphan", SyncOperation.Create,
            SyncPayloads.Serialize(new SessionSyncPayload
            {
                GlobalId = "s-orphan",
                GameGlobalId = "game-missing",
                DeviceId = "device-B",
                ProcessName = "foo.exe",
                StartedAtUtc = "2026-10-01T10:00:00Z",
                EndedAtUtc = "2026-10-01T10:10:00Z",
                DurationSeconds = 600,
                TimePrecision = TimePrecision.Exact
            }), deviceId: "device-B");

        (await engine.RunOnceAsync()).Deferred.Should().Be(1);

        // 完全离线：只读台账即可重建四项不可变事实
        using var conn = Open();
        var row = conn.QuerySingle<DeferredSyncChange>(
            "SELECT * FROM sync_deferred_changes WHERE entity_global_id = 's-orphan';");

        row.Payload.Should().NotBeNullOrWhiteSpace();
        row.DeviceId.Should().Be("device-B");
        SyncPayloads.TryParseSession(row.Payload, out var facts, out _, out _).Should().BeTrue();
        facts!.DeviceId.Should().Be("device-B");
        facts.StartedAtUtc.Should().Be("2026-10-01T10:00:00Z");
        facts.DurationSeconds.Should().Be(600);
        facts.GameGlobalId.Should().Be("game-missing");
    }

    // ==================== 删除类变更不归对账管 ====================

    [Fact]
    public async Task DeleteChange_IsNotHandledByIdentityReconciliation()
    {
        var gameId = InsertGame("Foo", "game-G");
        InsertSession(gameId, "s-local", "device-A", "2026-10-01T10:00:00Z", 600);

        var outcome = await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-local", "game-G", "device-A", "2026-10-01T10:00:00Z", 600, SyncOperation.Delete));

        outcome.Result.Should().Be(SessionReconcileResult.Deferred);
        Scalar<int>("SELECT COUNT(*) FROM sync_tombstones;").Should().Be(0);
    }

    // ==================== 无候选：不插入、不删行 ====================

    [Fact]
    public async Task NoLocalCandidate_WritesNothing()
    {
        var outcome = await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-remote", "game-G", "device-A", "2026-10-01T10:00:00Z", 600));

        outcome.Result.Should().Be(SessionReconcileResult.NoLocalCandidate);
        Scalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(0, "本步骤不负责插入远端会话");
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
    }
}
