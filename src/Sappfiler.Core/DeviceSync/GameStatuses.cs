namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// <c>games.status</c> 的取值与「正常 Game」查询作用域的**唯一真相**。
///
/// <para>
/// 2026-10-07 用户批准（C5 / C-2）：用户删除 Game 时**不删除历史事实** ——
/// <c>sessions</c> 与 <c>daily_summary</c> 的 FK 都是 <c>ON DELETE CASCADE</c>，
/// 所以物理删除会连带抹掉历史 ✗。改为「历史归档」：保留 <c>games</c> 行作为 FK 目标，
/// 仅把 status 置为 <see cref="Archived"/>。
/// </para>
///
/// <para>
/// ⚠️ <see cref="Archived"/> 与 <see cref="Ignored"/> 是**两个不同概念**（用户明确要求不得混用）：
/// <list type="bullet">
/// <item><see cref="Ignored"/>：Game **仍然存在**，只是不参与某些同步/关联行为；</item>
/// <item><see cref="Archived"/>：Game **已被用户删除**，仅为历史事实保留。</item>
/// </list>
/// 因此 <c>GameRecord.IsIgnored</c>（<c>status == "ignored"</c>）**保持原义不变**，
/// 归档判定一律走 <see cref="IsArchived"/>。
/// </para>
/// </summary>
public static class GameStatuses
{
    /// <summary>正常游戏（默认）。</summary>
    public const string Active = "active";

    /// <summary>用户曾忽略：实体仍存在，只是不参与部分行为。</summary>
    public const string Ignored = "ignored";

    /// <summary>用户已删除：仅作为历史 Session / daily_summary 的 FK 目标而被保留。</summary>
    public const string Archived = "archived";

    public static bool IsArchived(string? status)
        => string.Equals(status, Archived, StringComparison.OrdinalIgnoreCase);

    public static bool IsIgnored(string? status)
        => string.Equals(status, Ignored, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 「正常 Game」查询作用域 —— **不带表别名**，直接 AND 进 <c>games</c> 的 WHERE。
    ///
    /// <para>
    /// 用它是为了让"正常 Game"成为**明确的语义**，而不是把 <c>AND status != 'archived'</c>
    /// 复制十几份（用户 2026-10-07 的第 1 条边界）。刻意只做这一个片段，
    /// 不引入 Repository 重构 / Specification Pattern。
    /// </para>
    /// <para>
    /// 注意：需要表别名时用 <see cref="NormalGamePredicateFor"/>（别名只允许 <c>g</c>/<c>games</c> 这类）。
    /// </para>
    /// </summary>
    public const string NormalGamePredicate = "LOWER(COALESCE(status, '')) <> '" + Archived + "'";

    /// <summary>带表别名的「正常 Game」判定（例：<c>g</c> ⇒ <c>LOWER(COALESCE(g.status,'')) &lt;&gt; 'archived'</c>）。</summary>
    public static string NormalGamePredicateFor(string alias)
        => $"LOWER(COALESCE({alias}.status, '')) <> '{Archived}'";
}
