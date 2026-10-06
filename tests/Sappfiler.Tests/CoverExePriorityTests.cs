using FluentAssertions;
using GameTimeTracker.Infrastructure.Covers;
using GameTimeTracker.Infrastructure.Process;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 封面 / exe 挑选的回归网（2026-10-05 真机缺陷）。
///
/// <para>
/// 现象：玩 Halo: The Master Chief Collection 时，游戏图标显示成了 **Easy Anti-Cheat 的 logo**。
/// 根因有两层：
/// </para>
/// <list type="number">
///   <item>反作弊 / 启动器 / 辅助进程的 exe 会被"安装目录前缀匹配"误认成游戏本体
///         （它们常常就住在游戏目录里，或与游戏同时运行）；</item>
///   <item>更致命的是**封面优先级**：exe 抠出来的 <c>.png</c> 压过了总表/Steam 的正规封面 <c>.jpg</c>，
///         而且"已有封面就不再取正经封面"——错误图标会永久粘住。</item>
/// </list>
/// </summary>
public class CoverExePriorityTests
{
    // ==================== ① 生态组件判定：反作弊 / 启动器 / 辅助进程 ====================

    [Theory]
    // 反作弊（含 EOS 版与"启动受保护游戏"外壳）
    [InlineData(@"E:\SteamLibrary\steamapps\common\Halo The Master Chief Collection\EasyAntiCheat\EasyAntiCheat.exe")]
    [InlineData(@"C:\Program Files (x86)\EasyAntiCheat_EOS\EasyAntiCheat_EOS.exe")]
    [InlineData(@"C:\Program Files (x86)\EasyAntiCheat\EasyAntiCheat.exe")]
    [InlineData(@"E:\Games\SomeGame\BattlEye\BEService.exe")]
    [InlineData(@"E:\Games\SomeGame\start_protected_game.exe")]
    // 平台启动器
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\upc.exe")]
    [InlineData(@"C:\Program Files (x86)\Origin\Origin.exe")]
    [InlineData(@"C:\Program Files\EA Games\EA Desktop\EADesktop.exe")]
    // 辅助 / 崩溃上报 / 工具型命名
    [InlineData(@"E:\Games\SomeGame\CrashReportClient.exe")]
    [InlineData(@"E:\Games\SomeGame\UnityCrashHandler64.exe")]
    public void EcosystemComponents_AreNotGameExes(string exePath)
        => ProcessFilter.IsEcosystemComponent(exePath).Should().BeTrue(
            $"{System.IO.Path.GetFileName(exePath)} 是游戏生态组件，绝不能当成游戏本体（否则会把它的 logo 抠成游戏图标）");

    [Theory]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Halo The Master Chief Collection\MCC\Binaries\Win64\MCC-Win64-Shipping.exe")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Halo Infinite\HaloInfinite.exe")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe")]
    [InlineData(@"E:\Games\Deadloop\DEATHLOOP.exe")]
    public void RealGameExecutables_AreNotFlagged(string exePath)
        => ProcessFilter.IsEcosystemComponent(exePath).Should().BeFalse(
            $"{System.IO.Path.GetFileName(exePath)} 是游戏本体，必须照常识别");

    // ==================== ② 优先级：本地已发现图标 > 本地正规封面 > 历史/远端封面 ====================
    // 2026-10-07 产品取向变更（v3 "本地优先"）：本段断言已按新语义更新。
    // 旧语义（v2）是"正规封面永远赢"，导致本地已成功提取的 exe 图标永远显示不出来（Halo 实测）。

    [Fact]
    public void LocalDiscoveredIcon_WinsOverRegularCover()
    {
        var dir = NewTempDir();
        try
        {
            var service = new CoverCacheService(dir);

            // ② 本地正规封面（.jpg）
            var realCover = Path.Combine(dir, "steam_976730.jpg");
            File.WriteAllBytes(realCover, new byte[] { 1, 2, 3, 4 });

            // ① 本地已发现的游戏图标（exe / 安装目录 logo 抠出来的 .exeicon.png）
            var localIcon = Path.Combine(dir, "steam_976730.exeicon.png");
            File.WriteAllBytes(localIcon, new byte[] { 9, 9, 9 });

            service.GetCoverPath("steam", "976730").Should().Be(localIcon,
                "2026-10-07 起：本地已发现的游戏图标优先于本地正规封面（此前是反过来的）");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LocalIcon_IsUsedWhenNoOtherCover()
    {
        var dir = NewTempDir();
        try
        {
            var service = new CoverCacheService(dir);
            var localIcon = Path.Combine(dir, "steam_976730.exeicon.png");
            File.WriteAllBytes(localIcon, new byte[] { 9, 9, 9 });

            service.GetCoverPath("steam", "976730").Should().Be(localIcon,
                "没有其它封面时，本地已发现的图标仍要能用（不能让 UI 变空白）");

            service.HasCover("steam", "976730").Should().BeTrue(
                "2026-10-07 起：本地已发现的图标**算作**「已有可显示封面」—— " +
                "编排器因此不再去取远端封面（旧语义相反，正是 Halo 的成因）");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RealCover_IsReportedAsCover()
    {
        var dir = NewTempDir();
        try
        {
            var service = new CoverCacheService(dir);
            File.WriteAllBytes(Path.Combine(dir, "steam_976730.jpg"), new byte[] { 1, 2, 3, 4 });

            service.HasCover("steam", "976730").Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ==================== ③ Cache Version：算法升级后旧缓存自动失效 ====================

    [Fact]
    public void DefaultCacheDirectory_IsVersioned()
    {
        (CoverCacheService.CacheVersion >= 2).Should().BeTrue(
            "v2 起 exe 图标降级为占位图 → 必须换版本号，让此前粘住的错误封面自动失效");

        var service = new CoverCacheService();     // 不传目录 = 生产路径

        service.GetCoverPath("steam", "976730").Should().MatchRegex(@"\\covers\\v\d+\\",
            "默认缓存目录必须带版本号（covers\\v2\\…），否则修一次封面逻辑就要用户手工删缓存");
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sappfiler_cover_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
