using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// 游戏库管理器：聚合所有平台检测结果，并提供进程→游戏的快速匹配
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class GameLibraryManager
{
    private readonly IReadOnlyList<IPlatformDetector> _detectors;

    // 已知游戏列表（安装目录 → 游戏信息）
    private List<InstalledGame> _installedGames = [];

    // 进程 exe 路径缓存（避免重复扫描，Key = exe路径小写，Value = 匹配到的游戏）
    private readonly ConcurrentDictionary<string, InstalledGame?> _processCache = new();

    public GameLibraryManager()
    {
        _detectors =
        [
            new SteamDetector(),
            new EpicDetector(),
            new GogDetector(),
            new UbisoftDetector(),
            new EaDetector(),
            new WeGameDetector(),
            new XboxDetector(),
        ];
    }

    /// <summary>
    /// 刷新所有平台的已安装游戏列表
    /// 建议：程序启动时调用一次，之后每 30 分钟刷新一次
    /// </summary>
    public void Refresh()
    {
        var games = new List<InstalledGame>();

        foreach (var detector in _detectors)
        {
            if (!detector.IsInstalled()) continue;

            try
            {
                // 各检测器**自己**会输出一行「扫描 X，收录 Y，跳过 Z（原因）」，
                // 所以这里不再重复记数量，只负责兜住异常与总计数。
                games.AddRange(detector.GetInstalledGames());
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[游戏库] {detector.PlatformName} 检索失败: {ex.Message}");
            }
        }

        _installedGames = games;
        _processCache.Clear();   // 刷新后清空缓存

        AppLog.Info($"[游戏库] 共检索到 {_installedGames.Count} 个已安装游戏");
    }

    /// <summary>
    /// 获取当前所有已安装游戏（供 UI 显示）
    /// </summary>
    public IReadOnlyList<InstalledGame> GetAllInstalledGames()
        => _installedGames;

    /// <summary>
    /// 根据 exe 路径直接匹配已知游戏
    /// </summary>
    public InstalledGame? MatchExe(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        return _processCache.GetOrAdd(
            exePath.ToLowerInvariant(),
            path => FindMatchingGame(path));
    }

    /// <summary>
    /// 根据扫描到的进程信息检测并构建游戏身份
    /// </summary>
    public GameTimeTracker.Core.Models.GameIdentity? DetectGame(GameTimeTracker.Core.Models.DetectedProcess process)
    {
        if (string.IsNullOrWhiteSpace(process.ExecutablePath)) return null;

        // 1. 优先从主流平台及手动库中匹配
        var installed = MatchExe(process.ExecutablePath);
        if (installed != null)
        {
            return new GameTimeTracker.Core.Models.GameIdentity(
                installed.Platform,
                installed.PlatformId,
                installed.Name,
                process.ProcessName,
                process.ExecutablePath
            );
        }

        // 2. XboxGames 目录匹配
        var pathLower = process.ExecutablePath.ToLowerInvariant();
        if (pathLower.Contains(@"\xboxgames\"))
        {
            var parts = process.ExecutablePath.Split(Path.DirectorySeparatorChar);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "xboxgames", StringComparison.OrdinalIgnoreCase))
                {
                    var folder = parts[i + 1];
                    var exe = Path.GetFileName(process.ExecutablePath);
                    return new GameTimeTracker.Core.Models.GameIdentity("xbox", $"xbox_{folder.ToLowerInvariant()}", folder, exe, process.ExecutablePath);
                }
            }
        }

        // 3. WindowsApps Xbox Store 匹配
        if (pathLower.Contains(@"\windowsapps\"))
        {
            var knownXbox = new[] { "halo", "forza", "ageofempires", "seaofthieves", "minecraft", "gears", "psychonauts", "starfield", "subnautica", "avowed" };
            if (knownXbox.Any(g => pathLower.Contains(g)))
            {
                var exe = Path.GetFileName(process.ExecutablePath);
                var title = !string.IsNullOrWhiteSpace(process.WindowTitle) ? process.WindowTitle : Path.GetFileNameWithoutExtension(exe);
                return new GameTimeTracker.Core.Models.GameIdentity("xbox", Path.GetFileNameWithoutExtension(exe).ToLowerInvariant(), title, exe, process.ExecutablePath);
            }
        }

        return null;
    }

    /// <summary>
    /// 根据进程检测是否为已知平台游戏
    /// </summary>
    /// <param name="process">要检测的进程</param>
    /// <returns>匹配到的游戏信息，null 表示不是已知游戏</returns>
    public InstalledGame? MatchProcess(System.Diagnostics.Process process)
    {
        string? exePath;
        try
        {
            exePath = process.MainModule?.FileName;
        }
        catch (Win32Exception) { return null; } // 无权限访问（系统进程等）
        catch (InvalidOperationException) { return null; } // 进程已退出

        if (string.IsNullOrEmpty(exePath)) return null;

        return MatchExe(exePath);
    }

    /// <summary>
    /// 扫描当前所有运行中的进程，返回匹配到的游戏进程
    /// </summary>
    public IEnumerable<(System.Diagnostics.Process Process, InstalledGame Game)> ScanRunningGames()
    {
        var processes = System.Diagnostics.Process.GetProcesses();

        foreach (var proc in processes)
        {
            var game = MatchProcess(proc);
            if (game is not null)
                yield return (proc, game);
        }
    }

    // ── 手动添加 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 手动添加游戏（用户在 UI 中手动指定的 exe 或目录）
    /// 添加后持久化到 SQLite，并更新内存中的列表
    /// </summary>
    public InstalledGame AddManualGame(string name, string exeOrDirectory)
    {
        string installDir;
        string? exePath = null;

        if (File.Exists(exeOrDirectory))
        {
            exePath    = exeOrDirectory;
            installDir = Path.GetDirectoryName(exeOrDirectory)!;
        }
        else if (Directory.Exists(exeOrDirectory))
        {
            installDir = exeOrDirectory;
        }
        else
        {
            throw new ArgumentException($"路径不存在: {exeOrDirectory}");
        }

        // 生成稳定的 platform_id
        var platformId = ComputeSha256Short(exeOrDirectory);

        var game = new InstalledGame(
            Platform:   "manual",
            PlatformId: platformId,
            Name:       name,
            InstallDir: installDir.TrimEnd('\\', '/'),
            ExePath:    exePath
        );

        // 添加到内存列表
        _installedGames = [.._installedGames, game];
        _processCache.Clear();
        return game;
    }

    // ── 私有辅助 ────────────────────────────────────────────────────────────

    private InstalledGame? FindMatchingGame(string exePathLower)
    {
        // 1. 先精确匹配 ExePath（若平台提供了精确路径）
        var exactMatch = _installedGames.FirstOrDefault(g =>
            g.ExePath?.ToLowerInvariant() == exePathLower);
        if (exactMatch is not null) return exactMatch;

        // 2. 目录前缀匹配（exe 位于游戏安装目录下）
        return _installedGames.FirstOrDefault(g =>
            exePathLower.StartsWith(
                g.InstallDir.ToLowerInvariant() + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));
    }

    private static string ComputeSha256Short(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLower();
    }
}
