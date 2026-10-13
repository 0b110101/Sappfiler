using Dapper;
using GameTimeTracker.Core.Backup;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// <b>备份 / 迁移的存储层</b>（2026-10-11）。
///
/// <para>
/// 本文件属于 <see cref="SqliteRepository"/> 的 partial，因此可以使用 <c>_writeLock</c> 与
/// private 的 <c>CreateConnection</c> —— 这正是"迁移合并必须与 <c>daily_summary</c> 重算处于
/// 同一写保护范围内、同一事务中"所要求的（不在事务里做任何网络操作）。
/// </para>
///
/// <para>
/// 两个能力：
/// <list type="number">
///   <item><b>一致性快照</b>（<see cref="CreateConsistentSnapshot"/>）：用 SQLite 官方 Online Backup API
///         在线复制（WAL 活跃写入下依然一致），导出后把快照切回自包含单文件，
///         因此 ZIP 里不会出现 <c>-wal</c>/<c>-shm</c> 伴生文件。</item>
///   <item><b>迁移合并</b>（<see cref="ImportMigrationFactsAsync"/>）：把备份库的**事实**
///         合并进本机（按跨设备身份去重），保留本机 device_id / 凭据 / 同步游标。</item>
/// </list>
/// </para>
/// </summary>
public partial class SqliteRepository
{
    /// <summary>
    /// 导出时必须从快照里剥离的 <c>settings</c> 键 —— 即**认证秘密**。
    ///
    /// <para>
    /// · <c>notion_token</c>：明文 API Token（唯一真相是 settings 表，见设置页写入点）；
    /// · <c>device_sync_webdav_account</c>：WebDAV 账号，属认证凭据的一半（密码本身是 DPAPI 密文，从不入库）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 刻意**保留** <c>game_database_id</c> / <c>daily_database_id</c>（Notion 数据库 ID）：
    /// 它们不是秘密，且恢复备份后重新填一次 Token 就能继续同步既有页面。
    /// </para>
    /// </summary>
    private static readonly string[] CredentialSettingKeys =
    {
        "notion_token",
        "device_sync_webdav_account"
    };

    // =========================================================================================
    // 1) 一致性快照
    // =========================================================================================

    /// <summary>
    /// 用 SQLite 官方 Online Backup API 把当前数据库复制成 <paramref name="targetDbPath"/> 处的
    /// **一致性快照**，并把快照切成自包含单文件（<c>journal_mode=DELETE</c>，不留 <c>-wal</c>/<c>-shm</c>）。
    ///
    /// <para>
    /// ⚠️ **不复制 <c>.db</c>/<c>-wal</c>/<c>-shm</c> 三件套**：那种做法在后台 5 秒心跳持续写入时
    /// 会捕捉到撕裂状态（本仓库既有的"更改存储位置"就是这么复制的，但它依赖"退出后重启"，
    /// 不适用于在线导出）。Backup API 由 SQLite 自身保证页面级一致视图。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 刻意**不获取写锁**：Backup API 本身一致性安全，而拿锁会阻塞心跳达整个复制时长。
    /// </para>
    /// </summary>
    internal void CreateConsistentSnapshot(string targetDbPath)
    {
        if (string.IsNullOrWhiteSpace(targetDbPath))
            throw new ArgumentException("目标路径不能为空", nameof(targetDbPath));

        var full = Path.GetFullPath(targetDbPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        // 目标必须干净：Backup API 会覆盖目标库内容，残留的 -wal 会让下一次打开看到陈旧页面。
        DeleteSqliteSidecarFiles(full);

        // ⚠️ Pooling=false 是**必需**的：连接池会在 Close() 之后继续持有物理文件句柄，
        //    导致紧随其后的步骤（剥离凭据 / 计算 SHA-256 / 打包 ZIP）打不开这个文件。
        using (var source = CreateConnection())
        using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = full,
                   Mode = SqliteOpenMode.ReadWriteCreate,
                   Pooling = false
               }.ToString()))
        {
            dest.Open();
            source.BackupDatabase(dest);

            // 快照必须是**自包含单文件**：ZIP 里不能有伴生 -wal，否则解压后打开会缺页。
            // 从 WAL 切到 DELETE 会做一次完整 checkpoint 并删除 -wal。
            dest.Execute("PRAGMA journal_mode=DELETE;");
            dest.Close();
        }

        // 防御：只清**伴生文件**（-wal / -shm），绝不动快照主文件本身。
        DeleteSqliteSidecarFiles(full);
    }

    /// <summary>
    /// 从快照中剥离认证秘密。返回被删除的行数。
    /// ⚠️ 必须在**快照文件**上执行（绝不碰当前数据库）。
    /// </summary>
    internal static int StripCredentialsFromSnapshot(string snapshotDbPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(snapshotDbPath),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        conn.Open();

        if (!TableExists(conn, "settings")) return 0;

        var removed = conn.Execute(
            "DELETE FROM settings WHERE key IN @keys;",
            new { keys = CredentialSettingKeys });

        // ⚠️ 只 DELETE 是**不够**的：SQLite 删除行之后，明文仍可能留在空闲页里，
        //    用二进制扫描依然能搜到 token（实测确认）。导出前必须 VACUUM 重建文件，
        //    做到"物理上不含凭据"，而不是"逻辑上查不到"。
        if (removed > 0)
        {
            conn.Execute("VACUUM;");
        }

        // 复核：剥离后不得残留任何已知凭据键（"导出不含秘密"必须是可验证的，而不是声称的）。
        var leftover = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM settings WHERE key IN @keys;",
            new { keys = CredentialSettingKeys });

        if (leftover > 0)
            throw new InvalidOperationException($"备份快照仍残留 {leftover} 条凭据设置，已中止导出。");

        return removed;
    }

    // =========================================================================================
    // 2) 只读统计（预览 / 清单）
    // =========================================================================================

    /// <summary>
    /// 只读打开一份数据库文件，读出数据概况、日期范围、schema 最高版本与完整性检查结论。
    /// **纯只读**：用于"校验完成前不修改任何现有数据"。
    /// </summary>
    internal static BackupStatsSnapshot ReadBackupStats(string dbPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        conn.Open();

        var integrity = conn.ExecuteScalar<string?>("PRAGMA integrity_check;");
        var integrityOk = string.Equals(integrity?.Trim(), "ok", StringComparison.OrdinalIgnoreCase);

        var counts = new BackupCounts
        {
            Games = CountRows(conn, "games"),
            Sessions = CountRows(conn, "sessions"),
            DailySummary = CountRows(conn, "daily_summary"),
            DailyAdjustments = CountRows(conn, "daily_duration_adjustments"),
            DailyPlayExclusions = CountRows(conn, "daily_play_exclusions"),
            SyncTombstones = CountRows(conn, "sync_tombstones"),
            SyncSupersessions = CountRows(conn, "sync_identity_supersessions"),
            GameCatalog = CountRows(conn, "game_catalog")
        };

        BackupDateRange? range = null;
        if (counts.DailySummary > 0)
        {
            var min = conn.ExecuteScalar<string?>("SELECT MIN(date) FROM daily_summary;");
            var max = conn.ExecuteScalar<string?>("SELECT MAX(date) FROM daily_summary;");
            if (!string.IsNullOrWhiteSpace(min) && !string.IsNullOrWhiteSpace(max))
                range = new BackupDateRange { Min = min!, Max = max! };
        }

        var schemaMax = conn.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;");

        return new BackupStatsSnapshot(counts, range, schemaMax, integrityOk);
    }

    /// <summary>当前库已应用到的最高 schema 版本（= 本应用支持的最高版本，因为启动时已迁移到最新）。</summary>
    internal int ReadCurrentSchemaMax()
    {
        using var conn = CreateConnection();
        return conn.ExecuteScalar<int>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations;");
    }

    // =========================================================================================
    // 3) 迁移合并（在线、单事务、单写锁）
    // =========================================================================================

    /// <summary>
    /// <b>迁移到新设备</b>：把 <paramref name="sourceDbPath"/>（备份快照）里的**事实**合并进本机。
    ///
    /// <para>
    /// 合并范围（2026-10-11 核实结论驱动）：
    /// <list type="bullet">
    ///   <item><b>导入</b>：<c>games</c>（缺失的）、<c>sessions</c>（已结束）、
    ///         <c>daily_duration_adjustments</c>、<c>daily_play_exclusions</c>、
    ///         <c>sync_tombstones</c>、<c>sync_identity_supersessions</c>；</item>
    ///   <item><b>不导入</b>：<c>device_id</c>、<c>sync_state</c>（游标）、<c>sync_queue</c>、
    ///         <c>sync_deferred_changes</c>、<c>devices</c>、<c>deleted_archive</c>、<c>settings</c>（凭据）；</item>
    ///   <item><b>重算</b>：所有受影响的 <c>(game, 会计日)</c>，用既有
    ///         <c>RefreshDailyRowFromRemainingFactsCore</c>（自动应用删除排除集与受保护残差）
    ///         —— 绝不直接搬运 <c>daily_summary</c>（它是派生缓存）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>不重建 outbox</b>：已核实 Gap Repair 只对 <c>device_id</c> = 本机的
    /// games/sessions/adjustments 补推；exclusions/tombstones/supersessions 本就不在补推范围内。
    /// 迁移进来的事实保留**原设备归属**，本机不代推（尊重既有同步语义），
    /// 而已删除记录不会复活 —— 那依赖**本地墓碑行**拦截远端旧 Create，与队列无关。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 冲突（同一 <c>(platform, platform_id)</c> 在本机已被**另一个** global_id 占用）⇒
    /// **整体不写入**并报告，绝不猜测身份合并。
    /// </para>
    /// </summary>
    internal async Task<BackupImportResult> ImportMigrationFactsAsync(
        string sourceDbPath, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);

        // Commit 之前的任何异常都会让事务整体回滚；Commit 之后只允许**纯内存**操作，
        // 否则"已提交却因日志/构造异常报失败"会误导用户重复导入。
        BackupImportResult result;
        try
        {
            using var src = OpenReadOnly(sourceDbPath);
            using var dst = CreateConnection();
            using var tx = dst.BeginTransaction();

            var source = ReadMigrationSource(src);

            // ---------- ① 冲突检测（**任何写入之前**；有冲突则一个字都不写） ----------
            var conflicts = new List<MigrationConflict>();
            var gameIdMap = new Dictionary<long, long>();
            var gamesReused = 0;

            foreach (var g in source.Games)
            {
                var localId = dst.QuerySingleOrDefault<long?>(
                    "SELECT id FROM games WHERE global_id = @g LIMIT 1;",
                    new { g = g.GlobalId }, tx);

                if (localId is not null)
                {
                    gameIdMap[g.Id] = localId.Value;
                    gamesReused++;
                    continue;
                }

                var taken = dst.QuerySingleOrDefault<LocalGameRow?>(
                    "SELECT id AS Id, COALESCE(global_id, '') AS GlobalId, name AS Name, "
                    + "COALESCE(status, '') AS Status "
                    + "FROM games WHERE platform = @p AND platform_id = @pid LIMIT 1;",
                    new { p = g.Platform, pid = g.PlatformId }, tx);

                if (taken is not null)
                {
                    // ⚠️ 2026-10-12：本地那条若是**同一个跨设备身份**（global_id 相同），就不是身份冲突 ——
                    //    直接复用。它可能处于 archived（用户删过这款游戏）：复用既保证"不会产生
                    //    无法复用的游戏记录"，也避免与 UNIQUE(platform, platform_id) 打架。
                    if (!string.IsNullOrWhiteSpace(taken.GlobalId)
                        && string.Equals(taken.GlobalId, g.GlobalId, StringComparison.Ordinal))
                    {
                        gameIdMap[g.Id] = taken.Id;
                        gamesReused++;
                        continue;
                    }

                    conflicts.Add(new MigrationConflict
                    {
                        Kind = "game_platform_id_taken",
                        Description = $"游戏「{g.Name}」（平台 {g.Platform} / 平台 ID {g.PlatformId}）"
                            + $"在本机已被另一个条目（{taken.Name}，本机 global_id {Short(taken.GlobalId)}"
                            + (string.IsNullOrWhiteSpace(taken.Status) ? "" : $"，状态 {taken.Status}")
                            + "）占用；两条记录属于不同的游戏身份，无法安全自动合并。"
                    });
                }
            }

            if (conflicts.Count > 0)
            {
                tx.Rollback();
                return new BackupImportResult
                {
                    Success = false,
                    Mode = BackupImportMode.Migrate,
                    Conflicts = conflicts,
                    Message = $"检测到 {conflicts.Count} 项无法安全自动合并的冲突，已阻止导入（**未写入任何数据**）。",
                    Error = "conflict"
                };
            }

            // ---------- ② 补齐缺失的 games（先 games，sessions 有 FK 依赖它） ----------
            var gamesImported = 0;
            foreach (var g in source.Games)
            {
                if (gameIdMap.ContainsKey(g.Id)) continue;

                dst.Execute(
                    """
                    INSERT INTO games
                        (platform, platform_id, name, executable, executable_path,
                         notion_page_id, status, global_id, reactivated_at_utc, created_at, updated_at)
                    VALUES
                        (@Platform, @PlatformId, @Name, @Executable, @ExecutablePath,
                         @NotionPageId, @Status, @GlobalId, @ReactivatedAtUtc, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                    """,
                    new
                    {
                        g.Platform,
                        g.PlatformId,
                        g.Name,
                        g.Executable,
                        g.ExecutablePath,
                        g.NotionPageId,
                        Status = string.IsNullOrWhiteSpace(g.Status) ? "active" : g.Status,
                        g.GlobalId,
                        // ⚠️ 2026-10-12：**原样携带**源数据库的重新激活意图（源缺列 / 值为空 ⇒ NULL）。
                        //    绝不用导入时刻伪造 —— 导入是"搬运数据"，不是"用户重新激活这款游戏"。
                        //    （另：这里的 updated_at 写 CURRENT_TIMESTAMP 是既有行为，**不再参与任何仲裁**，
                        //      因此不会像修复前那样让所有导入行免疫更早的远端 Delete。）
                        g.ReactivatedAtUtc
                    }, tx);

                gameIdMap[g.Id] = dst.ExecuteScalar<long>("SELECT last_insert_rowid();", transaction: tx);
                gamesImported++;
            }

            // ---------- ③ 计算受影响日期 + 在**插入事实之前**量出本机既有残差 ----------
            var cutoff = ReadDailyCutoffHour(dst, tx);
            var sessionsToImport = new List<SourceSessionRow>();
            var sessionsSkipped = 0;

            var affected = new HashSet<(int GameId, string Date)>();

            foreach (var s in source.Sessions)
            {
                if (!gameIdMap.TryGetValue(s.GameId, out var dstGameId)) { sessionsSkipped++; continue; }

                var exists = dst.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sessions WHERE global_id = @g;", new { g = s.GlobalId }, tx) > 0;
                if (exists) { sessionsSkipped++; continue; }

                if (!TryParseWallClock(s.StartTime, out var start)) { sessionsSkipped++; continue; }

                sessionsToImport.Add(s);

                var span = Math.Max(0, s.DurationSeconds);
                foreach (var d in GameTimeTracker.Core.Services.GameSessionManager.ComputeDailyDeltas(
                             start, start.AddSeconds(span), span, cutoff))
                {
                    affected.Add(((int)dstGameId, d.Date));
                }
            }

            var adjustmentsToImport = new List<SourceAdjustmentRow>();
            var adjustmentsSkipped = 0;
            foreach (var a in source.Adjustments)
            {
                if (!gameIdMap.TryGetValue(a.GameId, out var dstGameId)) { adjustmentsSkipped++; continue; }

                var exists = dst.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM daily_duration_adjustments WHERE adjustment_global_id = @g;",
                    new { g = a.AdjustmentGlobalId }, tx) > 0;
                if (exists) { adjustmentsSkipped++; continue; }

                adjustmentsToImport.Add(a);
                if (!string.IsNullOrWhiteSpace(a.StatisticalDate))
                    affected.Add(((int)dstGameId, a.StatisticalDate));
            }

            var exclusionsToImport = new List<SourceExclusionRow>();
            var exclusionsSkipped = 0;
            foreach (var e in source.Exclusions)
            {
                if (!gameIdMap.TryGetValue(e.GameId, out var dstGameId)) { exclusionsSkipped++; continue; }

                var exists = dst.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM daily_play_exclusions WHERE exclusion_global_id = @g;",
                    new { g = e.ExclusionGlobalId }, tx) > 0;
                if (exists) { exclusionsSkipped++; continue; }

                exclusionsToImport.Add(e);
                if (!string.IsNullOrWhiteSpace(e.StatisticalDate))
                    affected.Add(((int)dstGameId, e.StatisticalDate));
            }

            // 残差必须在事实插入**之前**量：迁移是"合并"，本机既有的来源不明时长不得被抹掉。
            var residualBefore = new Dictionary<(int GameId, string Date), int>();
            foreach (var key in affected)
                residualBefore[key] = MeasureResidualSeconds(dst, tx, key.GameId, key.Date, cutoff);

            // ---------- ④ 写入事实（先墓碑/身份映射，再事实；顺序保证不复活） ----------
            var tombstonesImported = 0;
            var tombstonesSkipped = 0;
            foreach (var t in source.Tombstones)
            {
                var rows = dst.Execute(
                    """
                    INSERT INTO sync_tombstones
                        (tombstone_id, entity_type, entity_global_id, device_id, deleted_at_utc, created_at_utc)
                    VALUES (@TombstoneId, @EntityType, @EntityGlobalId, @DeviceId, @DeletedAtUtc, @CreatedAtUtc)
                    ON CONFLICT(entity_type, entity_global_id) DO NOTHING;
                    """, t, tx);
                if (rows > 0) tombstonesImported++; else tombstonesSkipped++;
            }

            var supersessionsImported = 0;
            var supersessionsSkipped = 0;
            foreach (var u in source.Supersessions)
            {
                var rows = dst.Execute(
                    """
                    INSERT INTO sync_identity_supersessions
                        (supersession_id, entity_type, superseded_global_id, canonical_global_id,
                         device_id, reason, created_at_utc)
                    VALUES (@SupersessionId, @EntityType, @SupersededGlobalId, @CanonicalGlobalId,
                            @DeviceId, @Reason, @CreatedAtUtc)
                    ON CONFLICT(supersession_id) DO NOTHING;
                    """, u, tx);
                if (rows > 0) supersessionsImported++; else supersessionsSkipped++;
            }

            var sessionsImported = 0;
            foreach (var s in sessionsToImport)
            {
                var dstGameId = gameIdMap[s.GameId];
                dst.Execute(
                    """
                    INSERT INTO sessions
                        (game_id, pid, process_name, start_time, end_time, last_heartbeat,
                         duration_seconds, is_active, created_at,
                         global_id, device_id, version, started_at_utc, ended_at_utc, time_precision)
                    VALUES
                        (@GameId, @Pid, @ProcessName, @StartTime, @EndTime, @LastHeartbeat,
                         @DurationSeconds, 0, COALESCE(@CreatedAt, CURRENT_TIMESTAMP),
                         @GlobalId, @DeviceId, 1, @StartedAtUtc, @EndedAtUtc, @TimePrecision);
                    """,
                    new
                    {
                        GameId = dstGameId,
                        Pid = s.Pid,
                        ProcessName = string.IsNullOrWhiteSpace(s.ProcessName) ? "unknown" : s.ProcessName,
                        s.StartTime,
                        s.EndTime,
                        LastHeartbeat = string.IsNullOrWhiteSpace(s.LastHeartbeat) ? s.StartTime : s.LastHeartbeat,
                        s.DurationSeconds,
                        s.CreatedAt,
                        s.GlobalId,
                        s.DeviceId,
                        s.StartedAtUtc,
                        s.EndedAtUtc,
                        TimePrecision = string.IsNullOrWhiteSpace(s.TimePrecision) ? "estimated" : s.TimePrecision
                    }, tx);
                sessionsImported++;
            }

            var adjustmentsImported = 0;
            foreach (var a in adjustmentsToImport)
            {
                dst.Execute(
                    """
                    INSERT INTO daily_duration_adjustments
                        (adjustment_global_id, game_id, statistical_date, device_id,
                         duration_seconds, source, created_at_utc, observed_remote_seconds)
                    VALUES (@AdjustmentGlobalId, @GameId, @StatisticalDate, @DeviceId,
                            @DurationSeconds, @Source, @CreatedAtUtc, @ObservedRemoteSeconds);
                    """,
                    new
                    {
                        a.AdjustmentGlobalId,
                        GameId = gameIdMap[a.GameId],
                        a.StatisticalDate,
                        a.DeviceId,
                        a.DurationSeconds,
                        Source = string.IsNullOrWhiteSpace(a.Source) ? "manual" : a.Source,
                        a.CreatedAtUtc,
                        a.ObservedRemoteSeconds
                    }, tx);
                adjustmentsImported++;
            }

            var exclusionsImported = 0;
            foreach (var e in exclusionsToImport)
            {
                dst.Execute(
                    """
                    INSERT INTO daily_play_exclusions
                        (exclusion_global_id, game_id, statistical_date, session_global_id, device_id, created_at_utc)
                    VALUES (@ExclusionGlobalId, @GameId, @StatisticalDate, @SessionGlobalId, @DeviceId, @CreatedAtUtc)
                    ON CONFLICT(exclusion_global_id) DO NOTHING;
                    """,
                    new
                    {
                        e.ExclusionGlobalId,
                        GameId = gameIdMap[e.GameId],
                        e.StatisticalDate,
                        e.SessionGlobalId,
                        e.DeviceId,
                        e.CreatedAtUtc
                    }, tx);
                exclusionsImported++;
            }

            // ---------- ⑤ 重算所有受影响 (game, 会计日) —— 同一事务内 ----------
            var recomputed = 0;
            foreach (var key in affected)
            {
                RefreshDailyRowFromRemainingFactsCore(
                    dst, tx, key.GameId, key.Date, cutoff, residualBefore.GetValueOrDefault(key));
                recomputed++;
            }

            tx.Commit();

            // Commit 之后只做纯内存构造（不会抛）。
            result = new BackupImportResult
            {
                Success = true,
                Mode = BackupImportMode.Migrate,
                GamesImported = gamesImported,
                GamesReused = gamesReused,
                SessionsImported = sessionsImported,
                SessionsSkipped = sessionsSkipped,
                AdjustmentsImported = adjustmentsImported,
                AdjustmentsSkipped = adjustmentsSkipped,
                ExclusionsImported = exclusionsImported,
                ExclusionsSkipped = exclusionsSkipped,
                TombstonesImported = tombstonesImported,
                TombstonesSkipped = tombstonesSkipped,
                SupersessionsImported = supersessionsImported,
                SupersessionsSkipped = supersessionsSkipped,
                DailyRowsRecomputed = recomputed,
                RequiresNotionReauth = false,
                Message = BuildMigrationMessage(gamesImported, gamesReused, sessionsImported,
                    sessionsSkipped, adjustmentsImported, exclusionsImported, tombstonesImported, recomputed)
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("[备份迁移] 合并失败，已整体回滚（未写入任何数据）", ex);
            return new BackupImportResult
            {
                Success = false,
                Mode = BackupImportMode.Migrate,
                Message = "迁移失败，已整体回滚，本机数据未受影响。",
                Error = ex.Message
            };
        }
        finally
        {
            _writeLock.Release();
        }

        AppLog.Info($"[备份迁移] 合并完成：games +{result.GamesImported}(复用 {result.GamesReused})、"
            + $"sessions +{result.SessionsImported}(跳过 {result.SessionsSkipped})、"
            + $"adjustments +{result.AdjustmentsImported}(跳过 {result.AdjustmentsSkipped})、"
            + $"exclusions +{result.ExclusionsImported}(跳过 {result.ExclusionsSkipped})、"
            + $"tombstones +{result.TombstonesImported}(跳过 {result.TombstonesSkipped})、"
            + $"supersessions +{result.SupersessionsImported}(跳过 {result.SupersessionsSkipped})、"
            + $"重算 {result.DailyRowsRecomputed} 个派生行（已提交；日志在写锁外，不影响结果）");

        return result;
    }

    // =========================================================================================
    // 内部：源库读取与辅助
    // =========================================================================================

    private static string BuildMigrationMessage(
        int gamesImported, int gamesReused, int sessionsImported, int sessionsSkipped,
        int adjustmentsImported, int exclusionsImported, int tombstonesImported, int recomputed)
        => $"已合并 {gamesImported} 个游戏（另有 {gamesReused} 个与本机同一身份，已复用）、"
           + $"{sessionsImported} 条会话记录、{adjustmentsImported} 条时长调整、{exclusionsImported} 条删除排除、"
           + $"{tombstonesImported} 条删除墓碑；重算 {recomputed} 个每日统计。"
           + (sessionsSkipped > 0 ? $"（{sessionsSkipped} 条会话因已存在或未定型而跳过）" : "")
           + " 本机设备身份与凭据配置保持不变。"
           + " 说明：合并进来的记录保留**原设备归属**，本机不会代替原设备把它们补推到云端；"
           + "本机数据已完整可用，若需要云端一致请在原设备上完成同步。";

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        conn.Open();
        return conn;
    }

    private static int CountRows(SqliteConnection conn, string table)
        => TableExists(conn, table)
            ? conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table};")
            : 0;

    private static bool TableExists(SqliteConnection conn, string table)
        => conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @n;", new { n = table }) > 0;

    /// <summary>
    /// 列是否存在。迁移导入的**源库**可能是旧版本（例如还没有 migration 016 的
    /// <c>games.reactivated_at_utc</c>）—— 直接引用不存在的列会让整条 SELECT 报错，
    /// 因此必须按列存在性动态拼装查询。
    /// </summary>
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
    /// 只清理 SQLite 的**伴生文件**（<c>-wal</c> / <c>-shm</c>），绝不删除主数据库文件。
    /// （曾经误删主文件导致导出拿到不存在/被占用的快照。）
    /// </summary>
    private static void DeleteSqliteSidecarFiles(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            try
            {
                var path = dbPath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删不掉不影响正确性：Backup API 会覆盖目标内容。
            }
        }
    }

    private static string Short(string value)
        => string.IsNullOrEmpty(value) ? "(无)" : value.Length <= 8 ? value : value[..8] + "…";

    /// <summary>读取迁移源的全部事实（一次性读进内存：源是本地文件，量级为个人游戏记录）。</summary>
    private static MigrationSource ReadMigrationSource(SqliteConnection src)
    {
        // ⚠️ 2026-10-12：源库可能是 migration 016 之前的版本（没有 reactivated_at_utc）。
        //    列不存在时**不引用它** ⇒ Dapper 映射出 null ⇒ 按 NULL 处理（"从未重新激活"）。
        var sourceHasReactivation = TableExists(src, "games")
            && ColumnExists(src, "games", "reactivated_at_utc");

        var reactivationSelect = sourceHasReactivation
            ? ", reactivated_at_utc AS ReactivatedAtUtc"
            : "";

        var games = TableExists(src, "games")
            ? src.Query<SourceGameRow>(
                $"""
                SELECT id AS Id, platform AS Platform, platform_id AS PlatformId, name AS Name,
                       executable AS Executable, executable_path AS ExecutablePath,
                       notion_page_id AS NotionPageId, COALESCE(status, 'active') AS Status,
                       COALESCE(global_id, '') AS GlobalId{reactivationSelect}
                FROM games
                WHERE NULLIF(TRIM(global_id), '') IS NOT NULL;
                """).ToList()
            : new List<SourceGameRow>();

        var sessions = TableExists(src, "sessions")
            ? src.Query<SourceSessionRow>(
                """
                SELECT id AS Id, game_id AS GameId, COALESCE(pid, 0) AS Pid,
                       COALESCE(process_name, '') AS ProcessName,
                       start_time AS StartTime, end_time AS EndTime,
                       last_heartbeat AS LastHeartbeat,
                       COALESCE(duration_seconds, 0) AS DurationSeconds,
                       created_at AS CreatedAt,
                       COALESCE(global_id, '') AS GlobalId, COALESCE(device_id, '') AS DeviceId,
                       COALESCE(started_at_utc, '') AS StartedAtUtc,
                       COALESCE(ended_at_utc, '') AS EndedAtUtc,
                       COALESCE(time_precision, 'estimated') AS TimePrecision
                FROM sessions
                WHERE is_active = 0
                  AND NULLIF(TRIM(global_id), '') IS NOT NULL
                  AND NULLIF(TRIM(start_time), '') IS NOT NULL;
                """).ToList()
            : new List<SourceSessionRow>();

        var adjustments = TableExists(src, "daily_duration_adjustments")
            ? src.Query<SourceAdjustmentRow>(
                """
                SELECT adjustment_global_id AS AdjustmentGlobalId, game_id AS GameId,
                       statistical_date AS StatisticalDate, COALESCE(device_id, '') AS DeviceId,
                       COALESCE(duration_seconds, 0) AS DurationSeconds, COALESCE(source, '') AS Source,
                       COALESCE(created_at_utc, '') AS CreatedAtUtc,
                       observed_remote_seconds AS ObservedRemoteSeconds
                FROM daily_duration_adjustments
                WHERE NULLIF(TRIM(adjustment_global_id), '') IS NOT NULL;
                """).ToList()
            : new List<SourceAdjustmentRow>();

        var exclusions = TableExists(src, "daily_play_exclusions")
            ? src.Query<SourceExclusionRow>(
                """
                SELECT exclusion_global_id AS ExclusionGlobalId, game_id AS GameId,
                       statistical_date AS StatisticalDate, session_global_id AS SessionGlobalId,
                       COALESCE(device_id, '') AS DeviceId, COALESCE(created_at_utc, '') AS CreatedAtUtc
                FROM daily_play_exclusions
                WHERE NULLIF(TRIM(exclusion_global_id), '') IS NOT NULL;
                """).ToList()
            : new List<SourceExclusionRow>();

        var tombstones = TableExists(src, "sync_tombstones")
            ? src.Query<SourceTombstoneRow>(
                """
                SELECT tombstone_id AS TombstoneId, entity_type AS EntityType,
                       entity_global_id AS EntityGlobalId, COALESCE(device_id, '') AS DeviceId,
                       COALESCE(deleted_at_utc, '') AS DeletedAtUtc, COALESCE(created_at_utc, '') AS CreatedAtUtc
                FROM sync_tombstones
                WHERE NULLIF(TRIM(tombstone_id), '') IS NOT NULL
                  AND NULLIF(TRIM(entity_global_id), '') IS NOT NULL;
                """).ToList()
            : new List<SourceTombstoneRow>();

        var supersessions = TableExists(src, "sync_identity_supersessions")
            ? src.Query<SourceSupersessionRow>(
                """
                SELECT supersession_id AS SupersessionId, entity_type AS EntityType,
                       superseded_global_id AS SupersededGlobalId, canonical_global_id AS CanonicalGlobalId,
                       COALESCE(device_id, '') AS DeviceId, COALESCE(reason, '') AS Reason,
                       COALESCE(created_at_utc, '') AS CreatedAtUtc
                FROM sync_identity_supersessions
                WHERE NULLIF(TRIM(supersession_id), '') IS NOT NULL;
                """).ToList()
            : new List<SourceSupersessionRow>();

        return new MigrationSource(games, sessions, adjustments, exclusions, tombstones, supersessions);
    }

    private sealed record MigrationSource(
        List<SourceGameRow> Games,
        List<SourceSessionRow> Sessions,
        List<SourceAdjustmentRow> Adjustments,
        List<SourceExclusionRow> Exclusions,
        List<SourceTombstoneRow> Tombstones,
        List<SourceSupersessionRow> Supersessions);

    private sealed class LocalGameRow
    {
        public long Id { get; set; }
        public string GlobalId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private sealed class SourceGameRow
    {
        public long Id { get; set; }
        public string Platform { get; set; } = "";
        public string PlatformId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Executable { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string? NotionPageId { get; set; }
        public string Status { get; set; } = "active";
        public string GlobalId { get; set; } = "";

        /// <summary>
        /// 源库的重新激活意图时间戳（migration 016）。源库没有该列、或该值为空 ⇒ <c>null</c>，
        /// 导入时按 NULL 写入（绝不用导入时刻伪造意图）。
        /// </summary>
        public string? ReactivatedAtUtc { get; set; }
    }

    private sealed class SourceSessionRow
    {
        public long Id { get; set; }
        public long GameId { get; set; }
        public long Pid { get; set; }
        public string ProcessName { get; set; } = "";
        public string StartTime { get; set; } = "";
        public string? EndTime { get; set; }
        public string? LastHeartbeat { get; set; }
        public int DurationSeconds { get; set; }
        public string? CreatedAt { get; set; }
        public string GlobalId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string StartedAtUtc { get; set; } = "";
        public string EndedAtUtc { get; set; } = "";
        public string TimePrecision { get; set; } = "estimated";
    }

    private sealed class SourceAdjustmentRow
    {
        public string AdjustmentGlobalId { get; set; } = "";
        public long GameId { get; set; }
        public string StatisticalDate { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public int DurationSeconds { get; set; }
        public string Source { get; set; } = "";
        public string CreatedAtUtc { get; set; } = "";
        public int? ObservedRemoteSeconds { get; set; }
    }

    private sealed class SourceExclusionRow
    {
        public string ExclusionGlobalId { get; set; } = "";
        public long GameId { get; set; }
        public string StatisticalDate { get; set; } = "";
        public string SessionGlobalId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string CreatedAtUtc { get; set; } = "";
    }

    private sealed class SourceTombstoneRow
    {
        public string TombstoneId { get; set; } = "";
        public string EntityType { get; set; } = "";
        public string EntityGlobalId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string DeletedAtUtc { get; set; } = "";
        public string CreatedAtUtc { get; set; } = "";
    }

    private sealed class SourceSupersessionRow
    {
        public string SupersessionId { get; set; } = "";
        public string EntityType { get; set; } = "";
        public string SupersededGlobalId { get; set; } = "";
        public string CanonicalGlobalId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Reason { get; set; } = "";
        public string CreatedAtUtc { get; set; } = "";
    }
}

/// <summary>只读统计结果（<see cref="SqliteRepository.ReadBackupStats"/> 的返回值）。</summary>
public readonly record struct BackupStatsSnapshot(
    BackupCounts Counts,
    BackupDateRange? DateRange,
    int SchemaMax,
    bool IntegrityOk);
