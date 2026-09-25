using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Covers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace GameTimeTracker.App.ViewModels;

public partial class GenreStatViewModel : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Percentage { get; set; } = string.Empty;
    public string CountText { get; set; } = string.Empty;
    public double Ratio { get; set; }
    public double BarHeight { get; set; }
    public string DurationText { get; set; } = string.Empty;
    public int GameCount { get; set; }
    public string ColorHex { get; set; } = "#4F8FFF";
    public SolidColorBrush ColorBrush { get; set; } = new(Colors.DodgerBlue);
    public string PathData { get; set; } = string.Empty;
    public string SubTagsText { get; set; } = string.Empty;
    public string TooltipText
    {
        get
        {
            if (Name == "其他")
            {
                if (!string.IsNullOrWhiteSpace(SubTagsText))
                {
                    return $"其他 ({GameCount} 个游戏 · {DurationText})\n包含所有其他标签：\n{SubTagsText}";
                }
                return $"其他 ({GameCount} 个游戏 · {DurationText})";
            }
            return $"{Name}: {GameCount} 个游戏 · {Percentage} ({DurationText})";
        }
    }
}

public partial class TrendPointItemViewModel : ObservableObject
{
    public DateTime Date { get; set; }
    public string DateLabel { get; set; } = string.Empty;
    public bool HasDateLabel => !string.IsNullOrEmpty(DateLabel);
    public string FullDateTitle { get; set; } = string.Empty;
    public string ValueText { get; set; } = string.Empty;
    public int GameCount { get; set; }
    public string GameCountText { get; set; } = string.Empty;
    public bool HasGames => GameCount > 0;
    public bool IsToday { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double TranslateX => X - 12;
    public double TranslateY => Y - 12;
    public double LabelCanvasLeft => X - 18;
    public double LabelTranslateX => X - 18;
}

public partial class DonutSliceViewModel : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string PercentageText { get; set; } = string.Empty;
    public string DurationText { get; set; } = string.Empty;
    public SolidColorBrush ColorBrush { get; set; } = new(Colors.DodgerBlue);
    public string ColorHex { get; set; } = "#4F8FFF";
    public string? CoverPath { get; set; }
    public string PathData { get; set; } = string.Empty;
}

public class ExplorationAvatarItem
{
    public string? CoverPath { get; set; }
    public Thickness Margin { get; set; } = new(0);
}

public partial class ExplorationCategoryViewModel : ObservableObject
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Count { get; set; }
    public string CountText { get; set; } = "0 款";
    public string PercentageText { get; set; } = "0%";
    public string ColorHex { get; set; } = "#3B82F6";
    public SolidColorBrush ColorBrush { get; set; } = new(Colors.DodgerBlue);
    public string IconGlyph { get; set; } = "\uE735";
    public List<ExplorationAvatarItem> Avatars { get; set; } = new();
    public int Delta { get; set; }
    public string DeltaText { get; set; } = string.Empty;
    public bool HasDelta => Delta != 0;
    public bool HasOverflow { get; set; }
    public string OverflowText { get; set; } = string.Empty;
    public bool HasAnyCovers => Avatars.Count > 0;
    public string TooltipText { get; set; } = string.Empty;
}

public partial class PlaytimeTierViewModel : ObservableObject
{
    public string TierName { get; set; } = string.Empty;
    public int GameCount { get; set; }
    public string CountText { get; set; } = "(0)";
    public string Percentage { get; set; } = "0%";
    public string ColorHex { get; set; } = "#94A3B8";
    public SolidColorBrush ColorBrush { get; set; } = new(Colors.Gray);
    public string PathData { get; set; } = string.Empty;
    public string TooltipText => $"{TierName}: {GameCount}款游戏 ({Percentage})";
}

public partial class StatsViewModel : ObservableObject
{
    private readonly IDatabaseRepository _repo;
    private readonly CoverCacheService _coverCache;
    private readonly TrackerConfig _config;

    public StatsViewModel(IDatabaseRepository repo, CoverCacheService coverCache, TrackerConfig config)
    {
        _repo = repo;
        _coverCache = coverCache;
        _config = config;
    }

    [ObservableProperty] public partial StatsPeriodMode PeriodMode { get; set; } = StatsPeriodMode.Month;
    [ObservableProperty] public partial DateTime CurrentPeriodDate { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string PeriodTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string PeriodRecordCardTitle { get; set; } = "本月游戏记录";

    // Period tab selection helpers
    [ObservableProperty] public partial bool IsMonthSelected { get; set; } = true;
    [ObservableProperty] public partial bool IsQuarterSelected { get; set; } = false;
    [ObservableProperty] public partial bool IsYearSelected { get; set; } = false;

    // Trend chart mode
    [ObservableProperty] public partial bool IsTrendDuration { get; set; } = true;
    [ObservableProperty] public partial bool IsTrendSessions { get; set; } = false;
    [ObservableProperty] public partial string TrendMaxText { get; set; } = "10h";
    [ObservableProperty] public partial string TrendMidText { get; set; } = "5h";
    [ObservableProperty] public partial string TrendBaseText { get; set; } = "0h";
    [ObservableProperty] public partial string TrendStartDate { get; set; } = string.Empty;
    [ObservableProperty] public partial string TrendMidDate { get; set; } = string.Empty;
    [ObservableProperty] public partial string TrendEndDate { get; set; } = string.Empty;
    [ObservableProperty] public partial string TrendLinePathData { get; set; } = string.Empty;
    [ObservableProperty] public partial string TrendAreaPathData { get; set; } = string.Empty;
    [ObservableProperty] public partial string TrendDotsPathData { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasTodayPoint { get; set; } = false;
    [ObservableProperty] public partial double TodayDotX { get; set; } = 0;
    [ObservableProperty] public partial double TodayDotY { get; set; } = 0;
    [ObservableProperty] public partial double ChartBaselineY { get; set; } = 120;

    public const string TablerArrowUpPath = "M 12 4 l 0 16 M 16 8 l -4 -4 l -4 4";
    public const string TablerArrowDownPath = "M 12 20 l 0 -16 M 16 16 l -4 4 l -4 -4";
    public const string TablerDashPath = "M 6 12 l 12 0";

    // Overview Metric Cards
    [ObservableProperty] public partial string TotalDurationText { get; set; } = "0h";
    [ObservableProperty] public partial string TotalDeltaText { get; set; } = "0% 较上月";
    [ObservableProperty] public partial string TotalDeltaPercentText { get; set; } = "0%";
    [ObservableProperty] public partial string TotalDeltaIconData { get; set; } = TablerDashPath;
    [ObservableProperty] public partial Brush TotalDeltaBrush { get; set; } = new SolidColorBrush(Colors.Gray);

    [ObservableProperty] public partial string TotalSessionsText { get; set; } = "0 次";
    [ObservableProperty] public partial string SessionsDeltaText { get; set; } = "0% 较上月";
    [ObservableProperty] public partial string SessionsDeltaPercentText { get; set; } = "0%";
    [ObservableProperty] public partial string SessionsDeltaIconData { get; set; } = TablerDashPath;
    [ObservableProperty] public partial Brush SessionsDeltaBrush { get; set; } = new SolidColorBrush(Colors.Gray);

    [ObservableProperty] public partial string DailyAvgDurationText { get; set; } = "0h";
    [ObservableProperty] public partial string DailyAvgDeltaText { get; set; } = "0% 较上月";
    [ObservableProperty] public partial string DailyAvgDeltaPercentText { get; set; } = "0%";
    [ObservableProperty] public partial string DailyAvgDeltaIconData { get; set; } = TablerDashPath;
    [ObservableProperty] public partial Brush DailyAvgDeltaBrush { get; set; } = new SolidColorBrush(Colors.Gray);

    [ObservableProperty] public partial string PeriodComparisonLabel { get; set; } = "较上月";

    // Top Game
    [ObservableProperty] public partial bool HasTopGame { get; set; } = false;
    [ObservableProperty] public partial string TopGameName { get; set; } = "暂无游戏";
    [ObservableProperty] public partial string TopGameDurationText { get; set; } = "0h";
    [ObservableProperty] public partial string TopGamePercentageText { get; set; } = "0%";
    [ObservableProperty] public partial string? TopGameCoverPath { get; set; }

    // Donut Center & Track
    [ObservableProperty] public partial string DonutCenterDurationText { get; set; } = "0h";
    [ObservableProperty] public partial string DonutTrackPath { get; set; } = BuildFullDonut(80, 80, 72, 48);
    [ObservableProperty] public partial int GenreTotalGamesCount { get; set; } = 0;

    // 【游戏探索】 (Game Exploration)
    [ObservableProperty] public partial int ExplorationTotalGamesCount { get; set; } = 0;
    [ObservableProperty] public partial string ExplorationSubtitle { get; set; } = "本月你在游戏库中的探索与尝试情况";
    [ObservableProperty] public partial string ExplorationTrackPath { get; set; } = BuildFullDonut(75, 75, 66, 45);

    // 【游戏时长分布】 (Playtime Tier Distribution)
    [ObservableProperty] public partial int TierTotalGamesCount { get; set; } = 0;
    [ObservableProperty] public partial string TierTrackPath { get; set; } = BuildFullDonut(75, 75, 66, 45);

    // 【游戏活跃度】
    [ObservableProperty] public partial string GameActivityCardTitle { get; set; } = "游戏活跃度";

    // Streak & Quote (Bottom Banner)
    [ObservableProperty] public partial int StreakDays { get; set; } = 0;
    [ObservableProperty] public partial string StreakTitleText { get; set; } = "保持热爱，继续前进！";
    [ObservableProperty] public partial string QuoteText { get; set; } = "“ 游戏不是逃避现实，而是让现实更值得期待。 ”";

    // Collections
    public ObservableCollection<GameStatItem> GameRankings { get; } = new();
    public ObservableCollection<GameStatItem> PeriodGameRecords { get; } = new();
    public ObservableCollection<GenreStatViewModel> GenreStats { get; } = new();
    public ObservableCollection<DonutSliceViewModel> DonutSlices { get; } = new();
    public ObservableCollection<GameStatItem> DonutLegend { get; } = new();
    public ObservableCollection<DailyTrendPoint> TrendPoints { get; } = new();
    public ObservableCollection<TrendPointItemViewModel> TrendPointItems { get; } = new();

    // New Collections
    public ObservableCollection<DonutSliceViewModel> ExplorationDonutSlices { get; } = new();
    public ObservableCollection<ExplorationCategoryViewModel> ExplorationCategories { get; } = new();
    public ObservableCollection<DonutSliceViewModel> PlaytimeTierDonutSlices { get; } = new();
    public ObservableCollection<PlaytimeTierViewModel> PlaytimeTiers { get; } = new();
    public ObservableCollection<PlaytimeTierViewModel> LeftPlaytimeTiers { get; } = new();
    public ObservableCollection<PlaytimeTierViewModel> RightPlaytimeTiers { get; } = new();
    public ObservableCollection<GameActivityItem> GameActivities { get; } = new();

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private StatsOverviewResult? _lastResult;
    private double _chartActualWidth = 0;
    private double _chartActualHeight = 0;

    public void UpdateChartSize(double width, double height)
    {
        if (width <= 0) return;
        if (Math.Abs(_chartActualWidth - width) < 1.0 && Math.Abs(_chartActualHeight - height) < 1.0)
        {
            return;
        }
        _chartActualWidth = width;
        _chartActualHeight = height;
        if (_lastResult != null)
        {
            RenderTrendChart(_lastResult.TrendPoints, _lastResult.MaxDailyMinutes, _lastResult.MaxDailySessions);
        }
    }

    [RelayCommand]
    public async Task SetMonthModeAsync()
    {
        PeriodMode = StatsPeriodMode.Month;
        IsMonthSelected = true;
        IsQuarterSelected = false;
        IsYearSelected = false;
        await RefreshDataAsync();
    }

    [RelayCommand]
    public async Task SetQuarterModeAsync()
    {
        PeriodMode = StatsPeriodMode.Quarter;
        IsMonthSelected = false;
        IsQuarterSelected = true;
        IsYearSelected = false;
        await RefreshDataAsync();
    }

    [RelayCommand]
    public async Task SetYearModeAsync()
    {
        PeriodMode = StatsPeriodMode.Year;
        IsMonthSelected = false;
        IsQuarterSelected = false;
        IsYearSelected = true;
        await RefreshDataAsync();
    }

    [RelayCommand]
    public async Task ResetToCurrentMonthAsync()
    {
        PeriodMode = StatsPeriodMode.Month;
        IsMonthSelected = true;
        IsQuarterSelected = false;
        IsYearSelected = false;
        CurrentPeriodDate = DateTime.Today;
        await RefreshDataAsync();
    }

    [RelayCommand]
    public async Task ResetToCurrentQuarterAsync()
    {
        PeriodMode = StatsPeriodMode.Quarter;
        IsMonthSelected = false;
        IsQuarterSelected = true;
        IsYearSelected = false;
        CurrentPeriodDate = DateTime.Today;
        await RefreshDataAsync();
    }

    [RelayCommand]
    public async Task ResetToCurrentYearAsync()
    {
        PeriodMode = StatsPeriodMode.Year;
        IsMonthSelected = false;
        IsQuarterSelected = false;
        IsYearSelected = true;
        CurrentPeriodDate = DateTime.Today;
        await RefreshDataAsync();
    }

    [RelayCommand]
    public async Task ResetToCurrentPeriodAsync()
    {
        CurrentPeriodDate = DateTime.Today;
        await RefreshDataAsync();
    }

    [RelayCommand]
    public async Task PrevPeriodAsync()
    {
        CurrentPeriodDate = PeriodMode switch
        {
            StatsPeriodMode.Month => CurrentPeriodDate.AddMonths(-1),
            StatsPeriodMode.Quarter => CurrentPeriodDate.AddMonths(-3),
            StatsPeriodMode.Year => CurrentPeriodDate.AddYears(-1),
            _ => CurrentPeriodDate.AddMonths(-1)
        };
        await RefreshDataAsync();
    }

    [RelayCommand]
    public async Task NextPeriodAsync()
    {
        CurrentPeriodDate = PeriodMode switch
        {
            StatsPeriodMode.Month => CurrentPeriodDate.AddMonths(1),
            StatsPeriodMode.Quarter => CurrentPeriodDate.AddMonths(3),
            StatsPeriodMode.Year => CurrentPeriodDate.AddYears(1),
            _ => CurrentPeriodDate.AddMonths(1)
        };
        await RefreshDataAsync();
    }

    [RelayCommand]
    public void SetTrendMode(string mode)
    {
        if (mode == "sessions")
        {
            IsTrendSessions = true;
            IsTrendDuration = false;
        }
        else
        {
            IsTrendDuration = true;
            IsTrendSessions = false;
        }

        if (_lastResult != null)
        {
            RenderTrendChart(_lastResult.TrendPoints, _lastResult.MaxDailyMinutes, _lastResult.MaxDailySessions);
        }
    }

    public async Task RefreshDataAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            var range = StatsAggregator.ComputePeriodRange(CurrentPeriodDate, PeriodMode);
            PeriodTitle = PeriodMode switch
            {
                StatsPeriodMode.Month => $"{CurrentPeriodDate.Year}年{CurrentPeriodDate.Month}月",
                StatsPeriodMode.Quarter => $"{CurrentPeriodDate.Year} Q{(CurrentPeriodDate.Month - 1) / 3 + 1}",
                StatsPeriodMode.Year => $"{CurrentPeriodDate.Year}年",
                _ => $"{CurrentPeriodDate.Year}年{CurrentPeriodDate.Month}月"
            };
            PeriodRecordCardTitle = PeriodMode switch
            {
                StatsPeriodMode.Month => "本月游戏记录",
                StatsPeriodMode.Quarter => "本季游戏记录",
                StatsPeriodMode.Year => "本年游戏记录",
                _ => "本期游戏记录"
            };
            ExplorationSubtitle = PeriodMode switch
            {
                StatsPeriodMode.Quarter => "本季你在游戏库中的探索与尝试情况",
                StatsPeriodMode.Year => "本年你在游戏库中的探索与尝试情况",
                _ => "本月你在游戏库中的探索与尝试情况"
            };

        // 1. Fetch raw summaries for previous-previous + previous + current period
        var prevPrevStartDate = PeriodMode switch
        {
            StatsPeriodMode.Year => range.PrevStartDate.AddYears(-1),
            StatsPeriodMode.Quarter => range.PrevStartDate.AddMonths(-3),
            _ => range.PrevStartDate.AddMonths(-1)
        };
        var minDateStr = prevPrevStartDate.ToString("yyyy-MM-dd");
        var maxDateStr = range.EndDate.ToString("yyyy-MM-dd");
        var allSummaries = await _repo.GetDailySummariesRangeAsync(minDateStr, maxDateStr);
        var allGames = await _repo.GetAllGamesAsync();
        var genresMap = await _repo.GetGameGenresMapAsync();
        var earliestPlayDates = await _repo.GetEarliestPlayDatesAsync();

        // 2. Build game cover map
        var coverMap = new Dictionary<int, string?>();
        foreach (var g in allGames)
        {
            var cover = _coverCache.GetCoverPath(g.Platform, g.PlatformId);
            if (File.Exists(cover) && new FileInfo(cover).Length > 0)
            {
                coverMap[g.Id] = cover;
            }
        }

        // 3. Aggregate
        var result = StatsAggregator.AggregatePeriod(range, allSummaries, genresMap, coverMap, earliestPlayDates);
        _lastResult = result;

        // 4. Update Overview Cards
        TotalDurationText = result.TotalDurationText;
        TotalDeltaText = result.TotalDeltaText;
        TotalDeltaPercentText = result.TotalDeltaPercentText;
        TotalDeltaBrush = GetDeltaBrush(result.TotalDeltaPercent);
        TotalDeltaIconData = GetDeltaIconData(result.TotalDeltaPercent);

        TotalSessionsText = $"{result.TotalSessions} 次";
        SessionsDeltaText = result.SessionsDeltaText;
        SessionsDeltaPercentText = result.SessionsDeltaPercentText;
        SessionsDeltaBrush = GetDeltaBrush(result.SessionsDeltaPercent);
        SessionsDeltaIconData = GetDeltaIconData(result.SessionsDeltaPercent);

        DailyAvgDurationText = result.DailyAvgDurationText;
        DailyAvgDeltaText = result.DailyAvgDeltaText;
        DailyAvgDeltaPercentText = result.DailyAvgDeltaPercentText;
        DailyAvgDeltaBrush = GetDeltaBrush(result.DailyAvgDeltaPercent);
        DailyAvgDeltaIconData = GetDeltaIconData(result.DailyAvgDeltaPercent);

        PeriodComparisonLabel = result.PeriodComparisonLabel;

        if (result.TopGame != null)
        {
            HasTopGame = true;
            TopGameName = result.TopGame.Name;
            TopGameDurationText = result.TopGame.DurationText;
            TopGamePercentageText = result.TopGame.PercentageText;
            TopGameCoverPath = result.TopGame.CoverPath;
        }
        else
        {
            HasTopGame = false;
            TopGameName = "暂无游玩数据";
            TopGameDurationText = "0h";
            TopGamePercentageText = "0%";
            TopGameCoverPath = null;
        }

        DonutCenterDurationText = result.TotalDurationText;

        // 5. Update Donut Chart & Legend
        DonutSlices.Clear();
        RenderDonutSlices(result.DonutSlices);

        // 6. Update Trend Chart
        TrendPoints.Clear();
        foreach (var tp in result.TrendPoints)
        {
            TrendPoints.Add(tp);
        }
        RenderTrendChart(result.TrendPoints, result.MaxDailyMinutes, result.MaxDailySessions);

        // 7. Update Rankings (Take top 6)
        GameRankings.Clear();
        foreach (var item in result.GameRankings.Take(6))
        {
            GameRankings.Add(item);
        }

        // 8. Update Genre Stats (Donut Chart matching design)
        GenreStats.Clear();
        int totalGenreGames = result.GenreStats.Sum(x => x.GameCount);
        GenreTotalGamesCount = totalGenreGames;

        double cx = 75.0, cy = 75.0;
        double R = 66.0, r = 45.0;
        double gap = result.GenreStats.Count > 1 ? 2.5 : 0;
        double currentAngle = 0;

        foreach (var g in result.GenreStats)
        {
            double sweep = g.Percentage * 360.0;
            string path;
            if (sweep >= 359.5 || result.GenreStats.Count == 1)
            {
                path = BuildFullDonut(cx, cy, R, r);
            }
            else
            {
                double effectiveSweep = Math.Max(0.5, sweep - gap);
                double effectiveStart = currentAngle + gap / 2.0;
                path = BuildDonutArc(cx, cy, R, r, effectiveStart, effectiveSweep);
            }
            currentAngle += sweep;

            GenreStats.Add(new GenreStatViewModel
            {
                Name = g.Name,
                Percentage = g.PercentageText,
                CountText = g.CountText,
                Ratio = g.Ratio,
                GameCount = g.GameCount,
                DurationText = g.DurationText,
                ColorHex = g.ColorHex,
                ColorBrush = new SolidColorBrush(ParseColor(g.ColorHex)),
                PathData = path,
                SubTagsText = g.SubTagsText
            });
        }

        if (result.GenreStats.Count == 0)
        {
            GenreStats.Add(new GenreStatViewModel
            {
                Name = "无记录",
                Percentage = "0%",
                CountText = "(0)",
                GameCount = 0,
                DurationText = "0h",
                ColorHex = "#94A3B8",
                ColorBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 148, 163, 184)),
                PathData = BuildFullDonut(cx, cy, R, r),
                SubTagsText = string.Empty
            });
        }

        // 9. Update 【游戏探索】 (Game Exploration)
        ExplorationCategories.Clear();
        if (result.Exploration != null)
        {
            ExplorationTotalGamesCount = result.Exploration.TotalGamesCount;
            ExplorationSubtitle = PeriodMode switch
            {
                StatsPeriodMode.Month => "本月你在游戏库中的探索与尝试情况",
                StatsPeriodMode.Quarter => "本季你在游戏库中的探索与尝试情况",
                StatsPeriodMode.Year => "今年你在游戏库中的探索与尝试情况",
                _ => "本期你在游戏库中的探索与尝试情况"
            };

            foreach (var cat in result.Exploration.Categories)
            {
                string iconGlyph = cat.IconType switch
                {
                    "sparkle" => "\uE735",
                    "repeat" => "\uE72C",
                    "history" => "\uE81C",
                    "pause" => "\uE769",
                    _ => "\uE734"
                };

                var avatars = new List<ExplorationAvatarItem>();
                int maxAvatars = 2;
                var covers = cat.CoverPaths.Take(maxAvatars).ToList();
                for (int i = 0; i < covers.Count; i++)
                {
                    avatars.Add(new ExplorationAvatarItem
                    {
                        CoverPath = covers[i],
                        Margin = new Thickness(i == 0 ? 0 : -8, 0, 0, 0)
                    });
                }

                int overflow = cat.Count - covers.Count;

                ExplorationCategories.Add(new ExplorationCategoryViewModel
                {
                    Key = cat.Key,
                    Title = cat.Title,
                    Description = cat.Description,
                    Count = cat.Count,
                    CountText = $"{cat.Count} 款",
                    PercentageText = cat.PercentageText,
                    ColorHex = cat.ColorHex,
                    ColorBrush = new SolidColorBrush(ParseColor(cat.ColorHex)),
                    IconGlyph = iconGlyph,
                    Avatars = avatars,
                    Delta = cat.Delta,
                    DeltaText = cat.DeltaText,
                    HasOverflow = overflow > 0,
                    OverflowText = $"+{overflow}",
                    TooltipText = cat.TooltipText
                });
            }

            RenderDonutCollection(result.ExplorationDonutSlices ?? Array.Empty<DonutSlice>(), ExplorationDonutSlices, 75, 75, 66, 45, 2.5);
        }

        // 10. Update 【游戏时长分布】 (Playtime Tier Distribution)
        PlaytimeTiers.Clear();
        LeftPlaytimeTiers.Clear();
        RightPlaytimeTiers.Clear();
        if (result.PlaytimeTiers != null)
        {
            TierTotalGamesCount = result.PlaytimeTiers.TotalGamesCount;

            for (int i = 0; i < result.PlaytimeTiers.Tiers.Count; i++)
            {
                var t = result.PlaytimeTiers.Tiers[i];
                var vm = new PlaytimeTierViewModel
                {
                    TierName = t.TierName,
                    GameCount = t.GameCount,
                    CountText = t.CountText,
                    Percentage = t.PercentageText,
                    ColorHex = t.ColorHex,
                    ColorBrush = new SolidColorBrush(ParseColor(t.ColorHex))
                };
                PlaytimeTiers.Add(vm);
                if (i % 2 == 0)
                {
                    LeftPlaytimeTiers.Add(vm);
                }
                else
                {
                    RightPlaytimeTiers.Add(vm);
                }
            }

            RenderDonutCollection(result.PlaytimeTierDonutSlices ?? Array.Empty<DonutSlice>(), PlaytimeTierDonutSlices, 75, 75, 66, 45, 2.5);
        }

        // 11. Update 【游戏活跃度】 (Game Activities)
        GameActivities.Clear();
        if (result.GameActivities != null)
        {
            foreach (var act in result.GameActivities.Take(5))
            {
                GameActivities.Add(act);
            }
        }

        // 12. Period Game Records (Take top 5, kept for compatibility)
        PeriodGameRecords.Clear();
        foreach (var item in result.GameRankings.Take(5))
        {
            PeriodGameRecords.Add(item);
        }

        // 13. Update Streak using accurate database query
        StreakDays = await _repo.GetConsecutiveStreakDaysAsync(DateTime.Today);
        StreakTitleText = StreakDays > 0
            ? $"你已连续游戏 {StreakDays} 天\n保持热爱，继续前进！"
            : "今天还没有开始游戏，去开启一场冒险吧！";
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static void RenderDonutCollection(
        IReadOnlyList<DonutSlice> slices,
        ObservableCollection<DonutSliceViewModel> targetCollection,
        double cx = 75.0, double cy = 75.0, double R = 66.0, double r = 45.0, double gap = 2.5)
    {
        targetCollection.Clear();
        if (slices.Count == 0 || slices.All(s => s.SweepAngle <= 0))
        {
            targetCollection.Add(new DonutSliceViewModel
            {
                Name = "无记录",
                PercentageText = "0%",
                DurationText = "0h",
                ColorBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 148, 163, 184)),
                ColorHex = "#94A3B8",
                PathData = BuildFullDonut(cx, cy, R, r)
            });
            return;
        }

        var validSlices = slices.Where(s => s.SweepAngle > 0.01).ToList();
        double effectiveGap = validSlices.Count > 1 ? gap : 0;
        double currentAngle = 0;

        foreach (var s in validSlices)
        {
            double sweep = s.Percentage * 360.0;
            string path;
            if (sweep >= 359.5 || validSlices.Count == 1)
            {
                path = BuildFullDonut(cx, cy, R, r);
            }
            else
            {
                double effectiveSweep = Math.Max(0.5, sweep - effectiveGap);
                double effectiveStart = currentAngle + effectiveGap / 2.0;
                path = BuildDonutArc(cx, cy, R, r, effectiveStart, effectiveSweep);
            }
            currentAngle += sweep;

            targetCollection.Add(new DonutSliceViewModel
            {
                Name = s.Name,
                PercentageText = s.PercentageText,
                DurationText = s.DurationText,
                ColorHex = s.ColorHex,
                ColorBrush = new SolidColorBrush(ParseColor(s.ColorHex)),
                CoverPath = s.CoverPath,
                PathData = path
            });
        }
    }

    private void RenderDonutSlices(IReadOnlyList<DonutSlice> slices)
    {
        double cx = 80, cy = 80;
        double R = 72, r = 48;

        if (slices.Count == 0)
        {
            // Empty grey ring
            DonutSlices.Add(new DonutSliceViewModel
            {
                Name = "无记录",
                PercentageText = "0%",
                DurationText = "0h",
                ColorBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 148, 163, 184)),
                ColorHex = "#94A3B8",
                CoverPath = null,
                PathData = BuildFullDonut(cx, cy, R, r)
            });
            return;
        }

        double gap = slices.Count > 1 ? 2.5 : 0;
        foreach (var s in slices)
        {
            if (s.SweepAngle <= 0.1) continue;

            string path;
            if (s.SweepAngle >= 359.5)
            {
                path = BuildFullDonut(cx, cy, R, r);
            }
            else
            {
                double effectiveSweep = Math.Max(0.5, s.SweepAngle - gap);
                double effectiveStart = s.StartAngle + gap / 2.0;
                path = BuildDonutArc(cx, cy, R, r, effectiveStart, effectiveSweep);
            }

            DonutSlices.Add(new DonutSliceViewModel
            {
                Name = s.Name,
                PercentageText = s.PercentageText,
                DurationText = s.DurationText,
                ColorBrush = new SolidColorBrush(ParseColor(s.ColorHex)),
                ColorHex = s.ColorHex,
                CoverPath = s.CoverPath,
                PathData = path
            });
        }
    }

    private static string BuildFullDonut(double cx, double cy, double R, double r)
    {
        return $"M {cx:F1},{cy - R:F1} " +
               $"A {R:F1},{R:F1} 0 1,1 {cx:F1},{cy + R:F1} " +
               $"A {R:F1},{R:F1} 0 1,1 {cx:F1},{cy - R:F1} " +
               $"M {cx:F1},{cy - r:F1} " +
               $"A {r:F1},{r:F1} 0 1,0 {cx:F1},{cy + r:F1} " +
               $"A {r:F1},{r:F1} 0 1,0 {cx:F1},{cy - r:F1} Z";
    }

    private static string BuildDonutArc(double cx, double cy, double R, double r, double startAngleDeg, double sweepAngleDeg)
    {
        // Angles measured clockwise starting from top (-90 degrees)
        double a1 = (startAngleDeg - 90.0) * Math.PI / 180.0;
        double a2 = (startAngleDeg + sweepAngleDeg - 90.0) * Math.PI / 180.0;

        double ox1 = cx + R * Math.Cos(a1);
        double oy1 = cy + R * Math.Sin(a1);
        double ox2 = cx + R * Math.Cos(a2);
        double oy2 = cy + R * Math.Sin(a2);

        double ix1 = cx + r * Math.Cos(a1);
        double iy1 = cy + r * Math.Sin(a1);
        double ix2 = cx + r * Math.Cos(a2);
        double iy2 = cy + r * Math.Sin(a2);

        int isLargeArc = sweepAngleDeg > 180.0 ? 1 : 0;

        // Path: Move to ox1,oy1 -> Arc to ox2,oy2 -> Line to ix2,iy2 -> Arc back to ix1,iy1 -> Close
        return $"M {ox1:F1},{oy1:F1} A {R:F1},{R:F1} 0 {isLargeArc},1 {ox2:F1},{oy2:F1} L {ix2:F1},{iy2:F1} A {r:F1},{r:F1} 0 {isLargeArc},0 {ix1:F1},{iy1:F1} Z";
    }

    private void RenderTrendChart(IReadOnlyList<DailyTrendPoint> points, int maxMinutes, int maxSessions)
    {
        TrendPointItems.Clear();
        HasTodayPoint = false;

        if (points.Count == 0)
        {
            TrendLinePathData = string.Empty;
            TrendAreaPathData = string.Empty;
            TrendDotsPathData = string.Empty;
            TrendMaxText = "0h";
            TrendMidText = "0h";
            TrendStartDate = string.Empty;
            TrendMidDate = string.Empty;
            TrendEndDate = string.Empty;
            return;
        }

        TrendStartDate = points.First().Date.ToString("M/d");
        TrendMidDate = points[points.Count / 2].Date.ToString("M/d");
        TrendEndDate = points.Last().Date.ToString("M/d");

        double width = _chartActualWidth > 100 ? _chartActualWidth : 500;
        double height = _chartActualHeight > 50 ? _chartActualHeight : 160;
        double padLeft = 35;
        double padRight = 20;
        double padTop = 10;
        double padBottom = 22;

        double chartW = Math.Max(20, width - padLeft - padRight);
        double chartH = Math.Max(20, height - padTop - padBottom);
        double baselineY = padTop + chartH;
        ChartBaselineY = baselineY;

        bool isDuration = IsTrendDuration;
        double maxVal = isDuration ? maxMinutes : maxSessions;
        if (maxVal <= 0) maxVal = isDuration ? 60 : 5;

        // Update Y labels
        if (isDuration)
        {
            TrendMaxText = DailyAggregator.FormatHoursMinutes((int)maxVal);
            TrendMidText = DailyAggregator.FormatHoursMinutes((int)(maxVal / 2));
            TrendBaseText = "0h";
        }
        else
        {
            TrendMaxText = $"{(int)maxVal}次";
            TrendMidText = $"{(int)(maxVal / 2)}次";
            TrendBaseText = "0次";
        }

        int count = points.Count;
        var coords = new List<(double X, double Y)>();
        bool foundToday = false;
        double todayX = 0, todayY = 0;

        for (int i = 0; i < count; i++)
        {
            var pt = points[i];
            double x = count > 1 ? padLeft + (i * chartW / (count - 1)) : padLeft + chartW / 2;
            double val = isDuration ? pt.DurationMinutes : pt.SessionCount;
            double y = baselineY - (val / maxVal * chartH);
            coords.Add((x, y));

            bool isToday = pt.IsToday || pt.Date.Date == DateTime.Today;
            if (isToday)
            {
                foundToday = true;
                todayX = x;
                todayY = y;
            }

            TrendPointItems.Add(new TrendPointItemViewModel
            {
                Date = pt.Date,
                DateLabel = pt.DateLabel,
                FullDateTitle = pt.FullDateTitle,
                ValueText = isDuration ? $"总游戏时长 {pt.DurationText}" : $"游玩次数 {pt.SessionCount} 次",
                GameCount = pt.GameCount,
                GameCountText = $"游戏 {pt.GameCount} 款",
                IsToday = isToday,
                X = x,
                Y = y
            });
        }

        HasTodayPoint = foundToday;
        TodayDotX = todayX;
        TodayDotY = todayY;

        // Build Line Path and Area Path with cubic bezier smoothing
        var lineSb = new System.Text.StringBuilder();
        lineSb.Append($"M {coords[0].X:F1},{coords[0].Y:F1} ");

        for (int i = 0; i < coords.Count - 1; i++)
        {
            var p0 = i > 0 ? coords[i - 1] : coords[i];
            var p1 = coords[i];
            var p2 = coords[i + 1];
            var p3 = i + 2 < coords.Count ? coords[i + 2] : p2;

            double cp1X = p1.X + (p2.X - p0.X) / 6.0;
            double cp1Y = p1.Y + (p2.Y - p0.Y) / 6.0;
            double cp2X = p2.X - (p3.X - p1.X) / 6.0;
            double cp2Y = p2.Y - (p3.Y - p1.Y) / 6.0;

            lineSb.Append($"C {cp1X:F1},{cp1Y:F1} {cp2X:F1},{cp2Y:F1} {p2.X:F1},{p2.Y:F1} ");
        }

        TrendLinePathData = lineSb.ToString();

        // Area: from line path, connect to bottom right, line to bottom left, close
        var areaSb = new System.Text.StringBuilder(TrendLinePathData);
        areaSb.Append($"L {coords[^1].X:F1},{baselineY:F1} L {coords[0].X:F1},{baselineY:F1} Z");
        TrendAreaPathData = areaSb.ToString();

        TrendDotsPathData = string.Empty;
    }

    private static Brush GetDeltaBrush(double deltaPercent)
    {
        if (deltaPercent > 0)
        {
            return new SolidColorBrush(ColorHelper.FromArgb(255, 52, 211, 153)); // Green
        }
        if (deltaPercent < 0)
        {
            return new SolidColorBrush(ColorHelper.FromArgb(255, 248, 113, 113)); // Red
        }
        return new SolidColorBrush(ColorHelper.FromArgb(255, 148, 163, 184)); // Gray
    }

    private static string GetDeltaIconData(double deltaPercent)
    {
        if (deltaPercent > 0) return TablerArrowUpPath;
        if (deltaPercent < 0) return TablerArrowDownPath;
        return TablerDashPath;
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return ColorHelper.FromArgb(255, r, g, b);
            }
        }
        catch { }
        return Colors.DodgerBlue;
    }
}
