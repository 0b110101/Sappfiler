using GameTimeTracker.App.ViewModels;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Infrastructure.DeviceSync;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameTimeTracker.App.Views;

/// <summary>
/// 设备同步页（2h-3）。
///
/// ⚠️ 本页**只调用服务门面**（<see cref="DeviceSyncService"/>）：不知道 WebDAV 协议，
/// 也不直接读写数据库 —— 同步的判定与落库全部在既有 2c/2e 引擎里。
/// </summary>
public sealed partial class SyncPage : Page
{
    private DeviceSyncService? _service;
    private bool _initializing;
    private bool _syncEnabled;

    public SyncPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is DeviceSyncService service) _service = service;

        await RefreshAsync();
    }

    // ==================== 状态刷新 ====================

    /// <summary>刷新状态卡 + 回填非敏感配置（**密码框永远不回填**）。</summary>
    public async Task RefreshAsync()
    {
        if (_service is null) return;

        try
        {
            _initializing = true;

            var status = await _service.GetStatusAsync();

            EnabledText.Text = status.Enabled ? "已启用" : "已停用";
            BackendText.Text = status.InsecureHttp ? "WebDAV（http:// 未加密）" : "WebDAV";
            PendingText.Text = status.Pending.ToString();
            LastSyncText.Text = FormatRelative(status.LastSyncAtUtc);
            DeviceIdText.Text = $"本机设备 ID：{ShortDeviceId(status.DeviceId)}";

            // 最近一次同步结果（口径全部来自服务层的真实一轮结果；停用后仍显示历史结果）
            LastOutcomeText.Text = string.IsNullOrWhiteSpace(status.LastOutcome)
                ? SyncOutcomeText.NotSynced
                : status.LastOutcome;
            LastOutcomeAtText.Text = FormatRelative(status.LastSyncAtUtc);
            LastOutcomeCountsText.Text =
                $"上传 {status.Uploaded} · 下载 {status.Downloaded} · 延迟 {status.Delayed}"
                + $" · Deferred {status.Deferred} · 失败 {status.Failed}";

            ToggleEnabledBtn.Content = status.Enabled ? "停用同步" : "重新启用同步";
            _syncEnabled = status.Enabled;

            if (string.IsNullOrWhiteSpace(status.LastError))
            {
                ErrorPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = Sanitize(status.LastError);
            }

            var settings = await _service.LoadSettingsAsync();

            ServiceTypeCombo.SelectedIndex = IsJianguoYun(settings.ServerUrl) ? 0 : 1;
            ServerUrlInput.Text = settings.ServerUrl;
            ServerUrlInput.IsReadOnly = IsJianguoYun(settings.ServerUrl);
            AccountInput.Text = settings.Account;
            SyncDirInput.Text = settings.SyncDirectory;

            SyncNowBtn.IsEnabled = status.Enabled;
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"读取同步状态失败：{ex.GetType().Name}");
        }
        finally
        {
            _initializing = false;
        }
    }

    private static bool IsJianguoYun(string serverUrl)
        => serverUrl.Trim().TrimEnd('/')
            .Equals(WebDavSyncSettings.JianguoYunServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 人话版相对时间。实现**唯一**在 <see cref="HomeViewModel.FormatRelativeTime"/>（2026-10-12 提取）——
    /// 本页与主窗口左下角状态胶囊必须同一口径，否则会出现"页面说 3 分钟前、侧栏说刚刚"。
    /// </summary>
    private static string FormatRelative(DateTimeOffset? at) => HomeViewModel.FormatRelativeTime(at);

    /// <summary>设备 ID 只展示前后各 6 位（提示"勿复制数据目录"，同时方便用户比对）。</summary>
    private static string ShortDeviceId(string deviceId)
        => string.IsNullOrWhiteSpace(deviceId) ? "（未生成）"
            : deviceId.Length <= 16 ? deviceId
            : $"{deviceId[..6]}…{deviceId[^6..]}";

    /// <summary>错误文案脱敏：绝不把任何凭据/完整 URL 显示在界面上。</summary>
    private static string Sanitize(string message)
    {
        var text = message.Replace("Basic ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>
    /// 右下角悬浮提示（见 <c>SyncPage.xaml</c> 末尾：它挂在 ScrollViewer 之外，滚动时不动）。
    /// <paramref name="message"/> 由调用方脱敏（见 <see cref="Sanitize"/>）。
    /// </summary>
    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        // 收起规则（2026-10-13 统一交给 ToastAutoFade）：
        //   · 成功 ⇒ 静止 3 秒后**缓慢渐隐**（800ms）；鼠标移上去不消失，移开重新倒数 3 秒；
        //   · 进行中（Informational）与警告 / 错误保持可见 ——
        //     长任务需要看到"正在进行"，错误往往要读全、甚至照着重试。
        _toast ??= new ToastAutoFade(StatusInfoBar);
        _toast.Show(
            title: null,
            message: message,
            severity: severity,
            autoHide: severity == InfoBarSeverity.Success);
    }

    private ToastAutoFade? _toast;

    private WebDavSyncSettings BuildSettingsFromInputs()
    {
        var serverUrl = ServerUrlInput.Text?.Trim() ?? string.Empty;
        if (ServiceTypeCombo.SelectedIndex == 0) serverUrl = WebDavSyncSettings.JianguoYunServerUrl;

        var dir = SyncDirInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(dir)) dir = WebDavSyncSettings.DefaultSyncDirectory;

        return new WebDavSyncSettings
        {
            ServerUrl = serverUrl,
            Account = AccountInput.Text?.Trim() ?? string.Empty,
            SyncDirectory = dir,
            IntervalMinutes = WebDavSyncSettings.DefaultIntervalMinutes,
            Enabled = true
        };
    }

    // ==================== 交互 ====================

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        // 本页可能刚改过「是否启用设备同步」⇒ 左下角状态胶囊的分支会变，离开前重算一次（2026-10-12）。
        _ = MainWindow.CurrentWindow?.RefreshSidebarSyncStatusAsync();
        MainWindow.CurrentWindow?.NavigateToSettings();
    }

    private void OnServiceTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        var isJianguo = ServiceTypeCombo.SelectedIndex == 0;
        ServerUrlInput.IsReadOnly = isJianguo;
        if (isJianguo) ServerUrlInput.Text = WebDavSyncSettings.JianguoYunServerUrl;
    }

    private async void OnTestConnectionClicked(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;

        TestBtn.IsEnabled = false;
        ShowInfo(InfoBarSeverity.Informational, "正在测试连接…");

        try
        {
            var result = await _service.TestConnectionAsync(BuildSettingsFromInputs(), PasswordInput.Password);
            ShowInfo(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, result.Message);
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;

        SaveBtn.IsEnabled = false;
        ShowInfo(InfoBarSeverity.Informational, "正在保存并启用…");

        try
        {
            var settings = BuildSettingsFromInputs();

            // 首次启用的"大量上传"确认（只在真的需要时打断用户）
            var (games, sessions) = await _service.CountBackfillCandidatesAsync();
            var total = games + sessions;

            if (total > WebDavSyncSettings.BackfillConfirmThreshold && !await ConfirmLargeUploadAsync(total))
            {
                ShowInfo(InfoBarSeverity.Warning, "已取消启用。");
                return;
            }

            var result = await _service.SaveAndEnableAsync(settings, PasswordInput.Password);
            if (!result.Ok)
            {
                ShowInfo(InfoBarSeverity.Error, result.Message);
                return;
            }

            PasswordInput.Password = string.Empty;
            ShowInfo(InfoBarSeverity.Success, "已启用。正在上传本机数据…");
            await RefreshAsync();

            // 刚启用设备同步 ⇒ 左下角状态胶囊应从「本地模式」切到 WebDAV 状态（2026-10-12）。
            _ = MainWindow.CurrentWindow?.RefreshSidebarSyncStatusAsync();

            // 播种在后台跑（可中断、可续跑，下次启动会继续）
            _ = Task.Run(async () =>
            {
                var progress = await _service.RunBackfillAsync();

                // 启用阶段再补一次「历史 Outbox 缺口」：把播种 done 之后才出现、又没经过结束路径的
                // 历史事实（旧版本结束的会话 / 崩溃后自愈补了 identity 的会话）补进队列。
                // 只补缺口、不重扫历史；失败也不影响上面的播种结果（本页不直接碰队列）。
                await _service.RepairOutboxGapsAsync();

                DispatcherQueue.TryEnqueue(async () =>
                {
                    ShowInfo(InfoBarSeverity.Success,
                        progress.Done
                            ? $"首次同步完成：共上传 {progress.Enqueued} 条记录（游戏 {progress.GamesTotal}、会话 {progress.SessionsTotal}）。"
                            : $"首次同步仍在进行（已入队 {progress.Enqueued} 条），将在下次启动时继续。");
                    await RefreshAsync();
                });
            });
        }
        finally
        {
            SaveBtn.IsEnabled = true;
        }
    }

    private async void OnSyncNowClicked(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;

        SyncNowBtn.IsEnabled = false;
        ShowInfo(InfoBarSeverity.Informational, "正在同步…");

        try
        {
            var result = await _service.SyncNowAsync();

            if (result.Error is not null)
            {
                // 真实错误照实展示 —— 绝不归类为"无变化"。
                ShowInfo(InfoBarSeverity.Warning, Sanitize(result.Error));
            }
            else
            {
                // ⚠️ 「已是最新」与否**必须看待发队列的真实状态**（status.Pending 是真查库的结果）：
                //    本轮上传/下载为 0 还可能是"被退避跳过"或"引擎空转但队列仍有待发"，不能据此宣称已是最新。
                var status = await _service.GetStatusAsync();
                var message = SyncNowMessage.Build(result, status.Pending);

                ShowInfo(
                    SyncNowMessage.HasTransfer(result) ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
                    message);
            }

            await RefreshAsync();
        }
        finally
        {
            SyncNowBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 停用 / 重新启用（同一个按钮，按当前状态切换）。
    ///
    /// 停用 = 只关开关（服务端 <c>DeviceSyncService.DisableAsync</c> 只写 Enabled="0"），
    /// 同步数据、配置、凭据、最近一次结果全部保留；重新启用走既有"保存并启用"链路
    /// （复用已保存的凭据，协议/目录校验照旧）。本页**只**调服务门面，不碰 Engine / Backend / HttpClient。
    /// </summary>
    private async void OnToggleEnabledClicked(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;

        ToggleEnabledBtn.IsEnabled = false;

        try
        {
            if (_syncEnabled)
            {
                ShowInfo(InfoBarSeverity.Informational, "正在停用同步…");

                var result = await _service.DisableAsync();
                ShowInfo(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                    result.Ok ? "已停用设备同步。同步数据与配置都保留，可随时重新启用。" : result.Message);
            }
            else
            {
                ShowInfo(InfoBarSeverity.Informational, "正在重新启用…");

                var result = await _service.SaveAndEnableAsync(BuildSettingsFromInputs(), PasswordInput.Password);
                if (!result.Ok)
                {
                    ShowInfo(InfoBarSeverity.Error, result.Message);
                    return;
                }

                PasswordInput.Password = string.Empty;
                ShowInfo(InfoBarSeverity.Success, "已重新启用设备同步。");
            }

            await RefreshAsync();

            // 停用 / 重新启用都会改变左下角状态胶囊的分支 ⇒ 立刻重算（2026-10-12）。
            _ = MainWindow.CurrentWindow?.RefreshSidebarSyncStatusAsync();
        }
        finally
        {
            ToggleEnabledBtn.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmLargeUploadAsync(int total)
    {
        var dialog = new ContentDialog
        {
            Title = "首次同步提示",
            Content = $"本次首次同步预计上传 {total} 条记录。\n\n"
                + "· 同步的是 Sappfiler 数据，不是 SQLite 数据库文件；\n"
                + "· 记录较多时可能需要几分钟，中途可以关闭应用，下次启动会继续；\n"
                + "· 建议先备份本地数据。\n\n"
                + "现在开始吗？",
            PrimaryButtonText = "开始同步",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.XamlRoot = XamlRoot;   // WinUI3 里 ContentDialog 必须显式指定宿主 XamlRoot

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
