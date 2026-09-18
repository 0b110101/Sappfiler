using FluentAssertions;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Covers;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.Notion;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameTimeTracker.Tests;

/// <summary>
/// 库级封面补全的优先级：总表页面 icon（正方形小图）→ cover（横幅，做小封面会裁得难看）→ Steam CDN。
/// 用户明确要求：cover 尺寸不适合做封面，只用它做 Hero 背景图。
/// </summary>
public class CoverIconPriorityTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repo;
    private readonly string _cacheDir;

    public CoverIconPriorityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gticon_{Guid.NewGuid():N}.db");
        _repo = new SqliteRepository(_dbPath);
        _cacheDir = Path.Combine(Path.GetTempPath(), $"gticoncache_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_cacheDir, true); } catch { }
    }

    private static HttpMessageHandler StubHandler(byte[] payload) => new StubHandlerImpl(payload);

    private sealed class StubHandlerImpl(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
    }

    [Fact]
    public async Task EnsureLibraryCovers_PrefersIconUrl_OverCoverUrl()
    {
        string? requestedUrl = null;
        var handler = new ProbeHandler(b => requestedUrl = b);
        var cover = new CoverCacheService(_cacheDir, new HttpClient(handler));

        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "2001", "图标优先游戏", "i.exe", @"C:\i.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-icon");
        await _repo.UpsertCatalogItemsAsync(new[]
        {
            new NotionGameCatalogItem
            {
                PageId = "master-icon",
                Name = "图标优先游戏",
                IconUrl = "https://example.com/icon.png",
                CoverUrl = "https://example.com/cover.jpg"
            }
        });

        await cover.EnsureLibraryCoversAsync(_repo);

        requestedUrl.Should().Be("https://example.com/icon.png",
            "icon 是正方形小图，做封面比横幅 cover 合适；cover 留给 Hero 背景");
        cover.HasCover("steam", "2001").Should().BeTrue();
    }

    [Fact]
    public async Task EnsureLibraryCovers_FallsBackToCoverUrl_WhenIconMissing()
    {
        string? requestedUrl = null;
        var handler = new ProbeHandler(b => requestedUrl = b);
        var cover = new CoverCacheService(_cacheDir, new HttpClient(handler));

        var game = await _repo.GetOrCreateGameAsync(
            new GameIdentity("steam", "2002", "只有cover的游戏", "c.exe", @"C:\c.exe"));
        await _repo.UpdateGameNotionIdAsync(game.Id, "master-cover2");
        await _repo.UpsertCatalogItemsAsync(new[]
        {
            new NotionGameCatalogItem
            {
                PageId = "master-cover2",
                Name = "只有cover的游戏",
                CoverUrl = "https://example.com/cover2.jpg"
            }
        });

        await cover.EnsureLibraryCoversAsync(_repo);

        requestedUrl.Should().Be("https://example.com/cover2.jpg", "没有 icon 时 cover 仍是有效兜底");
        cover.HasCover("steam", "2002").Should().BeTrue();
    }

    /// <summary>记录第一个被请求的 URL，返回固定字节。</summary>
    private sealed class ProbeHandler(Action<string?> onUrl) : HttpMessageHandler
    {
        private int _called;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _called, 1) == 0)
                onUrl(request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47])
            });
        }
    }
}
