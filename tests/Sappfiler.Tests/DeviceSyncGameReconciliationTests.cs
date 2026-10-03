using Dapper;
using FluentAssertions;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 2e 步骤 5 验收：**Game 身份对账**（把远端 Game 安全收敛到本地 canonical Game）。
///
/// 用户点名的 5 条：
///   ① canonical 的 global_id 以**本地**为准，绝不因远端 payload 看起来更新而覆盖；
///   ② 不删 canonical、不写 deletion tombstone（合并 ≠ 删除）；
///   ③ ignored 在**落库层**再保护一次（两层防线）；
///   ④ **不动** canonical 的 executable / executable_path（保护 GetGameByPathOrExeAsync 监听链）；
///   ⑤ **不覆盖** canonical 的 Notion Page ID；双方页面不同 → 延后。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public class DeviceSyncGameReconciliationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;

    public DeviceSyncGameReconciliationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sappfiler_reconcile_{Guid.NewGuid():N}.db");
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

    private int InsertGame(string platform, string platformId, string name,
        string? exe = null, string? exePath = null, string? notion = null,
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

    private static SyncChange RemoteGame(string globalId, string platform, string platformId,
        string name = "Foo", string? exe = "foo.exe", string? exePath = @"E:\SteamLibrary\foo\foo.exe",
        string? notion = null)
        => new()
        {
            ChangeId = "chg-" + Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Game),
            EntityGlobalId = globalId,
            Operation = SyncOperation.Create,
            Payload = SyncPayloads.Serialize(new GameSyncPayload
            {
                GlobalId = globalId,
                Name = name,
                Platform = platform,
                PlatformId = platformId,
                Executable = exe ?? string.Empty,
                ExecutablePath = exePath ?? string.Empty,
                NotionPageId = notion
            }),
            DeviceId = "device-B",
            CreatedAtUtc = "2026-10-03T00:00:00Z"
        };

    // ==================== ① + ② 保留本地 identity / 不写墓碑 ====================

    [Fact]
    public async Task Matched_KeepsLocalGlobalId_AndRecordsSupersession_WithoutTombstone()
    {
        var localId = InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe", globalId: "local-A");

        var outcome = await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456"));

        outcome.Result.Should().Be(GameReconcileResult.Merged);
        outcome.CanonicalLocalId.Should().Be(localId);
        outcome.CanonicalGlobalId.Should().Be("local-A", "① canonical 的 identity 以本地为准");
        outcome.Level.Should().Be(IdentityMatchLevel.StrongKey);

        // ② 身份合并 ≠ 删除
        Scalar<int>("SELECT COUNT(*) FROM sync_tombstones;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(1);
        Scalar<string>("SELECT superseded_global_id FROM sync_identity_supersessions;").Should().Be("remote-B");
        Scalar<string>("SELECT canonical_global_id FROM sync_identity_supersessions;").Should().Be("local-A");

        // canonical 行仍在，且 identity 未被改写
        Scalar<string>("SELECT global_id FROM games WHERE id = @id;", new { id = localId }).Should().Be("local-A");
    }

    [Fact]
    public async Task Matched_WhenCanonicalHasNoIdentity_InheritsRemoteIdentity()
    {
        var localId = InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe", globalId: null);

        var outcome = await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456"));

        outcome.Result.Should().Be(GameReconcileResult.IdentityContinued,
            "本地没有 identity 时继承远端的（同一实体延续），而不是丢掉 identity");
        Scalar<string>("SELECT global_id FROM games WHERE id = @id;", new { id = localId }).Should().Be("remote-B");
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0,
            "身份被延续 = 没有第二个 identity 需要合并");
    }

    // ==================== ④ 不动 executable / executable_path ====================

    [Fact]
    public async Task Matched_DoesNotTouchExecutableOrPath()
    {
        var localId = InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe", globalId: "local-A");

        await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456",
            exe: "foo.exe", exePath: @"E:\SteamLibrary\foo\foo.exe"));

        using var conn = Open();
        var row = conn.QuerySingle<(string Exe, string Path, string? Notion)>(
            "SELECT executable, executable_path, notion_page_id FROM games WHERE id = @id;", new { id = localId });

        row.Exe.Should().Be("foo.exe");
        row.Path.Should().Be(@"D:\Steam\foo.exe", "④ 身份对账不改写本地运行识别所依赖的路径");
        row.Notion.Should().BeNull();
    }

    // ==================== ⑤ Notion Page ID 不被覆盖 / 冲突则延后 ====================

    [Fact]
    public async Task NotionPageConflict_IsDeferred_AndLocalPageIdIsNotOverwritten()
    {
        var localId = InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe",
            notion: "page-local", globalId: "local-A");

        var outcome = await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456",
            notion: "page-remote"));

        outcome.Result.Should().Be(GameReconcileResult.Deferred, "⑤ 双方绑定不同页面 → 绝不替用户自动选一个");
        outcome.Reason.Should().Be(IdentityDecisionReasons.NotionPageConflict);

        Scalar<string>("SELECT notion_page_id FROM games WHERE id = @id;", new { id = localId })
            .Should().Be("page-local", "⑤ Provider identity 不被覆盖");
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
    }

    [Fact]
    public async Task SameNotionPage_AllowsMerge_AndKeepsLocalPageId()
    {
        var localId = InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe",
            notion: "page-same", globalId: "local-A");

        var outcome = await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456",
            notion: "page-same"));

        outcome.Result.Should().Be(GameReconcileResult.Merged);
        Scalar<string>("SELECT notion_page_id FROM games WHERE id = @id;", new { id = localId })
            .Should().Be("page-same");
    }

    // ==================== ③ ignored：两层防线 ====================

    [Fact]
    public async Task IgnoredCanonical_IsBlocked_AndNothingIsWritten()
    {
        var localId = InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe",
            globalId: "local-A", status: "ignored");

        var outcome = await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456"));

        outcome.Result.Should().Be(GameReconcileResult.Blocked);
        outcome.Reason.Should().Be(IdentityDecisionReasons.LocalIgnored);

        // 保持 ignored、不复活、不改状态、不创建替代 Game、不写任何记录
        Scalar<string>("SELECT status FROM games WHERE id = @id;", new { id = localId }).Should().Be("ignored");
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(1, "不得自动创建替代 Game");
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sync_tombstones;").Should().Be(0);
    }

    // ==================== 强键冲突 / 无候选 ====================

    [Fact]
    public async Task StrongKeyConflict_IsBlocked_AndNothingIsWritten()
    {
        InsertGame("steam", "999999", "Foo", "foo.exe", @"D:\Steam\foo.exe", globalId: "local-A");

        var outcome = await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456",
            exePath: @"D:\Steam\foo.exe"));

        outcome.Result.Should().Be(GameReconcileResult.Blocked);
        outcome.Reason.Should().Be(IdentityDecisionReasons.StrongKeyConflict);
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
    }

    [Fact]
    public async Task NoLocalCandidate_IsReported_AndNothingIsWritten()
    {
        var outcome = await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456"));

        outcome.Result.Should().Be(GameReconcileResult.NoLocalCandidate);
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(0, "本步骤不负责插入远端新游戏");
    }

    // ==================== 形态 2：远端 identity 在本地也已落地成一行 ====================

    [Fact]
    public async Task WhenRemoteIdentityAlreadyExistsLocally_ItIsFoldedIntoCanonical()
    {
        // 本地同时存在权威行 A（强键）与早先同步进来的 B 行
        var canonicalId = InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe", globalId: "local-A");
        var redundantId = InsertGame("manual", "999", "Foo", "old.exe", @"E:\Old\old.exe", globalId: "remote-B");

        using (var conn = Open())
        {
            conn.Execute("""
                INSERT INTO sessions (game_id, pid, process_name, start_time, last_heartbeat, duration_seconds, is_active)
                VALUES (@g, 1234, 'old.exe', '2026-10-01 10:00:00', '2026-10-01 10:00:00', 600, 0);

                INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
                VALUES ('2026-10-01', @g, 600, 10, 1, 'pending');
                """, new { g = redundantId });
        }

        var outcome = await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456",
            exe: "foo.exe", exePath: @"D:\Steam\foo.exe"));

        outcome.Result.Should().Be(GameReconcileResult.Merged);
        outcome.CanonicalLocalId.Should().Be(canonicalId);

        // 冗余行被折叠（数据搬走、行删除），但**不写 deletion tombstone**
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(1);
        Scalar<int>("SELECT COUNT(*) FROM games WHERE id = @id;", new { id = redundantId }).Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sync_tombstones;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(1);

        // 数据迁移到 canonical
        Scalar<int>("SELECT COUNT(*) FROM sessions WHERE game_id = @g;", new { g = canonicalId }).Should().Be(1);
        Scalar<int>("SELECT duration_seconds FROM daily_summary WHERE game_id = @g;", new { g = canonicalId }).Should().Be(600);

        // ④⑤ canonical 自己的 exe/path/notion 一律照旧
        using var verify = Open();
        var row = verify.QuerySingle<(string Exe, string Path)>(
            "SELECT executable, executable_path FROM games WHERE id = @id;", new { id = canonicalId });
        row.Exe.Should().Be("foo.exe");
        row.Path.Should().Be(@"D:\Steam\foo.exe", "不因为折叠了另一行就改写 canonical 的运行路径");
    }

    // ==================== 幂等 ====================

    [Fact]
    public async Task RepeatedReconcile_IsIdempotent()
    {
        InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe", globalId: "local-A");
        var change = RemoteGame("remote-B", "steam", "123456");

        (await _repo.ReconcileRemoteGameAsync(change)).Result.Should().Be(GameReconcileResult.Merged);
        (await _repo.ReconcileRemoteGameAsync(change)).Result.Should().Be(GameReconcileResult.AlreadyKnown);

        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(1, "重复对账不得堆积记录");
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(1);
    }

    // ==================== 原子性：任一步失败整体回滚 ====================

    [Fact]
    public async Task WhenWriteFails_EverythingRollsBack()
    {
        var canonicalId = InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe", globalId: "local-A");
        var redundantId = InsertGame("manual", "999", "Foo", "old.exe", @"E:\Old\old.exe", globalId: "remote-B");

        using (var conn = Open())
        {
            conn.Execute("""
                INSERT INTO sessions (game_id, pid, process_name, start_time, last_heartbeat, duration_seconds, is_active)
                VALUES (@g, 1234, 'old.exe', '2026-10-01 10:00:00', '2026-10-01 10:00:00', 600, 0);
                """, new { g = redundantId });

            // 让事务的最后一步（写 supersession）失败
            conn.Execute("""
                CREATE TRIGGER abort_supersession BEFORE INSERT ON sync_identity_supersessions
                BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
                """);
        }

        var outcome = await _repo.ReconcileRemoteGameAsync(RemoteGame("remote-B", "steam", "123456",
            exe: "foo.exe", exePath: @"D:\Steam\foo.exe"));

        outcome.Result.Should().Be(GameReconcileResult.Deferred, "失败要如实返回，不能假装合并成功");

        // 整体回滚：冗余行还在、会话没被搬走
        Scalar<int>("SELECT COUNT(*) FROM games;").Should().Be(2);
        Scalar<int>("SELECT COUNT(*) FROM games WHERE id = @id;", new { id = redundantId }).Should().Be(1);
        Scalar<int>("SELECT COUNT(*) FROM sessions WHERE game_id = @g;", new { g = redundantId }).Should().Be(1);
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
        Scalar<string>("SELECT global_id FROM games WHERE id = @id;", new { id = canonicalId }).Should().Be("local-A");
    }

    // ==================== 删除类变更不归对账管 ====================

    [Fact]
    public async Task DeleteChange_IsNotHandledByIdentityReconciliation()
    {
        InsertGame("steam", "123456", "Foo", "foo.exe", @"D:\Steam\foo.exe", globalId: "local-A");

        var deleteChange = RemoteGame("remote-B", "steam", "123456");
        deleteChange.Operation = SyncOperation.Delete;

        var outcome = await _repo.ReconcileRemoteGameAsync(deleteChange);

        outcome.Result.Should().Be(GameReconcileResult.Deferred);
        Scalar<int>("SELECT COUNT(*) FROM sync_identity_supersessions;").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM sync_tombstones;").Should().Be(0, "删除走墓碑路径，不由对账代劳");
    }
}
