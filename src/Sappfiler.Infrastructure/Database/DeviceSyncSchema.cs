using System.Globalization;
using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// 多设备同步的表结构与一次性迁移（Phase 1）。
///
/// **设计约束**（本分支要与 `feature/multi-backend-sync` 共存、将来都合并进 main）：
///   · 所有 DDL 集中在**本文件**，<c>SqliteRepository.InitializeDatabase()</c> 里只加**一行调用** ——
///     把"两边都改同一个方法"的冲突面压缩到那一行。
///   · 只做**追加**：不重建表、不改主键、不改现有列的语义、不动 daily_summary。
///   · 全部语句幂等，可重复执行；任何失败都**不得影响现有本地功能**。
///
/// **本阶段不做的事**：不同步 daily_summary、不碰 Cloudflare/WebDAV/UI、不写任何网络代码。
/// </summary>
internal static class DeviceSyncSchema
{
    /// <summary>
    /// 在 <c>InitializeDatabase()</c> 末尾调用（此时 games/sessions/settings/... 已建好）。
    /// </summary>
    public static void Ensure(SqliteConnection conn)
    {
        try
        {
            EnsureSyncTables(conn);
            EnsureSessionsSyncColumns(conn);

            // migration 002：games.global_id。
            // DDL 的主要调用点其实在「启动去重**之前**」（见 SqliteRepository.InitializeDatabase），
            // 这里再调一次只是保证幂等完备 —— 将来那行若被挪走，也不会漏建列。
            EnsureGamesIdentityColumn(conn);
            BackfillGameSyncIdentity(conn);

            EnsureLocalDevice(conn);
        }
        catch (Exception ex)
        {
            // 迁移失败绝不能让程序起不来：同步是附加能力，本地计时/统计必须照常工作。
            AppLog.Error("[设备同步] 表结构初始化失败，本次跳过（本地功能不受影响）", ex);
        }
    }

    /// <summary>
    /// 启动**去重之前**必须就绪的部分：同步元数据表（去重要写 <c>sync_tombstones</c>）、
    /// <c>games.global_id</c>（去重要读它来决定 identity 是继承还是丢弃）、
    /// 本机设备身份（墓碑要记 <c>device_id</c>）。
    ///
    /// ⚠️ 为什么单独开一个入口：默认的 <see cref="Ensure"/> 跑在去重**之后**，而：
    ///   · 去重需要读 <c>games.global_id</c> —— 列不存在时 Dapper 的 <c>SELECT *</c> 会**静默忽略**
    ///     （不抛异常）→ 继承/墓碑逻辑无声失效，日志里也看不出来；
    ///   · 去重需要写 <c>sync_tombstones</c> —— 表不存在会直接抛异常 → 整次去重被外层 catch 跳过。
    ///
    /// 全部幂等，且**失败不抛**：返回 false 表示同步身份基础设施没准备好，
    /// 此时去重必须退回"纯旧行为"（不继承 global_id、不写墓碑），绝不能让去重本身失效。
    /// </summary>
    public static bool EnsurePrerequisitesForDeduplication(SqliteConnection conn)
    {
        try
        {
            EnsureSyncTables(conn);
            EnsureGamesIdentityColumn(conn);
            EnsureLocalDevice(conn);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 去重前置的同步基础设施未就绪，本次不处理 identity/墓碑（去重照常进行）：{ex.Message}");
            return false;
        }
    }

    /// <summary>创建同步元数据表（全部 IF NOT EXISTS，很轻，每次启动都跑没负担）。</summary>
    private static void EnsureSyncTables(SqliteConnection conn)
    {
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version        INTEGER PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS devices (
                device_id        TEXT PRIMARY KEY,
                device_name      TEXT NOT NULL DEFAULT 'Windows Computer',
                platform         TEXT NOT NULL DEFAULT 'windows',
                app_version      TEXT NOT NULL DEFAULT '',
                created_at_utc   TEXT NOT NULL,
                last_seen_at_utc TEXT,
                last_sync_at_utc TEXT,
                status           INTEGER NOT NULL DEFAULT 0,
                is_this_device   INTEGER NOT NULL DEFAULT 0
            );

            -- Outbox：只存"还没成功送出去"的变更；收到 ACK 后置 completed 并定期清理。
            -- payload 是**变更发生那一刻的快照**，不是推送时再去业务表读。
            CREATE TABLE IF NOT EXISTS sync_queue (
                queue_id          TEXT PRIMARY KEY,
                entity_type       TEXT NOT NULL,
                entity_global_id  TEXT NOT NULL,
                operation         INTEGER NOT NULL,
                base_version      INTEGER NOT NULL DEFAULT 0,
                payload           TEXT NOT NULL DEFAULT '',
                created_at_utc    TEXT NOT NULL,
                retry_count       INTEGER NOT NULL DEFAULT 0,
                next_retry_at_utc TEXT,
                status            INTEGER NOT NULL DEFAULT 0,
                last_error        TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_sync_queue_status ON sync_queue(status, next_retry_at_utc);
            CREATE INDEX IF NOT EXISTS idx_sync_queue_entity ON sync_queue(entity_type, entity_global_id);

            -- 删除墓碑：独立于 deleted_archive（后者是本机删除审计，语义不同）。
            CREATE TABLE IF NOT EXISTS sync_tombstones (
                tombstone_id      TEXT PRIMARY KEY,
                entity_type       TEXT NOT NULL,
                entity_global_id  TEXT NOT NULL,
                device_id         TEXT NOT NULL,
                deleted_at_utc    TEXT NOT NULL,
                created_at_utc    TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_sync_tombstones_entity
                ON sync_tombstones(entity_type, entity_global_id);

            -- 每个 Backend 独立维护游标（切换后端时绝不复用）。
            CREATE TABLE IF NOT EXISTS sync_state (
                backend          TEXT NOT NULL,
                account_id       TEXT NOT NULL,
                cursor           TEXT NOT NULL DEFAULT '',
                last_sync_at_utc TEXT,
                last_error       TEXT,
                PRIMARY KEY (backend, account_id)
            );
            """);
    }

    /// <summary>
    /// 给 <c>sessions</c> 追加同步身份列。**不重建表、不改主键**（id 继续做本机内部键）。
    ///
    /// ⚠️ SQLite 的两条硬限制（照通常写法会直接失败，实测确认）：
    ///   1. <c>ALTER TABLE ADD COLUMN</c> **不能**加"NOT NULL 且无默认值"的列；
    ///   2. 也**不能**通过 ADD COLUMN 加 UNIQUE —— 唯一性只能在事后建 UNIQUE INDEX。
    ///   所以这里的做法是：列加成可空 → 回填 → 再建唯一索引；"非空"由应用层保证。
    /// </summary>
    private static void EnsureSessionsSyncColumns(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(sessions);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1)); // 列名
            }
        }

        void AddColumn(string name, string ddl)
        {
            if (existing.Contains(name)) return;
            try
            {
                conn.Execute($"ALTER TABLE sessions ADD COLUMN {ddl};");
            }
            catch (Exception ex)
            {
                // 与项目既有惯例一致：加列失败视为"已存在"，但留日志便于排查。
                AppLog.Warn($"[设备同步] 追加 sessions.{name} 失败（可能已存在）：{ex.Message}");
            }
        }

        AddColumn("global_id", "global_id TEXT");
        AddColumn("device_id", "device_id TEXT");
        AddColumn("version", "version INTEGER NOT NULL DEFAULT 1");
        AddColumn("deleted_at_utc", "deleted_at_utc TEXT");
        AddColumn("started_at_utc", "started_at_utc TEXT");
        AddColumn("ended_at_utc", "ended_at_utc TEXT");
        AddColumn("time_precision", "time_precision TEXT NOT NULL DEFAULT 'exact'");

        conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_sessions_global_id ON sessions(global_id);");

        BackfillSessionSyncFields(conn);
    }

    /// <summary>
    /// 一次性回填：给历史 sessions 生成 <c>global_id</c>、归属本机设备、并把本地时间串近似换算成 UTC。
    ///
    /// ⚠️ **准确性限制（必须如实告知用户，不要假装历史时间很准）**：
    ///   · 既有实现只保存"墙上时间"，**从未保存过时区信息**；这里只能按
    ///     <c>TimeZoneInfo.Local</c> 的**逐日期**偏移（含 DST 规则）反推 → 历史 UTC 是**近似值**。
    ///   · 因此历史行标记 <c>time_precision = 'estimated'</c>；迁移之后新写入的行才是 <c>'exact'</c>。
    ///   · 另外：现有 duration 是 5 秒心跳**增量累加**出来的，start/end 本身就不精确 ——
    ///     这是既有设计的性质，不是本次迁移引入的（心跳周期 5 秒保持不变）。
    ///
    /// 性能与自愈：**不用"只跑一次"的标记**，而是每次启动都只补"缺同步字段"的行。
    ///   · 查询条件是 <c>global_id IS NULL OR ...</c>，可以走 <c>idx_sessions_global_id</c>，
    ///     即使在几十万行的库上也只碰缺失的那部分；
    ///   · 更重要的是**自愈**：Phase 1 还没有接线业务写入路径，此期间新产生的会话
    ///     也会缺 global_id；被"一次性标记"跳过的话它们将**永远无法同步**。
    ///     （2026-10-03 就是被回归测试抓到的：标记置位后，构造仓储之后写入的会话补不上。）
    /// </summary>
    private static void BackfillSessionSyncFields(SqliteConnection conn)
    {
        var deviceId = EnsureLocalDevice(conn);

        var rows = conn.Query<LegacySessionRow>("""
            SELECT id                AS Id,
                   start_time        AS StartTime,
                   end_time          AS EndTime,
                   global_id         AS GlobalId
            FROM sessions
            WHERE global_id      IS NULL OR global_id      = ''
               OR device_id      IS NULL OR device_id      = ''
               OR started_at_utc IS NULL OR started_at_utc = ''
            """).ToList();

        if (rows.Count > 0)
        {
            using var tx = conn.BeginTransaction();
            foreach (var row in rows)
            {
                conn.Execute("""
                    UPDATE sessions
                    SET global_id      = @globalId,
                        device_id      = COALESCE(NULLIF(device_id, ''), @deviceId),
                        started_at_utc = COALESCE(NULLIF(started_at_utc, ''), @startedUtc),
                        ended_at_utc   = CASE
                                             WHEN NULLIF(ended_at_utc, '') IS NULL AND @endedUtc IS NOT NULL
                                             THEN @endedUtc
                                             ELSE ended_at_utc
                                         END,
                        time_precision = @precision
                    WHERE id = @id;
                    """,
                    new
                    {
                        globalId = string.IsNullOrWhiteSpace(row.GlobalId) ? NewId() : row.GlobalId,
                        deviceId,
                        startedUtc = LocalToUtcIso(row.StartTime),
                        endedUtc = LocalToUtcIso(row.EndTime),
                        precision = TimePrecision.Estimated,
                        id = row.Id
                    },
                    tx);
            }
            tx.Commit();

            AppLog.Info($"[设备同步] 已为 {rows.Count} 条历史会话回填 global_id / device_id / UTC 时间（time_precision=estimated）");
        }

        conn.Execute("""
            INSERT INTO schema_migrations (version, applied_at_utc) VALUES (1, @now)
            ON CONFLICT(version) DO NOTHING;
            """, new { now = UtcNowIso() });
    }

    /// <summary>
    /// migration 002（DDL 部分）：<c>games.global_id</c> —— 跨设备的 Game identity。
    ///
    /// ⚠️ **必须在 <c>DeduplicateGamesAndDailySummaries</c> 之前调用**：
    /// 去重要读 global_id 才能决定"继承还是丢弃 + 要不要写墓碑"，
    /// 而 Dapper 的 <c>SELECT *</c> 在列不存在时**静默忽略**（不抛异常）→ 逻辑会无声失效。
    ///
    /// SQLite 兼容性同 sessions（见 <see cref="EnsureSessionsSyncColumns"/>）：
    /// ADD COLUMN 不能加"NOT NULL 且无默认值"的列、也不能加 UNIQUE，
    /// 所以做法是「列可空 → 事后建唯一索引 → 非空由应用层保证」。
    /// SQLite 的唯一索引允许多个 NULL，所以幽灵行可以一直保持 NULL。
    /// </summary>
    public static void EnsureGamesIdentityColumn(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "games", "global_id"))
        {
            try
            {
                conn.Execute("ALTER TABLE games ADD COLUMN global_id TEXT;");
            }
            catch (Exception ex)
            {
                // 与项目既有惯例一致：加列失败视为"已存在"，但留日志便于排查。
                AppLog.Warn($"[设备同步] 追加 games.global_id 失败（可能已存在）：{ex.Message}");
            }
        }

        try
        {
            conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_games_global_id ON games(global_id);");
        }
        catch (Exception ex)
        {
            // ⚠️ 索引失败**绝不能**拖累回填：否则 games 永远拿不到身份，而且没人会察觉。
            AppLog.Warn($"[设备同步] 创建 idx_games_global_id 失败（不影响回填）：{ex.Message}");
        }
    }

    /// <summary>
    /// migration 002（回填部分）：**只给"可同步"的游戏**分配 <c>global_id</c>。
    ///
    /// 为什么只给可同步的：纯幽灵行（无 exe、无 session）永远不该进入同步体系，
    /// 给它们分配身份只会制造无意义的云端墓碑 —— 这就是"纯幽灵行不污染云端"的机制本身。
    /// 判定式复用 <see cref="GameSyncEligibility.SqlPredicate"/>，不另写一份。
    ///
    /// 为什么"每次启动补齐"而不是"一次性标记"：与 sessions 的回填同款**自愈** ——
    /// 期间新出现的可同步游戏（用户手加 exe / Notion 拉取 / 库扫描发现）也必须拿到身份，
    /// 一次性标记会让它们**永远**拿不到 global_id（Phase 1 就是被回归测试抓到过这个坑）。
    ///
    /// 幂等：只更新 <c>global_id</c> 仍为空的行 → 重复执行不会产生第二套 UUID。
    /// 注：不动 <c>updated_at</c> —— 这不是用户可见的修改，没必要平白搅动其他逻辑。
    /// </summary>
    private static void BackfillGameSyncIdentity(SqliteConnection conn)
    {
        var ids = conn.Query<int>($"""
            SELECT games.id
            FROM games
            WHERE NULLIF(TRIM(global_id), '') IS NULL
              AND {GameSyncEligibility.SqlPredicate};
            """).ToList();

        if (ids.Count > 0)
        {
            using var tx = conn.BeginTransaction();
            foreach (var id in ids)
            {
                conn.Execute("""
                    UPDATE games
                    SET global_id = @globalId
                    WHERE id = @id AND NULLIF(TRIM(global_id), '') IS NULL;
                    """,
                    new { globalId = NewId(), id },
                    tx);
            }
            tx.Commit();

            AppLog.Info($"[设备同步] 已为 {ids.Count} 个可同步游戏分配 global_id");
        }

        conn.Execute("""
            INSERT INTO schema_migrations (version, applied_at_utc) VALUES (2, @now)
            ON CONFLICT(version) DO NOTHING;
            """, new { now = UtcNowIso() });
    }

    /// <summary>列是否存在（用于幂等 ALTER TABLE）。</summary>
    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 取得（必要时生成）本机设备 ID，并保证 <c>devices</c> 表里有本机那一行。
    /// device_id 一经生成**永不改变**（用户只能改 device_name）。
    /// </summary>
    public static string EnsureLocalDevice(SqliteConnection conn)
    {
        var existing = conn.QuerySingleOrDefault<string>(
            "SELECT value FROM settings WHERE key = @k",
            new { k = DeviceSyncConstants.SettingKeyDeviceId });

        var deviceId = string.IsNullOrWhiteSpace(existing) ? NewId() : existing!;

        conn.Execute("""
            INSERT INTO settings (key, value) VALUES (@k, @v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = CURRENT_TIMESTAMP;
            """, new { k = DeviceSyncConstants.SettingKeyDeviceId, v = deviceId });

        var now = UtcNowIso();
        var name = string.IsNullOrWhiteSpace(Environment.MachineName) ? "Windows Computer" : Environment.MachineName;

        // 已存在则只刷新"最后出现时间"与本机标记；**不覆盖用户改过的 device_name**。
        conn.Execute("""
            INSERT INTO devices (device_id, device_name, platform, app_version,
                                 created_at_utc, last_seen_at_utc, status, is_this_device)
            VALUES (@id, @name, 'windows', '', @now, @now, 0, 1)
            ON CONFLICT(device_id) DO UPDATE SET
                last_seen_at_utc = excluded.last_seen_at_utc,
                is_this_device   = 1;
            """, new { id = deviceId, name, now });

        return deviceId;
    }

    /// <summary>
    /// 本地"墙上时间"字符串 → ISO-8601 UTC。
    /// 用 TimeZoneInfo 的**逐日期**偏移（比"统一用当前偏移"准：历史行落在夏令时期间也能算对）；
    /// 遇到春季 DST 跳变造成的"不存在时刻"则退回按偏移近似，绝不抛异常。
    /// </summary>
    private static string? LocalToUtcIso(string? localTime)
    {
        if (string.IsNullOrWhiteSpace(localTime)) return null;

        if (!DateTime.TryParse(localTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        try
        {
            if (!TimeZoneInfo.Local.IsInvalidTime(local))
            {
                return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // 落到下面的近似分支
        }

        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return local.Subtract(offset).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static string UtcNowIso()
        => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>生成新的同步实体 ID（UUIDv4，N 格式：32 位十六进制，无连字符）。</summary>
    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>回填查询用的行（只读这几列，避免把 sessions 整表载入内存）。</summary>
    private sealed class LegacySessionRow
    {
        public long Id { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string? GlobalId { get; set; }
    }
}
