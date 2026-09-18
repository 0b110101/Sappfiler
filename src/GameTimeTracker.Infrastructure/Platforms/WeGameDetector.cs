using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// WeGame（腾讯）平台检测器
/// 数据来源：注册表安装路径 + 游戏子目录扫描
/// 注意：WeGame 版本更新较频繁，此实现覆盖主流版本
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WeGameDetector : IPlatformDetector
{
    public string PlatformName => "wegame";

    public bool IsInstalled()
        => GetWeGamePath() is not null;

    public IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        var games = new List<InstalledGame>();
        var scan = new DetectorScanLog("wegame");

        var wegamePath = GetWeGamePath();
        if (wegamePath is null)
        {
            // 装是装了（IsInstalled 通过了），但拿不到安装路径 —— 这条日志是排查的关键
            scan.Skip("拿不到 WeGame 安装路径");
            scan.Report();
            return games;
        }

        // 方式一：扫描 WeGame 安装目录下的 game 子目录
        var gameBaseDir = Path.Combine(wegamePath, "game");
        if (Directory.Exists(gameBaseDir))
        {
            foreach (var g in ScanGameDirectory(gameBaseDir))
            {
                scan.Seen();
                scan.Kept();
                games.Add(g);
            }
        }
        else
        {
            scan.Skip("安装目录下无 game 子目录");
        }

        // 方式二：扫描常见游戏配置文件
        var configGames = ScanWeGameConfig(wegamePath).ToList();
        scan.Seen();
        if (configGames.Count > 0) scan.Kept();
        else scan.Skip("配置文件里没解析出游戏");
        MergeGames(games, configGames);

        // 方式三：注册表扫描（部分老版本游戏注册了独立键）
        foreach (var g in ScanWeGameRegistry())
        {
            scan.Seen();
            scan.Kept();
            games.Add(g);
        }

        scan.Report();
        return games;
    }

    // ── 私有辅助 ────────────────────────────────────────────────────────────

    private static string? GetWeGamePath()
    {
        // 优先用户级，再查机器级
        var paths = new[]
        {
            (@"HKEY_CURRENT_USER\Software\Tencent\WeGame",     "installPath"),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\Tencent\WeGame",    "installPath"),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Tencent\WeGame", "installPath"),
        };

        foreach (var (regPath, valueName) in paths)
        {
            var val = Registry.GetValue(regPath, valueName, null) as string;
            if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                return val;
        }
        return null;
    }

    /// <summary>扫描 WeGame/game/ 下每个游戏子目录</summary>
    private static IEnumerable<InstalledGame> ScanGameDirectory(string gameBaseDir)
    {
        foreach (var gameDir in Directory.EnumerateDirectories(gameBaseDir))
        {
            // WeGame 的每个游戏子目录名通常是数字 GameID
            var dirName = Path.GetFileName(gameDir);

            // 尝试读取 game.ini 或 game_info.json
            var game = TryReadGameIni(gameDir, dirName)
                    ?? TryReadGameInfoJson(gameDir, dirName);

            if (game is not null)
                yield return game;
        }
    }

    private static InstalledGame? TryReadGameIni(string gameDir, string dirName)
    {
        var iniPath = Path.Combine(gameDir, "game.ini");
        if (!File.Exists(iniPath)) return null;

        try
        {
            var ini = ParseSimpleIni(File.ReadAllText(iniPath));
            var name = ini.GetValueOrDefault("name") ?? ini.GetValueOrDefault("gameName") ?? dirName;
            var exePath = ini.GetValueOrDefault("exePath") ?? ini.GetValueOrDefault("launchExe");
            var installPath = ini.GetValueOrDefault("installPath") ?? gameDir;
            var gameId = ini.GetValueOrDefault("gameId") ?? dirName;

            if (!Directory.Exists(installPath)) installPath = gameDir;

            return new InstalledGame(
                Platform:   "wegame",
                PlatformId: gameId,
                Name:       name,
                InstallDir: installPath.TrimEnd('\\', '/'),
                ExePath:    File.Exists(exePath) ? exePath : null
            );
        }
        catch { return null; }
    }

    private static InstalledGame? TryReadGameInfoJson(string gameDir, string dirName)
    {
        var jsonPath = Path.Combine(gameDir, "game_info.json");
        if (!File.Exists(jsonPath)) return null;

        try
        {
            var json   = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root   = json.RootElement;
            var name   = root.TryGetProperty("name", out var n) ? n.GetString() : dirName;
            var gameId = root.TryGetProperty("gameId", out var id) ? id.GetString() : dirName;
            var installPath = root.TryGetProperty("installPath", out var ip) ? ip.GetString() : gameDir;

            if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
                installPath = gameDir;

            return new InstalledGame(
                Platform:   "wegame",
                PlatformId: gameId ?? dirName,
                Name:       name ?? dirName,
                InstallDir: installPath!.TrimEnd('\\', '/'),
                ExePath:    null
            );
        }
        catch { return null; }
    }

    private static IEnumerable<InstalledGame> ScanWeGameConfig(string wegamePath)
    {
        // WeGame 部分版本将已安装游戏写入 LocalAppData 缓存
        var localCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeGame");
        if (!Directory.Exists(localCache)) yield break;

        // 扫描 .json 配置文件（仅作补充）
        foreach (var json in Directory.EnumerateFiles(localCache, "*.json",
            SearchOption.AllDirectories))
        {
            InstalledGame? game = null;
            try { game = TryReadGameInfoJson(Path.GetDirectoryName(json)!, json); }
            catch { }
            if (game is not null) yield return game;
        }
    }

    /// <summary>部分独立游戏（如 LOL）有独立注册表项</summary>
    private static IEnumerable<InstalledGame> ScanWeGameRegistry()
    {
        var tencentKeys = new[]
        {
            @"SOFTWARE\Tencent",
            @"SOFTWARE\WOW6432Node\Tencent",
        };

        foreach (var keyPath in tencentKeys)
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) continue;

            foreach (var subName in key.GetSubKeyNames())
            {
                // 跳过 WeGame 本身，只看游戏子键
                if (subName.Equals("WeGame", StringComparison.OrdinalIgnoreCase)) continue;

                using var subKey = key.OpenSubKey(subName);
                var installDir = subKey?.GetValue("InstallPath")?.ToString()
                              ?? subKey?.GetValue("Install_Path")?.ToString();

                if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                {
                    yield return new InstalledGame(
                        Platform:   "wegame",
                        PlatformId: subName,
                        Name:       subName,
                        InstallDir: installDir.TrimEnd('\\', '/'),
                        ExePath:    null
                    );
                }
            }
        }
    }

    private static void MergeGames(List<InstalledGame> target, IEnumerable<InstalledGame> source)
    {
        foreach (var game in source)
        {
            // 按安装目录去重
            if (!target.Any(g => g.InstallDir.Equals(game.InstallDir,
                StringComparison.OrdinalIgnoreCase)))
                target.Add(game);
        }
    }

    private static Dictionary<string, string> ParseSimpleIni(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith('['))
                continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            var key = trimmed[..eq].Trim();
            var val = trimmed[(eq + 1)..].Trim().Trim('"');
            result.TryAdd(key, val);
        }
        return result;
    }
}
