using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.Services;

public record GameTimeBreakdown(
    string GameName,
    int Minutes,
    string DurationText
);

public record ActivityHeatmapCell(
    DateTime Date,
    int DayOfWeek,   // 0 = 周一, 1 = 周二 ... 6 = 周日
    int WeekIndex,   // 0 .. 11
    int DurationMinutes,
    string DurationText,
    int GameCount,
    IReadOnlyList<GameTimeBreakdown> Games,
    int Level,       // 0 to 5
    bool IsToday,
    bool IsFuture,
    string TooltipText
);

public record ActivityHeatmapMonthMarker(
    string MonthLabel,
    int ColumnIndex
);

public record ActivityHeatmapResult(
    IReadOnlyList<ActivityHeatmapCell> Cells,
    IReadOnlyList<ActivityHeatmapMonthMarker> MonthMarkers,
    int MaxMinutes,
    int TotalDays,
    int ActiveDays
);

public record DashboardStats(
    int TodayMinutes,
    string TodayDurationText,
    double TodayDeltaPercent, // positive or negative
    string TodayDeltaText,
    int WeekMinutes,
    string WeekDurationText,
    double WeekDeltaPercent,
    string WeekDeltaText,
    int StreakDays,
    IReadOnlyList<HeatmapDayModel> HeatmapDays,
    IReadOnlyList<GamePlayProgress> TodayTopGames,
    ActivityHeatmapResult ActivityHeatmap
);

public record HeatmapDayModel(
    DateTime Date,
    int DurationMinutes,
    int GameCount,
    int Level, // 0 to 5
    bool IsCurrentMonth,
    bool IsToday
);

public record GamePlayProgress(
    int GameId,
    string GameName,
    string Platform,
    string PlatformId,
    int Minutes,
    string DurationText,
    double Percentage // 0.0 to 1.0 (relative to top game or total)
);

public static class DailyAggregator
{
    /// <summary>
    /// 卡片与列表展示用的时分格式化：例如 120m → 2h，125m → 2h 05m，132m → 2h 12m，45m → 0h 45m，0m → 0h。
    /// 若刚好为整数小时，则省略分钟部分（如 2h 代替 2h 00m）。
    /// </summary>
    public static string FormatHoursMinutes(int minutes)
    {
        if (minutes <= 0) return "0h";
        int h = minutes / 60;
        int m = minutes % 60;
        if (h > 0 && m == 0) return $"{h}h";
        return $"{h}h {m:D2}m";
    }

    /// <summary>界面汇总时长统一以 Xh YYm（如 2h 05m）优雅展示。</summary>
    public static string FormatDuration(int minutes)
        => FormatHoursMinutes(minutes);


    public static string FormatSeconds(int totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>
    /// Max-relative grading: Calculates heatmap intensity level (0 to 5) relative to maximum duration.
    /// Handles maxMinutes == 0 edge case cleanly.
    /// </summary>
    public static int CalculateHeatmapLevel(int minutes, int maxMinutes)
    {
        if (minutes <= 0 || maxMinutes <= 0) return 0;
        double ratio = (double)minutes / maxMinutes;
        if (ratio > 0.80) return 5;
        if (ratio > 0.60) return 4;
        if (ratio > 0.40) return 3;
        if (ratio > 0.20) return 2;
        return 1;
    }

    /// <summary>
    /// 首页热力图的周数（7 行 × 该周数 = 覆盖天数）。
    ///
    /// ⚠️ **读取数据的日期窗口必须 ≥ 它**，否则热力图左侧会一直是空的。
    ///    2026-09-19 QA 反馈的"只显示到 6/11、更早的记录没拉到本地"就是这么来的：
    ///    读取窗口当时写死 100 天（注释还写着"覆盖 84 天热力图"，那是热力图只有 12 周时的值），
    ///    而热力图早已扩到 52 周 → 两边口径对不上，左边 3/4 永远是空的，
    ///    「游戏时长记录 N 天」也只统计到窗口内的天数。
    ///    所以窗口改由 <see cref="DataWindowStart"/> 统一给出，不要再另写数字。
    /// </summary>
    public const int HeatmapWeekCount = 52;

    /// <summary>热力图覆盖的天数（7 × <see cref="HeatmapWeekCount"/>）。</summary>
    public static int HeatmapDays => HeatmapWeekCount * 7;

    /// <summary>
    /// 首页各项统计（含热力图）需要的**最早日期**。
    ///
    /// 起点 = 参考日往前 <see cref="HeatmapDays"/> 天，再留 7 天余量 ——
    /// 热力图的第一周落在"本周日往前 51 周"，可能比整 364 天再早几天。
    /// </summary>
    public static DateTime DataWindowStart(DateTime referenceDate)
        => referenceDate.Date.AddDays(-(HeatmapDays + 7));

    /// <summary>
    /// Generates the 7 rows x 52 columns (~364 days, 1 year) Activity Contribution Heatmap.
    /// Range: from Monday of (weekCount - 1) weeks ago to Sunday of the current week.
    /// </summary>
    public static ActivityHeatmapResult GenerateActivityHeatmap(
        DateTime referenceDate,
        IReadOnlyList<DailySummary> allSummaries,
        int weekCount = HeatmapWeekCount)
    {
        var refDate = referenceDate.Date;
        // Sunday of current week (DayOfWeek.Sunday is 0)
        int sundayOffset = (int)refDate.DayOfWeek;
        var thisSunday = refDate.AddDays(-sundayOffset);
        var startDate = thisSunday.AddDays(-7 * (weekCount - 1));
        var endDate = thisSunday.AddDays(6); // Saturday of this week

        // Group summaries by date and by game
        var summariesByDate = allSummaries
            .GroupBy(s => s.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Pre-compute daily totals and games breakdown
        var dailyBreakdowns = new Dictionary<string, (int totalMins, List<GameTimeBreakdown> games)>();
        foreach (var kvp in summariesByDate)
        {
            var dateStr = kvp.Key;
            var list = kvp.Value;
            var total = list.Sum(s => s.DurationMinutes);
            var breakdown = list
                .OrderByDescending(s => s.DurationMinutes)
                .Select(s => new GameTimeBreakdown(
                    GameName: string.IsNullOrWhiteSpace(s.GameName) ? $"Game #{s.GameId}" : s.GameName,
                    Minutes: s.DurationMinutes,
                    DurationText: FormatDuration(s.DurationMinutes)
                ))
                .ToList();
            dailyBreakdowns[dateStr] = (total, breakdown);
        }

        // Calculate maxMinutes within the window (excluding future days)
        int maxMinutes = 0;
        int activeDays = 0;
        for (var d = startDate; d <= endDate; d = d.AddDays(1))
        {
            if (d <= refDate)
            {
                var dStr = d.ToString("yyyy-MM-dd");
                if (dailyBreakdowns.TryGetValue(dStr, out var info) && info.totalMins > 0)
                {
                    if (info.totalMins > maxMinutes) maxMinutes = info.totalMins;
                    activeDays++;
                }
            }
        }

        var cells = new List<ActivityHeatmapCell>();
        var monthMarkers = new List<ActivityHeatmapMonthMarker>();
        int lastMarkerMonth = -1;
        int lastMarkerWeek = -10;

        for (int weekIdx = 0; weekIdx < weekCount; weekIdx++)
        {
            var weekSunday = startDate.AddDays(weekIdx * 7);

            // Place month marker on month change with spacing
            if (weekSunday.Month != lastMarkerMonth && (weekIdx - lastMarkerWeek >= 3 || lastMarkerWeek == -10))
            {
                monthMarkers.Add(new ActivityHeatmapMonthMarker($"{weekSunday.Month}月", weekIdx));
                lastMarkerMonth = weekSunday.Month;
                lastMarkerWeek = weekIdx;
            }

            for (int dayOfWeek = 0; dayOfWeek < 7; dayOfWeek++)
            {
                var cellDate = weekSunday.AddDays(dayOfWeek);
                var dateStr = cellDate.ToString("yyyy-MM-dd");
                bool isFuture = cellDate > refDate;
                bool isToday = cellDate == refDate;

                dailyBreakdowns.TryGetValue(dateStr, out var info);
                int mins = isFuture ? 0 : info.totalMins;
                var games = isFuture ? new List<GameTimeBreakdown>() : (info.games ?? new List<GameTimeBreakdown>());
                int level = isFuture ? 0 : CalculateHeatmapLevel(mins, maxMinutes);

                // Build rich tooltip
                string tooltip;
                if (isFuture)
                {
                    tooltip = string.Empty;
                }
                else if (mins <= 0)
                {
                    tooltip = $"{cellDate:M月d日}\n没有游戏记录";
                }
                else
                {
                    var lines = new List<string>
                    {
                        cellDate.ToString("M月d日"),
                        $"总计 {FormatDuration(mins)} · {games.Count} 个游戏"
                    };
                    foreach (var g in games.Take(5))
                    {
                        lines.Add($"{g.GameName}  {g.DurationText}");
                    }
                    if (games.Count > 5)
                    {
                        lines.Add($"等共 {games.Count} 款游戏");
                    }
                    tooltip = string.Join("\n", lines);
                }

                cells.Add(new ActivityHeatmapCell(
                    Date: cellDate,
                    DayOfWeek: dayOfWeek,
                    WeekIndex: weekIdx,
                    DurationMinutes: mins,
                    DurationText: FormatDuration(mins),
                    GameCount: games.Count,
                    Games: games,
                    Level: level,
                    IsToday: isToday,
                    IsFuture: isFuture,
                    TooltipText: tooltip
                ));
            }
        }

        return new ActivityHeatmapResult(
            Cells: cells,
            MonthMarkers: monthMarkers,
            MaxMinutes: maxMinutes,
            TotalDays: weekCount * 7,
            ActiveDays: activeDays
        );
    }

    public static DashboardStats Aggregate(
        DateTime referenceDate,
        IReadOnlyList<DailySummary> allSummaries)
    {
        var todayStr = referenceDate.ToString("yyyy-MM-dd");
        var yesterdayStr = referenceDate.AddDays(-1).ToString("yyyy-MM-dd");

        // 1. Group summaries by date and calculate totals
        var dateTotals = allSummaries
            .GroupBy(s => s.Date)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.DurationMinutes));

        var todayMinutes = dateTotals.GetValueOrDefault(todayStr, 0);
        var yesterdayMinutes = dateTotals.GetValueOrDefault(yesterdayStr, 0);

        double todayDelta = 0.0;
        if (yesterdayMinutes > 0)
        {
            todayDelta = Math.Round(((double)(todayMinutes - yesterdayMinutes) / yesterdayMinutes) * 100.0, 1);
        }
        else if (todayMinutes > 0)
        {
            todayDelta = 100.0;
        }

        var todayDeltaText = todayDelta >= 0 ? $"{todayDelta}% 较昨日" : $"{Math.Abs(todayDelta)}% 较昨日";

        // 2. Week calculation (Monday as start of week)
        int diff = (7 + (referenceDate.DayOfWeek - DayOfWeek.Monday)) % 7;
        var startOfThisWeek = referenceDate.Date.AddDays(-diff);
        var endOfThisWeek = startOfThisWeek.AddDays(6);

        var startOfLastWeek = startOfThisWeek.AddDays(-7);
        var endOfLastWeek = startOfThisWeek.AddDays(-1);

        int thisWeekMinutes = 0;
        for (var d = startOfThisWeek; d <= endOfThisWeek; d = d.AddDays(1))
        {
            thisWeekMinutes += dateTotals.GetValueOrDefault(d.ToString("yyyy-MM-dd"), 0);
        }

        int lastWeekMinutes = 0;
        for (var d = startOfLastWeek; d <= endOfLastWeek; d = d.AddDays(1))
        {
            lastWeekMinutes += dateTotals.GetValueOrDefault(d.ToString("yyyy-MM-dd"), 0);
        }

        double weekDelta = 0.0;
        if (lastWeekMinutes > 0)
        {
            weekDelta = Math.Round(((double)(thisWeekMinutes - lastWeekMinutes) / lastWeekMinutes) * 100.0, 1);
        }
        else if (thisWeekMinutes > 0)
        {
            weekDelta = 100.0;
        }

        var weekDeltaText = weekDelta >= 0 ? $"{weekDelta}% 较上周" : $"{Math.Abs(weekDelta)}% 较上周";

        // 3. Consecutive Streak Calculation
        int streak = 0;
        var checkDate = referenceDate.Date;
        // If today has games, start counting from today; otherwise if yesterday has games, start from yesterday
        if (dateTotals.GetValueOrDefault(checkDate.ToString("yyyy-MM-dd"), 0) > 0)
        {
            while (dateTotals.GetValueOrDefault(checkDate.ToString("yyyy-MM-dd"), 0) > 0)
            {
                streak++;
                checkDate = checkDate.AddDays(-1);
            }
        }
        else
        {
            checkDate = checkDate.AddDays(-1);
            while (dateTotals.GetValueOrDefault(checkDate.ToString("yyyy-MM-dd"), 0) > 0)
            {
                streak++;
                checkDate = checkDate.AddDays(-1);
            }
        }

        // 4. Heatmap Month Grid (Full weeks containing the current month)
        var firstDayOfMonth = new DateTime(referenceDate.Year, referenceDate.Month, 1);
        var lastDayOfMonth = firstDayOfMonth.AddMonths(1).AddDays(-1);

        // Find Monday on or before firstDayOfMonth
        int firstDiff = (7 + (firstDayOfMonth.DayOfWeek - DayOfWeek.Monday)) % 7;
        var gridStart = firstDayOfMonth.AddDays(-firstDiff);

        // Find Sunday on or after lastDayOfMonth
        int lastDiff = (7 + (DayOfWeek.Sunday - lastDayOfMonth.DayOfWeek)) % 7;
        var gridEnd = lastDayOfMonth.AddDays(lastDiff);

        var dateGameCounts = allSummaries
            .GroupBy(s => s.Date)
            .ToDictionary(g => g.Key, g => g.Select(s => s.GameId).Distinct().Count());

        var heatmapDays = new List<HeatmapDayModel>();
        for (var d = gridStart; d <= gridEnd; d = d.AddDays(1))
        {
            var dStr = d.ToString("yyyy-MM-dd");
            var mins = dateTotals.GetValueOrDefault(dStr, 0);
            var count = dateGameCounts.GetValueOrDefault(dStr, 0);
            heatmapDays.Add(new HeatmapDayModel(
                Date: d,
                DurationMinutes: mins,
                GameCount: count,
                Level: CalculateHeatmapLevel(mins, 360),
                IsCurrentMonth: d.Month == referenceDate.Month,
                IsToday: d.Date == referenceDate.Date
            ));
        }

        // 5. Today's Top Games
        var todayGames = allSummaries
            .Where(s => s.Date == todayStr)
            .OrderByDescending(s => s.DurationMinutes)
            .ToList();

        var topGamesList = todayGames.Select(g => new GamePlayProgress(
            GameId: g.GameId,
            GameName: string.IsNullOrWhiteSpace(g.GameName) ? $"Game #{g.GameId}" : g.GameName,
            Platform: g.Platform,
            PlatformId: g.PlatformId,
            Minutes: g.DurationMinutes,
            DurationText: FormatDuration(g.DurationMinutes),
            // 进度条 = 该游戏时长占当日总时长的比例（此前是相对最长游戏，单游戏恒 100%、多游戏第二名恒低）
            Percentage: todayMinutes > 0 ? Math.Min(1.0, (double)g.DurationMinutes / todayMinutes) : 0.0
        )).ToList();

        // 6. Generate 52-week (1-year) Activity Heatmap (7x52 = 364 days)
        var activityHeatmap = GenerateActivityHeatmap(referenceDate, allSummaries, HeatmapWeekCount);

        return new DashboardStats(
            TodayMinutes: todayMinutes,
            TodayDurationText: FormatDuration(todayMinutes),
            TodayDeltaPercent: todayDelta,
            TodayDeltaText: todayDeltaText,
            WeekMinutes: thisWeekMinutes,
            WeekDurationText: FormatDuration(thisWeekMinutes),
            WeekDeltaPercent: weekDelta,
            WeekDeltaText: weekDeltaText,
            StreakDays: streak,
            HeatmapDays: heatmapDays,
            TodayTopGames: topGamesList,
            ActivityHeatmap: activityHeatmap
        );
    }
}
