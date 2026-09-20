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
    private Window? _window;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
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
