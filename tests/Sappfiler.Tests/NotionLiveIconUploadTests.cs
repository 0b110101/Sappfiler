using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTimeTracker.Core.Notion;
using GameTimeTracker.Infrastructure.Notion;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// **真实 Notion 验证**（默认不联网，只有设置环境变量 <c>SAPF_LIVE_NOTION=1</c> 时才执行）。
/// </summary>
/// <remarks>
/// 安全策略：
/// <list type="bullet">
/// <item>Token 只从本机 Sappfiler 的本地数据库读取，**绝不**通过命令行/环境变量传入；</item>
/// <item>Token 只在内存里用于 HTTP 头，不写日志、不打印、不落盘；</item>
/// <item>只写入**专门准备的测试库**，不触碰正式 Daily 数据、不触碰 Master Page、不删除任何既有页面。</item>
/// </list>
/// </remarks>
public class NotionLiveIconUploadTests
{
    /// <summary>用户指定的测试库（「每日游戏时长 (测试)」）。</summary>
    private const string TestDailyDatabaseId = "3f1c4ab2637880b6ab07e3b859c64cec";

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("SAPF_LIVE_NOTION") == "1";

    /// <summary>从本机 DB 的 settings.notion_token 取 Token（实测有效长度是前 50 字符）。</summary>
    private static string? ReadLocalToken()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sappfiler");
        foreach (var file in new[] { "gametime.db-wal", "gametime.db" })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) continue;
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(path));
            foreach (Match m in Regex.Matches(text, "(ntn_[A-Za-z0-9_\\-\\.]{15,}|secret_[A-Za-z0-9_\\-\\.]{15,})"))
            {
                var v = m.Value;
                return v.Length > 50 ? v[..50] : v;
            }
        }
        return null;
    }

    /// <summary>文件版映射存储：用于验证"同一哈希复用同一个 file_upload_id"。</summary>
    private sealed class FileStore : INotionIconMappingStore
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "sapf-live-mapping-" + Guid.NewGuid().ToString("N") + ".json");
        private Dictionary<string, string> Load() => File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new()
            : new();

        public Task<string?> GetSettingAsync(string key)
        {
            var d = Load();
            return Task.FromResult(d.TryGetValue(key, out var v) ? v : null);
        }

        public Task SetSettingAsync(string key, string value)
        {
            var d = Load();
            d[key] = value;
            File.WriteAllText(_path, JsonSerializer.Serialize(d));
            return Task.CompletedTask;
        }

        public Task DeleteSettingAsync(string key)
        {
            var d = Load();
            d.Remove(key);
            File.WriteAllText(_path, JsonSerializer.Serialize(d));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Live_FileUpload_AttachesIcon_ReusesSameHash()
    {
        if (!LiveEnabled) return; // 默认静默跳过（不联网）

        var token = ReadLocalToken();
        Assert.False(string.IsNullOrWhiteSpace(token), "本机未找到 Notion Token（settings.notion_token）");

        // 取一张真实本地封面（缓存里已有的即可）
        var coversDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sappfiler", "cache", "covers");
        var cover = Directory.Exists(coversDir)
            ? Directory.EnumerateFiles(coversDir, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
            : null;
        Assert.False(cover == null, "本地封面缓存为空，无法验证");

        var client = new NotionClient(token!);
        var store = new FileStore();
        var svc = new DailyIconUploadService(client, store);

        // 第 1 次：上传
        var id1 = await svc.EnsureUploadedAsync(cover);
        Assert.False(string.IsNullOrWhiteSpace(id1), "上传失败（详见 data\\logs\\app.log）");

        // 第 2 次：同哈希 ⇒ 必须复用同一个 id（不产生第 2 个 File Upload）
        var id2 = await svc.EnsureUploadedAsync(cover);
        Assert.Equal(id1, id2);

        // 用产品的 Daily 新建路径，把该 file_upload 作为 icon 写进**测试库**的一个新页面
        var title = "SAPF-ICON-LIVE-TEST " + DateTime.Now.ToString("HH:mm:ss");
        var pageId = await client.CreateDailyRecordAsync(
            TestDailyDatabaseId, DateTime.Today.ToString("yyyy-MM-dd"), title, 1, null,
            iconUrl: null, iconFileUploadId: id1);
        Assert.False(string.IsNullOrWhiteSpace(pageId));

        // 读回页面：icon 必须是 file（Notion 读回形状，不含 file_upload id）
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Notion-Version", "2022-06-28");

        var pageJson = JsonDocument.Parse(await http.GetStringAsync($"https://api.notion.com/v1/pages/{pageId}")).RootElement;
        var iconType = pageJson.TryGetProperty("icon", out var icon) ? icon.GetProperty("type").GetString() : null;
        Assert.Equal("file", iconType);

        // File Upload 读回：附着后 expiry_time 必须为 null（= 不再过期）
        var fuJson = JsonDocument.Parse(await http.GetStringAsync($"https://api.notion.com/v1/file_uploads/{id1}")).RootElement;
        var expiryIsNull = fuJson.GetProperty("expiry_time").ValueKind == JsonValueKind.Null;
        Console.WriteLine($"[LIVE] pageId={pageId} icon.type={iconType} file_upload.status={fuJson.GetProperty("status").GetString()} expiry_time={(expiryIsNull ? "null(不再过期)" : "非 null")}");
        Assert.True(expiryIsNull, "附着后 expiry_time 应为 null");
    }
}
