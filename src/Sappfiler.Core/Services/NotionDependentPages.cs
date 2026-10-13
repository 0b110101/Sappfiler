namespace GameTimeTracker.Core.Services;

/// <summary>
/// **依赖 Notion 游戏总表**的界面功能（2026-10-12，产品语义由用户裁定）。
///
/// <para>
/// 它们管理的都是"本地游戏 ↔ Notion 总表"之间的关系，而不是独立的本地功能：
/// <list type="bullet">
///   <item><see cref="PendingNav"/>「待处理」= **尚未绑定到总表**的游戏；</item>
///   <item><see cref="MappingsNav"/>「游戏映射库」= 本地游戏身份与**总表条目**之间的映射关系。</item>
/// </list>
/// 因此**没有绑定总表时它们没有任何可管理的对象** —— 一律不该出现。
/// 未来接入思源 / Obsidian 等其他 Provider 时，把判据换成"是否存在任一已绑定的总表 Provider"即可，
/// 页面与调用点都不必重排。
/// </para>
///
/// <para>
/// 本类只承载**规则**（谁依赖总表、不可见时怎么办），界面动作仍在 App 层执行。
/// 规则放在 Core 是为了能被自动化测试覆盖：App 工程是 WinUI，测试工程不引用它
/// ⇒ 若把规则写在界面代码里，就只能靠肉眼验证。
/// </para>
///
/// <para>
/// ⚠️ 与设备同步（WebDAV）**无关**：本类只判"是否绑定了总表"，不涉及任何同步协议、数据库结构。
/// </para>
/// </summary>
public static class NotionDependentPages
{
    /// <summary>首页 —— 任何时候都可用，作为"不可见页面"的兜底去向。</summary>
    public const string HomeNav = "Home";

    /// <summary>「待处理」（未绑定到 Notion 总表的游戏）。</summary>
    public const string PendingNav = "Pending";

    /// <summary>「游戏映射库」（本地游戏身份 ↔ Notion 总表条目）。</summary>
    public const string MappingsNav = "Mappings";

    /// <summary>全部依赖 Notion 总表的导航标签。</summary>
    public static readonly IReadOnlyList<string> All = new[] { PendingNav, MappingsNav };

    /// <summary>该导航标签是否依赖 Notion 总表。</summary>
    public static bool IsNotionDependent(string? navTag)
        => navTag is PendingNav or MappingsNav;

    /// <summary>
    /// 该导航项在当前绑定状态下是否应当出现。
    /// 非依赖页恒为 <c>true</c>（未知标签也按 <c>true</c> 处理：**绝不因为规则写错而误藏导航项**）。
    /// </summary>
    public static bool ShouldShowNav(string? navTag, bool notionConfigured)
        => !IsNotionDependent(navTag) || notionConfigured;

    /// <summary>
    /// 页面已不可见时，是否**必须切换到别的页面**（而不是留在已隐藏的页面上）。
    ///
    /// <list type="bullet">
    ///   <item>「游戏映射库」⇒ <c>true</c>：它的全部内容都是映射关系，没有"未绑定"空态，
    ///         留在上面只会是一屏无意义的列表 ⇒ 必须切走（用户裁定）。</item>
    ///   <item>「待处理」⇒ <c>false</c>：该页自身能给出诚实的空态说明，留在原页即可。</item>
    /// </list>
    /// </summary>
    public static bool MustNavigateAwayWhenUnavailable(string? navTag)
        => navTag == MappingsNav;

    /// <summary>
    /// 当前页因解绑而不可用时应当落到哪一页；无需切换 ⇒ <c>null</c>。
    /// 同时满足"依赖总表 + 未绑定 + 没有可用的自有空态"时才给出兜底目标。
    /// </summary>
    public static string? ResolveFallbackNav(string? currentNav, bool notionConfigured)
        => !notionConfigured
           && IsNotionDependent(currentNav)
           && MustNavigateAwayWhenUnavailable(currentNav)
            ? HomeNav
            : null;

    /// <summary>
    /// 某个导航标签在绑定状态变化后是否应当**离开**（用于"用户正停在该页"的处置判断）。
    /// </summary>
    public static bool ShouldLeaveAfterBindingChange(string? currentNav, bool notionConfigured)
        => ResolveFallbackNav(currentNav, notionConfigured) is not null;
}
