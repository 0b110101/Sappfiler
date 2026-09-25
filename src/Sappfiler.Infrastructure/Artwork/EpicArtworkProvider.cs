using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.Artwork;

[SupportedOSPlatform("windows")]
public sealed class EpicArtworkProvider : IGameArtworkProvider
{
    public string ProviderName => "EpicLocal";
    public int Priority => 31;

    private static readonly string ManifestDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public bool CanHandle(GameIdentity game)
    {
        return string.Equals(game.Platform, "epic", StringComparison.OrdinalIgnoreCase);
    }

    public Task<GameArtwork?> TryGetArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(game)) return Task.FromResult<GameArtwork?>(null);

        // 尝试从 Epic 清单文件解析安装路径
        string? installDir = null;
        if (Directory.Exists(ManifestDir))
        {
            try
            {
                foreach (var itemFile in Directory.EnumerateFiles(ManifestDir, "*.item"))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var json = File.ReadAllText(itemFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var appName = root.TryGetProperty("AppName", out var p1) ? p1.GetString() : null;
                    var catalogId = root.TryGetProperty("CatalogItemId", out var p2) ? p2.GetString() : null;

                    if (string.Equals(appName, game.PlatformId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(catalogId, game.PlatformId, StringComparison.OrdinalIgnoreCase))
                    {
                        installDir = root.TryGetProperty("InstallLocation", out var pLoc) ? pLoc.GetString() : null;
                        break;
                    }
                }
            }
            catch { }
        }

        // 如果找到了安装目录，在安装目录及其常见资源子目录中搜索横版素材
        if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
        {
            string[] candidateFiles = {
                Path.Combine(installDir, "banner.jpg"),
                Path.Combine(installDir, "banner.png"),
                Path.Combine(installDir, "header.jpg"),
                Path.Combine(installDir, "header.png"),
                Path.Combine(installDir, "cover.jpg"),
                Path.Combine(installDir, "cover.png")
            };

            foreach (var file in candidateFiles)
            {
                if (File.Exists(file) && ArtworkValidator.TryValidateImage(file, out var w, out var h))
                {
                    return Task.FromResult<GameArtwork?>(new GameArtwork
                    {
                        FilePathOrUrl = file,
                        IsLocalFile = true,
                        Source = ArtworkSource.EpicLocal,
                        Type = ArtworkType.DirectoryBanner,
                        Width = w,
                        Height = h
                    });
                }
            }
        }

        return Task.FromResult<GameArtwork?>(null);
    }
}
