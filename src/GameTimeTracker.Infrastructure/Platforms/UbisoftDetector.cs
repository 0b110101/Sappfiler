using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// Ubisoft Connect 平台检测器
/// 数据来源：HKLM\SOFTWARE\Ubisoft\Launcher\Installs\*
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UbisoftDetector : IPlatformDetector
{
    private const string UbiRegistryPath = @"SOFTWARE\Ubisoft\Launcher\Installs";

    // Uplay GameID → 游戏名称的补充映射（常见游戏，作为 fallback）
    // 完整名称可通过 Ubisoft 的 catalog API 获取，此处仅为常见游戏
    private static readonly Dictionary<string, string> KnownGameNames = new()
    {
        ["635"]  = "Assassin's Creed Odyssey",
        ["274"]  = "Assassin's Creed Origins",
        ["5252"]  = "Watch Dogs 2",
        ["2406"]  = "Far Cry 5",
        ["6291"]  = "Far Cry 6",
        ["1771"]  = "The Division 2",
        ["3539"]  = "Rainbow Six Siege",
    };

    public string PlatformName => "ubisoft";

    public bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(UbiRegistryPath);
        return key is not null;
    }

    public IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        using var installsKey = Registry.LocalMachine.OpenSubKey(UbiRegistryPath);
        if (installsKey is null) return [];

        var games = new List<InstalledGame>();

        foreach (var gameId in installsKey.GetSubKeyNames())
        {
            try
            {
                using var gameKey = installsKey.OpenSubKey(gameId);
                if (gameKey is null) continue;

                var installDir = gameKey.GetValue("InstallDir")?.ToString();
                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) continue;

                // Ubisoft 注册表不含游戏名，尝试几种方式获取
                var name = GetGameName(gameId, installDir);

                games.Add(new InstalledGame(
                    Platform:   "ubisoft",
                    PlatformId: gameId,
                    Name:       name,
                    InstallDir: installDir.TrimEnd('\\', '/'),
                    ExePath:    null   // 通过目录匹配，不需要精确 exe
                ));
            }
            catch { /* 跳过损坏的注册表项 */ }
        }

        return games;
    }

    // ── 私有辅助 ────────────────────────────────────────────────────────────

    private static string GetGameName(string gameId, string installDir)
    {
        // 1. 已知映射表
        if (KnownGameNames.TryGetValue(gameId, out var knownName))
            return knownName;

        // 2. 尝试读取安装目录名作为 fallback
        var dirName = Path.GetFileName(installDir.TrimEnd('\\', '/'));
        if (!string.IsNullOrEmpty(dirName))
            return dirName;

        return $"Ubisoft Game ({gameId})";
    }
}
