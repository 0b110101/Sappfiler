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

    private async Task LoadPendingGamesAsync()
    {
        if (_repo == null) return;
        var pending = await _repo.GetPendingGamesAsync();
        var activePending = pending.Where(p => p.Status != "ignored").ToList();

        // 填充本地封面路径（复用与其它页面一致的缓存查找；缺失时按需提取一次图标）
        if (_coverCache != null)
        {
            foreach (var game in activePending)
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
            await LoadPendingGamesAsync();
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
