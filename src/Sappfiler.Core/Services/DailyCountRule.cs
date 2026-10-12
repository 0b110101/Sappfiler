namespace GameTimeTracker.Core.Services;

/// <summary>
/// <b>日统计「场次」口径的唯一契约实现</b>（纯计算，不碰数据库）。
///
/// <para>
/// 所有会写 <c>daily_summary.session_count</c> 的路径（心跳、会话结束、远端落地、
/// 时长增量、Notion 导入、身份合并、一致性重算、历史迁移）都必须通过本类计算，
/// <b>不允许再各自手写累加公式</b>。契约全文见 <c>docs/DailyStatisticsContract.md</c>。
/// </para>
///
/// <para>
/// 规则（v1）：
/// <list type="number">
///   <item>只有时长 <b>≥60 秒</b> 的 Session 才算「有效场次」；</item>
///   <item>Session 的场次归属其**起始时刻的会计日**，跨日不重复计次；</item>
///   <item>Adjustment 声明与历史残差**合并成一个 bonus 项**：二者之和 &gt; 60 秒时 +1 次
///         （因此二者永远不会各自触发、也不会重复计数）；</item>
///   <item>重复重算同一份事实必须得到完全相同的值。</item>
/// </list>
/// </para>
/// </summary>
public static class DailyCountRule
{
    /// <summary>规则版本（写入迁移台账 <c>rule_version</c>，便于事后追溯）。</summary>
    public const string Version = "count-rule-v1";

    /// <summary>计入场次的最小 Session 时长（秒）。</summary>
    public const int MinCountedSessionSeconds = 60;

    /// <summary>声明/残差触发一次额外场次的阈值（秒）：严格大于该值才 +1。</summary>
    public const int DeclarationBonusThresholdSeconds = 60;

    /// <summary>该时长的 Session 是否算一场「有效游玩」。</summary>
    public static bool IsCountedSession(long durationSeconds)
        => durationSeconds >= MinCountedSessionSeconds;

    /// <summary>
    /// 是否存在「外部声明 / 历史残差」带来的额外场次。
    /// ⚠️ 声明与残差**相加后只判定一次** —— 这是"二者不会重复计数"的保证。
    /// </summary>
    public static bool HasDeclarationBonus(long declarationSeconds, long residualSeconds)
        => declarationSeconds + Math.Max(0, residualSeconds) > DeclarationBonusThresholdSeconds;

    /// <summary>
    /// 计算某 (game, 会计日) 的目标场次。
    /// </summary>
    /// <param name="countedSessions">该会计日内**起始**且时长 ≥60 秒的 Session 条数。</param>
    /// <param name="declarationSeconds">该统计日全部有效 Adjustment 声明秒数之和。</param>
    /// <param name="residualSeconds">来源不明的历史残差秒数（受保护，绝不归零）。</param>
    public static int Compute(int countedSessions, long declarationSeconds, long residualSeconds = 0)
        => countedSessions + (HasDeclarationBonus(declarationSeconds, residualSeconds) ? 1 : 0);

    /// <summary>Session 起始时刻归属的会计日（统一走既有会计日逻辑）。</summary>
    public static string AccountingDateOf(DateTime startLocalWallClock, int cutoffHour)
        => AccountingDateHelper.GetAccountingDateString(startLocalWallClock, cutoffHour);
}
