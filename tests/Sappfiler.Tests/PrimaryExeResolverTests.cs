using FluentAssertions;
using GameTimeTracker.Infrastructure.Covers;
using GameTimeTracker.Infrastructure.Platforms;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 本体裁决（2026-10-07 事故修复 C1/C2）。
///
/// <para>
/// 真机事故：Steam 游戏 <c>InstalledGame.ExePath</c> 恒为 null ⇒ 只能按安装目录前缀认领进程 ⇒
/// 目录里的 <c>mcclauncher.exe</c> 被当成本体 ⇒ 抠出了 EasyAntiCheat 的小蓝熊图标。
/// </para>
///
/// <para>
/// 最关键的两条（T-A / T-B）：**历史污染 session 不能证明某个 exe 是本体**。
/// </para>
/// </summary>
public class PrimaryExeResolverTests
{
    private const string HaloDir = @"E:\SteamLibrary\steamapps\common\Halo The Master Chief Collection";
    private const string Mcclauncher = HaloDir + @"\mcclauncher.exe";
    private const string Shipping = HaloDir + @"\MCC\Binaries\Win64\MCC-Win64-Shipping.exe";
    private const string Eac = HaloDir + @"\easyanticheat\easyanticheat_eos_setup.exe";
    private const string UnrealCef = HaloDir + @"\Engine\Binaries\Win64\UnrealCEFSubProcess.exe";
    private const string GameName = "Halo: The Master Chief Collection";

    private static GroupVerdict Resolve(
        string[] exes,
        string? knownPrimary = null,
        Dictionary<string, int>? history = null,
        Dictionary<string, int>? ticks = null,
        string gameName = GameName)
        => PrimaryExeResolver.Resolve(
            exes.Select((e, i) => new ProcessExeCandidate(e, Path.GetFileName(e), 1000 + i)).ToList(),
            knownPrimary,
            e => history != null && history.TryGetValue(e, out var n) ? n : 0,
            e => ticks != null && ticks.TryGetValue(e, out var t) ? t : 1,
            gameName);

    // ==================== T-A / T-B：历史污染免疫（本次事故的核心） ====================

    [Fact] // T-A
    public void PollutedHistory_DoesNotBeatRealPrimary()
    {
        var v = Resolve(new[] { Mcclauncher, Shipping },
            history: new() { [Mcclauncher] = 8, [Shipping] = 6 });

        v.PrimaryExe.Should().Be(Shipping, "8 条历史污染 session 不能证明启动器是本体");
        v.Decisions.First(d => d.ExePath == Mcclauncher).IsPrimary.Should().BeFalse();
        v.Decisions.First(d => d.ExePath == Mcclauncher).Tier.Should().Be(ExeTier.Downgraded);
    }

    [Fact] // T-B（更狠：100 条污染 + 本体 0 条历史）
    public void HundredPollutedSessions_StillLoseToPrimaryStyle()
    {
        var v = Resolve(new[] { Mcclauncher, Shipping },
            history: new() { [Mcclauncher] = 100, [Shipping] = 0 });

        v.PrimaryExe.Should().Be(Shipping,
            "本体凭 T2（*Shipping.exe + \\Binaries\\Win64）胜出，与 session 数量无关");
    }

    // ==================== 硬否决 / 降级 ====================

    [Fact]
    public void AntiCheat_IsHardDenied_EvenWithHugeHistory()
    {
        var v = Resolve(new[] { Eac, UnrealCef, Shipping },
            history: new() { [Eac] = 999, [UnrealCef] = 999 });

        v.Decisions.First(d => d.ExePath == Eac).Tier.Should().Be(ExeTier.Denied);
        v.Decisions.First(d => d.ExePath == UnrealCef).Tier.Should().Be(ExeTier.Denied);
        v.PrimaryExe.Should().Be(Shipping);
    }

    [Fact]
    public void AntiCheat_Alone_NeverBecomesPrimary()
    {
        var v = Resolve(new[] { Eac }, ticks: new() { [Eac] = 50 });
        v.PrimaryExe.Should().BeNull("T5 永不晋升（新运行证据也不行）");
    }

    // ==================== Launcher 反例：不误伤"本体就叫 Launcher"的游戏 ====================

    [Fact]
    public void LauncherAlone_WithNewRunEvidence_MayBecomePrimary()
    {
        var solo = @"E:\Games\SomeGame\SomeGameLauncher.exe";
        var notYet = Resolve(new[] { solo }, ticks: new() { [solo] = PrimaryExeResolver.RequiredConsecutiveTicks - 1 });
        notYet.PrimaryExe.Should().BeNull("还没攒够连续 tick 的新运行证据");

        var yes = Resolve(new[] { solo }, ticks: new() { [solo] = PrimaryExeResolver.RequiredConsecutiveTicks });
        yes.PrimaryExe.Should().Be(solo, "无更可信候选 + 连续 3 tick（≈15s）新运行证据 ⇒ 可晋升");
    }

    [Fact]
    public void LauncherLosesWhenRealPrimaryExists()
    {
        var solo = @"E:\Games\SomeGame\SomeGameLauncher.exe";
        var body = @"E:\Games\SomeGame\SomeGame-Shipping.exe";
        var v = Resolve(new[] { solo, body }, ticks: new() { [solo] = 99 });
        v.PrimaryExe.Should().Be(body);
    }

    // ==================== 常规档位 ====================

    [Fact]
    public void KnownPrimary_Wins()
    {
        var g1 = @"E:\Games\A\a.exe";
        var g2 = @"E:\Games\A\b-Shipping.exe";
        var v = Resolve(new[] { g1, g2 }, knownPrimary: g1);
        v.PrimaryExe.Should().Be(g1, "T0 已持久化的本体优先");
    }

    [Fact]
    public void UnknownExe_IsDeferredWithoutTickEvidence()
    {
        var unknown = @"E:\Games\A\mystery.exe";
        var v = Resolve(new[] { unknown }, ticks: new() { [unknown] = 1 });
        v.PrimaryExe.Should().BeNull("T3 未知：仅观察，无写权");
        v.Decisions.Single().Tier.Should().Be(ExeTier.Unknown);
    }

    [Fact]
    public void ResultIsOrderIndependent()
    {
        var a = Resolve(new[] { Mcclauncher, Shipping });
        var b = Resolve(new[] { Shipping, Mcclauncher });
        a.PrimaryExe.Should().Be(b.PrimaryExe).And.Be(Shipping, "同 tick 批裁决必须与候选顺序无关");
    }

    // ==================== C4 安全气囊（CoverCacheService v4） ====================

    [Fact]
    public void CoverCache_Airbag_RejectsDeniedAndUntrustedLauncher()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sappfiler_airbag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new CoverCacheService(dir);
            var exeDir = Path.Combine(Path.GetTempPath(), "sappfiler_airbag_exe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(exeDir);
            try
            {
                // T5：反作弊（路径含 \easyanticheat）
                var eac = Path.Combine(exeDir, "easyanticheat", "EasyAntiCheat.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(eac)!);
                File.WriteAllBytes(eac, new byte[] { 1 });
                svc.ExtractAndSaveExecutableIcon(eac, "steam", "976730").Should().BeFalse("T5 永不抠图");

                // T4：启动器角色 + 未受信调用者
                var launcher = Path.Combine(exeDir, "some_launcher.exe");
                File.WriteAllBytes(launcher, new byte[] { 1 });
                svc.ExtractAndSaveExecutableIcon(launcher, "steam", "976730").Should().BeFalse(
                    "未受信调用者不得抠启动器图标");

                Directory.GetFiles(dir).Should().BeEmpty("被拒时不得留下任何图标文件");
            }
            finally { Directory.Delete(exeDir, recursive: true); }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
