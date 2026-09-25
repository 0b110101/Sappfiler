using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;

namespace GameTimeTracker.Infrastructure.Artwork;

[SupportedOSPlatform("windows")]
public sealed class SteamRemoteArtworkProvider : IGameArtworkProvider
{
    public string ProviderName => "SteamRemote";
    public int Priority => 50;

    private readonly HttpClient _httpClient;

    public SteamRemoteArtworkProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public bool CanHandle(GameIdentity game)
    {
        return string.Equals(game.Platform, "steam", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(game.PlatformId)
               && long.TryParse(game.PlatformId, out _);
    }

    public async Task<GameArtwork?> TryGetArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(game)) return null;

        var appId = game.PlatformId.Trim();
        var cacheDir = Path.Combine(AppPaths.ArtworkCacheDir, "steam");
        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        var localPath = Path.Combine(cacheDir, $"{appId}_store_header.jpg");

        // 若已下载且有效，直接返回，避免重复请求
        if (File.Exists(localPath) && ArtworkValidator.TryValidateImage(localPath, out var wCached, out var hCached))
        {
            return new GameArtwork
            {
                FilePathOrUrl = localPath,
                IsLocalFile = true,
                Source = ArtworkSource.SteamRemote,
                Type = ArtworkType.StoreHeader,
                Width = wCached,
                Height = hCached
            };
        }

        string[] cdnUrls = {
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
            $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/header.jpg"
        };

        foreach (var url in cdnUrls)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length >= ArtworkValidator.MinFileSizeBytes)
                    {
                        var tempFile = localPath + ".tmp";
                        await File.WriteAllBytesAsync(tempFile, bytes, cancellationToken);
                        if (ArtworkValidator.TryValidateImage(tempFile, out var w, out var h))
                        {
                            if (File.Exists(localPath)) File.Delete(localPath);
                            File.Move(tempFile, localPath);

                            return new GameArtwork
                            {
                                FilePathOrUrl = localPath,
                                IsLocalFile = true,
                                Source = ArtworkSource.SteamRemote,
                                Type = ArtworkType.StoreHeader,
                                Width = w,
                                Height = h
                            };
                        }
                        else
                        {
                            try { File.Delete(tempFile); } catch { }
                        }
                    }
                }
            }
            catch
            {
                // 网络超时或失败，尝试下一个 CDN 或返回 null
            }
        }

        return null;
    }
}
