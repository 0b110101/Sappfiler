using System.Runtime.InteropServices;
using Windows.Graphics;
using GameTimeTracker.App.Tray;
using GameTimeTracker.App.ViewModels;
using GameTimeTracker.App.Views;
using GameTimeTracker.Core.Backup;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure;
using GameTimeTracker.Infrastructure.Artwork;
using GameTimeTracker.Infrastructure.Covers;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.DeviceSync;
using GameTimeTracker.Infrastructure.Notion;
using GameTimeTracker.Infrastructure.Platforms;
using GameTimeTracker.Infrastructure.Process;
using GameTimeTracker.Infrastructure.Secrets;
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
    private readonly IGameArtworkService _artworkService;
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

    // ===== 设备同步（WebDAV）=====
    // ⚠️ 与 5 秒的 MonitorLoopAsync **完全独立**：本地游戏监听绝不等待网络。
    private DeviceSyncService? _deviceSyncService;
    private CancellationTokenSource? _syncLoopCts;
    private DateTime _lastSessionEndSyncAt = DateTime.MinValue;

    /// <summary>会话结束触发同步的去抖窗口（≥5 分钟，避免"每局一次请求"）。</summary>
    private static readonly TimeSpan SessionEndSyncDebounce = TimeSpan.FromMinutes(5);

    // ===== 监听循环异常日志的**去重抑制**（2026-10-12）=====
    // 同一个不可恢复的错误（实机例：(platform, platform_id) 被一条已归档行占用）每 5 秒复现一次，
    // 无条件记录会把 app.log 刷爆、反而把真正的新问题淹没。
    // ⚠️ 抑制的只是**重复日志**，不是错误本身：首次立即记录，之后按间隔带"累计次数"记录，
    //    绝不会让用户误以为计时成功（本地计时是否真的在记录另有权威判据：sessions 表）。
    private static readonly TimeSpan MonitorErrorLogInterval = TimeSpan.FromMinutes(10);
    private string _monitorErrorSignature = "";
    private int _monitorErrorRepeatCount;
    private DateTime _monitorErrorFirstSeenUtc = DateTime.MinValue;
    private DateTime _monitorErrorLastLoggedUtc = DateTime.MinValue;

    // 游戏库重扫节流：安装/更新发生在程序启动之后的游戏，
    // 若只依赖启动时那一次扫描，将永远无法被识别。
    private static readonly TimeSpan LibraryRefreshInterval = TimeSpan.FromMinutes(30);
    private DateTime _lastLibraryRefresh = DateTime.UtcNow;

    // Cached page instances for 0ms instantaneous navigation
    private HomePage? _homePage;
    private HistoryPage? _historyPage;
    private StatsPage? _statsPage;
    private StatsViewModel? _statsViewModel;
    private PendingPage? _pendingPage;
    private MappingsPage? _mappingsPage;
    private SettingsPage? _settingsPage;
    private SyncPage? _syncPage;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private const uint MSGFLT_ALLOW = 1;

    private static readonly uint WM_ACTIVATE_INSTANCE = RegisterWindowMessage(App.SingleInstanceMsgName);

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
    /// <param name="ct">
    /// 可选的取消令牌。⚠️ 2026-10-13：只在**步骤之间**检查 —— 用户在同步期间改了 Notion 配置时，
    /// 旧链必须尽快停手，不能继续按旧配置的意图往下写（详见 SettingsPage 的首次同步逻辑）。
    /// 步骤**内部**不取消：那需要把 token 一路透传进同步服务，超出本次修复范围。
    /// </param>
    public async Task RunInitialSyncAsync(CancellationToken ct = default)
    {
        if (!_config.IsNotionConfigured) return;

        // 每个步骤执行前先看令牌：被取消就立刻收工（不抛异常，调用方按"未完成"处理）。
        async Task<bool> RunStepAsync(Func<Task> step)
        {
            if (ct.IsCancellationRequested)
            {
                AppLog.Info("[同步] 首次同步在步骤之间被取消（配置已变更）⇒ 提前结束，不再执行后续步骤");
                return false;
            }

            await step();
            return true;
        }

        if (!await RunStepAsync(() => _syncService.RefreshGameCatalogCacheAsync())) return;
        if (!await RunStepAsync(() => _syncService.ReconcileNotionDeletionsAsync())) return;
        if (!await RunStepAsync(() => _syncService.AutoLinkGamesFromCatalogAsync())) return;
        if (!await RunStepAsync(() => _syncService.PullDailyRecordsFromNotionAsync())) return;
        if (!await RunStepAsync(() => _syncService.BackfillRelationsAsync())) return;
        if (!await RunStepAsync(() => _syncService.SyncPendingDailyRecordsAsync())) return;

        if (ct.IsCancellationRequested)
        {
            AppLog.Info("[同步] 首次同步在步骤之间被取消（配置已变更）⇒ 跳过收尾刷新");
            return;
        }

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

        // 设备同步（WebDAV）：与 Notion Provider 那条流水线**完全独立**。
        // ⚠️ 用具体类型 SqliteRepository：设备同步层（引擎 / 播种 / Deferred 重处理）建立在它之上；
        //    运行期实例本来就是 SqliteRepository，这里是显式化而不是转型风险。
        _deviceSyncService = new DeviceSyncService(
            (SqliteRepository)_repo,
            new DpapiSecretStore(AppPaths.DataDir));

        var artworkProviders = new IGameArtworkProvider[]
        {
            new NotionArtworkProvider(_repo),
            new SteamArtworkProvider(),
            new EpicArtworkProvider(),
            new XboxArtworkProvider(),
            new EaArtworkProvider(),
            new UbisoftArtworkProvider(),
            new LocalGameArtworkProvider(),
            new SteamRemoteArtworkProvider(),
            new DefaultArtworkProvider()
        };
        _artworkService = new GameArtworkService(artworkProviders);
        _ = _artworkService.CleanExpiredCacheAsync(300L * 1024 * 1024);

        // 3. Create ViewModel
        // 传入设备同步门面：Notion 未配置但设备同步已启用时，今日页「立即同步」走 WebDAV
        // （复用同一个 DeviceSyncService，不复制同步实现）。
        _homeViewModel = new HomeViewModel(
            _repo, _sessionManager, _syncService, _coverCache, _artworkService, _deviceSyncService);
        _homeViewModel.RequestNavigate = (target) =>
        {
            DispatcherQueue.TryEnqueue(() => NavigateTo(target));
        };
        _statsViewModel = new StatsViewModel(_repo, _coverCache, _config);

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
            _appWindow.Title = "Sappfiler";

            // ── 初始窗口尺寸随显示器与 DPI 缩放自适应（适配大屏与小屏笔记本）──────────────
            // 原先直接按物理像素计算并在 1200 保底，导致在 14寸笔记本（150% 缩放，逻辑视口仅 1280）下
            // 物理 1200 像素相当于只有 800 逻辑像素，内容被严重挤压。
            // 现按 DPI 换算为逻辑像素（DIPs）自适应计算：
            //   · 紧凑小屏/笔记本高缩放 (逻辑宽 < 1400)：按工作区宽 84% / 高 86% 自适应
            //   · 大屏 / 标准 1080p 桌面屏 (逻辑宽 >= 1400)：保持经典 62% x 78% (约 1200x780)，上限 1720x1040
            var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);

            var winW = 1200;
            var winH = 780;
            if (display != null)
            {
                var workArea = display.WorkArea;
                uint dpi = GetDpiForWindow(_hwnd);
                double scale = (dpi > 0 ? dpi : 96) / 96.0;

                // 转为有效逻辑像素（DIPs）计算，统一不同屏幕尺寸和高 DPI 缩放下的视觉比例
                double workAreaDipsW = workArea.Width / scale;
                double workAreaDipsH = workArea.Height / scale;

                double targetDipsW;
                double targetDipsH;

                if (workAreaDipsW < 1400)
                {
                    // 紧凑小屏幕 / 笔记本高 DPI 缩放（如 13~14寸 1080p @ 125%~150%，有效逻辑宽仅 1280~1536）
                    // 给予更充裕的工作区占比，让内容在单/双列自适应下自然舒展
                    targetDipsW = Math.Clamp(workAreaDipsW * 0.84, 1000, 1180);
                    targetDipsH = Math.Clamp(workAreaDipsH * 0.86, 600, 860);
                }
                else
                {
                    // 大屏 / 标准 1080p 桌面屏（如 27寸 1080p @ 100%、2K、4K）
                    // 保持经典比例 62% x 78%，稳稳落在 1200x780 左右，大屏绝不发散
                    targetDipsW = Math.Clamp(workAreaDipsW * 0.62, 1200, 1720);
                    targetDipsH = Math.Clamp(workAreaDipsH * 0.78, 780, 1040);
                }

                // 转换回物理像素给 AppWindow
                winW = (int)Math.Round(targetDipsW * scale);
                winH = (int)Math.Round(targetDipsH * scale);

                // 别超过工作区 —— 否则窗口边角会跑到屏幕外（含任务栏）
                winW = Math.Min(winW, workArea.Width - 30);
                winH = Math.Min(winH, workArea.Height - 30);
            }

            _appWindow.Resize(new SizeInt32(winW, winH));

            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                _appWindow.SetIcon(iconPath);
            }

            if (display != null)
            {
                var x = (display.WorkArea.Width - winW) / 2;
                var y = (display.WorkArea.Height - winH) / 2;
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
                    _trayService?.ShowNotification("Sappfiler", "已最小化到系统托盘，后台持续统计游戏时长。");
                }

                TrimMemoryWorkingSet();
                AppLog.Info("[窗口] 已隐藏并裁剪工作集（纯后台低占用运行）");
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
                if (_statsViewModel != null && _currentNav == "Stats") await _statsViewModel.RefreshDataAsync();
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

                _homeViewModel.ResumeTimers();

                // ⚠️ 判断依据是**内容是否真的没了**，不是 _currentNav。
                // _currentNav 只表示"上次导航到哪"，内容被释放之后它可能仍留着旧值，
                // 拿它当条件就会跳过重建、界面白白空着（2026-09-19 的白屏就是这么来的）。
                if (!hasContent)
                {
                    var tag = string.IsNullOrEmpty(_navToRestore) ? "Home" : _navToRestore;
                    AppLog.Info($"[托盘] 内容已释放，重建页面：{tag}");
                    NavigateTo(tag);
                }
                else if (_currentNav == "Home")
                {
                    _ = _homeViewModel.RefreshAllDataAsync();
                }

                AppLog.Info("[托盘] 恢复界面结束");
            });
        };

        // Subclass window procedure to receive WM_TRAYICON and single-instance notifications
        _subclassProc = new SubclassProc(WndProc);
        SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)101, UIntPtr.Zero);

        if (WM_ACTIVATE_INSTANCE != 0)
        {
            ChangeWindowMessageFilterEx(_hwnd, WM_ACTIVATE_INSTANCE, MSGFLT_ALLOW, IntPtr.Zero);
        }
    }

    /// <summary>
    /// 当窗口关闭收进托盘时调用：清空临时解码图片缓存、通知 GC 全量回收并裁剪工作集，
    /// 确保后台纯记录时长时常驻物理内存处于极低水平（前后端分离设计：托盘低占用）。
    /// </summary>
    public void TrimMemoryWorkingSet()
    {
        try
        {
            // 1. 暂停首页非必要的 1 秒 UI 倒计时轮询，避免窗口隐藏期间后台持续触发 UI 布局运算
            _homeViewModel.PauseTimers();

            // 2. 清除已解码的本地图片缓存（位图缓存是占用内存的大头）
            Converters.StringToImageSourceConverter.ClearLocalImageCache();

            // 3. 驱动托管垃圾回收
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);

            // 4. 将物理工作集交还操作系统（EmptyWorkingSet）
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));

            AppLog.Info("[内存] 托盘低占用模式生效：工作集已裁剪，位图缓存已清空");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[内存] 工作集裁剪异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 开机自启动静默启动：首帧激活确保 WinUI 3 宿主就绪后立即隐藏并裁剪工作集。
    /// </summary>
    public void StartMinimized()
    {
        Activate();
        _appWindow?.Hide();
        TrimMemoryWorkingSet();
    }

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_ACTIVATE_INSTANCE && WM_ACTIVATE_INSTANCE != 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AppLog.Info("[单例] 收到重复启动激活通知，正在还原并置顶已有窗口");
                if (_trayService != null)
                {
                    _trayService.ShowMainWindow();
                }
                else
                {
                    _appWindow?.Show();
                    Activate();
                }
            });
            return IntPtr.Zero;
        }

        if (uMsg == SystemTrayService.WM_TRAYICON)
        {
            _trayService?.HandleTrayMessage((int)lParam);
            return IntPtr.Zero;
        }
        if (uMsg == WM_GETMINMAXINFO)
        {
            var minMax = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            uint dpi = GetDpiForWindow(_hwnd);
            double s = (dpi > 0 ? dpi : 96) / 96.0;
            // 限制最小逻辑窗口尺寸：840 x 560 DIPs
            minMax.ptMinTrackSize.x = (int)Math.Round(840 * s);
            minMax.ptMinTrackSize.y = (int)Math.Round(560 * s);
            Marshal.StructureToPtr(minMax, lParam, true);
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private async Task InitializeAndStartMonitoringAsync()
    {
        await _sessionManager.InitializeAsync();
        await _sessionManager.CleanupZombieSessionsAsync();

        // 🔧 2026-10-11（A/B 实机对差根因修复）：daily_summary 是**派生缓存**，但它可以被
        //    「Notion 删除对账」单独删行（DeleteDailySummariesAsync）而**会话事实一行未动**；
        //    删除后没有任何路径会从 canonical 事实重新派生 ⇒ 该 (game, 日) 的时长永久消失。
        //
        //    ⚠️ 启动阶段**只做只读审计**（apply: false）：任何有数据影响的操作
        //    （补缺行 / 恢复被 Notion 删除的行 / 场次归一）都必须由人工显式授权后另行调用
        //    ReconcileDailySummariesAsync(apply: true) / ReportAsync(..., restoreNotionDeletedRows: true,
        //    applyCounts: true)。启动绝不改动统计数字，只把差额写进日志供诊断。
        try
        {
            // 详细报告只在具体仓储上（接口只暴露"修复行数"）——这里必须走只读形态。
            if (_repo is SqliteRepository concreteRepo)
            {
                var audit = await concreteRepo.ReconcileDailySummariesReportAsync(apply: false);

                var actionable = audit.Issues.Count(i => !i.Fixed);
                if (actionable > 0 || audit.Orphans.Count > 0)
                {
                    AppLog.Warn(
                        $"[日统计审计] 启动只读审计（未写库）：{audit.Summary}；"
                        + $"待人工裁定 {actionable} 项、孤儿 {audit.Orphans.Count} 条");
                }
            }
        }
        catch (Exception ex)
        {
            // 审计失败绝不影响统计事实与游戏监听（本地计时永远优先）。
            AppLog.Warn($"[日统计审计] 启动审计失败（不影响统计事实）：{ex.Message}");
        }

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

            var cutoffStr = await _repo.GetSettingAsync("daily_cutoff_hour");
            if (int.TryParse(cutoffStr, out var cutoffVal))
            {
                _config.DailyCutoffHour = AccountingDateHelper.NormalizeCutoffHour(cutoffVal);
                _sessionManager.DailyCutoffHour = _config.DailyCutoffHour;
            }

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
                    // 2j：新机首次恢复时，游戏行是**上面这行 Pull 期间**才被创建的 ——
                    // 所以「已忽略」必须在 Pull 之后再应用一次，否则"换机后忽略失效"
                    // 恰恰会在最需要它的场景（全新机器）下复现。幂等、不访问网络。
                    await _syncService.ApplyCatalogIgnoredStateAsync();
                    await _syncService.BackfillRelationsAsync();
                    await _syncService.SyncPendingDailyRecordsAsync();
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
                    _gameLibrary.RegisterKnownGame(
                        string.IsNullOrWhiteSpace(g.Platform) ? "manual" : g.Platform,
                        string.IsNullOrWhiteSpace(g.PlatformId) ? g.Id.ToString() : g.PlatformId,
                        g.Name,
                        g.ExecutablePath);
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

        // 左下角状态胶囊的唯一写入入口（2026-10-12 重做）：它必须跟着**事实**走。
        _syncService.SyncStatusChanged += (s, status) =>
        {
            _ = DispatcherQueue.TryEnqueue(async () => await RefreshSidebarSyncStatusAsync());
        };

        // Notion 的事实状态由 ViewModel 计算（待处理 / 待同步 / 已同步到 Notion），侧栏跟着它走 ——
        // 保证"今日页卡片显示什么，侧栏就显示什么"，不会两处各说一套。
        _homeViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(HomeViewModel.NotionStatusText))
            {
                _ = DispatcherQueue.TryEnqueue(async () => await RefreshSidebarSyncStatusAsync());
            }
        };

        await UpdatePendingBadgeAsync();
        if (_trayService != null) await _trayService.RefreshStateAsync();

        await RefreshSidebarSyncStatusAsync();

        // 监听游戏会话结束：游戏退出时自动同步到 Notion（后台空转周期同步已按需求取消）
        _sessionManager.SessionEnded += (s, session) =>
        {
            if (_config.IsNotionConfigured)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500); // 确保本地落库完成
                        await _syncService.SyncPendingDailyRecordsAsync();
                        await _homeViewModel.RefreshAllDataAsync();
                        if (_trayService != null) await _trayService.RefreshStateAsync();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"[同步] 游戏退出后自动同步失败: {ex.Message}");
                    }
                });
            }
        };

        // 设备同步：会话结束去抖触发（≥5 分钟才真正跑一次，避免"每局一次请求"）
        _sessionManager.SessionEnded += (s, session) =>
        {
            if (DateTime.UtcNow - _lastSessionEndSyncAt < SessionEndSyncDebounce) return;
            _lastSessionEndSyncAt = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500);   // 等本地落库/入队完成
                    if (_deviceSyncService != null)
                    {
                        await _deviceSyncService.SyncNowAsync();

                        // 侧栏状态跟着**真实一轮结果**走（2026-10-12）—— 这里是后台线程，必须回 UI 线程。
                        _ = DispatcherQueue.TryEnqueue(async () => await RefreshSidebarSyncStatusAsync());
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[设备同步] 会话结束触发同步失败（本地计时不受影响）：{ex.GetType().Name}");
                }
            });
        };

        // Start background 5s process monitor loop (本地进程监控不受任何影响)
        _monitorCts = new CancellationTokenSource();
        _ = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));

        // 设备同步：启动一次（复位 InFlight → 跑一轮 → Deferred 重处理 → 播种续跑）
        // ⚠️ 后台执行、延迟几秒，绝不阻塞窗口与本地监听。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (_deviceSyncService != null) await _deviceSyncService.StartupAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[设备同步] 启动流程异常（已吞掉）：{ex.GetType().Name}");
            }
            finally
            {
                // 启动这一轮会做：复位 InFlight → 推送 → Deferred 重处理 → 播种续跑，
                // 队列条数会变 ⇒ 侧栏必须跟着更新，否则停在启动瞬间的旧数字上
                // （用户 2026-10-12 反馈"显示的跟实际不同步"）。
                _ = DispatcherQueue.TryEnqueue(async () => await RefreshSidebarSyncStatusAsync());
            }
        });

        // 设备同步：独立的定时循环（默认 15 分钟）。**绝不放进 MonitorLoopAsync**。
        _syncLoopCts = new CancellationTokenSource();
        _ = Task.Run(() => DeviceSyncLoopAsync(_syncLoopCts.Token));

        // 备份恢复结果：由 App 在启动早期（任何仓储创建之前）执行的状态机产出。
        // ⚠️ 失败结果**必须**让用户看见，绝不能静默。
        await ShowPendingRestoreOutcomeAsync();
    }

    /// <summary>
    /// 启动早期"恢复备份"状态机的结果（由 <c>App.OnLaunched</c> 在创建任何仓储之前写入）。
    /// 展示一次后清空；静默托盘启动时不弹窗，保留给设置页的「数据管理」区域展示。
    /// </summary>
    internal static RestoreOutcome? PendingRestoreOutcome { get; set; }

    /// <summary>
    /// 展示数据恢复结果。窗口可见时立即弹窗；静默启动时只记日志（不打断用户）。
    /// </summary>
    private async Task ShowPendingRestoreOutcomeAsync()
    {
        var outcome = PendingRestoreOutcome;
        if (outcome is null || outcome.Kind == RestoreOutcomeKind.None) return;

        try
        {
            var xamlRoot = Content?.XamlRoot;

            if (_appWindow is null || !_appWindow.IsVisible || xamlRoot is null)
            {
                // 静默启动（托盘）：不打断用户；结果留在 PendingRestoreOutcome 里由设置页展示。
                AppLog.Info($"[备份恢复] 结果待展示（静默启动）：{outcome.Message}");
                return;
            }

            PendingRestoreOutcome = null;

            var detail = outcome.Message;
            if (!string.IsNullOrWhiteSpace(outcome.SafetySnapshotPath))
            {
                detail += $"\n\n替换前的安全快照：{outcome.SafetySnapshotPath}";
            }

            var dialog = new ContentDialog
            {
                Title = outcome.IsSuccess ? "数据恢复已完成" : "数据恢复未成功",
                Content = detail,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // 展示失败绝不影响数据（恢复结果本身已经落盘在 restore 状态文件里）。
            AppLog.Warn($"[备份恢复] 展示恢复结果失败（不影响数据）：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除游戏**之前**：按既有规则结束该游戏的全部活跃会话（落尾段 daily + 跨日切分 +
    /// 同步入队），使删除后重新启动该游戏时**必然新建 Session**（2026-10-12）。
    ///
    /// <para>
    /// ⚠️ 只影响这一个游戏；其它游戏的活跃会话不受影响。
    /// ⚠️ 不结束会话的后果：删除归档后重新启动会复用内存里的旧会话，计时起点错误。
    /// </para>
    /// </summary>
    internal Task<int> EndGameSessionsForDeleteAsync(int gameId)
        => _sessionManager.EndSessionsForGameAsync(gameId);

    // =========================================================================================
    // 左下角「同步状态」胶囊（2026-10-12 重做）
    // =========================================================================================

    private bool _refreshingSidebarSync;

    /// <summary>
    /// 刷新左下角「同步状态」胶囊。**这是该胶囊的唯一写入点**，任何其它地方都不许直接改它的文字。
    ///
    /// <para>
    /// 显示内容按<b>事实</b>与<b>绑定情况</b>决定（此前写死了「Notion 已同步 / 上次同步 10:24」——
    /// 既不是基于事实，用户根本没绑定 Notion 时也照显）：
    /// <list type="number">
    ///   <item>绑定了 Notion → Notion 的**事实**同步状况 + 上次真正同步成功的时间；</item>
    ///   <item>否则启用 WebDAV 设备同步 → 设备同步状态 + 上次同步时间（含"尚未同步过"）；</item>
    ///   <item>两者都没有 → 本地模式 + **上次导出备份时间**（没有记录就如实说"尚未导出过备份"）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>失败绝不抛：状态胶囊只是展示，读不到就退化为"尚未…"，不能影响主界面。</para>
    /// </summary>
    internal async Task RefreshSidebarSyncStatusAsync()
    {
        if (_refreshingSidebarSync) return;
        _refreshingSidebarSync = true;

        try
        {
            string title;
            string subtitle;
            string brushKey;

            // 悬停补充说明（可空）：用来回答"为什么是这个状态" —— 光一个数字会让人以为不同步。
            string? detail = null;

            if (_config.IsNotionConfigured)
            {
                // 事实状态由 ViewModel 统一计算（与今日页卡片同源，二者永不打架）。
                title = string.IsNullOrWhiteSpace(_homeViewModel.NotionStatusText)
                    ? "Notion 已绑定"
                    : _homeViewModel.NotionStatusText;

                subtitle = await DescribeLastFactTimeAsync(
                    HomeViewModel.SettingKeyLastNotionSyncUtc, "上次同步", "尚未同步过");

                brushKey = string.IsNullOrWhiteSpace(_homeViewModel.NotionStatusBrushKey)
                    ? "TextPrimaryBrush"
                    : _homeViewModel.NotionStatusBrushKey;
            }
            else
            {
                var device = _deviceSyncService is null ? null : await _deviceSyncService.GetStatusAsync();

                if (device is { Enabled: true })
                {
                    // ⚠️ 标题与状态点**必须同一套判据**（2026-10-12 修正）：
                    //    此前标题只看 LastError、状态点还看 Failed ⇒ 会出现"红点 + 待同步文案"的自相矛盾。
                    var hasError = device.LastError is not null || device.Failed > 0;

                    if (hasError)
                    {
                        title = "WebDAV 同步异常";
                        brushKey = "StatusRedBrush";
                        detail = string.IsNullOrWhiteSpace(device.LastError)
                            ? $"最近一轮有 {device.Failed} 条被服务端拒绝。"
                            : device.LastError;
                    }
                    else if (device.Pending > 0)
                    {
                        // ⚠️ 用「待发」而不是「待同步」（2026-10-12 用户反馈"显示不同步"）：
                        //    这个数字 = 队列里 status ∈ {待发, 在途} 的条数，**包含正在推送中的条目**，
                        //    写成"待同步"会被读成"没在同步"。
                        title = $"WebDAV 待发 · {device.Pending} 条";
                        brushKey = "StatusOrangeBrush";
                        detail = "含正在推送中的条目；每 15 分钟自动同步一次，也可在今日页点「立即同步」。";
                    }
                    else
                    {
                        title = "WebDAV 已同步";
                        brushKey = "StatusGreenBrush";
                    }

                    subtitle = device.LastSyncAtUtc is { } last
                        ? $"上次同步 {HomeViewModel.FormatRelativeTime(last)}"
                        : "尚未同步过";
                }
                else
                {
                    title = "未启用同步 · 本地模式";
                    subtitle = await DescribeLastFactTimeAsync(
                        BackupFormat.SettingKeyLastExportUtc, "上次导出备份", "尚未导出过备份");
                    brushKey = "TextSecondaryBrush";
                }
            }

            // 留痕：状态胶囊显示什么、**依据哪一类事实** —— 用户报过"显示的不是事实"，
            // 这一行让它可审计（QA 不必截图就能判断分支与来源是否正确）。
            AppLog.Info($"[侧栏] 同步状态：{title} ｜ {subtitle}（依据：{brushKey}）");

            SidebarSyncStatus.Text = title;
            SidebarSyncTime.Text = subtitle;
            SidebarSyncDot.Fill = HomeViewModel.ResolveStatusBrush(brushKey);

            // 文字过长时省略号截断 ⇒ 悬停看全文（用户 2026-10-12 反馈"截断显示不全"）；
            // 并把"为什么是这个状态"一并放进悬停提示 —— 只给数字会让人以为不同步。
            var statusTooltip = string.IsNullOrWhiteSpace(detail) ? title : $"{title}\n{detail}";
            ToolTipService.SetToolTip(SidebarSyncStatus, statusTooltip);
            ToolTipService.SetToolTip(SidebarSyncTime, subtitle);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[侧栏] 刷新同步状态失败（不影响功能）：{ex.Message}");
        }
        finally
        {
            _refreshingSidebarSync = false;
        }
    }

    /// <summary>
    /// 把一条**持久化的事实时间戳**读成"上次同步 3 分钟前"这类文案；
    /// 没有记录时如实返回 <paramref name="noneText"/> —— 绝不用当前时间凑一个出来。
    /// </summary>
    private async Task<string> DescribeLastFactTimeAsync(string settingKey, string label, string noneText)
    {
        try
        {
            var raw = await _repo.GetSettingAsync(settingKey);
            if (!string.IsNullOrWhiteSpace(raw)
                && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                return $"{label} {HomeViewModel.FormatRelativeTime(parsed)}";
            }
        }
        catch
        {
            // 读设置失败不该影响侧栏 —— 退化为"尚未…"。
        }

        return noneText;
    }

    /// <summary>
    /// 数据导入（**迁移**语义）完成后刷新界面状态：游戏库、今日统计、待处理队列、托盘与缓存页面。
    /// ⚠️ 恢复语义不在此时刷新 —— 它要重启后才生效（替换发生在下一次启动的最开始）。
    /// </summary>
    internal void RefreshAfterDataImport()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                _gameLibrary.Refresh();
                await _homeViewModel.RefreshAllDataAsync();
                await UpdatePendingBadgeAsync();
                if (_trayService != null) await _trayService.RefreshStateAsync();

                // 缓存页面不会自己感知到新数据 ⇒ 让历史/统计页在下次进入时重建。
                _historyPage = null;
                _statsPage = null;
                if (_currentNav is "History" or "Stats") NavigateTo(_currentNav);

                AppLog.Info("[数据管理] 导入后界面状态已刷新");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[数据管理] 导入后刷新界面失败（不影响数据）：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 设备同步的独立定时循环（间隔来自设置，默认 15 分钟；受坚果云 600 请求/30 分钟约束）。
    ///
    /// ⚠️ 刻意与 <see cref="MonitorLoopAsync"/>（5 秒）分离：网络出任何问题都不影响本地记录。
    /// ⚠️ 与既有 <c>_monitorCts</c> 一样：退出走进程结束，不做 stop/start 状态机（v2 §E 冻结）。
    /// </summary>
    private async Task DeviceSyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var minutes = WebDavSyncSettings.DefaultIntervalMinutes;

                if (_deviceSyncService != null)
                {
                    var status = await _deviceSyncService.GetStatusAsync(ct);
                    minutes = status.IntervalMinutes;
                }

                // 先等一个完整间隔（启动时已经同步过一次）
                await Task.Delay(TimeSpan.FromMinutes(WebDavSyncSettings.ClampInterval(minutes)), ct);

                if (_deviceSyncService != null)
                {
                    await _deviceSyncService.SyncNowAsync(ct);

                    // 周期同步跑完也要刷新侧栏（它是"事实"的展示，不能停在启动时那一刻）。
                    _ = DispatcherQueue.TryEnqueue(async () => await RefreshSidebarSyncStatusAsync());
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[设备同步] 定时循环异常（继续运行）：{ex.GetType().Name}");
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public void UpdateDailyCutoffHour(int cutoffHour)
    {
        _config.DailyCutoffHour = AccountingDateHelper.NormalizeCutoffHour(cutoffHour);
        _sessionManager.DailyCutoffHour = _config.DailyCutoffHour;
        _ = _homeViewModel.RefreshAllDataAsync();
        if (_trayService != null) _ = _trayService.RefreshStateAsync();
    }

    /// <summary>
    /// 监听循环异常的**去重记录**：同类错误首次立即记 ERROR，此后每
    /// <see cref="MonitorErrorLogInterval"/> 记一次并附累计次数 —— 既不刷屏，也绝不静默。
    /// 换了另一类错误时立刻记录并重置计数（新问题必须马上可见）。
    /// </summary>
    private void LogMonitorLoopError(Exception ex)
    {
        var signature = $"{ex.GetType().Name}|{ex.Message}";
        var now = DateTime.UtcNow;

        if (!string.Equals(signature, _monitorErrorSignature, StringComparison.Ordinal))
        {
            _monitorErrorSignature = signature;
            _monitorErrorRepeatCount = 1;
            _monitorErrorFirstSeenUtc = now;
            _monitorErrorLastLoggedUtc = now;

            AppLog.Error(
                $"[计时] 监听循环本轮异常（循环继续，本地计时不受影响）：{ex.GetType().Name}: {ex.Message}", ex);
            return;
        }

        _monitorErrorRepeatCount++;

        if (now - _monitorErrorLastLoggedUtc >= MonitorErrorLogInterval)
        {
            _monitorErrorLastLoggedUtc = now;
            var minutes = (now - _monitorErrorFirstSeenUtc).TotalMinutes;

            AppLog.Error(
                $"[计时] 同一个错误自 {_monitorErrorFirstSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 起已连续出现 "
                + $"{_monitorErrorRepeatCount} 次（约 {minutes:F0} 分钟），仍在持续；"
                + $"为免刷屏，相同错误每 {MonitorErrorLogInterval.TotalMinutes:F0} 分钟只记录一次。"
                + $"错误：{signature}");
        }
    }

    /// <summary>监听循环本轮完全成功 ⇒ 清掉抑制状态，下一次错误重新立即记录。</summary>
    private void ResetMonitorErrorSuppression()
    {
        if (_monitorErrorRepeatCount == 0) return;

        _monitorErrorSignature = "";
        _monitorErrorRepeatCount = 0;
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var tickWatch = System.Diagnostics.Stopwatch.StartNew();

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

                // 1.5 本体裁决（2026-10-07 C1/C2/C3）：把本 tick 的**全部**候选一次性交给裁决器，
                //     先裁决完再执行写入 —— 否则启动器会"先到先得"，把本体挤掉（Halo/mcclauncher 实测）。
                try
                {
                    var candidates = processes
                        .Where(p => !string.IsNullOrWhiteSpace(p.ExecutablePath))
                        .Select(p => new GameTimeTracker.Infrastructure.Platforms.ProcessExeCandidate(
                            p.ExecutablePath!, p.ProcessName, p.Pid))
                        .ToList();

                    var sessionCounts = await _repo.GetSessionProcessCountsAsync();
                    var allGamesForPrimary = await _repo.GetAllGamesAsync();
                    var knownPrimary = allGamesForPrimary
                        .Where(g => !string.IsNullOrWhiteSpace(g.ExecutablePath))
                        .GroupBy(g => (g.Platform + "/" + g.PlatformId).ToLowerInvariant())
                        .ToDictionary(grp => grp.Key, grp => (string?)grp.First().ExecutablePath,
                                      StringComparer.OrdinalIgnoreCase);

                    _gameLibrary.BeginScanTick(candidates, sessionCounts, knownPrimary);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[本体裁决] 本轮批量裁决失败，退化为旧行为: {ex.Message}");
                }

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
                            try { _gameLibrary.RegisterKnownGame(identity.Platform, identity.PlatformId, existing.Name, proc.ExecutablePath); } catch { }
                        }
                    }

                    if (identity != null)
                    {
                        activePids.Add(proc.Pid);
                        var game = await _repo.GetOrCreateGameAsync(identity);

                        // Ensure cover cache is triggered
                        // trusted: true —— 该 exe 已由本体裁决确认为 PRIMARY（允许其触发一次性升级重写）
                        _ = _coverCache.EnsureCoverAsync(identity.Platform, identity.PlatformId, identity.ExecutablePath, trusted: true);

                        // Start or heartbeat session
                        if (!_sessionManager.IsPidActive(proc.Pid))
                        {
                            if (!_sessionManager.IsGameActive(game.Id))
                            {
                                AppLog.Info($"[计时] 开始记录「{game.Name}」({identity.Platform}/{identity.PlatformId}) pid={proc.Pid}");
                            }
                            else
                            {
                                AppLog.Info($"[计时] 附加关联子进程「{game.Name}」pid={proc.Pid}");
                            }
                            await _sessionManager.StartSessionAsync(game, proc);
                        }
                        else
                        {
                            // 归属校验：只相信"本轮扫描得到的 GameId"。PID 被系统复用给另一个游戏时，
                            // 旧映射不可信 —— 由管理器摘除旧关联并按当前 Game 重新建立（绝不串记时长）。
                            if (await _sessionManager.HeartbeatOrReassignAsync(proc.Pid, game, proc))
                            {
                                AppLog.Warn($"[计时] PID {proc.Pid} 已被系统复用并重新归属「{game.Name}」：旧关联已摘除");
                            }
                        }
                    }
                }

                // 3. Check ended sessions
                foreach (var pid in _sessionManager.GetTrackedPids())
                {
                    if (!activePids.Contains(pid))
                    {
                        AppLog.Info($"[计时] 结束记录 pid={pid}");
                        await _sessionManager.EndSessionAsync(pid);
                    }
                }

                // 4. Update badge and tray state
                await UpdatePendingBadgeAsync();
                if (_trayService != null) await _trayService.RefreshStateAsync();

                // 本轮完全成功 ⇒ 清掉异常抑制状态，下一次错误重新立即记录。
                ResetMonitorErrorSuppression();
            }
            catch (Exception ex)
            {
                // 🔒 2026-10-11（实机事故修复）：这里以前是无条件的 `catch { }` ——
                //    任何一次写库失败（写锁竞争 / SQLITE_BUSY / 会话状态不一致）都**不留任何痕迹**，
                //    实机表现为"正在玩的游戏不再被记录，重启才恢复"，而日志里查不到任何原因。
                //    监听循环必须继续运行，但异常必须可诊断（用户裁定：不允许吞掉异常后假装成功）。
                //
                // 🔒 2026-10-12：同一错误每 5 秒复现时会刷爆日志 ⇒ 交给去重抑制（见 LogMonitorLoopError）。
                LogMonitorLoopError(ex);
            }

            // 互斥可见性：设备同步的一轮会长时间持有写锁，监听这一拍可能被迫等待。
            // 记录"异常慢的一拍"，以便把"监听卡住"与"监听正常、只是在等写锁"区分开。
            if (tickWatch.Elapsed > TimeSpan.FromSeconds(Math.Max(3, _config.ProcessScanIntervalSeconds) * 3))
            {
                AppLog.Warn($"[计时] 监听循环本轮耗时 {tickWatch.ElapsedMilliseconds} ms"
                    + "（明显超过扫描间隔，通常是与同步轮次的写入互斥、等待写锁；秒数不会丢失，会在下一拍补上）");
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

    /// <summary>
    /// 上一次算出的「Notion 是否绑定」——它同时决定「待处理」与「游戏映射库」两个导航项的可见性
    /// （规则见 <see cref="NotionDependentPages"/>）。
    /// 本方法被监听循环**每 5 秒**调用一次，只在绑定状态**发生变化**时才写日志与做后续页面处置
    /// （否则会把日志刷爆、并每 5 秒白读一次库）。
    /// </summary>
    private bool? _lastNotionBound;

    public async Task UpdatePendingBadgeAsync()
    {
        try
        {
            // ⚠️ 2026-10-12（产品语义，用户裁定）：「待处理」与「游戏映射库」管理的都是
            //    **本地游戏与 Notion 游戏总表之间的关系**，不是独立的本地功能
            //    （「待处理」= 尚未绑定到总表的游戏；「游戏映射库」= 本地身份 ↔ 总表条目的映射）。
            //    未来接入思源 / Obsidian 等其他 Provider 时同理。
            //    因此本机**根本没绑定 Notion** 时它们没有任何可管理的对象：既不该出现导航项，
            //    「待处理」也不该有角标 —— 让用户看到语义不成立的数字/列表是错的。
            //    规则集中在 NotionDependentPages（Core）⇒ 此处只负责"应用规则"，不另写一套判断。
            var notionBound = _syncService.IsNotionConfigured;

            var count = 0;
            if (notionBound)
            {
                var pending = await _repo.GetPendingGamesAsync();
                count = pending.Count(p => p.Status != "ignored");
            }

            var bindingChanged = _lastNotionBound != notionBound;
            if (bindingChanged)
            {
                _lastNotionBound = notionBound;
                AppLog.Info(notionBound
                    ? "[Notion 依赖页] 已绑定 Notion ⇒ 显示「待处理」「游戏映射库」导航项"
                    : "[Notion 依赖页] 未绑定 Notion ⇒ 收起「待处理」「游戏映射库」导航项"
                      + "（它们管理的都是\"与总表的关系\"，没有总表就没有可管理的东西）");
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                // 未绑定 Notion ⇒ 导航项整体收起（不是置灰：它们在这里没有意义）。
                NavPendingBtn.Visibility = NavVisibility(NotionDependentPages.PendingNav, notionBound);
                NavMappingsBtn.Visibility = NavVisibility(NotionDependentPages.MappingsNav, notionBound);

                if (notionBound && count > 0)
                {
                    PendingBadgeBorder.Visibility = Visibility.Visible;
                    PendingBadgeText.Text = count > 99 ? "99+" : count.ToString();
                }
                else
                {
                    PendingBadgeBorder.Visibility = Visibility.Collapsed;
                }

                // ⚠️ 只在**状态变化**时做后续处置：本方法被监听循环每 5 秒调用一次，
                //    无条件刷新会导致每 5 秒读一次库并重算封面（浪费且无必要）。
                if (bindingChanged) ApplyNotionBindingChange(notionBound);
            });
        }
        catch
        {
        }
    }

    /// <summary>按"该页是否依赖 Notion 总表"决定导航项可见性（规则见 <see cref="NotionDependentPages"/>）。</summary>
    private static Visibility NavVisibility(string navTag, bool notionConfigured)
        => NotionDependentPages.ShouldShowNav(navTag, notionConfigured)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// 绑定状态刚刚发生变化（**必须在 UI 线程调用**）：处置"用户此刻正看着的页面"。
    ///
    /// <list type="bullet">
    ///   <item>「游戏映射库」没有"未绑定"空态 ⇒ 立刻切到仍可用的页面：
    ///         不能把用户留在已隐藏的页面上，也不能留下空白页（用户裁定）。</item>
    ///   <item>「待处理」页自身能显示诚实空态 ⇒ 原地刷新内容即可。</item>
    /// </list>
    /// </summary>
    private void ApplyNotionBindingChange(bool notionBound)
    {
        // ⚠️ 2026-10-13：绑定状态一变，托盘也要**立刻**跟着变 ——
        //    未绑定 ⇒ 不得再显示"同步异常"（历史失败残留）或"等待绑定"。
        //    不等 5 秒监听轮询（否则用户会看到"配置已清空但图标还是红的"）。
        _ = _trayService?.RefreshStateAsync();

        try
        {
            var fallback = NotionDependentPages.ResolveFallbackNav(_currentNav, notionBound);
            if (fallback is not null)
            {
                AppLog.Info($"[映射库] 未绑定 Notion 而当前停留在「{_currentNav}」⇒ 安全切换到「{fallback}」"
                    + "（映射库管理的是与总表的关系，解绑后没有可管理的对象）");

                // 丢弃缓存实例：它的视觉树不再复用，重新绑定后重新构建
                //（与数据导入时丢弃历史/统计页同理）。
                _mappingsPage = null;
                NavigateTo(fallback);
                return;
            }

            // 解绑时一并丢弃映射库缓存，避免它被"复活"到已隐藏的页面上（下次绑定时重建）。
            if (!notionBound) _mappingsPage = null;

            // 「待处理」页留在原处，但内容要立刻变成诚实空态。
            if (_pendingPage is not null) _ = _pendingPage.RefreshAsync();
        }
        catch (Exception ex)
        {
            // 切页失败绝不能把界面留在"已隐藏的页面 + 空白"这种状态 ⇒ 记录 + 兜底回首页。
            AppLog.Error($"[映射库] 绑定状态变化后的页面处置失败，兜底回首页：{ex.Message}", ex);
            try
            {
                _mappingsPage = null;
                NavigateTo(NotionDependentPages.HomeNav);
            }
            catch (Exception ex2)
            {
                AppLog.Error("[映射库] 兜底回首页同样失败（界面可能停在原处，数据不受影响）", ex2);
            }
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
        NavHomePath.Stroke = tag == "Home" ? accentBrush : textSecondary;
        NavHomeText.Foreground = tag == "Home" ? textPrimary : textSecondary;
        NavHomeText.FontWeight = tag == "Home" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        NavHistoryBtn.Background = tag == "History" ? activeBrush : transparent;
        NavHistoryIndicator.Visibility = tag == "History" ? Visibility.Visible : Visibility.Collapsed;
        NavHistoryPath.Stroke = tag == "History" ? accentBrush : textSecondary;
        NavHistoryText.Foreground = tag == "History" ? textPrimary : textSecondary;
        NavHistoryText.FontWeight = tag == "History" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        NavStatsBtn.Background = tag == "Stats" ? activeBrush : transparent;
        NavStatsIndicator.Visibility = tag == "Stats" ? Visibility.Visible : Visibility.Collapsed;
        NavStatsPath.Stroke = tag == "Stats" ? accentBrush : textSecondary;
        NavStatsText.Foreground = tag == "Stats" ? textPrimary : textSecondary;
        NavStatsText.FontWeight = tag == "Stats" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        NavPendingBtn.Background = tag == "Pending" ? activeBrush : transparent;
        NavPendingIndicator.Visibility = tag == "Pending" ? Visibility.Visible : Visibility.Collapsed;
        NavPendingPath.Stroke = tag == "Pending" ? accentBrush : textSecondary;
        NavPendingText.Foreground = tag == "Pending" ? textPrimary : textSecondary;
        NavPendingText.FontWeight = tag == "Pending" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        NavMappingsBtn.Background = tag == "Mappings" ? activeBrush : transparent;
        NavMappingsIndicator.Visibility = tag == "Mappings" ? Visibility.Visible : Visibility.Collapsed;
        NavMappingsPath.Stroke = tag == "Mappings" ? accentBrush : textSecondary;
        NavMappingsText.Foreground = tag == "Mappings" ? textPrimary : textSecondary;
        NavMappingsText.FontWeight = tag == "Mappings" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        // "Sync"（多设备同步）是设置下的子页面 → 保持"设置"分区高亮
        var settingsSection = tag is "Settings" or "Sync";
        NavSettingsBtn.Background = settingsSection ? activeBrush : transparent;
        NavSettingsIndicator.Visibility = settingsSection ? Visibility.Visible : Visibility.Collapsed;
        NavSettingsPath.Stroke = settingsSection ? accentBrush : textSecondary;
        NavSettingsText.Foreground = settingsSection ? textPrimary : textSecondary;
        NavSettingsText.FontWeight = settingsSection ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }

    /// <summary>回到设置页（供子页面反向导航，沿用 SettingsPage 既有的 CurrentWindow 范式）。</summary>
    public void NavigateToSettings() => NavigateTo("Settings");

    /// <summary>打开"多设备同步"页。</summary>
    public void NavigateToSync() => NavigateTo("Sync");

    private void NavigateTo(string tag)
    {
        // 逐步面包屑：这条路径在"从托盘恢复"时静默崩溃过（原生层，无堆栈可看），
        // 只能靠"日志停在哪一行"定位死在哪个调用上。2026-09-19 加，定位完也不要删。
        AppLog.Info($"[导航] 开始 → {tag}");

        // ⚠️ 2026-10-12（用户裁定）：依赖 Notion 总表的页面（「待处理」/「游戏映射库」）在未绑定总表时
        //    没有可管理的对象，导航项已被收起。这里是**兜底**：若在"收起生效前的几秒窗口"内被点到
        //    （或将来出现新的调用方），也不能把用户送到一个语义不成立的页面上。
        if (!NotionDependentPages.ShouldShowNav(tag, _syncService?.IsNotionConfigured == true))
        {
            AppLog.Info($"[导航] 「{tag}」依赖 Notion 总表但当前未绑定 ⇒ 改去「{NotionDependentPages.HomeNav}」");
            tag = NotionDependentPages.HomeNav;
        }

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
            case "Stats":
                AppLog.Info("[导航] 构建 StatsPage…");
                _statsPage ??= new StatsPage { ViewModel = _statsViewModel! };
                ContentFrame.Content = _statsPage;
                _ = _statsViewModel!.RefreshDataAsync();
                AppLog.Info("[导航] 完成 → Stats");
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
            case "Sync":
                if (_syncPage == null)
                {
                    ContentFrame.Navigate(typeof(SyncPage), _deviceSyncService);
                    _syncPage = ContentFrame.Content as SyncPage;
                }
                else
                {
                    ContentFrame.Content = _syncPage;
                }
                _ = _syncPage?.RefreshAsync();
                break;
            case "Settings":
                if (_settingsPage == null)
                {
                    ContentFrame.Navigate(typeof(SettingsPage), (_repo, _config, _notionClient, _syncService));
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
    private void OnNavStatsClicked(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateTo("Stats");
    private void OnNavPendingClicked(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateTo("Pending");
    private void OnNavMappingsClicked(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateTo("Mappings");
    private void OnNavSettingsClicked(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateTo("Settings");
}
