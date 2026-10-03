using Dapper;
using FluentAssertions;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// Phase 2a 护栏：Outbox / 墓碑 / 同步游标 / 设备的本地存储语义。
///
/// 重点保护的几条不变量：
///   · payload 是"入队那一刻的快照"，不是延迟读取
///   · 只有收到 ACK 才能置 completed；失败要回到 Pending 并按退避安排下次重试
///   · 进程被杀留在 InFlight 的记录，重启后必须回到 Pending（否则永远发不出去）
///   · Outbox 不是历史表：completed 超过保留期要清理
///   · 墓碑要能去重、并按保留期清理
///   · 每个 Backend 的游标互相独立；设备改名不改 device_id
///
/// ⚠️ 本文件只存在于 `feature/multi-device-sync` 分支，另两条分支由 csproj 条件自动排除。
/// </summary>
public class DeviceSyncOutboxTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;

    public DeviceSyncOutboxTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sappfiler_outbox_{Guid.NewGuid():N}.db");
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

    private static SyncQueueItem NewItem(string globalId, string payload = "{\"schema_version\":1}", SyncOperation op = SyncOperation.Create)
        => new()
        {
            QueueId = Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Session),
            EntityGlobalId = globalId,
            Operation = op,
            BaseVersion = 1,
            Payload = payload,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Status = SyncQueueStatus.Pending
        };

    // ============================ Outbox ============================

    [Fact]
    public async Task Enqueue_ThenReadBack_KeepsPayloadSnapshotAndInsertionOrder()
    {
        await _repo.EnqueueOutboxAsync(NewItem("global-1", "{\"n\":1}"));
        await _repo.EnqueueOutboxAsync(NewItem("global-2", "{\"n\":2}"));
        await _repo.EnqueueOutboxAsync(NewItem("global-3", "{\"n\":3}"));

        var pending = await _repo.GetPendingOutboxAsync();

        pending.Should().HaveCount(3);
        // 入队顺序必须稳定（同一秒内 created_at 分辨不出先后，靠 rowid）
        pending.Select(p => p.EntityGlobalId).Should().Equal("global-1", "global-2", "global-3");
        // payload 是快照本身，原样可读回
        pending[0].Payload.Should().Be("{\"n\":1}");
        pending.Should().OnlyContain(p => p.Status == SyncQueueStatus.Pending);
    }

    [Fact]
    public async Task MarkCompleted_RemovesFromPending_ButKeepsRowUntilPruned()
    {
        var item = NewItem("global-done");
        await _repo.EnqueueOutboxAsync(item);

        var affected = await _repo.MarkOutboxCompletedAsync(new[] { item.QueueId });
        affected.Should().Be(1);

        (await _repo.GetPendingOutboxAsync()).Should().BeEmpty("已收到 ACK 的变更不能再被推送");
        (await _repo.PruneCompletedOutboxAsync()).Should().Be(0, "未到保留期不该被清理");

        using var conn = Open();
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sync_queue;").Should().Be(1, "ACK 之后行要留着直到清理");
        conn.ExecuteScalar<string>("SELECT status FROM sync_queue;").Should().Be("2");
    }

    [Fact]
    public async Task FailedItem_StaysPending_WithRetryScheduledAndErrorRecorded()
    {
        var item = NewItem("global-retry");
        await _repo.EnqueueOutboxAsync(item);

        await _repo.RecordOutboxFailureAsync(item.QueueId, "HTTP 503", SyncOutboxPolicy.NextRetryDelay(0));

        var pending = await _repo.GetPendingOutboxAsync();
        pending.Should().BeEmpty("退避时间还没到，不该立刻重试");

        using var conn = Open();
        conn.ExecuteScalar<long>("SELECT retry_count FROM sync_queue;").Should().Be(1);
        conn.ExecuteScalar<string>("SELECT last_error FROM sync_queue;").Should().Be("HTTP 503");
        conn.ExecuteScalar<string>("SELECT status FROM sync_queue;").Should().Be("0", "失败要回到 Pending，绝不能让待发记录消失");

        // 把重试时间提前到过去，应立刻重新可推送
        conn.Execute("UPDATE sync_queue SET next_retry_at_utc = '2000-01-01T00:00:00Z';");
        (await _repo.GetPendingOutboxAsync()).Should().ContainSingle()
            .Which.EntityGlobalId.Should().Be("global-retry");
    }

    [Fact]
    public async Task InFlightLeftByCrash_IsReturnedToPendingOnRestart()
    {
        var item = NewItem("global-crashed");
        await _repo.EnqueueOutboxAsync(item);

        // 模拟"推送进行中进程被杀"：状态停在 InFlight
        using (var conn = Open())
        {
            conn.Execute("UPDATE sync_queue SET status = 1;");
        }
        (await _repo.GetPendingOutboxAsync()).Should().BeEmpty("InFlight 不该被并发重复推送");

        var reset = await _repo.ResetInFlightOutboxAsync();
        reset.Should().Be(1);

        (await _repo.GetPendingOutboxAsync()).Should().ContainSingle()
            .Which.EntityGlobalId.Should().Be("global-crashed",
                "否则这条记录会永远发不出去");
    }

    [Fact]
    public async Task PruneCompleted_RemovesOnlyExpiredCompletedRows()
    {
        var expired = NewItem("global-old");
        var fresh = NewItem("global-fresh");
        await _repo.EnqueueOutboxAsync(expired);
        await _repo.EnqueueOutboxAsync(fresh);

        var expiredAt = DateTime.UtcNow - SyncOutboxPolicy.CompletedRetention - TimeSpan.FromDays(1);
        using (var conn = Open())
        {
            conn.Execute("UPDATE sync_queue SET status = 2;");
            conn.Execute(
                "UPDATE sync_queue SET created_at_utc = @t WHERE queue_id = @id;",
                new { t = expiredAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), id = expired.QueueId });
        }

        (await _repo.PruneCompletedOutboxAsync()).Should().Be(1);

        using var verify = Open();
        verify.Query<string>("SELECT entity_global_id FROM sync_queue;")
            .Should().Equal("global-fresh");
    }

    [Fact]
    public async Task PendingQuery_RespectsBatchLimit()
    {
        for (var i = 0; i < 5; i++) await _repo.EnqueueOutboxAsync(NewItem($"global-{i}"));

        (await _repo.GetPendingOutboxAsync(limit: 2)).Should().HaveCount(2);
    }

    // ============================ 墓碑 ============================

    [Fact]
    public async Task Tombstone_IsDeduplicatedPerEntity_AndPrunedByRetention()
    {
        var now = DateTime.UtcNow;
        await _repo.AddTombstoneAsync(new SyncTombstone
        {
            TombstoneId = Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Session),
            EntityGlobalId = "gone-1",
            DeviceId = "dev-a",
            DeletedAtUtc = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            CreatedAtUtc = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        });

        // 同一实体重复删除 → 只保留一条（UNIQUE(entity_type, entity_global_id)）
        await _repo.AddTombstoneAsync(new SyncTombstone
        {
            TombstoneId = Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Session),
            EntityGlobalId = "gone-1",
            DeviceId = "dev-a",
            DeletedAtUtc = now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            CreatedAtUtc = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        });

        var tombstones = await _repo.GetTombstonesAsync();
        tombstones.Should().ContainSingle();

        (await _repo.PruneTombstonesAsync()).Should().Be(0, "30 天保留期内不得清理");

        using var conn = Open();
        conn.Execute("UPDATE sync_tombstones SET deleted_at_utc = '2000-01-01T00:00:00Z';");
        (await _repo.PruneTombstonesAsync()).Should().Be(1);
    }

    // ============================ 同步游标 ============================

    [Fact]
    public async Task SyncState_IsIndependentPerBackend()
    {
        await _repo.SaveSyncStateAsync(new SyncStateRecord
        {
            Backend = "cloudflare", AccountId = "me", Cursor = "18293",
            LastSyncAtUtc = "2026-10-03T00:00:00Z"
        });
        await _repo.SaveSyncStateAsync(new SyncStateRecord
        {
            Backend = "webdav", AccountId = "me", Cursor = "294",
            LastSyncAtUtc = "2026-10-03T00:00:00Z"
        });

        (await _repo.GetSyncStateAsync("cloudflare", "me"))!.Cursor.Should().Be("18293");
        (await _repo.GetSyncStateAsync("webdav", "me"))!.Cursor.Should().Be("294",
            "切换后端绝不能复用另一套游标");

        // 再次保存是更新而不是新增
        await _repo.SaveSyncStateAsync(new SyncStateRecord
        {
            Backend = "cloudflare", AccountId = "me", Cursor = "18400",
            LastSyncAtUtc = "2026-10-03T01:00:00Z"
        });

        using var conn = Open();
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sync_state;").Should().Be(2);
        (await _repo.GetSyncStateAsync("cloudflare", "me"))!.Cursor.Should().Be("18400");
    }

    [Fact]
    public async Task GetSyncState_UnknownBackend_ReturnsNull()
    {
        (await _repo.GetSyncStateAsync("nobody", "me")).Should().BeNull();
    }

    // ============================ 设备 ============================

    [Fact]
    public async Task LocalDevice_ExistsOnce_AndRenameKeepsIdentity()
    {
        var deviceId = await _repo.GetLocalDeviceIdAsync();
        deviceId.Should().NotBeNullOrWhiteSpace();

        var devices = await _repo.GetDevicesAsync();
        devices.Should().ContainSingle();
        devices[0].IsThisDevice.Should().BeTrue();
        devices[0].DeviceId.Should().Be(deviceId);

        await _repo.RenameDeviceAsync(deviceId, "Gaming Laptop");

        var after = await _repo.GetDevicesAsync();
        after.Should().ContainSingle();
        after[0].DeviceName.Should().Be("Gaming Laptop");
        after[0].DeviceId.Should().Be(deviceId, "改名绝不能改变设备身份");

        // 反复读取不会制造第二台"本机"
        (await _repo.GetLocalDeviceIdAsync()).Should().Be(deviceId);
        (await _repo.GetDevicesAsync()).Should().ContainSingle();
    }

    // ============================ 退避档位 ============================

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 300)]
    [InlineData(5, 900)]
    [InlineData(6, 1800)]
    [InlineData(7, 1800)]
    [InlineData(99, 1800)]
    public void NextRetryDelay_FollowsFrozenSchedule_AndCapsAt30Minutes(int retryCount, int expectedSeconds)
    {
        SyncOutboxPolicy.NextRetryDelay(retryCount).TotalSeconds.Should().Be(expectedSeconds);
    }
}
