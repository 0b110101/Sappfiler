using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using Microsoft.Win32;

namespace GameTimeTracker.Infrastructure.Artwork;

[SupportedOSPlatform("windows")]
public sealed class SteamArtworkProvider : IGameArtworkProvider
{
    public string ProviderName => "SteamLocal";
    public int Priority => 30;

    private readonly string? _overrideSteamPath;

    public SteamArtworkProvider(string? overrideSteamPath = null)
    {
        _overrideSteamPath = overrideSteamPath;
    }

    public bool CanHandle(GameIdentity game)
    {
        return string.Equals(game.Platform, "steam", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(game.PlatformId)
               && long.TryParse(game.PlatformId, out _);
    }

    public Task<GameArtwork?> TryGetArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(game)) return Task.FromResult<GameArtwork?>(null);

        var steamPath = _overrideSteamPath ?? FindSteamPath();
        if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
        {
            return Task.FromResult<GameArtwork?>(null);
        }

        var libraryCacheDir = Path.Combine(steamPath, "appcache", "librarycache");
        if (!Directory.Exists(libraryCacheDir))
        {
            return Task.FromResult<GameArtwork?>(null);
        }

        var appId = game.PlatformId.Trim();

        // 收集所有候选路径（按照 LibraryHeader 920×430 优先，绝不包含 3840×1240 的 LibraryHero）
        var candidatePaths = EnumerateCandidateArtworkPaths(libraryCacheDir, appId);

        foreach (var path in candidatePaths)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (ArtworkValidator.TryValidateImage(path, out var w, out var h))
            {
                var artwork = new GameArtwork
                {
                    FilePathOrUrl = path,
                    IsLocalFile = true,
                    Source = ArtworkSource.SteamLocal,
                    Type = ArtworkType.LibraryHeader,
                    Width = w,
                    Height = h
                };
                return Task.FromResult<GameArtwork?>(artwork);
            }
        }

        return Task.FromResult<GameArtwork?>(null);
    }

    /// <summary>
    /// 兼容 Steam 多种本地目录布局（新结构、旧结构、带 content-hash 的子目录）。
    /// 注意：严格排除 library_hero.jpg 等超大图。
    /// </summary>
    public static IEnumerable<string> EnumerateCandidateArtworkPaths(string libraryCacheDir, string appId)
    {
        // 1. 新布局: librarycache\{appId}\library_header.jpg
        var modernAppDir = Path.Combine(libraryCacheDir, appId);
        if (Directory.Exists(modernAppDir))
        {
            var header1 = Path.Combine(modernAppDir, "library_header.jpg");
            if (File.Exists(header1)) yield return header1;

            var header2 = Path.Combine(modernAppDir, "header.jpg");
            if (File.Exists(header2)) yield return header2;

            // 2. 带 content-hash 子目录: librarycache\{appId}\{hash}\library_header.jpg
            string[] subDirs = [];
            try
            {
                subDirs = Directory.GetDirectories(modernAppDir);
            }
            catch { }

            foreach (var sub in subDirs)
            {
                var hashHeader = Path.Combine(sub, "library_header.jpg");
                if (File.Exists(hashHeader)) yield return hashHeader;

                var hashHeader2 = Path.Combine(sub, "header.jpg");
                if (File.Exists(hashHeader2)) yield return hashHeader2;
            }
        }

        // 3. 旧布局: librarycache\{appId}_library_header.jpg
        var legacyHeader = Path.Combine(libraryCacheDir, $"{appId}_library_header.jpg");
        if (File.Exists(legacyHeader)) yield return legacyHeader;

        var legacyHeader2 = Path.Combine(libraryCacheDir, $"{appId}_header.jpg");
        if (File.Exists(legacyHeader2)) yield return legacyHeader2;
    }

    /// <summary>
    /// 动态探测 Steam 安装根目录（杜绝硬编码）
    /// </summary>
    public static string? FindSteamPath()
    {
        // 1. 查注册表 HKCU
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                var val = hkcu.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(val))
                {
                    var normalized = val.Replace('/', '\\');
                    if (Directory.Exists(normalized)) return normalized;
                }
            }
            catch { }
        }

        // 2. 查注册表 HKLM (32位与64位)
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                var val = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam")?.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                    return val;

                var wowVal = hklm.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(wowVal) && Directory.Exists(wowVal))
                    return wowVal;
            }
            catch { }
        }

        // 3. 常见盘符探测
        string[] commonPaths = {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"E:\Steam",
            @"F:\Steam",
            @"D:\SteamLibrary",
            @"E:\SteamLibrary",
            @"D:\Games\Steam",
            @"E:\Games\Steam"
        };

        foreach (var p in commonPaths)
        {
            if (Directory.Exists(p)) return p;
        }

        return null;
    }
}
