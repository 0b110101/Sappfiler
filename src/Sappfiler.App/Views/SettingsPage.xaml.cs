using System.Text;
using GameTimeTracker.App.Services;
using GameTimeTracker.Core.Backup;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Backup;
using GameTimeTracker.Infrastructure.Database;
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
    private INotionSyncService? _syncService;
    private bool _isInitializingAutoStart;
    private bool _isInitializingTheme;
    private bool _isInitializingCutoff;

    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>跳到"多设备同步"页（沿用既有范式：子页面通过 <c>MainWindow.CurrentWindow</c> 反向导航）。</summary>
    private void OnOpenSyncClicked(object sender, RoutedEventArgs e)
        => MainWindow.CurrentWindow?.NavigateToSync();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 版本号来自 AssemblyInformationalVersion（由仓库根 Directory.Build.props 统一注入）。
        // 形如 "0.3.2-alpha.3"（SemVer；正式确认后去掉 -alpha.N），展示时补上 "v" 前缀。
        VersionText.Text = $"Sappfiler v{GetAppVersion()}";

        if (e.Parameter is ValueTuple<IDatabaseRepository, TrackerConfig, INotionClient, INotionSyncService> t4)
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

            RefreshStorageUi();
            ShowPendingRestoreOutcomeIfAny();
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

    /// <summary>
    /// 「首次同步」的并发保护与代次闸门（2026-10-13，真机故障修复）。
    /// ⚠️ 它**不阻塞保存**：修改/清除配置任何时候都必须能立刻完成。
    /// </summary>
    private readonly FirstSyncGate _firstSyncGate = new();

    /// <summary>正在跑的首次同步的取消源：用户改动配置时立刻请求取消（步骤之间生效）。</summary>
    private CancellationTokenSource? _firstSyncCts;

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

        // ⚠️ 2026-10-13（真机故障修复）：保存/解绑必须**立刻完成**，绝不被后台同步按住 ——
        //    先作废并取消"正在跑的首次同步"：
        //    · 作废 ⇒ 它完成时不得再改 InfoBar（否则会盖掉这次的"保存成功"），也不占名额；
        //    · 取消 ⇒ 它在同步链的**步骤之间**尽快停手，不再按旧配置的意图继续写入。
        //    真机事故：原先这里整条链期间禁用保存按钮，Token 无效时链内逐条重试把窗口拖到分钟级，
        //    用户既不能解绑也不能重新绑定。
        _firstSyncGate.Invalidate();
        CancelRunningFirstSync();

        var nowConfigured = _config.IsNotionConfigured;
        var willRunFirstSync = !wasConfigured && nowConfigured;

        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.Title = "保存成功";
        StatusInfoBar.Message = willRunFirstSync
            ? "Notion 凭证与数据库 ID 已保存并即时生效。正在后台进行首次同步，期间可随时修改或清除配置。"
            : "Notion 凭证与数据库 ID 已保存并即时生效。";
        StatusInfoBar.IsOpen = true;

        // 左下角状态胶囊按"绑定情况"决定显示什么 ⇒ 绑定状态一变就必须立刻重算（2026-10-12）。
        _ = MainWindow.CurrentWindow?.RefreshSidebarSyncStatusAsync();

        // 「待处理」是"未绑定到 Notion 总表的游戏"⇒ 绑定/解绑后它的可见性随之改变（2026-10-12）。
        _ = MainWindow.CurrentWindow?.UpdatePendingBadgeAsync();

        if (willRunFirstSync)
        {
            StartFirstTimeSync();
        }
    }

    /// <summary>取消正在跑的首次同步（配置已变更 ⇒ 旧链不得继续按旧意图写入）。</summary>
    private void CancelRunningFirstSync()
    {
        var cts = _firstSyncCts;
        _firstSyncCts = null;
        if (cts is null) return;

        try
        {
            cts.Cancel();
            GameTimeTracker.Infrastructure.AppLog.Info("[设置] 已请求取消正在运行的首次同步（配置已变更）");
        }
        catch (Exception ex)
        {
            // 取消失败绝不能影响保存本身。
            GameTimeTracker.Infrastructure.AppLog.Warn($"[设置] 取消首次同步失败（不影响保存）：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动首次同步：**不 await、不禁用任何按钮** —— 保存操作到这里就结束了。
    /// 并发保护由 <see cref="FirstSyncGate"/> 负责（同一时刻只允许一次）。
    /// </summary>
    private void StartFirstTimeSync()
    {
        var generation = _firstSyncGate.TryBegin();
        if (generation is null)
        {
            GameTimeTracker.Infrastructure.AppLog.Info("[设置] 已有首次同步在运行 ⇒ 本次不重复触发");
            return;
        }

        var cts = new CancellationTokenSource();
        _firstSyncCts = cts;
        _ = RunFirstTimeSyncAsync(generation.Value, cts);
    }

    /// <summary>
    /// 首次保存配置后的一次性同步：把既有本地游戏/时长推上去、把 Notion 里已有的记录拉下来。
    ///
    /// <para>
    /// ⚠️ 2026-10-13：**不再禁用保存/测试按钮**。"能否修改或清除配置"是用户的基本权利，
    /// 绝不能被后台同步工作按住（真机上 Token 无效时这条链会被拖到分钟级）。
    /// </para>
    ///
    /// <para>
    /// 界面更新受**代次闸门**约束：只有仍是当前那一次才允许改 InfoBar ——
    /// 用户在此期间改过配置的话，旧任务完成时不得覆盖新配置对应的提示。
    /// </para>
    /// </summary>
    private async Task RunFirstTimeSyncAsync(int generation, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        GameTimeTracker.Infrastructure.AppLog.Info(
            $"[设置] 首次同步开始（代次 {generation}；保存/测试按钮保持可用）");

        try
        {
            var main = MainWindow.CurrentWindow;
            if (main == null) return;

            await main.RunInitialSyncAsync(ct);

            if (IsSuperseded(generation, ct)) return;

            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "首次同步完成";
            StatusInfoBar.Message = "已拉取 Notion 总表与每日时长记录，本地记录也已同步上去。";
            StatusInfoBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            GameTimeTracker.Infrastructure.AppLog.Info(
                $"[设置] 首次同步（代次 {generation}）已取消：配置已变更 ⇒ 不更新界面状态");
        }
        catch (Exception ex)
        {
            if (IsSuperseded(generation, ct)) return;

            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "配置已保存，但首次同步未完成";
            StatusInfoBar.Message = $"{ex.Message} 程序会在后台按周期自动重试。";
            StatusInfoBar.IsOpen = true;
        }
        finally
        {
            // ⚠️ 只有"仍是当前那一次"才释放名额：被作废的旧任务在这里必须是 no-op，
            //    否则会把新一次同步的占用误清掉，让并发保护失效。
            var released = _firstSyncGate.End(generation);
            if (ReferenceEquals(_firstSyncCts, cts)) _firstSyncCts = null;
            GameTimeTracker.Infrastructure.AppLog.Info(
                $"[设置] 首次同步（代次 {generation}）结束（释放名额={released}）");
        }
    }

    /// <summary>该次首次同步是否已被取代（被取消，或用户又保存过配置）⇒ 不得再更新界面状态。</summary>
    private bool IsSuperseded(int generation, CancellationToken ct)
        => ct.IsCancellationRequested || !_firstSyncGate.IsCurrent(generation);

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

    // =========================================================================================
    // 数据管理：导出备份 / 导入备份（恢复 · 迁移）
    // =========================================================================================

    private async void OnExportBackupClicked(object sender, RoutedEventArgs e)
    {
        if (_repo is not SqliteRepository concrete) return;

        try
        {
            // ⚠️ WinUI 3 桌面应用必须显式把选择器绑到窗口句柄，否则会抛。
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.CurrentWindow);
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add(BackupFormat.FileTypeDescription, new List<string> { BackupFormat.FileExtension });
            picker.SuggestedFileName = $"sappfiler-backup-{DateTime.Now:yyyyMMdd-HHmm}";

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            SetDataMgmtBusy(true, "正在生成一致性快照并导出…");
            try
            {
                var service = new SappfilerBackupService(concrete, GetAppVersion());
                var result = await service.ExportAsync(file.Path);

                if (result.Success)
                {
                    var mb = result.FileSizeBytes / 1024.0 / 1024.0;
                    DataMgmtStatusText.Visibility = Visibility.Visible;
                    DataMgmtStatusText.Text =
                        $"导出成功：{result.FilePath}\n"
                        + $"大小 {mb:0.00} MB ｜ 导出时间 {LocalTimeText(result.ExportedAtUtc)}\n"
                        + $"内容：游戏 {result.Counts.Games} 个 ｜ 会话 {result.Counts.Sessions} 条 ｜ "
                        + $"每日记录 {result.Counts.DailySummary} 条 ｜ 时长调整 {result.Counts.DailyAdjustments} 条 ｜ "
                        + $"删除排除 {result.Counts.DailyPlayExclusions} 条（不含任何认证凭据）";

                    StatusInfoBar.Severity = InfoBarSeverity.Success;
                    StatusInfoBar.Title = "备份导出成功";
                    StatusInfoBar.Message = $"{result.FilePath}（{mb:0.00} MB）";
                    StatusInfoBar.IsOpen = true;

                    // 未绑定 Notion / 未启用 WebDAV 时，左下角状态胶囊显示的就是"上次导出备份时间"
                    // ⇒ 导出成功必须立刻刷新它（2026-10-12）。
                    _ = MainWindow.CurrentWindow?.RefreshSidebarSyncStatusAsync();
                }
                else
                {
                    ShowWarning("备份导出失败", result.Error);
                }
            }
            finally
            {
                SetDataMgmtBusy(false);
            }
        }
        catch (Exception ex)
        {
            SetDataMgmtBusy(false);
            ShowWarning("备份导出异常", ex.Message);
        }
    }

    private async void OnImportBackupClicked(object sender, RoutedEventArgs e)
    {
        if (_repo is not SqliteRepository concrete) return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.CurrentWindow);
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(BackupFormat.FileExtension);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            // ① 只读校验 + 预览（**校验完成前不修改任何数据**）
            SetDataMgmtBusy(true, "正在校验备份文件…");
            BackupPreview preview;
            try
            {
                var inspectService = new SappfilerImportService(concrete, AppPaths.DataDir);
                preview = await inspectService.InspectAsync(file.Path);
            }
            finally
            {
                SetDataMgmtBusy(false);
            }

            if (!preview.IsUsable)
            {
                DataMgmtStatusText.Visibility = Visibility.Visible;
                DataMgmtStatusText.Text = preview.RejectionReason;
                ShowWarning("备份文件不可用", preview.RejectionReason);
                return;
            }

            // ② 预览 + 选择语义（恢复 / 迁移），有风险的操作必须二次确认
            var mode = await ShowImportPreviewAsync(preview);
            if (mode is null) return;

            // ③ 执行
            SetDataMgmtBusy(true, mode == BackupImportMode.Restore
                ? "正在校验并暂存恢复数据…"
                : "正在合并备份数据…");
            try
            {
                var importService = new SappfilerImportService(concrete, AppPaths.DataDir);
                var result = mode == BackupImportMode.Restore
                    ? await importService.ImportRestoreAsync(preview)
                    : await importService.ImportMigrateAsync(preview);

                ShowImportResult(result);
            }
            finally
            {
                SetDataMgmtBusy(false);
            }
        }
        catch (Exception ex)
        {
            SetDataMgmtBusy(false);
            ShowWarning("导入异常", ex.Message);
        }
    }

    /// <summary>预览 + 语义选择。返回 <c>null</c> 表示用户取消。</summary>
    private async Task<BackupImportMode?> ShowImportPreviewAsync(BackupPreview preview)
    {
        var c = preview.ActualCounts;
        var sb = new StringBuilder();
        sb.AppendLine($"备份时间：{LocalTimeText(preview.ExportedAtUtc)}");
        sb.AppendLine($"来源版本：v{preview.AppVersion}"
            + (preview.SameMachineAsCurrent ? "（与本机是同一台电脑）" : $"（来自 {preview.ExportedFromMachine}）"));
        sb.AppendLine("完整性校验：通过");
        sb.AppendLine();
        sb.AppendLine($"游戏 {c.Games} 个 ｜ 会话 {c.Sessions} 条 ｜ 每日记录 {c.DailySummary} 条");
        if (preview.DateRange is { } range && !string.IsNullOrWhiteSpace(range.Min))
        {
            sb.AppendLine($"日期范围：{range.Min} ~ {range.Max}");
        }
        sb.AppendLine($"时长调整 {c.DailyAdjustments} 条 ｜ 删除排除 {c.DailyPlayExclusions} 条 ｜ 删除墓碑 {c.SyncTombstones} 条");
        sb.AppendLine();
        sb.AppendLine("【恢复备份】用备份整体替换本机数据库（含设备身份与同步状态）。需要重启生效；"
            + "替换前会自动为当前数据保存安全快照，失败会自动回滚。");
        sb.AppendLine("【迁移到本机】只把备份里的游玩记录合并进本机现有数据（保留本机设备身份与凭据配置），"
            + "无需重启；已存在的记录不会被重复计数，已删除的记录不会复活。");
        sb.AppendLine();
        sb.AppendLine("两种方式都不会导入认证凭据；若备份来自其他电脑，Notion 可能需要重新填写 Token。");

        var dialog = new ContentDialog
        {
            Title = "导入备份",
            Content = new TextBlock
            {
                Text = sb.ToString(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            },
            PrimaryButtonText = "恢复备份（需重启）",
            SecondaryButtonText = "迁移到本机",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => BackupImportMode.Restore,
            ContentDialogResult.Secondary => BackupImportMode.Migrate,
            _ => null
        };
    }

    private void ShowImportResult(BackupImportResult result)
    {
        DataMgmtStatusText.Visibility = Visibility.Visible;

        // 恢复语义：已暂存，等重启生效（不是"已完成"，文案必须诚实）
        if (result.Success && result.Mode == BackupImportMode.Restore)
        {
            // ⚠️ 必须讲清"怎么重启"与"为什么还要重新填凭据"，否则普通用户不知道下一步该做什么。
            var guidance = result.Message
                + "（可从托盘图标右键选择「退出」，然后重新打开 Sappfiler。）"
                + (result.RequiresNotionReauth
                    ? " 备份不含认证信息：若使用 Notion 或 WebDAV 同步，请在本页重新填写相应凭据。"
                    : "");

            DataMgmtStatusText.Text = guidance;
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "备份已就绪，重启后生效";
            StatusInfoBar.Message = guidance;
            StatusInfoBar.IsOpen = true;
            return;
        }

        // 迁移语义：已完成，刷新界面状态
        if (result.Success)
        {
            DataMgmtStatusText.Text = result.Message;
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "迁移完成";
            StatusInfoBar.Message = result.Message;
            StatusInfoBar.IsOpen = true;

            MainWindow.CurrentWindow?.RefreshAfterDataImport();
            return;
        }

        // 冲突 / 失败：必须讲清楚原因，绝不用"操作失败"糊弄
        var text = result.Message;
        if (result.Conflicts.Count > 0)
        {
            text += "\n\n" + string.Join("\n", result.Conflicts.Select(c => "· " + c.Description));
        }
        if (!string.IsNullOrWhiteSpace(result.Error) && result.Error != "conflict")
        {
            text += $"\n\n原因：{result.Error}";
        }

        DataMgmtStatusText.Text = text;
        ShowWarning(
            result.Conflicts.Count > 0 ? "存在无法自动合并的冲突，未写入任何数据" : "导入未完成",
            text);
    }

    /// <summary>
    /// 展示启动早期"恢复备份"状态机的结果。
    /// （静默托盘启动时 MainWindow 不弹窗，结果会留到这里展示 —— 失败结果绝不静默。）
    /// </summary>
    private void ShowPendingRestoreOutcomeIfAny()
    {
        var outcome = MainWindow.PendingRestoreOutcome;
        if (outcome is null || outcome.Kind == RestoreOutcomeKind.None) return;

        MainWindow.PendingRestoreOutcome = null;

        DataMgmtStatusText.Visibility = Visibility.Visible;
        DataMgmtStatusText.Text = outcome.Message;

        StatusInfoBar.Severity = outcome.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusInfoBar.Title = outcome.IsSuccess ? "数据恢复已完成" : "数据恢复未成功";
        StatusInfoBar.Message = outcome.Message
            + (string.IsNullOrWhiteSpace(outcome.SafetySnapshotPath)
                ? ""
                : $"\n替换前的安全快照：{outcome.SafetySnapshotPath}");
        StatusInfoBar.IsOpen = true;
    }

    private void SetDataMgmtBusy(bool busy, string status = "")
    {
        ExportBackupBtn.IsEnabled = !busy;
        ImportBackupBtn.IsEnabled = !busy;
        DataMgmtProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (busy && !string.IsNullOrWhiteSpace(status))
        {
            DataMgmtStatusText.Visibility = Visibility.Visible;
            DataMgmtStatusText.Text = status;
        }
    }

    private static string LocalTimeText(string utcIso)
    {
        if (DateTime.TryParse(utcIso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var utc))
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        return string.IsNullOrWhiteSpace(utcIso) ? "（未知）" : utcIso;
    }
}
