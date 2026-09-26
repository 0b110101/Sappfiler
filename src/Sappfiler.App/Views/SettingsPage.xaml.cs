using System.Text.Json;
using GameTimeTracker.App.Services;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Notion;
using GameTimeTracker.Infrastructure.Sync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameTimeTracker.App.Views;

public sealed partial class SettingsPage : Page
{
    private IDatabaseRepository? _repo;
    private TrackerConfig? _config;
    private INotionClient? _notionClient;
    private INotionSyncService? _syncService;
    private ISyncOrchestrator? _syncOrchestrator;
    private ObsidianSyncProvider? _obsidianProvider;
    private SiYuanSyncProvider? _siYuanProvider;
    private bool _isInitializingAutoStart;
    private bool _isInitializingTheme;
    private bool _isInitializingCutoff;
    private bool _isInitializingProviders;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 版本号来自 AssemblyInformationalVersion（由仓库根 Directory.Build.props 统一注入）。
        // 形如 "0.3.1-alpha.3"（SemVer；正式确认后去掉 -alpha.N），展示时补上 "v" 前缀。
        VersionText.Text = $"Sappfiler v{GetAppVersion()}";

        if (e.Parameter is ValueTuple<IDatabaseRepository, TrackerConfig, INotionClient, INotionSyncService, ISyncOrchestrator> t5)
        {
            _repo = t5.Item1;
            _config = t5.Item2;
            _notionClient = t5.Item3;
            _syncService = t5.Item4;
            _syncOrchestrator = t5.Item5;
        }
        else if (e.Parameter is ValueTuple<IDatabaseRepository, TrackerConfig, INotionClient, INotionSyncService> t4)
        {
            _repo = t4.Item1;
            _config = t4.Item2;
            _notionClient = t4.Item3;
            _syncService = t4.Item4;
        }
        else if (e.Parameter is ValueTuple<IDatabaseRepository, TrackerConfig, INotionClient> t3)
        {
            _repo = t3.Item1;
            _config = t3.Item2;
            _notionClient = t3.Item3;
        }

        if (_repo != null && _config != null)
        {
            _obsidianProvider = (_syncOrchestrator?.GetProvider("obsidian") as ObsidianSyncProvider) ?? new ObsidianSyncProvider(_repo);
            _siYuanProvider = (_syncOrchestrator?.GetProvider("siyuan") as SiYuanSyncProvider) ?? new SiYuanSyncProvider(_repo);

            var autoStartDb = await _repo.GetSettingAsync("auto_start");
            AutoStartHelper.SyncAutoStartRegistration(autoStartDb);

            _isInitializingAutoStart = true;
            AutoStartToggle.IsOn = AutoStartHelper.IsAutoStartEnabled();
            _isInitializingAutoStart = false;

            var themeMode = await _repo.GetSettingAsync("theme_mode");
            _isInitializingTheme = true;
            ThemeToggle.IsOn = themeMode == "Dark";
            _isInitializingTheme = false;

            var cutoffStr = await _repo.GetSettingAsync("daily_cutoff_hour");
            int cutoffHour = int.TryParse(cutoffStr, out var parsedCutoff)
                ? parsedCutoff
                : _config.DailyCutoffHour;
            _isInitializingCutoff = true;
            SelectCutoffHourInCombo(cutoffHour);
            _isInitializingCutoff = false;

            TokenInput.Password = await _repo.GetSettingAsync("notion_token") ?? _config.NotionToken;
            GameDbInput.Text = await _repo.GetSettingAsync("game_database_id") ?? _config.GameDatabaseId;
            DailyDbInput.Text = await _repo.GetSettingAsync("daily_database_id") ?? _config.DailyDatabaseId;

            _isInitializingProviders = true;
            var notionCfg = await _repo.GetProviderConfigAsync("notion");
            NotionEnabledToggle.IsOn = notionCfg?.Enabled ?? _config.IsNotionConfigured;

            var obsidianCfg = await _obsidianProvider.GetCurrentConfigAsync();
            ObsidianEnabledToggle.IsOn = obsidianCfg.Enabled;
            ObsidianModeCombo.SelectedIndex = string.Equals(obsidianCfg.Mode, "api", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            ObsidianVaultInput.Text = obsidianCfg.VaultPath ?? "";
            ObsidianPortInput.Text = obsidianCfg.ApiPort.ToString();
            ObsidianKeyInput.Password = obsidianCfg.ApiKey ?? "";
            ObsidianDailyFolderInput.Text = obsidianCfg.DailyFolder ?? "Sappfiler/Daily";
            ObsidianGamesFolderInput.Text = obsidianCfg.GamesFolder ?? "Sappfiler/Games";
            ObsidianApiPanel.Visibility = string.Equals(obsidianCfg.Mode, "api", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            ObsidianFilePanel.Visibility = string.Equals(obsidianCfg.Mode, "api", StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;

            var siYuanCfg = await _siYuanProvider.GetCurrentConfigAsync();
            SiYuanEnabledToggle.IsOn = siYuanCfg.Enabled;
            SiYuanEndpointInput.Text = string.IsNullOrWhiteSpace(siYuanCfg.Endpoint) ? "http://127.0.0.1:6806" : siYuanCfg.Endpoint;
            SiYuanTokenInput.Password = siYuanCfg.Token ?? "";
            SiYuanNotebookCombo.Text = siYuanCfg.NotebookId ?? "";
            SiYuanMasterDbInput.Text = siYuanCfg.MasterDatabaseId ?? "";
            SiYuanDailyDbInput.Text = siYuanCfg.DailyDatabaseId ?? "";
            SiYuanRootPathInput.Text = string.IsNullOrWhiteSpace(siYuanCfg.RootDocPath) ? "/Sappfiler" : siYuanCfg.RootDocPath;
            _isInitializingProviders = false;

            RefreshStorageUi();
        }
    }

    private void SelectCutoffHourInCombo(int hour)
    {
        var targetTag = hour.ToString();
        for (int i = 0; i < CutoffHourCombo.Items.Count; i++)
        {
            if (CutoffHourCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == targetTag)
            {
                CutoffHourCombo.SelectedIndex = i;
                return;
            }
        }
        CutoffHourCombo.SelectedIndex = 0; // 默认 24
    }

    private async void OnCutoffHourChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingCutoff || _repo == null || _config == null) return;

        if (CutoffHourCombo.SelectedItem is ComboBoxItem selected &&
            int.TryParse(selected.Tag?.ToString(), out var hour))
        {
            _config.DailyCutoffHour = hour;
            await _repo.SetSettingAsync("daily_cutoff_hour", hour.ToString());
            MainWindow.CurrentWindow?.UpdateDailyCutoffHour(hour);
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

    private async void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializingAutoStart || _repo == null) return;
        var isEnabled = AutoStartToggle.IsOn;
        var success = AutoStartHelper.SetAutoStart(isEnabled);
        if (success)
        {
            await _repo.SetSettingAsync("auto_start", isEnabled ? "1" : "0");
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = isEnabled ? "开机自启已开启" : "开机自启已关闭";
            StatusInfoBar.Message = isEnabled ? "系统登录后将自动在后台托盘静默启动。" : "已取消开机自启动设置。";
            StatusInfoBar.IsOpen = true;
        }
        else
        {
            _isInitializingAutoStart = true;
            AutoStartToggle.IsOn = !isEnabled;
            _isInitializingAutoStart = false;

            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "设置失败";
            StatusInfoBar.Message = "无法读写注册表开机启动项，请检查系统安全权限。";
            StatusInfoBar.IsOpen = true;
        }
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

    private async void OnNotionEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializingProviders || _repo == null) return;
        var enabled = NotionEnabledToggle.IsOn;
        await _repo.SetProviderConfigAsync("notion", enabled, "{}");
    }

    // ================= Obsidian 设置 =================

    private async void OnObsidianEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializingProviders || _repo == null || _obsidianProvider == null) return;
        var cfg = await _obsidianProvider.GetCurrentConfigAsync();
        cfg.Enabled = ObsidianEnabledToggle.IsOn;
        await _repo.SetProviderConfigAsync("obsidian", cfg.Enabled, JsonSerializer.Serialize(cfg));
    }

    private void OnObsidianModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObsidianModeCombo.SelectedItem is ComboBoxItem item)
        {
            var isApi = string.Equals(item.Tag?.ToString(), "api", StringComparison.OrdinalIgnoreCase);
            if (ObsidianApiPanel != null) ObsidianApiPanel.Visibility = isApi ? Visibility.Visible : Visibility.Collapsed;
            if (ObsidianFilePanel != null) ObsidianFilePanel.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void OnBrowseVaultClicked(object sender, RoutedEventArgs e)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.CurrentWindow);
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            ObsidianVaultInput.Text = folder.Path;
        }
    }

    private async void OnSaveObsidianClicked(object sender, RoutedEventArgs e)
    {
        if (_repo == null) return;
        var mode = (ObsidianModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "file";
        int.TryParse(ObsidianPortInput.Text.Trim(), out var port);
        if (port <= 0) port = 27124;

        var cfg = new ObsidianProviderConfig
        {
            Enabled = ObsidianEnabledToggle.IsOn,
            Mode = mode,
            VaultPath = ObsidianVaultInput.Text.Trim(),
            ApiPort = port,
            ApiKey = ObsidianKeyInput.Password.Trim(),
            DailyFolder = string.IsNullOrWhiteSpace(ObsidianDailyFolderInput.Text) ? "Sappfiler/Daily" : ObsidianDailyFolderInput.Text.Trim(),
            GamesFolder = string.IsNullOrWhiteSpace(ObsidianGamesFolderInput.Text) ? "Sappfiler/Games" : ObsidianGamesFolderInput.Text.Trim()
        };

        var json = JsonSerializer.Serialize(cfg);
        await _repo.SetProviderConfigAsync("obsidian", cfg.Enabled, json);

        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.Title = "Obsidian 设置已保存";
        StatusInfoBar.Message = "Obsidian 同步参数已成功持久化。";
        StatusInfoBar.IsOpen = true;
    }

    private async void OnTestObsidianClicked(object sender, RoutedEventArgs e)
    {
        var mode = (ObsidianModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "file";
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "测试中";
        StatusInfoBar.Message = "正在检测 Obsidian 连接状态...";
        StatusInfoBar.IsOpen = true;

        if (string.Equals(mode, "file", StringComparison.OrdinalIgnoreCase))
        {
            var vaultPath = ObsidianVaultInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath))
            {
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "目录无效";
                StatusInfoBar.Message = "指定的 Vault 目录不存在，请选择有效的 Obsidian 库文件夹。";
            }
            else
            {
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "检测成功";
                StatusInfoBar.Message = $"Vault 目录有效: {vaultPath}";
            }
        }
        else
        {
            int.TryParse(ObsidianPortInput.Text.Trim(), out var port);
            if (port <= 0) port = 27124;
            var apiKey = ObsidianKeyInput.Password.Trim();

            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
                };
                using var client = new HttpClient(handler);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                using var resp = await client.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    StatusInfoBar.Severity = InfoBarSeverity.Success;
                    StatusInfoBar.Title = "连接成功！";
                    StatusInfoBar.Message = "Obsidian Local REST API 测试通过。";
                }
                else
                {
                    StatusInfoBar.Severity = InfoBarSeverity.Error;
                    StatusInfoBar.Title = "连接失败";
                    StatusInfoBar.Message = $"API 返回状态码 {(int)resp.StatusCode}，请确认 API Key 是否正确。";
                }
            }
            catch
            {
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "连接失败";
                StatusInfoBar.Message = $"无法连接到 127.0.0.1:{port}，请确认 Obsidian 是否开启以及插件是否正在运行。";
            }
        }
    }

    // ================= 思源笔记设置 =================

    private async void OnSiYuanEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializingProviders || _repo == null || _siYuanProvider == null) return;
        var cfg = await _siYuanProvider.GetCurrentConfigAsync();
        cfg.Enabled = SiYuanEnabledToggle.IsOn;
        await _repo.SetProviderConfigAsync("siyuan", cfg.Enabled, JsonSerializer.Serialize(cfg));
    }

    private async void OnFetchNotebooksClicked(object sender, RoutedEventArgs e)
    {
        var endpoint = SiYuanEndpointInput.Text.Trim();
        var token = SiYuanTokenInput.Password.Trim();
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = "http://127.0.0.1:6806";

        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "获取中";
        StatusInfoBar.Message = "正在从思源笔记拉取笔记本列表...";
        StatusInfoBar.IsOpen = true;

        try
        {
            var tempCfg = new SiYuanProviderConfig { Endpoint = endpoint, Token = token };
            using var client = new HttpClient();
            var provider = new SiYuanSyncProvider(_repo!, client);
            var json = JsonSerializer.Serialize(tempCfg);
            await _repo!.SetProviderConfigAsync("siyuan", SiYuanEnabledToggle.IsOn, json);

            var notebooks = await provider.GetNotebooksAsync();
            SiYuanNotebookCombo.Items.Clear();
            if (notebooks.Count > 0)
            {
                foreach (var nb in notebooks)
                {
                    SiYuanNotebookCombo.Items.Add(new ComboBoxItem
                    {
                        Content = $"{nb.Name} ({nb.Id})",
                        Tag = nb.Id
                    });
                }
                SiYuanNotebookCombo.SelectedIndex = 0;
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "获取成功";
                StatusInfoBar.Message = $"已发现 {notebooks.Count} 个笔记本，请在下拉列表中选择。";
            }
            else
            {
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = "未发现笔记本";
                StatusInfoBar.Message = "未获取到思源笔记本列表，请检查思源笔记是否已启动且 API Token 是否匹配。";
            }
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "获取失败";
            StatusInfoBar.Message = $"连接思源异常: {ex.Message}";
        }
    }

    private async void OnSaveSiYuanClicked(object sender, RoutedEventArgs e)
    {
        if (_repo == null) return;
        var notebookId = SiYuanNotebookCombo.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? SiYuanNotebookCombo.Text.Trim()
            : SiYuanNotebookCombo.Text.Trim();

        var cfg = new SiYuanProviderConfig
        {
            Enabled = SiYuanEnabledToggle.IsOn,
            Endpoint = string.IsNullOrWhiteSpace(SiYuanEndpointInput.Text) ? "http://127.0.0.1:6806" : SiYuanEndpointInput.Text.Trim(),
            Token = SiYuanTokenInput.Password.Trim(),
            NotebookId = notebookId,
            MasterDatabaseId = SiYuanMasterDbInput.Text.Trim(),
            DailyDatabaseId = SiYuanDailyDbInput.Text.Trim(),
            RootDocPath = string.IsNullOrWhiteSpace(SiYuanRootPathInput.Text) ? "/Sappfiler" : SiYuanRootPathInput.Text.Trim()
        };

        var json = JsonSerializer.Serialize(cfg);
        await _repo.SetProviderConfigAsync("siyuan", cfg.Enabled, json);

        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.Title = "思源设置已保存";
        StatusInfoBar.Message = "思源笔记同步配置已保存。";
        StatusInfoBar.IsOpen = true;
    }

    private async void OnTestSiYuanClicked(object sender, RoutedEventArgs e)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "测试中";
        StatusInfoBar.Message = "正在连接思源笔记内核 API...";
        StatusInfoBar.IsOpen = true;

        var endpoint = string.IsNullOrWhiteSpace(SiYuanEndpointInput.Text) ? "http://127.0.0.1:6806" : SiYuanEndpointInput.Text.Trim();
        var token = SiYuanTokenInput.Password.Trim();

        try
        {
            var tempCfg = new SiYuanProviderConfig { Endpoint = endpoint, Token = token };
            var json = JsonSerializer.Serialize(tempCfg);
            await _repo!.SetProviderConfigAsync("siyuan", SiYuanEnabledToggle.IsOn, json);

            var provider = new SiYuanSyncProvider(_repo!);
            var success = await provider.TestConnectionAsync();

            if (success)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "连接成功！";
                StatusInfoBar.Message = "思源笔记内核 API 通信正常。";
            }
            else
            {
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "连接失败";
                StatusInfoBar.Message = "无法连接思源笔记，请检查服务是否开启或 Token 是否正确。";
            }
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "连接失败";
            StatusInfoBar.Message = $"测试失败: {ex.Message}";
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

    private async void OnNormalizeTitlesClicked(object sender, RoutedEventArgs e)
    {
        if (_syncService == null || _config == null || !_config.IsNotionConfigured)
        {
            ShowWarning("Notion 未配置", "请先在上方正确配置 Notion Token 及两个数据库 ID 并保存。");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "规范化 Notion 历史记录标题",
            Content = "本操作将扫描 Notion 每日时长表中的所有历史记录，统一将标题格式规范化为「游戏名 · 时长 h」标准格式，并对齐总表游戏名称与页面图标。\n\n安全保障承诺：\n· 绝不修改或重写「时长」与「日期」数值属性\n· 绝不删除或归档任何页面\n· 未关联总表的游戏条目将 100% 保持原样跳过\n\n是否立即开始执行？",
            PrimaryButtonText = "确认开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        NormalizeTitlesBtn.IsEnabled = false;
        NormalizeTitlesBtn.Content = "正在规范化...";
        NormalizeProgressBar.Visibility = Visibility.Visible;
        NormalizeProgressBar.IsIndeterminate = false;
        NormalizeProgressBar.Minimum = 0;
        NormalizeProgressBar.Maximum = 100;
        NormalizeProgressBar.Value = 0;
        NormalizeStatusText.Visibility = Visibility.Visible;
        NormalizeStatusText.Text = "正在扫描 Notion 远端记录...";

        try
        {
            var progress = new Progress<(int current, int total, string currentItem)>(info =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (info.total > 0)
                    {
                        NormalizeProgressBar.Maximum = info.total;
                        NormalizeProgressBar.Value = info.current;
                    }
                    NormalizeStatusText.Text = $"[{info.current}/{info.total}] {info.currentItem}";
                });
            });

            var normResult = await _syncService.NormalizeHistoricalDailyTitlesAsync(progress);

            NormalizeStatusText.Text = normResult.SummaryMessage;
            StatusInfoBar.Severity = normResult.ErrorCount > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusInfoBar.Title = "历史格式规范化";
            StatusInfoBar.Message = normResult.SummaryMessage;
            StatusInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            NormalizeStatusText.Text = $"执行失败: {ex.Message}";
            ShowWarning("格式规范化异常", ex.Message);
        }
        finally
        {
            NormalizeTitlesBtn.IsEnabled = true;
            NormalizeTitlesBtn.Content = "开始格式规范化";
            NormalizeProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowWarning(string title, string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Warning;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
