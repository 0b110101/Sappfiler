using System.Globalization;
using Dapper;
using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// Phase 1 迁移护栏：在"已经存在单机数据"的库上追加**同步身份**与**同步元数据表**，
/// 必须做到 **零数据丢失、零现有功能回归、可重复执行**。
///
/// ⚠️ 本文件只存在于 `feature/multi-device-sync` 分支（依赖 DeviceSyncModels / DeviceSyncSchema）。
///    它在另两条分支上会被 Sappfiler.Tests.csproj 按"源文件是否存在"自动排除。
/// </summary>
public class DeviceSyncMigrationTests : IDisposable
{
    private readonly string _dbPath;

    public DeviceSyncMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sappfiler_devsync_{Guid.NewGuid():N}.db");
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

    /// <summary>
    /// 造一个"迁移前的老库"：只有 main 上真实存在的 sessions / settings 结构
    /// —— 注意 sessions **完全没有**任何同步列，用来验证 ADD COLUMN + 回填这条路径。
    /// </summary>
    private SqliteConnection SeedLegacySchema()
    {
        var conn = Open();
        conn.Execute("""
            CREATE TABLE settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                game_id INTEGER NOT NULL,
                pid INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                start_time DATETIME NOT NULL,
                end_time DATETIME,
                last_heartbeat DATETIME NOT NULL,
                duration_seconds INTEGER DEFAULT 0,
                is_active INTEGER DEFAULT 1,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            INSERT INTO sessions (game_id, pid, process_name, start_time, end_time, last_heartbeat, duration_seconds, is_active)
            VALUES (1, 4242, 'bg3', '2026-09-30 18:00:00', '2026-09-30 20:00:00', '2026-09-30 20:00:00', 7200, 0);

            INSERT INTO sessions (game_id, pid, process_name, start_time, end_time, last_heartbeat, duration_seconds, is_active)
            VALUES (1, 4243, 'bg3', '2026-10-01 19:00:00', NULL, '2026-10-01 19:30:00', 1800, 1);
            """);
        return conn;
    }

    private sealed class SessionSyncRow
    {
        public long Id { get; set; }
        public string? GlobalId { get; set; }
        public string? DeviceId { get; set; }
        public string? StartedUtc { get; set; }
        public string? EndedUtc { get; set; }
        public string? Precision { get; set; }
    }

    private static List<SessionSyncRow> ReadSyncRows(SqliteConnection conn)
        => conn.Query<SessionSyncRow>("""
            SELECT id           AS Id,
                   global_id    AS GlobalId,
                   device_id    AS DeviceId,
                   started_at_utc AS StartedUtc,
                   ended_at_utc   AS EndedUtc,
                   time_precision AS Precision
            FROM sessions
            ORDER BY id
            """).ToList();

    [Fact]
    public void Ensure_CreatesSyncMetadataTables()
    {
        using var conn = SeedLegacySchema();

        DeviceSyncSchema.Ensure(conn);

        var tables = conn.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table';").ToList();
        tables.Should().Contain(new[]
        {
            "devices", "sync_queue", "sync_tombstones", "sync_state", "schema_migrations"
        });
    }

    [Fact]
    public void Ensure_AddsSessionSyncColumns_WithoutRebuildingTable()
    {
        using var conn = SeedLegacySchema();
        var beforeCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sessions;");

        DeviceSyncSchema.Ensure(conn);

        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('sessions');").ToList();
        columns.Should().Contain(new[] { "id", "game_id", "start_time", "duration_seconds" },
            "原有列一个都不能少");
        columns.Should().Contain(new[]
            {
                "global_id", "device_id", "version", "deleted_at_utc",
                "started_at_utc", "ended_at_utc", "time_precision"
            },
            "同步身份列要补上");

        // 主键仍是 INTEGER（没有为了同步把 id 改成 UUID），行数也没变（不是重建表）
        conn.ExecuteScalar<string>("SELECT typeof(id) FROM sessions LIMIT 1;").Should().Be("integer");
        conn.ExecuteScalar<string>("SELECT typeof(game_id) FROM sessions LIMIT 1;").Should().Be("integer");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sessions;").Should().Be(beforeCount);
    }

    [Fact]
    public void Ensure_BackfillsGlobalId_DeviceId_AndUtcTimes()
    {
        using var conn = SeedLegacySchema();

        DeviceSyncSchema.Ensure(conn);

        var rows = ReadSyncRows(conn);
        rows.Should().HaveCount(2);

        // 每条历史会话都要有**互不相同**的 global_id
        rows.Select(r => r.GlobalId).Should().NotContainNulls().And.OnlyHaveUniqueItems();

        // 全部归属本机设备
        var localDeviceId = conn.QuerySingle<string>("SELECT value FROM settings WHERE key = 'device_id';");
        localDeviceId.Should().NotBeNullOrWhiteSpace();
        rows.Should().OnlyContain(r => r.DeviceId == localDeviceId);

        // 历史时间只能近似换算 → 必须标记 estimated，且能解析为 UTC
        rows.Should().OnlyContain(r => r.Precision == TimePrecision.Estimated);
        rows.Should().OnlyContain(r => r.StartedUtc != null && r.StartedUtc!.EndsWith("Z", StringComparison.Ordinal));
        DateTime.TryParse(rows[0].StartedUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out _).Should().BeTrue();

        // 已结束的会话有 ended_at_utc；仍在进行的那条保持 NULL —— 不要凭空造结束时间
        rows[0].EndedUtc.Should().NotBeNullOrWhiteSpace();
        rows[1].EndedUtc.Should().BeNullOrWhiteSpace();
    }

    [Fact]
    public void Ensure_IsIdempotent_AndKeepsExactlyOneLocalDevice()
    {
        using var conn = SeedLegacySchema();

        DeviceSyncSchema.Ensure(conn);
        var deviceId1 = conn.QuerySingle<string>("SELECT value FROM settings WHERE key = 'device_id';");
        var globalIds1 = ReadSyncRows(conn).Select(r => r.GlobalId).ToList();

        DeviceSyncSchema.Ensure(conn);
        DeviceSyncSchema.Ensure(conn);

        // device_id 一经生成绝不改变；global_id 不会被重新生成
        conn.QuerySingle<string>("SELECT value FROM settings WHERE key = 'device_id';").Should().Be(deviceId1);
        ReadSyncRows(conn).Select(r => r.GlobalId).Should().Equal(globalIds1);

        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM devices;").Should().Be(1);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM devices WHERE is_this_device = 1;").Should().Be(1);

        // 迁移登记必须幂等：重复 Ensure 不得重复登记。
        // ⚠️ 不要断言"总数为 1" —— 迁移条目会随 Phase 增长（2e 起已有 1/2/3/4），
        //    这里只断言"行数 == 去重后的版本数"（即没有重复登记）。
        var migrationRows = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM schema_migrations;");
        var distinctVersions = conn.ExecuteScalar<long>("SELECT COUNT(DISTINCT version) FROM schema_migrations;");
        migrationRows.Should().Be(distinctVersions);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM schema_migrations WHERE version = 1;").Should().Be(1);
    }

    [Fact]
    public void Ensure_DoesNotResurrectDeletion_GlobalIdKeepsUniqueIndex()
    {
        using var conn = SeedLegacySchema();
        DeviceSyncSchema.Ensure(conn);

        var existingGlobalId = conn.QuerySingle<string>("SELECT global_id FROM sessions LIMIT 1;");

        var act = () => conn.Execute("""
            INSERT INTO sessions (game_id, pid, process_name, start_time, last_heartbeat, global_id)
            VALUES (1, 999, 'dup', '2026-10-02 10:00:00', '2026-10-02 10:00:00', @g);
            """, new { g = existingGlobalId });

        act.Should().Throw<SqliteException>("global_id 必须有唯一索引兜底（幂等的最后一道防线）");
    }

    [Fact]
    public async Task RepositoryStartup_MigratesAnExistingDatabase_WithoutLosingData()
    {
        // 1. 模拟"已经正常用了一阵子的机器"：用仓储正常建库并写入真实数据
        var repo = new SqliteRepository(_dbPath);
        var game = await repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "1086940", "Baldur's Gate 3", "bg3.exe", @"D:\Games\bg3.exe"));
        var session = await repo.CreateSessionAsync(game.Id, 4242, "bg3", new DateTime(2026, 9, 30, 18, 0, 0));
        await repo.EndSessionAsync(session.Id, session.StartTime.AddHours(2), 7200);
        await repo.AddSessionDurationToDailyAsync("2026-09-30", game.Id, 7200);

        // 2. 再开一次（走完整启动迁移路径）
        var repo2 = new SqliteRepository(_dbPath);

        (await repo2.GetAllGamesAsync()).Should().ContainSingle(g => g.Name == "Baldur's Gate 3");

        var summaries = await repo2.GetDailySummariesByDateAsync("2026-09-30");
        summaries.Should().ContainSingle("现有汇总数据不能因为迁移而丢失");
        summaries[0].DurationMinutes.Should().Be(120, "现有 duration 语义与数值都不能被改动");

        // 3. 同步列已回填
        using var conn = Open();
        conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sessions WHERE global_id IS NOT NULL AND global_id <> '';").Should().Be(1);
        conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sessions WHERE started_at_utc IS NOT NULL AND started_at_utc <> '';").Should().Be(1);

        var syncRow = ReadSyncRows(conn).Single();
        syncRow.EndedUtc.Should().NotBeNullOrWhiteSpace("正常结束的会话应有结束时间");
        syncRow.Precision.Should().Be(TimePrecision.Estimated);
    }
}
