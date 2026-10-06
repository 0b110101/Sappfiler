using FluentAssertions;
using GameTimeTracker.Infrastructure.Covers;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 封面**来源槽位与优先级**的回归网（2026-10-07，v3 "本地优先"）。
///
/// <para>
/// 语义（产品取向）：
/// ① 本地已发现的游戏图标 <c>.exeicon.png</c>
/// → ② 本地正规封面 <c>.png/.jpg</c>
/// → ③ 历史/远端封面 <c>.history.jpg</c>（Notion 总表历史 icon/cover、Steam CDN）
/// → ④ 默认占位路径。
/// </para>
///
/// <para>
/// 修复的真机现象：Halo MCC 本地 EXE 图标已成功提取，却永远被"远端种子"的封面压住。
/// 根因是**远端来源被写进了本地正规封面槽** <c>.jpg</c>。
/// </para>
/// </summary>
public class CoverPriorityTests
{
    // ==================== ① 显示优先级 ====================

    [Fact]
    public void LocalIcon_WinsOverHistoryCover()   // 项 1
    {
        WithTempCache((service, dir) =>
        {
            var localIcon = Touch(dir, "steam_976730.exeicon.png", 9);
            Touch(dir, "steam_976730.history.jpg", 1);

            service.GetCoverPath("steam", "976730").Should().Be(localIcon);
        });
    }

    [Fact]
    public void LocalRegularCover_WinsOverHistoryCover()   // 项 2
    {
        WithTempCache((service, dir) =>
        {
            var localCover = Touch(dir, "steam_976730.jpg", 1);
            Touch(dir, "steam_976730.history.jpg", 2);

            service.GetCoverPath("steam", "976730").Should().Be(localCover);
        });
    }

    [Fact]
    public void LocalIcon_WinsOverBothOtherTiers()   // 项 3
    {
        WithTempCache((service, dir) =>
        {
            var localIcon = Touch(dir, "steam_976730.exeicon.png", 9);
            Touch(dir, "steam_976730.png", 1);
            Touch(dir, "steam_976730.history.jpg", 2);

            service.GetCoverPath("steam", "976730").Should().Be(localIcon);
        });
    }

    [Fact]
    public void HistoryCover_IsUsedWhenNothingLocal()   // 项 4（"从未在本机启动过"的兜底场景）
    {
        WithTempCache((service, dir) =>
        {
            var history = Touch(dir, "steam_976730.history.jpg", 3);

            service.GetCoverPath("steam", "976730").Should().Be(history);
        });
    }

    [Fact]
    public void NothingCached_ReturnsDefaultPath()   // 项 5（UI 走无封面分支）
    {
        WithTempCache((service, dir) =>
        {
            var path = service.GetCoverPath("steam", "976730");

            Path.GetFileName(path).Should().Be("steam_976730.png");
            File.Exists(path).Should().BeFalse();
        });
    }

    // ==================== ② 生态组件绝不能被"本地优先"放回来 ====================

    [Fact]
    public void EcosystemComponent_IsRejectedAndLeavesNoFile()   // 项 6
    {
        WithTempCache((service, dir) =>
        {
            // 造一个"反作弊组件"的真实文件（名字/目录命中 ProcessFilter）
            var exeDir = Path.Combine(Path.GetTempPath(), "sappfiler_eac_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(exeDir);
            try
            {
                var exe = Path.Combine(exeDir, "EasyAntiCheat.exe");
                File.WriteAllBytes(exe, new byte[] { 1, 2, 3 });

                service.ExtractAndSaveExecutableIcon(exe, "steam", "976730").Should().BeFalse(
                    "反作弊组件的 exe 永远不能作为图标来源");
                Directory.GetFiles(dir).Should().BeEmpty(
                    "被拒绝时**不能留下任何文件**（否则它会在下一次被判为『本地已发现的图标』）");
            }
            finally
            {
                Directory.Delete(exeDir, recursive: true);
            }
        });
    }

    // ==================== ③ 槽位：exe 图标不得落进"正规封面"槽 ====================

    [Fact]
    public void ExecutableIcon_NeverLandsInRegularCoverSlot_EvenWithPrefixedPlatformId()   // 项 7（原 :422-425 缺陷）
    {
        WithTempCache((service, dir) =>
        {
            var installDir = Path.Combine(Path.GetTempPath(), "sappfiler_install_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(installDir);
            try
            {
                // 安装目录里放一张 logo（>1500B 才会被采用）与一个游戏 exe
                File.WriteAllBytes(Path.Combine(installDir, "Logo.png"), new byte[2000]);
                var exe = Path.Combine(installDir, "SomeGame.exe");
                File.WriteAllBytes(exe, new byte[] { 1, 2, 3 });

                // platformId 自带平台前缀（Xbox 那种形态）—— 旧实现会顺手写成 {plat}_{去掉前缀}.png
                service.ExtractAndSaveExecutableIcon(exe, "xbox", "xbox_abc").Should().BeTrue();

                var names = Directory.GetFiles(dir).Select(Path.GetFileName).ToList();
                names.Should().Contain("xbox_xbox_abc.exeicon.png");
                names.Should().NotContain("xbox_abc.png",
                    "exe/目录 logo 得到的图标只能进 .exeicon.png 占位槽，不得落进正规封面槽");
                names.Should().NotContain(n => n!.EndsWith(".jpg"));
            }
            finally
            {
                Directory.Delete(installDir, recursive: true);
            }
        });
    }

    // ==================== ④ 编排器语义：本地有东西就不去取远端 ====================

    [Fact]
    public void OrchestratorGuard_LocalIconCountsAsCover()   // 项 8
    {
        WithTempCache((service, dir) =>
        {
            Touch(dir, "steam_976730.exeicon.png", 9);

            service.HasCover("steam", "976730").Should().BeTrue(
                "EnsureLibraryCoversAsync 用 HasCover 作守卫；本地已发现图标必须让它直接跳过（不再取远端）");
            service.HasAnyDisplayableCover("steam", "976730").Should().BeTrue();
        });
    }

    [Fact]
    public async Task RemoteCover_IsWrittenIntoHistorySlotOnly()   // 项 9 + 项 10（Steam CDN 也属历史档）
    {
        var handler = new StubImageHandler();
        using var http = new HttpClient(handler);

        await WithTempCacheAsync((service, dir) =>
        {
            // 本地什么都没有 → 允许兜底；steam + 纯数字 AppID 走 CDN 分支
            var path = service.EnsureCoverAsync("steam", "976730").GetAwaiter().GetResult();

            handler.Requests.Should().Be(1, "本地为空时才允许去取远端");
            Path.GetFileName(path!).Should().Be("steam_976730.history.jpg",
                "远端（Steam CDN / Notion）只能写历史槽，绝不能冒充本地正规封面 .jpg");
            File.Exists(Path.Combine(dir, "steam_976730.jpg")).Should().BeFalse();
            return Task.CompletedTask;
        }, http);
    }

    [Fact]
    public async Task RemoteCover_IsNotFetchedAgainWhenLocalIconExists()   // 项 8（编排器行为）
    {
        var handler = new StubImageHandler();
        using var http = new HttpClient(handler);

        await WithTempCacheAsync((service, dir) =>
        {
            Touch(dir, "steam_976730.exeicon.png", 9);

            var path = service.EnsureCoverAsync("steam", "976730").GetAwaiter().GetResult();

            handler.Requests.Should().Be(0, "本地已发现图标 ⇒ 不再发起任何远端请求");
            Path.GetFileName(path!).Should().Be("steam_976730.exeicon.png");
            return Task.CompletedTask;
        }, http);
    }

    // ==================== 辅助 ====================

    private sealed class StubImageHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0x01, 0x02, 0x03 });
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    private static string Touch(string cacheDir, string fileName, byte value)
    {
        var path = Path.Combine(cacheDir, fileName);
        File.WriteAllBytes(path, new byte[] { value, value, value });
        return path;
    }

    private static void WithTempCache(Action<CoverCacheService, string> body)
    {
        var dir = NewTempDir();
        try { body(new CoverCacheService(dir), dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static async Task WithTempCacheAsync(Func<CoverCacheService, string, Task> body, HttpClient http)
    {
        var dir = NewTempDir();
        try { await body(new CoverCacheService(dir, http), dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sappfiler_priority_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
