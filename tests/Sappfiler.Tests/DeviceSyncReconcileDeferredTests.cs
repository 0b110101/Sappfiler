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
/// 2e 步骤 7 验收：**Deferred 台账重处理**。
///
/// 用户冻结的四条：
///   ① 取 unresolved → 从台账 payload 重建远端变更 → 重跑**现有**规则 → 有结论就写
///      <c>resolved_at_utc</c> + <c>resolution</c>；仍无法确定则保持 unresolved。
///   ② **不删除台账行**（审计：为什么曾延后 / 何时重处理 / 最后怎么解决）。
///   ③ **不无限循环**：单遍 + 有界；仍 unresolved 的留到下次调用，不自旋。
///   ④ **不改步骤 5/6 的判定语义**：不做 max duration、不做"最新胜"、不做本地/远端优先、
///      不自动选 Notion Page、不自动覆盖 exe/path、不自动复活 ignored。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public class DeviceSyncReconcileDeferredTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;

    public DeviceSyncReconcileDeferredTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sappfiler_deferred_{Guid.NewGuid():N}.db");
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

    private int InsertGame(string platform, string platformId, string name, string? exePath = @"E:\g\foo.exe",
        string? notion = null, string? globalId = null, string status = "active")
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>("""
            INSERT INTO games (platform, platform_id, name, executable, executable_path, notion_page_id, status, global_id)
            VALUES (@platform, @platformId, @name, @exe, @exePath, @notion, @status, @globalId);
            SELECT last_insert_rowid();
            """,
            new { platform, platformId, name, exe = "foo.exe", exePath, notion, status, globalId });
    }

    private void SeedDeferredRow(string changeId, string entityType, string entityGlobalId, string reason,
        string? payload, SyncOperation operation = SyncOperation.Create, string deviceId = "device-B")
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO sync_deferred_changes
                (change_id, backend, entity_type, entity_global_id, operation, device_id, payload,
                 reason, schema_version, first_seen_at_utc, last_seen_at_utc, seen_count)
            VALUES (@changeId, 'mock', @entityType, @entityGlobalId, @operation, @deviceId, @payload,
                    @reason, 1, '2026-10-03T00:00:00Z', '2026-10-03T00:00:00Z', 1);
            """,
            new { changeId, entityType, entityGlobalId, operation = (int)operation, deviceId, payload, reason });
    }

    private static string GamePayload(string globalId, string platform, string platformId,
        string name = "Foo", string? exePath = @"E:\g\foo.exe", string? notion = null)
        => SyncPayloads.Serialize(new GameSyncPayload
        {
            GlobalId = globalId,
            Name = name,
            Platform = platform,
            PlatformId = platformId,
            Executable = "foo.exe",
            ExecutablePath = exePath ?? string.Empty,
            NotionPageId = notion
        });

    private static string SessionPayload(string globalId, string gameGlobalId, string deviceId,
        string startedUtc, int duration)
        => SyncPayloads.Serialize(new SessionSyncPayload
        {
            GlobalId = globalId,
            GameGlobalId = gameGlobalId,
            DeviceId = deviceId,
            ProcessName = "foo.exe",
            StartedAtUtc = startedUtc,
            EndedAtUtc = startedUtc,
            DurationSeconds = duration,
            TimePrecision = TimePrecision.Exact
        });

    private (string ResolvedAt, string? Resolution) LedgerRow(string changeId)
    {
        using var conn = Open();
        return conn.QuerySingle<(string, string?)>(
            "SELECT COALESCE(resolved_at_utc, ''), resolution FROM sync_deferred_changes WHERE change_id = @id;",
            new { id = changeId });
    }

    // ============ ① 端到端闭环：2c 延后 → 2e 重处理收敛 ============

    [Fact]
    public async Task DeferredGame_FromRealPull_IsResolvedByReconciliation_AndLedgerIsKept()
    {
        // 本地先占住业务键，制造"远端同键不同 identity" → 同步引擎延后并写台账
        var localId = InsertGame("steam", "123456", "Foo", globalId: "local-A");

        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(_repo, mock);
        var seededChangeId = mock.SeedRemoteChange(nameof(SyncEntityType.Game), "remote-B", SyncOperation.Create,
            GamePayload("remote-B", "steam", "123456"), deviceId: "device-B");

        (await engine.RunOnceAsync()).Deferred.Should().Be(1);
        Scalar<int>("SELECT COUNT(*) FROM sync_deferred_changes WHERE resolved_at_utc IS NULL;").Should().Be(1);

        // —— 步骤 7：重处理（完全离线，只吃台账 payload）——
        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.Scanned.Should().Be(1);
        summary.Resolved.Should().Be(1);
        summary.Resolutions.Should().Equal(new[] { ReconciliationResolutions.MergedLocalIdentity });

        // 身份已收敛
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(1);
        Scalar<string>("SELECT canonical_global_id FROM sync_identity_supersessions;").Should().Be("local-A");
        Scalar<string>("SELECT global_id FROM games WHERE id = @id;", new { id = localId }).Should().Be("local-A");

        // ② 台账行**没有被删除**，只是被标记为已解决
        Scalar<int>("SELECT COUNT(*) FROM sync_deferred_changes;").Should().Be(1);
        var row = LedgerRow(seededChangeId);
        row.Resolution.Should().Be(ReconciliationResolutions.MergedLocalIdentity);
        row.ResolvedAt.Should().NotBeNullOrWhiteSpace();
    }

    // ============ ① 仍无法确定 → 保持 unresolved（且不自旋） ============

    [Fact]
    public async Task StillAmbiguous_StaysUnresolved_AndIsNotLoopedOn()
    {
        // 双方绑定不同 Notion 页面 → 冻结语义：所有命中级别一律延后，绝不替用户选一个
        InsertGame("steam", "123456", "Foo", notion: "page-local", globalId: "local-A");
        SeedDeferredRow("chg-notion", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity,
            GamePayload("remote-B", "steam", "123456", notion: "page-remote"));

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.Resolved.Should().Be(0);
        summary.StillDeferred.Should().Be(1);
        summary.Scanned.Should().Be(1, "单遍：扫到就结束，不在本批次里自旋");

        LedgerRow("chg-notion").ResolvedAt.Should().BeNullOrEmpty("无法确定就必须保持 unresolved");
        LedgerRow("chg-notion").Resolution.Should().BeNull();
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);

        // 再跑一次仍然只是"再试一遍"，不会产生任何新的写入
        var second = await _repo.ReconcileDeferredChangesAsync();
        second.Scanned.Should().Be(1);
        second.StillDeferred.Should().Be(1);
        Scalar<int>("SELECT COUNT(*) FROM sync_deferred_changes;").Should().Be(1);
    }

    [Fact]
    public async Task BatchLimit_IsRespected_AndDoesNotLoopUntilEmpty()
    {
        InsertGame("steam", "123456", "Foo", notion: "page-local", globalId: "local-A");

        for (var i = 1; i <= 3; i++)
        {
            SeedDeferredRow($"chg-{i}", nameof(SyncEntityType.Game), $"remote-{i}",
                DeferredReasons.BusinessKeyTakenByOtherIdentity,
                GamePayload($"remote-{i}", "steam", "123456", notion: "page-remote"));
        }

        var summary = await _repo.ReconcileDeferredChangesAsync(batchLimit: 1);

        summary.Scanned.Should().Be(1, "有界：只扫 batchLimit 条，绝不循环到清空");
        Scalar<int>("SELECT COUNT(*) FROM sync_deferred_changes WHERE resolved_at_utc IS NULL;").Should().Be(3);
    }

    // ============ 阻塞类结论：写结论码，但不反复重试 ============

    [Fact]
    public async Task IgnoredCanonical_IsResolvedAsProtected_AndRowStaysIgnored()
    {
        var localId = InsertGame("steam", "123456", "Foo", globalId: "local-A", status: "ignored");
        SeedDeferredRow("chg-ignored", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity,
            GamePayload("remote-B", "steam", "123456"));

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.Resolutions.Should().Equal(new[] { ReconciliationResolutions.LocalIgnoredProtected });
        Scalar<string>("SELECT status FROM games WHERE id = @id;", new { id = localId }).Should().Be("ignored");
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
    }

    [Fact]
    public async Task StrongKeyConflict_IsResolvedAsBlocked_AndWritesNothing()
    {
        InsertGame("steam", "999999", "Foo", globalId: "local-A");
        SeedDeferredRow("chg-conflict", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity,
            GamePayload("remote-B", "steam", "123456"));

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.Resolutions.Should().Equal(new[] { ReconciliationResolutions.BlockedStrongKeyConflict });
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(1);
    }

    // ============ 无 payload / 类型不符 / 不支持的类型 ============

    [Fact]
    public async Task LedgerRowsWithoutUsablePayload_AreConvergedAsLegacy()
    {
        InsertGame("steam", "123456", "Foo", globalId: "local-A");

        SeedDeferredRow("chg-null", nameof(SyncEntityType.Game), "remote-1",
            DeferredReasons.BusinessKeyTakenByOtherIdentity, payload: null);
        SeedDeferredRow("chg-broken", nameof(SyncEntityType.Game), "remote-2",
            DeferredReasons.BusinessKeyTakenByOtherIdentity, payload: "{ not json }");

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.NoPayload.Should().Be(1);
        summary.Resolutions.Should().Contain(ReconciliationResolutions.LegacyNoPayload);
        LedgerRow("chg-null").Resolution.Should().Be(ReconciliationResolutions.LegacyNoPayload);
        LedgerRow("chg-broken").Resolution.Should().Be(ReconciliationResolutions.LegacyNoPayload,
            "坏 payload 无法重处理，必须收敛掉，否则每次启动都白跑");
    }

    [Fact]
    public async Task UnsupportedEntityType_IsLeftUnresolved_ForLaterPhases()
    {
        SeedDeferredRow("chg-map", "GameMapping", "map-1",
            DeferredReasons.UnsupportedEntityType,
            GamePayload("map-1", "steam", "123456"));

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.UnsupportedType.Should().Be(1);
        summary.Resolved.Should().Be(0);
        LedgerRow("chg-map").ResolvedAt.Should().BeNullOrEmpty("2e 不覆盖 GameMapping，留给集成阶段");
    }

    // ============ 墓碑优先：绝不复活已删除实体 ============

    [Fact]
    public async Task TombstonedLocal_IsSkipped_NeverConvergedNorReapplied()
    {
        InsertGame("steam", "123456", "Foo", globalId: "local-A");

        using (var conn = Open())
        {
            conn.Execute("""
                INSERT INTO sync_tombstones
                    (tombstone_id, entity_type, entity_global_id, device_id, deleted_at_utc, created_at_utc)
                VALUES ('t-1', 'Game', 'remote-B', 'device-B', '2026-10-02T00:00:00Z', '2026-10-02T00:00:00Z');
                """);
        }

        SeedDeferredRow("chg-tomb", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.TombstonedLocal,
            GamePayload("remote-B", "steam", "123456"));

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.SkippedTombstoned.Should().Be(1);
        summary.Resolved.Should().Be(0);
        LedgerRow("chg-tomb").ResolvedAt.Should().BeNullOrEmpty();
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(1, "绝不因为重处理而复活/新建实体");
    }

    // ============ Session：依赖就绪 → 用既有落地规则补落地 ============

    [Fact]
    public async Task DeferredSession_WhenDependencyArrives_IsReappliedByExistingRules()
    {
        // 台账里有一条"依赖缺失"的会话（当初游戏还没到本地）
        SeedDeferredRow("chg-sess", nameof(SyncEntityType.Session), "s-orphan",
            DeferredReasons.DependencyMissing,
            SessionPayload("s-orphan", "game-missing", "device-B", "2026-10-01T10:00:00Z", 600));

        // 依赖还没到 → 仍然 unresolved
        (await _repo.ReconcileDeferredChangesAsync()).Resolved.Should().Be(0);
        LedgerRow("chg-sess").ResolvedAt.Should().BeNullOrEmpty();

        // 依赖到了（游戏已落地）
        InsertGame("steam", "123456", "Foo", globalId: "game-missing");

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.Resolutions.Should().Equal(new[] { ReconciliationResolutions.DependencyResolved });
        Scalar<int>("SELECT COUNT(*) FROM sessions WHERE global_id = 's-orphan';").Should().Be(1,
            "依赖就绪后必须用既有落地规则把它补落地，否则这条远端数据永久丢失");
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE global_id = 's-orphan';").Should().Be(600);
    }

    [Fact]
    public async Task DeferredSession_FactMismatch_StaysUnresolved_NoMaxDurationRule()
    {
        var gameId = InsertGame("steam", "123456", "Foo", globalId: "game-G");

        using (var conn = Open())
        {
            conn.Execute("""
                INSERT INTO sessions (game_id, pid, process_name, start_time, end_time, last_heartbeat,
                                      duration_seconds, is_active, global_id, device_id,
                                      started_at_utc, ended_at_utc, time_precision)
                VALUES (@g, 1, 'foo.exe', '2026-10-01 10:00:00', '2026-10-01 10:10:00', '2026-10-01 10:10:00',
                        600, 0, 's-local', 'device-A', '2026-10-01T10:00:00Z', '2026-10-01T10:00:00Z', 'exact');
                """, new { g = gameId });
        }

        SeedDeferredRow("chg-sess2", nameof(SyncEntityType.Session), "s-remote",
            SessionIdentityDecisionReasons.ImmutableFactMismatch,
            SessionPayload("s-remote", "game-G", "device-A", "2026-10-01T10:00:00Z", 3700));

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.Resolved.Should().Be(0);
        summary.StillDeferred.Should().Be(1);
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE global_id = 's-local';").Should().Be(600,
            "④ 绝不出现'取最大 duration'这类隐藏裁决");
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
    }

    // ============ 幂等 / 离线 ============

    [Fact]
    public async Task RepeatedRuns_AreIdempotent_AndNeverTouchTheBackend()
    {
        InsertGame("steam", "123456", "Foo", globalId: "local-A");
        SeedDeferredRow("chg-once", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity,
            GamePayload("remote-B", "steam", "123456"));

        // 没有任何 SyncEngine / Backend 参与 —— 重处理必须完全离线
        var mock = new MockSyncBackend();

        (await _repo.ReconcileDeferredChangesAsync()).Resolved.Should().Be(1);
        var second = await _repo.ReconcileDeferredChangesAsync();

        second.Scanned.Should().Be(0, "已解决的条目不会被再次扫描");
        second.Resolved.Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(1, "重复运行不得堆积");
        mock.PushCallCount.Should().Be(0);
        mock.PullCallCount.Should().Be(0);
    }
}
