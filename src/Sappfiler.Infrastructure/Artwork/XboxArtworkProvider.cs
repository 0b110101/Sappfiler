using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.Artwork;

[SupportedOSPlatform("windows")]
public sealed class XboxArtworkProvider : IGameArtworkProvider
{
    public string ProviderName => "XboxLocal";
    public int Priority => 32;

    public bool CanHandle(GameIdentity game)
    {
        return string.Equals(game.Platform, "xbox", StringComparison.OrdinalIgnoreCase);
    }

    public Task<GameArtwork?> TryGetArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(game)) return Task.FromResult<GameArtwork?>(null);

        var exePath = game.ExecutablePath;
        if (string.IsNullOrWhiteSpace(exePath))
            return Task.FromResult<GameArtwork?>(null);

        var dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Task.FromResult<GameArtwork?>(null);

        // Xbox 常见资源名称
        string[] candidates = {
            Path.Combine(dir, "SplashScreenImage.png"),
            Path.Combine(dir, "SplashScreen.png"),
            Path.Combine(dir, "Wide310x150Logo.png"),
            Path.Combine(dir, "WideLogo.png"),
            Path.Combine(dir, "Assets", "SplashScreenImage.png"),
            Path.Combine(dir, "Assets", "SplashScreen.png"),
            Path.Combine(dir, "Assets", "Wide310x150Logo.png"),
            Path.Combine(dir, "header.png"),
            Path.Combine(dir, "banner.png")
        };

        foreach (var file in candidates)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (File.Exists(file) && ArtworkValidator.TryValidateImage(file, out var w, out var h))
            {
                return Task.FromResult<GameArtwork?>(new GameArtwork
                {
                    FilePathOrUrl = file,
                    IsLocalFile = true,
                    Source = ArtworkSource.XboxLocal,
                    Type = ArtworkType.DirectoryBanner,
                    Width = w,
                    Height = h
                });
            }
        }

        return Task.FromResult<GameArtwork?>(null);
    }
}
