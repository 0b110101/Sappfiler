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
        // ⚠️ 这是一个**显示条数上限**，不是数据缺失 —— 拉取路径没有日期过滤，
        //    本地库里有全部记录。原先写 100，QA 那边有 287 条，于是"历史"页只显示最近 100 条，
        //    看起来就像"更早的记录没拉下来"（2026-09-19 反馈）。
        //    提高到 1000，够覆盖几年的日常使用；真要再多就得做分页/虚拟化了。
        var records = await _repo.GetRecentDailyRecordsAsync(1000);
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

        _allRecords = viewModels;
        FilterHistory(SearchBox?.Text ?? string.Empty);
    }

    private List<RecentRecordViewModel> _allRecords = new();

    private void FilterHistory(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            HistoryRepeater.ItemsSource = _allRecords;
        }
        else
        {
            var clean = query.Trim().ToLowerInvariant();
            HistoryRepeater.ItemsSource = _allRecords.Where(r =>
                (r.GameName != null && r.GameName.ToLowerInvariant().Contains(clean)) ||
                (r.DateText != null && r.DateText.ToLowerInvariant().Contains(clean)) ||
                (r.Platform != null && r.Platform.ToLowerInvariant().Contains(clean))).ToList();
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput || string.IsNullOrEmpty(sender.Text))
        {
            FilterHistory(sender.Text);
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        FilterHistory(args.QueryText);
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
            PlayRecordDeletionResult? localDeletion = null;

            if (_syncService != null)
            {
                result = await _syncService.DeleteDailyRecordEverywhereAsync(vm.DailySummaryId);
            }
            else
            {
                // 未配置 Notion 时也要走同一条删除链路（作废分片 + 撤回声明 + 重算所有受影响日期），
                // 否则本地删除会被下一次重算复活。
                localDeletion = await _repo.DeletePlayRecordAsync(vm.DailySummaryId);
            }

            await LoadHistoryAsync();

            // ⚠️ 2026-10-11：删除**没有完全生效**时（该日仍有无法归属的历史残差、
            //    或有进行中的会话未计入），绝不能显示"完全删除成功"。
            var fullyDeleted = result?.LocalPlayRecordFullyDeleted ?? localDeletion?.FullyDeleted ?? true;
            var deletionNote = result?.LocalDeletionNote ?? localDeletion?.Note ?? "";
            var remainingSeconds = result?.LocalRemainingSeconds ?? localDeletion?.RemainingSeconds ?? 0;
            var activeSkipped = result?.LocalActiveSessionsSkipped ?? localDeletion?.ActiveSessionsSkipped ?? 0;

            var notionText = result == null || result.NotionSkipped
                ? "Notion 未连接或该记录未同步，仅处理了本地数据。"
                : $"Notion：归档 {result.NotionDailyRecordsArchived} 条" +
                  (result.NotionDailyRecordsFailed > 0 ? $"，{result.NotionDailyRecordsFailed} 条失败。" : "。");

            if (!fullyDeleted)
            {
                // 2026-10-11：部分删除必须给出**可预期的完成方式**，否则用户只看到"没删干净"。
                var completionHint = activeSkipped > 0
                    ? "结束游戏后可再次删除这条记录以完成清理。"
                    : "该日仍有无法归属的历史时长，不会被删除。";

                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = $"已部分删除 {vm.DateText} 的记录";
                StatusInfoBar.Message =
                    $"{deletionNote}（剩余约 {remainingSeconds} 秒仍在统计中）{completionHint}{notionText}";
            }
            else
            {
                StatusInfoBar.Severity = result is { NotionDailyRecordsFailed: > 0 }
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success;
                StatusInfoBar.Title = $"已删除 {vm.DateText} 的记录";
                StatusInfoBar.Message = notionText;
            }

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
