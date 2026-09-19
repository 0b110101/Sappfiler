using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using Microsoft.UI.Windowing;

namespace GameTimeTracker.App.Tray;

public class SystemTrayService : IDisposable
{
    public const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 101;
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int TPM_RIGHTBUTTON = 0x0002;
    private const int TPM_RETURNCMD = 0x0100;

    private const int MF_STRING = 0x0000;
    private const int MF_SEPARATOR = 0x0800;
    private const int MF_DISABLED = 0x0002;
    private const int MF_GRAYED = 0x0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, int uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private readonly AppWindow _appWindow;
    private readonly IntPtr _mainWindowHandle;
    private readonly GameSessionManager _sessionManager;
    private readonly IDatabaseRepository _repo;
    private readonly INotionSyncService _syncService;

    private NOTIFYICONDATA _nid;
    private IntPtr _currentIconHandle = IntPtr.Zero;
    private string _currentState = "idle"; // "idle", "gaming", "pending", "error"
    private bool _lastSyncFailed = false;

    public Action? OnOpenSettings { get; set; }

    /// <summary>窗口被重新显示后回调，用于重建被释放的页面视觉树。</summary>
    public Action? OnWindowShown { get; set; }

    public SystemTrayService(
        AppWindow appWindow,
        IntPtr mainWindowHandle,
        GameSessionManager sessionManager,
        IDatabaseRepository repo,
        INotionSyncService syncService)
    {
        _appWindow = appWindow;
        _mainWindowHandle = mainWindowHandle;
        _sessionManager = sessionManager;
        _repo = repo;
        _syncService = syncService;

        InitializeTrayIcon();
        SetupEventListeners();
        _ = RefreshStateAsync();
    }

    private void InitializeTrayIcon()
    {
        _currentIconHandle = CreateControllerIconHandle("idle");

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _mainWindowHandle,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _currentIconHandle,
            szTip = "GameTime Tracker - 后台正在运行"
        };

        Shell_NotifyIcon(NIM_ADD, ref _nid);
    }

    private void SetupEventListeners()
    {
        _sessionManager.SessionStarted += async (s, session) => await RefreshStateAsync();
        _sessionManager.SessionEnded += async (s, session) => await RefreshStateAsync();
        _syncService.SyncStatusChanged += async (s, status) =>
        {
            if (status.Contains("异常") || status.Contains("失败"))
            {
                _lastSyncFailed = true;
            }
            else if (status.Contains("成功") || status.Contains("就绪") || status.Contains("完成"))
            {
                _lastSyncFailed = false;
            }
            await RefreshStateAsync();
        };
    }

    public async Task RefreshStateAsync()
    {
        var active = _sessionManager.GetActiveSessions();
        if (active.Count > 0)
        {
            UpdateState("gaming");
            return;
        }

        if (_lastSyncFailed)
        {
            UpdateState("error");
            return;
        }

        try
        {
            var pending = await _repo.GetPendingGamesAsync();
            var unhandled = pending.Where(p => p.Status != "ignored").ToList();
            if (unhandled.Count > 0)
            {
                UpdateState("pending");
                return;
            }
        }
        catch
        {
        }

        UpdateState("idle");
    }

    public void UpdateState(string state)
    {
        if (_currentState == state && _currentIconHandle != IntPtr.Zero) return;
        _currentState = state;

        var oldIcon = _currentIconHandle;
        _currentIconHandle = CreateControllerIconHandle(state);

        _nid.hIcon = _currentIconHandle;
        _nid.szTip = state switch
        {
            "gaming" => "GameTime Tracker - 游戏中",
            "pending" => "GameTime Tracker - 等待绑定",
            "error" => "GameTime Tracker - 同步异常",
            _ => "GameTime Tracker - 空闲中"
        };
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);

        if (oldIcon != IntPtr.Zero)
        {
            DestroyIcon(oldIcon);
        }
    }

    public void HandleTrayMessage(int messageType)
    {
        switch (messageType)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                ShowMainWindow();
                break;

            case WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }
    }

    public void ShowMainWindow()
    {
        // 面包屑日志：这条路径崩溃过（CoreMessagingXP 里的 0xc000027b，原生层，
        // 托管的 UnhandledException / AppDomain 处理器都接不到）。
        // 留两行"进入/完成"，下次真出问题就能确定死在哪个调用上 —— 不要删。
        GameTimeTracker.Infrastructure.AppLog.Info("[托盘] 显示主窗口…");
        try
        {
            ShowWindow(_mainWindowHandle, SW_RESTORE);
            _appWindow.Show();
            SetForegroundWindow(_mainWindowHandle);
            OnWindowShown?.Invoke();
            GameTimeTracker.Infrastructure.AppLog.Info("[托盘] 主窗口已显示");
        }
        catch (Exception ex)
        {
            GameTimeTracker.Infrastructure.AppLog.Error("[托盘] 显示主窗口失败", ex);
        }
    }

    public void ShowNotification(string title, string message)
    {
        // ⚠️ NIF_INFO 用完必须清掉。
        // _nid 是**复用**的结构体，`uFlags |= NIF_INFO` 一旦设上就再也不会被清除，
        // 于是之后每一次 Shell_NotifyIcon(NIM_MODIFY) —— 包括刷新托盘图标、更新
        // 悬浮提示文字 —— 都会顺带弹一次气泡。用户看到的就是"怎么老弹提示"。
        // （2026-09-19 雾山反馈"关闭界面时的提示有点烦"时查到的。）
        _nid.uFlags |= NIF_INFO;
        _nid.szInfoTitle = title;
        _nid.szInfo = message;
        _nid.dwInfoFlags = 1; // Info icon

        Shell_NotifyIcon(NIM_MODIFY, ref _nid);

        _nid.uFlags &= ~NIF_INFO;
        _nid.szInfoTitle = string.Empty;
        _nid.szInfo = string.Empty;
    }

    private async void ShowContextMenu()
    {
        GetCursorPos(out var pt);
        var hMenu = CreatePopupMenu();

        // 1. Current Game item
        var activeSession = _sessionManager.GetActiveSessions().FirstOrDefault();
        string gameStatusText = "● 空闲中";
        if (activeSession != null)
        {
            var game = await _repo.GetGameByIdAsync(activeSession.GameId);
            var dur = DailyAggregator.FormatSeconds(activeSession.DurationSeconds);
            gameStatusText = $"● 正在游玩: {game?.Name ?? "未知游戏"} {dur}";
        }
        AppendMenu(hMenu, MF_STRING | MF_DISABLED | MF_GRAYED, (IntPtr)10, gameStatusText);

        // 2. Today's Total
        var todaySummaries = await _repo.GetDailySummariesByDateAsync(DateTime.Today.ToString("yyyy-MM-dd"));
        var todayMins = todaySummaries.Sum(s => s.DurationMinutes);
        AppendMenu(hMenu, MF_STRING | MF_DISABLED | MF_GRAYED, (IntPtr)11, $"📅 今日总计: {DailyAggregator.FormatDuration(todayMins)}");

        // 3. Pending count
        var pending = await _repo.GetPendingGamesAsync();
        AppendMenu(hMenu, MF_STRING | MF_DISABLED | MF_GRAYED, (IntPtr)12, $"⚠️ 待处理: {pending.Count}");

        AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, "");

        // 4. Actions
        AppendMenu(hMenu, MF_STRING, (IntPtr)1, "📊 打开主面板");
        AppendMenu(hMenu, MF_STRING, (IntPtr)2, "🔄 立即同步");
        AppendMenu(hMenu, MF_STRING, (IntPtr)3, "⚙ 设置");
        AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, "");
        AppendMenu(hMenu, MF_STRING, (IntPtr)4, "❌ 退出");

        SetForegroundWindow(_mainWindowHandle);
        var cmd = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _mainWindowHandle, IntPtr.Zero);
        DestroyMenu(hMenu);

        switch (cmd)
        {
            case 1:
                ShowMainWindow();
                break;
            case 2:
                _ = _syncService.SyncPendingDailyRecordsAsync();
                break;
            case 3:
                ShowMainWindow();
                OnOpenSettings?.Invoke();
                break;
            case 4:
                ExitApplication();
                break;
        }
    }

    private void ExitApplication()
    {
        Dispose();
        Environment.Exit(0);
    }

    private static IntPtr CreateControllerIconHandle(string state)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color bgColor = state switch
        {
            "gaming" => Color.FromArgb(34, 197, 94),   // Green
            "pending" => Color.FromArgb(245, 158, 11), // Orange
            "error" => Color.FromArgb(239, 68, 68),    // Red
            _ => Color.FromArgb(203, 213, 225)         // Slate / White
        };

        Color accentColor = state == "idle" ? Color.FromArgb(30, 41, 59) : Color.White;

        // Controller Body
        using (var brush = new SolidBrush(bgColor))
        {
            using var path = new GraphicsPath();
            path.AddArc(2, 6, 8, 8, 180, 90);
            path.AddArc(22, 6, 8, 8, 270, 90);
            path.AddArc(22, 18, 8, 8, 0, 90);
            path.AddArc(2, 18, 8, 8, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }

        // D-Pad
        using (var accentBrush = new SolidBrush(accentColor))
        {
            g.FillRectangle(accentBrush, 7, 14, 6, 4);
            g.FillRectangle(accentBrush, 8, 13, 4, 6);
        }

        // Action Buttons
        using (var yellowBrush = new SolidBrush(Color.FromArgb(241, 196, 15)))
        using (var redBrush = new SolidBrush(Color.FromArgb(231, 76, 60)))
        {
            g.FillEllipse(yellowBrush, 20, 12, 3, 3);
            g.FillEllipse(redBrush, 24, 16, 3, 3);
        }

        return bitmap.GetHicon();
    }

    public void Dispose()
    {
        if (_nid.hWnd != IntPtr.Zero)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _nid.hWnd = IntPtr.Zero;
        }

        if (_currentIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentIconHandle);
            _currentIconHandle = IntPtr.Zero;
        }
    }
}
