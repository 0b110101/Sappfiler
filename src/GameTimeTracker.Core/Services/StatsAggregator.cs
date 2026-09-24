using System.Text.Json;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.Services;

public enum StatsPeriodMode
{
    Month,
    Quarter,
    Year
}

public record PeriodRange(
    DateTime StartDate,
    DateTime EndDate,
    DateTime PrevStartDate,
    DateTime PrevEndDate,
    string DisplayTitle,
    string PeriodComparisonLabel, // "较上月" / "较上季度" / "较上年"
    StatsPeriodMode Mode = StatsPeriodMode.Month
);

public record GameStatItem(
    int GameId,
    string Name,
    string Platform,
    string PlatformId,
    int TotalMinutes,
    string DurationText,
    double Percentage,       // 0.0 ~ 1.0 (relative to total minutes)
    string PercentageText,   // e.g. "25.5%"
    double RatioToMax,       // 0.0 ~ 1.0 (relative to #1 game)
    int Rank,
    string ColorHex,
    string? CoverPath = null
);

public record GenreDistributionItem(
    string Name,
    int GameCount,
    int TotalMinutes,
    string DurationText,
    double Percentage,       // 0.0 ~ 1.0
    string PercentageText,   // e.g. "32%"
    string CountText,        // e.g. "(9)"
    double Ratio,            // 0.0 ~ 1.0 (relative to max genre)
    string ColorHex,
    string SubTagsText = ""
);

public record DailyTrendPoint(
    DateTime Date,
    string DateLabel,        // "9/1", "9/15" or "1月", "2月"
    string FullDateTitle,    // "9月17日"
    int DurationMinutes,
    string DurationText,
    int SessionCount,
    int GameCount,
    string TooltipText,
    bool IsToday = false,
    double X = 0,
    double YDuration = 0,
    double YSessions = 0
);

public record DonutSlice(
    string Name,
    double Percentage,       // 0.0 ~ 1.0
    string PercentageText,
    string DurationText,
    string ColorHex,
    double StartAngle,       // in degrees
    double SweepAngle,       // in degrees
    string? CoverPath = null
);

public record StatsOverviewResult(
    PeriodRange Range,
    int TotalMinutes,
    string TotalDurationText,
    double TotalDeltaPercent,
    string TotalDeltaText,
    string TotalDeltaPercentText,
    int TotalSessions,
    double SessionsDeltaPercent,
    string SessionsDeltaText,
    string SessionsDeltaPercentText,
    int DailyAvgMinutes,
    string DailyAvgDurationText,
    double DailyAvgDeltaPercent,
    string DailyAvgDeltaText,
    string DailyAvgDeltaPercentText,
    string PeriodComparisonLabel,
    GameStatItem? TopGame,
    IReadOnlyList<GameStatItem> GameRankings,
    IReadOnlyList<DonutSlice> DonutSlices,
    IReadOnlyList<DailyTrendPoint> TrendPoints,
    IReadOnlyList<GenreDistributionItem> GenreStats,
    int MaxDailyMinutes,
    int MaxDailySessions,
    GameExplorationResult? Exploration = null,
    IReadOnlyList<DonutSlice>? ExplorationDonutSlices = null,
    PlaytimeTierResult? PlaytimeTiers = null,
    IReadOnlyList<DonutSlice>? PlaytimeTierDonutSlices = null,
    IReadOnlyList<GameActivityItem>? GameActivities = null
);

public static class StatsAggregator
{
    private static readonly string[] Palette =
    {
        "#4F8FFF", "#36B37E", "#FF9F43", "#A855F7", "#EF4444",
        "#06B6D4", "#EC4899", "#8B5CF6", "#10B981", "#64748B"
    };

    public static PeriodRange ComputePeriodRange(DateTime targetDate, StatsPeriodMode mode)
    {
        var date = targetDate.Date;
        switch (mode)
        {
            case StatsPeriodMode.Month:
            {
                var start = new DateTime(date.Year, date.Month, 1);
                var end = start.AddMonths(1).AddDays(-1);
                var prevStart = start.AddMonths(-1);
                var prevEnd = start.AddDays(-1);
                return new PeriodRange(
                    start, end, prevStart, prevEnd,
                    $"{date.Year}年{date.Month}月",
                    "较上月",
                    StatsPeriodMode.Month
                );
            }
            case StatsPeriodMode.Quarter:
            {
                int q = (date.Month - 1) / 3 + 1;
                var start = new DateTime(date.Year, (q - 1) * 3 + 1, 1);
                var end = start.AddMonths(3).AddDays(-1);
                var prevStart = start.AddMonths(-3);
                var prevEnd = start.AddDays(-1);
                return new PeriodRange(
                    start, end, prevStart, prevEnd,
                    $"{date.Year}年第{q}季度",
                    "较上季度",
                    StatsPeriodMode.Quarter
                );
            }
            case StatsPeriodMode.Year:
            default:
            {
                var start = new DateTime(date.Year, 1, 1);
                var end = new DateTime(date.Year, 12, 31);
                var prevStart = new DateTime(date.Year - 1, 1, 1);
                var prevEnd = new DateTime(date.Year - 1, 12, 31);
                return new PeriodRange(
                    start, end, prevStart, prevEnd,
                    $"{date.Year}年",
                    "较上年",
                    StatsPeriodMode.Year
                );
            }
        }
    }

    public const string TierGrey = "#94A3B8";   // 0-2h (最少)
    public const string TierGreen = "#10B981";  // 2-10h
    public const string TierBlue = "#3B82F6";   // 10-50h
    public const string TierYellow = "#F59E0B"; // 50-100h
    public const string TierPurple = "#8B5CF6"; // 100-500h
    public const string TierOrange = "#F97316"; // 500h+ (最多)

    /// <summary>
    /// 进度条阶梯色：从最多到最少（1st 橙 → 2nd 紫 → 3rd 黄 → 4th 蓝 → 5th 绿 → 6th 灰）。
    /// </summary>
    public static readonly string[] ProgressBarRankColors =
    {
        TierOrange, // #F97316 (最多)
        TierPurple, // #8B5CF6
        TierYellow, // #F59E0B
        TierBlue,   // #3B82F6
        TierGreen,  // #10B981
        TierGrey    // #94A3B8 (最少)
    };

    public static StatsOverviewResult AggregatePeriod(
        PeriodRange range,
        IReadOnlyList<DailySummary> allSummaries,
        IReadOnlyDictionary<string, List<string>>? gameGenresMap = null,
        IReadOnlyDictionary<int, string?>? gameCoverMap = null,
        IReadOnlyDictionary<int, string>? earliestPlayDates = null)
    {
        string startStr = range.StartDate.ToString("yyyy-MM-dd");
        string endStr = range.EndDate.ToString("yyyy-MM-dd");
        string prevStartStr = range.PrevStartDate.ToString("yyyy-MM-dd");
        string prevEndStr = range.PrevEndDate.ToString("yyyy-MM-dd");

        var curSummaries = allSummaries
            .Where(s => string.CompareOrdinal(s.Date, startStr) >= 0 && string.CompareOrdinal(s.Date, endStr) <= 0)
            .ToList();

        var prevSummaries = allSummaries
            .Where(s => string.CompareOrdinal(s.Date, prevStartStr) >= 0 && string.CompareOrdinal(s.Date, prevEndStr) <= 0)
            .ToList();

        DateTime prevPrevStartDate = range.PeriodComparisonLabel switch
        {
            "较上年" => range.PrevStartDate.AddYears(-1),
            "较上季" or "较上季度" => range.PrevStartDate.AddMonths(-3),
            _ => range.PrevStartDate.AddMonths(-1)
        };
        DateTime prevPrevEndDate = range.PrevStartDate.AddDays(-1);
        string prevPrevStartStr = prevPrevStartDate.ToString("yyyy-MM-dd");
        string prevPrevEndStr = prevPrevEndDate.ToString("yyyy-MM-dd");

        var prevPrevSummaries = allSummaries
            .Where(s => string.CompareOrdinal(s.Date, prevPrevStartStr) >= 0 && string.CompareOrdinal(s.Date, prevPrevEndStr) <= 0)
            .ToList();

        // 1. Current period metrics
        int curTotalMinutes = curSummaries.Sum(s => s.DurationMinutes);
        int curTotalSessions = curSummaries.Sum(s => Math.Max(1, s.SessionCount));
        var curActiveDays = curSummaries.Select(s => s.Date).Distinct().Count();
        int curAvgMinutes = curActiveDays > 0 ? (int)Math.Round((double)curTotalMinutes / curActiveDays) : 0;

        // 2. Previous period metrics
        int prevTotalMinutes = prevSummaries.Sum(s => s.DurationMinutes);
        int prevTotalSessions = prevSummaries.Sum(s => Math.Max(1, s.SessionCount));
        var prevActiveDays = prevSummaries.Select(s => s.Date).Distinct().Count();
        int prevAvgMinutes = prevActiveDays > 0 ? (int)Math.Round((double)prevTotalMinutes / prevActiveDays) : 0;

        // 3. Deltas
        double totalDelta = ComputeDeltaPercent(curTotalMinutes, prevTotalMinutes);
        double sessionsDelta = ComputeDeltaPercent(curTotalSessions, prevTotalSessions);
        double avgDelta = ComputeDeltaPercent(curAvgMinutes, prevAvgMinutes);

        string totalDeltaText = FormatDeltaText(totalDelta, range.PeriodComparisonLabel);
        string sessionsDeltaText = FormatDeltaText(sessionsDelta, range.PeriodComparisonLabel);
        string avgDeltaText = FormatDeltaText(avgDelta, range.PeriodComparisonLabel);

        string totalDeltaPercentText = FormatDeltaPercentOnly(totalDelta);
        string sessionsDeltaPercentText = FormatDeltaPercentOnly(sessionsDelta);
        string avgDeltaPercentText = FormatDeltaPercentOnly(avgDelta);

        // 4. Game rankings
        var gamesGrouped = curSummaries
            .GroupBy(s => s.GameId)
            .Select(g =>
            {
                var first = g.First();
                int mins = g.Sum(x => x.DurationMinutes);
                string name = string.IsNullOrWhiteSpace(first.GameName) ? $"Game #{first.GameId}" : first.GameName;
                return new
                {
                    GameId = g.Key,
                    Name = name,
                    first.Platform,
                    first.PlatformId,
                    Minutes = mins
                };
            })
            .OrderByDescending(x => x.Minutes)
            .ToList();

        int maxGameMinutes = gamesGrouped.FirstOrDefault()?.Minutes ?? 0;
        var gameRankings = new List<GameStatItem>();
        for (int i = 0; i < gamesGrouped.Count; i++)
        {
            var g = gamesGrouped[i];
            double pct = curTotalMinutes > 0 ? (double)g.Minutes / curTotalMinutes : 0;
            double ratioToMax = maxGameMinutes > 0 ? (double)g.Minutes / maxGameMinutes : 0;
            string? cover = null;
            if (gameCoverMap != null && gameCoverMap.TryGetValue(g.GameId, out var cPath))
            {
                cover = cPath;
            }

            gameRankings.Add(new GameStatItem(
                GameId: g.GameId,
                Name: g.Name,
                Platform: g.Platform,
                PlatformId: g.PlatformId,
                TotalMinutes: g.Minutes,
                DurationText: DailyAggregator.FormatHoursMinutes(g.Minutes),
                Percentage: pct,
                PercentageText: $"{Math.Round(pct * 100.0, 1)}%",
                RatioToMax: ratioToMax,
                Rank: i + 1,
                ColorHex: i < ProgressBarRankColors.Length ? ProgressBarRankColors[i] : TierGrey,
                CoverPath: cover
            ));
        }

        var topGame = gameRankings.FirstOrDefault();

        // 5. Donut Slices (Top 5 games, no "其他游戏")
        var donutSlices = new List<DonutSlice>();
        if (curTotalMinutes > 0)
        {
            double currentAngle = 0;
            int topLimit = 5;
            var topForDonut = gameRankings.Take(topLimit).ToList();

            for (int i = 0; i < topForDonut.Count; i++)
            {
                var item = topForDonut[i];
                double sweep = item.Percentage * 360.0;
                donutSlices.Add(new DonutSlice(
                    Name: item.Name,
                    Percentage: item.Percentage,
                    PercentageText: item.PercentageText,
                    DurationText: item.DurationText,
                    ColorHex: item.ColorHex,
                    StartAngle: currentAngle,
                    SweepAngle: sweep,
                    CoverPath: item.CoverPath
                ));
                currentAngle += sweep;
            }
        }

        // 6. Trend points (Daily for Month, Weekly for Quarter, Monthly 12-pts for Year)
        var trendPoints = new List<DailyTrendPoint>();
        int maxDailyMins = 0;
        int maxDailySessions = 0;

        if (range.PeriodComparisonLabel == "较上年") // Year mode: exactly 12 monthly data points
        {
            int year = range.StartDate.Year;
            for (int m = 1; m <= 12; m++)
            {
                var mStart = new DateTime(year, m, 1);
                var mEnd = new DateTime(year, m, DateTime.DaysInMonth(year, m));
                string mStartStr = mStart.ToString("yyyy-MM-dd");
                string mEndStr = mEnd.ToString("yyyy-MM-dd");

                var mSummaries = curSummaries
                    .Where(s => string.CompareOrdinal(s.Date, mStartStr) >= 0 && string.CompareOrdinal(s.Date, mEndStr) <= 0)
                    .ToList();

                int mins = mSummaries.Sum(x => x.DurationMinutes);
                int sessions = mSummaries.Sum(x => Math.Max(1, x.SessionCount));
                int gamesCount = mSummaries.Select(x => x.GameId).Distinct().Count();

                if (mins > maxDailyMins) maxDailyMins = mins;
                if (sessions > maxDailySessions) maxDailySessions = sessions;

                string fullDateTitle = $"{year}年{m}月";
                string dateLabel = $"{m}月";
                bool isCurrent = (year == DateTime.Today.Year && m == DateTime.Today.Month);
                string tooltip = $"{fullDateTitle}\n总游戏时长 {DailyAggregator.FormatHoursMinutes(mins)}\n游戏 {gamesCount} 款";

                trendPoints.Add(new DailyTrendPoint(
                    Date: mStart,
                    DateLabel: dateLabel,
                    FullDateTitle: fullDateTitle,
                    DurationMinutes: mins,
                    DurationText: DailyAggregator.FormatHoursMinutes(mins),
                    SessionCount: sessions,
                    GameCount: gamesCount,
                    TooltipText: tooltip,
                    IsToday: isCurrent
                ));
            }
        }
        else if (range.PeriodComparisonLabel == "较上季度") // Quarter mode: weekly intervals ending on Sunday
        {
            var curStart = range.StartDate.Date;
            var qEnd = range.EndDate.Date;
            var weekRanges = new List<(DateTime Start, DateTime End)>();

            while (curStart <= qEnd)
            {
                int daysToSunday = ((int)DayOfWeek.Sunday - (int)curStart.DayOfWeek + 7) % 7;
                var curEnd = curStart.AddDays(daysToSunday);
                if (curEnd > qEnd) curEnd = qEnd;
                weekRanges.Add((curStart, curEnd));
                curStart = curEnd.AddDays(1);
            }

            for (int i = 0; i < weekRanges.Count; i++)
            {
                var (wStart, wEnd) = weekRanges[i];
                string wStartStr = wStart.ToString("yyyy-MM-dd");
                string wEndStr = wEnd.ToString("yyyy-MM-dd");

                var wSummaries = curSummaries
                    .Where(s => string.CompareOrdinal(s.Date, wStartStr) >= 0 && string.CompareOrdinal(s.Date, wEndStr) <= 0)
                    .ToList();

                int mins = wSummaries.Sum(x => x.DurationMinutes);
                int sessions = wSummaries.Sum(x => Math.Max(1, x.SessionCount));
                int gamesCount = wSummaries.Select(x => x.GameId).Distinct().Count();

                if (mins > maxDailyMins) maxDailyMins = mins;
                if (sessions > maxDailySessions) maxDailySessions = sessions;

                string fullDateTitle = $"{wStart.Month}月{wStart.Day}日–{wEnd.Month}月{wEnd.Day}日";
                string dateLabel = (i % 2 == 0 || i == weekRanges.Count - 1)
                    ? $"{wStart.Month}/{wStart.Day}"
                    : string.Empty;

                bool isCurrent = (DateTime.Today >= wStart && DateTime.Today <= wEnd);
                string tooltip = $"{fullDateTitle}\n总游戏时长 {DailyAggregator.FormatHoursMinutes(mins)}\n游戏 {gamesCount} 款";

                trendPoints.Add(new DailyTrendPoint(
                    Date: wStart,
                    DateLabel: dateLabel,
                    FullDateTitle: fullDateTitle,
                    DurationMinutes: mins,
                    DurationText: DailyAggregator.FormatHoursMinutes(mins),
                    SessionCount: sessions,
                    GameCount: gamesCount,
                    TooltipText: tooltip,
                    IsToday: isCurrent
                ));
            }
        }
        else // Month mode: daily granularity
        {
            var summariesByDate = curSummaries
                .GroupBy(s => s.Date)
                .ToDictionary(g => g.Key, g => g.ToList());

            for (var d = range.StartDate; d <= range.EndDate; d = d.AddDays(1))
            {
                string dStr = d.ToString("yyyy-MM-dd");
                int mins = 0;
                int sessions = 0;
                int gamesCount = 0;

                if (summariesByDate.TryGetValue(dStr, out var dayList))
                {
                    mins = dayList.Sum(x => x.DurationMinutes);
                    sessions = dayList.Sum(x => Math.Max(1, x.SessionCount));
                    gamesCount = dayList.Select(x => x.GameId).Distinct().Count();
                }

                if (mins > maxDailyMins) maxDailyMins = mins;
                if (sessions > maxDailySessions) maxDailySessions = sessions;

                string dateLabel = (d.Day == 1 || d.Day == 5 || d.Day == 10 || d.Day == 15 || d.Day == 20 || d.Day == 25 || d.Date == range.EndDate.Date)
                    ? $"{d.Month}/{d.Day}"
                    : string.Empty;

                string fullDateTitle = $"{d.Month}月{d.Day}日";
                string tooltip = $"{fullDateTitle}\n总游戏时长 {DailyAggregator.FormatHoursMinutes(mins)}\n游戏 {gamesCount} 款";

                trendPoints.Add(new DailyTrendPoint(
                    Date: d,
                    DateLabel: dateLabel,
                    FullDateTitle: fullDateTitle,
                    DurationMinutes: mins,
                    DurationText: DailyAggregator.FormatHoursMinutes(mins),
                    SessionCount: sessions,
                    GameCount: gamesCount,
                    TooltipText: tooltip,
                    IsToday: d.Date == DateTime.Today
                ));
            }
        }

        // 7. Genre Distribution (Vertical Bars)
        var genreStats = BuildGenreStats(curSummaries, gameGenresMap);

        // 8. Exploration Stats & Donut Slices
        var (exploration, explorationSlices) = BuildExploration(
            range, curSummaries, prevSummaries, prevPrevSummaries, allSummaries, earliestPlayDates, gameCoverMap);

        // 9. Playtime Tier Distribution & Donut Slices
        var (playtimeTiers, playtimeTierSlices) = BuildPlaytimeTiers(curSummaries, range.Mode);

        // 10. Game Activities (sorted by ActiveDays desc)
        var gameActivities = BuildGameActivities(curSummaries, gameCoverMap);

        return new StatsOverviewResult(
            Range: range,
            TotalMinutes: curTotalMinutes,
            TotalDurationText: DailyAggregator.FormatHoursMinutes(curTotalMinutes),
            TotalDeltaPercent: totalDelta,
            TotalDeltaText: totalDeltaText,
            TotalDeltaPercentText: totalDeltaPercentText,
            TotalSessions: curTotalSessions,
            SessionsDeltaPercent: sessionsDelta,
            SessionsDeltaText: sessionsDeltaText,
            SessionsDeltaPercentText: sessionsDeltaPercentText,
            DailyAvgMinutes: curAvgMinutes,
            DailyAvgDurationText: DailyAggregator.FormatHoursMinutes(curAvgMinutes),
            DailyAvgDeltaPercent: avgDelta,
            DailyAvgDeltaText: avgDeltaText,
            DailyAvgDeltaPercentText: avgDeltaPercentText,
            PeriodComparisonLabel: range.PeriodComparisonLabel,
            TopGame: topGame,
            GameRankings: gameRankings,
            DonutSlices: donutSlices,
            TrendPoints: trendPoints,
            GenreStats: genreStats,
            MaxDailyMinutes: maxDailyMins,
            MaxDailySessions: maxDailySessions,
            Exploration: exploration,
            ExplorationDonutSlices: explorationSlices,
            PlaytimeTiers: playtimeTiers,
            PlaytimeTierDonutSlices: playtimeTierSlices,
            GameActivities: gameActivities
        );
    }

    private static (GameExplorationResult Result, IReadOnlyList<DonutSlice> Slices) BuildExploration(
        PeriodRange range,
        IReadOnlyList<DailySummary> curSummaries,
        IReadOnlyList<DailySummary> prevSummaries,
        IReadOnlyList<DailySummary> prevPrevSummaries,
        IReadOnlyList<DailySummary> allSummaries,
        IReadOnlyDictionary<int, string>? earliestPlayDates,
        IReadOnlyDictionary<int, string?>? gameCoverMap)
    {
        string startStr = range.StartDate.ToString("yyyy-MM-dd");
        string prevStartStr = range.PrevStartDate.ToString("yyyy-MM-dd");
        string prevEndStr = range.PrevEndDate.ToString("yyyy-MM-dd");

        var curGames = curSummaries.GroupBy(s => s.GameId).ToDictionary(g => g.Key, g => new
        {
            Name = g.First().GameName,
            Minutes = g.Sum(x => x.DurationMinutes)
        });

        var prevGames = prevSummaries.GroupBy(s => s.GameId).ToDictionary(g => g.Key, g => new
        {
            Name = g.First().GameName,
            Minutes = g.Sum(x => x.DurationMinutes)
        });

        var prevPrevGames = prevPrevSummaries.GroupBy(s => s.GameId).ToDictionary(g => g.Key, g => new
        {
            Name = g.First().GameName,
            Minutes = g.Sum(x => x.DurationMinutes)
        });

        var earliestMap = earliestPlayDates != null
            ? new Dictionary<int, string>(earliestPlayDates)
            : allSummaries.GroupBy(s => s.GameId).ToDictionary(g => g.Key, g => g.Min(s => s.Date) ?? startStr);

        var newGames = new List<(int Id, string Name, int Minutes)>();
        var ongoingGames = new List<(int Id, string Name, int Minutes)>();
        var returningGames = new List<(int Id, string Name, int Minutes)>();
        var pausedGames = new List<(int Id, string Name, int Minutes)>();

        foreach (var (gid, g) in curGames)
        {
            if (g.Minutes <= 0) continue;

            string earliest = earliestMap.TryGetValue(gid, out var ed) ? ed : startStr;
            bool isFirstPlayedInCur = string.CompareOrdinal(earliest, startStr) >= 0;

            if (isFirstPlayedInCur)
            {
                newGames.Add((gid, g.Name, g.Minutes));
            }
            else if (prevGames.ContainsKey(gid) && prevGames[gid].Minutes > 0)
            {
                ongoingGames.Add((gid, g.Name, g.Minutes));
            }
            else
            {
                returningGames.Add((gid, g.Name, g.Minutes));
            }
        }

        foreach (var (gid, g) in prevGames)
        {
            if (g.Minutes <= 0) continue;
            if (!curGames.ContainsKey(gid) || curGames[gid].Minutes <= 0)
            {
                pausedGames.Add((gid, g.Name, g.Minutes));
            }
        }

        // Calculate previous period's category counts for delta calculation
        int prevNewCount = 0;
        int prevOngoingCount = 0;
        int prevReturningCount = 0;
        int prevPausedCount = 0;

        foreach (var (gid, g) in prevGames)
        {
            if (g.Minutes <= 0) continue;
            string earliest = earliestMap.TryGetValue(gid, out var ed) ? ed : prevStartStr;
            bool isFirstPlayedInPrev = string.CompareOrdinal(earliest, prevStartStr) >= 0;

            if (isFirstPlayedInPrev)
            {
                prevNewCount++;
            }
            else if (prevPrevGames.ContainsKey(gid) && prevPrevGames[gid].Minutes > 0)
            {
                prevOngoingCount++;
            }
            else
            {
                prevReturningCount++;
            }
        }

        foreach (var (gid, g) in prevPrevGames)
        {
            if (g.Minutes <= 0) continue;
            if (!prevGames.ContainsKey(gid) || prevGames[gid].Minutes <= 0)
            {
                prevPausedCount++;
            }
        }

        int deltaNew = newGames.Count - prevNewCount;
        int deltaOngoing = ongoingGames.Count - prevOngoingCount;
        int deltaReturning = returningGames.Count - prevReturningCount;
        int deltaPaused = pausedGames.Count - prevPausedCount;

        int totalExploration = newGames.Count + ongoingGames.Count + returningGames.Count + pausedGames.Count;

        string periodPrefix = range.PeriodComparisonLabel switch
        {
            "较上季" or "较上季度" => "本季",
            "较上年" => "今年",
            _ => "本月"
        };
        string prevPeriodPrefix = range.PeriodComparisonLabel switch
        {
            "较上季" or "较上季度" => "上季",
            "较上年" => "去年",
            _ => "上月"
        };

        var categories = new List<GameExplorationCategory>();

        void AddCategory(string key, string title, string desc, string color, string icon, List<(int Id, string Name, int Minutes)> items, int delta)
        {
            double pct = totalExploration > 0 ? (double)items.Count / totalExploration : 0;
            var covers = new List<string>();
            var names = new List<string>();

            // 按游玩时长从大到小排序，高时长游戏置顶
            var sortedItems = items.OrderByDescending(x => x.Minutes).ToList();

            foreach (var item in sortedItems)
            {
                names.Add(item.Name);
                if (gameCoverMap != null && gameCoverMap.TryGetValue(item.Id, out var c) && !string.IsNullOrEmpty(c))
                {
                    covers.Add(c);
                }
            }

            string deltaText = delta > 0 ? $"+{delta}" : (delta < 0 ? $"{delta}" : "");

            // 悬停提示：展示前 8 款游戏及精确时长，超出时优雅省略
            string tooltipText;
            if (sortedItems.Count == 0)
            {
                tooltipText = $"{title}：暂无游戏";
            }
            else
            {
                const int maxTooltipItems = 8;
                var lines = new List<string> { $"{title} ({sortedItems.Count}款)：" };
                int displayCount = Math.Min(sortedItems.Count, maxTooltipItems);
                for (int i = 0; i < displayCount; i++)
                {
                    var it = sortedItems[i];
                    string dur = DailyAggregator.FormatHoursMinutes(it.Minutes);
                    lines.Add($"{it.Name} · {dur}");
                }
                if (sortedItems.Count > maxTooltipItems)
                {
                    lines.Add($"… 等共 {sortedItems.Count} 款游戏");
                }
                tooltipText = string.Join("\n", lines);
            }

            categories.Add(new GameExplorationCategory(
                Key: key,
                Title: title,
                Description: desc,
                Count: items.Count,
                Percentage: pct,
                PercentageText: $"{Math.Round(pct * 100.0, 0)}%",
                ColorHex: color,
                IconType: icon,
                GameNames: names,
                CoverPaths: covers,
                Delta: delta,
                DeltaText: deltaText,
                TooltipText: tooltipText
            ));
        }

        AddCategory("new", "新游戏", $"{periodPrefix}首次游玩的游戏", "#3B82F6", "sparkle", newGames, deltaNew);
        AddCategory("ongoing", "持续游玩", $"{periodPrefix}内在继续游玩的游戏", "#10B981", "repeat", ongoingGames, deltaOngoing);
        AddCategory("returning", "回归游玩", "之前玩过，重新开始的游戏", "#8B5CF6", "history", returningGames, deltaReturning);
        AddCategory("paused", "暂停游玩", $"{prevPeriodPrefix}记录，{periodPrefix}未再游玩的游戏", "#F59E0B", "pause", pausedGames, deltaPaused);

        var slices = new List<DonutSlice>();
        double currentAngle = 0;
        foreach (var cat in categories)
        {
            if (cat.Count <= 0) continue;
            double sweep = cat.Percentage * 360.0;
            slices.Add(new DonutSlice(
                Name: cat.Title,
                Percentage: cat.Percentage,
                PercentageText: cat.PercentageText,
                DurationText: $"{cat.Count}款",
                ColorHex: cat.ColorHex,
                StartAngle: currentAngle,
                SweepAngle: sweep
            ));
            currentAngle += sweep;
        }

        return (new GameExplorationResult(totalExploration, categories), slices);
    }

    private static (PlaytimeTierResult Result, IReadOnlyList<DonutSlice> Slices) BuildPlaytimeTiers(
        IReadOnlyList<DailySummary> curSummaries,
        StatsPeriodMode mode = StatsPeriodMode.Month)
    {
        var gamesGrouped = curSummaries
            .GroupBy(s => s.GameId)
            .Select(g => new { GameId = g.Key, Minutes = g.Sum(x => x.DurationMinutes) })
            .Where(x => x.Minutes > 0)
            .ToList();

        int totalGames = gamesGrouped.Count;

        var tierDefs = mode switch
        {
            StatsPeriodMode.Quarter => new (string Name, int Min, int Max, string Color)[]
            {
                ("0-3h", 0, 180, TierGrey),
                ("3-10h", 180, 600, TierGreen),
                ("10-30h", 600, 1800, TierBlue),
                ("30-60h", 1800, 3600, TierYellow),
                ("60-100h", 3600, 6000, TierPurple),
                ("100h+", 6000, int.MaxValue, TierOrange)
            },
            StatsPeriodMode.Year => new (string Name, int Min, int Max, string Color)[]
            {
                ("0-10h", 0, 600, TierGrey),
                ("10-30h", 600, 1800, TierGreen),
                ("30-60h", 1800, 3600, TierBlue),
                ("60-120h", 3600, 7200, TierYellow),
                ("120-240h", 7200, 14400, TierPurple),
                ("240h+", 14400, int.MaxValue, TierOrange)
            },
            _ => new (string Name, int Min, int Max, string Color)[] // Month and default
            {
                ("0-2h", 0, 120, TierGrey),
                ("2-5h", 120, 300, TierGreen),
                ("5-15h", 300, 900, TierBlue),
                ("15-30h", 900, 1800, TierYellow),
                ("30-50h", 1800, 3000, TierPurple),
                ("50h+", 3000, int.MaxValue, TierOrange)
            }
        };

        var tierItems = new List<PlaytimeTierItem>();
        var slices = new List<DonutSlice>();
        double currentAngle = 0;

        foreach (var def in tierDefs)
        {
            int count = gamesGrouped.Count(g => g.Minutes >= def.Min && (def.Max == int.MaxValue ? true : g.Minutes < def.Max));
            double pct = totalGames > 0 ? (double)count / totalGames : 0;

            tierItems.Add(new PlaytimeTierItem(
                TierName: def.Name,
                MinMinutes: def.Min,
                MaxMinutes: def.Max,
                GameCount: count,
                Percentage: pct,
                PercentageText: $"{Math.Round(pct * 100.0, 0)}%",
                CountText: $"({count})",
                ColorHex: def.Color
            ));

            if (count > 0)
            {
                double sweep = pct * 360.0;
                slices.Add(new DonutSlice(
                    Name: def.Name,
                    Percentage: pct,
                    PercentageText: $"{Math.Round(pct * 100.0, 0)}%",
                    DurationText: $"{count}款",
                    ColorHex: def.Color,
                    StartAngle: currentAngle,
                    SweepAngle: sweep
                ));
                currentAngle += sweep;
            }
        }

        return (new PlaytimeTierResult(totalGames, tierItems), slices);
    }

    private static IReadOnlyList<GameActivityItem> BuildGameActivities(
        IReadOnlyList<DailySummary> curSummaries,
        IReadOnlyDictionary<int, string?>? gameCoverMap)
    {
        var grouped = curSummaries
            .GroupBy(s => s.GameId)
            .Select(g =>
            {
                var first = g.First();
                int activeDays = g.Select(x => x.Date).Distinct().Count();
                int totalMinutes = g.Sum(x => x.DurationMinutes);
                string lastDate = g.Max(x => x.Date) ?? "";
                string lastPlayedText = "最近";
                if (DateTime.TryParse(lastDate, out var dt))
                {
                    var diff = (DateTime.Today - dt.Date).Days;
                    if (diff <= 0) lastPlayedText += "今天";
                    else if (diff == 1) lastPlayedText += "昨天";
                    else lastPlayedText += $" {diff} 天前";
                }
                else
                {
                    lastPlayedText += lastDate;
                }

                string? cover = null;
                if (gameCoverMap != null && gameCoverMap.TryGetValue(g.Key, out var c))
                {
                    cover = c;
                }

                string name = string.IsNullOrWhiteSpace(first.GameName) ? $"Game #{first.GameId}" : first.GameName;
                return new
                {
                    GameId = g.Key,
                    Name = name,
                    first.Platform,
                    first.PlatformId,
                    ActiveDays = activeDays,
                    TotalMinutes = totalMinutes,
                    LastPlayedDate = lastDate,
                    LastPlayedText = lastPlayedText,
                    CoverPath = cover
                };
            })
            .Where(x => x.TotalMinutes > 0)
            .OrderByDescending(x => x.ActiveDays)
            .ThenByDescending(x => x.TotalMinutes)
            .ToList();

        int maxActiveDays = grouped.FirstOrDefault()?.ActiveDays ?? 1;

        return grouped.Select((x, idx) => new GameActivityItem(
            GameId: x.GameId,
            Name: x.Name,
            Platform: x.Platform,
            PlatformId: x.PlatformId,
            ActiveDays: x.ActiveDays,
            TotalMinutes: x.TotalMinutes,
            LastPlayedDate: x.LastPlayedDate,
            LastPlayedText: x.LastPlayedText,
            RatioToMax: maxActiveDays > 0 ? (double)x.ActiveDays / maxActiveDays : 0,
            CoverPath: x.CoverPath,
            ColorHex: idx < ProgressBarRankColors.Length ? ProgressBarRankColors[idx] : TierGrey
        )).ToList();
    }

    private static readonly string[] GenrePalette =
    {
        "#4F8FFF", // Blue
        "#06B6D4", // Cyan
        "#FF9F43", // Orange
        "#A855F7", // Purple
        "#8B5CF6", // Violet
    };
    private const string GenreOtherColorHex = "#94A3B8"; // Gray

    private static IReadOnlyList<GenreDistributionItem> BuildGenreStats(
        IReadOnlyList<DailySummary> curSummaries,
        IReadOnlyDictionary<string, List<string>>? gameGenresMap)
    {
        // 游戏名 -> 累计时长
        var gameMinutes = curSummaries
            .GroupBy(s => (s.GameName ?? "").Trim())
            .ToDictionary(g => g.Key, g => g.Sum(x => x.DurationMinutes));

        var genreMap = new Dictionary<string, (int GameCount, int TotalMinutes)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (gameName, mins) in gameMinutes)
        {
            if (string.IsNullOrWhiteSpace(gameName)) continue;

            List<string>? genres = null;
            if (gameGenresMap != null && gameGenresMap.TryGetValue(gameName, out var foundGenres) && foundGenres.Count > 0)
            {
                genres = foundGenres;
            }

            if (genres == null || genres.Count == 0)
            {
                genres = new List<string> { "其他" };
            }

            foreach (var g in genres)
            {
                var cleanGenre = g.Trim();
                if (string.IsNullOrWhiteSpace(cleanGenre)) continue;

                if (!genreMap.TryGetValue(cleanGenre, out var stat))
                {
                    stat = (0, 0);
                }
                genreMap[cleanGenre] = (stat.GameCount + 1, stat.TotalMinutes + mins);
            }
        }

        var nonOther = genreMap
            .Where(kv => !string.Equals(kv.Key, "其他", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value.GameCount)
            .ThenByDescending(kv => kv.Value.TotalMinutes)
            .ToList();

        genreMap.TryGetValue("其他", out var otherStat);
        int otherCount = otherStat.GameCount;
        int otherMins = otherStat.TotalMinutes;

        var topGenres = nonOther.Take(5).ToList();
        var extraGenres = nonOther.Skip(5).ToList();

        otherCount += extraGenres.Sum(x => x.Value.GameCount);
        otherMins += extraGenres.Sum(x => x.Value.TotalMinutes);

        // Collect all extra tag names for tooltip
        var extraTagNames = new List<string>();
        foreach (var eg in extraGenres)
        {
            extraTagNames.Add(eg.Key);
        }
        if (otherStat.GameCount > 0)
        {
            extraTagNames.Add("未分类");
        }
        string otherSubTags = extraTagNames.Count > 0
            ? string.Join("、", extraTagNames)
            : string.Empty;

        var finalGenres = new List<(string Name, int GameCount, int TotalMinutes, string ColorHex, string SubTagsText)>();
        for (int i = 0; i < topGenres.Count; i++)
        {
            var g = topGenres[i];
            finalGenres.Add((g.Key, g.Value.GameCount, g.Value.TotalMinutes, GenrePalette[i % GenrePalette.Length], string.Empty));
        }

        if (otherCount > 0)
        {
            finalGenres.Add(("其他", otherCount, otherMins, GenreOtherColorHex, otherSubTags));
        }

        if (finalGenres.Count == 0) return Array.Empty<GenreDistributionItem>();

        int totalCount = finalGenres.Sum(x => x.GameCount);
        int maxCount = finalGenres.Max(x => x.GameCount);

        var list = new List<GenreDistributionItem>();
        for (int i = 0; i < finalGenres.Count; i++)
        {
            var item = finalGenres[i];
            double pct = totalCount > 0 ? (double)item.GameCount / totalCount : 0;
            double ratio = maxCount > 0 ? (double)item.GameCount / maxCount : 0;

            list.Add(new GenreDistributionItem(
                Name: item.Name,
                GameCount: item.GameCount,
                TotalMinutes: item.TotalMinutes,
                DurationText: DailyAggregator.FormatHoursMinutes(item.TotalMinutes),
                Percentage: pct,
                PercentageText: $"{Math.Round(pct * 100.0, 0)}%",
                CountText: $"({item.GameCount})",
                Ratio: ratio,
                ColorHex: item.ColorHex,
                SubTagsText: item.SubTagsText
            ));
        }

        return list;
    }

    private static double ComputeDeltaPercent(int current, int previous)
    {
        if (previous > 0)
        {
            return Math.Round(((double)(current - previous) / previous) * 100.0, 1);
        }
        return current > 0 ? 100.0 : 0.0;
    }

    private static string FormatDeltaText(double deltaPercent, string comparisonLabel)
    {
        if (deltaPercent >= 0)
        {
            return $"{deltaPercent}% {comparisonLabel}";
        }
        return $"{Math.Abs(deltaPercent)}% {comparisonLabel}";
    }

    public static string FormatDeltaPercentOnly(double deltaPercent)
    {
        if (deltaPercent != 0) return $"{Math.Abs(deltaPercent)}%";
        return "0%";
    }
}
