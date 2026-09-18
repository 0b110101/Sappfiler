using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// GOG Galaxy 平台检测器
/// 数据来源：HKLM\SOFTWARE\GOG.com\Games\*
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GogDetector : IPlatformDetector
{
    private const string GogRegistryPath = @"SOFTWARE\GOG.com\Games";

    public string PlatformName => "gog";

    public bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(GogRegistryPath);
        return key is not null;
    }

    public IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        using var gogKey = Registry.LocalMachine.OpenSubKey(GogRegistryPath);
        if (gogKey is null) return [];

        var games = new List<InstalledGame>();

        foreach (var gameId in gogKey.GetSubKeyNames())
        {
            try
            {
                using var gameKey = gogKey.OpenSubKey(gameId);
                if (gameKey is null) continue;

                var name       = gameKey.GetValue("gameName")?.ToString();
                var exePath    = gameKey.GetValue("exe")?.ToString();
                var installDir = gameKey.GetValue("path")?.ToString();
                var productId  = gameKey.GetValue("productId")?.ToString() ?? gameId;

                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) continue;

                games.Add(new InstalledGame(
                    Platform:   "gog",
                    PlatformId: productId,
                    Name:       name ?? "Unknown",
                    InstallDir: installDir.TrimEnd('\\', '/'),
                    ExePath:    File.Exists(exePath) ? exePath : null
                ));
            }
            catch { /* 跳过损坏的注册表项 */ }
        }

        return games;
    }
}
