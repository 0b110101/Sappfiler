using System.Runtime.InteropServices;
using Windows.Graphics;
using GameTimeTracker.App.Tray;
using GameTimeTracker.App.ViewModels;
using GameTimeTracker.App.Views;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure;
using GameTimeTracker.Infrastructure.Covers;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.Notion;
using GameTimeTracker.Infrastructure.Platforms;
using GameTimeTracker.Infrastructure.Process;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace GameTimeTracker.App;

public sealed partial class MainWindow : Window
{
    private AppWindow? _appWindow;
    private IntPtr _hwnd;
    private readonly IDatabaseRepository _repo;
    private readonly IProcessMonitor _processMonitor;
    private readonly GameLibraryManager _gameLibrary;
    private readonly GameSessionManager _sessionManager;
    private readonly CoverCacheService _coverCache;
    private readonly INotionClient _notionClient;
    private readonly INotionSyncService _syncService;
    private readonly TrackerConfig _config;

    private readonly HomeViewModel _homeViewModel;
    private SystemTrayService? _trayService;

    /// <summary>
    /// 「已最小化到系统托盘」这条提示**每次运行只弹一次**。
    /// 每关一次窗口就弹一遍会很烦（2026-09-19 雾山反馈），
    /// 但一次都不弹也不好 —— 新用户不知道程序还在后台跑。
    /// </summary>
    private bool _minimizeNoticeShown;
    private SubclassProc? _subclassProc;
    private CancellationTokenSource? _monitorCts;

    // 游戏库重扫节流：安装/更新发生在程序启动之后的游戏，
    // 若只依赖启动时那一次扫描，将永远无法被识别。
    private static readonly TimeSpan LibraryRefreshInterval = TimeSpan.FromMinutes(30);
    private DateTime _lastLibraryRefresh = DateTime.UtcNow;

    // Cached page instances for 0ms instantaneous navigation
    private HomePage? _homePage;
    private HistoryPage? _historyPage;
    private PendingPage? _pendingPage;
    private MappingsPage? _mappingsPage;
    private SettingsPage? _settingsPage;
    private string _currentNav = "Home";
    private string _navToRestore = "Home";

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private const uint WM_GETMINMAXINFO = 0x0024;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    public static MainWindow? CurrentWindow { get; private set; }

    /// <summary>
    /// 供设置页使用：新用户第一次保存 Notion 配置成功后，立刻跑一遍完整同步链。
    /// 不需要新开窗口引用 —— 设置页通过 CurrentWindow 拿。
    /// </summary>
    public INotionSyncService SyncService => _syncService;

    /// <summary>
    /// 首次配置成功后的一次性同步：目录 → 对账 → 自动关联 → 拉取 → 回填 → 推送 → 回刷标题。
    /// 顺序与启动链/周期链保持一致，避免"拉取没跑就推送"造成重复行。
    /// 进度通过 SyncService.SyncStatusChanged 事件反馈（侧边栏状态条已经在监听）。
    /// </summary>
    public async Task RunInitialSyncAsync()
    {
        if (!_config.IsNotionConfigured) return;

        await _syncService.RefreshGameCatalogCacheAsync();
        await _syncService.ReconcileNotionDeletionsAsync();
        await _syncService.AutoLinkGamesFromCatalogAsync();
        await _syncService.PullDailyRecordsFromNotionAsync();
        await _syncService.BackfillRelationsAsync();
        await _syncService.SyncPendingDailyRecordsAsync();
        await _syncService.RefreshDailyTitlesFromMasterAsync();
        _ = _coverCache.EnsureLibraryCoversAsync(_repo);
        await _homeViewModel.RefreshAllDataAsync();
    }

    public void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }

        ApplyCaptionButtonColors(theme);
    }

    /// <summary>
    /// 右上角最小化/最大化/关闭按钮由 AppWindowTitleBar 绘制，不会自动跟随 RequestedTheme，
    /// 浅色模式下容易残留浅色前景导致「看不见」。这里按主题显式着色。
    /// </summary>
    private void ApplyCaptionButtonColors(ElementTheme theme)
    {
        if (_appWindow is null) return;
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var titleBar = _appWindow.TitleBar;
        if (titleBar is null) return;

        var isDark = theme == ElementTheme.Dark ||
                     (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        var glyph = isDark
            ? Windows.UI.Color.FromArgb(255, 0xE6, 0xE6, 0xF2)
            : Windows.UI.Color.FromArgb(255, 0x1F, 0x2A, 0x3A);
        var hoverBg = isDark
            ? Windows.UI.Color.FromArgb(255, 0x2A, 0x2D, 0x4E)
            : Windows.UI.Color.FromArgb(255, 0xE2, 0xE8, 0xF0);
        var pressedBg = isDark
            ? Windows.UI.Color.FromArgb(255, 0x33, 0x37, 0x5E)
            : Windows.UI.Color.FromArgb(255, 0xD2, 0xDA, 0xE6);

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = glyph;
        titleBar.ButtonInactiveForegroundColor = glyph;
        titleBar.ButtonHoverBackgroundColor = hoverBg;
        titleBar.ButtonHoverForegroundColor = glyph;
        titleBar.ButtonPressedBackgroundColor = pressedBg;
        titleBar.ButtonPressedForegroundColor = glyph;
    }

    public MainWindow()
    {
        CurrentWindow = this;
        InitializeComponent();

        // 1. Setup Window Size & Titlebar
        SetupWindowGeometry();

        // 2. Instantiate Backend Services
        _config = new TrackerConfig();
        _repo = new SqliteRepository(_config.DbPath);
        _processMonitor = new Win32ProcessMonitor();
        _gameLibrary = new GameLibraryManager();
        _gameLibrary.Refresh();
        _sessionManager = new GameSessionManager(_repo);
        _coverCache = new CoverCacheService();
        _notionClient = new NotionClient(_config.NotionToken);
        _syncService = new NotionSyncService(_repo, _notionClient, _config);

        // 3. Create ViewModel
        _homeViewModel = new HomeViewModel(_repo, _sessionManager, _syncService, _coverCache);
        _homeViewModel.RequestNavigate = (target) =>
        {
            DispatcherQueue.TryEnqueue(() => NavigateTo(target));
        };

        // 4. Initialize System Tray & Subclass WndProc
        InitializeSystemTray();

        // 5. Navigate to HomePage
        NavigateTo("Home");

        // 6. Initialize Services and start background monitoring loop
        _ = InitializeAndStartMonitoringAsync();
    }

    private void SetupWindowGeometry()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (_appWindow != null)
        {
            _appWindow.Title = "GameTime Tracker";
            _appWindow.Resize(new SizeInt32(1200, 780));

            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                _appWindow.SetIcon(iconPath);
            }

            var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            if (display != null)
            {
                var x = (display.WorkArea.Width - 1200) / 2;
                var y = (display.WorkArea.Height - 780) / 2;
                _appWindow.Move(new PointInt32(Math.Max(0, x), Math.Max(0, y)));
            }

            // 关闭窗口 = 收进托盘：只 Hide，**不释放页面视觉树**。
            //
            // ⚠️ 这里曾调用 ReleaseVisualTree()（把 ContentFrame.Content 置空并丢掉
            //    页面引用），目的是隐藏期间降常驻内存。2026-09-19 判定必须放弃：
            //    它会让「关界面 → 托盘打开」随机闪退，且**日志里一行都没有**。
            //
            //    机制（这是关键，不要再"优化"回去）：
            //    · 页面被从可视树上摘掉后，各 ViewModel 仍然强引用着页面 ——
            //      x:Bind 生成的绑定会把 PropertyChanged 处理器挂在 ViewModel 上，
            //      所以 _homePage = null 并不能真正释放它，只是让它"脱离可视树"。
            //    · 而后台同步/刷新随时会驱动那些绑定去更新元素
            //      （日志实测：拆完树的 60ms 后仍在打 `[同步] 已从 Notion 同步 9 条记录`）。
            //    · 更新一个 peer 已销毁的元素，WinRT 投影层解析 ABI 指针时踩空。
            //
            //    症状：崩溃在 CoreMessagingXP.dll，异常码 0xc000027b
            //    （STATUS_STOWED_EXCEPTION），WER 签名 combase.dll / 80004005。
            //    属于**原生层**崩溃，App 的 UnhandledException 与
            //    AppDomain.UnhandledException 都接不到 —— 所以日志空白。
            //
            //    代价：隐藏期间当前页面留在内存里（主要是热力图那张卡片）。
            //    这是刻意的取舍 —— 稳定性优先，且页面本就随导航常驻。
            _appWindow.Closing += (sender, args) =>
            {
                args.Cancel = true;
                // 面包屑：崩溃是原生层的，这几行是判断"死在哪个阶段"的唯一线索。
                AppLog.Info("[窗口] 收到关闭请求，收进托盘");
                _appWindow.Hide();

                // 提示只弹一次：关窗口是用户主动行为，不需要每次都被告知一遍。
                if (!_minimizeNoticeShown)
                {
                    _minimizeNoticeShown = true;
                    _trayService?.ShowNotification("GameTimeTracker", "已最小化到系统托盘，后台持续统计游戏时长。");
                }

                AppLog.Info("[窗口] 已隐藏（保留页面，后台刷新可安全更新）");
            };

            // 切回窗口时顺手检查一次 Notion 侧的删除（节流 60 秒），
            // 否则删完 Notion 切回程序要等最长 15 分钟才反应。
            // 注意 Activated 是 Window 的事件，AppWindow 上没有。
            Activated += OnMainWindowActivated;
        }

        // 主题设置缺失时也要保证标题栏按钮可见
        ApplyCaptionButtonColors(ElementTheme.Default);
    }

    /// <summary>
    /// 窗口重新获得焦点时，节流地检查一次 Notion 侧有没有删除。
    /// 删除对账本来只在启动和 15 分钟周期里跑，用户在 Notion 删完再切回程序
    /// 往往等不到那一轮，看起来就像"没生效"。
    /// 初值设为当前时间：启动那一次窗口激活会让启动链已经在做的事重复一遍，没必要。
    /// </summary>
    private DateTime _lastNotionChangeCheck = DateTime.UtcNow;
    private static readonly TimeSpan NotionChangeCheckThrottle = TimeSpan.FromSeconds(60);

    private async void OnMainWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        if (!_config.IsNotionConfigured) return;
        if (DateTime.UtcNow - _lastNotionChangeCheck < NotionChangeCheckThrottle) return;

        _lastNotionChangeCheck = DateTime.UtcNow;

        try
        {
            // 顺序同启动链：先刷新总表快照，再对账删除。
            await _syncService.RefreshGameCatalogCacheAsync();
            var result = await _syncService.ReconcileNotionDeletionsAsync();
            if (result.HasChanges)
            {
                await _homeViewModel.RefreshAllDataAsync();
                if (_historyPage != null) await _historyPage.RefreshAsync();
                if (_pendingPage != null) await _pendingPage.RefreshAsync();
                if (_mappingsPage != null) await _mappingsPage.RefreshAsync();
            }
        }
        catch
        {
            // 后台检查失败不该打扰用户，下一轮周期同步会兜住
        }
    }

    private void InitializeSystemTray()
    {
        if (_appWindow == null) return;

        _trayService = new SystemTrayService(_appWindow, _hwnd, _sessionManager, _repo, _syncService);
        _trayService.OnOpenSettings = () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NavigateTo("Settings");
            });
        };

        // 从托盘恢复窗口时，重建之前被释放的页面
        _trayService.OnWindowShown = () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 面包屑：这条路径曾静默崩溃（原生层），逐步记录才能定位。
                var hasContent = ContentFrame.Content is not null;
                AppLog.Info($"[托盘] 恢复界面开始（有内容={hasContent} _currentNav=\"{_currentNav}\" _navToRestore=\"{_navToRestore}\"）");

                // ⚠️ 判断依据是**内容是否真的没了**，不是 _currentNav。
                // _currentNav 只表示"上次导航到哪"，内容被释放之后它可能仍留着旧值，
                // 拿它当条件就会跳过重建、界面白白空着（2026-09-19 的白屏就是这么来的）。
                if (!hasContent)
                {
                    var tag = string.IsNullOrEmpty(_navToRestore) ? "Home" : _navToRestore;
                    AppLog.Info($"[托盘] 内容已释放，重建页面：{tag}");
                    NavigateTo(tag);
                }
                AppLog.Info("[托盘] 恢复界面结束");
            });
        };

        // Subclass window procedure to receive WM_TRAYICON notifications
        _subclassProc = new SubclassProc(WndProc);
        SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)101, UIntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == SystemTrayService.WM_TRAYICON)
        {
            _trayService?.HandleTrayMessage((int)lParam);
            return IntPtr.Zero;
        }
        if (uMsg == WM_GETMINMAXINFO)
        {
            var minMax = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            minMax.ptMinTrackSize.x = 980;
            minMax.ptMinTrackSize.y = 640;
            Marshal.StructureToPtr(minMax, lParam, true);
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private async Task InitializeAndStartMonitoringAsync()
    {
        await _sessionManager.InitializeAsync();
        await _sessionManager.CleanupZombieSessionsAsync();

        // 1. Load Notion credentials & DB IDs from SQLite database
        try
        {
            var token = await _repo.GetSettingAsync("notion_token");
            var gameDb = await _repo.GetSettingAsync("game_database_id");
            var dailyDb = await _repo.GetSettingAsync("daily_database_id");

            // 设置页保存的值可能带首尾空白（复制粘贴常见），统一修剪后再使用
            if (!string.IsNullOrEmpty(token))
            {
                _config.NotionToken = token.Trim();
                _notionClient.UpdateToken(token.Trim());
            }
            if (!string.IsNullOrEmpty(gameDb)) _config.GameDatabaseId = gameDb.Trim();
            if (!string.IsNullOrEmpty(dailyDb)) _config.DailyDatabaseId = dailyDb.Trim();

            if (_config.IsNotionConfigured)
            {
                _ = Task.Run(async () =>
                {
                    // 顺序很关键：先把总表目录拉下来，自动关联才有数据可匹配；
                    // 删除对账要排在目录刷新之后（它以 game_catalog 作为总表的快照），
                    // 且必须早于 Pull —— 否则被删游戏的每日记录又会被拉回来。
                    await _syncService.RefreshGameCatalogCacheAsync();
                    await _syncService.ReconcileNotionDeletionsAsync();
                    await _syncService.AutoLinkGamesFromCatalogAsync();
                    await _syncService.PullDailyRecordsFromNotionAsync();
                    await _syncService.BackfillRelationsAsync();
                    await _syncService.SyncPendingDailyRecordsAsync();
                    // 回刷必须在推送之后：这轮刚推上去的记录此时才有 notion_title 快照。
                    await _syncService.RefreshDailyTitlesFromMasterAsync();
                    _ = _coverCache.EnsureLibraryCoversAsync(_repo);
                    await _homeViewModel.RefreshAllDataAsync();
                });
            }
        }
        catch { }

        try
        {
            var existingGames = await _repo.GetAllGamesAsync();
            foreach (var g in existingGames)
            {
                if (g.Status != "ignored" && !string.IsNullOrWhiteSpace(g.ExecutablePath))
                {
                    _gameLibrary.AddManualGame(g.Name, g.ExecutablePath);
                }
            }
        }
        catch { }

        try
        {
            var themeMode = await _repo.GetSettingAsync("theme_mode");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (themeMode == "Dark") ApplyTheme(ElementTheme.Dark);
                else if (themeMode == "Light") ApplyTheme(ElementTheme.Light);
            });
        }
        catch { }

        await _homeViewModel.RefreshAllDataAsync();

        _syncService.SyncStatusChanged += (s, status) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SidebarNotionStatus.Text = status;
                SidebarNotionTime.Text = $"更新于 {DateTime.Now:HH:mm}";
            });
        };

        await UpdatePendingBadgeAsync();
        if (_trayService != null) await _trayService.RefreshStateAsync();

        // Start background 5s process monitor loop
        _monitorCts = new CancellationTokenSource();
        _ = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));

        // Start background periodic Notion sync loop (every 15 min)
        _ = Task.Run(() => PeriodicSyncLoopAsync(_monitorCts.Token));
    }

    private async Task PeriodicSyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _config.SyncIntervalMinutes)), ct);
                if (_config.IsNotionConfigured)
                {
                    // 周期内也刷新总表目录，否则运行期间新加到 Notion 的游戏永远不会被关联
                    await _syncService.RefreshGameCatalogCacheAsync();
                    await _syncService.ReconcileNotionDeletionsAsync();
                    await _syncService.AutoLinkGamesFromCatalogAsync();
                    await _syncService.PullDailyRecordsFromNotionAsync();
                    await _syncService.BackfillRelationsAsync();
                    await _syncService.SyncPendingDailyRecordsAsync();
                    // 总表改名的回刷：拉取只同步时长，不会碰标题，所以每轮都要补这一下。
                    await _syncService.RefreshDailyTitlesFromMasterAsync();
                    _ = _coverCache.EnsureLibraryCoversAsync(_repo);
                    await _homeViewModel.RefreshAllDataAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Silent tolerance in background periodic sync loop
            }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 0. Clean up zombie sessions (processes terminated externally)
                await _sessionManager.CleanupZombieSessionsAsync();

                // 0.5 周期重扫游戏库（30 分钟），让运行期间新装/刚更新的游戏也能被识别
                if (DateTime.UtcNow - _lastLibraryRefresh >= LibraryRefreshInterval)
                {
                    _lastLibraryRefresh = DateTime.UtcNow;
                    try { _gameLibrary.Refresh(); } catch { }
                }

                // 1. Scan running processes
                var processes = await _processMonitor.ScanRunningProcessesAsync();

                // 2. Identify active games
                var activePids = new HashSet<int>();
                foreach (var proc in processes)
                {
                    var identity = _gameLibrary.DetectGame(proc);
                    if (identity == null)
                    {
                        var existing = await _repo.GetGameByPathOrExeAsync(proc.ExecutablePath, proc.ProcessName + ".exe");
                        if (existing != null && existing.Status != "ignored")
                        {
                            identity = new GameIdentity(
                                string.IsNullOrWhiteSpace(existing.Platform) ? "manual" : existing.Platform,
                                string.IsNullOrWhiteSpace(existing.PlatformId) ? existing.Id.ToString() : existing.PlatformId,
                                existing.Name,
                                proc.ProcessName + ".exe",
                                proc.ExecutablePath
                            );
                            try { _gameLibrary.AddManualGame(existing.Name, proc.ExecutablePath); } catch { }
                        }
                    }

                    if (identity != null)
                    {
                        activePids.Add(proc.Pid);
                        var game = await _repo.GetOrCreateGameAsync(identity);

                        // Ensure cover cache is triggered
                        _ = _coverCache.EnsureCoverAsync(identity.Platform, identity.PlatformId, identity.ExecutablePath);

                        // Start or heartbeat session
                        var activeSessions = _sessionManager.GetActiveSessions();
                        if (!activeSessions.Any(s => s.Pid == proc.Pid))
                        {
                            // 只在**开始计时**时记一条。这个循环 5 秒跑一次，
                            // 心跳不能记，否则日志会被刷爆。
                            AppLog.Info($"[计时] 开始记录「{game.Name}」({identity.Platform}/{identity.PlatformId}) pid={proc.Pid}");
                            await _sessionManager.StartSessionAsync(game, proc);
                        }
                        else
                        {
                            await _sessionManager.HeartbeatSessionAsync(proc.Pid);
                        }
                    }
                }

                // 3. Check ended sessions
                var currentActive = _sessionManager.GetActiveSessions();
                foreach (var s in currentActive)
                {
                    if (!activePids.Contains(s.Pid))
                    {
                        AppLog.Info($"[计时] 结束记录 pid={s.Pid}");
                        await _sessionManager.EndSessionAsync(s.Pid);
                    }
                }

                // 4. Update badge and tray state
                await UpdatePendingBadgeAsync();
                if (_trayService != null) await _trayService.RefreshStateAsync();
            }
            catch
            {
                // Silent error tolerance in background thread
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.ProcessScanIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task UpdatePendingBadgeAsync()
    {
        try
        {
            var pending = await _repo.GetPendingGamesAsync();
            var count = pending.Count(p => p.Status != "ignored");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (count > 0)
                {
                    PendingBadgeBorder.Visibility = Visibility.Visible;
                    PendingBadgeText.Text = count > 99 ? "99+" : count.ToString();
                }
                else
                {
                    PendingBadgeBorder.Visibility = Visibility.Collapsed;
                }
            });
        }
        catch
        {
        }
    }

    // ----------------- Sidebar Navigation -----------------

    private void OnNavPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag && tag != _currentNav)
        {
            b.Background = (Brush)Application.Current.Resources["NavHoverBrush"];
        }
    }

    private void OnNavPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag && tag != _currentNav)
        {
            b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void SetActiveNav(string tag)
    {
        _currentNav = tag;
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var activeBrush = (Brush)Application.Current.Resources["NavActiveBrush"];
        var accentBrush = (Brush)Application.Current.Resources["AccentBlueBrush"];
        var textPrimary = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        var textSecondary = (Brush)Application.Current.Resources["TextSecondaryBrush"];

        NavHomeBtn.Background = tag == "Home" ? activeBrush : transparent;
        NavHomeIndicator.Visibility = tag == "Home" ? Visibility.Visible : Visibility.Collapsed;
        NavHomeIcon.Foreground = tag == "Home" ? accentBrush : textSecondary;
        NavHomeText.Foreground = tag == "Home" ? textPrimary : textSecondary;
        NavHomeText.FontWeight = tag == "Home" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        NavHistoryBtn.Background = tag == "History" ? activeBrush : transparent;
        NavHistoryIndicator.Visibility = tag == "History" ? Visibility.Visible : Visibility.Collapsed;
        NavHistoryIcon.Foreground = tag == "History" ? accentBrush : textSecondary;
        NavHistoryText.Foreground = tag == "History" ? textPrimary : textSecondary;
        NavHistoryText.FontWeight = tag == "History" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        NavPendingBtn.Background = tag == "Pending" ? activeBrush : transparent;
        NavPendingIndicator.Visibility = tag == "Pending" ? Visibility.Visible : Visibility.Collapsed;
        NavPendingIcon.Foreground = tag == "Pending" ? accentBrush : textSecondary;
        NavPendingText.Foreground = tag == "Pending" ? textPrimary : textSecondary;
        NavPendingText.FontWeight = tag == "Pending" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        NavMappingsBtn.Background = tag == "Mappings" ? activeBrush : transparent;
        NavMappingsIndicator.Visibility = tag == "Mappings" ? Visibility.Visible : Visibility.Collapsed;
        NavMappingsIcon.Foreground = tag == "Mappings" ? accentBrush : textSecondary;
        NavMappingsText.Foreground = tag == "Mappings" ? textPrimary : textSecondary;
        NavMappingsText.FontWeight = tag == "Mappings" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        NavSettingsBtn.Background = tag == "Settings" ? activeBrush : transparent;
        NavSettingsIndicator.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        NavSettingsIcon.Foreground = tag == "Settings" ? accentBrush : textSecondary;
        NavSettingsText.Foreground = tag == "Settings" ? textPrimary : textSecondary;
        NavSettingsText.FontWeight = tag == "Settings" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void NavigateTo(string tag)
    {
        // 逐步面包屑：这条路径在"从托盘恢复"时静默崩溃过（原生层，无堆栈可看），
        // 只能靠"日志停在哪一行"定位死在哪个调用上。2026-09-19 加，定位完也不要删。
        AppLog.Info($"[导航] 开始 → {tag}");

        // 1. Instant visual response on UI (0ms)
        SetActiveNav(tag);
        AppLog.Info($"[导航] 高亮已更新 → {tag}");

        // 2. Instant page display from cache
        switch (tag)
        {
            case "Home":
                AppLog.Info("[导航] 构建 HomePage…");
                _homePage ??= new HomePage { ViewModel = _homeViewModel };
                AppLog.Info("[导航] HomePage 就绪 → 设置 ContentFrame.Content");
                ContentFrame.Content = _homePage;
                AppLog.Info("[导航] ContentFrame 已设置 → 触发数据刷新");
                _ = _homeViewModel.RefreshAllDataAsync();
                AppLog.Info("[导航] 完成 → Home");
                break;
            case "History":
                if (_historyPage == null)
                {
                    ContentFrame.Navigate(typeof(HistoryPage), (_repo, _coverCache, _syncService));
                    _historyPage = ContentFrame.Content as HistoryPage;
                }
                else
                {
                    ContentFrame.Content = _historyPage;
                }
                _ = _historyPage?.RefreshAsync();
                break;
            case "Pending":
                if (_pendingPage == null)
                {
                    ContentFrame.Navigate(typeof(PendingPage), (_repo, _syncService, _config, _notionClient, _coverCache));
                    _pendingPage = ContentFrame.Content as PendingPage;
                    if (_pendingPage != null)
                    {
                        _pendingPage.OnPendingCountChanged = () => _ = UpdatePendingBadgeAsync();
                    }
                }
                else
                {
                    ContentFrame.Content = _pendingPage;
                }
                _ = _pendingPage?.RefreshAsync();
                break;
            case "Mappings":
                if (_mappingsPage == null)
                {
                    ContentFrame.Navigate(typeof(MappingsPage), (_repo, _syncService));
                    _mappingsPage = ContentFrame.Content as MappingsPage;
                }
                else
                {
                    ContentFrame.Content = _mappingsPage;
                }
                _ = _mappingsPage?.RefreshAsync();
                break;
            case "Settings":
                if (_settingsPage == null)
                {
                    ContentFrame.Navigate(typeof(SettingsPage), (_repo, _config, _notionClient));
                    _settingsPage = ContentFrame.Content as SettingsPage;
                }
                else
                {
                    ContentFrame.Content = _settingsPage;
                }
                break;
        }
    }

    private async void OnManualAddGameClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Dialogs.ManualAddGameDialog(_hwnd)
            {
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var name = dialog.GameName;
                var path = dialog.GamePath;
                var installed = _gameLibrary.AddManualGame(name, path);
                var exeName = System.IO.Path.GetFileName(installed.ExePath ?? path);
                var identity = new GameIdentity("manual", installed.PlatformId, name, exeName, installed.ExePath ?? path);
                var game = await _repo.GetOrCreateGameAsync(identity);
                if (!string.IsNullOrWhiteSpace(installed.ExePath))
                {
                    _ = _coverCache.EnsureCoverAsync("manual", installed.PlatformId, installed.ExePath);
                }
                _trayService?.ShowNotification("添加成功", $"已成功添加游戏: {name}");
                _ = _mappingsPage?.RefreshAsync();
                _ = _homeViewModel.RefreshAllDataAsync();
            }
        }
        catch (Exception ex)
        {
            var errDlg = new ContentDialog
            {
                Title = "添加失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = this.Content.XamlRoot
            };
            _ = errDlg.ShowAsync();
        }
    }

    private void OnOpenNotionClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            string targetUrl = "https://www.notion.so";
            var cleanDb = _config.DailyDatabaseId?.Replace("-", "");
            if (!string.IsNullOrWhiteSpace(cleanDb))
            {
                targetUrl = $"https://www.notion.so/{cleanDb}";
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(targetUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void OnNavHomeClicked(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateTo("Home");
    private void OnNavHistoryClicked(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateTo("History");
    private void OnNavPendingClicked(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateTo("Pending");
    private void OnNavMappingsClicked(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateTo("Mappings");
    private void OnNavSettingsClicked(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateTo("Settings");
}
