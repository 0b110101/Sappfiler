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
    private ISyncOrchestrator? _orchestrator;
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
            case (IDatabaseRepository repo, INotionSyncService syncService, ISyncOrchestrator orchestrator):
                _repo = repo;
                _syncService = syncService;
                _orchestrator = orchestrator;
                break;
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
        var allMappings = await _repo.GetAllGameMappingsAsync();
        var mapLookup = allMappings.GroupBy(m => m.GameId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var g in games)
        {
            mapLookup.TryGetValue(g.Id, out var gMaps);
            gMaps ??= new List<GameMappingRecord>();

            bool isBound = gMaps.Count > 0 || !string.IsNullOrWhiteSpace(g.NotionPageId);
            string badgeText;
            if (gMaps.Count > 0)
            {
                badgeText = $"● 已关联 ({gMaps.Count}端)";
            }
            else if (!string.IsNullOrWhiteSpace(g.NotionPageId))
            {
                badgeText = "● 已关联 (Notion)";
            }
            else
            {
                badgeText = "● 未关联";
            }

            var sbTip = new System.Text.StringBuilder();
            if (gMaps.Count > 0)
            {
                foreach (var m in gMaps)
                {
                    sbTip.AppendLine($"{m.Provider.ToUpperInvariant()}: {m.RemoteName ?? m.RemoteId}");
                    if (!string.IsNullOrWhiteSpace(m.MatchType))
                    {
                        sbTip.AppendLine($"  (匹配类型: {m.MatchType})");
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(g.NotionPageId))
            {
                sbTip.AppendLine($"NOTION: {g.NotionPageId}");
            }
            else
            {
                sbTip.Append("尚未与任何笔记或数据库建立身份映射");
            }

            g.IsMapped = isBound;
            g.MappingStatusText = badgeText;
            g.MappingDetailsTooltip = sbTip.ToString().Trim();
        }

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
        if (sender is not Button { Tag: GameRecord game } || _repo == null || _matcher == null) return;

        try
        {
            var enabledProviders = _orchestrator?.Providers.Where(p => p.IsEnabled).ToList()
                ?? new List<ISyncProvider>();

            if (enabledProviders.Count == 0 && _syncService != null)
            {
                await BindNotionSingleAsync(game);
                return;
            }

            if (enabledProviders.Count == 0)
            {
                ShowStatus(InfoBarSeverity.Warning, "未启用同步后端", "请先在「设置」中配置并启用笔记后端。");
                return;
            }

            int boundCount = 0;
            foreach (var provider in enabledProviders)
            {
                var catalog = await provider.GetRemoteCatalogAsync();
                var matches = _matcher.MatchAllCandidates(game.Name, catalog, steamAppId: game.PlatformId);

                var dialog = new GameBindingDialog
                {
                    XamlRoot = XamlRoot
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
                    var selectedCand = dialog.GetSelectedCandidate();

                    if (!string.IsNullOrEmpty(selectedId))
                    {
                        if (selectedId == "CREATE_NEW")
                        {
                            var newId = await provider.CreateGameEntryAsync(game);
                            await _repo.UpsertGameMappingAsync(game.Id, provider.ProviderName, newId, game.Name, null, "manual_create", 100);
                        }
                        else
                        {
                            var candName = selectedCand?.RemoteName ?? game.Name;
                            var candLoc = selectedCand?.Location;
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
                await LoadMappingsAsync();
                ShowStatus(InfoBarSeverity.Success, "绑定成功", $"已成功为「{game.Name}」更新笔记后端映射。");
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "绑定失败", ex.Message);
        }
    }

    private async Task BindNotionSingleAsync(GameRecord game)
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
                ShowStatus(InfoBarSeverity.Success, "绑定成功", $"已将「{game.Name}」关联至 Notion 总表，并已触发记录同步。");
            }
        }
    }

    private async void OnUnbindClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameRecord game } && _repo != null)
        {
            var mappings = await _repo.GetGameMappingsAsync(game.Id);
            foreach (var m in mappings)
            {
                await _repo.DeleteGameMappingAsync(game.Id, m.Provider);
            }
            await _repo.UpdateGameNotionIdAsync(game.Id, "");
            await LoadMappingsAsync();
            ShowStatus(InfoBarSeverity.Informational, "已解除关联", $"已清除「{game.Name}」在各笔记后端的映射关系，历史游玩时长完好保留。");
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
