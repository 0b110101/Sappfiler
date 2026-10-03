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
/// 2c 阶段的 **Mock 专属**用例：故障注入（ACK 丢失、后端抛异常、彻底离线）。
///
/// ⚠️ 2f 之后**通用契约已迁移**到 <see cref="DeviceSyncBackendContract"/>（对任何 Backend 都成立）；
/// 这里只留 Mock 实现细节的注入用例 —— 因为这些"模拟 ACK 丢失 / 模拟网络中断"的旋钮
/// 是 Mock 特有的，不属于"任何 Backend 都必须满足"的契约面。
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

    // ============================ Push：ACK 丢失 ============================

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

    // ============================ Push：后端抛异常 ============================

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
