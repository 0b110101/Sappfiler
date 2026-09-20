namespace GameTimeTracker.Core.Services;

/// <summary>
/// 跨日业务日期与时间戳计算助手。
/// 支持自由设置 24 点 ~ 30 点作为跨日结算点（24 = 00:00，28 = 次日 04:00）。
/// 在跨日结算点前游玩的时间均归属于前一日的统计，避免深夜游戏跨日割裂。
/// </summary>
public static class AccountingDateHelper
{
    /// <summary>
    /// 标准化跨日整点小时（限制在 24 ~ 30 之间，默认 24）。
    /// </summary>
    public static int NormalizeCutoffHour(int cutoffHour)
    {
        if (cutoffHour < 24 || cutoffHour > 30)
        {
            // 容错：如果传入 0~6 点，换算为 24~30 点
            if (cutoffHour >= 0 && cutoffHour <= 6) return cutoffHour + 24;
            return 24;
        }
        return cutoffHour;
    }

    /// <summary>
    /// 获取跨日偏移小时数（24->0, 25->1, 28->4, 30->6）。
    /// </summary>
    public static int GetOffsetHours(int cutoffHour)
    {
        var normalized = NormalizeCutoffHour(cutoffHour);
        return normalized - 24;
    }

    /// <summary>
    /// 计算指定时间点对应的业务归属日期（即减去偏移小时数后的 Date）。
    /// </summary>
    public static DateTime GetAccountingDate(DateTime dt, int cutoffHour)
    {
        int offset = GetOffsetHours(cutoffHour);
        return dt.AddHours(-offset).Date;
    }

    /// <summary>
    /// 获取指定时间点对应的业务归属日期字符串（格式："yyyy-MM-dd"）。
    /// </summary>
    public static string GetAccountingDateString(DateTime dt, int cutoffHour)
    {
        return GetAccountingDate(dt, cutoffHour).ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// 获取指定时间点之后将遭遇的下一个跨日分界时间戳（绝对时间）。
    /// 例如：对于 2026-09-17 02:30 且 cutoff=28（归属 2026-09-16），下一个分界点为 2026-09-17 04:00:00。
    /// </summary>
    public static DateTime GetNextCutoffBoundary(DateTime dt, int cutoffHour)
    {
        var accountingDate = GetAccountingDate(dt, cutoffHour);
        int offset = GetOffsetHours(cutoffHour);
        return accountingDate.AddDays(1).AddHours(offset);
    }
}
