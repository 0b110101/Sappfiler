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
/// Phase 2c 验收：同步协议本身（Push 状态机 / Pull 原子性 / 游标 / 幂等 / 退避 / 墓碑传播）。
///
/// 用的是内存 Mock 后端，**不联网**：真实 Cloudflare / WebDAV 属于后续阶段。
///
/// ⚠️ 贯穿全部用例的一条底线：**SyncEngine 出任何异常都不能影响本地计时**
/// （见 <see cref="NetworkFailure_DoesNotThrow_AndLocalTimingStillWorks"/>）。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public class DeviceSyncEngineTests : IDisposable
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

    // ============================ 工具 ============================

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sappfiler_engine_{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        return path;
    }

    private static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

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

    private static async Task<string> EnqueueGameAsync(
        SqliteRepository repo, string globalId, string platformId, string name = "RemoteGame")
    {
        var payload = SyncPayloads.Serialize(new GameSyncPayload
        {
            GlobalId = globalId,
            Name = name,
            Platform = "manual",
            PlatformId = platformId,
            Executable = name + ".exe",
            ExecutablePath = @"E:\games\" + name + ".exe"
        });

        var queueId = Guid.NewGuid().ToString("N");
        await repo.EnqueueOutboxAsync(new SyncQueueItem
        {
            QueueId = queueId,
            EntityType = nameof(SyncEntityType.Game),
            EntityGlobalId = globalId,
            Operation = SyncOperation.Create,
            BaseVersion = 1,
            Payload = payload,
            CreatedAtUtc = NowIso(),
            Status = SyncQueueStatus.Pending
        });

        return queueId;
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

    private static string SessionPayload(string globalId, string gameGlobalId, int durationSeconds = 600)
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

    // ============================ Push ============================

    [Fact]
    public async Task Push_WhenBackendAcks_MarksCompleted()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        var queueId = await EnqueueGameAsync(repo, "g-1", "pid-1");

        var result = await engine.RunOnceAsync();

        result.Completed.Should().Be(1);
        result.PushFailed.Should().Be(0);
        result.Error.Should().BeNull();

        mock.DistinctEntityCount(nameof(SyncEntityType.Game)).Should().Be(1);
        Scalar<string>(db, "SELECT status FROM sync_queue WHERE queue_id = @id;", new { id = queueId })
            .Should().Be("2", "收到 ACK 必须置为 Completed");
    }

    [Fact]
    public async Task Push_WhenAckIsLost_RetryDoesNotDuplicateData()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        var queueId = await EnqueueGameAsync(repo, "g-ack", "pid-ack");

        // 服务端已落库，但响应在回程丢了 → 客户端必须认为"失败"
        mock.DropAckOnNextPush = true;

        var first = await engine.RunOnceAsync();
        first.PushFailed.Should().Be(1);
        first.Error.Should().NotBeNull();
        mock.DistinctEntityCount(nameof(SyncEntityType.Game)).Should().Be(1, "服务端确实已经落库了");
        Scalar<string>(db, "SELECT status FROM sync_queue WHERE queue_id = @id;", new { id = queueId })
            .Should().Be("0", "客户端只能认为失败，记录必须留在待发里");

        // 退避结束后重推同一条变更（同一个 change_id）
        using (var conn = Open(db))
        {
            conn.Execute("UPDATE sync_queue SET next_retry_at_utc = '2000-01-01T00:00:00Z';");
        }

        var second = await engine.RunOnceAsync();

        second.Completed.Should().Be(1);
        mock.DuplicateSuppressedCount.Should().Be(1, "服务端必须靠 change_id 幂等去重");
        mock.DistinctEntityCount(nameof(SyncEntityType.Game)).Should().Be(1, "绝不能产生第二份数据");
        mock.Log.Count(c => c.EntityGlobalId == "g-ack").Should().Be(1);
    }

    [Fact]
    public async Task Push_WhenBackendFails_SchedulesBackoffAndDoesNotRetryImmediately()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        var queueId = await EnqueueGameAsync(repo, "g-fail", "pid-fail");
        mock.ThrowOnNextPush = true;

        var first = await engine.RunOnceAsync();
        first.PushFailed.Should().Be(1);
        first.Error.Should().NotBeNull();

        Scalar<int>(db, "SELECT retry_count FROM sync_queue WHERE queue_id = @id;", new { id = queueId })
            .Should().Be(1);
        Scalar<string>(db, "SELECT status FROM sync_queue WHERE queue_id = @id;", new { id = queueId })
            .Should().Be("0", "失败要回到 Pending，记录绝不能丢");
        Scalar<string>(db, "SELECT next_retry_at_utc FROM sync_queue WHERE queue_id = @id;", new { id = queueId })
            .Should().NotBeNullOrWhiteSpace("必须安排退避重试");

        // 退避没过期 → 不允许重试
        var second = await engine.RunOnceAsync();
        mock.PushCallCount.Should().Be(1, "退避期内不该再次发起推送");
        second.Completed.Should().Be(0);

        // 退避过期 → 正常重试成功
        using (var conn = Open(db))
        {
            conn.Execute("UPDATE sync_queue SET next_retry_at_utc = '2000-01-01T00:00:00Z';");
        }

        (await engine.RunOnceAsync()).Completed.Should().Be(1);
    }

    [Fact]
    public async Task Push_WhenProcessDies_InFlightIsRecoveredOnNextStart()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        await EnqueueGameAsync(repo, "g-crash", "pid-crash");

        // 模拟"推送进行中进程被杀"：状态停在 InFlight
        using (var conn = Open(db))
        {
            conn.Execute("UPDATE sync_queue SET status = 1;");
        }

        var beforeRestart = await engine.RunOnceAsync();
        beforeRestart.Completed.Should().Be(0);
        mock.PushCallCount.Should().Be(0, "InFlight 的记录不该被并发重复推送");

        await engine.StartAsync();   // 下次启动的复位

        var afterRestart = await engine.RunOnceAsync();
        afterRestart.Completed.Should().Be(1, "否则这条变更永远发不出去");
        mock.DistinctEntityCount(nameof(SyncEntityType.Game)).Should().Be(1);
    }

    [Fact]
    public async Task Push_RespectsPolicyBatchSize()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        for (var i = 0; i < SyncOutboxPolicy.PushBatchSize + 10; i++)
        {
            await EnqueueGameAsync(repo, $"g-batch-{i}", $"pid-batch-{i}");
        }

        var first = await engine.RunOnceAsync();
        first.Completed.Should().Be(SyncOutboxPolicy.PushBatchSize);
        mock.PushBatchSizes[0].Should().Be(SyncOutboxPolicy.PushBatchSize, "一批不超过策略上限");

        var second = await engine.RunOnceAsync();
        second.Completed.Should().Be(10, "剩下的下一批继续推");
    }

    // ============================ Pull ============================

    [Fact]
    public async Task Pull_AppliesDataAndAdvancesCursorInSameRun()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-remote", SyncOperation.Create, GamePayload("g-remote", "pid-remote"));

        var result = await engine.RunOnceAsync();

        result.Pulled.Should().Be(1);
        result.Applied.Should().Be(1);
        result.CursorAdvanced.Should().BeTrue();

        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-remote';").Should().Be(1);
        Scalar<string>(db, "SELECT cursor FROM sync_state WHERE backend = @b;", new { b = mock.BackendId })
            .Should().Be("1");
    }

    [Fact]
    public async Task Pull_WhenApplyFailsMidway_RollsBackDataAndCursorTogether()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        // 同一批里两条：Game（先） + Session（后）。让 Session 的 INSERT 必定失败。
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-tx", SyncOperation.Create, GamePayload("g-tx", "pid-tx"));
        mock.SeedRemoteChange(nameof(SyncEntityType.Session), "s-tx", SyncOperation.Create, SessionPayload("s-tx", "g-tx"));

        using (var conn = Open(db))
        {
            conn.Execute("""
                CREATE TRIGGER abort_session_insert BEFORE INSERT ON sessions
                BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
                """);
        }

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
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-dup", SyncOperation.Create, GamePayload("g-dup", "pid-dup"));

        (await engine.RunOnceAsync()).Applied.Should().Be(1);

        // 把游标倒回去 = 服务端重放同一批（崩溃后重拉就是这个效果）
        using (var conn = Open(db))
        {
            conn.Execute("UPDATE sync_state SET cursor = '0';");
        }

        var replay = await engine.RunOnceAsync();
        replay.Applied.Should().Be(0, "已存在就不该再插一次");
        replay.Deferred.Should().Be(1);
        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-dup';").Should().Be(1);
    }

    [Fact]
    public async Task Pull_TwoDevices_EachEventuallyReceivesTheOthersData()
    {
        var deviceA = NewDbPath();
        var deviceB = NewDbPath();
        var repoA = new SqliteRepository(deviceA);
        var repoB = new SqliteRepository(deviceB);

        var mock = new MockSyncBackend();     // 同一个"服务端"
        var engineA = new DeviceSyncEngine(repoA, mock);
        var engineB = new DeviceSyncEngine(repoB, mock);

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
        mock.DistinctEntityCount(nameof(SyncEntityType.Game)).Should().Be(2, "服务端也只有两份，没有重复");
    }

    [Fact]
    public async Task Pull_EmptyResult_StillTracksServerWatermark()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        var empty = await engine.RunOnceAsync();
        empty.Pulled.Should().Be(0);
        Scalar<string>(db, "SELECT cursor FROM sync_state WHERE backend = @b;", new { b = mock.BackendId })
            .Should().Be("0", "空拉取也要把服务端高水位记下来");

        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-after-empty", SyncOperation.Create, GamePayload("g-after-empty", "pid-after-empty"));

        (await engine.RunOnceAsync()).Applied.Should().Be(1, "之后的变更要能被拉到");
    }

    [Fact]
    public async Task Pull_RespectsPolicyBatchSize()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        var total = SyncOutboxPolicy.PullBatchSize + 10;
        for (var i = 0; i < total; i++)
        {
            mock.SeedRemoteChange(nameof(SyncEntityType.Game), $"g-many-{i}", SyncOperation.Create,
                GamePayload($"g-many-{i}", $"pid-many-{i}"));
        }

        var first = await engine.RunOnceAsync();
        first.Pulled.Should().Be(SyncOutboxPolicy.PullBatchSize);
        first.Applied.Should().Be(SyncOutboxPolicy.PullBatchSize);
        mock.PullBatchSizes[0].Should().Be(SyncOutboxPolicy.PullBatchSize, "一批不超过策略上限");

        var second = await engine.RunOnceAsync();
        second.Applied.Should().Be(10, "剩下的下一批继续拉");
    }

    // ============================ 墓碑 ============================

    [Fact]
    public async Task Tombstone_FromRemote_IsPropagatedLocally()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-deleted", SyncOperation.Delete, "{}", "device-x");

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
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        // 云端已有墓碑
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-gone", SyncOperation.Delete, "{}", "device-x");
        await engine.RunOnceAsync();

        // ① 本地还想推一条旧的 Create → 服务端必须永久拒绝 → 死信，不再无限重试
        var queueId = await EnqueueGameAsync(repo, "g-gone", "pid-gone");
        var push = await engine.RunOnceAsync();

        push.PushRejected.Should().Be(1);
        Scalar<string>(db, "SELECT status FROM sync_queue WHERE queue_id = @id;", new { id = queueId })
            .Should().Be("3", "永久拒绝要进死信，不能一直重试");
        mock.HasEntity(nameof(SyncEntityType.Game), "g-gone").Should().BeFalse("绝不能复活");

        // ② 另一台落后设备推上来的旧数据，同样不能复活
        await engine.RunOnceAsync();   // 清掉死信后的一轮（这里主要是确保状态稳定）

        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-gone", SyncOperation.Create, GamePayload("g-gone", "pid-gone"), "stale-device");
        var pull = await engine.RunOnceAsync();

        pull.Applied.Should().Be(0);
        pull.Deferred.Should().BeGreaterThan(0);
        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-gone';")
            .Should().Be(0, "墓碑优先：陈旧数据不允许把已删除实体拉回本地");
    }

    // ============================ 游标隔离 / 协议护栏 ============================

    [Fact]
    public async Task Cursor_IsIsolatedPerBackend()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);

        var cloudflare = new MockSyncBackend("cloudflare");
        var webdav = new MockSyncBackend("webdav");

        cloudflare.SeedRemoteChange(nameof(SyncEntityType.Game), "g-cf", SyncOperation.Create, GamePayload("g-cf", "pid-cf"));

        await new DeviceSyncEngine(repo, cloudflare).RunOnceAsync();
        await new DeviceSyncEngine(repo, webdav).RunOnceAsync();

        Scalar<string>(db, "SELECT cursor FROM sync_state WHERE backend = 'cloudflare';").Should().Be("1");
        Scalar<string>(db, "SELECT cursor FROM sync_state WHERE backend = 'webdav';").Should().Be("0",
            "换后端绝不复用另一个后端的游标");
        Scalar<int>(db, "SELECT COUNT(*) FROM sync_state;").Should().Be(2);
    }

    [Fact]
    public async Task Pull_UnknownSchemaVersion_IsDeferredInsteadOfMisparsed()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        var futureJson = """{"schema_version":99,"global_id":"g-future","name":"X","platform":"manual","platform_id":"pid-future"}""";
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "g-future", SyncOperation.Create, futureJson);

        var result = await engine.RunOnceAsync();

        result.Applied.Should().Be(0);
        result.Deferred.Should().Be(1, "不认识的 schema 宁可不同步，也不能按旧结构硬解");
        Scalar<int>(db, "SELECT COUNT(*) FROM games WHERE global_id = 'g-future';").Should().Be(0);
    }

    // ============================ 底线：同步绝不影响本地计时 ============================

    [Fact]
    public async Task NetworkFailure_DoesNotThrow_AndLocalTimingStillWorks()
    {
        var db = NewDbPath();
        var repo = new SqliteRepository(db);
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(repo, mock);

        await EnqueueGameAsync(repo, "g-offline", "pid-offline");
        mock.ThrowOnNextPush = true;
        mock.ThrowOnNextPull = true;

        // 后端彻底不可用：RunOnce 不允许把异常抛给调用方（可能是游戏监听链路上的调用）
        DeviceSyncRunResult? result = null;
        var act = async () => { result = await engine.RunOnceAsync(); };
        await act.Should().NotThrowAsync();

        result!.Error.Should().NotBeNull("失败只能在返回值里体现，不能抛出去");

        // 本地计时路径必须照常工作
        var game = await repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "999001", "OfflineGame", "OfflineGame.exe", @"E:\offline\OfflineGame.exe"));
        await repo.AddSessionDurationToDailyAsync("2026-10-01", game.Id, 120);

        var summaries = await repo.GetDailySummariesByDateAsync("2026-10-01");
        summaries.Should().ContainSingle();
        summaries[0].DurationSeconds.Should().Be(120, "同步失败绝不能影响本地时长记录");
    }
}
