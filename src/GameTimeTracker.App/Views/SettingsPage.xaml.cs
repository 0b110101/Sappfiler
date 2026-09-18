using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Notion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameTimeTracker.App.Views;

public sealed partial class SettingsPage : Page
{
    private IDatabaseRepository? _repo;
    private TrackerConfig? _config;
    private INotionClient? _notionClient;
    private bool _isInitializingTheme;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 版本号来自 AssemblyInformationalVersion（由仓库根 Directory.Build.props 统一注入）。
        // 形如 "0.9.5-alpha17"，展示时补上 "v" 前缀。
        VersionText.Text = $"GameTimeTracker v{GetAppVersion()}";

        if (e.Parameter is (IDatabaseRepository repo, TrackerConfig config, INotionClient client))
        {
            _repo = repo;
            _config = config;
            _notionClient = client;

            var themeMode = await _repo.GetSettingAsync("theme_mode");
            _isInitializingTheme = true;
            ThemeToggle.IsOn = themeMode == "Dark";
            _isInitializingTheme = false;

            TokenInput.Password = await _repo.GetSettingAsync("notion_token") ?? _config.NotionToken;
            GameDbInput.Text = await _repo.GetSettingAsync("game_database_id") ?? _config.GameDatabaseId;
            DailyDbInput.Text = await _repo.GetSettingAsync("daily_database_id") ?? _config.DailyDatabaseId;

            RefreshStorageUi();
        }
    }

    /// <summary>读取程序集信息版本；取不到时回退到程序集版本，避免界面出现空白。</summary>
    private static string GetAppVersion()
    {
        var asm = typeof(SettingsPage).Assembly;
        return asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                   is [System.Reflection.AssemblyInformationalVersionAttribute info, ..]
                   && !string.IsNullOrWhiteSpace(info.InformationalVersion)
            ? info.InformationalVersion.Split('+')[0]
            : asm.GetName().Version?.ToString() ?? "unknown";
    }

    private async void OnThemeToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializingTheme || _repo == null) return;
        var isDark = ThemeToggle.IsOn;
        MainWindow.CurrentWindow?.ApplyTheme(isDark ? ElementTheme.Dark : ElementTheme.Light);
        await _repo.SetSettingAsync("theme_mode", isDark ? "Dark" : "Light");
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_repo == null || _config == null) return;

        var token = TokenInput.Password.Trim();
        var gameDb = GameDbInput.Text.Trim();
        var dailyDb = DailyDbInput.Text.Trim();

        // 先记下"保存前"是否已配置：新用户从「没配」变成「配好了」的那一刻要触发一次同步。
        // 已经配好的人反复点保存不该再触发 —— 那只会让每点一次都打一轮 Notion。
        var wasConfigured = _config.IsNotionConfigured;

        await _repo.SetSettingAsync("notion_token", token);
        await _repo.SetSettingAsync("game_database_id", gameDb);
        await _repo.SetSettingAsync("daily_database_id", dailyDb);

        _config.NotionToken = token;
        _config.GameDatabaseId = gameDb;
        _config.DailyDatabaseId = dailyDb;

        // 关键：让正在运行的 NotionClient 实例立刻用上新 Token。
        // 之前只更新了 _config，client 内存里还是启动时读的旧值 ——
        // 用户改完 Token 不重启的话，业务请求会一直 401（测试连接用的是输入框的新值，所以显示"验证通过"，极具误导性）。
        _notionClient?.UpdateToken(token);

        var nowConfigured = _config.IsNotionConfigured;

        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.Title = "保存成功";
        StatusInfoBar.Message = "Notion 凭证与数据库 ID 已保存并即时生效。";
        StatusInfoBar.IsOpen = true;

        if (!wasConfigured && nowConfigured)
        {
            await RunFirstTimeSyncAsync();
        }
    }

    /// <summary>
    /// 新用户首次保存配置后的第一次同步。
    /// 目的是把既有的本地游戏/时长推上去、把 Notion 里已有的记录拉下来，
    /// 否则用户会看到一条空列表，得干等到下一个 15 分钟周期。
    /// 用 InfoBar 做最简单的进度提示（成功/失败都覆盖），不做进度条。
    /// </summary>
    private async Task RunFirstTimeSyncAsync()
    {
        var main = MainWindow.CurrentWindow;
        if (main == null) return;

        SaveBtn.IsEnabled = false;
        TestBtn.IsEnabled = false;
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "正在首次同步";
        StatusInfoBar.Message = "正在读取游戏总表并同步记录，请稍候…";
        StatusInfoBar.IsOpen = true;

        try
        {
            await main.RunInitialSyncAsync();

            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "首次同步完成";
            StatusInfoBar.Message = "已拉取 Notion 总表与每日时长记录，本地记录也已同步上去。";
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "配置已保存，但首次同步未完成";
            StatusInfoBar.Message = $"{ex.Message} 程序会在后台按周期自动重试。";
        }
        finally
        {
            StatusInfoBar.IsOpen = true;
            SaveBtn.IsEnabled = true;
            TestBtn.IsEnabled = true;
        }
    }

    private async void OnTestConnectionClicked(object sender, RoutedEventArgs e)
    {
        var token = TokenInput.Password.Trim();
        if (string.IsNullOrEmpty(token))
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "请输入 Token";
            StatusInfoBar.Message = "请先在上方输入 Notion API Integration Token。";
            StatusInfoBar.IsOpen = true;
            return;
        }

        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "测试中";
        StatusInfoBar.Message = "正在连接 Notion API 服务器...";
        StatusInfoBar.IsOpen = true;

        var client = new NotionClient(token);
        var success = await client.TestConnectionAsync();

        if (success)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "连接成功！";
            StatusInfoBar.Message = "Notion API 验证通过，凭证有效。";
        }
        else
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "连接失败";
            StatusInfoBar.Message = "无法通过此 Token 访问 Notion，请检查网络或 Token 权限。";
        }
    }

    // ================= 存档位置 =================

    private void RefreshStorageUi()
    {
        StoragePathText.Text = AppPaths.DataDir;
        ResetStorageBtn.Visibility = AppPaths.ReadBootstrapFile() != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnChangeStorageClicked(object sender, RoutedEventArgs e)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.CurrentWindow);
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        var newPath = folder.Path.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(newPath, AppPaths.DataDir, StringComparison.OrdinalIgnoreCase))
        {
            ShowWarning("位置未变化", "新位置与当前存档位置相同。");
            return;
        }

        if (Directory.EnumerateFileSystemEntries(newPath).Any())
        {
            // 允许非空目录（比如用户之前迁移过、想迁回去），但数据库文件冲突时拒绝覆盖
            var existingDb = Path.Combine(newPath, "gametime.db");
            if (File.Exists(existingDb))
            {
                ShowWarning("目标已存在数据库", $"新位置已有 gametime.db，为避免数据混淆未做更改。请换一个空文件夹，或先手动处理该文件。");
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(newPath);

            // 复制数据库（含 WAL/SHM）——复制而非移动，当前实例继续用旧位置直到重启
            var srcDb = AppPaths.DbPath;
            if (File.Exists(srcDb))
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var s = srcDb + suffix;
                    if (File.Exists(s)) File.Copy(s, Path.Combine(newPath, "gametime.db" + suffix), overwrite: true);
                }
            }

            // 复制封面缓存
            var srcCovers = AppPaths.CoversDir;
            if (Directory.Exists(srcCovers))
            {
                var dstCovers = Path.Combine(newPath, "cache", "covers");
                Directory.CreateDirectory(dstCovers);
                foreach (var file in Directory.GetFiles(srcCovers))
                {
                    File.Copy(file, Path.Combine(dstCovers, Path.GetFileName(file)), overwrite: true);
                }
            }

            AppPaths.SetCustomDataDir(newPath);
            RefreshStorageUi();

            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "存档位置已更改";
            StatusInfoBar.Message = "现有数据已复制到新位置。请完全退出程序（托盘退出）后重新启动生效。";
            StatusInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ShowWarning("更改失败", ex.Message);
        }
    }

    private void OnResetStorageClicked(object sender, RoutedEventArgs e)
    {
        AppPaths.ClearCustomDataDir();
        RefreshStorageUi();
        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.Title = "已恢复默认位置";
        StatusInfoBar.Message = $"数据将使用 {AppPaths.DefaultDataDir}。完全退出程序后重新启动生效。";
        StatusInfoBar.IsOpen = true;
    }

    private void ShowWarning(string title, string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Warning;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
