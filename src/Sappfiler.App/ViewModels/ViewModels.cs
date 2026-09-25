using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Covers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GameTimeTracker.App.ViewModels;

public partial class HeatmapCellViewModel : ObservableObject
{
    [ObservableProperty] public partial DateTime Date { get; set; }
    [ObservableProperty] public partial string DayText { get; set; } = "";
    [ObservableProperty] public partial int DurationMinutes { get; set; }
    [ObservableProperty] public partial string DurationText { get; set; } = "0m";
    [ObservableProperty] public partial int GameCount { get; set; }
    [ObservableProperty] public partial int Level { get; set; } // 0 to 4
    [ObservableProperty] public partial bool IsCurrentMonth { get; set; } = true;
    [ObservableProperty] public partial bool IsToday { get; set; }
    [ObservableProperty] public partial string TooltipText { get; set; } = "";
}

public partial class GameListItemViewModel : ObservableObject
{
    [ObservableProperty] public partial int GameId { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Platform { get; set; } = "";
    [ObservableProperty] public partial string PlatformId { get; set; } = "";
    [ObservableProperty] public partial string DurationText { get; set; } = "";
    [ObservableProperty] public partial double ProgressPercentage { get; set; } // 0 to 100
    [ObservableProperty] public partial string PercentageText { get; set; } = "";
    [ObservableProperty] public partial string? CoverPath { get; set; }
    public bool HasCover => !string.IsNullOrEmpty(CoverPath) && File.Exists(CoverPath);
}

public partial class RecentRecordViewModel : ObservableObject
{
    [ObservableProperty] public partial string DateText { get; set; } = "";
    [ObservableProperty] public partial string GameName { get; set; } = "";
    [ObservableProperty] public partial string Platform { get; set; } = "";
    [ObservableProperty] public partial string PlatformId { get; set; } = "";
    [ObservableProperty] public partial string DurationText { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "● 已同步";
    [ObservableProperty] public partial string StatusBrushKey { get; set; } = "StatusGreenBrush";
    [ObservableProperty] public partial string? CoverPath { get; set; }

    /// <summary>对应 daily_summary.id；删除该条记录时需要它。</summary>
    public int DailySummaryId { get; set; }

    /// <summary>该条记录在 Notion 上的 page id，空表示尚未同步（删除确认文案需要）。</summary>
    public string? NotionPageId { get; set; }

    public int DurationMinutes { get; set; }
    public bool HasCover => !string.IsNullOrEmpty(CoverPath) && File.Exists(CoverPath);

    public Microsoft.UI.Xaml.Media.Brush StatusBrush
    {
        get
        {
            var key = string.IsNullOrEmpty(StatusBrushKey) ? "StatusGreenBrush" : StatusBrushKey;
            if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var res) && res is Microsoft.UI.Xaml.Media.Brush b)
            {
                return b;
            }
            return key switch
            {
                "StatusOrangeBrush" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFB, 0x92, 0x3C)),
                "StatusRedBrush" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF8, 0x71, 0x71)),
                _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x34, 0xD3, 0x99))
            };
        }
    }
}

public partial class HomeViewModel : ObservableObject
{
    private readonly IDatabaseRepository _repo;
    private readonly GameSessionManager _sessionManager;
    private readonly INotionSyncService _syncService;
    private readonly CoverCacheService _coverCache;
    private readonly IGameArtworkService? _artworkService;
    private readonly IGameMatcher _matcher;
    private readonly DispatcherQueue _dispatcherQueue;
    private CancellationTokenSource? _heroArtworkCts;
    private int? _heroArtworkTargetGameId;

    // Current Game State (Hero Card)
    [ObservableProperty] public partial bool IsGameRunning { get; set; }
    [ObservableProperty] public partial string CurrentGameTitle { get; set; } = "暂无运行中的游戏";
    [ObservableProperty] public partial string CurrentGameSubtitle { get; set; } = "后台持续监听 Steam / Epic / Xbox 游戏";
    [ObservableProperty] public partial string CurrentGameOriginalTitle { get; set; } = "";
    [ObservableProperty] public partial string CurrentGameTimer { get; set; } = "00:00:00";
    [ObservableProperty] public partial string CurrentGamePlatform { get; set; } = "空闲监控中";
    [ObservableProperty] public partial string CurrentGameTag { get; set; } = "";
    [ObservableProperty] public partial string? CurrentGameCoverPath { get; set; }
    [ObservableProperty] public partial string? CurrentGameHeroBackgroundUrl { get; set; }

    /// <summary>
    /// 当前游戏对应的商店页面链接；只有 Steam 平台且 AppID 干净时才有值，
    /// 其它平台保持 null（Hero 卡片右上角的平台胶囊据此决定是否可点）。
    /// </summary>
    [ObservableProperty] public partial string? CurrentGameStoreUrl { get; set; }

    /// <summary>鼠标悬停在 Hero 卡片上时暂停多游戏轮播</summary>
    [ObservableProperty] public partial bool IsHeroCarouselPaused { get; set; }

    public bool HasCurrentGameCover => !string.IsNullOrEmpty(CurrentGameCoverPath);

    public const string TablerArrowUpPath = "M 12 4 l 0 16 M 16 8 l -4 -4 l -4 4";
    public const string TablerArrowDownPath = "M 12 20 l 0 -16 M 16 16 l -4 4 l -4 -4";
    public const string TablerDashPath = "M 6 12 l 12 0";

    // Stats
    [ObservableProperty] public partial string TodayDurationText { get; set; } = "0h";
    [ObservableProperty] public partial string TodayDeltaText { get; set; } = "较昨日 0%";
    [ObservableProperty] public partial string TodayDeltaPercentText { get; set; } = "0%";
    [ObservableProperty] public partial string TodayDeltaIconData { get; set; } = TablerDashPath;
    [ObservableProperty] public partial Microsoft.UI.Xaml.Media.Brush TodayDeltaBrush { get; set; } = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x10, 0xB9, 0x81));

    [ObservableProperty] public partial string WeekDurationText { get; set; } = "0h";
    [ObservableProperty] public partial string WeekDeltaText { get; set; } = "较上周 0%";
    [ObservableProperty] public partial string WeekDeltaPercentText { get; set; } = "0%";
    [ObservableProperty] public partial string WeekDeltaIconData { get; set; } = TablerDashPath;
    [ObservableProperty] public partial Microsoft.UI.Xaml.Media.Brush WeekDeltaBrush { get; set; } = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x10, 0xB9, 0x81));

    [ObservableProperty] public partial int StreakDays { get; set; } = 3;
    [ObservableProperty] public partial string StreakRankTitle { get; set; } = "🎮 Lv.1 愿望单收集家";
    [ObservableProperty] public partial string NextMilestoneText { get; set; } = "下一个里程碑：6 天 (🌿 Lv.2)";
    [ObservableProperty] public partial double NextMilestoneTarget { get; set; } = 6;
    [ObservableProperty] public partial string HeatmapActiveDaysText { get; set; } = "游戏时长记录 0 天";

    // 12-Week Activity Heatmap
    [ObservableProperty] public partial ActivityHeatmapResult? ActivityHeatmap { get; set; }
    [ObservableProperty] public partial string CurrentMonthHeader { get; set; } = DateTime.Today.ToString("yyyy年M月");
    public ObservableCollection<HeatmapCellViewModel> HeatmapCells { get; } = new();

    // Lists
    private List<GameListItemViewModel> _allTodayGamesCache = new();
    [ObservableProperty] public partial bool IsTodayGamesExpanded { get; set; } = false;
    [ObservableProperty] public partial bool HasMoreThanThreeTodayGames { get; set; } = false;
    [ObservableProperty] public partial string TodayGamesToggleText { get; set; } = "查看全部 ∨";

    public ObservableCollection<GameListItemViewModel> TodayGames { get; } = new();
    public ObservableCollection<RecentRecordViewModel> RecentRecords { get; } = new();

    // Notion Status
    [ObservableProperty] public partial string NotionStatusText { get; set; } = "未绑定 Notion · 本地模式";
    [ObservableProperty] public partial string NotionLastSyncText { get; set; } = "在设置页填入 Token 与数据库 ID 后启用同步";
    [ObservableProperty] public partial string NotionStatusBrushKey { get; set; } = "TextPrimaryBrush";

    public Microsoft.UI.Xaml.Media.Brush NotionStatusBrush
    {
        get
        {
            var key = string.IsNullOrEmpty(NotionStatusBrushKey) ? "TextPrimaryBrush" : NotionStatusBrushKey;
            if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var res) && res is Microsoft.UI.Xaml.Media.Brush b)
            {
                return b;
            }
            return key switch
            {
                "StatusGreenBrush" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x34, 0xD3, 0x99)),
                "StatusOrangeBrush" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFB, 0x92, 0x3C)),
                "StatusRedBrush" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF8, 0x71, 0x71)),
                _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Xaml.Application.Current?.RequestedTheme == Microsoft.UI.Xaml.ApplicationTheme.Dark
                        ? Windows.UI.Color.FromArgb(255, 0xCC, 0xCC, 0xFF)
                        : Windows.UI.Color.FromArgb(255, 0x0F, 0x17, 0x2A))
            };
        }
    }

    partial void OnNotionStatusBrushKeyChanged(string value)
    {
        OnPropertyChanged(nameof(NotionStatusBrush));
    }

    partial void OnNotionStatusTextChanged(string value)
    {
        UpdateNotionStatusBrush(value);
    }

    public void UpdateNotionStatusBrush(string? status = null)
    {
        var text = status ?? NotionStatusText;
        if (!_syncService.IsNotionConfigured || text.Contains("未绑定") || text.Contains("本地模式"))
        {
            // 本地模式就黑色（其他小标题的颜色：TextPrimaryBrush）
            NotionStatusBrushKey = "TextPrimaryBrush";
        }
        else if (text.Contains("异常") || text.Contains("失败") || text.Contains("错误"))
        {
            // 同步异常红色
            NotionStatusBrushKey = "StatusRedBrush";
        }
        else if (text.Contains("待处理") || text.Contains("待同步") || text.Contains("进行中") || text.Contains("正在"))
        {
            // 待处理待同步黄色
            NotionStatusBrushKey = "StatusOrangeBrush";
        }
        else if (text.Contains("已同步") || text.Contains("同步完成") || text.Contains("最新") || text.Contains("就绪") || text.Contains("已更新"))
        {
            // 成功绿色
            NotionStatusBrushKey = "StatusGreenBrush";
        }
        else
        {
            NotionStatusBrushKey = "StatusOrangeBrush";
        }
    }

    // Navigation callbacks
    public Action<string>? RequestNavigate { get; set; }
    public Func<Func<Task>, Task>? HeroTransitionHandler { get; set; }

    private readonly DispatcherTimer _secondTimer;
    private readonly DispatcherTimer _dayBoundaryTimer;
    private DateTime _loadedAccountingDate = DateTime.MinValue;
    private int _heroCarouselTick;   // 秒计数，用于多游戏轮播（每 5 秒切换）
    private int _heroCarouselIndex;  // 当前轮播到的会话下标
    private bool _isHeroTransitioning; // 当前是否正在执行卡片淡入淡出动效
    private int? _activeGameId;      // 当前 Hero 卡片所展示的游戏 ID
    private int _currentElapsedSeconds; // 当前读秒单调累加秒数（防止多进程或异步心跳引起时间抽搐倒退）
    private string? _activeGamePlatform;
    private string? _activeGamePlatformId;
    private string? _activeGameExePath;
    private bool _coverRefreshQueued;

    public HomeViewModel(
        IDatabaseRepository repo,
        GameSessionManager sessionManager,
        INotionSyncService syncService,
        CoverCacheService coverCache,
        IGameArtworkService? artworkService = null)
    {
        _repo = repo;
        _sessionManager = sessionManager;
        _syncService = syncService;
        _coverCache = coverCache;
        _artworkService = artworkService;
        _matcher = new GameMatcher();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _secondTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _secondTimer.Tick += OnSecondTimerTick;

        // 跨日检测定时器：每 10 秒轻量比对当前业务日期与上次加载日期，跨越 24 点（或自定义跨日结算点）时自动触发重算刷新
        _dayBoundaryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _dayBoundaryTimer.Tick += (s, e) =>
        {
            var currentAccountingDate = AccountingDateHelper.GetAccountingDate(DateTime.Now, _sessionManager.DailyCutoffHour);
            if (_loadedAccountingDate != DateTime.MinValue && currentAccountingDate != _loadedAccountingDate)
            {
                _ = RefreshAllDataAsync();
            }
        };
        _dayBoundaryTimer.Start();

        _sessionManager.SessionStarted += OnSessionStarted;
        _sessionManager.SessionHeartbeat += OnSessionHeartbeat;
        _sessionManager.SessionEnded += OnSessionEnded;
        _syncService.SyncStatusChanged += OnSyncStatusChanged;
        _coverCache.CoverDownloaded += OnCoverDownloaded;
    }

    private bool _isWindowHidden;

    /// <summary>
    /// 暂停前台 UI 轮询定时器（窗口隐藏/收进托盘时调用，避免后台无谓的 UI 刷新消耗）。
    /// </summary>
    public void PauseTimers()
    {
        _isWindowHidden = true;
        _secondTimer.Stop();
        _dayBoundaryTimer.Stop();
    }

    /// <summary>
    /// 恢复前台 UI 轮询定时器（窗口从托盘重新打开时调用）。
    /// </summary>
    public void ResumeTimers()
    {
        _isWindowHidden = false;
        _dayBoundaryTimer.Start();
        if (IsGameRunning && !_secondTimer.IsEnabled)
        {
            _secondTimer.Start();
        }
    }

    private sealed record ActiveGameSummary(
        int GameId,
        GameSession EarliestSession,
        DateTime EarliestStartTime,
        int MaxDurationSeconds
    );

    private List<ActiveGameSummary> GetDistinctActiveGames()
    {
        var allSessions = _sessionManager.GetActiveSessions();
        if (allSessions.Count == 0) return new List<ActiveGameSummary>();

        // ⚠️ 关键去重与稳定排序：按 GameId 分组，并严格按 GameId 升序排列。
        // 多进程游戏（启动器 launcher.exe + 游戏主进程 game.exe、崩溃收集器等）对应同一款游戏（相同 GameId）。
        // 绝不能按 EarliestStartTime 排序，因为游戏启动时启动时间相近或子进程生灭会导致列表顺序随机翻转，
        // 造成轮播下标与游戏映射混乱（连续两次显示同一款游戏）！
        return allSessions
            .GroupBy(s => s.GameId)
            .Select(g => new ActiveGameSummary(
                GameId: g.Key,
                EarliestSession: g.OrderBy(s => s.StartTime).First(),
                EarliestStartTime: g.Min(s => s.StartTime),
                MaxDurationSeconds: g.Max(s => s.DurationSeconds)
            ))
            .OrderBy(g => g.GameId)
            .ToList();
    }

    private void OnSecondTimerTick(object? sender, object e)
    {
        if (!IsGameRunning) return;

        var distinctGames = GetDistinctActiveGames();
        if (distinctGames.Count == 0) return;

        // 1. 多游戏轮播：
        // 只有当真正运行了多款不同游戏、鼠标未悬停在卡片上、且当前未在动画过渡中时才推进 5 秒轮播
        if (distinctGames.Count > 1 && !IsHeroCarouselPaused && !_isHeroTransitioning)
        {
            _heroCarouselTick++;
            if (_heroCarouselTick >= 5)
            {
                _heroCarouselTick = 0;
                _heroCarouselIndex = (_heroCarouselIndex + 1) % distinctGames.Count;
                var nextGroup = distinctGames[_heroCarouselIndex];
                _ = TransitionHeroCardAsync(nextGroup);
                return;
            }
        }
        else if (distinctGames.Count <= 1)
        {
            _heroCarouselTick = 0;
        }

        // 2. 如果正在进行卡片淡入淡出动画过渡，跳过本秒读秒更新，防止与过渡状态打架
        if (_isHeroTransitioning) return;

        // 3. 当前展示游戏的秒数更新：
        // 根据当前活跃的游戏 ID 查找对应分组；找不到则回退到当前轮播下标
        var activeGroup = (_activeGameId.HasValue
            ? distinctGames.FirstOrDefault(g => g.GameId == _activeGameId.Value)
            : null) ?? distinctGames[_heroCarouselIndex % distinctGames.Count];

        var active = activeGroup.EarliestSession;

        if (active != null)
        {
            if (_activeGameId != active.GameId)
            {
                // 切换到了不同的游戏，对齐该游戏的基准时间
                _activeGameId = active.GameId;
                _currentElapsedSeconds = Math.Max(activeGroup.MaxDurationSeconds, (int)(DateTime.Now - activeGroup.EarliestStartTime).TotalSeconds);
            }
            else
            {
                // 同一款游戏：平稳以 1 秒为周期单调递增。
                // 仅当发生系统休眠、时间跳变等严重偏差（>3秒）时才做静默校准，平时杜绝任何跳秒与抽搐
                var wallSeconds = Math.Max(activeGroup.MaxDurationSeconds, (int)(DateTime.Now - activeGroup.EarliestStartTime).TotalSeconds);
                if (Math.Abs(wallSeconds - _currentElapsedSeconds) > 3)
                {
                    _currentElapsedSeconds = wallSeconds;
                }
                else
                {
                    _currentElapsedSeconds++;
                }
            }

            CurrentGameTimer = DailyAggregator.FormatSeconds(_currentElapsedSeconds);

            if (CurrentGameCoverPath == null && _activeGamePlatform != null && _activeGamePlatformId != null)
            {
                var cover = _coverCache.GetCoverPath(_activeGamePlatform, _activeGamePlatformId);
                if (File.Exists(cover) && new FileInfo(cover).Length > 0)
                {
                    CurrentGameCoverPath = cover;
                }
                else if (!string.IsNullOrEmpty(_activeGameExePath) && File.Exists(_activeGameExePath))
                {
                    _coverCache.ExtractAndSaveExecutableIcon(_activeGameExePath, _activeGamePlatform, _activeGamePlatformId);
                    cover = _coverCache.GetCoverPath(_activeGamePlatform, _activeGamePlatformId);
                    if (File.Exists(cover) && new FileInfo(cover).Length > 0)
                    {
                        CurrentGameCoverPath = cover;
                    }
                }
            }

            if (CurrentGameHeroBackgroundUrl == null && _heroArtworkTargetGameId != active.GameId && _artworkService != null && _activeGamePlatform != null && _activeGamePlatformId != null)
            {
                var identity = new GameIdentity(_activeGamePlatform, _activeGamePlatformId, "", "", _activeGameExePath ?? "", null, active.GameId);
                _ = LoadHeroArtworkAsync(identity, active.GameId);
            }
        }
    }

    [RelayCommand]
    public async Task RefreshAllDataAsync()
    {
        var today = AccountingDateHelper.GetAccountingDate(DateTime.Now, _sessionManager.DailyCutoffHour);
        _loadedAccountingDate = today;

        // ⚠️ 读取窗口由 DailyAggregator 统一给出，**不要在这里另写天数**。
        //    2026-09-19 QA 反馈的"记录只显示到 6/11、更早的没拉到本地"就是这个数字造成的：
        //    这里原来写死 `AddDays(-100)`（注释还写着"覆盖 84 天热力图"，那是热力图只有 12 周时的值），
        //    而热力图早已是 52 周（364 天）→ 窗口比热力图小了 3/4，
        //    于是热力图左边永远是空的，「游戏时长记录 N 天」也只统计到窗口内的天数。
        //    记录**其实早就全量拉到本地了**（拉取路径没有日期过滤），纯粹是显示/统计被截断。
        var startRange = DailyAggregator.DataWindowStart(today).ToString("yyyy-MM-dd");
        var endRange = today.AddDays(7).ToString("yyyy-MM-dd");

        // 1. Fetch data asynchronously
        var allSummaries = await _repo.GetDailySummariesRangeAsync(startRange, endRange);
        var stats = DailyAggregator.Aggregate(today, allSummaries);
        var recents = await _repo.GetRecentDailyRecordsAsync(5);

        // Precompute today's games (All games played today)
        var newTodayGames = new List<GameListItemViewModel>();
        foreach (var g in stats.TodayTopGames)
        {
            var cover = _coverCache.GetCoverPath(g.Platform, g.PlatformId);
            if (!File.Exists(cover) || new FileInfo(cover).Length == 0)
            {
                var dbGame = await _repo.GetGameByIdAsync(g.GameId);
                if (dbGame != null && !string.IsNullOrEmpty(dbGame.ExecutablePath))
                {
                    _coverCache.ExtractAndSaveExecutableIcon(dbGame.ExecutablePath, g.Platform, g.PlatformId);
                    cover = _coverCache.GetCoverPath(g.Platform, g.PlatformId);
                }
            }

            var pctText = g.Percentage < 0.01 && g.Minutes > 0 ? "<1%" : $"{Math.Round(g.Percentage * 100)}%";
            newTodayGames.Add(new GameListItemViewModel
            {
                GameId = g.GameId,
                Title = g.GameName,
                Platform = g.Platform,
                PlatformId = g.PlatformId,
                DurationText = g.DurationText,
                ProgressPercentage = Math.Round(g.Percentage * 100.0, 0),
                PercentageText = pctText,
                CoverPath = File.Exists(cover) && new FileInfo(cover).Length > 0 ? cover : null
            });
        }

        // Precompute recents
        var newRecents = new List<RecentRecordViewModel>();
        foreach (var r in recents)
        {
            var cover = _coverCache.GetCoverPath(r.Platform, r.PlatformId);
            var (statusText, brushKey) = r.SyncStatus switch
            {
                "synced" => ("● 已同步", "StatusGreenBrush"),
                "error" => ("● 失败", "StatusRedBrush"),
                "syncing" => ("● 同步中", "AccentBlueBrush"),
                _ => ("● 待同步", "StatusOrangeBrush")
            };

            newRecents.Add(new RecentRecordViewModel
            {
                DateText = r.Date,
                GameName = r.GameName,
                Platform = r.Platform,
                PlatformId = r.PlatformId,
                DurationText = DailyAggregator.FormatDuration(r.DurationMinutes),
                StatusText = statusText,
                StatusBrushKey = brushKey,
                CoverPath = File.Exists(cover) ? cover : null
            });
        }

        // Active recorded days count (days with > 0 minutes)
        var activeDays = allSummaries.Where(s => s.DurationMinutes > 0).Select(s => s.Date).Distinct().Count();

        // 2. Dispatch UI mutations safely to UI Thread
        _dispatcherQueue.TryEnqueue(() =>
        {
            TodayDurationText = DailyAggregator.FormatHoursMinutes(stats.TodayMinutes);
            TodayDeltaText = stats.TodayDeltaText;
            TodayDeltaPercentText = $"{Math.Abs(stats.TodayDeltaPercent)}%";
            TodayDeltaIconData = stats.TodayDeltaPercent > 0 ? TablerArrowUpPath : (stats.TodayDeltaPercent < 0 ? TablerArrowDownPath : TablerDashPath);

            WeekDurationText = DailyAggregator.FormatHoursMinutes(stats.WeekMinutes);
            WeekDeltaText = stats.WeekDeltaText;
            WeekDeltaPercentText = $"{Math.Abs(stats.WeekDeltaPercent)}%";
            WeekDeltaIconData = stats.WeekDeltaPercent > 0 ? TablerArrowUpPath : (stats.WeekDeltaPercent < 0 ? TablerArrowDownPath : TablerDashPath);

            var greenBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x10, 0xB9, 0x81));
            var redBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF8, 0x71, 0x71));
            var grayBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x94, 0xA3, 0xB8));
            TodayDeltaBrush = stats.TodayDeltaPercent > 0 ? greenBrush : (stats.TodayDeltaPercent < 0 ? redBrush : grayBrush);
            WeekDeltaBrush = stats.WeekDeltaPercent > 0 ? greenBrush : (stats.WeekDeltaPercent < 0 ? redBrush : grayBrush);

            var streak = Math.Max(stats.StreakDays, 1);
            StreakDays = streak;

            if (streak <= 5)
            {
                StreakRankTitle = "🎮 Lv.1 愿望单收集家";
                NextMilestoneText = "下一个里程碑：6 天 (🌿 Lv.2)";
                NextMilestoneTarget = 6;
            }
            else if (streak <= 15)
            {
                StreakRankTitle = "🌿 Lv.2 背包塞满草药的玩家";
                NextMilestoneText = "下一个里程碑：16 天 (⭐ Lv.3)";
                NextMilestoneTarget = 16;
            }
            else if (streak <= 30)
            {
                StreakRankTitle = "⭐ Lv.3 掌握彩蛋位置的知情人";
                NextMilestoneText = "下一个里程碑：31 天 (💎 Lv.4)";
                NextMilestoneTarget = 31;
            }
            else if (streak <= 52)
            {
                StreakRankTitle = "💎 Lv.4 全成就全收集狂人";
                NextMilestoneText = "下一个里程碑：53 天 (🏆 Lv.5)";
                NextMilestoneTarget = 53;
            }
            else
            {
                StreakRankTitle = "🏆 Lv.5 传说的无伤通关者";
                NextMilestoneText = "已达成最高段位！传奇缔造中";
                NextMilestoneTarget = streak;
            }

            HeatmapActiveDaysText = $"游戏时长记录 {activeDays} 天";
            CurrentMonthHeader = today.ToString("yyyy年M月");
            ActivityHeatmap = stats.ActivityHeatmap;

            _allTodayGamesCache = newTodayGames;
            HasMoreThanThreeTodayGames = newTodayGames.Count > 3;

            TodayGames.Clear();
            var itemsToShow = IsTodayGamesExpanded ? newTodayGames : newTodayGames.Take(3);
            foreach (var item in itemsToShow)
            {
                TodayGames.Add(item);
            }

            RecentRecords.Clear();
            foreach (var item in newRecents)
            {
                RecentRecords.Add(item);
            }
        });

        // 3. Update Notion Status state
        if (!_syncService.IsNotionConfigured)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                NotionStatusText = "未绑定 Notion · 本地模式";
                NotionLastSyncText = "在设置页填入 Token 与数据库 ID 后启用同步";
                UpdateNotionStatusBrush();
            });
        }
        else if (!_isSyncing)
        {
            var pendingGames = await _repo.GetPendingGamesAsync();
            var unhandledGames = pendingGames.Count(p => p.Status != "ignored");
            var pendingSummaries = await _repo.GetPendingDailySummariesAsync();

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (unhandledGames > 0)
                {
                    NotionStatusText = $"待处理 · {unhandledGames} 款游戏未绑定";
                    UpdateNotionStatusBrush();
                }
                else if (pendingSummaries.Count > 0)
                {
                    NotionStatusText = $"待同步 · {pendingSummaries.Count} 条记录待推送";
                    UpdateNotionStatusBrush();
                }
                else if (NotionStatusText.Contains("未绑定") || NotionStatusText.Contains("本地模式"))
                {
                    NotionStatusText = "已同步到 Notion";
                    UpdateNotionStatusBrush();
                }
                else
                {
                    UpdateNotionStatusBrush();
                }
            });
        }

        // 4. Update Hero Card
        await RefreshHeroCardAsync();
    }

    private async Task TransitionHeroCardAsync(ActiveGameSummary nextGroup)
    {
        if (_isHeroTransitioning) return;
        _isHeroTransitioning = true;

        try
        {
            var nextSession = nextGroup.EarliestSession;
            var nextGameId = nextSession.GameId;
            var nextElapsed = Math.Max(nextGroup.MaxDurationSeconds, (int)(DateTime.Now - nextGroup.EarliestStartTime).TotalSeconds);

            if (HeroTransitionHandler != null)
            {
                await HeroTransitionHandler(async () =>
                {
                    _activeGameId = nextGameId;
                    _currentElapsedSeconds = nextElapsed;
                    CurrentGameTimer = DailyAggregator.FormatSeconds(_currentElapsedSeconds);
                    await RefreshHeroCardAsync(nextSession);
                });
            }
            else
            {
                _activeGameId = nextGameId;
                _currentElapsedSeconds = nextElapsed;
                CurrentGameTimer = DailyAggregator.FormatSeconds(_currentElapsedSeconds);
                await RefreshHeroCardAsync(nextSession);
            }
        }
        finally
        {
            _isHeroTransitioning = false;
        }
    }

    public async Task RefreshHeroCardAsync(GameSession? active = null)
    {
        if (active == null)
        {
            var distinctGames = GetDistinctActiveGames();
            if (distinctGames.Count == 0)
            {
                active = null;
            }
            else
            {
                // 关键保护：如果当前正在展示的游戏仍然处于运行中，继续保持当前游戏，杜绝因刷新导致卡片被重置
                var currentRunning = _activeGameId.HasValue
                    ? distinctGames.FirstOrDefault(g => g.GameId == _activeGameId.Value)
                    : null;

                if (currentRunning != null)
                {
                    active = currentRunning.EarliestSession;
                }
                else
                {
                    _heroCarouselIndex = _heroCarouselIndex % distinctGames.Count;
                    active = distinctGames[_heroCarouselIndex].EarliestSession;
                }
            }
        }

        GameRecord? activeGame = null;
        string? activeCover = null;
        string? activeHeroBg = null;
        GameIdentity? identity = null;

        if (active != null)
        {
            activeGame = await _repo.GetGameByIdAsync(active.GameId);
            if (activeGame != null)
            {
                if (!string.IsNullOrEmpty(activeGame.ExecutablePath) && File.Exists(activeGame.ExecutablePath))
                {
                    try
                    {
                        _coverCache.ExtractAndSaveExecutableIcon(activeGame.ExecutablePath, activeGame.Platform, activeGame.PlatformId);
                    }
                    catch { }
                }

                var localCover = _coverCache.GetCoverPath(activeGame.Platform, activeGame.PlatformId);
                if (File.Exists(localCover) && new FileInfo(localCover).Length > 0)
                {
                    activeCover = localCover;
                }

                if (_artworkService != null)
                {
                    identity = new GameIdentity(
                        activeGame.Platform,
                        activeGame.PlatformId,
                        activeGame.Name,
                        activeGame.Executable,
                        activeGame.ExecutablePath,
                        activeGame.NotionPageId,
                        activeGame.Id
                    );

                    using var fastCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                    try
                    {
                        var art = await _artworkService.ResolveArtworkAsync(identity, fastCts.Token);
                        if (art != null && !string.IsNullOrEmpty(art.FilePathOrUrl))
                        {
                            activeHeroBg = art.FilePathOrUrl;
                            _heroArtworkTargetGameId = activeGame.Id;
                        }
                    }
                    catch { }
                }

                if (activeHeroBg == null && !string.IsNullOrEmpty(activeGame.NotionPageId))
                {
                    var catItem = await _repo.GetCatalogItemByPageIdAsync(activeGame.NotionPageId);
                    if (catItem != null && !string.IsNullOrEmpty(catItem.CoverUrl))
                    {
                        activeHeroBg = catItem.CoverUrl;
                        activeCover ??= catItem.CoverUrl;
                    }
                }

                if (activeHeroBg == null)
                {
                    // 未绑定（或绑定页没有 cover）时，仍按匹配链在总表里找一次，
                    // 这样总表后续补上 cover 也能自动生效。
                    var catalogItems = await _repo.GetCatalogItemsAsync();

                    // 复用统一匹配链（含封面 URL 内嵌的 Steam AppID）。
                    // 只做精确同名比对时，「风暴怕死队」这类中英异名条目永远匹配不上。
                    // 注：匹配链自 2026-09-19 起只返回确定性结果，
                    // 原先这里为排除模糊候选写的 `!= "fuzzy_candidate"` 已无必要。
                    var hit = _matcher.MatchGame(activeGame.Name, catalogItems, activeGame.PlatformId)
                                      .FirstOrDefault();
                    var matched = hit == null
                        ? null
                        : catalogItems.FirstOrDefault(c => c.PageId == hit.PageId);

                    if (matched != null && !string.IsNullOrEmpty(matched.CoverUrl))
                    {
                        activeHeroBg = matched.CoverUrl;
                        activeCover ??= matched.CoverUrl;
                    }
                }

                activeCover ??= activeHeroBg;
            }
        }

        void ApplyToUi()
        {
            if (active != null && activeGame != null)
            {
                _activeGamePlatform = activeGame.Platform;
                _activeGamePlatformId = activeGame.PlatformId;
                _activeGameExePath = activeGame.ExecutablePath;

                IsGameRunning = true;
                CurrentGameTitle = activeGame.Name;
                CurrentGameSubtitle = activeGame.Name;
                CurrentGameOriginalTitle = activeGame.Executable;
                CurrentGamePlatform = string.IsNullOrWhiteSpace(activeGame.Platform) ? "MANUAL" : activeGame.Platform.ToUpper();
                CurrentGameTag = string.IsNullOrWhiteSpace(activeGame.Platform) ? "MANUAL" : activeGame.Platform.ToUpper();

                // 汇总该游戏所有子进程会话：取最早启动时间与最大时长
                var gameSessions = _sessionManager.GetActiveSessions().Where(s => s.GameId == activeGame.Id).ToList();
                var earliestStart = gameSessions.Count > 0 ? gameSessions.Min(s => s.StartTime) : active.StartTime;
                var maxDuration = gameSessions.Count > 0 ? gameSessions.Max(s => s.DurationSeconds) : active.DurationSeconds;
                var calculated = Math.Max(maxDuration, (int)(DateTime.Now - earliestStart).TotalSeconds);

                if (_activeGameId != activeGame.Id)
                {
                    _activeGameId = activeGame.Id;
                    _currentElapsedSeconds = calculated;
                }
                else
                {
                    // 同一游戏刷新时（如后台数据或网络同步），若当前秒数与实际时间偏差在 3 秒内，不强行覆写，保持界面秒表平滑
                    if (Math.Abs(calculated - _currentElapsedSeconds) > 3)
                    {
                        _currentElapsedSeconds = calculated;
                    }
                }

                CurrentGameTimer = DailyAggregator.FormatSeconds(_currentElapsedSeconds);

                CurrentGameCoverPath = activeCover;
                CurrentGameStoreUrl = BuildSteamStoreUrl(activeGame.Platform, activeGame.PlatformId);

                if (_artworkService != null && identity != null)
                {
                    if (activeHeroBg != null)
                    {
                        CurrentGameHeroBackgroundUrl = activeHeroBg;
                        _heroArtworkTargetGameId = activeGame.Id;
                    }
                    else if (_heroArtworkTargetGameId != activeGame.Id)
                    {
                        CurrentGameHeroBackgroundUrl = null;
                        _ = LoadHeroArtworkAsync(identity, activeGame.Id);
                    }
                }
                else
                {
                    CurrentGameHeroBackgroundUrl = activeHeroBg;
                }

                if (CurrentGameCoverPath == null && !string.IsNullOrEmpty(activeGame.ExecutablePath))
                {
                    _ = _coverCache.EnsureCoverAsync(activeGame.Platform, activeGame.PlatformId, activeGame.ExecutablePath);
                }

                if (!_isWindowHidden && !_secondTimer.IsEnabled)
                {
                    _secondTimer.Start();
                }
            }
            else
            {
                _heroArtworkCts?.Cancel();
                _heroArtworkTargetGameId = null;

                _activeGameId = null;
                _currentElapsedSeconds = 0;
                _activeGamePlatform = null;
                _activeGamePlatformId = null;
                _activeGameExePath = null;

                IsGameRunning = false;
                CurrentGameTitle = "暂无运行中的游戏";
                CurrentGameSubtitle = "后台持续监听 Steam / Epic / Xbox 平台游戏";
                CurrentGameOriginalTitle = "";
                CurrentGamePlatform = "空闲监控中";
                CurrentGameTag = "监控中";
                CurrentGameTimer = "00:00:00";
                CurrentGameCoverPath = null;
                CurrentGameHeroBackgroundUrl = null;
                CurrentGameStoreUrl = null;

                if (_secondTimer.IsEnabled)
                {
                    _secondTimer.Stop();
                }
            }
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            ApplyToUi();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(ApplyToUi);
        }
    }

    private async Task LoadHeroArtworkAsync(GameIdentity identity, int targetGameId)
    {
        if (_artworkService == null) return;

        _heroArtworkCts?.Cancel();
        var cts = new CancellationTokenSource();
        _heroArtworkCts = cts;
        _heroArtworkTargetGameId = targetGameId;

        try
        {
            var artwork = await _artworkService.ResolveArtworkAsync(identity, cts.Token);
            if (!cts.IsCancellationRequested && _heroArtworkTargetGameId == targetGameId)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_activeGameId == targetGameId && !cts.IsCancellationRequested)
                    {
                        CurrentGameHeroBackgroundUrl = artwork?.FilePathOrUrl;
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Artwork] Error resolving artwork: {ex.Message}");
        }
    }

    /// <summary>
    /// Steam 商店链接。仅在平台为 steam 且 platform_id 是纯数字 AppID 时生成；
    /// Epic / Xbox / 手动添加的游戏一律返回 null（Hero 胶囊退化为不可点样式）。
    /// </summary>
    private static string? BuildSteamStoreUrl(string? platform, string? platformId)
    {
        if (!string.Equals(platform, "steam", StringComparison.OrdinalIgnoreCase)) return null;

        var id = platformId?.Trim();
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit)) return null;

        return $"https://store.steampowered.com/app/{id}/";
    }

    [RelayCommand]
    public void OpenNotion()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.notion.so",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    [RelayCommand]
    public void ManageMappings()
    {
        RequestNavigate?.Invoke("Mappings");
    }

    [RelayCommand]
    public void ToggleTodayGames()
    {
        IsTodayGamesExpanded = !IsTodayGamesExpanded;
        TodayGamesToggleText = IsTodayGamesExpanded ? "收起 ∧" : "查看全部 ∨";
        TodayGames.Clear();
        var itemsToShow = IsTodayGamesExpanded ? _allTodayGamesCache : _allTodayGamesCache.Take(3);
        foreach (var item in itemsToShow)
        {
            TodayGames.Add(item);
        }
    }

    [RelayCommand]
    public void ViewAllHistory()
    {
        RequestNavigate?.Invoke("History");
    }

    [RelayCommand]
    public void OpenSettings()
    {
        RequestNavigate?.Invoke("Settings");
    }

    private bool _isSyncing;

    [RelayCommand]
    public async Task SyncNowAsync()
    {
        // 防重入：周期同步在网络差时可能带重试跑几分钟，期间再点「立即同步」
        // 两条链会并发竞争（状态互相覆盖、写锁排队），看起来就像"点了没反应"。
        if (_isSyncing)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                NotionStatusText = "同步进行中…";
                NotionLastSyncText = "上一轮同步尚未完成，请稍候";
            });
            return;
        }

        // Notion 未配置（Token 或任一数据库 ID 缺失）时不上网，直接给出本地模式提示。
        if (!_syncService.IsNotionConfigured)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                NotionStatusText = "未绑定 Notion · 本地模式";
                NotionLastSyncText = "在设置页填入 Token 与数据库 ID 后启用同步";
            });
            await RefreshAllDataAsync();
            return;
        }

        _isSyncing = true;
        try
        {
            await _syncService.RefreshGameCatalogCacheAsync();

            // 「立即同步」也要把 Notion 侧的删除对账带上。
            // 否则在 Notion 删了记录、点立即同步，本地照样不动 —— 只能干等周期同步。
            var reconciled = await _syncService.ReconcileNotionDeletionsAsync();

            await _syncService.PullDailyRecordsFromNotionAsync();
            var count = await _syncService.SyncPendingDailyRecordsAsync();
            await _syncService.BackfillRelationsAsync();
            // 手动同步也要回刷标题，否则用户在总表改名后点「立即同步」看不到任何变化。
            await _syncService.RefreshDailyTitlesFromMasterAsync();
            _ = _coverCache.EnsureLibraryCoversAsync(_repo);
            _dispatcherQueue.TryEnqueue(() =>
            {
                NotionStatusText = reconciled.HasChanges
                    ? $"已同步（清理 {reconciled.DeletedGames} 款游戏 / {reconciled.DeletedDailyRecords} 条记录）"
                    : "已同步到 Notion";
                NotionLastSyncText = $"上次同步 {DateTime.Now:HH:mm}";
            });
            await RefreshAllDataAsync();
        }
        catch (Exception ex)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                NotionStatusText = "同步异常";
                NotionLastSyncText = ex.Message;
            });
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void OnSessionStarted(object? sender, GameSession session)
    {
        void HandleSessionStarted()
        {
            var distinctGames = GetDistinctActiveGames();
            if (distinctGames.Count > 0)
            {
                var targetIndex = distinctGames.FindIndex(g => g.GameId == session.GameId);
                if (targetIndex >= 0)
                {
                    _heroCarouselIndex = targetIndex;
                }
                // 新游戏启动时重置轮播计时，保证新游戏完整展示满 5 秒，不会刚启动就被切走
                _heroCarouselTick = 0;
            }

            if (!_secondTimer.IsEnabled) _secondTimer.Start();
            _ = RefreshHeroCardAsync(session);
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            HandleSessionStarted();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(HandleSessionStarted);
        }

        _ = RefreshAllDataAsync();
    }

    private void OnSessionHeartbeat(object? sender, GameSession session)
    {
        // 5 秒心跳纯粹用于后台 GameSessionManager 进行数据库落库、时长增量聚合与 Notion 同步。
        // 严禁在此处异步篡改 UI 的 _currentElapsedSeconds，彻底切断后台心跳到达时刻与 UI 定时器的相位差导致的秒数抽搐与双跳。
        // 界面秒表由 UI 线程的 _secondTimer 独立平稳维护（精确每秒 +1）。
    }

    private void OnSessionEnded(object? sender, GameSession session)
    {
        void HandleSessionEnded()
        {
            var remaining = _sessionManager.GetActiveSessions();
            if (remaining.Count == 0)
            {
                _secondTimer.Stop();
                _activeGameId = null;
                _currentElapsedSeconds = 0;
                CurrentGameTimer = "00:00:00";
                IsGameRunning = false;
                _heroCarouselTick = 0;
                _heroCarouselIndex = 0;
            }
            else
            {
                // 如果退出的游戏正好是当前展示的游戏，重置 tick 计时让下一个游戏展示满 5 秒
                if (_activeGameId.HasValue && session.GameId == _activeGameId.Value)
                {
                    _heroCarouselTick = 0;
                }
            }
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            HandleSessionEnded();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(HandleSessionEnded);
        }

        _ = RefreshHeroCardAsync(null);
        _ = RefreshAllDataAsync();
    }

    private void OnSyncStatusChanged(object? sender, string status)
    {
        // 同步链所有**结果与失败原因**都经此事件上报，统一落日志供 QA 排查。
        //
        // 但"正在进行…"这类纯 UI 进度提示不记：它们没有任何诊断价值
        // （下一行紧跟着就是结果），却占掉每轮同步一半的日志行数。
        // 约定：**进度提示以 "..." 结尾，结果不带** —— 见 NotionServices 里各 Invoke 点。
        if (!IsProgressMessage(status))
        {
            Infrastructure.AppLog.Info($"[同步] {status}");
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            NotionStatusText = status;
            NotionLastSyncText = $"上次同步 {DateTime.Now:HH:mm}";
        });
    }

    /// <summary>
    /// 判断是否是"正在进行…"型进度提示（只用于刷新界面，不值得写日志）。
    /// 同时兼容 ASCII 三点与中文省略号两种写法，避免哪天文案换了就失效。
    /// </summary>
    private static bool IsProgressMessage(string status)
    {
        if (string.IsNullOrEmpty(status)) return true;
        var trimmed = status.TrimEnd();
        return trimmed.EndsWith("...", StringComparison.Ordinal)
            || trimmed.EndsWith("…", StringComparison.Ordinal);
    }

    private void OnCoverDownloaded(object? sender, string platformId)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsGameRunning && _activeGamePlatform != null && _activeGamePlatformId != null &&
                string.Equals(_activeGamePlatformId, platformId, StringComparison.OrdinalIgnoreCase))
            {
                var cover = _coverCache.GetCoverPath(_activeGamePlatform, _activeGamePlatformId);
                if (File.Exists(cover) && new FileInfo(cover).Length > 0)
                {
                    CurrentGameCoverPath = cover;
                }
            }
        });

        // 【绝不能在这里直接调用 RefreshAllDataAsync】
        // ExtractAndSaveExecutableIcon 是【同步】触发本事件的，而 RefreshAllDataAsync
        // 结尾会 await RefreshHeroCardAsync → 再调 ExtractAndSaveExecutableIcon。
        // 又因为 Microsoft.Data.Sqlite 的异步 API 实际是同步完成的，整条调用链会一路
        // 同步下行并回到起点，形成无界递归，最终栈溢出（0xC00000FD）直接崩溃进程。
        // 因此这里必须投递到调度器并加防重入标志，彻底断开调用栈。
        if (_coverRefreshQueued) return;
        _coverRefreshQueued = true;
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _coverRefreshQueued = false;
            _ = RefreshAllDataAsync();
        });
    }
}
