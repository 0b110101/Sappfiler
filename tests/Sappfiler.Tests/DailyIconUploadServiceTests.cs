using GameTimeTracker.Core.Notion;
using GameTimeTracker.Infrastructure.Notion;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// CoverHash → file_upload_id 的复用/重传/失败降级（硬约束 ②③④⑤⑨）。
/// </summary>
public class DailyIconUploadServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sapf-icon-" + Guid.NewGuid().ToString("N"));

    public DailyIconUploadServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>极简内存版映射存储（真实实现是 settings 表）。</summary>
    private sealed class MemoryStore : INotionIconMappingStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Task<string?> GetSettingAsync(string key) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
        public Task SetSettingAsync(string key, string value) { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteSettingAsync(string key) { Values.Remove(key); return Task.CompletedTask; }
    }

    private static string PngPath(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 });
        return p;
    }

    // ③ 新 CoverHash → create → send → 保存 mapping
    [Fact]
    public async Task NewHash_CreatesSendsAndPersistsMapping()
    {
        var client = new FakeNotionClient();
        var store = new MemoryStore();
        var svc = new DailyIconUploadService(client, store);
        var cover = PngPath(_dir, "steam_1.jpg");

        var id = await svc.EnsureUploadedAsync(cover);

        Assert.NotNull(id);
        Assert.Single(client.FileUploadCreates);
        Assert.Single(client.FileUploadSends);
        Assert.Equal("image/png", client.FileUploadCreates[0].ContentType); // 内容判类型，不看 .jpg
        Assert.Equal(cover, client.FileUploadSends[0].Path);

        var mapping = NotionIconMapping.Parse(store.Values[NotionIconUploadKeys.Key(CoverFileProbeHashOf(cover))]);
        Assert.NotNull(mapping);
        Assert.Equal(id, mapping!.FileUploadId);
        Assert.Equal("image/png", mapping.ContentType);
        Assert.Equal(1, mapping.AttachedCount);
    }

    // ② 同 CoverHash ⇒ 复用，绝不重复创建 File Upload
    [Fact]
    public async Task SameHash_ReusesWithoutSecondUpload()
    {
        var client = new FakeNotionClient();
        var store = new MemoryStore();
        var svc = new DailyIconUploadService(client, store);
        var cover = PngPath(_dir, "steam_2.png");

        var first = await svc.EnsureUploadedAsync(cover);
        var second = await svc.EnsureUploadedAsync(cover);

        Assert.Equal(first, second);
        Assert.Single(client.FileUploadCreates);   // 只有 1 次 create
        Assert.Single(client.FileUploadSends);
    }

    // 封面换图（内容变）⇒ 新哈希 ⇒ 必须重新上传
    [Fact]
    public async Task ChangedContent_UploadsAgain()
    {
        var client = new FakeNotionClient();
        var store = new MemoryStore();
        var svc = new DailyIconUploadService(client, store);
        var cover = PngPath(_dir, "steam_3.png");

        var first = await svc.EnsureUploadedAsync(cover);
        File.WriteAllBytes(cover, new byte[] { 0xFF, 0xD8, 0xFF, 0x11, 0x22, 0x33 });
        var second = await svc.EnsureUploadedAsync(cover);

        Assert.NotEqual(first, second);
        Assert.Equal(2, client.FileUploadCreates.Count);
    }

    // ④ mapping 里的 id 已失效（Notion 查询不到）⇒ 最多自动重传一次
    [Fact]
    public async Task InvalidMappingId_ReuploadsAtMostOnce()
    {
        var client = new FakeNotionClient();
        var store = new MemoryStore();
        var svc = new DailyIconUploadService(client, store);
        var cover = PngPath(_dir, "steam_4.png");

        var hash = CoverFileProbeHashOf(cover);
        store.Values[NotionIconUploadKeys.Key(hash)] = new NotionIconMapping
        {
            FileUploadId = "dead-id",          // 不在 client.KnownFileUploadIds 里 ⇒ 查询返回 null
            ContentType = "image/png",
            Size = new FileInfo(cover).Length,
            BytesHash = hash,
            UploadedAtUtc = DateTimeOffset.UtcNow,
            LastUsedAtUtc = DateTimeOffset.UtcNow,
            AttachedCount = 1
        }.ToJson();

        var id = await svc.EnsureUploadedAsync(cover);

        Assert.NotNull(id);
        Assert.NotEqual("dead-id", id);
        Assert.Single(client.FileUploadCreates);          // 重传**恰好一次**
        var saved = NotionIconMapping.Parse(store.Values[NotionIconUploadKeys.Key(hash)]);
        Assert.Equal(id, saved!.FileUploadId);
    }

    // ⑤ create 失败 / send 失败 ⇒ 返回 null 且不抛（绝不阻塞游戏时长记录），且不写映射
    [Fact]
    public async Task CreateFailure_ReturnsNullAndDoesNotThrow()
    {
        var client = new FakeNotionClient { FailFileUploadCreate = true };
        var store = new MemoryStore();
        var svc = new DailyIconUploadService(client, store);
        var cover = PngPath(_dir, "steam_5.png");

        var id = await svc.EnsureUploadedAsync(cover);

        Assert.Null(id);
        Assert.Empty(store.Values);
    }

    [Fact]
    public async Task SendFailure_ReturnsNullAndWritesNoMapping()
    {
        var client = new FakeNotionClient { FailFileUploadSend = true };
        var store = new MemoryStore();
        var svc = new DailyIconUploadService(client, store);
        var cover = PngPath(_dir, "steam_6.png");

        var id = await svc.EnsureUploadedAsync(cover);

        Assert.Null(id);
        Assert.Single(client.FileUploadCreates);   // 创建过
        Assert.Single(client.FileUploadSends);     // 但发送失败
        Assert.Empty(store.Values);                // 关键：不落映射，下次重来
    }

    [Fact]
    public async Task MissingCover_ReturnsNull_NoApiCalls()
    {
        var client = new FakeNotionClient();
        var svc = new DailyIconUploadService(client, new MemoryStore());

        Assert.Null(await svc.EnsureUploadedAsync(Path.Combine(_dir, "absent.png")));
        Assert.Null(await svc.EnsureUploadedAsync(null));
        Assert.Empty(client.FileUploadCreates);
    }

    // ⑨ 映射里只有持久 id，绝不是 Notion 读回的临时 icon.file.url
    [Fact]
    public async Task Mapping_HoldsOnlyPersistentId_NoTemporaryUrl()
    {
        var client = new FakeNotionClient();
        var store = new MemoryStore();
        var svc = new DailyIconUploadService(client, store);
        var cover = PngPath(_dir, "steam_7.png");

        var id = await svc.EnsureUploadedAsync(cover);
        await svc.EnsureUploadedAsync(cover);   // 复用一轮

        var json = store.Values[NotionIconUploadKeys.Key(CoverFileProbeHashOf(cover))];
        Assert.DoesNotContain("https://", json);          // 不含任何 URL
        Assert.DoesNotContain("\"url\"", json);
        Assert.Contains(id!, json);
        Assert.Equal(2, NotionIconMapping.Parse(json)!.AttachedCount); // 复用次数被累计
    }

    private static string CoverFileProbeHashOf(string path) => GameTimeTracker.Core.Covers.CoverFileProbe.Sha256Hex(path);
}
