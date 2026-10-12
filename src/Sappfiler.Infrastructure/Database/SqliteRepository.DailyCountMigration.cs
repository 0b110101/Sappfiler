using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Services;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>旧场次计数的迁移分类（全部由**可验证的数据条件**产生，禁止按日期/名称硬编码）。</summary>
public enum DailyCountMigrationCategory
{
    /// <summary>事实完整、目标值可确定（秒数与事实一致、无残差）。</summary>
    Recomputable,

    /// <summary>旧版 manualBonus 残留（`存值 = 会话数 + 1` 且 `存秒 &gt; 当日起始会话秒和 + 60`）。</summary>
    LegacyManualBonus,

    /// <summary>历史默认值残留（存值 = 1，无声明支撑，且会话数 ≠ 1）。</summary>
    DefaultCountResidue,

    /// <summary>跨日 Session 导致的旧口径差异（本日有时长落入，但会话起始在别的会计日）。</summary>
    CrossDayAccounting,

    /// <summary>声明 bonus 生效导致的差异（声明 + 残差 &gt; 60 ⇒ +1）。</summary>
    AdjustmentCounting,

    /// <summary>存在来源不明的历史残差（存值 &gt; 事实值）⇒ 保守保留，不自动改。</summary>
    UnknownResidual,

    /// <summary>孤儿事实或已归档游戏 ⇒ 只报告，不自动改。</summary>
    OrphanOrArchived
}

/// <summary>
/// 迁移**动作**（唯一）：与「诊断信号」分离 —— 同一条记录可以同时命中多个信号，
/// 但只有**一个**动作，且动作由安全闸门决定（证据不足一律 <see cref="ManualReview"/>）。
/// </summary>
public enum DailyCountMigrationAction
{
    /// <summary>自动重算为事实派生值（**秒数不变**）。</summary>
    AutoRecompute,

    /// <summary>保留现值（无差异、或现值已被事实支持）。</summary>
    KeepCurrent,

    /// <summary>
    /// 人工审核：证据不足以确定目标值 —— 未知残差、仅凭启发式的"默认值残留"嫌疑、
    /// 孤儿事实、已归档游戏、身份冲突。**保留原始 session_count**。
    /// </summary>
    ManualReview
}

/// <summary>迁移**诊断信号**（可同时成立；按位标记，仅用于审计与人工裁定，不做优先级覆盖）。</summary>
[Flags]
public enum DailyCountMigrationSignal
{
    None = 0,

    /// <summary>事实完整、目标值可由事实唯一确定。</summary>
    FactComplete = 1,

    /// <summary>旧版 manualBonus 签名（**可证实的历史规则**：存值 = 当日起始会话数 + 1 且存秒 &gt; 起始日会话秒和 + 60）。</summary>
    LegacyManualBonus = 2,

    /// <summary>本日有时长落入的跨日会话（场次按起始会计日归属）。</summary>
    CrossDay = 4,

    /// <summary>声明 (+残差) &gt; 60 触发场次加成。</summary>
    DeclarationBonus = 8,

    /// <summary>存值 = 1 且无声明支撑：旧版默认值残留的**嫌疑**（证据不足，不可自动改写）。</summary>
    DefaultCountResidueSuspected = 16,

    /// <summary>存值 &gt; 事实值：来源不明的历史残差（受保护，绝不清零）。</summary>
    UnknownResidual = 32,

    /// <summary>孤儿事实（game 行不存在）。</summary>
    OrphanFact = 64,

    /// <summary>游戏已归档（用户已删除）。</summary>
    ArchivedGame = 128
}

/// <summary>迁移清单里的一行（逐条可对账）。</summary>
public sealed class DailyCountMigrationRow
{
    public long GameId { get; init; }
    public string GameGlobalId { get; init; } = "";
    public string GameName { get; init; } = "";
    public string Date { get; init; } = "";

    /// <summary>daily_summary 行 id；0 = 没有对应派生行（孤儿/缺行）。</summary>
    public long DailyRowId { get; init; }

    public int StoredCount { get; init; }
    public int TargetCount { get; init; }
    public int StoredSeconds { get; init; }
    public int FactSeconds { get; init; }
    public int ResidualSeconds { get; init; }

    /// <summary>本会计日「起始且 ≥60 秒」的会话数。</summary>
    public int CountedSessions { get; init; }

    /// <summary>本会计日有时长落入的会话数（含跨日）。</summary>
    public int ContributingSessions { get; init; }

    public int AdjustmentCount { get; init; }
    public int AdjustmentSeconds { get; init; }

    public bool GameArchived { get; init; }
    public bool IsOrphanFact { get; init; }

    public DailyCountMigrationCategory Category { get; init; }

    /// <summary>迁移**动作**（唯一）。</summary>
    public DailyCountMigrationAction Action { get; init; } = DailyCountMigrationAction.KeepCurrent;

    /// <summary>诊断**信号**（可同时成立，全部可追溯）。</summary>
    public IReadOnlyList<DailyCountMigrationSignal> Signals { get; init; } = Array.Empty<DailyCountMigrationSignal>();

    /// <summary>与 <see cref="Signals"/> 一一对应的可读原因。</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public string Reason { get; init; } = "";

    /// <summary>是否允许自动迁移（= 动作是自动重算）。</summary>
    public bool AutoMigratable => Action == DailyCountMigrationAction.AutoRecompute;

    /// <summary>信号的可读合并文本（写台账 / 报告用）。</summary>
    public string SignalsText => Signals.Count == 0 ? "none" : string.Join(",", Signals);

    public int Delta => TargetCount - StoredCount;

    public override string ToString()
        => $"{Date} game={GameId}({GameName}) 存={StoredCount} 目标={TargetCount} ({Delta:+#;-#;0}) "
         + $"秒 存={StoredSeconds}/事实={FactSeconds}/残差={ResidualSeconds} 会话={CountedSessions}(贡献{ContributingSessions}) "
         + $"声明={AdjustmentCount}条/{AdjustmentSeconds}s [{Category}/{Action}] 信号=[{SignalsText}] "
         + $"{(AutoMigratable ? "可迁移" : "需人工")} {Reason}";
}

/// <summary>迁移清单（含指纹，用于 apply 时校验"库状态未变"）。</summary>
public sealed class DailyCountMigrationPlan
{
    public string RuleVersion { get; init; } = DailyCountRule.Version;
    public string PlanHash { get; init; } = "";
    public string GeneratedAtUtc { get; init; } = "";
    public IReadOnlyList<DailyCountMigrationRow> Rows { get; init; } = Array.Empty<DailyCountMigrationRow>();

    /// <summary>可自动迁移的行。</summary>
    public IReadOnlyList<DailyCountMigrationRow> AutoRows => Rows.Where(r => r.AutoMigratable).ToList();

    /// <summary>需人工裁定的行（未知残差 / 孤儿 / 归档）。</summary>
    public IReadOnlyList<DailyCountMigrationRow> ManualRows => Rows.Where(r => !r.AutoMigratable).ToList();

    public string Summary
        => $"规则 {RuleVersion}｜候选 {Rows.Count} 行（自动 {AutoRows.Count} / 人工 {ManualRows.Count}）"
         + $"｜预计场次变化 {AutoRows.Sum(r => r.Delta):+#;-#;0}｜指纹 {PlanHash[..Math.Min(12, PlanHash.Length)]}";
}

/// <summary>迁移执行结果（apply / dry-run / 拒绝 / 已应用）。</summary>
public sealed class DailyCountMigrationResult
{
    public bool Applied { get; init; }
    public bool Refused { get; init; }
    public bool AlreadyApplied { get; init; }
    public string Note { get; init; } = "";
    public string PlanHash { get; init; } = "";
    public int Updated { get; init; }
    public IReadOnlyList<DailyCountMigrationRow> Rows { get; init; } = Array.Empty<DailyCountMigrationRow>();
    public string Summary => $"{(Applied ? "已应用" : Refused ? "已拒绝" : "dry-run")}｜更新 {Updated} 行｜{Note}";
}

/// <summary>迁移台账行（只读查询用）。</summary>
public sealed class DailyCountMigrationLedgerRow
{
    public long Id { get; set; }
    public string RuleVersion { get; set; } = "";
    public string PlanHash { get; set; } = "";
    public long GameId { get; set; }
    public string GameGlobalId { get; set; } = "";
    public string Date { get; set; } = "";
    public string Category { get; set; } = "";
    public string Signals { get; set; } = "";
    public int BeforeCount { get; set; }
    public int AfterCount { get; set; }
    public int BeforeSeconds { get; set; }
    public int AfterSeconds { get; set; }
    public string Reason { get; set; } = "";
    public string AppliedAtUtc { get; set; } = "";
}

/// <summary>
/// <b>旧场次计数迁移服务</b>（count-rule-v1）：
/// 默认**只读预演**；apply 必须显式开关 + 清单指纹校验；单事务写入并留台账；可幂等重跑、可回滚。
///
/// <para>
/// ⚠️ 刻意**不接入**启动流程、不接入同步/Notion 流程 —— 迁移只能由人工显式触发。
/// </para>
/// </summary>
public partial class SqliteRepository
{
    // ============================ 预演 ============================

    /// <summary>只读预演：产出逐行迁移清单（不写任何数据）。</summary>
    public async Task<DailyCountMigrationPlan> PlanDailyCountMigrationAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            var plan = PlanDailyCountMigrationCore(conn, tx);
            tx.Commit();
            return plan;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>应用（默认 dry-run）：先校验清单指纹与当前库状态一致，再在**单事务**里写已批准的行。</summary>
    public async Task<DailyCountMigrationResult> ApplyDailyCountMigrationAsync(
        DailyCountMigrationPlan plan, bool apply = false, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var current = PlanDailyCountMigrationCore(conn, tx);

            if (current.Rows.Count == 0)
            {
                tx.Rollback();
                return new DailyCountMigrationResult
                {
                    Applied = true, AlreadyApplied = true, PlanHash = current.PlanHash,
                    Note = "当前库已无待迁移行（幂等：无需改动）"
                };
            }

            if (plan is null || !string.Equals(current.PlanHash, plan.PlanHash, StringComparison.Ordinal))
            {
                tx.Rollback();
                return new DailyCountMigrationResult
                {
                    Refused = true, PlanHash = current.PlanHash,
                    Note = $"库状态与预演清单不一致（清单 {plan?.PlanHash[..Math.Min(12, (plan?.PlanHash ?? "").Length)]} "
                         + $"vs 当前 {current.PlanHash[..Math.Min(12, current.PlanHash.Length)]}）⇒ 拒绝应用，请重新预演",
                    Rows = current.Rows
                };
            }

            var auto = current.AutoRows.ToList();
            if (!apply)
            {
                tx.Rollback();
                return new DailyCountMigrationResult
                {
                    Applied = false, PlanHash = current.PlanHash, Updated = 0,
                    Note = $"dry-run：将更新 {auto.Count} 行；人工裁定 {current.ManualRows.Count} 行不动",
                    Rows = current.Rows
                };
            }

            var now = UtcNowIso();
            var updated = 0;
            foreach (var row in auto)
            {
                if (row.DailyRowId == 0) continue;   // 没有派生行 ⇒ 场次迁移不建行

                var affected = conn.Execute(
                    """
                    UPDATE daily_summary
                    SET session_count = @target
                    WHERE id = @id AND session_count = @before;
                    """,
                    new { target = row.TargetCount, id = row.DailyRowId, before = row.StoredCount }, tx);

                if (affected == 0) continue;   // 期间被别人改过 ⇒ 跳过（不覆盖）

                conn.Execute(
                    """
                    INSERT INTO daily_count_migrations
                        (rule_version, plan_hash, game_id, game_global_id, date, category, signals,
                         before_count, after_count, before_seconds, after_seconds, reason, applied_at_utc)
                    VALUES
                        (@ruleVersion, @planHash, @gameId, @gameGlobalId, @date, @category, @signals,
                         @beforeCount, @afterCount, @beforeSeconds, @afterSeconds, @reason, @appliedAtUtc);
                    """,
                    new
                    {
                        ruleVersion = current.RuleVersion,
                        planHash = current.PlanHash,
                        gameId = row.GameId,
                        gameGlobalId = row.GameGlobalId,
                        date = row.Date,
                        category = row.Category.ToString(),
                        signals = row.SignalsText,
                        beforeCount = row.StoredCount,
                        afterCount = row.TargetCount,
                        beforeSeconds = row.StoredSeconds,
                        afterSeconds = row.StoredSeconds,
                        reason = row.Reason,
                        appliedAtUtc = now
                    }, tx);

                updated++;
            }

            tx.Commit();
            AppLog.Info($"[场次迁移] 已应用 {updated} 行（规则 {current.RuleVersion}，指纹 {current.PlanHash[..12]}）");
            return new DailyCountMigrationResult
            {
                Applied = true, PlanHash = current.PlanHash, Updated = updated,
                Note = $"已更新 {updated} 行；秒数未改动；台账已记录",
                Rows = current.Rows
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>只读：迁移台账（最近 N 条）。</summary>
    public async Task<IReadOnlyList<DailyCountMigrationLedgerRow>> GetDailyCountMigrationHistoryAsync(int limit = 200)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<DailyCountMigrationLedgerRow>(
            """
            SELECT id AS Id, rule_version AS RuleVersion, plan_hash AS PlanHash, game_id AS GameId,
                   COALESCE(game_global_id, '') AS GameGlobalId, date AS Date, category AS Category,
                   COALESCE(signals, '') AS Signals,
                   before_count AS BeforeCount, after_count AS AfterCount,
                   before_seconds AS BeforeSeconds, after_seconds AS AfterSeconds,
                   COALESCE(reason, '') AS Reason, applied_at_utc AS AppliedAtUtc
            FROM daily_count_migrations
            ORDER BY id DESC
            LIMIT @limit;
            """,
            new { limit });
        return rows.ToList();
    }

    /// <summary>
    /// 回滚某个清单：把台账里该指纹的行恢复为修改前的场次（幂等：仅在当前值 == 迁移后值时恢复），
    /// 并写入 <c>rollback</c> 标记行。秒数自始至终未改动。
    /// </summary>
    public async Task<int> RollbackDailyCountMigrationAsync(string planHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(planHash)) return 0;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            var ledger = conn.Query<DailyCountMigrationLedgerRow>(
                """
                SELECT id AS Id, rule_version AS RuleVersion, plan_hash AS PlanHash, game_id AS GameId,
                       COALESCE(game_global_id, '') AS GameGlobalId, date AS Date, category AS Category,
                       before_count AS BeforeCount, after_count AS AfterCount, before_seconds AS BeforeSeconds,
                       after_seconds AS AfterSeconds, COALESCE(reason, '') AS Reason, applied_at_utc AS AppliedAtUtc
                FROM daily_count_migrations
                WHERE plan_hash = @planHash AND category <> 'rollback';
                """,
                new { planHash }, tx).ToList();

            var restored = 0;
            foreach (var l in ledger)
            {
                var affected = conn.Execute(
                    """
                    UPDATE daily_summary SET session_count = @before
                    WHERE game_id = @gameId AND date = @date AND session_count = @after;
                    """,
                    new { before = l.BeforeCount, after = l.AfterCount, gameId = l.GameId, date = l.Date }, tx);

                if (affected == 0) continue;

                conn.Execute(
                    """
                    INSERT INTO daily_count_migrations
                        (rule_version, plan_hash, game_id, game_global_id, date, category,
                         before_count, after_count, before_seconds, after_seconds, reason, applied_at_utc)
                    VALUES
                        (@ruleVersion, @planHash, @gameId, @gameGlobalId, @date, 'rollback',
                         @beforeCount, @afterCount, @beforeSeconds, @afterSeconds, '回滚到迁移前值', @appliedAtUtc);
                    """,
                    new
                    {
                        ruleVersion = l.RuleVersion,
                        planHash,
                        gameId = l.GameId,
                        gameGlobalId = l.GameGlobalId,
                        date = l.Date,
                        beforeCount = l.AfterCount,
                        afterCount = l.BeforeCount,
                        beforeSeconds = l.BeforeSeconds,
                        afterSeconds = l.AfterSeconds,
                        appliedAtUtc = UtcNowIso()
                    }, tx);

                restored++;
            }

            tx.Commit();
            AppLog.Info($"[场次迁移] 回滚 {restored} 行（指纹 {planHash[..Math.Min(12, planHash.Length)]}）");
            return restored;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ============================ 核心（可测、只读） ============================

    internal static DailyCountMigrationPlan PlanDailyCountMigrationCore(SqliteConnection conn, SqliteTransaction? tx)
    {
        var cutoff = ReadDailyCutoffHour(conn, tx);
        var rows = new List<DailyCountMigrationRow>();

        var games = conn.Query<MigrationGameRow>(
            "SELECT id AS Id, name AS Name, COALESCE(global_id, '') AS GlobalId, COALESCE(status, '') AS Status FROM games;",
            transaction: tx).ToDictionary(g => g.Id, g => g);

        // ⚠️ 2026-10-11：被用户显式删除的会话分片不计入事实口径（与运行时**同一份**排除集）。
        var exclusions = LoadAllPlayExclusions(conn, tx);

        // 会话事实按 (game, 会计日) 归集：起始会计日计数 + 分摊秒数 + 贡献会话
        var counted = new Dictionary<(long, string), int>();
        var contributing = new Dictionary<(long, string), int>();
        var factSeconds = new Dictionary<(long, string), int>();
        var legacySecondsOnStartDate = new Dictionary<(long, string), int>();
        var legacyCountedOnStartDate = new Dictionary<(long, string), int>();
        var orphanSessionSeconds = new Dictionary<long, int>();

        foreach (var s in conn.Query<MigrationSessionRow>(
                     """
                     SELECT id AS Id, game_id AS GameId, start_time AS StartTime, duration_seconds AS DurationSeconds,
                            COALESCE(global_id, '') AS GlobalId
                     FROM sessions;
                     """, transaction: tx))
        {
            if (!games.ContainsKey(s.GameId)) { orphanSessionSeconds[s.GameId] = 0; continue; }
            if (!TryParseWallClock(s.StartTime, out var start)) continue;

            var span = Math.Max(0, s.DurationSeconds);
            if (span <= 0) continue;

            var startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var accountingDate = DailyCountRule.AccountingDateOf(start, cutoff);

            // ⚠️ 2026-10-11：用户显式删除的分片不计入"当前事实口径"（`legacy*` 两个字典是
            //    **旧实现口径的模拟**，刻意保持原样 —— 它们的用途是识别 manualBonus 签名，不是事实）。
            if (DailyCountRule.IsCountedSession(span)
                && !IsSliceExcluded(exclusions, s.GameId, accountingDate, s.GlobalId))
            {
                counted[(s.GameId, accountingDate)] = counted.GetValueOrDefault((s.GameId, accountingDate)) + 1;
            }

            legacySecondsOnStartDate[(s.GameId, startDate)] =
                legacySecondsOnStartDate.GetValueOrDefault((s.GameId, startDate)) + span;

            // 旧实现（口径统一前）的口径是"日历日起始日 + ≥60s 计数"，保留它用于识别 manualBonus 签名
            if (DailyCountRule.IsCountedSession(span))
            {
                legacyCountedOnStartDate[(s.GameId, startDate)] =
                    legacyCountedOnStartDate.GetValueOrDefault((s.GameId, startDate)) + 1;
            }

            foreach (var d in GameSessionManager.ComputeDailyDeltas(start, start.AddSeconds(span), span, cutoff))
            {
                if (IsSliceExcluded(exclusions, s.GameId, d.Date, s.GlobalId)) continue;

                var key = (s.GameId, d.Date);
                factSeconds[key] = factSeconds.GetValueOrDefault(key) + d.DurationSeconds;
                contributing[key] = contributing.GetValueOrDefault(key) + 1;
            }
        }

        // 声明事实
        var adjSeconds = new Dictionary<(long, string), int>();
        var adjCount = new Dictionary<(long, string), int>();
        foreach (var a in conn.Query<MigrationAdjustmentRow>(
                     """
                     SELECT game_id AS GameId, statistical_date AS Date, duration_seconds AS DurationSeconds,
                            adjustment_global_id AS GlobalId
                     FROM daily_duration_adjustments;
                     """, transaction: tx))
        {
            var key = (a.GameId, a.Date);
            adjSeconds[key] = adjSeconds.GetValueOrDefault(key) + Math.Max(0, a.DurationSeconds);
            adjCount[key] = adjCount.GetValueOrDefault(key) + 1;

            // ⚠️ 声明是**事实的一部分**：期望秒数 = 会话派生 + 声明
            //    （漏掉这一步会把所有带声明的行误判成 unknown_residual —— 由契约测试抓到的真实缺陷）。
            factSeconds[key] = factSeconds.GetValueOrDefault(key) + Math.Max(0, a.DurationSeconds);

            if (!games.ContainsKey(a.GameId))
            {
                rows.Add(new DailyCountMigrationRow
                {
                    GameId = a.GameId,
                    Date = a.Date,
                    IsOrphanFact = true,
                    Category = DailyCountMigrationCategory.OrphanOrArchived,
                    Action = DailyCountMigrationAction.ManualReview,
                    Signals = new[] { DailyCountMigrationSignal.OrphanFact },
                    Reasons = new[] { $"孤儿声明 {a.GlobalId}：game 行不存在 ⇒ 只报告，不修改、不归属" },
                    AdjustmentCount = 1,
                    AdjustmentSeconds = Math.Max(0, a.DurationSeconds),
                    Reason = $"孤儿声明 {a.GlobalId}：game 行不存在 ⇒ 只报告，不修改、不归属"
                });
            }
        }

        foreach (var orphan in orphanSessionSeconds.Keys)
        {
            rows.Add(new DailyCountMigrationRow
            {
                GameId = orphan,
                IsOrphanFact = true,
                Category = DailyCountMigrationCategory.OrphanOrArchived,
                Action = DailyCountMigrationAction.ManualReview,
                Signals = new[] { DailyCountMigrationSignal.OrphanFact },
                Reasons = new[] { "孤儿会话：game 行不存在 ⇒ 只报告，不修改、不归属" },
                Reason = "孤儿会话：game 行不存在 ⇒ 只报告，不修改、不归属"
            });
        }

        // 派生行逐条判定
        foreach (var d in conn.Query<MigrationDailyRow>(
                     """
                     SELECT id AS Id, date AS Date, game_id AS GameId, duration_seconds AS DurationSeconds,
                            session_count AS SessionCount
                     FROM daily_summary;
                     """, transaction: tx))
        {
            var key = (d.GameId, d.Date);
            var hasGame = games.TryGetValue(d.GameId, out var game);
            var gameArchived = hasGame && GameStatuses.IsArchived(game!.Status);

            var facts = factSeconds.GetValueOrDefault(key);
            var residual = Math.Max(0, d.DurationSeconds - facts);
            var countedSessions = counted.GetValueOrDefault(key);
            var declarations = adjSeconds.GetValueOrDefault(key);
            var target = DailyCountRule.Compute(countedSessions, declarations, residual);

            // 归档 / 孤儿派生行**始终报告**（即使场次恰好一致）—— 否则这类风险在报告里完全看不见。
            var rowNeedsAttention = gameArchived || !hasGame || residual > 0 || target != d.SessionCount;
            if (!rowNeedsAttention) continue;

            // 旧 manualBonus 签名（与旧实现逐条对应）：当日**起始且 ≥60s** 的会话 ≥1（旧代码只在有效场次>0 时才写回），
            // 且 存值 = 该计数 + 1，且 存秒 > 当日起始会话秒和 + 60。
            var legacyCounted = legacyCountedOnStartDate.GetValueOrDefault((d.GameId, d.Date));
            var manualBonusSignature = legacyCounted >= 1
                && d.SessionCount == legacyCounted + 1
                && d.DurationSeconds > legacySecondsOnStartDate.GetValueOrDefault((d.GameId, d.Date)) + 60;
            var crossDay = contributing.GetValueOrDefault(key) > countedSessions;
            var bonusActive = DailyCountRule.HasDeclarationBonus(declarations, residual);
            var residueSuspected = d.SessionCount == 1 && declarations == 0 && countedSessions != 1 && d.DurationSeconds > 0;

            // ---------- 诊断信号：**可以同时成立**，逐条留痕（不再互相掩盖） ----------
            var signals = new List<DailyCountMigrationSignal>();
            var reasons = new List<string>();

            if (!hasGame)
            {
                signals.Add(DailyCountMigrationSignal.OrphanFact);
                reasons.Add("game 行不存在（孤儿派生行）⇒ 只报告，绝不建游戏、不归属");
            }
            else if (gameArchived)
            {
                signals.Add(DailyCountMigrationSignal.ArchivedGame);
                reasons.Add("游戏已归档（用户已删除）⇒ 不自动修改历史场次");
            }

            if (residual > 0)
            {
                signals.Add(DailyCountMigrationSignal.UnknownResidual);
                reasons.Add($"存在 {residual}s 残差（存值高于事实）：来源不可证实 ⇒ 保守保留，绝不清零");
            }

            if (manualBonusSignature)
            {
                signals.Add(DailyCountMigrationSignal.LegacyManualBonus);
                reasons.Add($"旧 manualBonus 签名（可证实的历史规则）：存值 = 当日起始会话数 + 1（{legacyCounted}+1）"
                          + $" 且 存秒 {d.DurationSeconds} > 起始日会话秒和 "
                          + $"{legacySecondsOnStartDate.GetValueOrDefault((d.GameId, d.Date))} + 60");
            }

            if (bonusActive)
            {
                signals.Add(DailyCountMigrationSignal.DeclarationBonus);
                reasons.Add($"声明 {declarations}s + 残差 {residual}s > 60 ⇒ 按契约计 1 次加成");
            }

            if (crossDay)
            {
                signals.Add(DailyCountMigrationSignal.CrossDay);
                reasons.Add($"跨日：本日有时长落入的会话 {contributing.GetValueOrDefault(key)} 条，"
                          + $"但起始在本日的仅 {countedSessions} 条");
            }

            if (residueSuspected)
            {
                signals.Add(DailyCountMigrationSignal.DefaultCountResidueSuspected);
                reasons.Add("存值 = 1 且无声明支撑、真实会话数 ≠ 1：**疑似**旧版默认值残留，"
                          + "但数据无法证明该 1 是「默认值残留」还是「历史事实后来变化/那天的确只玩了一场」"
                          + " ⇒ 转人工审核，保留原值");
            }

            if (signals.Count == 0) signals.Add(DailyCountMigrationSignal.FactComplete);

            // ---------- 迁移动作（唯一）：证据不足一律人工审核并保留原值 ----------
            var needsHuman = signals.Contains(DailyCountMigrationSignal.OrphanFact)
                          || signals.Contains(DailyCountMigrationSignal.ArchivedGame)
                          || signals.Contains(DailyCountMigrationSignal.UnknownResidual)
                          || signals.Contains(DailyCountMigrationSignal.DefaultCountResidueSuspected);

            var action = needsHuman
                ? DailyCountMigrationAction.ManualReview
                : DailyCountMigrationAction.AutoRecompute;

            // 台账兼容：保留单一 primary category（语义不变），动作/信号另存
            DailyCountMigrationCategory category;
            if (!hasGame || gameArchived) category = DailyCountMigrationCategory.OrphanOrArchived;
            else if (residual > 0) category = DailyCountMigrationCategory.UnknownResidual;
            else if (manualBonusSignature) category = DailyCountMigrationCategory.LegacyManualBonus;
            else if (bonusActive) category = DailyCountMigrationCategory.AdjustmentCounting;
            else if (crossDay) category = DailyCountMigrationCategory.CrossDayAccounting;
            else if (residueSuspected) category = DailyCountMigrationCategory.DefaultCountResidue;
            else category = DailyCountMigrationCategory.Recomputable;

            rows.Add(new DailyCountMigrationRow
            {
                GameId = d.GameId,
                GameGlobalId = hasGame ? game!.GlobalId : "",
                GameName = hasGame ? game!.Name : "(game 不存在)",
                Date = d.Date,
                DailyRowId = d.Id,
                StoredCount = d.SessionCount,
                TargetCount = target,
                StoredSeconds = d.DurationSeconds,
                FactSeconds = facts,
                ResidualSeconds = residual,
                CountedSessions = countedSessions,
                ContributingSessions = contributing.GetValueOrDefault(key),
                AdjustmentCount = adjCount.GetValueOrDefault(key),
                AdjustmentSeconds = declarations,
                GameArchived = gameArchived,
                Category = category,
                Action = action,
                Signals = signals,
                Reasons = reasons,
                Reason = string.Join("；", reasons)
            });
        }

        var ordered = rows.OrderBy(r => r.Date, StringComparer.Ordinal).ThenBy(r => r.GameId).ToList();
        return new DailyCountMigrationPlan
        {
            PlanHash = ComputePlanHash(ordered),
            GeneratedAtUtc = UtcNowIso(),
            Rows = ordered
        };
    }

    private static string ComputePlanHash(IReadOnlyList<DailyCountMigrationRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(DailyCountRule.Version).Append('\n');
        foreach (var r in rows)
        {
            sb.Append(r.GameId).Append('|').Append(r.Date).Append('|')
              .Append(r.DailyRowId).Append('|').Append(r.StoredCount).Append('|').Append(r.TargetCount).Append('|')
              .Append(r.StoredSeconds).Append('|').Append(r.FactSeconds).Append('|')
              .Append(r.Category).Append('|').Append((int)r.Action).Append('|').Append(r.SignalsText).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private sealed class MigrationGameRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string GlobalId { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private sealed class MigrationSessionRow
    {
        public long Id { get; set; }
        public long GameId { get; set; }
        public string? StartTime { get; set; }
        public int DurationSeconds { get; set; }
        public string GlobalId { get; set; } = "";
    }

    private sealed class MigrationAdjustmentRow
    {
        public long GameId { get; set; }
        public string Date { get; set; } = "";
        public int DurationSeconds { get; set; }
        public string GlobalId { get; set; } = "";
    }

    private sealed class MigrationDailyRow
    {
        public long Id { get; set; }
        public string Date { get; set; } = "";
        public long GameId { get; set; }
        public int DurationSeconds { get; set; }
        public int SessionCount { get; set; }
    }
}
