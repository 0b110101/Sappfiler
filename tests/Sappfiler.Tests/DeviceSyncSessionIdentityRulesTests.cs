using FluentAssertions;
using GameTimeTracker.Core.DeviceSync;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 2e 步骤 6 验收（纯函数层）：**Session 身份判定矩阵**。
///
/// Session 是**历史事实**，不是配置对象。这个矩阵刻意证明三件事：
///   ① 只有四项不可变事实（device_id + started_at_utc + duration_seconds + game identity）
///      完全一致才允许收敛；
///   ② 任一项不一致 → Deferred，**绝不用 LWW / 取最大 duration / 取最新时间戳裁决**；
///   ③ <see cref="SessionFacts"/> 里**根本没有** ended_at / created_at / process_name
///      —— 结构上就不存在"用它们裁决"的可能。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public class DeviceSyncSessionIdentityRulesTests
{
    private const string DeviceA = "device-A";
    private const string GameG = "game-global-1";
    private const string Start = "2026-10-01T10:00:00Z";

    private static SessionFacts Fact(
        string globalId, string device = DeviceA, string start = Start,
        int duration = 600, string game = GameG)
        => new(globalId, device, start, duration, game);

    public sealed record Case(
        string Name,
        SessionFacts Remote,
        SessionFacts[] Locals,
        SessionIdentityDecisionKind ExpectedKind,
        string ExpectedReason,
        int? ExpectedCanonicalLocalId = null);

    public static TheoryData<Case> Matrix() => new()
    {
        // ── ① 同一个 global_id：幂等 / 拒绝改写历史 ──────────────────────
        new("同 global_id + 事实一致 → 幂等（SameIdentity）",
            Fact("s-1"), new[] { Fact("s-1") },
            SessionIdentityDecisionKind.SameIdentity, SessionIdentityDecisionReasons.SameGlobalId, 1),

        new("同 global_id 但 duration 不同 → Blocked（拒绝改写历史）",
            Fact("s-1", duration: 900), new[] { Fact("s-1", duration: 600) },
            SessionIdentityDecisionKind.Blocked, SessionIdentityDecisionReasons.ImmutableFactMismatch),

        // ── ② 不同 global_id：四项全同才收敛 ─────────────────────────────
        new("不同 global_id + 四项不可变事实全同 → EquivalentFact",
            Fact("s-remote"), new[] { Fact("s-local") },
            SessionIdentityDecisionKind.EquivalentFact, SessionIdentityDecisionReasons.SameImmutableFact, 1),

        new("started_at 时区写法不同但同一时刻 → 仍然等价",
            Fact("s-remote", start: "2026-10-01T10:00:00Z"),
            new[] { Fact("s-local", start: "2026-10-01T10:00:00") },
            SessionIdentityDecisionKind.EquivalentFact, SessionIdentityDecisionReasons.SameImmutableFact, 1),

        // ── ③ 任一项不一致 → Deferred（绝不裁决谁对） ────────────────────
        new("device_id 不同 → Deferred",
            Fact("s-remote", device: "device-B"), new[] { Fact("s-local", device: DeviceA) },
            SessionIdentityDecisionKind.Deferred, SessionIdentityDecisionReasons.ImmutableFactMismatch),

        new("started_at 不同 → Deferred",
            Fact("s-remote", start: "2026-10-01T11:00:00Z"), new[] { Fact("s-local", start: Start) },
            SessionIdentityDecisionKind.Deferred, SessionIdentityDecisionReasons.ImmutableFactMismatch),

        new("★ duration 更大 → Deferred（**证明没有 '取最大 duration' 这种隐藏裁决**）",
            Fact("s-remote", duration: 3700), new[] { Fact("s-local", duration: 3600) },
            SessionIdentityDecisionKind.Deferred, SessionIdentityDecisionReasons.ImmutableFactMismatch),

        new("game identity 不同 → Deferred（四项里最容易漏的一项）",
            Fact("s-remote", game: "game-global-2"), new[] { Fact("s-local", game: GameG) },
            SessionIdentityDecisionKind.Deferred, SessionIdentityDecisionReasons.ImmutableFactMismatch),

        // ── 其它边界 ──────────────────────────────────────────────────
        new("多条等价 → Deferred（不猜）",
            Fact("s-remote"),
            new[] { Fact("s-local-1"), Fact("s-local-2") },
            SessionIdentityDecisionKind.Deferred, SessionIdentityDecisionReasons.MultipleEquivalentFacts),

        new("远端缺 device_id → 信息不足，不允许收敛",
            Fact("s-remote", device: ""), new[] { Fact("s-local") },
            SessionIdentityDecisionKind.Deferred, SessionIdentityDecisionReasons.InsufficientImmutableFact),

        new("远端 duration = 0 → 信息不足，不允许收敛",
            Fact("s-remote", duration: 0), new[] { Fact("s-local", duration: 0) },
            SessionIdentityDecisionKind.Deferred, SessionIdentityDecisionReasons.InsufficientImmutableFact),

        new("远端缺 game identity → 信息不足，不允许收敛",
            Fact("s-remote", game: ""), new[] { Fact("s-local", game: "") },
            SessionIdentityDecisionKind.Deferred, SessionIdentityDecisionReasons.InsufficientImmutableFact),

        new("本地无候选 → NoLocalCandidate",
            Fact("s-remote"), Array.Empty<SessionFacts>(),
            SessionIdentityDecisionKind.NoLocalCandidate, SessionIdentityDecisionReasons.NoCandidate),
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void SessionIdentityMatrix_CoversEveryBranch(Case testCase)
    {
        var locals = testCase.Locals
            .Select((facts, index) => new LocalSessionCandidate(index + 1, facts))
            .ToList();

        var decision = SessionIdentityRules.Decide(testCase.Remote, locals);

        decision.Kind.Should().Be(testCase.ExpectedKind, $"[{testCase.Name}]");
        decision.Reason.Should().Be(testCase.ExpectedReason, $"[{testCase.Name}]");
        decision.CanonicalLocalId.Should().Be(testCase.ExpectedCanonicalLocalId, $"[{testCase.Name}]");
        decision.AllowsConvergence.Should().Be(
            testCase.ExpectedKind == SessionIdentityDecisionKind.EquivalentFact, $"[{testCase.Name}]");
    }

    [Fact]
    public void SessionFacts_HasNoMutableFields_SoNoLwwIsEvenPossible()
    {
        // 结构性保证：判定输入里没有 ended_at / created_at / process_name / 时间精度 之类的可变字段，
        // 因此"谁更新谁胜""名字像就合并"这类裁决在类型层面就不可能实现。
        var properties = typeof(SessionFacts).GetProperties().Select(p => p.Name).ToArray();

        properties.Should().BeEquivalentTo(new[]
        {
            nameof(SessionFacts.GlobalId),
            nameof(SessionFacts.DeviceId),
            nameof(SessionFacts.StartedAtUtc),
            nameof(SessionFacts.DurationSeconds),
            nameof(SessionFacts.GameGlobalId)
        });
    }
}
