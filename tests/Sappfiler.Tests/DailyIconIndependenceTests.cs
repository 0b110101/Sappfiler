using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameTimeTracker.Infrastructure.Notion;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// Master / Daily 双链路独立性（硬约束 ⑥⑦⑧）。
/// 前半段是**线上请求体**断言（真发出去什么），后半段是**源码守卫**（防止旧链路被写回来）。
/// </summary>
public class DailyIconIndependenceTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Content != null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"page-xyz\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private static (NotionClient Client, CapturingHandler Handler) MakeClient()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        return (new NotionClient("test-token", http), handler);
    }

    // ⑥ Daily 新建只使用 file_upload，绝不出现 external / 总表图标 URL
    [Fact]
    public async Task CreateDailyRecord_WithFileUploadId_UsesFileUploadIconOnly()
    {
        var (client, handler) = MakeClient();

        await client.CreateDailyRecordAsync(
            "11111111-1111-1111-1111-111111111111", "2026-10-06", "测试游戏", 90,
            gamePageId: null, iconUrl: null, iconFileUploadId: "file-upload-abc");

        var body = handler.Bodies.Single();
        Assert.Contains("\"file_upload\"", body);
        Assert.Contains("file-upload-abc", body);
        Assert.DoesNotContain("external", body);
        Assert.DoesNotContain("cdn.cloudflare", body);
        Assert.DoesNotContain("http", body);   // 图标字段里没有任何 URL
    }

    // ⑥ 更新路径同理
    [Fact]
    public async Task UpdateDailyRecord_WithFileUploadId_UsesFileUploadIcon()
    {
        var (client, handler) = MakeClient();

        await client.UpdateDailyRecordAsync(
            "22222222-2222-2222-2222-222222222222", 12, null,
            iconUrl: null, writeDuration: false, iconFileUploadId: "file-upload-def");

        var body = handler.Bodies.Single();
        Assert.Contains("\"file_upload\"", body);
        Assert.Contains("file-upload-def", body);
        Assert.DoesNotContain("external", body);
    }

    // ⑧ Master 侧行为不变：SetPageIconAsync 仍然是 external（Steam CDN 那套逻辑未被动过）
    [Fact]
    public async Task SetPageIcon_StillUsesExternalUrl_Unchanged()
    {
        var (client, handler) = MakeClient();

        await client.SetPageIconAsync("33333333-3333-3333-3333-333333333333",
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/header.jpg");

        var body = handler.Bodies.Single();
        Assert.Contains("\"external\"", body);
        Assert.DoesNotContain("file_upload", body);
    }

    // ⑦ Daily 的图标解析实现里不得出现总表图标（源码守卫）
    [Fact]
    public void DailyIconResolver_SourceNeverTouchesGameCatalogIconUrl()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Sappfiler.Infrastructure", "Notion", "NotionServices.cs"));

        var start = src.IndexOf("private async Task<string> ResolveDailyDisplayAsync", StringComparison.Ordinal);
        Assert.True(start > 0, "未找到 ResolveDailyDisplayAsync");

        var end = src.IndexOf("public async Task RefreshGameCatalogCacheAsync", StringComparison.Ordinal);
        Assert.True(end > start, "区域定位失败");

        var region = src[start..end];

        // ① 旧链路已断：Daily 解析区不再读 game_catalog 的 IconUrl
        Assert.DoesNotContain("cat.IconUrl", region);
        // ② 新链路存在：图标来自本地封面
        Assert.Contains("GetCoverPath", region);
        Assert.Contains("ResolveDailyIconUploadIdAsync", region);
    }

    // ⑦ 守卫：不允许再出现"从 ResolveDailyDisplayAsync 里取图标"的写法
    [Fact]
    public void NoDailyCallSite_DestructuresIconFromDisplayResolver()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Sappfiler.Infrastructure", "Notion", "NotionServices.cs"));
        Assert.DoesNotContain("var (displayName, iconUrl)", src);
    }

    // ⑧ 守卫：Master 的"已有图标就不覆盖"规则必须原样保留
    [Fact]
    public void MasterIconRule_StillSkipsPagesThatAlreadyHaveAnIcon()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Sappfiler.Infrastructure", "Notion", "NotionServices.cs"));
        Assert.Contains("if (!string.IsNullOrEmpty(cat.IconType)) continue;", src);
        Assert.Contains("private const string NotionVersion = \"2022-06-28\";", src);
    }

    private static string RepoRoot([CallerFilePath] string callerFilePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFilePath)!, "..", ".."));
}
