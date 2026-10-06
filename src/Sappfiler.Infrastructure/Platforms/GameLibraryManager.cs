using System;
using System.Collections.Concurrent;
using GameTimeTracker.Infrastructure.Process;
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
                AppLog.Warn($"[平台检索] {detector.PlatformName} 检索失败: {ex.Message}");
            }
        }

        _installedGames = games;
        _processCache.Clear();   // 刷新后清空缓存

        AppLog.Info($"[平台检索] 共检索到 {_installedGames.Count} 个已记录游戏");
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
            path =>
            {
                // ⚠️ **反作弊 / 启动器 / 辅助进程永远不算游戏本体**。
                // 它们常常就住在游戏安装目录里（例如 <game>\EasyAntiCheat\EasyAntiCheat.exe），
                // 或与游戏同时运行（例如 C:\Program Files (x86)\EasyAntiCheat_EOS），
                // 只按"安装目录前缀"匹配就会把它们认成这款游戏，并让它们的 exe 路径
                // 进入游戏身份（games.executable_path）—— 后果是游戏图标变成反作弊的 logo：
                // Halo: The Master Chief Collection 真机实测（2026-10-05）即为该现象。
                //
                // 判据复用 ProcessFilter（与进程黑名单同一批关键字，避免两处漂移）。
                // 放在缓存回调里执行 ⇒ 同一路径每次库刷新只记一条日志，不会每轮刷屏。
                if (ProcessFilter.IsEcosystemComponent(path))
                {
                    AppLog.Info($"[平台检索] 忽略非游戏组件：{path}");
                    return (InstalledGame?)null;
                }

                return FindMatchingGame(path);
            });
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
            // 本体裁决门（2026-10-07）：Deferred/Downgraded/Denied 的 exe **绝不能**成为游戏本体。
            // 这样 mcclauncher.exe 这类启动器不会再被"安装目录前缀匹配"认领。
            var key = (installed.Platform + "/" + installed.PlatformId).ToLowerInvariant();
            if (_tickVerdicts.TryGetValue(key, out var verdict))
            {
                var isPrimary = verdict.Decisions.Any(d =>
                    d.IsPrimary && string.Equals(d.ExePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase));
                if (!isPrimary)
                {
                    AppLog.Info($"[本体裁决] 忽略非本体进程：{process.ExecutablePath}");
                    return null;
                }
            }

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
        return RegisterKnownGame("manual", null, name, exeOrDirectory);
    }

    /// <summary>
    /// 注册数据库中已记录的游戏（保留其真实平台与 platform_id，绝不篡改为 manual）
    /// </summary>
    public InstalledGame RegisterKnownGame(string platform, string? platformId, string name, string exeOrDirectory)
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
            // 路径可能在别处或未插盘，仍容错记录
            installDir = Path.GetDirectoryName(exeOrDirectory) ?? "";
            exePath    = exeOrDirectory;
        }

        var plat = string.IsNullOrWhiteSpace(platform) ? "manual" : platform.ToLowerInvariant();
        var pid = string.IsNullOrWhiteSpace(platformId) ? ComputeSha256Short(exeOrDirectory) : platformId;

        var game = new InstalledGame(
            Platform:   plat,
            PlatformId: pid,
            Name:       name,
            InstallDir: installDir.TrimEnd('\\', '/'),
            ExePath:    exePath
        );

        // 如果内存中已有完全相同 exePath 的项，优先保留非 manual 的权威项
        var existingIdx = _installedGames.FindIndex(g =>
            !string.IsNullOrWhiteSpace(g.ExePath) &&
            string.Equals(g.ExePath, exePath, StringComparison.OrdinalIgnoreCase));

        if (existingIdx >= 0)
        {
            var old = _installedGames[existingIdx];
            // 若旧项是 manual 而新项是知名平台，或者新项信息更全，则替换
            if (old.Platform == "manual" && plat != "manual")
            {
                var list = new List<InstalledGame>(_installedGames);
                list[existingIdx] = game;
                _installedGames = list;
            }
        }
        else
        {
            _installedGames = [.._installedGames, game];
        }

        _processCache.Clear();
        return game;
    }

    // ── 本体裁决（C1/C2，2026-10-07）───────────────────────────────────────

    /// <summary>本 exe **连续**被观察到的 tick 数（5s/轮）。纯内存状态 —— 绝不由历史 session 反推。</summary>
    private readonly ConcurrentDictionary<string, int> _exeTickStreak = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本 tick 每个游戏（platform/platformId）的裁决结果。</summary>
    private readonly ConcurrentDictionary<string, GroupVerdict> _tickVerdicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 由扫描循环在**每轮扫描开始时**调用一次：把本 tick 的全部候选**一次性**交给裁决器。
    /// </summary>
    /// <remarks>
    /// 为什么必须"一次性"：否则先被处理的候选会先写 DB/先抠图，后到的本体候选再正确也晚了
    /// —— 这正是 mcclauncher.exe 抢先的成因。
    /// </remarks>
    public void BeginScanTick(
        IReadOnlyList<ProcessExeCandidate> candidates,
        IReadOnlyDictionary<string, int>? historicalSessionCounts = null,
        IReadOnlyDictionary<string, string?>? knownPrimaryByGameKey = null)
    {
        if (candidates == null) return;

        // 1) 更新"连续 tick"证据（内存）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c.ExePath)) seen.Add(c.ExePath);
        }
        foreach (var exe in seen)
        {
            _exeTickStreak.AddOrUpdate(exe, 1, (_, v) => v + 1);
        }
        foreach (var exe in _exeTickStreak.Keys.ToList())
        {
            if (!seen.Contains(exe)) _exeTickStreak.TryRemove(exe, out _);
        }

        // 2) 按游戏分组（先沿用目录前缀认领，随后由裁决过滤）
        _tickVerdicts.Clear();
        var groups = new Dictionary<string, (InstalledGame Game, List<ProcessExeCandidate> Items)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.ExePath)) continue;
            var game = FindMatchingGame(c.ExePath.ToLowerInvariant());
            if (game is null || string.IsNullOrWhiteSpace(game.InstallDir)) continue;

            var key = (game.Platform + "/" + game.PlatformId).ToLowerInvariant();
            if (!groups.TryGetValue(key, out var g))
            {
                g = (game, new List<ProcessExeCandidate>());
                groups[key] = g;
            }
            g.Items.Add(c);
        }

        // 3) 逐组裁决（同 tick、同视图内整体判定）
        foreach (var (key, g) in groups)
        {
            var verdict = PrimaryExeResolver.Resolve(
                g.Items,
                knownPrimaryByGameKey != null && knownPrimaryByGameKey.TryGetValue(key, out var kp) ? kp : null,
                exe => historicalSessionCounts != null &&
                       historicalSessionCounts.TryGetValue(Path.GetFileNameWithoutExtension(exe), out var n) ? n : 0,
                exe => _exeTickStreak.TryGetValue(exe, out var s) ? s : 0,
                g.Game.Name);

            _tickVerdicts[key] = verdict;

            foreach (var d in verdict.Decisions)
            {
                var tag = d.IsPrimary ? "PRIMARY" : d.Tier.ToString();
                AppLog.Info($"[本体裁决] {tag} {d.ExePath} — {d.Reason}");
            }
        }
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
