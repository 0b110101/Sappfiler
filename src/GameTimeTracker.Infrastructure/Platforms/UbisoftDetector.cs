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

    /// <summary>
    /// 打开 Ubisoft 注册表键，**两个视图都试**。
    /// </summary>
    /// <remarks>
    /// Ubisoft Connect 是 32 位程序，在 64 位 Windows 上写 HKLM\SOFTWARE\... 会被
    /// 重定向到 WOW6432Node\SOFTWARE\...；本程序是 x64，直接读 Registry.LocalMachine
    /// 拿到的是 64 位视图，**看不到 Ubisoft 的游戏**。
    /// 同 GOG / Steam 的同类问题（详见 GogDetector.OpenGogKey 的说明）。
    /// </remarks>
    private static RegistryKey? OpenUbiKey()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                var key = baseKey.OpenSubKey(UbiRegistryPath);
                if (key is not null)
                {
                    baseKey.Dispose();
                    return key;
                }
                baseKey.Dispose();
            }
            catch { /* 该视图不可用时试下一个 */ }
        }
        return null;
    }

    public bool IsInstalled()
    {
        using var key = OpenUbiKey();
        return key is not null;
    }

    public IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        using var installsKey = OpenUbiKey();
        if (installsKey is null) return [];

        var games = new List<InstalledGame>();
        var scan = new DetectorScanLog("ubisoft");

        foreach (var gameId in installsKey.GetSubKeyNames())
        {
            scan.Seen();
            try
            {
                using var gameKey = installsKey.OpenSubKey(gameId);
                if (gameKey is null) { scan.Skip("注册表项读不到"); continue; }

                var installDir = gameKey.GetValue("InstallDir")?.ToString();
                if (string.IsNullOrEmpty(installDir)) { scan.Skip("注册表缺 InstallDir"); continue; }
                if (!Directory.Exists(installDir)) { scan.Skip("安装目录不存在"); continue; }

                // Ubisoft 注册表不含游戏名，尝试几种方式获取
                var name = GetGameName(gameId, installDir);

                games.Add(new InstalledGame(
                    Platform:   "ubisoft",
                    PlatformId: gameId,
                    Name:       name,
                    InstallDir: installDir.TrimEnd('\\', '/'),
                    ExePath:    null   // 通过目录匹配，不需要精确 exe
                ));
                scan.Kept();
            }
            catch { scan.Skip("注册表项解析失败"); }
        }

        scan.Report();
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
