using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.Artwork;

[SupportedOSPlatform("windows")]
public sealed class LocalGameArtworkProvider : IGameArtworkProvider
{
    public string ProviderName => "GameDirectory";
    // 权重排在远程之前（由第 1 条需求明确指定）
    public int Priority => 40;

    private static readonly string[] CandidateFileNames = {
        "header.jpg",
        "header.png",
        "banner.jpg",
        "banner.png",
        "cover.jpg",
        "cover.png",
        "SplashScreenImage.png",
        "SplashScreen.png",
        "Wide310x150Logo.png",
        "WideLogo.png",
        "splash.png",
        "splash.jpg",
        "background.jpg",
        "background.png"
    };

    public bool CanHandle(GameIdentity game)
    {
        return !string.IsNullOrWhiteSpace(game.ExecutablePath);
    }

    public Task<GameArtwork?> TryGetArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath))
            return Task.FromResult<GameArtwork?>(null);

        var exePath = game.ExecutablePath;
        var exeDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir))
            return Task.FromResult<GameArtwork?>(null);

        var candidateDirs = new List<string> { exeDir };

        try
        {
            var parent = Directory.GetParent(exeDir)?.FullName;
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                candidateDirs.Add(parent);

                var contentSub = Path.Combine(parent, "Content");
                if (Directory.Exists(contentSub)) candidateDirs.Add(contentSub);

                var assetsSub = Path.Combine(parent, "Assets");
                if (Directory.Exists(assetsSub)) candidateDirs.Add(assetsSub);
            }

            var exeContent = Path.Combine(exeDir, "Content");
            if (Directory.Exists(exeContent)) candidateDirs.Add(exeContent);

            var exeAssets = Path.Combine(exeDir, "Assets");
            if (Directory.Exists(exeAssets)) candidateDirs.Add(exeAssets);
        }
        catch { }

        foreach (var dir in candidateDirs)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!Directory.Exists(dir)) continue;

            foreach (var name in CandidateFileNames)
            {
                var candidateFile = Path.Combine(dir, name);
                if (File.Exists(candidateFile))
                {
                    if (ArtworkValidator.TryValidateImage(candidateFile, out var w, out var h))
                    {
                        var artwork = new GameArtwork
                        {
                            FilePathOrUrl = candidateFile,
                            IsLocalFile = true,
                            Source = ArtworkSource.GameDirectory,
                            Type = ArtworkType.DirectoryBanner,
                            Width = w,
                            Height = h
                        };
                        return Task.FromResult<GameArtwork?>(artwork);
                    }
                }
            }
        }

        return Task.FromResult<GameArtwork?>(null);
    }
}
