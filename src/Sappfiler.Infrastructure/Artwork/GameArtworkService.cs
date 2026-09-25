using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;

namespace GameTimeTracker.Infrastructure.Artwork;

[SupportedOSPlatform("windows")]
public sealed class GameArtworkService : IGameArtworkService
{
    private readonly List<IGameArtworkProvider> _providers;
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly long _defaultMaxSizeBytes;

    public GameArtworkService(
        IEnumerable<IGameArtworkProvider>? providers = null,
        string? cacheDirectory = null,
        HttpClient? httpClient = null,
        long defaultMaxSizeBytes = 300L * 1024 * 1024)
    {
        _cacheDirectory = cacheDirectory ?? AppPaths.ArtworkCacheDir;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _defaultMaxSizeBytes = defaultMaxSizeBytes;

        if (!Directory.Exists(_cacheDirectory))
        {
            try { Directory.CreateDirectory(_cacheDirectory); } catch { }
        }

        _providers = providers?.OrderBy(p => p.Priority).ToList() ?? new List<IGameArtworkProvider>();
    }

    /// <summary>
    /// 解析游戏卡片横版背景图。
    /// 严格遵循降级链：
    /// ① Sappfiler 本地 Cache
    /// ② Notion Cover
    /// ③ 平台本地 Artwork (Steam / Epic / Xbox / EA / Ubisoft)
    /// ④ 本地游戏安装目录 Artwork (cover/banner/header/splash) —— 权重在远程之前
    /// ⑤ 平台远程 Artwork (Steam Remote Header via CDN)
    /// ⑥ 默认兜底 (Default / Placeholder)
    /// </summary>
    public async Task<GameArtwork?> ResolveArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default)
    {
        if (game == null) return null;

        // ① 检查 Sappfiler 本地 Cache
        var cached = CheckLocalCache(game);
        if (cached != null)
        {
            return cached;
        }

        // 遍历 Providers（按 Priority 升序）
        // Notion (20) → Local Platforms (30-34) → GameDirectory (40) → Remote (50) → Default (999)
        foreach (var provider in _providers)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!provider.CanHandle(game)) continue;

            try
            {
                var artwork = await provider.TryGetArtworkAsync(game, cancellationToken);
                if (artwork != null)
                {
                    // 若是远端 Notion 链接，尝试在后台下载并写入本地 Cache，以便后续离线直接命中
                    if (!artwork.IsLocalFile && artwork.Source == ArtworkSource.Notion && !string.IsNullOrWhiteSpace(artwork.FilePathOrUrl))
                    {
                        var downloadedPath = await TryDownloadAndCacheNotionCoverAsync(game, artwork.FilePathOrUrl, cancellationToken);
                        if (!string.IsNullOrEmpty(downloadedPath))
                        {
                            return new GameArtwork
                            {
                                FilePathOrUrl = downloadedPath,
                                IsLocalFile = true,
                                Source = ArtworkSource.Notion,
                                Type = ArtworkType.NotionCover,
                                Width = artwork.Width,
                                Height = artwork.Height
                            };
                        }
                    }

                    return artwork;
                }
            }
            catch
            {
                // 单个 provider 异常绝不中断后续降级
            }
        }

        return null;
    }

    private GameArtwork? CheckLocalCache(GameIdentity game)
    {
        if (string.IsNullOrWhiteSpace(_cacheDirectory) || !Directory.Exists(_cacheDirectory))
            return null;

        var safePlatform = (game.Platform ?? "unknown").ToLowerInvariant();
        var safeId = string.Join("_", (game.PlatformId ?? "").Split(Path.GetInvalidFileNameChars())).ToLowerInvariant();

        // 检查平台子目录或根缓存
        string[] candidateSubDirs = {
            Path.Combine(_cacheDirectory, safePlatform),
            Path.Combine(_cacheDirectory, "notion"),
            _cacheDirectory
        };

        foreach (var sub in candidateSubDirs)
        {
            if (!Directory.Exists(sub)) continue;

            string[] candidateFiles = {
                Path.Combine(sub, $"{safeId}_header.jpg"),
                Path.Combine(sub, $"{safeId}_store_header.jpg"),
                Path.Combine(sub, $"{safeId}.jpg"),
                Path.Combine(sub, $"{safeId}.png"),
                Path.Combine(sub, $"{safePlatform}_{safeId}.jpg"),
                Path.Combine(sub, $"{safePlatform}_{safeId}.png")
            };

            foreach (var file in candidateFiles)
            {
                if (File.Exists(file) && ArtworkValidator.TryValidateImage(file, out var w, out var h))
                {
                    try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch { }

                    return new GameArtwork
                    {
                        FilePathOrUrl = file,
                        IsLocalFile = true,
                        Source = ArtworkSource.Cache,
                        Type = ArtworkType.StoreHeader,
                        Width = w,
                        Height = h
                    };
                }
            }
        }

        return null;
    }

    private async Task<string?> TryDownloadAndCacheNotionCoverAsync(GameIdentity game, string url, CancellationToken ct)
    {
        try
        {
            var notionCacheDir = Path.Combine(_cacheDirectory, "notion");
            if (!Directory.Exists(notionCacheDir))
            {
                Directory.CreateDirectory(notionCacheDir);
            }

            var safeId = string.Join("_", (game.PlatformId ?? game.Name ?? "cover").Split(Path.GetInvalidFileNameChars())).ToLowerInvariant();
            var targetFile = Path.Combine(notionCacheDir, $"{safeId}.jpg");

            if (File.Exists(targetFile) && ArtworkValidator.TryValidateImage(targetFile, out _, out _))
            {
                File.SetLastAccessTimeUtc(targetFile, DateTime.UtcNow);
                return targetFile;
            }

            using var resp = await _httpClient.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length >= ArtworkValidator.MinFileSizeBytes)
                {
                    var tmp = targetFile + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes, ct);
                    if (ArtworkValidator.TryValidateImage(tmp, out _, out _))
                    {
                        if (File.Exists(targetFile)) File.Delete(targetFile);
                        File.Move(tmp, targetFile);
                        File.SetLastAccessTimeUtc(targetFile, DateTime.UtcNow);
                        return targetFile;
                    }
                    else
                    {
                        try { File.Delete(tmp); } catch { }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// LRU 缓存清理：当缓存目录总容量超过上限（默认 300MB）时，
    /// 按最后访问时间（LastAccessTimeUtc）升序淘汰最久未使用的图片，清理到 80% 水位。
    /// </summary>
    public Task CleanExpiredCacheAsync(long maxSizeBytes = 300L * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(_cacheDirectory)) return;

            try
            {
                var files = new DirectoryInfo(_cacheDirectory)
                    .EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                long totalBytes = files.Sum(f => f.Length);
                if (totalBytes <= maxSizeBytes)
                    return;

                long targetBytes = (long)(maxSizeBytes * 0.8);
                var sorted = files.OrderBy(f => f.LastAccessTimeUtc).ToList();

                foreach (var file in sorted)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (totalBytes <= targetBytes) break;

                    try
                    {
                        var len = file.Length;
                        file.Delete();
                        totalBytes -= len;
                    }
                    catch { }
                }
            }
            catch { }
        }, cancellationToken);
    }
}
