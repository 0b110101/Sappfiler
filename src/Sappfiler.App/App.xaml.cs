using System.Runtime.InteropServices;
using System.Threading;
using GameTimeTracker.App.Services;
using GameTimeTracker.Infrastructure;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GameTimeTracker.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public const string SingleInstanceMutexName = @"Global\Sappfiler_SingleInstance_Mutex_DDD89790";
    public const string SingleInstanceMsgName = "Sappfiler_ActivateInstance_DDD89790";

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const int ASFW_ANY = -1;
    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;

    private static Mutex? _singleInstanceMutex;

    private Window? _window;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }

        if (!createdNew)
        {
            try
            {
                var msgId = RegisterWindowMessage(SingleInstanceMsgName);
                if (msgId != 0)
                {
                    AllowSetForegroundWindow(ASFW_ANY);
                    PostMessage(HWND_BROADCAST, msgId, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch
            {
                // 忽略 IPC 唤醒过程中的偶发异常，确保重复进程快速静默退出
            }

            Environment.Exit(0);
            return;
        }

        InitializeComponent();

        UnhandledException += (sender, e) =>
        {
            AppLog.Error("XAML 未处理异常（崩溃）", e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            AppLog.Error("AppDomain 未处理异常（崩溃）", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        };

        AppLog.Info("========== 应用启动 ==========");
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request for the process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 启动时自动检查并迁移/校准开机自启项（清理旧版 GameTimeTracker 并对齐当前 Sappfiler 实际路径）
        try
        {
            AutoStartHelper.SyncAutoStartRegistration();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[启动] 自动同步自启动配置异常: {ex.Message}");
        }

        var cmdArgs = Environment.GetCommandLineArgs();
        var isAutoStart = cmdArgs.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));

        var mainWindow = new MainWindow();
        _window = mainWindow;

        if (isAutoStart)
        {
            AppLog.Info("[启动] 检测到 --autostart / --minimized 参数，以静默托盘模式启动");
            mainWindow.StartMinimized();
        }
        else
        {
            _window.Activate();
        }
    }
}
