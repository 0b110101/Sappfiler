namespace GameTimeTracker.Core.Services;

/// <summary>
/// 时长换算的**唯一取整点**（2026-10-09 canonical seconds 清扫）。
///
/// <para>不变量：</para>
/// <code>
/// duration_seconds = canonical fact（事实层，任何地方都不许被分钟污染）
/// duration_minutes = derived / display compatibility（= seconds / 60，且**只截断一次**）
/// hours            = display only
/// </code>
///
/// <para>
/// 规则：任何统计都必须 <c>Σ duration_seconds</c>，**最后一步**才除以 60；
/// 严禁 <c>Σ floor(seconds / 60)</c> 再当总分钟用 —— 每行最多丢 59 秒，
/// 行数不同（跨日拆分、双机合并）就会让 A/B 显示差 1 分钟。
/// </para>
/// </summary>
public static class DurationMath
{
    /// <summary>秒 → 分钟（截断；唯一允许的"由秒派生分钟"实现）。</summary>
    public static int MinutesFromSeconds(long seconds)
        => (int)(Math.Max(0, seconds) / 60);

    /// <summary>秒 → 小时（浮点，仅供展示/跨系统换算，**不要**再拿回去当事实）。</summary>
    public static double HoursFromSeconds(long seconds)
        => Math.Max(0, seconds) / 3600.0;

    /// <summary>对一批"带秒级时长"的行求和（先求和，绝不逐行换算）。</summary>
    public static long SumSeconds<T>(IEnumerable<T> rows, Func<T, int> secondsSelector)
        => rows.Sum(r => (long)secondsSelector(r));
}
