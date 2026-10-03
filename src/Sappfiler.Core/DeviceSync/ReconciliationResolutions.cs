namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// Deferred 台账的**处理结论**（写入 <c>sync_deferred_changes.resolution</c>）。
///
/// ⚠️ <b>Resolved ≠ Completed</b>（用户 2026-10-03 冻结）：
/// 写 <c>resolved_at_utc</c> 只表示"这条 Deferred 已经过 Identity / Conflict Reconciliation，
/// 并且有了明确结论"；**台账行绝不删除**，因为它是审计资料 ——
/// 以后还要能回答"为什么曾经被延后 / 什么时候重新处理 / 最后怎么解决"。
///
/// 取值来自冻结契约（附录 I.8）；<see cref="Converged"/> 由用户本轮明确给出；
/// <see cref="BlockedImmutableFactMismatch"/> 是 Session 侧"identity 已确定但不可变事实互相矛盾"
/// 的结论码（与 Game 侧的强键冲突同源，但语义不同，故不共用）。
/// </summary>
public static class ReconciliationResolutions
{
    /// <summary>命中本地权威行并已并入（Game 侧）。</summary>
    public const string MergedLocalIdentity = "merged_local_identity";

    /// <summary>该 identity 已知被 supersede，直接重定向（幂等重放）。</summary>
    public const string RedirectedToCanonical = "redirected_to_canonical";

    /// <summary>本地命中行是 <c>ignored</c> → 保持不动（不再重试）。</summary>
    public const string LocalIgnoredProtected = "local_ignored_protected";

    /// <summary>本地已有同 <c>global_id</c>（2c 幂等）。</summary>
    public const string AlreadyPresent = "already_present";

    /// <summary>会话依赖的游戏已就绪，并已用**既有落地规则**补落地。</summary>
    public const string DependencyResolved = "dependency_resolved";

    /// <summary>强键明确冲突 → 刻意阻断（**这是决策，不是待办**）。</summary>
    public const string BlockedStrongKeyConflict = "blocked_strong_key_conflict";

    /// <summary>同一个 identity 却报了互相矛盾的不可变事实 → 阻断，拒绝改写历史。</summary>
    public const string BlockedImmutableFactMismatch = "blocked_immutable_fact_mismatch";

    /// <summary>已收敛（Session 侧：不同 global_id 但四项不可变事实一致）。</summary>
    public const string Converged = "converged";

    /// <summary>
    /// 台账里**没有可用的 payload 快照**（2c 时期的历史遗留 / payload 损坏 / 类型不符）→ 无法重处理。
    /// 这类条目必须收敛掉，否则每次启动都会白白重试。
    /// </summary>
    public const string LegacyNoPayload = "legacy_no_payload";
}
