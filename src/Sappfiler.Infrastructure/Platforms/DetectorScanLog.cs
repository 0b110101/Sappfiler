namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// 记录一次平台扫描"看到多少 / 收下多少 / 为什么跳过"，最后输出一行汇总。
/// </summary>
/// <remarks>
/// 为什么需要它：检测器原先用 `continue` 静默跳过不合格的条目，
/// **漏识别时日志里毫无痕迹**。2026-09-18 连续两次踩这个坑
/// （Steam 的 StateFlags 位标志判断错、GOG/Ubisoft 漏了 32 位注册表视图），
/// 都是"主流平台的游戏检测不到"，却只能靠翻源码猜原因。
///
/// 用法：
/// <code>
/// var scan = new DetectorScanLog("steam");
/// foreach (...) {
///     scan.Seen();
///     if (不符合) { scan.Skip("StateFlags 未安装"); continue; }
///     scan.Kept();
/// }
/// scan.Report();
/// </code>
///
/// 输出形如：
/// <code>
/// [游戏库] steam: 扫描 15，收录 12，跳过 3（安装目录不存在×2、StateFlags 未安装×1）
/// </code>
/// 跳过数为 0 时只输出"扫描 N，收录 N"，不啰嗦。
/// </remarks>
internal sealed class DetectorScanLog
{
    private readonly string _platform;
    private readonly Dictionary<string, int> _skipReasons = new(StringComparer.Ordinal);
    private int _seen;
    private int _kept;

    public DetectorScanLog(string platform) => _platform = platform;

    /// <summary>看到一条候选（无论最终收不收）。</summary>
    public void Seen() => _seen++;

    /// <summary>收录一条。</summary>
    public void Kept() => _kept++;

    /// <summary>
    /// 跳过一条，并记下原因。
    /// 原因要写得**能直接指导排查**，例如「StateFlags 未安装」而不是「过滤」。
    /// </summary>
    public void Skip(string reason)
    {
        _skipReasons[reason] = _skipReasons.TryGetValue(reason, out var n) ? n + 1 : 1;
    }

    /// <summary>写一行汇总日志。扫描数为 0 时也记（这是"平台没装/路径不对"的信号）。</summary>
    public void Report()
    {
        if (_skipReasons.Count == 0)
        {
            AppLog.Info($"[平台检索] {_platform}: 扫描 {_seen}，收录 {_kept}");
            return;
        }

        // 按跳过数量降序，方便一眼看到主因
        var detail = string.Join("、", _skipReasons
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}×{kv.Value}"));

        var skipped = _seen - _kept;
        AppLog.Info($"[平台检索] {_platform}: 扫描 {_seen}，收录 {_kept}，跳过 {skipped}（{detail}）");
    }
}
