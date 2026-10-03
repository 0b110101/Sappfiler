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
/// 2f 验收：<b>Backend 通用契约套件</b>。
///
/// 目的：把"Backend 必须满足什么"从**具体实现**里剥出来 —— 任何
/// <see cref="ISyncBackend"/> 实现（当前是 Mock；将来是 Cloudflare / WebDAV）
/// 都必须通过这同一套用例，而不是各自重新解释协议。
///
/// <b>两层契约</b>：
///   · L1 后端自身：Push 幂等、墓碑优先、游标有序不回退、批量上限、错误用异常表达；
///   · L2 引擎 + 仓储：只有收到 ACK 才算完成、失败保持 Pending、Deferred ≠ Completed、
///     数据与游标同事务、未知 payload 版本不得猜。
///
/// ⚠️ <b>边界（2f 冻结）</b>：本套件是 <b>test-only</b>。
/// 生产层（src/）不得引用它，它也不得引用任何生产层的测试专用类型 ——
/// 新 Backend 的实现只需实现下面的 fixture 接口即可复用全部用例。
///
/// ⚠️ fixture 里的"服务端播种 / 观测"之所以要抽象：它们是**服务端侧**的东西，
/// 各家实现方式不同（Mock 是内存集合、Cloudflare 是 D1）。契约只要求
/// "能造出该状态、能查该状态"，不要求用同一种手段。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public abstract class DeviceSyncBackendContract : IDisposable
{
    private readonly List<string> _dbPaths = new();

    // ==================== fixture：由具体 Backend 的测试类实现 ====================

    /// <summary>造一个后端实例。<paramref name="backendId"/> 不同 = 不同后端（用于游标隔离）。</summary>
    protected abstract ISyncBackend CreateBackend(string backendId = "mock");

    /// <summary>
    /// 直接往"服务端 change feed"里放一条变更 —— 等价于"另一台设备更早推成功过"。
    /// **刻意绕过服务端校验**：只有这样才构造得出"陈旧设备在墓碑之前已推送"这类历史状态。
    /// </summary>
    protected abstract Task SeedServerChangeAsync(ISyncBackend backend, SyncChange change);

    /// <summary>服务端侧观测：该实体当前是否存在于 change feed 里。</summary>
    protected abstract bool ServerHasEntity(ISyncBackend backend, string entityType, string globalId);

    /// <summary>服务端侧观测：该实体是否有墓碑。</summary>
    protected abstract bool ServerHasTombstone(ISyncBackend backend, string entityType, string globalId);

    /// <summary>服务端侧观测：某类实体**去重后**的数量（用来证明"没有产生重复数据"）。</summary>
    protected abstract int ServerDistinctEntityCount(ISyncBackend backend, string entityType);

    // ==================== 通用工具 ====================

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
        var path = Path.Combine(Path.GetTempPath(), $"sappfiler_contract_{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        return path;
    }

    protected (SqliteRepository Repo, string Path) NewRepo()
    {
        var path = NewDbPath();
        return (new SqliteRepository(path), path);
    }

    protected static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    protected SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        conn.Open();
        return conn;
    }

    protected T Scalar<T>(string dbPath, string sql, object? param = null)
    {
        using var conn = Open(dbPath);
        return conn.ExecuteScalar<T>(sql, param)!;
    }

    protected void Exec(string dbPath, string sql, object? param = null)
    {
        using var conn = Open(dbPath);
        conn.Execute(sql, param);
    }

    protected static SyncChange RemoteChange(string entityType, string globalId, SyncOperation operation,
        string payload, string deviceId = "other-device")
        => new()
        {
            ChangeId = "seed-" + Guid.NewGuid().ToString("N"),
            EntityType = entityType,
            EntityGlobalId = globalId,
            Operation = operation,
            BaseVersion = 1,
            Payload = payload,
            DeviceId = deviceId,
            CreatedAtUtc = NowIso()
        };

    /// <summary>本地待发队列里放一条 Game 变更（模拟本机真实同步场景）。</summary>
    protected async Task<string> EnqueueGameAsync(SqliteRepository repo, string globalId, string platformId,
        string name = "RemoteGame")
    {
        var queueId = Guid.NewGuid().ToString("N");
        await repo.EnqueueOutboxAsync(new SyncQueueItem
        {
            QueueId = queueId,
            EntityType = nameof(SyncEntityType.Game),
            EntityGlobalId = globalId,
            Operation = SyncOperation.Create,
            BaseVersion = 1,
            Payload = GamePayload(globalId, platformId, name),
            CreatedAtUtc = NowIso(),
            Status = SyncQueueStatus.Pending
        });

        return queueId;
    }

    protected static string GamePayload(string globalId, string platformId, string name = "RemoteGame")
        => SyncPayloads.Serialize(new GameSyncPayload
        {
            GlobalId = globalId,
            Name = name,
            Platform = "manual",
            PlatformId = platformId,
            Executable = name + ".exe",
            ExecutablePath = @"E:\games\" + name + ".exe"
        });

    protected static string SessionPayload(string globalId, string gameGlobalId, int durationSeconds = 600)
        => SyncPayloads.Serialize(new SessionSyncPayload
        {
            GlobalId = globalId,
            GameGlobalId = gameGlobalId,
            DeviceId = "other-device",
            ProcessName = "RemoteGame.exe",
            StartedAtUtc = "2026-10-01T10:00:00Z",
            EndedAtUtc = "2026-10-01T10:10:00Z",
            DurationSeconds = durationSeconds,
            TimePrecision = TimePrecision.Exact
        });

    // ==================== L1：后端自身契约 ====================

    [Fact]
    public async Task Backend_SameChangeId_IsIdempotent()
    {
        var backend = CreateBackend();
        var change = RemoteChange(nameof(SyncEntityType.Game), "g-idem", SyncOperation.Create,
            GamePayload("g-idem", "pid-idem"));

        // 直接打后端公开 API（不经引擎）：ACK 丢失时客户端**一定会重推**同一条变更。
        var first = await backend.PushAsync(new[] { change });
        var second = await backend.PushAsync(new[] { change });

        first.AcceptedChangeIds.Should().Contain(change.ChangeId);
        second.AcceptedChangeIds.Should().Contain(change.ChangeId, "重复推送必须被确认，否则客户端永远收敛不了");
        ServerDistinctEntityCount(backend, nameof(SyncEntityType.Game)).Should().Be(1,
            "幂等键的全部意义就是：重推不产生第二份数据");
    }

    [Fact]
    public async Task Backend_TombstoneFirst_RefusesResurrectFromStaleClient()
    {
        var backend = CreateBackend();

        // 通过公开 API 造出墓碑（先删）—— 契约套件不做任何 backend-specific 播种
        var delete = RemoteChange(nameof(SyncEntityType.Game), "g-tomb", SyncOperation.Delete, "{}");
        (await backend.PushAsync(new[] { delete })).AcceptedChangeIds.Should().Contain(delete.ChangeId);
        ServerHasTombstone(backend, nameof(SyncEntityType.Game), "g-tomb").Should().BeTrue();

        // 陈旧客户端接着推一条更早的 Create → 必须**永久**拒绝
        var create = RemoteChange(nameof(SyncEntityType.Game), "g-tomb", SyncOperation.Create,
            GamePayload("g-tomb", "pid-tomb"));
        var outcome = await backend.PushAsync(new[] { create });

        outcome.AcceptedChangeIds.Should().NotContain(create.ChangeId);
        outcome.Rejected.Should().ContainSingle();
        outcome.Rejected[0].ChangeId.Should().Be(create.ChangeId);
        outcome.Rejected[0].Permanent.Should().BeTrue("墓碑是终态，重试没有任何意义，必须明确表达");
        ServerHasEntity(backend, nameof(SyncEntityType.Game), "g-tomb").Should().BeFalse("绝不能复活已删除实体");
    }

    [Fact]
    public async Task Backend_PullHonoursLimit_AndCursorNeverRegresses()
    {
        var backend = CreateBackend();
        var total = SyncOutboxPolicy.PullBatchSize + 5;

        for (var i = 0; i < total; i++)
        {
            await SeedServerChangeAsync(backend,
                RemoteChange(nameof(SyncEntityType.Game), $"g-lim-{i}", SyncOperation.Create,
                    GamePayload($"g-lim-{i}", $"pid-lim-{i}")));
        }

        var first = await backend.PullAsync(null, SyncOutboxPolicy.PullBatchSize);
        first.Changes.Should().HaveCount(SyncOutboxPolicy.PullBatchSize, "一次 Pull 不得超过 limit");

        var second = await backend.PullAsync(first.NextCursor, SyncOutboxPolicy.PullBatchSize);
        second.Changes.Should().HaveCount(5, "剩下的下一批继续给");

        var after = await backend.PullAsync(second.NextCursor, SyncOutboxPolicy.PullBatchSize);
        after.Changes.Should().BeEmpty("没有新变更时返回空，游标保持在高水位、绝不回退");
    }

    // ==================== L2：引擎 + 仓储契约 ====================

    [Fact]
    public async Task Push_WhenBackendAcks_MarksCompleted()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        var queueId = await EnqueueGameAsync(repo, "g-1", "pid-1");

        var result = await engine.RunOnceAsync();

        result.Completed.Should().Be(1);
        result.PushFailed.Should().Be(0);
        result.Error.Should().BeNull();

        ServerDistinctEntityCount(backend, nameof(SyncEntityType.Game)).Should().Be(1);
        Scalar<string>(db, "SELECT status FROM sync_queue WHERE queue_id = @id;", new { id = queueId })
            .Should().Be("2", "收到 ACK 必须置为 Completed");
    }

    [Fact]
    public async Task Push_WhenProcessDies_InFlightIsRecoveredOnNextStart()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        await EnqueueGameAsync(repo, "g-crash", "pid-crash");

        // 模拟"推送进行中进程被杀"：状态停在 InFlight
        Exec(db, "UPDATE sync_queue SET status = 1;");

        var beforeRestart = await engine.RunOnceAsync();
        beforeRestart.Completed.Should().Be(0);
        ServerDistinctEntityCount(backend, nameof(SyncEntityType.Game)).Should().Be(0,
            "InFlight 的记录不该被重复推送出去");
        Scalar<string>(db, "SELECT status FROM sync_queue WHERE queue_id IS NOT NULL LIMIT 1;")
            .Should().Be("1", "没复位之前必须原地不动");

        await engine.StartAsync();   // 下次启动的复位

        var afterRestart = await engine.RunOnceAsync();
        afterRestart.Completed.Should().Be(1, "否则这条变更永远发不出去");
        ServerDistinctEntityCount(backend, nameof(SyncEntityType.Game)).Should().Be(1);
    }

    [Fact]
    public async Task Push_RespectsPolicyBatchSize()
    {
        var (repo, _) = NewRepo();
        var engine = new DeviceSyncEngine(repo, CreateBackend());

        for (var i = 0; i < SyncOutboxPolicy.PushBatchSize + 10; i++)
        {
            await EnqueueGameAsync(repo, $"g-batch-{i}", $"pid-batch-{i}");
        }

        var first = await engine.RunOnceAsync();
        first.Completed.Should().Be(SyncOutboxPolicy.PushBatchSize, "一批不超过策略上限");

        var second = await engine.RunOnceAsync();
        second.Completed.Should().Be(10, "剩下的下一批继续推");
    }

    [Fact]
    public async Task Pull_AppliesDataAndAdvancesCursorInSameRun()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Game), "g-remote", SyncOperation.Create,
                GamePayload("g-remote", "pid-remote")));

        var result = await engine.RunOnceAsync();

        result.Pulled.Should().Be(1);
        result.Applied.Should().Be(1);
        result.CursorAdvanced.Should().BeTrue();

        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-remote';").Should().Be(1);
        Scalar<string>(db, "SELECT cursor FROM sync_state WHERE backend = @b;", new { b = backend.BackendId })
            .Should().NotBeNullOrEmpty("落地与游标必须一起提交");
    }

    [Fact]
    public async Task Pull_WhenApplyFailsMidway_RollsBackDataAndCursorTogether()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        // 同一批里两条：Game（先） + Session（后）。让 Session 的 INSERT 必定失败。
        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Game), "g-tx", SyncOperation.Create, GamePayload("g-tx", "pid-tx")));
        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Session), "s-tx", SyncOperation.Create, SessionPayload("s-tx", "g-tx")));

        Exec(db, """
            CREATE TRIGGER abort_session_insert BEFORE INSERT ON sessions
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        var result = await engine.RunOnceAsync();

        result.Error.Should().NotBeNull("引擎必须捕获而不是抛出");
        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-tx';")
            .Should().Be(0, "同一事务里的 Game 也必须回滚");
        Scalar<int>(db, "SELECT COUNT(*) FROM sync_state;")
            .Should().Be(0, "游标绝不能先于数据推进，否则会永久丢数据");
    }

    [Fact]
    public async Task Pull_Repeatedly_DoesNotCreateDuplicateEntities()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Game), "g-dup", SyncOperation.Create, GamePayload("g-dup", "pid-dup")));

        (await engine.RunOnceAsync()).Applied.Should().Be(1);

        // 把游标倒回去 = 服务端重放同一批（崩溃后重拉就是这个效果）
        Exec(db, "UPDATE sync_state SET cursor = '0';");

        var replay = await engine.RunOnceAsync();
        replay.Applied.Should().Be(0, "已存在就不该再插一次");
        replay.Deferred.Should().Be(1);
        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-dup';").Should().Be(1);
    }

    [Fact]
    public async Task Pull_TwoDevices_EachEventuallyReceivesTheOthersData()
    {
        var (repoA, deviceA) = NewRepo();
        var (repoB, deviceB) = NewRepo();

        var backend = CreateBackend();     // 同一个"服务端"
        var engineA = new DeviceSyncEngine(repoA, backend);
        var engineB = new DeviceSyncEngine(repoB, backend);

        await EnqueueGameAsync(repoA, "g-device-a", "pid-a", "OnlyOnA");
        await engineA.RunOnceAsync();

        await EnqueueGameAsync(repoB, "g-device-b", "pid-b", "OnlyOnB");
        await engineB.RunOnceAsync();

        // 各自再跑一轮把对方的数据拉回来
        await engineA.RunOnceAsync();
        await engineB.RunOnceAsync();

        Scalar<int>(deviceA, "SELECT COUNT(*) FROM games WHERE global_id = 'g-device-b';")
            .Should().Be(1, "A 最终要拿到 B 的数据");
        Scalar<int>(deviceB, "SELECT COUNT(*) FROM games WHERE global_id = 'g-device-a';")
            .Should().Be(1, "B 最终要拿到 A 的数据");
        ServerDistinctEntityCount(backend, nameof(SyncEntityType.Game)).Should().Be(2, "服务端也只有两份，没有重复");
    }

    [Fact]
    public async Task Pull_EmptyResult_StillTracksServerWatermark()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        var empty = await engine.RunOnceAsync();
        empty.Pulled.Should().Be(0);
        Scalar<int>(db, "SELECT COUNT(*) FROM sync_state WHERE backend = @b;", new { b = backend.BackendId })
            .Should().Be(1, "空拉取也要把服务端高水位记下来");

        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Game), "g-after-empty", SyncOperation.Create,
                GamePayload("g-after-empty", "pid-after-empty")));

        (await engine.RunOnceAsync()).Applied.Should().Be(1, "之后的变更要能被拉到");
    }

    [Fact]
    public async Task Pull_RespectsPolicyBatchSize()
    {
        var (repo, _) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        var total = SyncOutboxPolicy.PullBatchSize + 10;
        for (var i = 0; i < total; i++)
        {
            await SeedServerChangeAsync(backend,
                RemoteChange(nameof(SyncEntityType.Game), $"g-many-{i}", SyncOperation.Create,
                    GamePayload($"g-many-{i}", $"pid-many-{i}")));
        }

        var first = await engine.RunOnceAsync();
        first.Pulled.Should().Be(SyncOutboxPolicy.PullBatchSize, "一批不超过策略上限");
        first.Applied.Should().Be(SyncOutboxPolicy.PullBatchSize);

        var second = await engine.RunOnceAsync();
        second.Applied.Should().Be(10, "剩下的下一批继续拉");
    }

    [Fact]
    public async Task Tombstone_FromRemote_IsPropagatedLocally()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Game), "g-deleted", SyncOperation.Delete, "{}", "device-x"));

        var result = await engine.RunOnceAsync();

        result.TombstonesPropagated.Should().Be(1);
        Scalar<int>(db, "SELECT COUNT(*) FROM sync_tombstones WHERE entity_global_id = 'g-deleted';")
            .Should().Be(1);
        Scalar<string>(db, "SELECT device_id FROM sync_tombstones WHERE entity_global_id = 'g-deleted';")
            .Should().Be("device-x", "墓碑要记住是谁删的");
    }

    [Fact]
    public async Task Tombstone_StaleDevice_CannotResurrectDeletedEntity()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        // 云端已有墓碑
        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Game), "g-gone", SyncOperation.Delete, "{}", "device-x"));
        await engine.RunOnceAsync();

        // ① 本地还想推一条旧的 Create → 服务端必须永久拒绝 → 死信，不再无限重试
        var queueId = await EnqueueGameAsync(repo, "g-gone", "pid-gone");
        var push = await engine.RunOnceAsync();

        push.PushRejected.Should().Be(1);
        Scalar<string>(db, "SELECT status FROM sync_queue WHERE queue_id = @id;", new { id = queueId })
            .Should().Be("3", "永久拒绝要进死信，不能一直重试");
        ServerHasEntity(backend, nameof(SyncEntityType.Game), "g-gone").Should().BeFalse("绝不能复活");

        // ② 另一台落后设备推上来的旧数据（在墓碑之前就已存在于 feed），同样不能复活
        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Game), "g-gone", SyncOperation.Create,
                GamePayload("g-gone", "pid-gone"), "stale-device"));
        var pull = await engine.RunOnceAsync();

        pull.Applied.Should().Be(0);
        pull.Deferred.Should().BeGreaterThan(0);
        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-gone';")
            .Should().Be(0, "墓碑优先：陈旧数据不允许把已删除实体拉回本地");
    }

    [Fact]
    public async Task Cursor_IsIsolatedPerBackend()
    {
        var (repo, db) = NewRepo();
        var backendA = CreateBackend("cloudflare");
        var backendB = CreateBackend("webdav");

        await SeedServerChangeAsync(backendA,
            RemoteChange(nameof(SyncEntityType.Game), "g-cf", SyncOperation.Create, GamePayload("g-cf", "pid-cf")));

        await new DeviceSyncEngine(repo, backendA).RunOnceAsync();
        await new DeviceSyncEngine(repo, backendB).RunOnceAsync();

        Scalar<int>(db, "SELECT COUNT(*) FROM sync_state;").Should().Be(2, "每个 backend 一条独立游标");

        // 关键：B 绝不能复用 A 的游标 —— 给 B 塞一条，B 必须能拉到
        await SeedServerChangeAsync(backendB,
            RemoteChange(nameof(SyncEntityType.Game), "g-wd", SyncOperation.Create, GamePayload("g-wd", "pid-wd")));

        (await new DeviceSyncEngine(repo, backendB).RunOnceAsync()).Applied.Should().Be(1,
            "换后端绝不复用另一个后端的游标");
    }

    [Fact]
    public async Task Pull_UnknownSchemaVersion_IsDeferredInsteadOfMisparsed()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var engine = new DeviceSyncEngine(repo, backend);

        var futureJson = """{"schema_version":99,"global_id":"g-future","name":"X","platform":"manual","platform_id":"pid-future"}""";
        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Game), "g-future", SyncOperation.Create, futureJson));

        var result = await engine.RunOnceAsync();

        result.Applied.Should().Be(0);
        result.Deferred.Should().Be(1, "不认识的 schema 宁可不同步，也不能按旧结构硬解");
        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-future';").Should().Be(0);
    }
}

/// <summary>
/// 用内存 Mock 后端跑上面那套契约。Mock 专属的**故障注入**用例仍留在
/// <see cref="DeviceSyncEngineTests"/>（那里是 Mock 的实现细节，不是通用契约）。
/// </summary>
public sealed class DeviceSyncMockBackendContractTests : DeviceSyncBackendContract
{
    private readonly Dictionary<ISyncBackend, MockSyncBackend> _mocks = new();

    protected override ISyncBackend CreateBackend(string backendId = "mock")
    {
        var mock = new MockSyncBackend(backendId);
        _mocks[mock] = mock;
        return mock;
    }

    private MockSyncBackend Mock(ISyncBackend backend) => _mocks[backend];

    protected override Task SeedServerChangeAsync(ISyncBackend backend, SyncChange change)
    {
        Mock(backend).SeedRemoteChange(change.EntityType, change.EntityGlobalId, change.Operation,
            change.Payload, string.IsNullOrWhiteSpace(change.DeviceId) ? "other-device" : change.DeviceId);
        return Task.CompletedTask;
    }

    protected override bool ServerHasEntity(ISyncBackend backend, string entityType, string globalId)
        => Mock(backend).HasEntity(entityType, globalId);

    protected override bool ServerHasTombstone(ISyncBackend backend, string entityType, string globalId)
        => Mock(backend).HasTombstone(entityType, globalId);

    protected override int ServerDistinctEntityCount(ISyncBackend backend, string entityType)
        => Mock(backend).DistinctEntityCount(entityType);

    /// <summary>
    /// Mock 实现细节的观测点（通用契约表达不了，但确实是 Mock 契约的一部分）：
    /// 游标 = 服务端序号、批量大小透传、幂等抑制计数。
    /// </summary>
    [Fact]
    public async Task Mock_CursorIsServerSequenceNumber_AndBatchSizesFollowPolicy()
    {
        var (repo, db) = NewRepo();
        var backend = CreateBackend();
        var mock = Mock(backend);
        var engine = new DeviceSyncEngine(repo, backend);

        await SeedServerChangeAsync(backend,
            RemoteChange(nameof(SyncEntityType.Game), "g-mock-cursor", SyncOperation.Create,
                GamePayload("g-mock-cursor", "pid-mock-cursor")));

        (await engine.RunOnceAsync()).Applied.Should().Be(1);
        Scalar<string>(db, "SELECT cursor FROM sync_state WHERE backend = @b;", new { b = backend.BackendId })
            .Should().Be("1", "Mock 的游标就是服务端序号（下标 + 1）");

        // 另一个后端：空拉取也要写自己的高水位，且绝不复用前一个后端的值
        var another = CreateBackend("webdav");
        await new DeviceSyncEngine(repo, another).RunOnceAsync();
        Scalar<string>(db, "SELECT cursor FROM sync_state WHERE backend = 'webdav';")
            .Should().Be("0", "换后端绝不复用另一个后端的游标");

        // 批量大小：引擎必须把策略上限透传给后端
        mock.PullBatchSizes.Should().OnlyContain(v => v == SyncOutboxPolicy.PullBatchSize);

        for (var i = 0; i < SyncOutboxPolicy.PushBatchSize + 1; i++)
        {
            await EnqueueGameAsync(repo, $"g-mock-batch-{i}", $"pid-mock-batch-{i}");
        }

        await engine.RunOnceAsync();
        mock.PushBatchSizes.Should().ContainSingle();
        mock.PushBatchSizes[0].Should().Be(SyncOutboxPolicy.PushBatchSize, "推送批也不得超过策略上限");
    }
}

/// <summary>
/// 2f 的**契约面护栏**：把"已冻结的形状与版本语义"钉住，防止以后有人
/// 为了"设计完整"往 Backend outcome 里塞 Deferred、或给接口加 GetStatus/TestConnection。
/// </summary>
public class DeviceSyncContractSurfaceTests
{
    private static readonly Type[] BackendDtos =
    {
        typeof(SyncChange), typeof(PushOutcome), typeof(PushRejection), typeof(PullOutcome)
    };

    [Fact]
    public void Contract_ISyncBackendShape_IsFrozen()
    {
        var iface = typeof(ISyncBackend);

        iface.GetProperties().Select(p => p.Name).Should().BeEquivalentTo(new[] { "BackendId" });
        iface.GetMethods().Where(m => !m.IsSpecialName).Select(m => m.Name)
            .Should().BeEquivalentTo(new[] { "PushAsync", "PullAsync" },
                "2f 冻结：不增加 GetStatusAsync / TestConnectionAsync，也不引入 Request/Response DTO");
    }

    [Fact]
    public void Contract_BackendDtoFieldSets_AreFrozen()
    {
        typeof(SyncChange).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "ChangeId", "EntityType", "EntityGlobalId", "Operation",
            "BaseVersion", "Payload", "DeviceId", "CreatedAtUtc"
        });

        typeof(PushOutcome).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "AcceptedChangeIds", "Rejected"
        });

        typeof(PushRejection).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "ChangeId", "Reason", "Permanent"
        });

        typeof(PullOutcome).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "Changes", "NextCursor", "HasMore"
        });
    }

    [Fact]
    public void Contract_BackendNeverExposesClientSideOutcomes()
    {
        // Backend 只有 accepted / rejected / transport failure；
        // Deferred 与身份/冲突判定是**客户端**状态，绝不能出现在 Backend 契约里，
        // 否则服务端就被迫理解 strong_key_conflict / local_ignored_protected / immutable_fact_mismatch。
        var forbidden = new[] { "defer", "blocked", "supersed", "resolution", "ignored", "conflict" };

        var memberNames = BackendDtos.SelectMany(t => t.GetProperties().Select(p => p.Name))
            .Concat(typeof(ISyncBackend).GetMethods().Select(m => m.Name))
            .ToList();

        foreach (var name in memberNames)
        {
            forbidden.Should().NotContain(f => name.Contains(f, StringComparison.OrdinalIgnoreCase),
                $"「{name}」像是客户端一致性语义，不属于 Backend 契约");
        }
    }

    [Fact]
    public void Protocol_And_PayloadSchemaVersions_AreSeparateAndFrozen()
    {
        DeviceSyncConstants.CurrentProtocolVersion.Should().Be(1, "2f 首次冻结 Backend 协议 = v1");
        DeviceSyncConstants.CurrentSchemaVersion.Should().Be(1, "payload schema 版本沿用现有体系");

        new GameSyncPayload().SchemaVersion.Should().Be(DeviceSyncConstants.CurrentSchemaVersion);
        new SessionSyncPayload().SchemaVersion.Should().Be(DeviceSyncConstants.CurrentSchemaVersion);
    }

    [Fact]
    public void Payload_UnknownFields_AreIgnored_AndKnownFieldsSurvive()
    {
        var json = """{"schema_version":1,"global_id":"g-x","name":"Foo","platform":"manual","platform_id":"p-1","future_field":123,"nested":{"a":1}}""";

        SyncPayloads.TryParseGame(json, out var payload, out var version, out var error).Should().BeTrue(
            "未知字段必须被忽略（前向兼容），而不是让整条变更失败");

        version.Should().Be(DeviceSyncConstants.CurrentSchemaVersion);
        error.Should().BeNull();
        payload!.GlobalId.Should().Be("g-x");
        payload.PlatformId.Should().Be("p-1");
    }

    [Fact]
    public void Payload_UnknownSchemaVersion_IsRefused_NeverGuessed()
    {
        var json = """{"schema_version":99,"global_id":"g-future","name":"X","platform":"manual","platform_id":"p-1"}""";

        SyncPayloads.TryParseGame(json, out var payload, out var version, out var error).Should().BeFalse(
            "未知版本不得猜测兼容");

        payload.Should().BeNull("绝不能按旧结构硬解");
        version.Should().Be(99, "版本号要能读出来，供 Deferred 台账留痕");
        error.Should().NotBeNull().And.Contain("99");
        SyncPayloads.TryReadSchemaVersion(json).Should().Be(99);
    }
}
