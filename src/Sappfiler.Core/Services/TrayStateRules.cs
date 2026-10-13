namespace GameTimeTracker.Core.Services;

/// <summary>
/// <b>系统托盘状态的纯决策规则</b>（2026-10-13）。
///
/// <para>
/// 背景（真机反馈的缺陷）：托盘原先的"同步异常"红灯来自一个**字符串匹配的 latch**
/// （`_lastSyncFailed`）：只要收到过一条含「失败/异常」的同步状态消息就置位，
/// 只有收到含「成功/就绪/完成」的消息才清除。而**未绑定 Notion 时所有同步入口都提前返回、
/// 一条状态消息都不产生** ⇒ 绑定期间留下的失败结论在解绑后**永远清不掉**，
/// 托盘就会在"根本没有同步可做"的情况下长期显示红色「同步异常」，纯属误导。
/// </para>
///
/// <para>
/// 本类把两件事下沉为**纯函数**，便于自动化测试覆盖（托盘本体是 WinUI，测试工程不引用它）：
/// <list type="number">
///   <item><see cref="Resolve"/>：当前该显示哪个状态（优先级 + "配置闸门"）；</item>
///   <item><see cref="ApplyStatusMessage"/>：收到一条同步状态消息后，新的"最近一次同步是否失败"结论。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 范围的**刻意边界**：本类只处理 <b>Notion</b> 同步状态。
/// 设备同步（WebDAV）的失败**不参与**托盘状态（用户 2026-10-13 裁定，另案评估）。
/// </para>
/// </summary>
public static class TrayStateRules
{
    /// <summary>有游戏正在计时（优先级最高）。</summary>
    public const string Gaming = "gaming";

    /// <summary>同步失败（**仅在绑定了 Notion 时**才可能）。</summary>
    public const string Error = "error";

    /// <summary>有未绑定到总表的游戏等待处理（**仅在绑定了 Notion 时**才可能）。</summary>
    public const string Pending = "pending";

    /// <summary>空闲。</summary>
    public const string Idle = "idle";

    /// <summary>失败标记：命中任意一个即视为"这次同步失败了"。</summary>
    private static readonly string[] FailureMarkers = { "失败", "异常" };

    /// <summary>
    /// 成功标记：命中任意一个（且**没有**失败标记）才视为"这次同步成功了"。
    ///
    /// <para>
    /// 2026-10-13 扩充：原集合只有「成功/就绪/完成」，而真实业务里的成功文案几乎不含这些词
    /// （如 <c>游戏总表快照已是最新（共 N 款）</c>、<c>已同步 N 条记录</c>）⇒ 一次**成功**同步
    /// 不会清除此前的失败结论，托盘会一直挂着红灯。现补入两类真实文案：
    /// <list type="bullet">
    ///   <item><b>「已是最新」</b>：<c>全部记录已是最新</c>、<c>游戏总表快照已是最新（共 N 款）</c>；</item>
    ///   <item><b>「已同步」</b>：<c>已同步 N 条记录</c>、<c>游戏总表快照已同步（清理 N 条已失效条目）</c>。</item>
    /// </list>
    /// 这样"再次成功同步 ⇒ 清除历史错误"才真正成立（同步链第一步就是刷新总表快照）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 安全性来自 <see cref="IsSuccessMessage"/> 的**失败优先**：任何同时含「失败/异常」的消息
    /// 都不会被当成成功（例如 <c>已同步 20 条记录，失败 2 条</c>）。
    /// </para>
    /// </summary>
    private static readonly string[] SuccessMarkers = { "成功", "就绪", "完成", "已同步", "已是最新" };

    /// <summary>
    /// 当前该显示哪个托盘状态。
    ///
    /// <para>
    /// 优先级：<see cref="Gaming"/> &gt; <see cref="Error"/> &gt; <see cref="Pending"/> &gt; <see cref="Idle"/>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>"配置闸门"</b>：<paramref name="notionConfigured"/> 为 <c>false</c>（未绑定总表）时
    /// **绝不允许**返回 <see cref="Error"/> 或 <see cref="Pending"/> —— 没有总表就没有"同步失败"、
    /// 也没有"待绑定"这回事，历史失败结论一律作废。
    /// </para>
    /// </summary>
    /// <param name="hasActiveSession">是否有游戏正在计时。</param>
    /// <param name="lastSyncFailed">最近一次同步的结论（可能来自更早、绑定期间的失败）。</param>
    /// <param name="notionConfigured">当前是否绑定了 Notion 总表。</param>
    /// <param name="unhandledPendingCount">未忽略的待处理（未绑定总表）游戏数量。</param>
    public static string Resolve(
        bool hasActiveSession, bool lastSyncFailed, bool notionConfigured, int unhandledPendingCount)
    {
        if (hasActiveSession) return Gaming;
        if (lastSyncFailed && notionConfigured) return Error;
        if (notionConfigured && unhandledPendingCount > 0) return Pending;
        return Idle;
    }

    /// <summary>消息里是否含失败标记（「失败」/「异常」）。</summary>
    public static bool IsFailureMessage(string? status)
        => ContainsAny(status, FailureMarkers);

    /// <summary>
    /// 消息是否可以被当作"明确成功"。
    ///
    /// <para>
    /// ⚠️ <b>失败优先</b>：同时含失败标记与成功标记的**复合消息**（例如
    /// 「历史记录格式规范化完成！共扫描 30 条：规范化 20 条，失败 2 条」、
    /// 「已同步 20 条记录，失败 2 条」）**一律不算成功** ——
    /// 简单用字符串包含去判断"完成/已同步"会把部分成功误判为全成功。
    /// </para>
    /// </summary>
    public static bool IsSuccessMessage(string? status)
        => !IsFailureMessage(status) && ContainsAny(status, SuccessMarkers);

    /// <summary>
    /// 收到一条同步状态消息后，新的"最近一次同步是否失败"结论。
    ///
    /// <list type="bullet">
    ///   <item>未绑定总表 ⇒ 恒为 <c>false</c>：既不记录新的失败，也把旧结论**清除**；</item>
    ///   <item>含失败标记 ⇒ <c>true</c>（失败优先，复合消息也算失败）；</item>
    ///   <item>明确成功 ⇒ <c>false</c>；</item>
    ///   <item><b>其它一律保持不变</b>（进度提示、空文本、未知文案都不得误清或误置已有结论）。</item>
    /// </list>
    /// </summary>
    public static bool ApplyStatusMessage(bool lastSyncFailed, bool notionConfigured, string? status)
    {
        if (!notionConfigured) return false;
        if (IsFailureMessage(status)) return true;
        if (IsSuccessMessage(status)) return false;
        return lastSyncFailed;
    }

    private static bool ContainsAny(string? text, string[] markers)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
