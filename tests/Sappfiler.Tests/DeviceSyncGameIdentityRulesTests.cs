using FluentAssertions;
using GameTimeTracker.Core.DeviceSync;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 2e 步骤 3/4 验收：**Game 身份判定矩阵**。
///
/// 规则引擎是纯函数，所以这里可以按"每一级 × 每种结局"穷举，
/// 而不是只测几个 happy path。重点钉死三条：
///   ① **A 适用且不等 → 阻断，绝不继续 B/C/D**（本阶段最关键的一条）；
///   ② 「A 不适用」≠「A 冲突」：前者允许落到 B，后者必须阻断；
///   ③ ignored 绝对优先，且在**任何一级**命中都要阻断。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支。
/// </summary>
public class DeviceSyncGameIdentityRulesTests
{
    // ==================== 矩阵用例定义 ====================

    public sealed record LocalSpec(
        string? Platform = null,
        string? PlatformId = null,
        string? Exe = null,
        string? ExePath = null,
        string? Name = null,
        string? NotionPageId = null,
        bool IsIgnored = false,
        int LocalId = 1);

    public sealed record RuleCase(
        string Name,
        GameIdentityFacts Remote,
        LocalSpec[] Locals,
        IdentityDecisionKind ExpectedKind,
        IdentityMatchLevel ExpectedLevel,
        string ExpectedReason,
        int? ExpectedLocalId = null);

    private static GameIdentityFacts Steam(
        string appId, string exe = "foo.exe", string path = @"D:\Games\foo\foo.exe",
        string name = "Foo", string? notion = null)
        => new("steam", appId, exe, path, name, notion);

    private static LocalSpec LocalSteam(
        string appId, string exe = "foo.exe", string path = @"D:\Games\foo\foo.exe",
        string name = "Foo", string? notion = null, bool ignored = false, int id = 1)
        => new("steam", appId, exe, path, name, notion, ignored, id);

    public static TheoryData<RuleCase> DecisionMatrix() => new()
    {
        // ── A 级：强键 ────────────────────────────────────────────────
        new("A 相等（唯一）→ 命中 A",
            Steam("123456"), new[] { LocalSteam("123456") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.StrongKey,
            IdentityDecisionReasons.StrongKeyEqual, 1),

        new("A 相等（两个候选）→ 不猜，Deferred",
            Steam("123456"), new[] { LocalSteam("123456", id: 1), LocalSteam("123456", id: 2) },
            IdentityDecisionKind.Deferred, IdentityMatchLevel.StrongKey,
            IdentityDecisionReasons.MultipleCandidates, null),

        new("★ A 适用且不等（exe 路径与文件名全同）→ 阻断，绝不放行弱键",
            Steam("123456"), new[] { LocalSteam("999999") },
            IdentityDecisionKind.Blocked, IdentityMatchLevel.StrongKey,
            IdentityDecisionReasons.StrongKeyConflict, null),

        new("★ A 适用且不等（连名称也相同）→ 仍然阻断",
            Steam("123456", name: "Foo"), new[] { LocalSteam("999999", name: "Foo") },
            IdentityDecisionKind.Blocked, IdentityMatchLevel.StrongKey,
            IdentityDecisionReasons.StrongKeyConflict, null),

        new("A 适用且不等（本地有多个不同强键）→ 阻断",
            Steam("123456"),
            new[] { LocalSteam("777777", id: 1), LocalSteam("999999", id: 2) },
            IdentityDecisionKind.Blocked, IdentityMatchLevel.StrongKey,
            IdentityDecisionReasons.StrongKeyConflict, null),

        // ── A 的适用性边界（本地侧）────────────────────────────────────
        new("远程有强键、本地是 manual → A 不适用 → 落 B 命中",
            Steam("123456"),
            new[] { new LocalSpec("manual", "123456", "foo.exe", @"D:\Games\foo\foo.exe", "Foo") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.ExecutablePath,
            IdentityDecisionReasons.ExecutablePathMatch, 1),

        new("远程有强键、本地 platform_id 是 8 位（疑似本地 id）→ A 不适用 → 落 B",
            Steam("123456"),
            new[] { new LocalSpec("steam", "abcdef12", "foo.exe", @"D:\Games\foo\foo.exe", "Foo") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.ExecutablePath,
            IdentityDecisionReasons.ExecutablePathMatch, 1),

        new("远程有强键、本地 platform_id 是 16 位（疑似本地 id）→ A 不适用 → 落 B",
            Steam("123456"),
            new[] { new LocalSpec("steam", "abcdef1234567890", "foo.exe", @"D:\Games\foo\foo.exe", "Foo") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.ExecutablePath,
            IdentityDecisionReasons.ExecutablePathMatch, 1),

        new("远程是 manual → A 不适用（远程侧）→ 落 B",
            new GameIdentityFacts("manual", "123456", "foo.exe", @"D:\Games\foo\foo.exe", "Foo"),
            new[] { LocalSteam("123456") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.ExecutablePath,
            IdentityDecisionReasons.ExecutablePathMatch, 1),

        // ── B 级：可执行文件路径 ──────────────────────────────────────
        // ⚠️ 下面这几条刻意让本地是 manual（= A 不适用）：若本地也有 steam 强键，
        //    A 冲突会先阻断 —— 那不是测试妥协，而是规则本身如此（见上面 ★ 两条）。
        new("B 相等（唯一）→ 命中 B",
            Steam("123456", exe: "bar.exe", path: @"D:\Games\bar\bar.exe"),
            new[] { new LocalSpec("manual", "", "foo.exe", @"D:\Games\bar\bar.exe", "Bar") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.ExecutablePath,
            IdentityDecisionReasons.ExecutablePathMatch, 1),

        new("B 相等（两个候选）→ Deferred",
            Steam("123456", exe: "bar.exe", path: @"D:\Games\bar\bar.exe"),
            new[]
            {
                new LocalSpec("manual", "", "foo.exe", @"D:\Games\bar\bar.exe", "Bar", null, false, 1),
                new LocalSpec("manual", "", "baz.exe", @"D:\Games\bar\bar.exe", "Baz", null, false, 2)
            },
            IdentityDecisionKind.Deferred, IdentityMatchLevel.ExecutablePath,
            IdentityDecisionReasons.MultipleCandidates, null),

        new("B 可用但不等（弱键不阻断）→ 落 C 命中",
            Steam("123456", exe: "foo.exe", path: @"D:\Other\foo.exe"),
            new[] { new LocalSpec("manual", "", "foo.exe", @"D:\Games\foo\foo.exe", "Foo") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.ExecutableName,
            IdentityDecisionReasons.ExecutableNameMatch, 1),

        // ── C 级：可执行文件名 ───────────────────────────────────────
        new("C 相等（唯一）→ 命中 C",
            Steam("123456", exe: "foo.exe", path: @"D:\Other\foo.exe", name: "Foo"),
            new[] { new LocalSpec("manual", "", "FOO.EXE", @"D:\Games\foo\foo.exe", "Foo") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.ExecutableName,
            IdentityDecisionReasons.ExecutableNameMatch, 1),

        new("C 相等（两个候选）→ Deferred",
            Steam("123456", exe: "foo.exe", path: @"D:\Other\foo.exe"),
            new[]
            {
                new LocalSpec("manual", "", "foo.exe", @"D:\Games\a\foo.exe", "A", null, false, 1),
                new LocalSpec("manual", "", "foo.exe", @"D:\Games\b\foo.exe", "B", null, false, 2)
            },
            IdentityDecisionKind.Deferred, IdentityMatchLevel.ExecutableName,
            IdentityDecisionReasons.MultipleCandidates, null),

        new("B/C 都不可比（双方都无 exe/path）→ 落 D 命中",
            new GameIdentityFacts("manual", "", null, null, "Foo"),
            new[] { new LocalSpec("manual", "", null, null, "Foo") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.NormalizedName,
            IdentityDecisionReasons.NormalizedNameMatch, 1),

        // ── D 级：名称兜底（极保守） ─────────────────────────────────
        new("D：两侧绑定不同 Notion 页面 → Deferred（合并会毁掉一个 Provider 身份）",
            new GameIdentityFacts("manual", "1", null, null, "Foo", "page-a"),
            new[] { new LocalSpec("manual", "2", null, null, "Foo", "page-b") },
            IdentityDecisionKind.Deferred, IdentityMatchLevel.NormalizedName,
            IdentityDecisionReasons.NotionPageConflict, null),

        new("D：两侧绑定同一 Notion 页面 → 允许命中",
            new GameIdentityFacts("manual", "1", null, null, "Foo", "page-a"),
            new[] { new LocalSpec("manual", "2", null, null, "Foo", "page-a") },
            IdentityDecisionKind.Matched, IdentityMatchLevel.NormalizedName,
            IdentityDecisionReasons.NormalizedNameMatch, 1),

        new("D：名称命中两个候选 → Deferred",
            new GameIdentityFacts("manual", "1", null, null, "Foo"),
            new[]
            {
                new LocalSpec("manual", "2", null, null, "Foo", null, false, 1),
                new LocalSpec("manual", "3", null, null, "foo", null, false, 2)
            },
            IdentityDecisionKind.Deferred, IdentityMatchLevel.NormalizedName,
            IdentityDecisionReasons.MultipleCandidates, null),

        new("D：名称不命中 → Deferred",
            new GameIdentityFacts("manual", "1", null, null, "Foo"),
            new[] { new LocalSpec("manual", "2", null, null, "Bar") },
            IdentityDecisionKind.Deferred, IdentityMatchLevel.NormalizedName,
            IdentityDecisionReasons.NoCandidate, null),

        new("曾经有可比的弱键却没对上 → 不允许退到名称兜底",
            Steam("123456", exe: "foo.exe", path: @"D:\Other\foo.exe", name: "Foo"),
            new[] { new LocalSpec("manual", "", "bar.exe", @"D:\Games\bar\bar.exe", "Foo") },
            IdentityDecisionKind.Deferred, IdentityMatchLevel.NormalizedName,
            IdentityDecisionReasons.NoCandidate, null),

        new("本地一个候选都没有 → NoLocalCandidate",
            Steam("123456"), Array.Empty<LocalSpec>(),
            IdentityDecisionKind.NoLocalCandidate, IdentityMatchLevel.None,
            IdentityDecisionReasons.NoCandidate, null),

        // ── ignored：绝对优先，任何一级命中都阻断 ─────────────────────
        new("ignored 在 A 级命中 → 阻断",
            Steam("123456"), new[] { LocalSteam("123456", ignored: true) },
            IdentityDecisionKind.Blocked, IdentityMatchLevel.None,
            IdentityDecisionReasons.LocalIgnored, null),

        new("ignored 在 B 级命中（A 不适用）→ 阻断",
            Steam("123456"),
            new[] { new LocalSpec("manual", "123", "foo.exe", @"D:\Games\foo\foo.exe", "Foo", null, true) },
            IdentityDecisionKind.Blocked, IdentityMatchLevel.None,
            IdentityDecisionReasons.LocalIgnored, null),

        new("ignored 在 C 级命中 → 阻断",
            Steam("123456", exe: "foo.exe", path: @"D:\Other\foo.exe"),
            new[] { LocalSteam("999999", exe: "foo.exe", path: @"D:\Games\foo\foo.exe", ignored: true) },
            IdentityDecisionKind.Blocked, IdentityMatchLevel.None,
            IdentityDecisionReasons.LocalIgnored, null),

        new("ignored 只在名称上命中 → 依然阻断（不能用 name match 绕过）",
            new GameIdentityFacts("manual", "1", null, null, "Foo"),
            new[] { new LocalSpec("manual", "2", null, null, "Foo", null, true) },
            IdentityDecisionKind.Blocked, IdentityMatchLevel.None,
            IdentityDecisionReasons.LocalIgnored, null),

        new("ignored 与远端毫无共同键 → 不阻断，走正常规则",
            Steam("123456", exe: "foo.exe", path: @"D:\Games\foo\foo.exe", name: "Foo"),
            new[] { LocalSteam("999999", exe: "other.exe", path: @"D:\Other\other.exe", name: "Other", ignored: true) },
            IdentityDecisionKind.Blocked, IdentityMatchLevel.StrongKey,
            IdentityDecisionReasons.StrongKeyConflict, null),

        new("ignored 行不匹配、另一行 A 相等 → 正常命中（ignored 只保护它自己）",
            Steam("123456", exe: "foo.exe", path: @"D:\Games\foo\foo.exe", name: "Foo"),
            new[]
            {
                LocalSteam("999999", exe: "other.exe", path: @"D:\Other\other.exe", name: "Other", ignored: true, id: 2),
                LocalSteam("123456", id: 1)
            },
            IdentityDecisionKind.Matched, IdentityMatchLevel.StrongKey,
            IdentityDecisionReasons.StrongKeyEqual, 1),
    };

    [Theory]
    [MemberData(nameof(DecisionMatrix))]
    public void DecisionMatrix_CoversEveryBranch(RuleCase testCase)
    {
        var locals = testCase.Locals
            .Select(spec => new LocalGameCandidate(
                spec.LocalId,
                new GameIdentityFacts(
                    spec.Platform, spec.PlatformId, spec.Exe, spec.ExePath, spec.Name, spec.NotionPageId),
                spec.IsIgnored))
            .ToList();

        var decision = GameIdentityRules.Decide(testCase.Remote, locals);

        decision.Kind.Should().Be(testCase.ExpectedKind, $"[{testCase.Name}]");
        decision.Level.Should().Be(testCase.ExpectedLevel, $"[{testCase.Name}]");
        decision.Reason.Should().Be(testCase.ExpectedReason, $"[{testCase.Name}]");
        decision.CanonicalLocalId.Should().Be(testCase.ExpectedLocalId, $"[{testCase.Name}]");
        decision.AllowsMerge.Should().Be(
            testCase.ExpectedKind == IdentityDecisionKind.Matched, $"[{testCase.Name}]");
    }

    // ==================== 用户点名的头号场景（单独列出，便于一眼看到） ====================

    [Fact]
    public void StrongKeyConflict_BlocksWeakKeyMerge_EvenWhenEverythingElseIsIdentical()
    {
        // Remote: Steam / 123456   foo.exe   D:\Games\foo\foo.exe   "Foo"
        // Local : Steam / 999999   foo.exe   D:\Games\foo\foo.exe   "Foo"
        //   → A 适用且不等 ⇒ 阻断；**绝不能**因为 B/C/D 全同就自动合并（会污染 global_id）。
        var remote = Steam("123456");
        var locals = new[] { new LocalGameCandidate(1, ToFacts(LocalSteam("999999"))) };

        var decision = GameIdentityRules.Decide(remote, locals);

        decision.Kind.Should().Be(IdentityDecisionKind.Blocked);
        decision.Reason.Should().Be(IdentityDecisionReasons.StrongKeyConflict);
        decision.Level.Should().Be(IdentityMatchLevel.StrongKey);
        decision.CanonicalLocalId.Should().BeNull();
        decision.AllowsMerge.Should().BeFalse();
    }

    [Fact]
    public void NotApplicableStrongKey_IsNotAConflict_AndIsAllowedToFallThroughToWeakKeys()
    {
        // 「A 不适用」≠「A 冲突」：本地 platform_id 是本地行 id（8 位）→ A 不适用 → 允许落 B。
        var remote = Steam("123456");
        var locals = new[]
        {
            new LocalGameCandidate(1,
                new GameIdentityFacts("steam", "abcdef12", "foo.exe", @"D:\Games\foo\foo.exe", "Foo"))
        };

        var decision = GameIdentityRules.Decide(remote, locals);

        decision.Kind.Should().Be(IdentityDecisionKind.Matched);
        decision.Level.Should().Be(IdentityMatchLevel.ExecutablePath);
        decision.CanonicalLocalId.Should().Be(1);
    }

    private static GameIdentityFacts ToFacts(LocalSpec spec)
        => new(spec.Platform, spec.PlatformId, spec.Exe, spec.ExePath, spec.Name, spec.NotionPageId);

    // ==================== 归一化边界 ====================

    [Theory]
    [InlineData(@"D:\Games\Foo\foo.exe", @"d:/games/foo/foo.exe", true)]
    [InlineData(@"D:\Games\foo\foo.exe  ", @"d:\games\foo\foo.exe", true)]
    [InlineData(@"D:\Games\foo\foo.exe", @"E:\Games\foo\foo.exe", false)]
    public void PathNormalization_IgnoresCaseSeparatorsAndTrailingSpace(string a, string b, bool equal)
    {
        var left = new GameIdentityFacts(ExecutablePath: a);
        var right = new GameIdentityFacts(ExecutablePath: b);
        GameIdentityRules.PathEqual(left, right).Should().Be(equal);
    }

    [Theory]
    [InlineData("Foo.exe", "foo.EXE", true)]
    [InlineData(@"D:\Games\Foo\foo.exe", "foo.exe", true)]
    [InlineData("foo.exe", "bar.exe", false)]
    public void ExecutableNameNormalization_TakesFileNameOnly(string a, string b, bool equal)
    {
        GameIdentityRules.ExeEqual(
            new GameIdentityFacts(Executable: a),
            new GameIdentityFacts(Executable: b)).Should().Be(equal);
    }

    [Theory]
    [InlineData("steam", "123456", true)]
    [InlineData("steam", "abcdef12", false)]          // 疑似本地 GUID-ish id
    [InlineData("steam", "abcdef1234567890", false)]  // 同上（16 位）
    [InlineData("manual", "123456", false)]           // manual 的 platform_id 是本地行 id
    [InlineData("steam", "", false)]
    [InlineData("", "123456", false)]
    public void StrongKeyAvailability_MatchesProjectHeuristic(string platform, string platformId, bool expected)
    {
        GameIdentityRules.StrongKeyAvailable(new GameIdentityFacts(platform, platformId)).Should().Be(expected);
    }
}
