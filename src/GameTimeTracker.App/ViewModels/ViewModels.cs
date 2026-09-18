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
    private readonly IGameMatcher _matcher;
    private readonly DispatcherQueue _dispatcherQueue;

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

    public bool HasCurrentGameCover => !string.IsNullOrEmpty(CurrentGameCoverPath);

    // Stats
    [ObservableProperty] public partial string TodayDurationText { get; set; } = "0m";
    [ObservableProperty] public partial string TodayDeltaText { get; set; } = "较昨日 0%";
    [ObservableProperty] public partial string WeekDurationText { get; set; } = "0m";
    [ObservableProperty] public partial string WeekDeltaText { get; set; } = "较上周 0%";
    [ObservableProperty] public partial int StreakDays { get; set; } = 3;
    [ObservableProperty] public partial string StreakRankTitle { get; set; } = "Lv.1 愿望单收集家";
    [ObservableProperty] public partial string NextMilestoneText { get; set; } = "下一个里程碑：6 天 (Lv.2)";
    [ObservableProperty] public partial double NextMilestoneTarget { get; set; } = 6;
    [ObservableProperty] public partial string HeatmapActiveDaysText { get; set; } = "游戏时长记录 0 天";

    // 12-Week Activity Heatmap
    [ObservableProperty] public partial ActivityHeatmapResult? ActivityHeatmap { get; set; }
    [ObservableProperty] public partial string CurrentMonthHeader { get; set; } = DateTime.Today.ToString("yyyy年M月");
    public ObservableCollection<HeatmapCellViewModel> HeatmapCells { get; } = new();

    // Lists
    public ObservableCollection<GameListItemViewModel> TodayGames { get; } = new();
    public ObservableCollection<RecentRecordViewModel> RecentRecords { get; } = new();

    // Notion Status
    [ObservableProperty] public partial string NotionStatusText { get; set; } = "未绑定 Notion · 本地模式";
    [ObservableProperty] public partial string NotionLastSyncText { get; set; } = "在设置页填入 Token 与数据库 ID 后启用同步";

    // Navigation callbacks
    public Action<string>? RequestNavigate { get; set; }

    private readonly DispatcherTimer _secondTimer;
    private int _heroCarouselTick;   // 秒计数，用于多游戏轮播（每 5 秒切换）
    private int _heroCarouselIndex;  // 当前轮播到的会话下标
    private string? _activeGamePlatform;
    private string? _activeGamePlatformId;
    private string? _activeGameExePath;
    private bool _coverRefreshQueued;

    public HomeViewModel(
        IDatabaseRepository repo,
        GameSessionManager sessionManager,
        INotionSyncService syncService,
        CoverCacheService coverCache)
    {
        _repo = repo;
        _sessionManager = sessionManager;
        _syncService = syncService;
        _coverCache = coverCache;
        _matcher = new GameMatcher();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _secondTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _secondTimer.Tick += OnSecondTimerTick;

        _sessionManager.SessionStarted += OnSessionStarted;
        _sessionManager.SessionHeartbeat += OnSessionHeartbeat;
        _sessionManager.SessionEnded += OnSessionEnded;
        _syncService.SyncStatusChanged += OnSyncStatusChanged;
        _coverCache.CoverDownloaded += OnCoverDownloaded;
    }

    private void OnSecondTimerTick(object? sender, object e)
    {
        if (!IsGameRunning) return;

        var sessions = _sessionManager.GetActiveSessions()
            .OrderByDescending(s => s.StartTime)
            .ToList();
        if (sessions.Count == 0) return;

        // 多游戏同时运行：每 5 秒轮播切换 Hero 卡到下一个正在运行的游戏。
        // 会话列表可能已变化（游戏退出/新开），所以按 Pid 对齐当前 index。
        if (sessions.Count > 1 && ++_heroCarouselTick % 5 == 0)
        {
            _heroCarouselIndex = (_heroCarouselIndex + 1) % sessions.Count;
            _ = RefreshHeroCardAsync(sessions[_heroCarouselIndex % sessions.Count]);
            return;
        }

        var active = sessions.Count > 1
            ? sessions[_heroCarouselIndex % sessions.Count]
            : sessions[0];
        if (active != null)
        {
            var elapsed = Math.Max(active.DurationSeconds, (int)(DateTime.Now - active.StartTime).TotalSeconds);
            CurrentGameTimer = DailyAggregator.FormatSeconds(elapsed);

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

            // 注意：这里不再为 CurrentGameHeroBackgroundUrl 做任何本地图回退。
            // 背景图只认 Notion 总表的 cover，没有就保持原样（见 RefreshHeroCardAsync 的说明）。
        }
    }

    [RelayCommand]
    public async Task RefreshAllDataAsync()
    {
        var today = DateTime.Today;
        // Range covers at least 14 weeks back to include full 84 days of heatmap
        var startRange = today.AddDays(-100).ToString("yyyy-MM-dd");
        var endRange = today.AddDays(7).ToString("yyyy-MM-dd");

        // 1. Fetch data asynchronously
        var allSummaries = await _repo.GetDailySummariesRangeAsync(startRange, endRange);
        var stats = DailyAggregator.Aggregate(today, allSummaries);
        var recents = await _repo.GetRecentDailyRecordsAsync(5);

        // Precompute today's games (Top 3 only)
        var newTodayGames = new List<GameListItemViewModel>();
        foreach (var g in stats.TodayTopGames.Take(3))
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
            TodayDurationText = stats.TodayDurationText;
            TodayDeltaText = stats.TodayDeltaText;
            WeekDurationText = stats.WeekDurationText;
            WeekDeltaText = stats.WeekDeltaText;

            var streak = Math.Max(stats.StreakDays, 1);
            StreakDays = streak;

            if (streak <= 5)
            {
                StreakRankTitle = "Lv.1 愿望单收集家";
                NextMilestoneText = "下一个里程碑：6 天 (Lv.2)";
                NextMilestoneTarget = 6;
            }
            else if (streak <= 15)
            {
                StreakRankTitle = "Lv.2 背包塞满草药的玩家";
                NextMilestoneText = "下一个里程碑：16 天 (Lv.3)";
                NextMilestoneTarget = 16;
            }
            else if (streak <= 30)
            {
                StreakRankTitle = "Lv.3 掌握彩蛋位置的知情人";
                NextMilestoneText = "下一个里程碑：31 天 (Lv.4)";
                NextMilestoneTarget = 31;
            }
            else if (streak <= 52)
            {
                StreakRankTitle = "Lv.4 全成就全收集狂人";
                NextMilestoneText = "下一个里程碑：53 天 (Lv.5)";
                NextMilestoneTarget = 53;
            }
            else
            {
                StreakRankTitle = "Lv.5 传说的无伤通关者";
                NextMilestoneText = "已达成最高段位！传奇缔造中";
                NextMilestoneTarget = streak;
            }

            HeatmapActiveDaysText = $"游戏时长记录 {activeDays} 天";
            CurrentMonthHeader = today.ToString("yyyy年M月");
            ActivityHeatmap = stats.ActivityHeatmap;

            TodayGames.Clear();
            foreach (var item in newTodayGames)
            {
                TodayGames.Add(item);
            }

            RecentRecords.Clear();
            foreach (var item in newRecents)
            {
                RecentRecords.Add(item);
            }
        });

        // 3. Update Hero Card
        await RefreshHeroCardAsync();
    }

    public async Task RefreshHeroCardAsync(GameSession? active = null)
    {
        active ??= _sessionManager.GetActiveSessions().OrderByDescending(s => s.StartTime).FirstOrDefault();
        GameRecord? activeGame = null;
        string? activeCover = null;
        string? activeHeroBg = null;

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
                    var hit = _matcher.MatchGame(activeGame.Name, catalogItems, activeGame.PlatformId)
                                      .FirstOrDefault(c => c.MatchType != "fuzzy_candidate");
                    var matched = hit == null
                        ? null
                        : catalogItems.FirstOrDefault(c => c.PageId == hit.PageId);

                    if (matched != null && !string.IsNullOrEmpty(matched.CoverUrl))
                    {
                        activeHeroBg = matched.CoverUrl;
                        activeCover ??= matched.CoverUrl;
                    }
                }

                // 【设计约定】环境背景图只认 Notion 总表的 cover：
                // 总表有 cover 就用它的 URL，没有就保持原样、不铺任何图。
                // 因此这里刻意不再回退到 games.cover_url / 本地 SplashScreenImage / 本地图标封面
                // —— 用本地图标撑满整块背景会变成一团模糊色块，与设计不符。
                // 背景缺失时由 XAML 上的 Visibility 绑定自动隐藏。
                activeCover ??= activeHeroBg;
            }
        }

        _dispatcherQueue.TryEnqueue(() =>
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

                var elapsed = Math.Max(active.DurationSeconds, (int)(DateTime.Now - active.StartTime).TotalSeconds);
                CurrentGameTimer = DailyAggregator.FormatSeconds(elapsed);

                CurrentGameCoverPath = activeCover;
                CurrentGameHeroBackgroundUrl = activeHeroBg;
                CurrentGameStoreUrl = BuildSteamStoreUrl(activeGame.Platform, activeGame.PlatformId);

                if (CurrentGameCoverPath == null && !string.IsNullOrEmpty(activeGame.ExecutablePath))
                {
                    _ = _coverCache.EnsureCoverAsync(activeGame.Platform, activeGame.PlatformId, activeGame.ExecutablePath);
                }

                if (!_secondTimer.IsEnabled)
                {
                    _secondTimer.Start();
                }
            }
            else
            {
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
        });
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
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_secondTimer.IsEnabled) _secondTimer.Start();
        });
        _ = RefreshHeroCardAsync(session);
        _ = RefreshAllDataAsync();
    }

    private void OnSessionHeartbeat(object? sender, GameSession session)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsGameRunning)
            {
                var elapsed = Math.Max(session.DurationSeconds, (int)(DateTime.Now - session.StartTime).TotalSeconds);
                CurrentGameTimer = DailyAggregator.FormatSeconds(elapsed);
            }
        });
    }

    private void OnSessionEnded(object? sender, GameSession session)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _secondTimer.Stop();
            CurrentGameTimer = "00:00:00";
        });
        _ = RefreshHeroCardAsync(null);
        _ = RefreshAllDataAsync();
    }

    private void OnSyncStatusChanged(object? sender, string status)
    {
        // 同步链所有关键节点（各步骤、删除对账结果、失败原因）都经此事件上报，统一落日志供 QA 排查。
        Infrastructure.AppLog.Info($"[同步] {status}");
        _dispatcherQueue.TryEnqueue(() =>
        {
            NotionStatusText = status;
            NotionLastSyncText = $"上次同步 {DateTime.Now:HH:mm}";
        });
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
