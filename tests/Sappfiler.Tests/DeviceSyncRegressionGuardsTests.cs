using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.DeviceSync;
using GameTimeTracker.Infrastructure.Notion;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 2e 步骤 8 验收：**回归护栏**。
///
/// 本文件**不引入任何新的同步语义**，职责只有一个：
/// 证明 2b / 2c / 2d / 2e 的改动**没有破坏 Sappfiler 既有行为**。
///
/// 用户冻结的两条原则：
///   ① 为了做回归测试**不修改生产代码** —— 只使用现有 public API
///      （仓储方法、<see cref="NotionSyncService"/> 的真实构造、SQLite transaction / 触发器）。
///   ② 不为理论上的极端 race 引入新的生产 hook。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public class DeviceSyncRegressionGuardsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;

    public DeviceSyncRegressionGuardsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sappfiler_regress_{Guid.NewGuid():N}.db");
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

    // ============================ 工具 ============================

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

    private void Exec(string sql, object? param = null)
    {
        using var conn = Open();
        conn.Execute(sql, param);
    }

    private int InsertGame(string platform, string platformId, string name,
        string? exe = "foo.exe", string? exePath = @"E:\g\foo\foo.exe", string? notion = null,
        string? globalId = null, string status = "active")
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>("""
            INSERT INTO games (platform, platform_id, name, executable, executable_path,
                               notion_page_id, status, global_id)
            VALUES (@platform, @platformId, @name, @exe, @exePath, @notion, @status, @globalId);
            SELECT last_insert_rowid();
            """,
            new { platform, platformId, name, exe = exe ?? string.Empty, exePath = exePath ?? string.Empty, notion, status, globalId });
    }

    private int InsertSession(int gameId, string globalId, string deviceId, string startedUtc,
        int duration, bool active = false, string processName = "foo.exe")
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>("""
            INSERT INTO sessions (game_id, pid, process_name, start_time, end_time, last_heartbeat,
                                  duration_seconds, is_active, global_id, device_id,
                                  started_at_utc, ended_at_utc, time_precision)
            VALUES (@gameId, 1, @processName, '2026-10-01 10:00:00', '2026-10-01 10:10:00', '2026-10-01 10:10:00',
                    @duration, @active, @globalId, @deviceId,
                    @startedUtc, @startedUtc, 'exact');
            SELECT last_insert_rowid();
            """,
            new { gameId, globalId, deviceId, startedUtc, duration, active, processName });
    }

    private void InsertDailySummary(int gameId, string date, int seconds, int sessions)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
            VALUES (@date, @gameId, @seconds, @minutes, @sessions, 'pending');
            """,
            new { date, gameId, seconds, minutes = seconds / 60, sessions });
    }

    private void InsertCatalogItem(string pageId, string name, string identifiersJson)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO game_catalog (page_id, name, aliases_json, identifiers_json, genres_json, last_synced_at)
            VALUES (@pageId, @name, '[]', @identifiersJson, '[]', CURRENT_TIMESTAMP);
            """,
            new { pageId, name, identifiersJson });
    }

    private static SyncChange RemoteGame(string globalId, string platform, string platformId,
        string? exePath = @"E:\g\foo\foo.exe", string? notion = null)
        => new()
        {
            ChangeId = "chg-" + Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Game),
            EntityGlobalId = globalId,
            Operation = SyncOperation.Create,
            Payload = SyncPayloads.Serialize(new GameSyncPayload
            {
                GlobalId = globalId,
                Name = "Foo",
                Platform = platform,
                PlatformId = platformId,
                Executable = "foo.exe",
                ExecutablePath = exePath ?? string.Empty,
                NotionPageId = notion
            }),
            DeviceId = "device-B",
            CreatedAtUtc = "2026-10-01T12:00:00Z"
        };

    private static SyncChange RemoteSession(string globalId, string gameGlobalId, string deviceId,
        string startedUtc, int duration)
        => new()
        {
            ChangeId = "chg-" + Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Session),
            EntityGlobalId = globalId,
            Operation = SyncOperation.Create,
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
            CreatedAtUtc = "2026-10-01T12:00:00Z"
        };

    private void SeedDeferredRow(string changeId, string entityType, string entityGlobalId, string reason,
        string? payload, string? resolvedAt = null, string? resolution = null)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO sync_deferred_changes
                (change_id, backend, entity_type, entity_global_id, operation, device_id, payload,
                 reason, schema_version, first_seen_at_utc, last_seen_at_utc, seen_count,
                 resolved_at_utc, resolution)
            VALUES (@changeId, 'mock', @entityType, @entityGlobalId, 1, 'device-B', @payload,
                    @reason, 1, '2026-10-03T00:00:00Z', '2026-10-03T00:00:00Z', 1,
                    @resolvedAt, @resolution);
            """,
            new { changeId, entityType, entityGlobalId, payload, reason, resolvedAt, resolution });
    }

    private int SupersessionCount() => Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;");

    private int TombstoneCount() => Scalar<int>("SELECT COUNT(*) FROM sync_tombstones;");

    // ==================== Game：ignored / 强键 / exe-path / Notion ====================

    [Fact]
    public async Task IgnoredGame_IsNeverRevivedByRemoteChanges()
    {
        var localId = InsertGame("steam", "123456", "Foo", notion: "page-local",
            globalId: "local-A", status: "ignored");

        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(_repo, mock);
        mock.SeedRemoteChange(nameof(SyncEntityType.Game), "remote-B", SyncOperation.Create,
            RemoteGame("remote-B", "steam", "123456", notion: "page-local").Payload);

        var run = await engine.RunOnceAsync();
        await _repo.ReconcileDeferredChangesAsync();

        run.Deferred.Should().Be(1);
        run.Applied.Should().Be(0);

        // ignored 绝不能被远端复活：状态、字段、行数都不动
        Scalar<string>("SELECT status FROM games WHERE id = @id;", new { id = localId }).Should().Be("ignored");
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(1, "不得为远端游戏新建行");
        Scalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(0);
        SupersessionCount().Should().Be(0);
        TombstoneCount().Should().Be(0);
    }

    [Fact]
    public async Task StrongKeyConflict_NeverMergesLocalRow()
    {
        var localId = InsertGame("steam", "999999", "Foo", globalId: "local-A");

        var outcome = await _repo.ReconcileRemoteGameAsync(
            RemoteGame("remote-B", "steam", "123456"));

        outcome.Result.Should().Be(GameReconcileResult.Blocked,
            "强键明确指向两个不同游戏 —— 宁可不动，也不猜");
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(1);
        Scalar<string>("SELECT platform_id FROM games WHERE id = @id;", new { id = localId }).Should().Be("999999");
        Scalar<string>("SELECT global_id FROM games WHERE id = @id;", new { id = localId }).Should().Be("local-A");
        SupersessionCount().Should().Be(0, "被阻断就不能留下'已经合并过'的痕迹");
    }

    [Fact]
    public async Task Reconcile_KeepsExecutableAndPath_AndListenerStillFindsTheGame()
    {
        var localId = InsertGame("steam", "123456", "Foo", globalId: "local-A");

        var outcome = await _repo.ReconcileRemoteGameAsync(
            RemoteGame("remote-B", "steam", "123456", exePath: @"Z:\other\place\foo.exe"));

        outcome.Result.Should().Be(GameReconcileResult.Merged);

        // 既有行为：canonical 的 executable / executable_path 一动不能动
        Scalar<string>("SELECT executable FROM games WHERE id = @id;", new { id = localId }).Should().Be("foo.exe");
        Scalar<string>("SELECT executable_path FROM games WHERE id = @id;", new { id = localId })
            .Should().Be(@"E:\g\foo\foo.exe");

        // 监听链依赖的识别方法必须仍然认得它（游戏启动 / 5 秒心跳都走这条路）
        var found = await _repo.GetGameByPathOrExeAsync(@"E:\g\foo\foo.exe", "foo.exe");
        found.Should().NotBeNull("对账绝不能把监听链的关键路径改坏");
        found!.Id.Should().Be(localId);
    }

    [Fact]
    public async Task NotionPageId_IsNeverOverwritten_AndPageConflictStaysDeferred()
    {
        var localId = InsertGame("steam", "123456", "Foo", notion: "page-local", globalId: "local-A");
        SeedDeferredRow("chg-notion", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity,
            RemoteGame("remote-B", "steam", "123456", notion: "page-remote").Payload);

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.Resolved.Should().Be(0, "双方页面不同 = 必须由用户决定，不能自动覆盖");
        Scalar<string>("SELECT notion_page_id FROM games WHERE id = @id;", new { id = localId })
            .Should().Be("page-local", "Notion Page ID 绝不自动覆盖");
        SupersessionCount().Should().Be(0);
    }

    [Fact]
    public async Task AutoLinkFromCatalog_StillWorks_AfterReconciliation()
    {
        // 总表条目：真 page id（32 位十六进制）+ Steam 标识
        InsertCatalogItem("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Foo", "[\"1781260\"]");
        var localId = InsertGame("steam", "1781260", "Foo", notion: null, globalId: "local-A");

        // 先跑一轮对账（含一条会被收敛的台账记录），确认它不会破坏自动关联所需的数据
        SeedDeferredRow("chg-autolink", nameof(SyncEntityType.Game), "remote-X",
            DeferredReasons.BusinessKeyTakenByOtherIdentity, payload: null);
        await _repo.ReconcileDeferredChangesAsync();

        var config = new TrackerConfig
        {
            NotionToken = "fake-token",
            DailyDatabaseId = "fake-daily-db",
            GameDatabaseId = "fake-game-db"
        };
        var sync = new NotionSyncService(_repo, new FakeNotionClient(), config);

        var linked = await sync.AutoLinkGamesFromCatalogAsync();

        linked.Should().Be(1, "game_catalog 自动关联行为必须保持可用");
        Scalar<string>("SELECT notion_page_id FROM games WHERE id = @id;", new { id = localId })
            .Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }

    // ==================== Session：幂等 / 历史事实 / 墓碑 / daily_summary ====================

    [Fact]
    public async Task RepeatedSessionPull_IsIdempotent_AtBothLayers()
    {
        InsertGame("steam", "123456", "Foo", globalId: "game-G");

        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(_repo, mock);
        var payload = RemoteSession("s-remote", "game-G", "device-A", "2026-10-01T10:00:00Z", 600).Payload;

        mock.SeedRemoteChange(nameof(SyncEntityType.Session), "s-remote", SyncOperation.Create, payload, "device-A");
        (await engine.RunOnceAsync()).Applied.Should().Be(1);

        // 同一实体再次抵达（换一个 change_id，模拟重复 Pull / 服务端重放）
        mock.SeedRemoteChange(nameof(SyncEntityType.Session), "s-remote", SyncOperation.Create, payload, "device-A");
        await engine.RunOnceAsync();

        Scalar<int>("SELECT COUNT(*) FROM sessions WHERE global_id = 's-remote';").Should().Be(1,
            "重复 Pull 绝不能产生第二条会话（否则时长会双记）");

        // 2e 侧同样幂等
        var summary = await _repo.ReconcileDeferredChangesAsync();
        summary.Resolved.Should().BeGreaterThan(0);
        summary.Resolutions.Should().OnlyContain(r => r == ReconciliationResolutions.AlreadyPresent,
            "同 global_id 已存在 → already_present");
        Scalar<int>("SELECT COUNT(*) FROM sessions WHERE global_id = 's-remote';").Should().Be(1);
    }

    [Fact]
    public async Task SessionReconcile_NeverAltersCompletedFacts()
    {
        var gameId = InsertGame("steam", "123456", "Foo", globalId: "game-G");
        var sessionId = InsertSession(gameId, "s-local", "device-A", "2026-10-01T10:00:00Z", 600);

        var outcome = await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-remote", "game-G", "device-A", "2026-10-01T10:00:00Z", 600));

        outcome.Result.Should().Be(SessionReconcileResult.Converged);

        // 已完成的会话是历史事实：只允许补 identity，不允许改写任何事实
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE id = @id;", new { id = sessionId }).Should().Be(600);
        Scalar<string>("SELECT started_at_utc FROM sessions WHERE id = @id;", new { id = sessionId })
            .Should().Be("2026-10-01T10:00:00Z");
        Scalar<string>("SELECT ended_at_utc FROM sessions WHERE id = @id;", new { id = sessionId })
            .Should().Be("2026-10-01T10:00:00Z");
        Scalar<string>("SELECT process_name FROM sessions WHERE id = @id;", new { id = sessionId }).Should().Be("foo.exe");
        Scalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(1, "收敛 ≠ 复制一份");
    }

    [Fact]
    public async Task SessionReconcile_NeverWritesTombstone()
    {
        var gameId = InsertGame("steam", "123456", "Foo", globalId: "game-G");
        InsertSession(gameId, "s-local", "device-A", "2026-10-01T10:00:00Z", 600);

        await _repo.ReconcileRemoteSessionAsync(
            RemoteSession("s-remote", "game-G", "device-A", "2026-10-01T10:00:00Z", 600));

        TombstoneCount().Should().Be(0, "会话收敛是 identity 收敛，不是删除 —— 绝不能产生墓碑");
    }

    [Fact]
    public async Task RemoteSessionApply_NeverTouchesDailySummary()
    {
        var gameId = InsertGame("steam", "123456", "Foo", globalId: "game-G");
        InsertDailySummary(gameId, "2026-10-01", 3600, 1);

        // 用 2c 的真实落地路径把一条远端会话落到本地
        var mock = new MockSyncBackend();
        var engine = new DeviceSyncEngine(_repo, mock);
        mock.SeedRemoteChange(nameof(SyncEntityType.Session), "s-new", SyncOperation.Create,
            RemoteSession("s-new", "game-G", "device-B", "2026-10-01T14:00:00Z", 900).Payload, "device-B");

        (await engine.RunOnceAsync()).Applied.Should().Be(1);
        Scalar<int>("SELECT COUNT(*) FROM sessions WHERE global_id = 's-new';").Should().Be(1);

        // daily_summary 是**另一条同步线**（本阶段两端都不同步），
        // 绝不能因为落了远端会话就被改写 —— 否则总量会双记
        Scalar<int>("SELECT duration_seconds FROM daily_summary;").Should().Be(3600);
        Scalar<int>("SELECT session_count FROM daily_summary;").Should().Be(1);
    }

    // ==================== Transaction：失败必须整体回滚 ====================

    [Fact]
    public async Task GameReconcileFailure_RollsBackEverything()
    {
        var localId = InsertGame("steam", "123456", "Foo", notion: "page-local", globalId: "local-A");
        SeedDeferredRow("chg-rollback", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity,
            RemoteGame("remote-B", "steam", "123456", notion: "page-local").Payload);

        // 在"写 supersession"这一步注入失败 —— 这是对账里真正的写入点
        Exec("""
            CREATE TRIGGER trg_fail_supersession BEFORE INSERT ON sync_identity_supersessions
            BEGIN SELECT RAISE(ABORT, 'injected'); END;
            """);

        // 异常是被吞成 Deferred 还是抛给调用方，本测试不做断言（见报告）；
        // 这里要钉住的是：**绝不允许留下半完成状态**。
        var summary = await Record.ExceptionAsync(() => _repo.ReconcileDeferredChangesAsync());

        SupersessionCount().Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(1);
        Scalar<string>("SELECT global_id FROM games WHERE id = @id;", new { id = localId }).Should().Be("local-A");
        Scalar<string>("SELECT notion_page_id FROM games WHERE id = @id;", new { id = localId }).Should().Be("page-local");
        Scalar<string>("SELECT COALESCE(resolved_at_utc, '') FROM sync_deferred_changes WHERE change_id = 'chg-rollback';")
            .Should().BeNullOrEmpty("写入失败时台账必须保持 unresolved");

        // 去掉故障后重跑 → 正常收敛（且不重复写入）
        Exec("DROP TRIGGER trg_fail_supersession;");
        var retry = await _repo.ReconcileDeferredChangesAsync();
        retry.Resolved.Should().Be(1);
        SupersessionCount().Should().Be(1, "重跑不得堆积第二套 supersession");
    }

    [Fact]
    public async Task SessionReconcileFailure_RollsBackEverything()
    {
        var gameId = InsertGame("steam", "123456", "Foo", globalId: "game-G");
        var sessionId = InsertSession(gameId, "s-local", "device-A", "2026-10-01T10:00:00Z", 600);
        SeedDeferredRow("chg-session-rollback", nameof(SyncEntityType.Session), "s-remote",
            SessionIdentityDecisionReasons.SameImmutableFact,
            RemoteSession("s-remote", "game-G", "device-A", "2026-10-01T10:00:00Z", 600).Payload);

        Exec("""
            CREATE TRIGGER trg_fail_session_supersession BEFORE INSERT ON sync_identity_supersessions
            BEGIN SELECT RAISE(ABORT, 'injected'); END;
            """);

        await Record.ExceptionAsync(() => _repo.ReconcileDeferredChangesAsync());

        SupersessionCount().Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(1);
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE id = @id;", new { id = sessionId }).Should().Be(600);
        Scalar<string>("SELECT COALESCE(resolved_at_utc, '') FROM sync_deferred_changes WHERE change_id = 'chg-session-rollback';")
            .Should().BeNullOrEmpty();

        Exec("DROP TRIGGER trg_fail_session_supersession;");
        (await _repo.ReconcileDeferredChangesAsync()).Resolved.Should().Be(1);
        SupersessionCount().Should().Be(1);
    }

    [Fact]
    public async Task LedgerResolutionWriteFailure_LeavesConsistentState_AndConvergesOnRetry()
    {
        var localId = InsertGame("steam", "123456", "Foo", globalId: "local-A");
        SeedDeferredRow("chg-ledger", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity,
            RemoteGame("remote-B", "steam", "123456").Payload);

        // 台账写结论这一步失败：身份对账本身已经提交，但"已解决"标记写不进去。
        // 这不是半完成状态 —— 它必须能在下一次调用时收敛，且绝不重复写入。
        Exec("""
            CREATE TRIGGER trg_fail_ledger BEFORE UPDATE ON sync_deferred_changes
            BEGIN SELECT RAISE(ABORT, 'injected'); END;
            """);

        await Record.ExceptionAsync(() => _repo.ReconcileDeferredChangesAsync());

        Scalar<string>("SELECT COALESCE(resolved_at_utc, '') FROM sync_deferred_changes WHERE change_id = 'chg-ledger';")
            .Should().BeNullOrEmpty("标记写失败就必须还是 unresolved —— 不能假装已解决");
        SupersessionCount().Should().Be(1, "身份对账本身是完整的，不是半截");
        Scalar<string>("SELECT global_id FROM games WHERE id = @id;", new { id = localId }).Should().Be("local-A");

        Exec("DROP TRIGGER trg_fail_ledger;");
        var retry = await _repo.ReconcileDeferredChangesAsync();

        retry.Resolved.Should().Be(1, "重跑必须收敛");
        Scalar<string>("SELECT COALESCE(resolved_at_utc, '') FROM sync_deferred_changes WHERE change_id = 'chg-ledger';")
            .Should().NotBeNullOrEmpty();
        SupersessionCount().Should().Be(1, "重跑不得堆积第二套 supersession");
    }

    [Fact]
    public async Task EndSessionWithOutbox_StillWorks_AndReconciliationNeverDisturbsIt()
    {
        var gameId = InsertGame("steam", "123456", "Foo", globalId: "game-G");
        var sessionId = InsertSession(gameId, "s-live", "device-A", "2026-10-03T09:00:00Z", 0, active: true);

        _ = await _repo.EndSessionWithOutboxAsync(
            sessionId, new DateTime(2026, 10, 3, 9, 30, 0), 1800, Array.Empty<DailyDurationDelta>());

        // 2d 的原子路径特征：会话落结束 + 同步字段补齐 + Outbox 快照入队
        Scalar<int>("SELECT is_active FROM sessions WHERE id = @id;", new { id = sessionId }).Should().Be(0);
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE id = @id;", new { id = sessionId }).Should().Be(1800);
        var sessionGlobalId = Scalar<string>("SELECT global_id FROM sessions WHERE id = @id;", new { id = sessionId });
        sessionGlobalId.Should().NotBeNullOrEmpty("结束这一刻必须补齐同步身份");
        Scalar<int>("SELECT COUNT(*) FROM sync_queue WHERE entity_type = 'Session' AND entity_global_id = @gid;",
            new { gid = sessionGlobalId }).Should().Be(1);

        var payloadBefore = Scalar<string>("SELECT payload FROM sync_queue WHERE entity_global_id = @gid;",
            new { gid = sessionGlobalId });
        var queuedBefore = Scalar<int>("SELECT COUNT(*) FROM sync_queue;");
        Scalar<int>("SELECT COUNT(*) FROM sync_queue;").Should().Be(queuedBefore);
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE id = @id;", new { id = sessionId }).Should().Be(1800);

        // 跑一轮 2e 重处理（含一条会收敛的台账记录）→ 绝不能碰 2d 写下的东西
        SeedDeferredRow("chg-regress", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity, payload: null);
        await _repo.ReconcileDeferredChangesAsync();

        Scalar<int>("SELECT is_active FROM sessions WHERE id = @id;", new { id = sessionId }).Should().Be(0);
        Scalar<int>("SELECT duration_seconds FROM sessions WHERE id = @id;", new { id = sessionId }).Should().Be(1800);
        Scalar<int>("SELECT COUNT(*) FROM sync_queue WHERE entity_global_id = @gid;", new { gid = sessionGlobalId })
            .Should().Be(1, "已入队的同步任务绝不能被对账动到");
        Scalar<string>("SELECT payload FROM sync_queue WHERE entity_global_id = @gid;", new { gid = sessionGlobalId })
            .Should().Be(payloadBefore, "payload 是入队那一刻的快照，之后不得被改写");
    }

    // ==================== Sync：不碰 Backend / 不推游标 / 台账只增不删 ====================

    [Fact]
    public async Task DeferredRerun_NeverTouchesBackendOrCursorOrDeletesLedger()
    {
        InsertGame("steam", "123456", "Foo", globalId: "local-A");

        Exec("""
            INSERT INTO sync_state (backend, account_id, cursor, last_sync_at_utc)
            VALUES ('mock', 'acct-1', '42', '2026-10-01T00:00:00Z');
            """);

        SeedDeferredRow("chg-resolvable", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity,
            RemoteGame("remote-B", "steam", "123456").Payload);

        var mock = new MockSyncBackend();   // 故意不参与任何引擎调用

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.Resolved.Should().Be(1);

        // 离线重处理：完全不访问 Backend
        mock.PushCallCount.Should().Be(0);
        mock.PullCallCount.Should().Be(0);

        // 不推进 Pull 游标（游标只在 2c 的"数据 + 游标同事务"里动）
        Scalar<string>("SELECT cursor FROM sync_state WHERE backend = 'mock';").Should().Be("42");

        // 台账只增不删
        Scalar<int>("SELECT COUNT(*) FROM sync_deferred_changes;").Should().Be(1);
    }

    [Fact]
    public async Task AlreadyResolvedRow_IsNeverOverwritten()
    {
        InsertGame("steam", "123456", "Foo", globalId: "local-A");

        // 一条"早就解决过"的记录，结论是 legacy_no_payload；
        // 但它的 payload 其实**足以**走出一条完全不同的结论（merged_local_identity）。
        SeedDeferredRow("chg-done", nameof(SyncEntityType.Game), "remote-B",
            DeferredReasons.BusinessKeyTakenByOtherIdentity,
            RemoteGame("remote-B", "steam", "123456").Payload,
            resolvedAt: "2026-10-02T00:00:00Z",
            resolution: ReconciliationResolutions.LegacyNoPayload);

        var summary = await _repo.ReconcileDeferredChangesAsync();

        summary.Scanned.Should().Be(0, "已有结论的记录不再进入重处理范围");
        Scalar<string>("SELECT resolution FROM sync_deferred_changes WHERE change_id = 'chg-done';")
            .Should().Be(ReconciliationResolutions.LegacyNoPayload, "审计结论绝不能被第二次覆盖");
        Scalar<string>("SELECT resolved_at_utc FROM sync_deferred_changes WHERE change_id = 'chg-done';")
            .Should().Be("2026-10-02T00:00:00Z");
        SupersessionCount().Should().Be(0, "不重处理 = 不写入任何东西");
    }
}
