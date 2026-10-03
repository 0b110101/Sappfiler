using System.Globalization;

namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 一条 Session 的**不可变事实**。
///
/// ⚠️ Session 是**历史事实**，不是配置对象：
///   · 它没有"谁更新谁胜"；
///   · duration 不是一个"可以取最大"的属性；
///   · ended_at 也不是"谁新谁对"。
/// 因此判定"是不是同一局"只能看下面这四项**不可变事实**，别的字段（进程名、时间精度等）
/// 都不参与收敛判定。
/// </summary>
public sealed record SessionFacts(
    string? GlobalId,
    string? DeviceId,
    string? StartedAtUtc,
    int DurationSeconds,
    string? GameGlobalId);

/// <summary>本地候选会话（带本地主键，用于返回"收敛到哪一条"）。</summary>
public sealed record LocalSessionCandidate(int LocalId, SessionFacts Facts);

/// <summary>Session 身份判定类别。</summary>
public enum SessionIdentityDecisionKind
{
    /// <summary>本地已有**同一个 global_id** 的会话 → 幂等跳过（不写任何东西）。</summary>
    SameIdentity,

    /// <summary>global_id 不同，但四项不可变事实**完全一致** → 收敛（写 supersession）。</summary>
    EquivalentFact,

    /// <summary>无法确认（事实不一致 / 多候选 / 信息不足）→ 延后，绝不猜。</summary>
    Deferred,

    /// <summary>**拒绝改写历史**：同一个 global_id 却报了不同的事实。</summary>
    Blocked,

    /// <summary>本地一个候选都没有（不归本机制管，调用方按正常落地处理）。</summary>
    NoLocalCandidate
}

/// <summary>Session 判定的原因码。</summary>
public static class SessionIdentityDecisionReasons
{
    public const string SameGlobalId = "same_session_global_id";
    public const string SameImmutableFact = "same_immutable_fact";

    /// <summary>四项不可变事实不一致 → 延后（**绝不**用 LWW / 取最大 / 取最新来裁决）。</summary>
    public const string ImmutableFactMismatch = "immutable_fact_mismatch";

    /// <summary>存在多条等价事实 → 不猜。</summary>
    public const string MultipleEquivalentFacts = "multiple_equivalent_facts";

    /// <summary>四项不可变事实不完整（缺 device / start / duration / game identity）→ 无法确认身份。</summary>
    public const string InsufficientImmutableFact = "insufficient_immutable_fact";

    public const string NoCandidate = "no_candidate";
}

/// <summary>Session 身份判定结果。</summary>
public sealed record SessionIdentityDecision(
    SessionIdentityDecisionKind Kind,
    int? CanonicalLocalId,
    string Reason)
{
    /// <summary>是否允许把远端 identity 收敛到 <see cref="CanonicalLocalId"/> 指向的那一条。</summary>
    public bool AllowsConvergence
        => Kind == SessionIdentityDecisionKind.EquivalentFact && CanonicalLocalId is not null;
}

/// <summary>
/// Session 身份判定规则 —— **纯函数，无 IO、无数据库**（因此可穷举测试）。
///
/// 判定顺序（用户 2026-10-03 冻结）：
/// <code>
/// 同 global_id + 四项事实一致   → SameIdentity     （幂等，置空操作）
/// 同 global_id + 事实不一致     → Blocked          （拒绝改写历史）
/// 不同 global_id + 四项一致     → EquivalentFact   （收敛：只写 supersession）
/// 不同 global_id + 任一项不一致 → Deferred         （**绝不用 LWW / max duration 裁决**）
/// 多条等价                      → Deferred
/// 四项信息不完整                → Deferred
/// 本地无候选                    → NoLocalCandidate
/// </code>
///
/// ⚠️ 本类**只做判定**：数据库查询、会话合并、supersession 写入都在持久化层，
/// 不要把三者塞进规则引擎（用户要求保持"纯函数判定 → 持久化执行"两层结构）。
/// </summary>
public static class SessionIdentityRules
{
    /// <summary>四项不可变事实是否齐全（缺任何一项都无法确认身份 → 不允许收敛）。</summary>
    public static bool HasAllImmutableFacts(SessionFacts facts)
        => !string.IsNullOrWhiteSpace(facts.DeviceId)
        && !string.IsNullOrWhiteSpace(facts.StartedAtUtc)
        && facts.DurationSeconds > 0
        && !string.IsNullOrWhiteSpace(facts.GameGlobalId);

    /// <summary>
    /// 四条不可变事实是否**逐项一致**：device_id + started_at_utc + duration_seconds + game identity。
    /// 任何一项不一致 → false（调用方据此延后，**不做任何"取最新/取最大"裁决**）。
    /// </summary>
    public static bool SameImmutableFact(SessionFacts a, SessionFacts b)
        => HasAllImmutableFacts(a)
        && HasAllImmutableFacts(b)
        && string.Equals(a.DeviceId!.Trim(), b.DeviceId!.Trim(), StringComparison.OrdinalIgnoreCase)
        && SameInstant(a.StartedAtUtc, b.StartedAtUtc)
        && a.DurationSeconds == b.DurationSeconds
        && string.Equals(a.GameGlobalId!.Trim(), b.GameGlobalId!.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 两个 UTC 时间串是否表示同一时刻。
    /// 优先按时间解析比较（容忍格式差异），解析不了才退回字符串比较 —— 不引入任何"谁更新"语义。
    /// </summary>
    public static bool SameInstant(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        if (DateTime.TryParse(a, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var left)
            && DateTime.TryParse(b, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var right))
        {
            return left == right;
        }

        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static SessionIdentityDecision Decide(
        SessionFacts remote,
        IReadOnlyList<LocalSessionCandidate> localCandidates)
    {
        if (localCandidates.Count == 0)
        {
            return new SessionIdentityDecision(
                SessionIdentityDecisionKind.NoLocalCandidate, null, SessionIdentityDecisionReasons.NoCandidate);
        }

        // ── 1. 同一个 global_id ────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(remote.GlobalId))
        {
            var sameIdentity = localCandidates
                .Where(c => string.Equals(c.Facts.GlobalId, remote.GlobalId, StringComparison.Ordinal))
                .ToList();

            if (sameIdentity.Count > 0)
            {
                // 事实一致 → 幂等（置空操作）；事实不一致 → **拒绝改写历史**
                return sameIdentity.Any(c => SameImmutableFact(remote, c.Facts))
                    ? new SessionIdentityDecision(SessionIdentityDecisionKind.SameIdentity,
                        sameIdentity[0].LocalId, SessionIdentityDecisionReasons.SameGlobalId)
                    : new SessionIdentityDecision(SessionIdentityDecisionKind.Blocked, null,
                        SessionIdentityDecisionReasons.ImmutableFactMismatch);
            }
        }

        // ── 2. 不同 global_id：只有当四项不可变事实齐全时才可能收敛 ─────────
        if (!HasAllImmutableFacts(remote))
        {
            return new SessionIdentityDecision(SessionIdentityDecisionKind.Deferred, null,
                SessionIdentityDecisionReasons.InsufficientImmutableFact);
        }

        var equivalent = localCandidates.Where(c => SameImmutableFact(remote, c.Facts)).ToList();

        if (equivalent.Count == 1)
        {
            return new SessionIdentityDecision(SessionIdentityDecisionKind.EquivalentFact,
                equivalent[0].LocalId, SessionIdentityDecisionReasons.SameImmutableFact);
        }

        if (equivalent.Count > 1)
        {
            return new SessionIdentityDecision(SessionIdentityDecisionKind.Deferred, null,
                SessionIdentityDecisionReasons.MultipleEquivalentFacts);
        }

        // 有候选、但没有一条与它等价 → 事实不一致 → 延后（**绝不裁决谁对**）
        return new SessionIdentityDecision(SessionIdentityDecisionKind.Deferred, null,
            SessionIdentityDecisionReasons.ImmutableFactMismatch);
    }
}
