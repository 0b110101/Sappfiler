using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.Artwork;

[SupportedOSPlatform("windows")]
public sealed class EaArtworkProvider : IGameArtworkProvider
{
    public string ProviderName => "EaLocal";
    public int Priority => 33;

    public bool CanHandle(GameIdentity game)
    {
        return string.Equals(game.Platform, "ea", StringComparison.OrdinalIgnoreCase);
    }

    public Task<GameArtwork?> TryGetArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(game) || string.IsNullOrWhiteSpace(game.ExecutablePath))
            return Task.FromResult<GameArtwork?>(null);

        var dir = Path.GetDirectoryName(game.ExecutablePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Task.FromResult<GameArtwork?>(null);

        string[] candidates = {
            Path.Combine(dir, "header.jpg"),
            Path.Combine(dir, "header.png"),
            Path.Combine(dir, "banner.jpg"),
            Path.Combine(dir, "banner.png"),
            Path.Combine(dir, "__Installer", "header.jpg"),
            Path.Combine(dir, "__Installer", "header.png")
        };

        foreach (var file in candidates)
        {
            if (File.Exists(file) && ArtworkValidator.TryValidateImage(file, out var w, out var h))
            {
                return Task.FromResult<GameArtwork?>(new GameArtwork
                {
                    FilePathOrUrl = file,
                    IsLocalFile = true,
                    Source = ArtworkSource.EALocal,
                    Type = ArtworkType.DirectoryBanner,
                    Width = w,
                    Height = h
                });
            }
        }

        return Task.FromResult<GameArtwork?>(null);
    }
}
