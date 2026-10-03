namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 为什么判定"这两个 identity 其实是同一个实体"（写入
/// <c>sync_identity_supersessions.reason</c>）。
///
/// ⚠️ 这一整套语义与 <see cref="DeferredReasons"/>、以及"删除墓碑"是**三件不同的事**：
///   · 删除墓碑          = 实体被删除了
///   · Identity Supersession = 两套 identity 指向同一个实体（**不是删除**）
///   · Deferred          = 暂时无法应用、留痕待重处理
/// 三者不得混用。
/// </summary>
public static class SupersessionReasons
{
    /// <summary>启动去重：同名或同 Notion 页面把重复行合并掉（被淘汰行的 identity 并入权威行）。</summary>
    public const string StartupDedupDuplicateIdentity = "startup_dedup_duplicate_identity";

    /// <summary>2e 对账：强键命中（platform + platform_id 相等且适用）。</summary>
    public const string ReconcileStrongKey = "reconcile_strong_key";

    /// <summary>2e 对账：可执行文件路径（归一化）命中。</summary>
    public const string ReconcileExecutablePath = "reconcile_executable_path";

    /// <summary>2e 对账：可执行文件名命中。</summary>
    public const string ReconcileExecutableName = "reconcile_executable_name";

    /// <summary>2e 对账：归一化名称兜底命中（条件最严格）。</summary>
    public const string ReconcileNormalizedName = "reconcile_normalized_name";

    /// <summary>2e 对账：Session 判定为"同一不可变事实、两套身份"。</summary>
    public const string ReconcileSessionSameFact = "reconcile_session_same_fact";
}
