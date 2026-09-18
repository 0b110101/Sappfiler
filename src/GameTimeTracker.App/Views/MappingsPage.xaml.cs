using GameTimeTracker.App.Dialogs;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameTimeTracker.App.Views;

public sealed partial class MappingsPage : Page
{
    private IDatabaseRepository? _repo;
    private INotionSyncService? _syncService;
    private IGameMatcher? _matcher;
    private List<GameRecord> _allGames = new();

    public MappingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        switch (e.Parameter)
        {
            case (IDatabaseRepository repo, INotionSyncService syncService):
                _repo = repo;
                _syncService = syncService;
                break;
            case IDatabaseRepository repoOnly:
                _repo = repoOnly;
                break;
        }

        _matcher = new GameMatcher();

        if (_repo != null)
        {
            await SyncAndLoadAsync();
        }
    }

    public async Task RefreshAsync()
    {
        await LoadMappingsAsync();
    }

    /// <summary>
    /// 进入页面时先尝试自动关联一次，再加载列表。
    /// 否则总表里明明已有的游戏会一直显示为「未绑定」。
    /// </summary>
    private async Task SyncAndLoadAsync()
    {
        if (_repo == null) return;

        try
        {
            if (_syncService != null)
            {
                var catalog = await _repo.GetCatalogItemsAsync();
                if (catalog.Count == 0)
                {
                    await _syncService.RefreshGameCatalogCacheAsync();
                }

                var linked = await _syncService.AutoLinkGamesFromCatalogAsync();
                if (linked > 0)
                {
                    ShowStatus(InfoBarSeverity.Success, "已自动关联",
                        $"共将 {linked} 款本地游戏关联到 Notion 总表。");
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Warning, "自动关联未完成", ex.Message);
        }

        await LoadMappingsAsync();
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private async Task LoadMappingsAsync()
    {
        if (_repo == null) return;
        var games = await _repo.GetAllGamesAsync();
        _allGames = games.ToList();
        FilterMappings(SearchBox.Text);
    }

    private void FilterMappings(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            MappingsRepeater.ItemsSource = _allGames;
        }
        else
        {
            var clean = query.Trim().ToLowerInvariant();
            MappingsRepeater.ItemsSource = _allGames.Where(g =>
                g.Name.ToLowerInvariant().Contains(clean) ||
                g.Executable.ToLowerInvariant().Contains(clean) ||
                g.Platform.ToLowerInvariant().Contains(clean)).ToList();
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        FilterMappings(args.QueryText);
    }

    private async void OnBindClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameRecord game }) return;
        if (_repo == null || _syncService == null || _matcher == null) return;

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
                XamlRoot = XamlRoot
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

                    await LoadMappingsAsync();
                    ShowStatus(InfoBarSeverity.Success, "绑定成功",
                        $"已将「{game.Name}」关联至 Notion 总表，并已触发记录同步。");
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "绑定失败", ex.Message);
        }
    }

    private async void OnUnbindClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameRecord game } && _repo != null)
        {
            await _repo.UpdateGameNotionIdAsync(game.Id, "");
            await LoadMappingsAsync();
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameRecord game } || _repo == null) return;

        // 先弹确认：这一步会物理删除本地历史并在 Notion 归档页面，之前完全没有确认环节。
        var impact = await _repo.GetGameDeletionImpactAsync(game.Id);
        var confirmed = await DeleteConfirmDialog.ConfirmGameDeleteAsync(
            XamlRoot, game.Name, impact, _syncService?.IsNotionConfigured ?? false);

        if (!confirmed) return;

        try
        {
            if (_syncService == null)
            {
                await _repo.DeleteGameAsync(game.Id);
                await LoadMappingsAsync();
                ShowStatus(InfoBarSeverity.Warning, "已删除（本地）",
                    "同步服务不可用，Notion 侧未做任何改动。");
                return;
            }

            var result = await _syncService.DeleteGameEverywhereAsync(game.Id, deleteMasterEntry: true);
            await LoadMappingsAsync();

            var detail = result.NotionSkipped
                ? "Notion 未连接，仅删除了本地数据。"
                : $"Notion：归档 {result.NotionDailyRecordsArchived} 条每日记录" +
                  (result.MasterEntryArchived ? " + 总表条目。" : "。") +
                  (result.NotionDailyRecordsFailed > 0 ? $"（{result.NotionDailyRecordsFailed} 条失败）" : "");

            ShowStatus(result.NotionDailyRecordsFailed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                $"已删除「{game.Name}」", detail);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "删除失败", ex.Message);
        }
    }
}
