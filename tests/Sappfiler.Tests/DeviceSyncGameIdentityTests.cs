using Dapper;
using FluentAssertions;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// Phase 2b 护栏：migration 002（<c>games.global_id</c>）+ 启动去重的 identity / 墓碑一致性。
///
/// 对应冻结契约（用户 2026-10-03 确认）：
///   1. global_id 是跨设备 Game identity（不是 notion_page_id）
///   2. canonical 已有 global_id → 永远保留
///   3. canonical 没有 → 继承 duplicates 中第一个有效值
///   4. 全组都没有 → 保持 NULL，由下次启动自愈分配
///   5. 被删 duplicate 若「删除前 isSyncable」且「global_id 非空」→ 写墓碑
///   6. 墓碑只看 global_id，与 notion_page_id 无关
///   7. 任一步失败 → 整次变更 rollback
///   8. 纯幽灵行：不分配 global_id、不进 outbox、不产生墓碑
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支（csproj 条件自动排除）。
/// </summary>
public class DeviceSyncGameIdentityTests : IDisposable
{
    private readonly string _dbPath;

    public DeviceSyncGameIdentityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sappfiler_gid_{Guid.NewGuid():N}.db");

        // 第一次构造：建表 + migration 002 的 DDL。
        // 必须让 global_id 列先存在，下面的测试才能直接写入"既有 identity"来模拟真实场景。
        _ = new SqliteRepository(_dbPath);
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

    /// <summary>重新构造仓储 = 模拟"重启一次"（触发 prerequisites → 去重 → 自愈回填）。</summary>
    private SqliteRepository Reload() => new(_dbPath);

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

    // ============================ 测试数据工具 ============================

    private void InsertGame(int id, string name, string exe = "", string exePath = "",
                            string? notionPageId = null, string? globalId = null)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO games (id, platform, platform_id, name, executable, executable_path,
                               notion_page_id, status, global_id)
            VALUES (@id, 'steam', @platformId, @name, @exe, @exePath, @notionPageId, 'active', @globalId);
            """,
            new { id, platformId = $"pid-{id}", name, exe, exePath, notionPageId, globalId });
    }

    private void InsertSession(int gameId, int pid, string startTime, int durationSeconds)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO sessions (game_id, pid, process_name, start_time, last_heartbeat,
                                  duration_seconds, is_active)
            VALUES (@gameId, @pid, 'game.exe', @startTime, @startTime, @durationSeconds, 0);
            """,
            new { gameId, pid, startTime, durationSeconds });
    }

    private void InsertDaily(int gameId, string date, int durationSeconds)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
            VALUES (@date, @gameId, @durationSeconds, @minutes, 1, 'pending');
            """,
            new { date, gameId, durationSeconds, minutes = durationSeconds / 60 });
    }

    private sealed class GameRow
    {
        public int Id { get; set; }
        public string? GlobalId { get; set; }
        public string? NotionPageId { get; set; }
        public string? Executable { get; set; }
        public string? ExecutablePath { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class TombstoneRow
    {
        public string EntityType { get; set; } = string.Empty;
        public string EntityGlobalId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
    }

    private List<GameRow> Games()
    {
        using var conn = Open();
        return conn.Query<GameRow>("SELECT * FROM games ORDER BY id;").ToList();
    }

    private List<TombstoneRow> Tombstones()
    {
        using var conn = Open();
        return conn.Query<TombstoneRow>("SELECT * FROM sync_tombstones ORDER BY rowid;").ToList();
    }

    /// <summary>身份合并记录（与"删除墓碑"是两件不同的事，见 2e 冻结语义）。</summary>
    private sealed class SupersessionRow
    {
        public string EntityType { get; set; } = string.Empty;
        public string SupersededGlobalId { get; set; } = string.Empty;
        public string CanonicalGlobalId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
    }

    private List<SupersessionRow> Supersessions()
    {
        using var conn = Open();
        return conn.Query<SupersessionRow>(
            "SELECT * FROM sync_identity_supersessions ORDER BY rowid;").ToList();
    }

    // ==================== 契约 2/3/4：identity 继承 ====================

    [Fact]
    public void Dedup_InheritsGlobalId_WhenCanonicalHasNone()
    {
        // canonical（有真实可执行文件）没有 identity；被合并的那行有一个 identity
        InsertGame(1, "Promenade", exe: "Promenade.exe", exePath: @"E:\p\Promenade.exe");
        InsertGame(2, "Promenade", globalId: "ggg-inherit");

        _ = Reload();

        var games = Games();
        games.Should().ContainSingle();
        games[0].Id.Should().Be(1, "优先保留带真实可执行文件路径的那一行（既有评分规则不变）");
        games[0].GlobalId.Should().Be("ggg-inherit", "契约 3：canonical 没有 identity 时必须继承，绝不能丢");

        Tombstones().Should().BeEmpty("identity 被继承 = 没有消失，写墓碑反而是错的");
    }

    [Fact]
    public void Dedup_KeepsAuthoritativeGlobalId_WhenBothHaveOne()
    {
        // 契约 2：canonical 已有 identity → 永远保留自己的
        InsertGame(1, "Hades", exe: "hades.exe", exePath: @"E:\h\hades.exe", globalId: "ggg-authoritative");
        InsertGame(2, "Hades", globalId: "ggg-other");
        InsertSession(2, 2001, "2026-10-01 10:00:00", 600);

        _ = Reload();

        var games = Games();
        games.Should().ContainSingle();
        games[0].Id.Should().Be(1);
        games[0].GlobalId.Should().Be("ggg-authoritative");
    }

    // ==================== 契约 5：被丢弃 identity 的墓碑 ====================

    [Fact]
    public void Dedup_WhenDiscardedRowWasSyncable_RecordsIdentitySupersession_NotDeletionTombstone()
    {
        InsertGame(1, "Elden", exe: "elden.exe", exePath: @"E:\e\elden.exe", globalId: "ggg-A");
        InsertGame(2, "Elden", globalId: "ggg-B");
        InsertSession(2, 3001, "2026-10-01 11:00:00", 900); // 让第 2 行"删除前 isSyncable"

        _ = Reload();

        Games().Should().ContainSingle().Which.GlobalId.Should().Be("ggg-A");

        // ⚠️ 2e 语义修正（2026-10-03）：被淘汰的 identity 属于**身份合并**，不是**删除**。
        //    写成删除墓碑会让其它设备把"合并"理解成"删除"从而删掉自己的数据。
        Tombstones().Should().BeEmpty("身份合并绝不能写成删除墓碑");

        var supersessions = Supersessions();
        supersessions.Should().ContainSingle("被淘汰且曾可同步的 identity 必须记录它并入了谁");
        supersessions[0].EntityType.Should().Be("Game");
        supersessions[0].SupersededGlobalId.Should().Be("ggg-B");
        supersessions[0].CanonicalGlobalId.Should().Be("ggg-A");
        supersessions[0].DeviceId.Should().NotBeNullOrWhiteSpace("要记清是哪台设备做出的判断");
        supersessions[0].Reason.Should().Be(SupersessionReasons.StartupDedupDuplicateIdentity);
    }

    [Fact]
    public void Dedup_WhenBothHaveDistinctGlobalIds_KeepsOneAndSupersedesTheOther()
    {
        // 契约 5/6 的组合场景：两个不同 identity，且第 2 行即使没有 exe 也有 session → 可同步
        InsertGame(1, "Celeste", exe: "celeste.exe", exePath: @"E:\c\celeste.exe", globalId: "ggg-keep");
        InsertGame(2, "Celeste", globalId: "ggg-drop");
        InsertSession(2, 4001, "2026-10-01 12:00:00", 300);

        _ = Reload();

        Games().Should().ContainSingle().Which.GlobalId.Should().Be("ggg-keep");
        Tombstones().Should().BeEmpty("这不是删除");
        Supersessions().Select(s => s.SupersededGlobalId).Should().Equal("ggg-drop");
        Supersessions().Select(s => s.CanonicalGlobalId).Should().Equal("ggg-keep");
    }

    [Fact]
    public void Dedup_SameNotionPageId_StillRecordsIdentitySupersession()
    {
        // 契约 6 的**关键**场景：两行共用同一个 notion_page_id（同一个云端 Notion 实体），
        // 但 local global_id 不同 → 被丢弃的那个 identity **仍然要写墓碑**。
        // 理由：墓碑多写是幂等无害的，漏写是不可逆的身份残留。
        InsertGame(1, "Ori", exe: "ori.exe", exePath: @"E:\o\ori.exe", notionPageId: "page-shared", globalId: "ggg-A");
        InsertGame(2, "Ori", notionPageId: "page-shared", globalId: "ggg-B");
        InsertSession(2, 5001, "2026-10-01 13:00:00", 420);

        _ = Reload();

        var games = Games();
        games.Should().ContainSingle();
        games[0].GlobalId.Should().Be("ggg-A");
        games[0].NotionPageId.Should().Be("page-shared", "既有 Notion 合并行为不变");

        // 契约 6：identity 只认 global_id，notion_page_id 相同不构成豁免
        Tombstones().Should().BeEmpty("身份合并不是删除");
        Supersessions().Select(s => s.SupersededGlobalId).Should().Equal(new[] { "ggg-B" });
    }

    // ==================== 契约 8：纯幽灵行 ====================

    [Fact]
    public void Dedup_PureGhosts_GetNoIdentityAndNoTombstone()
    {
        InsertGame(1, "Ghost", notionPageId: "page-g");
        InsertGame(2, "Ghost", notionPageId: "page-g");

        _ = Reload();

        var games = Games();
        games.Should().ContainSingle();
        games[0].GlobalId.Should().BeNull("契约 8：纯幽灵行不分配 global_id");
        Tombstones().Should().BeEmpty("契约 8：纯幽灵行不产生墓碑（否则会污染云端）");
        Supersessions().Should().BeEmpty("纯幽灵行也没有身份可合并");
    }

    // ==================== 会话迁移 + 整体回滚 ====================

    [Fact]
    public void Dedup_MigratesAllSessionsToCanonical()
    {
        InsertGame(1, "Terraria", exe: "terraria.exe", exePath: @"E:\t\terraria.exe", globalId: "ggg-A");
        InsertGame(2, "Terraria", globalId: "ggg-B");
        InsertGame(3, "Terraria", globalId: "ggg-C");
        InsertSession(2, 6001, "2026-10-01 14:00:00", 300);
        InsertSession(3, 6002, "2026-10-01 15:00:00", 600);

        _ = Reload();

        Games().Should().ContainSingle().Which.Id.Should().Be(1);

        using var conn = Open();
        // 所有 sessions 必须指向权威行，否则计时数据会挂在已删除的游戏上
        conn.Query<int>("SELECT DISTINCT game_id FROM sessions;").Should().Equal(new[] { 1 });
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sessions;").Should().Be(2, "迁移而不是删除");
    }

    [Fact]
    public void Dedup_WhenAnyStepFails_RollsBackTheWholeMerge()
    {
        // 契约 7：任一步失败 → 整次变更 rollback。
        // 注入方式：加一个 BEFORE DELETE ON games 的触发器让"删除重复行"这一步失败。
        // 若没有事务，就会出现"sessions 已迁移 + games 未删除 + 墓碑没写"的中间态。
        InsertGame(1, "Rollback", exe: "rb.exe", exePath: @"E:\r\rb.exe", globalId: "ggg-A");
        InsertGame(2, "Rollback", globalId: "ggg-B");
        InsertSession(2, 7001, "2026-10-01 16:00:00", 900);
        InsertDaily(2, "2026-10-01", 900);

        using (var conn = Open())
        {
            conn.Execute("""
                CREATE TRIGGER abort_game_delete BEFORE DELETE ON games
                BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
                """);
        }

        // 不应抛出（去重的外层 catch 会记 Warn），但整次变更必须回滚
        _ = Reload();

        Games().Should().HaveCount(2, "删除失败 → canonical 的字段继承也必须一并回滚");

        using var conn2 = Open();
        // sessions 绝不能被「迁移了但游戏没删」地留在半途
        conn2.Query<int>("SELECT DISTINCT game_id FROM sessions;").Should().Equal(new[] { 2 });
        // daily_summary 的合并同样必须回滚
        conn2.Query<int>("SELECT DISTINCT game_id FROM daily_summary;").Should().Equal(new[] { 2 });
        Tombstones().Should().BeEmpty("墓碑不能先于删除写入");
        Supersessions().Should().BeEmpty("身份合并记录同样必须随事务回滚");
        conn2.ExecuteScalar<string>("SELECT global_id FROM games WHERE id = 1;").Should().Be("ggg-A",
            "canonical 的 identity 继承必须回滚");
    }

    // ==================== migration 幂等 ====================

    [Fact]
    public void Migration_RepeatedRuns_DoNotCreateSecondUuidOrIndex()
    {
        InsertGame(1, "Stardew", exe: "sdv.exe", exePath: @"E:\s\sdv.exe");

        _ = Reload();
        var firstId = Games().Single().GlobalId;
        firstId.Should().NotBeNullOrWhiteSpace("可同步游戏必须拿到 identity");

        _ = Reload();
        _ = Reload();

        Games().Single().GlobalId.Should().Be(firstId, "重复执行绝不能产生第二套 UUID");

        using var conn = Open();
        conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM pragma_index_list('games') WHERE name = 'idx_games_global_id';")
            .Should().Be(1, "唯一索引只能有一个");

        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM games WHERE NULLIF(TRIM(global_id), '') IS NOT NULL;")
            .Should().Be(1);

        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM schema_migrations WHERE version = 2;")
            .Should().Be(1, "migration 版本登记必须幂等");

        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM schema_migrations WHERE version = 1;")
            .Should().Be(1, "Phase 1 的版本登记不受影响");
    }

    // ====== 语义分离的硬护栏（2e 冻结：删除 ≠ 身份合并）======

    [Fact]
    public void Dedup_DiscardedIdentity_ProducesSupersessionOnly_AndZeroTombstones()
    {
        // 用户点名的回归：去重淘汰 duplicate identity 之后
        // **墓碑必须为 0**、身份合并记录必须为 1 —— 两个概念不得混用。
        InsertGame(1, "Guard", exe: "guard.exe", exePath: @"E:\g\guard.exe", globalId: "ggg-canonical");
        InsertGame(2, "Guard", globalId: "ggg-victim");
        InsertSession(2, 9001, "2026-10-01 20:00:00", 600);

        _ = Reload();

        Tombstones().Should().BeEmpty("这不是删除，绝不能写删除墓碑");
        var supersessions = Supersessions();
        supersessions.Should().ContainSingle();
        supersessions[0].SupersededGlobalId.Should().Be("ggg-victim");
        supersessions[0].CanonicalGlobalId.Should().Be("ggg-canonical");
    }

    [Fact]
    public async Task DeletionTombstone_And_IdentitySupersession_AreSeparateConcepts()
    {
        // 真删除 → sync_tombstones；身份合并 → sync_identity_supersessions。两者互不干扰。
        var repo = new SqliteRepository(_dbPath);

        using (var conn = Open())
        {
            conn.Execute("""
                INSERT INTO sync_tombstones
                    (tombstone_id, entity_type, entity_global_id, device_id, deleted_at_utc, created_at_utc)
                VALUES ('t-del', 'Game', 'g-deleted', 'dev-1', '2026-10-03T00:00:00Z', '2026-10-03T00:00:00Z');
                """);
        }

        await repo.AddIdentitySupersessionAsync(new SyncIdentitySupersession
        {
            SupersessionId = "s-merge",
            EntityType = nameof(SyncEntityType.Game),
            SupersededGlobalId = "g-merged",
            CanonicalGlobalId = "g-canonical",
            DeviceId = "dev-1",
            Reason = SupersessionReasons.ReconcileStrongKey,
            CreatedAtUtc = "2026-10-03T00:00:00Z"
        });

        Tombstones().Should().ContainSingle().Which.EntityGlobalId.Should().Be("g-deleted");
        Supersessions().Should().ContainSingle().Which.SupersededGlobalId.Should().Be("g-merged");

        // 重定向只认 supersession，绝不认墓碑
        (await repo.ResolveCanonicalGlobalIdAsync(nameof(SyncEntityType.Game), "g-merged"))
            .Should().Be("g-canonical");
        (await repo.ResolveCanonicalGlobalIdAsync(nameof(SyncEntityType.Game), "g-deleted"))
            .Should().BeNull("墓碑表示'被删除'，不是'身份合并'");

        // 幂等：同一身份重复记录只更新依据，不新增行
        await repo.AddIdentitySupersessionAsync(new SyncIdentitySupersession
        {
            SupersessionId = "s-merge-2",
            EntityType = nameof(SyncEntityType.Game),
            SupersededGlobalId = "g-merged",
            CanonicalGlobalId = "g-canonical",
            DeviceId = "dev-1",
            Reason = SupersessionReasons.ReconcileExecutablePath,
            CreatedAtUtc = "2026-10-03T01:00:00Z"
        });

        var after = Supersessions();
        after.Should().ContainSingle();
        after[0].Reason.Should().Be(SupersessionReasons.ReconcileExecutablePath);
    }

    // ==================== 防漂移：SQL 与 C# 判定必须一致 ====================

    [Fact]
    public void SyncabilityRule_SqlAndInMemory_AgreeOnEverySample()
    {
        // 名字各不相同，避免被去重合并（本测试只验证规则，不触发 dedup）
        InsertGame(1, "A-exe-only", exe: "a.exe");
        InsertGame(2, "B-path-only", exePath: @"E:\b\b.exe");
        InsertGame(3, "C-both", exe: "c.exe", exePath: @"E:\c\c.exe");
        InsertGame(4, "D-session-only");
        InsertSession(4, 8001, "2026-10-01 17:00:00", 300);
        InsertGame(5, "E-ghost", notionPageId: "page-e");
        InsertGame(6, "F-blank-exe", exe: "   ");

        using var conn = Open();

        var sqlIds = conn.Query<int>(
            $"SELECT games.id FROM games WHERE {GameSyncEligibility.SqlPredicate} ORDER BY games.id;")
            .ToList();

        var memoryIds = new List<int>();
        foreach (var row in conn.Query<GameRow>("SELECT id, executable, executable_path FROM games ORDER BY id;"))
        {
            var sessionCount = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sessions WHERE game_id = @id;", new { id = row.Id });

            if (GameSyncEligibility.IsSyncable(row.Executable, row.ExecutablePath, sessionCount))
            {
                memoryIds.Add(row.Id);
            }
        }

        sqlIds.Should().Equal(memoryIds,
            "SQL 判定式与内存判定必须给出完全相同的结果，否则规则会漂移");
        sqlIds.Should().Equal(new[] { 1, 2, 3, 4 },
            "只有 exe / path / session 三者之一非空的才算可同步：纯幽灵(5)与纯空白 exe(6)都不算");
    }
}
