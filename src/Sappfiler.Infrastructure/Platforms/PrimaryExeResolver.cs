using GameTimeTracker.Infrastructure.Process;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>本 tick 观察到的进程候选（来自一次进程枚举，**不重扫**）。</summary>
public sealed record ProcessExeCandidate(string ExePath, string ProcessName, int Pid);

/// <summary>裁决档位。</summary>
public enum ExeTier
{
    /// <summary>T0 已确认本体（games.executable_path 命中）。</summary>
    KnownPrimary,
    /// <summary>T1 可信运行证据（历史 session 且**非组件角色**）。</summary>
    TrustedRuntime,
    /// <summary>T2 本体样式（命名/路径像本体）。</summary>
    PrimaryStyle,
    /// <summary>T3 未知（观察，无写权）。</summary>
    Unknown,
    /// <summary>T4 启动器/安装器/辅助（默认降级，仅"无更可信候选 + 新运行证据"可晋升）。</summary>
    Downgraded,
    /// <summary>T5 反作弊/系统组件/旁路目录（硬否决，永不晋升）。</summary>
    Denied
}

/// <summary>单个候选的裁决结果。</summary>
public sealed record ExeDecision(string ExePath, string ProcessName, ExeTier Tier, bool IsPrimary, string Reason);

/// <summary>一次（同一 tick、同一游戏安装目录内）的裁决结果。</summary>
public sealed record GroupVerdict(string? PrimaryExe, IReadOnlyList<ExeDecision> Decisions);

/// <summary>
/// **本体裁决器**：回答"同一游戏安装目录里的哪个 EXE 才是游戏本体"。
///
/// <para>
/// 设计动机（2026-10-07 Halo 事故）：Steam 游戏的 <c>InstalledGame.ExePath</c> 恒为 null，
/// 于是 <c>FindMatchingGame</c> 只能按"安装目录前缀"认领进程 ⇒ 目录内**任意** exe
///（如 <c>mcclauncher.exe</c>）都会被当成这款游戏，进而把 EasyAntiCheat 的图标抠成游戏图标。
/// </para>
///
/// <para>
/// 两层模型（**不是**单一直线序）：
/// <code>
/// 候选 ──┬── T5 反作弊/系统组件/旁路目录   → DENY（永不写、永不持久化、永不晋升）
///        ├── T4 启动器/安装器/辅助         → DOWNGRADED（仅"无更可信候选 + 新运行证据"可晋升）
///        └── T0/T1/T2                      → PRIMARY
///             T3 未知                      → 观察，无写权
/// </code>
/// </para>
///
/// <para>
/// ⚠️ **历史污染免疫**：T4/T5 **绝不能**因为历史上存在（由旧 bug 产生的）session 而晋升。
/// 历史 session 只允许给"已通过非组件闸门"的候选加分（T1）。
/// </para>
/// </summary>
public static class PrimaryExeResolver
{
    /// <summary>
    /// 新运行证据阈值：**连续**观察到的 tick 数（5s 周期 × 3 ≈ 15s）。
    /// 固定口径，暂不做动态标定；必须是**本轮内存状态**产生的证据，不由历史 session 反推。
    /// </summary>
    public const int RequiredConsecutiveTicks = 3;

    private static readonly string[] BypassPathKeywords =
    {
        @"\easyanticheat", @"\battleye", @"\anticheat", @"\installers", @"\_commonredist",
        @"\redist", @"\directx", @"\dotnet", @"\support\", @"\engine\binaries", @"\tools\", @"\crashreport"
    };

    private static readonly string[] DowngradedNameKeywords =
    {
        "launcher", "bootstrapper", "bootstrap", "installer", "setup", "updater", "update",
        "helper", "config", "reporter", "report", "uninstall", "patcher"
    };

    private static readonly string[] PrimaryPathMarkers =
    {
        @"\binaries\", @"\bin\x64", @"\binaries\win64", @"\win64\", @"\x64\", @"\game\", @"\gamedata\"
    };

    private static readonly string[] PrimaryNameMarkers = { "shipping", "-win64", "win64" };

    /// <summary>T5：反作弊 / 系统组件 / 旁路目录 —— 硬否决。</summary>
    public static bool IsDenied(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return true;

        // 复用既有生态组件判据（含 EAC/BattlEye/启动器集合/工具名正则/路径关键字）
        if (ProcessFilter.IsEcosystemComponent(exePath)) return true;

        var lower = exePath.ToLowerInvariant();
        foreach (var kw in BypassPathKeywords)
        {
            if (lower.Contains(kw)) return true;
        }
        return false;
    }

    /// <summary>T4：角色降级（启动器 / 安装器 / 升级器 / 辅助）。</summary>
    public static bool IsDowngradedRole(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var name = Path.GetFileName(exePath).ToLowerInvariant();
        foreach (var kw in DowngradedNameKeywords)
        {
            if (name.Contains(kw)) return true;
        }
        return false;
    }

    /// <summary>T2：本体样式（命名或路径像游戏本体）。</summary>
    public static bool IsPrimaryStyle(string? exePath, string? gameName = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        var name = Path.GetFileName(exePath).ToLowerInvariant();
        foreach (var m in PrimaryNameMarkers)
        {
            if (name.Contains(m)) return true;
        }

        if (!string.IsNullOrWhiteSpace(gameName))
        {
            var g = new string(gameName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            var n = new string(name.Where(char.IsLetterOrDigit).ToArray());
            if (g.Length >= 4 && n.Contains(g)) return true;
        }

        var lower = exePath.ToLowerInvariant();
        foreach (var p in PrimaryPathMarkers)
        {
            if (lower.Contains(p)) return true;
        }

        return false;
    }

    /// <summary>
    /// 同一 tick、同一安装目录内的**批量**裁决。
    /// </summary>
    /// <param name="candidates">本 tick 该目录下出现的候选（顺序无关，结果必须与顺序无关）。</param>
    /// <param name="knownPrimaryExe">该游戏已持久化的本体 exe（games.executable_path），可为 null。</param>
    /// <param name="historicalSessionCount">历史 session 次数查询（**仅在通过非组件闸门后**才被使用）。</param>
    /// <param name="consecutiveTicks">本 exe 连续被观察到的 tick 数（内存状态，从 1 开始）。</param>
    /// <param name="gameName">游戏名（用于 T2 的名称相似判定）。</param>
    public static GroupVerdict Resolve(
        IReadOnlyList<ProcessExeCandidate> candidates,
        string? knownPrimaryExe,
        Func<string, int> historicalSessionCount,
        Func<string, int> consecutiveTicks,
        string? gameName = null)
    {
        var decisions = new List<ExeDecision>();
        if (candidates == null || candidates.Count == 0) return new GroupVerdict(null, decisions);

        // 去重（同一 exe 可能有多个进程），保留首个
        var byExe = new Dictionary<string, ProcessExeCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.ExePath)) continue;
            byExe.TryAdd(c.ExePath, c);
        }

        var known = Normalize(knownPrimaryExe);

        // ---- 第一遍：定档 ----
        var tiers = new Dictionary<string, ExeTier>(StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, cand) in byExe)
        {
            if (IsDenied(exe))
            {
                tiers[exe] = ExeTier.Denied;
                reasons[exe] = "T5 反作弊/系统组件/旁路目录（硬否决，永不晋升）";
                continue;
            }

            if (IsDowngradedRole(exe))
            {
                tiers[exe] = ExeTier.Downgraded;
                reasons[exe] = "T4 启动器/安装器/辅助角色（不能靠历史 session 晋升）";
                continue;
            }

            if (!string.IsNullOrEmpty(known) && Normalize(exe) == known)
            {
                tiers[exe] = ExeTier.KnownPrimary;
                reasons[exe] = "T0 已持久化的本体";
                continue;
            }

            if (IsPrimaryStyle(exe, gameName))
            {
                tiers[exe] = ExeTier.PrimaryStyle;
                reasons[exe] = "T2 本体命名/路径样式";
                continue;
            }

            var history = SafeCount(historicalSessionCount, exe);
            if (history > 0)
            {
                tiers[exe] = ExeTier.TrustedRuntime;
                reasons[exe] = $"T1 可信运行证据（非组件角色 + 历史 session={history}）";
                continue;
            }

            tiers[exe] = ExeTier.Unknown;
            reasons[exe] = "T3 未知（仅观察，无写权）";
        }

        // ---- 第二遍：选出 PRIMARY ----
        // 严格档位优先；同档内按 exe 路径序（保证"与候选顺序无关"）
        var ordered = tiers.Keys
            .OrderBy(k => TierRank(tiers[k]))
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? primary = null;
        foreach (var exe in ordered)
        {
            if (TierRank(tiers[exe]) <= TierRank(ExeTier.PrimaryStyle))
            {
                primary = exe;
                break;
            }
        }

        // 没有 T0/T1/T2 ⇒ 只有"无更可信候选 + 新运行证据（连续 N tick）"时，T4/T3 才可晋升
        if (primary == null)
        {
            var promotable = ordered.FirstOrDefault(exe =>
                (tiers[exe] == ExeTier.Downgraded || tiers[exe] == ExeTier.Unknown) &&
                SafeCount(consecutiveTicks, exe) >= RequiredConsecutiveTicks);

            if (promotable != null)
            {
                primary = promotable;
                tiers[promotable] = ExeTier.TrustedRuntime;
                reasons[promotable] =
                    $"T4/T3 晋升：无更可信候选 + 本轮连续 {SafeCount(consecutiveTicks, promotable)} tick 的新运行证据（≥{RequiredConsecutiveTicks}）";
            }
        }

        foreach (var exe in ordered)
        {
            var isPrimary = primary != null && string.Equals(exe, primary, StringComparison.OrdinalIgnoreCase);
            var reason = reasons[exe];
            if (!isPrimary && tiers[exe] != ExeTier.Denied && tiers[exe] != ExeTier.Downgraded)
            {
                reason += "（非 PRIMARY，本轮不写）";
            }
            decisions.Add(new ExeDecision(exe, byExe[exe].ProcessName, tiers[exe], isPrimary, reason));
        }

        return new GroupVerdict(primary, decisions);
    }

    private static int TierRank(ExeTier t) => t switch
    {
        ExeTier.KnownPrimary => 0,
        ExeTier.TrustedRuntime => 1,
        ExeTier.PrimaryStyle => 2,
        ExeTier.Unknown => 3,
        ExeTier.Downgraded => 4,
        _ => 5
    };

    private static int SafeCount(Func<string, int> f, string exe)
    {
        try { return f?.Invoke(exe) ?? 0; } catch { return 0; }
    }

    private static string Normalize(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : path.ToLowerInvariant();
}
