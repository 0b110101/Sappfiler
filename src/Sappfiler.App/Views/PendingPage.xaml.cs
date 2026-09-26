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
    private ISyncOrchestrator? _orchestrator;

    public Action? OnPendingCountChanged { get; set; }

    public PendingPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is (IDatabaseRepository repo, INotionSyncService syncService, TrackerConfig config, INotionClient notionClient, CoverCacheService coverCache, ISyncOrchestrator orchestrator))
        {
            _repo = repo;
            _syncService = syncService;
            _config = config;
            _notionClient = notionClient;
            _coverCache = coverCache;
            _orchestrator = orchestrator;
            _matcher = new GameMatcher();
            await LoadPendingGamesAsync();
        }
        else if (e.Parameter is (IDatabaseRepository repo1, INotionSyncService syncService1, TrackerConfig config1, INotionClient notionClient1, CoverCacheService coverCache1))
        {
            _repo = repo1;
            _syncService = syncService1;
            _config = config1;
            _notionClient = notionClient1;
            _coverCache = coverCache1;
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
        if (sender is not Button { Tag: GameRecord game } || _repo == null || _matcher == null) return;

        try
        {
            var enabledProviders = _orchestrator?.Providers.Where(p => p.IsEnabled).ToList()
                ?? new List<ISyncProvider>();

            if (enabledProviders.Count == 0 && _syncService != null && _config != null && _config.IsNotionConfigured)
            {
                await BindNotionLegacyAsync(game);
                return;
            }

            if (enabledProviders.Count == 0)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = "暂无启用的同步后端";
                StatusInfoBar.Message = "请先在「设置」中启用并配置 Notion、Obsidian 或思源笔记。";
                StatusInfoBar.IsOpen = true;
                return;
            }

            int boundCount = 0;
            foreach (var provider in enabledProviders)
            {
                var existingMap = await _repo.GetGameMappingAsync(game.Id, provider.ProviderName);
                if (existingMap != null) continue; // 已关联则无需重复弹窗

                var catalog = await provider.GetRemoteCatalogAsync();
                var matches = _matcher.MatchAllCandidates(game.Name, catalog, steamAppId: game.PlatformId);

                var dialog = new GameBindingDialog
                {
                    XamlRoot = this.XamlRoot
                };

                dialog.SetupForProvider(
                    provider.ProviderName,
                    provider.DisplayName,
                    game.Name,
                    $"{game.Platform.ToUpper()} · {game.PlatformId}",
                    game.Executable,
                    matches,
                    catalog);

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var selectedId = dialog.GetSelectedPageId();
                    var selectedCandidate = dialog.GetSelectedCandidate();

                    if (!string.IsNullOrEmpty(selectedId))
                    {
                        if (selectedId == "CREATE_NEW")
                        {
                            var newId = await provider.CreateGameEntryAsync(game);
                            await _repo.UpsertGameMappingAsync(game.Id, provider.ProviderName, newId, game.Name, null, "manual_create", 100);
                        }
                        else
                        {
                            var candName = selectedCandidate?.RemoteName ?? game.Name;
                            var candLoc = selectedCandidate?.Location;
                            await _repo.UpsertGameMappingAsync(game.Id, provider.ProviderName, selectedId, candName, candLoc, "manual", 100);

                            if (string.Equals(provider.ProviderName, "notion", StringComparison.OrdinalIgnoreCase) && _syncService != null)
                            {
                                await _syncService.LinkGameRelationAsync(game.Id, selectedId);
                            }
                        }
                        boundCount++;
                    }
                }
            }

            if (boundCount > 0)
            {
                await LoadPendingGamesAsync();
                OnPendingCountChanged?.Invoke();

                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "绑定成功";
                StatusInfoBar.Message = $"已成功为「{game.Name}」完成笔记后端关联与映射。";
                StatusInfoBar.IsOpen = true;
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

    private async Task BindNotionLegacyAsync(GameRecord game)
    {
        if (_repo == null || _syncService == null || _matcher == null) return;
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

    private async void OnIgnoreGameClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameRecord game } && _repo != null)
        {
            await _repo.UpdateGameStatusAsync(game.Id, "ignored");
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
