using GameTimeTracker.App.Dialogs;
using GameTimeTracker.App.ViewModels;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using GameTimeTracker.Infrastructure.Covers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameTimeTracker.App.Views;

public sealed partial class HistoryPage : Page
{
    private IDatabaseRepository? _repo;
    private CoverCacheService? _coverCache;
    private INotionSyncService? _syncService;

    public HistoryPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        switch (e.Parameter)
        {
            case (IDatabaseRepository repo, CoverCacheService coverCache, INotionSyncService syncService):
                _repo = repo;
                _coverCache = coverCache;
                _syncService = syncService;
                break;
            case (IDatabaseRepository repo2, CoverCacheService coverCache2):
                _repo = repo2;
                _coverCache = coverCache2;
                break;
        }

        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        if (_repo == null) return;
        var records = await _repo.GetRecentDailyRecordsAsync(100);
        var viewModels = records.Select(r =>
        {
            var cover = _coverCache?.GetCoverPath(r.Platform, r.PlatformId);
            return new RecentRecordViewModel
            {
                DailySummaryId = r.Id,
                NotionPageId = r.NotionPageId,
                DurationMinutes = r.DurationMinutes,
                DateText = r.Date,
                GameName = r.GameName,
                Platform = r.Platform.ToUpper(),
                PlatformId = r.PlatformId,
                DurationText = DailyAggregator.FormatDuration(r.DurationMinutes),
                StatusText = r.SyncStatus == "synced" ? "● 已同步" : "● 待同步",
                StatusBrushKey = r.SyncStatus == "synced" ? "StatusGreenBrush" : "StatusOrangeBrush",
                CoverPath = File.Exists(cover) ? cover : null
            };
        }).ToList();

        HistoryRepeater.ItemsSource = viewModels;
    }

    public async Task RefreshAsync()
    {
        await LoadHistoryAsync();
    }

    /// <summary>
    /// 删除一条每日记录：本地删掉该行，并把 Notion 上对应的页面移入回收站。
    /// 之前程序里没有这个入口，"在程序里删记录"根本做不到，也就谈不上影响 Notion。
    /// </summary>
    private async void OnDeleteRecordClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentRecordViewModel vm } || _repo == null) return;

        var confirmed = await DeleteConfirmDialog.ConfirmDailyDeleteAsync(
            XamlRoot,
            vm.DateText,
            vm.GameName,
            vm.DurationMinutes,
            !string.IsNullOrWhiteSpace(vm.NotionPageId),
            _syncService?.IsNotionConfigured ?? false);

        if (!confirmed) return;

        try
        {
            NotionDeleteResult? result = null;
            if (_syncService != null)
            {
                result = await _syncService.DeleteDailyRecordEverywhereAsync(vm.DailySummaryId);
            }
            else
            {
                await _repo.DeleteDailySummariesAsync(new[] { vm.DailySummaryId });
            }

            await LoadHistoryAsync();

            StatusInfoBar.Severity = result is { NotionDailyRecordsFailed: > 0 }
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            StatusInfoBar.Title = $"已删除 {vm.DateText} 的记录";
            StatusInfoBar.Message = result == null || result.NotionSkipped
                ? "Notion 未连接或该记录未同步，仅删除了本地数据。"
                : $"Notion：归档 {result.NotionDailyRecordsArchived} 条" +
                  (result.NotionDailyRecordsFailed > 0 ? $"，{result.NotionDailyRecordsFailed} 条失败" : "。");
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
