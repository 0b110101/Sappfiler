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
/// Phase 2c 冻结语义：**Deferred ≠ Completed**，且必须留下足够信息让后续阶段重新处理。
///
/// 用户要求台账必须保留：
/// change_id / entity_type / entity_global_id / reason / schema_version /
/// 首次发现时间 / 最近一次发现时间（+ 发现次数，用来判断是不是反复失败）。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public class DeviceSyncDeferredLedgerTests : IDisposable
{
    private readonly List<string> _dbPaths = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _dbPaths)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch { }
            }
        }
        GC.SuppressFinalize(this);
    }

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sappfiler_deferred_{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        return path;
    }

    private SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        conn.Open();
        return conn;
    }

    private T Scalar<T>(string dbPath, string sql, object? param = null)
    {
        using var conn = Open(dbPath);
        return conn.ExecuteScalar<T>(sql, param)!;
    }

    private void Exec(string dbPath, string sql)
    {
        using var conn = Open(dbPath);
        conn.Execute(sql);
    }

    private static string GamePayload(string globalId, string platformId, string name = "RemoteGame")
        => SyncPayloads.Serialize(new GameSyncPayload
        {
            GlobalId = globalId,
            Name = name,
            Platform = "manual",
            PlatformId = platformId,
            Executable = name + ".exe",
            ExecutablePath = @"E:\games\" + name + ".exe"
        });

    private static string SessionPayload(string globalId, string gameGlobalId)
        => SyncPayloads.Serialize(new SessionSyncPayload
        {
            GlobalId = globalId,
            GameGlobalId = gameGlobalId,
            DeviceId = "other-device",
            ProcessName = "RemoteGame.exe",
            StartedAtUtc = "2026-10-01T10:00:00Z",
            EndedAtUtc = "2026-10-01T10:10:00Z",
            DurationSeconds = 600,
            TimePrecision = TimePrecision.Exact
        });

    // ============================ 用例 ============================

    [Fact]
    public async Task Deferred_IsRecordedWithAllDiagnosticFields_AndCursorStillAdvances()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-led", SyncOperation.Create, GamePayload("g-led", "pid-led"));

        (await engine.RunOnceAsync()).Applied.Should().Be(1);

        // 游标倒回 = 服务端重放同一批（崩溃后重拉就是这个效果）→ 这次必须记为 Deferred
        Exec(db, "UPDATE sync_state SET cursor = '0';");
        var replay = await engine.RunOnceAsync();
        replay.Applied.Should().Be(0);
        replay.Deferred.Should().Be(1);
        replay.DeferredReasons.Should().Equal(new[] { DeferredReasons.AlreadyExistsLocal });

        // 台账必须留全 7 类信息
        using var conn = Open(db);
        var row = conn.QuerySingle<DeferredSyncChange>("SELECT * FROM sync_deferred_changes;");
        row.ChangeId.Should().NotBeNullOrWhiteSpace();
        row.Backend.Should().Be(mock.BackendId);
        row.EntityType.Should().Be(nameof(SyncEntityType.Game));
        row.EntityGlobalId.Should().Be("g-led");
        row.Reason.Should().Be(DeferredReasons.AlreadyExistsLocal);
        row.SchemaVersion.Should().Be(DeviceSyncConstants.CurrentSchemaVersion);
        row.FirstSeenAtUtc.Should().NotBeNullOrWhiteSpace();
        row.LastSeenAtUtc.Should().NotBeNullOrWhiteSpace();
        row.SeenCount.Should().Be(1);

        // migration 003：远端事实必须一并存下来，否则 2e 离线重处理无输入
        row.Operation.Should().Be(SyncOperation.Create);
        row.DeviceId.Should().Be("other-device", "要记住是哪台设备发来的");
        row.Payload.Should().NotBeNullOrWhiteSpace("payload 是重处理的唯一输入");
        row.ResolvedAtUtc.Should().BeNull("尚未经过 Identity Reconciliation");
        row.Resolution.Should().BeNull();

        // 游标确实越过了它 —— 这正是"必须留痕"的原因
        Scalar<string>(db, "SELECT cursor FROM sync_state WHERE backend = @b;", new { b = mock.BackendId })
            .Should().Be("1");
        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-led';").Should().Be(1);

        // 读接口可用（供诊断界面与 2e 重新处理）
        (await repo.GetDeferredChangesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Deferred_RepeatedFinding_RefreshesLastSeenButKeepsFirstSeen()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-repeat", SyncOperation.Create, GamePayload("g-repeat", "pid-repeat"));
        await engine.RunOnceAsync();

        Exec(db, "UPDATE sync_state SET cursor = '0';");
        await engine.RunOnceAsync();   // 第一次 Deferred

        // 把 first_seen 改成很久以前，验证第二次发现不会覆盖它
        Exec(db, "UPDATE sync_deferred_changes SET first_seen_at_utc = '2000-01-01T00:00:00Z';");

        Exec(db, "UPDATE sync_state SET cursor = '0';");
        await engine.RunOnceAsync();   // 第二次发现同一条变更

        using var conn = Open(db);
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sync_deferred_changes;")
            .Should().Be(1, "同一条 change_id 只能有一行（否则台账会无限膨胀）");

        var row = conn.QuerySingle<DeferredSyncChange>("SELECT * FROM sync_deferred_changes;");
        row.FirstSeenAtUtc.Should().Be("2000-01-01T00:00:00Z", "首次发现时间必须原样保留");
        row.LastSeenAtUtc.Should().NotBe("2000-01-01T00:00:00Z", "最近一次发现时间要刷新");
        row.SeenCount.Should().Be(2, "反复失败要看得出次数");
    }

    [Fact]
    public async Task Deferred_BusinessKeyTakenByOtherIdentity_IsClassified()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);

        // 本地已经有一个 (manual, pid-taken) 的游戏，identity 是 g-local
        Exec(db, """
            INSERT INTO games (platform, platform_id, name, executable, executable_path, status, global_id)
            VALUES ('manual', 'pid-taken', 'LocalGame', 'Local.exe', 'E:\l\Local.exe', 'active', 'g-local');
            """);

        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        // 另一台设备推上来同一个业务键、但 identity 不同 → 属于身份对账，2c 不猜
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-remote-same-key", SyncOperation.Create,
            GamePayload("g-remote-same-key", "pid-taken"));

        var result = await engine.RunOnceAsync();

        result.Applied.Should().Be(0);
        result.DeferredReasons.Should().Equal(new[] { DeferredReasons.BusinessKeyTakenByOtherIdentity });

        Scalar<int>(db, "SELECT COUNT(*) FROM games;").Should().Be(1, "既不覆盖、也不新建第二行");
        Scalar<string>(db, "SELECT global_id FROM games;").Should().Be("g-local", "本地 identity 绝不能被静默改掉");
    }

    [Fact]
    public async Task Deferred_TombstonedLocally_IsClassified()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-dead", SyncOperation.Delete, "{}", "device-x");
        (await engine.RunOnceAsync()).TombstonesPropagated.Should().Be(1);

        // 陈旧设备又把这条推回来 / 或拉取时重放旧的 Create → 必须被墓碑挡住
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-dead", SyncOperation.Create,
            GamePayload("g-dead", "pid-dead"), "stale-device");

        var result = await engine.RunOnceAsync();

        result.Applied.Should().Be(0);
        result.DeferredReasons.Should().Equal(new[] { DeferredReasons.TombstonedLocal });
        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-dead';").Should().Be(0);
    }

    [Fact]
    public async Task Deferred_DependencyMissing_IsClassified()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        // 只有会话、没有它引用的游戏（例如游戏那条因为别的原因延后了）
        mock.SeedRemoteChange(nameof(SyncEntityType.Session), "s-orphan", SyncOperation.Create,
            SessionPayload("s-orphan", "g-not-here"));

        var result = await engine.RunOnceAsync();

        result.Applied.Should().Be(0);
        result.DeferredReasons.Should().Equal(new[] { DeferredReasons.DependencyMissing });
        Scalar<int>(db, "SELECT COUNT(*) FROM sessions;").Should().Be(0);
    }

    [Fact]
    public async Task Deferred_UnsupportedSchemaVersion_KeepsTheVersionForDiagnosis()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        var futureJson = """{"schema_version":99,"global_id":"g-v99","name":"X","platform":"manual","platform_id":"pid-v99"}""";
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-v99", SyncOperation.Create, futureJson);

        var result = await engine.RunOnceAsync();

        result.Applied.Should().Be(0);
        result.DeferredReasons.Should().Equal(new[] { DeferredReasons.UnsupportedSchemaVersion });

        // 台账要把"到底看到了哪个版本"记下来，否则以后没法判断是客户端太旧还是服务端太新
        Scalar<int>(db, "SELECT schema_version FROM sync_deferred_changes WHERE entity_global_id = 'g-v99';")
            .Should().Be(99);
        Scalar<string>(db, "SELECT reason FROM sync_deferred_changes WHERE entity_global_id = 'g-v99';")
            .Should().Be(DeferredReasons.UnsupportedSchemaVersion);
    }

    [Fact]
    public async Task Deferred_UnsupportedEntityType_IsClassified()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        mock.SeedRemoteChange(nameof(SyncEntityType.GameMapping), "m-1", SyncOperation.Create, """{"schema_version":1}""");

        var result = await engine.RunOnceAsync();

        result.Applied.Should().Be(0);
        result.DeferredReasons.Should().Equal(new[] { DeferredReasons.UnsupportedEntityType });
    }

    [Fact]
    public async Task Deferred_StoresEnoughRemoteFacts_ToBeReprocessedOffline()
    {
        // 验收要点（用户 2026-10-03）：Deferred 必须真的保存了足够的**远端事实**，
        // 使得 2e 能在**完全离线**（不重新拉取）的前提下重处理它。
        var db = NewDbPath();
        var repo = new SqliteRepository(db);

        // 本地先占住业务键，制造"远端同键不同 identity"的冲突
        Exec(db, """
            INSERT INTO games (platform, platform_id, name, executable, executable_path, status, global_id)
            VALUES ('manual', '123456', 'Foo', 'Foo.exe', 'E:\f\Foo.exe', 'active', 'g-local');
            """);

        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        const string remoteGlobalId = "g-remote";
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), remoteGlobalId, SyncOperation.Create,
            GamePayload(remoteGlobalId, "123456", "Foo"), deviceId: "device-B");

        var result = await engine.RunOnceAsync();
        result.Deferred.Should().Be(1);
        result.DeferredReasons.Should().Equal(new[] { DeferredReasons.BusinessKeyTakenByOtherIdentity });

        // —— 从这里开始完全离线：只读本地台账 ——
        using var conn = Open(db);
        var row = conn.QuerySingle<DeferredSyncChange>(
            "SELECT * FROM sync_deferred_changes WHERE entity_global_id = @g;", new { g = remoteGlobalId });

        row.Payload.Should().NotBeNullOrWhiteSpace();
        row.DeviceId.Should().Be("device-B");
        row.Operation.Should().Be(SyncOperation.Create);

        // 关键：从台账里的 payload 就能重建远端对象的全部事实
        SyncPayloads.TryParseGame(row.Payload, out var facts, out _, out _).Should().BeTrue();
        facts!.GlobalId.Should().Be(remoteGlobalId);
        facts.Platform.Should().Be("manual");
        facts.PlatformId.Should().Be("123456", "这正是判定'强键冲突'所需的强身份键");
        facts.Name.Should().Be("Foo");
        facts.Executable.Should().Be("Foo.exe");
        facts.ExecutablePath.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Deferred_MixedBatch_RecordsEveryReason_AndNeverMarksAnythingCompleted()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        // 一条能落地 + 三条不同原因的延后
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-ok", SyncOperation.Create, GamePayload("g-ok", "pid-ok"));
        mock.SeedRemoteChange(nameof(SyncEntityType.Session), "s-orphan", SyncOperation.Create, SessionPayload("s-orphan", "g-missing"));
        mock.SeedRemoteChange(nameof(SyncEntityType.GameMapping), "m-x", SyncOperation.Create, """{"schema_version":1}""");
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-old-version", SyncOperation.Create,
            """{"schema_version":77,"global_id":"g-old-version","name":"Y","platform":"manual","platform_id":"pid-y"}""");

        var result = await engine.RunOnceAsync();

        result.Applied.Should().Be(1);
        result.Deferred.Should().Be(3);
        result.DeferredReasons.Should().BeEquivalentTo(new[]
        {
            DeferredReasons.DependencyMissing,
            DeferredReasons.UnsupportedEntityType,
            DeferredReasons.UnsupportedSchemaVersion
        });

        using var conn = Open(db);
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sync_deferred_changes;").Should().Be(3);
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM games;").Should().Be(1, "只有真的能落地的那条被写入");

        // 台账与游标同事务提交：不会出现"游标走了、台账没记上"
        conn.ExecuteScalar<string>("SELECT cursor FROM sync_state WHERE backend = @b;", new { b = mock.BackendId })
            .Should().Be("4");
    }
}
