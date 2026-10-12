using Dapper;
using GameTimeTracker.Core.Services;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// 一条「日统计一致性」审计记录（可解释诊断；不参与业务判断）。
/// </summary>
public sealed class DailyConsistencyIssue
{
    /// <summary>问题类型（见 <see cref="SqliteRepository"/> 上的 Issue* 常量）。</summary>
    public string Kind { get; init; } = "";

    /// <summary>统计日（会计日）。</summary>
    public string Date { get; init; } = "";

    /// <summary>本地 game id（0 = 事实的 game 已不存在，即孤儿）。</summary>
    public long GameId { get; init; }

    public string GameName { get; init; } = "";

    /// <summary>事实侧 identity（诊断用，可能为空）。</summary>
    public string GlobalId { get; init; } = "";

    /// <summary>修复前：daily_summary 里记的秒数（无行 = 0）。</summary>
    public int StoredSeconds { get; init; }

    /// <summary>事实侧期望值：Σ 会话派生秒数 + Σ 调整声明秒数。</summary>
    public int FactSeconds { get; init; }

    public int StoredCount { get; init; }

    public int FactCount { get; init; }

    /// <summary>人类可读说明（写日志/报告用）。</summary>
    public string Detail { get; init; } = "";

    /// <summary>本次是否被修复（false = 只报告，例如孤儿与受保护残差）。</summary>
    public bool Fixed { get; init; }

    public override string ToString()
        => $"[{Kind}] {Date} game={GameId}({GameName}) 存={StoredSeconds}s/{StoredCount}次 事实={FactSeconds}s/{FactCount}次 {(Fixed ? "已修" : "仅报告")} {Detail}";
}

/// <summary>
/// <c>daily_summary</c> 的**一致性审计 / 幂等修复**报告（修复前后逐日、逐游戏留痕）。
/// </summary>
public sealed class DailyConsistencyReport
{
    public bool Applied { get; init; }

    /// <summary>扫描到的 (game, 统计日) 基数。</summary>
    public int RowsScanned { get; init; }

    /// <summary>实际写入的行数（补行 + 修正）。</summary>
    public int RowsFixed { get; init; }

    public int RowsInserted { get; init; }

    public int RowsUpdated { get; init; }

    /// <summary>孤儿会话条数（game 行已不存在）。</summary>
    public int OrphanSessionCount { get; init; }

    public int OrphanAdjustmentCount { get; init; }

    /// <summary>孤儿事实的总秒数（**未**纳入任何日统计；只报告，不推断归属）。</summary>
    public long OrphanSeconds { get; init; }

    /// <summary>修复前 daily_summary 的总秒数。</summary>
    public long StoredSecondsBefore { get; init; }

    /// <summary>修复后 daily_summary 的总秒数。</summary>
    public long StoredSecondsAfter { get; init; }

    /// <summary>会话 + 调整声明的事实总秒数（不含孤儿）。</summary>
    public long FactSecondsTotal { get; init; }

    public IReadOnlyList<DailyConsistencyIssue> Issues { get; init; } = Array.Empty<DailyConsistencyIssue>();

    public IReadOnlyList<DailyConsistencyIssue> Orphans { get; init; } = Array.Empty<DailyConsistencyIssue>();

    public string Summary
        => $"扫描 {RowsScanned} 行 / 修复 {RowsFixed} 行（补 {RowsInserted} 改 {RowsUpdated}）"
         + $"；事实 {FactSecondsTotal}s，Daily {StoredSecondsBefore}s → {StoredSecondsAfter}s"
         + $"；孤儿会话 {OrphanSessionCount} 条、孤儿声明 {OrphanAdjustmentCount} 条（{OrphanSeconds}s）";
}

/// <summary>
/// <b>日统计一致性（缺口 / 偏差）审计与幂等修复</b>。
///
/// <para>
/// 事故背景（2026-10-11 A/B 实机对差）：<c>daily_summary</c> 是**纯派生缓存**，但它可以被
/// Notion 删除对账**单独删行**（<c>DeleteDailySummariesAsync</c>，留底 <c>deleted_archive
/// kind='daily' source='notion'</c>），而<b>会话事实一行未动</b>；删掉之后**没有任何路径**
/// 会从 canonical 事实重新派生该行 ⇒ 该 (game, 日) 的时长在统计里永久消失，
/// 另一端（从会话重新派生过）却仍有 ⇒ A/B 逐日统计分裂。
/// </para>
///
/// <para>
/// 本机制的口径与「唯一重算入口」<see cref="RecomputeDailySummaryForGameDate"/> **完全一致**
/// （复用同一份事实与同一个跨日拆分实现 <c>GameSessionManager.ComputeDailyDeltas</c>）：
/// 期望值 = Σ 会话派生秒数 + Σ 当前有效 Adjustment 声明秒数。
/// </para>
///
/// <list type="number">
///   <item><b>幂等</b>：用事实"设"值，重复运行第二次一定 0 改动；</item>
///   <item><b>只升不降</b>：存值 &gt; 事实值 ⇒ 视为「来源不明的历史残差」，**原样保留**并报告，
///         绝不静默抹掉（与 C 类保护同语义）；</item>
///   <item><b>不重复计调整</b>：调整按 (game, 统计日) 求和后一次性计入（集合语义，不是增量累加）；</item>
///   <item><b>孤儿只报告</b>：事实的 game 已不存在时**绝不**推断归属、**绝不**丢弃秒数，
///         而是记入 <see cref="DailyConsistencyReport.Orphans"/> 并给出秒数；</item>
///   <item><b>默认不产生 Notion 副作用</b>：被「Notion 删除对账」有意删除的行（有留底）
///         默认**不自动恢复**，只报告为 <see cref="SqliteRepository.IssueNotionDeletedRowWithFacts"/>；
///         确需恢复时由人工显式打开 <c>restoreNotionDeletedRows</c>。</item>
/// </list>
/// </summary>
public partial class SqliteRepository
{
    // ---------------------------- 问题类型常量 ----------------------------

    /// <summary>有事实、无行（且无"有意删除"留底）⇒ 自动补行。</summary>
    public const string IssueMissingRow = "missing_row";

    /// <summary>存值 &lt; 事实值 ⇒ 会自动上调到事实值（同步状态置 pending，与唯一重算入口一致）。</summary>
    public const string IssueSecondsMismatch = "seconds_mismatch";

    /// <summary>秒数一致但场次计数不符合统一口径。</summary>
    public const string IssueCountMismatch = "count_mismatch";

    /// <summary>存值 &gt; 事实值：来源不明的历史残差，**保留**并报告。</summary>
    public const string IssueResidualKept = "residual_kept";

    /// <summary>被 Notion 删除对账有意删除、但本地仍有会话/声明事实 → 默认只报告，等人工裁定。</summary>
    public const string IssueNotionDeletedRowWithFacts = "notion_deleted_row_with_facts";

    /// <summary>孤儿的会话事实（game 行不存在）。</summary>
    public const string IssueOrphanSession = "orphan_session";

    /// <summary>孤儿的调整声明（game 行不存在）。</summary>
    public const string IssueOrphanAdjustment = "orphan_adjustment";

    /// <summary>统计事实的 start_time 无法解析（旧口径会静默丢秒，这里必须可见）。</summary>
    public const string IssueUnparseableSession = "unparseable_session";

    /// <summary>孤儿的派生行（daily_summary 指向已不存在的 game；只报告，不删不改）。</summary>
    public const string IssueOrphanDailyRow = "orphan_daily_row";

    // ---------------------------- 对外入口 ----------------------------

    /// <summary>
    /// 启动自愈入口：跑一次 daily_summary 一致性修复，返回**实际修复的行数**（幂等）。
    /// </summary>
    public async Task<int> ReconcileDailySummariesAsync(bool apply = true)
        => (await ReconcileDailySummariesReportAsync(apply)).RowsFixed;

    /// <summary>
    /// 完整版（诊断 / 测试 / 人工修复）：返回带逐行留痕的报告。
    /// </summary>
    /// <param name="apply">
    /// <c>false</c> = 只审计不写库（dry-run，用于先看差额）；<c>true</c> = 写入差异。
    /// </param>
    /// <param name="restoreNotionDeletedRows">
    /// <c>false</c>（默认）= 被 Notion 删除对账有意删除的行**只报告**，不自动恢复
    /// （避免把用户删掉的 Notion 页面又建回来）；<c>true</c> = 依据本地会话事实恢复这些行
    /// （恢复为 <c>pending</c>，等待人工确认后再同步）。
    /// </param>
    public async Task<DailyConsistencyReport> ReconcileDailySummariesReportAsync(
        bool apply = true, bool restoreNotionDeletedRows = false, bool applyCounts = false,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var report = ReconcileDailySummariesCore(conn, tx, apply, restoreNotionDeletedRows, applyCounts);

            // apply=false 时不写任何内容，提交空事务无害（读一致性由同一个事务保证）。
            tx.Commit();

            if (report.RowsFixed > 0)
            {
                AppLog.Info($"[日统计自愈] {report.Summary}");
            }
            else if (report.RowsScanned > 0)
            {
                var onlyReports = report.Issues.Count(i => !i.Fixed) + report.Orphans.Count;
                if (onlyReports > 0)
                {
                    AppLog.Warn($"[日统计自愈] 审计发现 {onlyReports} 项差异（未自动改动）：{report.Summary}");
                }
            }

            return report;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---------------------------- 核心实现（同步、可测） ----------------------------

    internal static DailyConsistencyReport ReconcileDailySummariesCore(
        SqliteConnection conn, SqliteTransaction? tx, bool apply, bool restoreNotionDeletedRows, bool applyCounts)
    {
        var cutoffHour = ReadDailyCutoffHour(conn, tx);

        var issues = new List<DailyConsistencyIssue>();
        var orphans = new List<DailyConsistencyIssue>();

        // ① 本地 games（用于名称/identity 诊断 + 判定孤儿）
        var games = conn.Query<GameLookupRow>(
            "SELECT id AS Id, name AS Name, COALESCE(global_id, '') AS GlobalId FROM games;", transaction: tx)
            .ToDictionary(g => g.Id, g => g);
        var gameIds = games.Keys.ToHashSet();

        // ② 事实侧：会话（按会计日拆分，唯一实现）
        var factSeconds = new Dictionary<(long GameId, string Date), int>();
        var factCounts = new Dictionary<(long GameId, string Date), int>();
        long orphanSeconds = 0;
        var orphanSessions = 0;

        var sessions = conn.Query<SessionFactRow>(
            """
            SELECT id AS Id, game_id AS GameId, start_time AS StartTime, duration_seconds AS DurationSeconds,
                   COALESCE(global_id, '') AS GlobalId, COALESCE(device_id, '') AS DeviceId
            FROM sessions;
            """, transaction: tx);

        // ⚠️ 2026-10-11：被用户显式删除的会话分片**不得算进"事实"**——否则审计会判
        //    "存值 < 事实值"而自动上调，把已删除的时长复活（这是删除语义必须覆盖的路径）。
        var exclusions = LoadAllPlayExclusions(conn, tx);

        foreach (var s in sessions)
        {
            if (!gameIds.Contains(s.GameId))
            {
                orphanSessions++;
                var orphanSecs = Math.Max(0, s.DurationSeconds);
                orphanSeconds += orphanSecs;
                orphans.Add(new DailyConsistencyIssue
                {
                    Kind = IssueOrphanSession,
                    GameId = s.GameId,
                    GlobalId = s.GlobalId,
                    StoredSeconds = 0,
                    FactSeconds = orphanSecs,
                    Detail = $"会话 #{s.Id}（{orphanSecs}s，设备 {(s.DeviceId.Length > 8 ? s.DeviceId[..8] : s.DeviceId)}）"
                             + "的 game 行不存在：既不计入任何日统计、也不参与同步 —— 只报告，不推断归属、不删除"
                });
                continue;
            }

            if (!TryParseWallClock(s.StartTime, out var start))
            {
                issues.Add(new DailyConsistencyIssue
                {
                    Kind = IssueUnparseableSession,
                    GameId = s.GameId,
                    GameName = NameOf(games, s.GameId),
                    GlobalId = s.GlobalId,
                    FactSeconds = Math.Max(0, s.DurationSeconds),
                    Detail = $"会话 #{s.Id} 的 start_time=\"{s.StartTime}\" 无法解析 ⇒ 旧口径会静默丢秒，需人工修数据"
                });
                continue;
            }

            var span = Math.Max(0, s.DurationSeconds);
            if (span <= 0) continue;

            var end = start.AddSeconds(span);
            foreach (var delta in GameSessionManager.ComputeDailyDeltas(start, end, span, cutoffHour))
            {
                if (IsSliceExcluded(exclusions, s.GameId, delta.Date, s.GlobalId)) continue;

                var key = (s.GameId, delta.Date);
                factSeconds[key] = factSeconds.GetValueOrDefault(key) + delta.DurationSeconds;
            }

            // 场次计数：会话计入「起始时刻的会计日」（跨日会话只算一次，与 B 端实测口径一致）
            if (span >= 60)
            {
                var countDate = AccountingDateHelper.GetAccountingDateString(start, cutoffHour);
                if (!IsSliceExcluded(exclusions, s.GameId, countDate, s.GlobalId))
                {
                    var countKey = (s.GameId, countDate);
                    factCounts[countKey] = factCounts.GetValueOrDefault(countKey) + 1;
                }
            }
        }

        // ③ 事实侧：调整声明（集合求和，不会重复累加）
        var orphanAdjustments = 0;
        var adjustments = conn.Query<AdjustmentFactRow>(
            """
            SELECT game_id AS GameId, statistical_date AS Date, duration_seconds AS DurationSeconds,
                   adjustment_global_id AS AdjustmentGlobalId
            FROM daily_duration_adjustments;
            """, transaction: tx);

        foreach (var a in adjustments)
        {
            if (!gameIds.Contains(a.GameId))
            {
                orphanAdjustments++;
                var orphanSecs = Math.Max(0, a.DurationSeconds);
                orphanSeconds += orphanSecs;
                orphans.Add(new DailyConsistencyIssue
                {
                    Kind = IssueOrphanAdjustment,
                    Date = a.Date,
                    GameId = a.GameId,
                    GlobalId = a.AdjustmentGlobalId,
                    FactSeconds = orphanSecs,
                    Detail = $"声明 {a.AdjustmentGlobalId}（{a.Date}，{orphanSecs}s）的 game 行不存在：只报告，不推断归属"
                });
                continue;
            }

            var key = (a.GameId, a.Date);
            factSeconds[key] = factSeconds.GetValueOrDefault(key) + Math.Max(0, a.DurationSeconds);
        }

        // ④ 现状侧：daily_summary
        var storedRows = conn.Query<StoredDailyRow>(
            "SELECT id AS Id, date AS Date, game_id AS GameId, duration_seconds AS DurationSeconds, session_count AS SessionCount FROM daily_summary;",
            transaction: tx).ToList();
        var storedByKey = storedRows.ToDictionary(r => (r.GameId, r.Date), r => r);

        var storedSecondsBefore = storedRows.Sum(r => (long)r.DurationSeconds);
        var factSecondsTotal = factSeconds.Values.Sum(v => (long)v);

        // 派生行指向已不存在的 game（只有旧库 / 关闭外键的删除会留下）：只报告，绝不删改。
        foreach (var r in storedRows.Where(r => !gameIds.Contains(r.GameId)))
        {
            orphans.Add(new DailyConsistencyIssue
            {
                Kind = IssueOrphanDailyRow,
                Date = r.Date,
                GameId = r.GameId,
                StoredSeconds = Math.Max(0, r.DurationSeconds),
                StoredCount = r.SessionCount,
                Detail = $"daily_summary 行 #{r.Id} 指向不存在的 game {r.GameId}：只报告，不删不改（等人工裁定）"
            });
        }

        // ⑤ 「有意删除」留底：Notion 删除对账删掉的 (game, 日) —— 默认不自动恢复
        var deliberatelyDeleted = ReadArchivedDailyKeys(conn, tx);

        // ⑥ 逐行对差并（可选）修复
        var allKeys = factSeconds.Keys
            .Concat(storedRows.Select(r => (r.GameId, r.Date)))
            .Where(k => gameIds.Contains(k.GameId))
            .Distinct()
            .OrderBy(k => k.Date).ThenBy(k => k.GameId)
            .ToList();

        var rowsInserted = 0;
        var rowsUpdated = 0;

        foreach (var key in allKeys)
        {
            var fact = factSeconds.GetValueOrDefault(key);
            var factCount = factCounts.GetValueOrDefault(key) + (FactBonusCount(conn, tx, key.GameId, key.Date, 0) > 0 ? 1 : 0);
            var hasStored = storedByKey.TryGetValue(key, out var stored);
            var storedSeconds = hasStored ? Math.Max(0, stored!.DurationSeconds) : 0;
            var storedCount = hasStored ? stored!.SessionCount : 0;

            var name = NameOf(games, key.GameId);
            var globalId = games.TryGetValue(key.GameId, out var g) ? g.GlobalId : "";

            if (!hasStored)
            {
                if (fact <= 0) continue;

                if (deliberatelyDeleted.Contains(key) && !restoreNotionDeletedRows)
                {
                    issues.Add(new DailyConsistencyIssue
                    {
                        Kind = IssueNotionDeletedRowWithFacts,
                        Date = key.Date,
                        GameId = key.GameId,
                        GameName = name,
                        GlobalId = globalId,
                        StoredSeconds = 0,
                        FactSeconds = fact,
                        StoredCount = 0,
                        FactCount = factCount,
                        Detail = "该行是被「Notion 删除对账」有意删除的（deleted_archive kind='daily'），但本地会话/声明事实仍在："
                                 + "默认不自动恢复（避免把已删除的 Notion 页面建回来），需人工裁定；"
                                 + "用 restoreNotionDeletedRows=true 可依据本地事实恢复"
                    });
                    continue;
                }

                if (apply)
                {
                    conn.Execute(
                        """
                        INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
                        VALUES (@date, @gameId, @seconds, @minutes, @count, 'pending');
                        """,
                        new { date = key.Date, gameId = key.GameId, seconds = fact, minutes = fact / 60, count = factCount },
                        tx);
                    rowsInserted++;
                }

                issues.Add(new DailyConsistencyIssue
                {
                    Kind = IssueMissingRow,
                    Date = key.Date,
                    GameId = key.GameId,
                    GameName = name,
                    GlobalId = globalId,
                    StoredSeconds = 0,
                    FactSeconds = fact,
                    StoredCount = 0,
                    FactCount = factCount,
                    Detail = "缺失派生行：按 canonical 事实补回",
                    Fixed = apply
                });
                continue;
            }

            // 有行：秒数
            if (storedSeconds < fact)
            {
                if (apply)
                {
                    conn.Execute(
                        """
                        UPDATE daily_summary
                        SET duration_seconds = @seconds, duration_minutes = @minutes, sync_status = 'pending'
                        WHERE id = @id;
                        """,
                        new { seconds = fact, minutes = fact / 60, id = stored!.Id }, tx);
                    rowsUpdated++;
                }

                issues.Add(new DailyConsistencyIssue
                {
                    Kind = IssueSecondsMismatch,
                    Date = key.Date,
                    GameId = key.GameId,
                    GameName = name,
                    GlobalId = globalId,
                    StoredSeconds = storedSeconds,
                    FactSeconds = fact,
                    StoredCount = storedCount,
                    FactCount = factCount,
                    Detail = $"存值低于事实值（差额 {fact - storedSeconds}s）⇒ 上调到事实值",
                    Fixed = apply
                });
            }
            else if (storedSeconds > fact)
            {
                // 🛡️ 只升不降：来源不明的历史残差原样保留（与 C 类保护同语义）
                issues.Add(new DailyConsistencyIssue
                {
                    Kind = IssueResidualKept,
                    Date = key.Date,
                    GameId = key.GameId,
                    GameName = name,
                    GlobalId = globalId,
                    StoredSeconds = storedSeconds,
                    FactSeconds = fact,
                    StoredCount = storedCount,
                    FactCount = factCount,
                    Detail = $"存值高出事实值 {storedSeconds - fact}s：视为来源不明的历史残差，**保留不抹**（需人工裁定）"
                });
                continue;
            }

            // 有行且秒数一致：统一场次口径
            //
            // ⚠️ 2026-10-11 用户裁定：**本轮不自动改场次**（历史场次是"心跳首分钟置 1"与
            //    "结束路径按日历日计数"两条不一致路径的产物，归一化会改变既有 Notion 数字）。
            //    默认只报告；确需归一化时由人工显式打开 applyCounts。
            var expectedCount = factCount;
            if (storedCount != expectedCount)
            {
                if (apply && applyCounts)
                {
                    conn.Execute("UPDATE daily_summary SET session_count = @count WHERE id = @id;",
                        new { count = expectedCount, id = stored!.Id }, tx);
                    rowsUpdated++;
                }

                issues.Add(new DailyConsistencyIssue
                {
                    Kind = IssueCountMismatch,
                    Date = key.Date,
                    GameId = key.GameId,
                    GameName = name,
                    GlobalId = globalId,
                    StoredSeconds = storedSeconds,
                    FactSeconds = fact,
                    StoredCount = storedCount,
                    FactCount = expectedCount,
                    Detail = "场次计数不符合统一口径（≥60s 会话按**起始会计日**计数 + 声明/残差 ≥60s 计 1 次）"
                             + (applyCounts ? "" : "；本轮默认只报告，不改既有数字（人工裁定后再打开 applyCounts）"),
                    Fixed = apply && applyCounts
                });
            }
        }

        var storedSecondsAfter = apply
            ? conn.ExecuteScalar<long>("SELECT COALESCE(SUM(duration_seconds), 0) FROM daily_summary;", transaction: tx)
            : storedSecondsBefore;

        return new DailyConsistencyReport
        {
            Applied = apply,
            RowsScanned = allKeys.Count,
            RowsFixed = rowsInserted + rowsUpdated,
            RowsInserted = rowsInserted,
            RowsUpdated = rowsUpdated,
            OrphanSessionCount = orphanSessions,
            OrphanAdjustmentCount = orphanAdjustments,
            OrphanSeconds = orphanSeconds,
            StoredSecondsBefore = storedSecondsBefore,
            StoredSecondsAfter = storedSecondsAfter,
            FactSecondsTotal = factSecondsTotal,
            Issues = issues,
            Orphans = orphans
        };

        static string NameOf(Dictionary<long, GameLookupRow> games, long gameId)
            => games.TryGetValue(gameId, out var g) ? g.Name : $"(game {gameId} 不存在)";
    }

    /// <summary>
    /// <b>统一场次口径</b>（唯一实现）：
    /// <c>≥60 秒且起始会计日 == 该统计日</c>的会话数 + （调整声明 + 受保护残差 ≥ 60 秒 ? 1 : 0）。
    ///
    /// <para>
    /// 语义与旧口径（<c>substr(start_time,1,10)</c> 日历日 + <c>daily &gt; 会话和 + 60</c> 的 manualBonus）
    /// 在 <c>cutoff = 24</c>（默认）时**逐值等价**；cutoff ≠ 24 时把场次归到**会计日**，
    /// 与本机/远端 daily 的归属口径终于一致（旧口径会把跨结算点的会话计到错误的日期）。
    /// </para>
    /// </summary>
    internal static int ComputeSessionCountForAccountingDate(
        SqliteConnection conn, SqliteTransaction? tx, long gameId, string statisticalDate, int cutoffHour,
        int preservedResidualSeconds = 0)
    {
        var sessions = conn.Query<SessionFactRow>(
            $"""
            SELECT id AS Id, game_id AS GameId, start_time AS StartTime, duration_seconds AS DurationSeconds,
                   COALESCE(global_id, '') AS GlobalId, '' AS DeviceId
            FROM sessions
            WHERE game_id = @gameId AND duration_seconds >= {DailyCountRule.MinCountedSessionSeconds};
            """,
            new { gameId }, tx);

        // ⚠️ 2026-10-11：被用户显式删除的会话分片**不计场次**（与秒数口径同一份排除集）。
        var excluded = LoadExcludedSessionGlobalIds(conn, tx, gameId, statisticalDate);

        var included = 0;
        foreach (var s in sessions)
        {
            if (excluded.Count > 0 && !string.IsNullOrEmpty(s.GlobalId) && excluded.Contains(s.GlobalId)) continue;

            if (!TryParseWallClock(s.StartTime, out var start)) continue;
            if (string.Equals(AccountingDateHelper.GetAccountingDateString(start, cutoffHour), statisticalDate, StringComparison.Ordinal))
            {
                included++;
            }
        }

        // 唯一契约（docs/DailyStatisticsContract.md，count-rule-v1）：
        // 目标场次 = ≥60s 且起始会计日匹配的会话数 + (声明 + 残差 > 60 ? 1 : 0)。
        // ⚠️ 声明与残差**相加后只判定一次** ⇒ 二者永远不会各自触发、不会重复计数。
        return DailyCountRule.Compute(
            included,
            SumAdjustments(conn, tx, gameId, statisticalDate),
            preservedResidualSeconds);
    }

    /// <summary>上面口径里的「声明/残差 ≥60s 计 1 次」部分（供审计复用）。</summary>
    private static int FactBonusCount(
        SqliteConnection conn, SqliteTransaction? tx, long gameId, string statisticalDate, int preservedResidualSeconds)
        => (SumAdjustments(conn, tx, gameId, statisticalDate) + Math.Max(0, preservedResidualSeconds)) > 60 ? 1 : 0;

    /// <summary>
    /// 读 <c>deleted_archive</c> 里「有意删除」的 (game, 统计日) 集合（Notion 删除对账留底）。
    /// 解析失败 ⇒ 返回空集合（降级：把缺行当成"非有意删除"处理会误恢复，因此这里的失败只记日志）。
    /// </summary>
    private static HashSet<(long GameId, string Date)> ReadArchivedDailyKeys(SqliteConnection conn, SqliteTransaction? tx)
    {
        var set = new HashSet<(long, string)>();
        try
        {
            var rows = conn.Query<ArchivedDailyRow>(
                """
                SELECT CAST(json_extract(payload_json, '$.GameId') AS INTEGER) AS GameId,
                       json_extract(payload_json, '$.Date')                  AS Date
                FROM deleted_archive
                WHERE kind = 'daily';
                """, transaction: tx);

            foreach (var r in rows)
            {
                if (r.GameId is null || string.IsNullOrWhiteSpace(r.Date)) continue;
                set.Add((r.GameId.Value, r.Date!));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[日统计自愈] 读取 deleted_archive 留底失败（按“无有意删除留底”处理会偏保守）：{ex.Message}");
        }

        return set;
    }

    private sealed class GameLookupRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string GlobalId { get; set; } = "";
    }

    private sealed class SessionFactRow
    {
        public long Id { get; set; }
        public long GameId { get; set; }
        public string? StartTime { get; set; }
        public int DurationSeconds { get; set; }
        public string GlobalId { get; set; } = "";
        public string DeviceId { get; set; } = "";
    }

    private sealed class AdjustmentFactRow
    {
        public long GameId { get; set; }
        public string Date { get; set; } = "";
        public int DurationSeconds { get; set; }
        public string AdjustmentGlobalId { get; set; } = "";
    }

    private sealed class StoredDailyRow
    {
        public long Id { get; set; }
        public string Date { get; set; } = "";
        public long GameId { get; set; }
        public int DurationSeconds { get; set; }
        public int SessionCount { get; set; }
    }

    private sealed class ArchivedDailyRow
    {
        public long? GameId { get; set; }
        public string? Date { get; set; }
    }
}
