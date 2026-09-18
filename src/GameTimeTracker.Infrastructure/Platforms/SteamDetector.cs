using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// Steam 平台检测器
/// 数据来源：注册表 + steamapps/libraryfolders.vdf + appmanifest_*.acf
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamDetector : IPlatformDetector
{
    public string PlatformName => "steam";

    public bool IsInstalled()
        => GetSteamPath() is not null;

    public IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        var steamPath = GetSteamPath();
        if (steamPath is null) return [];

        var games = new List<InstalledGame>();

        foreach (var libraryPath in GetLibraryPaths(steamPath))
        {
            var steamappsDir = Path.Combine(libraryPath, "steamapps");
            if (!Directory.Exists(steamappsDir)) continue;

            foreach (var acfFile in Directory.EnumerateFiles(steamappsDir, "appmanifest_*.acf"))
            {
                try
                {
                    var acf = ParseKeyValues(File.ReadAllText(acfFile));
                    if (!acf.TryGetValue("appid", out var appId)) continue;
                    if (!acf.TryGetValue("name", out var name)) continue;
                    if (!acf.TryGetValue("installdir", out var installDir)) continue;

                    // StateFlags: 4 = 完全安装
                    if (acf.TryGetValue("StateFlags", out var flags) && flags != "4") continue;

                    var fullInstallDir = Path.Combine(steamappsDir, "common", installDir);
                    if (!Directory.Exists(fullInstallDir)) continue;

                    games.Add(new InstalledGame(
                        Platform:   "steam",
                        PlatformId: appId,
                        Name:       name,
                        InstallDir: fullInstallDir.TrimEnd('\\', '/'),
                        ExePath:    null   // Steam 游戏通过目录前缀匹配，不需要精确 exe
                    ));
                }
                catch { /* 跳过损坏的 manifest */ }
            }
        }

        return games;
    }

    // ── 私有辅助 ────────────────────────────────────────────────────────────

    private static string? GetSteamPath()
    {
        // 先查 64 位注册表视图，再查 32 位
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            var val = hkcu.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                return val;
        }
        return null;
    }

    private static IEnumerable<string> GetLibraryPaths(string steamPath)
    {
        // Steam 根目录本身也是一个库
        yield return steamPath;

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;

        var content = File.ReadAllText(vdfPath);

        // 匹配 "path" "D:\\SteamLibrary" 或新格式的 path 键
        var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""");
        foreach (Match match in matches)
        {
            var path = match.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(path) && !path.Equals(steamPath, StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    /// <summary>
    /// 极简 Valve KeyValues 解析（只取顶层 key-value 对）
    /// 足以处理 ACF / 简单 VDF，不支持嵌套（嵌套交给 libraryfolders.vdf 的正则处理）
    /// </summary>
    private static Dictionary<string, string> ParseKeyValues(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(content, @"""([^""]+)""\s+""([^""]*)""");
        foreach (Match m in matches)
            result.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        return result;
    }
}
