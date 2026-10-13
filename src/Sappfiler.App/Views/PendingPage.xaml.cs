using GameTimeTracker.App.Dialogs;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Covers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameTimeTracker.App.Views;

public sealed partial class PendingPage : Page
{
    private IDatabaseRepository? _repo;
    private INotionSyncService? _syncService;
    private IGameMatcher? _matcher;
    private CoverCacheService? _coverCache;

    private TrackerConfig? _config;
    private INotionClient? _notionClient;

    public Action? OnPendingCountChanged { get; set; }

    public PendingPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is (IDatabaseRepository repo, INotionSyncService syncService, TrackerConfig config, INotionClient notionClient, CoverCacheService coverCache))
        {
            _repo = repo;
            _syncService = syncService;
            _config = config;
            _notionClient = notionClient;
            _coverCache = coverCache;
            _matcher = new GameMatcher();
            await LoadPendingGamesAsync();
        }
        else if (e.Parameter is (IDatabaseRepository repo2, INotionSyncService syncService2))
        {
            _repo = repo2;
            _syncService = syncService2;
            _matcher = new GameMatcher();
            await LoadPendingGamesAsync();
        }
    }

    public async Task RefreshAsync()
    {
        await LoadPendingGamesAsync();
    }

    private bool _isIgnoredExpanded = false;
    private List<GameRecord> _ignoredGames = new();

    private async Task LoadPendingGamesAsync()
    {
        if (_repo == null) return;

        // ⚠️ 2026-10-12（产品语义，用户明确）：「待处理」= **未绑定到 Notion 游戏总表**的游戏
        //    （未来接入思源 / Obsidian 等其他 Provider 时同理）。
        //    本机没绑定 Notion ⇒ 这里根本没有需要处理的内容：如实说明并清空列表，
        //    **不得**落到下面那条"所有游戏均已完成 Notion 绑定"的空态文案（那会误导用户）。
        //    判据用 _syncService（两条导航重载都会赋值；_config 仅在 5 参重载里存在）。
        if (_syncService?.IsNotionConfigured == false)
        {
            NotionNotBoundNotice.Visibility = Visibility.Visible;
            EmptyNotice.Visibility = Visibility.Collapsed;
            PendingRepeater.ItemsSource = null;
            IgnoredRepeater.ItemsSource = null;
            IgnoredSection.Visibility = Visibility.Collapsed;
            return;
        }

        NotionNotBoundNotice.Visibility = Visibility.Collapsed;

        var pending = await _repo.GetPendingGamesAsync();
        var activePending = pending.Where(p => p.Status != "ignored").ToList();
        _ignoredGames = pending.Where(p => p.Status == "ignored").ToList();

        // 填充本地封面路径（复用与其它页面一致的缓存查找；缺失时按需提取一次图标）
        if (_coverCache != null)
        {
            foreach (var game in activePending.Concat(_ignoredGames))
            {
                try
                {
                    var cover = _coverCache.GetCoverPath(game.Platform, game.PlatformId);
                    if ((!File.Exists(cover) || new FileInfo(cover).Length == 0)
                        && !string.IsNullOrEmpty(game.ExecutablePath))
                    {
                        _coverCache.ExtractAndSaveExecutableIcon(game.ExecutablePath, game.Platform, game.PlatformId);
                        cover = _coverCache.GetCoverPath(game.Platform, game.PlatformId);
                    }
                    game.CoverPath = File.Exists(cover) && new FileInfo(cover).Length > 0 ? cover : null;
                }
                catch
                {
                    game.CoverPath = null;
                }
            }
        }

        PendingRepeater.ItemsSource = activePending;
        EmptyNotice.Visibility = activePending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 已忽略游戏列表与折叠栏
        IgnoredRepeater.ItemsSource = _ignoredGames;
        IgnoredSection.Visibility = _ignoredGames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IgnoredTitleText.Text = $"已忽略的游戏 ({_ignoredGames.Count})";

        OnPendingCountChanged?.Invoke();
    }

    private async void OnBindGameClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameRecord game } && _repo != null && _syncService != null && _matcher != null)
        {
            try
            {
                var catalog = await _repo.GetCatalogItemsAsync();
                if (catalog.Count == 0)
                {
                    await _syncService.RefreshGameCatalogCacheAsync();
                    catalog = await _repo.GetCatalogItemsAsync();
                }

                var candidates = _matcher.MatchGame(game.Name, catalog, game.PlatformId);

                var dialog = new GameBindingDialog
                {
                    XamlRoot = this.XamlRoot
                };

                dialog.Setup(game.Name, $"{game.Platform.ToUpper()} · {game.PlatformId}", game.Executable, candidates, catalog);

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var pageId = dialog.GetSelectedPageId();
                    if (!string.IsNullOrEmpty(pageId))
                    {
                        if (pageId == "CREATE_NEW")
                        {
                            pageId = await _syncService.CreateGameMasterAndLinkAsync(game.Id, game.Name);
                        }
                        else
                        {
                            await _syncService.LinkGameRelationAsync(game.Id, pageId);
                        }

                        await LoadPendingGamesAsync();
                        OnPendingCountChanged?.Invoke();

                        StatusInfoBar.Severity = InfoBarSeverity.Success;
                        StatusInfoBar.Title = "绑定成功";
                        StatusInfoBar.Message = $"已成功将「{game.Name}」关联至 Notion 游戏总表，并已触发记录同步。";
                        StatusInfoBar.IsOpen = true;
                    }
                }
            }
            catch (Exception ex)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "绑定失败";
                StatusInfoBar.Message = ex.Message;
                StatusInfoBar.IsOpen = true;
            }
        }
    }

    private async void OnIgnoreGameClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameRecord game } && _repo != null)
        {
            await _repo.UpdateGameStatusAsync(game.Id, "ignored");
            // 2j：忽略状态要跨机一致，否则换台电脑这个游戏又会开始计时。
            // fire-and-forget：UI 不等待，失败只记日志（本地状态已经生效）。
            _ = _syncService?.PushGameIgnoredStateAsync(game.Id, true);
            await LoadPendingGamesAsync();

            var undoBtn = new Button
            {
                Content = "撤销",
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4)
            };
            undoBtn.Click += async (s, args) =>
            {
                await _repo.UpdateGameStatusAsync(game.Id, "active");
                _ = _syncService?.PushGameIgnoredStateAsync(game.Id, false);
                await LoadPendingGamesAsync();
                StatusInfoBar.IsOpen = false;
            };

            StatusInfoBar.Severity = InfoBarSeverity.Informational;
            StatusInfoBar.Title = "已忽略";
            StatusInfoBar.Message = $"已忽略「{game.Name}」，它已移至下方「已忽略的游戏」中，可随时恢复。";
            StatusInfoBar.ActionButton = undoBtn;
            StatusInfoBar.IsOpen = true;
        }
    }

    private void OnToggleIgnoredClicked(object sender, RoutedEventArgs e)
    {
        _isIgnoredExpanded = !_isIgnoredExpanded;
        IgnoredRepeater.Visibility = _isIgnoredExpanded ? Visibility.Visible : Visibility.Collapsed;
        IgnoredChevronIcon.Glyph = _isIgnoredExpanded ? "\uE70E" : "\uE70D";
    }

    private async void OnRestoreGameClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameRecord game } && _repo != null)
        {
            await _repo.UpdateGameStatusAsync(game.Id, "active");
            _ = _syncService?.PushGameIgnoredStateAsync(game.Id, false);
            await LoadPendingGamesAsync();

            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "已恢复";
            StatusInfoBar.Message = $"已将「{game.Name}」恢复至待处理队列。";
            StatusInfoBar.ActionButton = null;
            StatusInfoBar.IsOpen = true;
        }
    }

    private async void OnDeleteGameClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameRecord game } || _repo == null) return;

        var impact = await _repo.GetGameDeletionImpactAsync(game.Id);
        var confirmed = await DeleteConfirmDialog.ConfirmGameDeleteAsync(
            XamlRoot, game.Name, impact, _syncService?.IsNotionConfigured ?? false);

        if (!confirmed) return;

        try
        {
            // 🛡️ 2026-10-12：删除游戏**之前**先按既有规则结束该游戏的活跃会话
            //    （落尾段 daily + 跨日切分 + 同步入队）。否则删除归档后重新启动游戏会**复用**这条旧会话，
            //    新计时就接在了旧起点上。只影响这一个游戏，其它游戏的活跃会话不受影响。
            if (MainWindow.CurrentWindow is { } mainWindow)
            {
                await mainWindow.EndGameSessionsForDeleteAsync(game.Id);
            }

            if (_syncService == null)
            {
                await _repo.DeleteGameAsync(game.Id);
                await LoadPendingGamesAsync();
                OnPendingCountChanged?.Invoke();
                return;
            }

            var result = await _syncService.DeleteGameEverywhereAsync(game.Id, deleteMasterEntry: true);
            await LoadPendingGamesAsync();
            OnPendingCountChanged?.Invoke();

            StatusInfoBar.Severity = result.NotionDailyRecordsFailed > 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            StatusInfoBar.Title = $"已删除「{game.Name}」";
            StatusInfoBar.Message = result.NotionSkipped
                ? "Notion 未连接，仅删除了本地数据。"
                : $"Notion：归档 {result.NotionDailyRecordsArchived} 条每日记录" +
                  (result.MasterEntryArchived ? " + 总表条目。" : "。") +
                  (result.NotionDailyRecordsFailed > 0 ? $"（{result.NotionDailyRecordsFailed} 条失败）" : "");
            StatusInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "删除失败";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
        }
    }
}
