using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;

namespace GameTimeTracker.Infrastructure.Sync;

/// <summary>
/// 多后端同步调度中心。
/// 协调所有启用的 Provider 并行同步、隔离失败，将结果保存至 SQLite。
/// </summary>
public sealed class SyncOrchestrator : ISyncOrchestrator
{
    private readonly IDatabaseRepository _repo;
    private readonly List<ISyncProvider> _providers;
    private readonly INotionSyncService? _notionSyncService;
    private readonly IGameMatcher _matcher;

    public event EventHandler<string>? SyncStatusChanged;

    public IReadOnlyList<ISyncProvider> Providers => _providers;

    public SyncOrchestrator(
        IDatabaseRepository repo,
        IEnumerable<ISyncProvider> providers,
        INotionSyncService? notionSyncService = null,
        IGameMatcher? matcher = null)
    {
        _repo = repo;
        _providers = providers.ToList();
        _notionSyncService = notionSyncService;
        _matcher = matcher ?? new GameMatcher();

        // 如果 NotionSyncService 发出状态变更，转发展示
        if (_notionSyncService != null)
        {
            _notionSyncService.SyncStatusChanged += (s, e) => SyncStatusChanged?.Invoke(this, e);
        }
    }

    public ISyncProvider? GetProvider(string providerName)
    {
        return _providers.FirstOrDefault(p =>
            string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyDictionary<string, SyncResult>> SyncDailyRecordAsync(PlaytimeRecord record)
    {
        var enabledProviders = _providers.Where(p => p.IsEnabled).ToList();
        var results = new Dictionary<string, SyncResult>();

        if (enabledProviders.Count == 0)
        {
            return results;
        }

        var tasks = enabledProviders.Select(async provider =>
        {
            try
            {
                var mapping = await _repo.GetGameMappingAsync(record.GameId, provider.ProviderName);

                // 若尚未建立远端游戏映射，先通过 Matcher 在远端现有目录中寻找确定性候选（防重复创建）
                if (mapping == null)
                {
                    try
                    {
                        var candidates = await provider.GetRemoteCatalogAsync();
                        if (candidates.Count > 0)
                        {
                            var match = _matcher.MatchCandidate(record.GameName, candidates, steamAppId: record.PlatformId);
                            if (match != null && match.CanAutoBind)
                            {
                                await _repo.UpsertGameMappingAsync(
                                    record.GameId,
                                    provider.ProviderName,
                                    match.Candidate.RemoteId,
                                    match.Candidate.RemoteName,
                                    match.Candidate.Location,
                                    match.MatchType.ToString(),
                                    match.Score);
                                mapping = await _repo.GetGameMappingAsync(record.GameId, provider.ProviderName);
                                AppLog.Info($"[SyncOrchestrator] 自动对齐 {provider.DisplayName} 现有笔记/条目: {match.Candidate.RemoteName} ({match.Evidence})");
                            }
                        }

                        // 如果仍然没有映射，仅在 Notion 下保持原有自动创建总表行为；Obsidian 与思源笔记绝不盲目新建
                        if (mapping == null && string.Equals(provider.ProviderName, "notion", StringComparison.OrdinalIgnoreCase))
                        {
                            var game = await _repo.GetGameByIdAsync(record.GameId);
                            if (game != null)
                            {
                                var remoteId = await provider.CreateGameEntryAsync(game);
                                if (!string.IsNullOrWhiteSpace(remoteId))
                                {
                                    await _repo.UpsertGameMappingAsync(record.GameId, provider.ProviderName, remoteId, game.Name, null, "auto_create", 100);
                                    mapping = await _repo.GetGameMappingAsync(record.GameId, provider.ProviderName);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"[SyncOrchestrator] 为 {provider.DisplayName} 检查或自动关联游戏映射异常 ({record.GameName}): {ex.Message}");
                    }
                }

                var res = await provider.SyncDailyAsync(record, mapping);

                if (res.Success)
                {
                    await _repo.UpsertSyncRecordAsync(
                        record.DailySummaryId,
                        provider.ProviderName,
                        "synced",
                        res.RemoteId);
                }
                else
                {
                    await _repo.UpsertSyncRecordAsync(
                        record.DailySummaryId,
                        provider.ProviderName,
                        "error",
                        res.RemoteId,
                        res.ErrorMessage);
                }

                return (provider.ProviderName, res);
            }
            catch (Exception ex)
            {
                var failedRes = SyncResult.Fail(ex.Message);
                await _repo.UpsertSyncRecordAsync(
                    record.DailySummaryId,
                    provider.ProviderName,
                    "error",
                    null,
                    ex.Message);

                return (provider.ProviderName, failedRes);
            }
        });

        var providerResults = await Task.WhenAll(tasks);
        foreach (var (name, res) in providerResults)
        {
            results[name] = res;
        }

        try
        {
            var game = await _repo.GetGameByIdAsync(record.GameId);
            if (game != null)
            {
                var stats = await _repo.GetAggregateStatsAsync(record.GameId);
                foreach (var provider in enabledProviders)
                {
                    try
                    {
                        await provider.SyncGameMetadataAsync(game, stats);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"[SyncOrchestrator] 更新 {provider.DisplayName} 游戏元数据异常 ({game.Name}): {ex.Message}");
                    }
                }
            }
        }
        catch { }

        return results;
    }

    public async Task<int> SyncAllPendingAsync()
    {
        var enabledProviders = _providers.Where(p => p.IsEnabled).ToList();
        if (enabledProviders.Count == 0)
        {
            SyncStatusChanged?.Invoke(this, "暂未启用任何同步目标");
            return 0;
        }

        int totalSynced = 0;

        foreach (var provider in enabledProviders)
        {
            try
            {
                SyncStatusChanged?.Invoke(this, $"正在向 {provider.DisplayName} 同步记录...");

                // 特殊处理 Notion：保留原有的总表缓存与双向回写拉取全流程
                if (provider.ProviderName == "notion" && _notionSyncService != null)
                {
                    var count = await _notionSyncService.SyncPendingDailyRecordsAsync();
                    await _notionSyncService.BackfillRelationsAsync();
                    await _notionSyncService.RefreshDailyTitlesFromMasterAsync();
                    totalSynced += count;
                    continue;
                }

                var pending = await _repo.GetPendingSummariesForProviderAsync(provider.ProviderName);
                int providerSuccess = 0;

                foreach (var item in pending)
                {
                    var record = new PlaytimeRecord
                    {
                        DailySummaryId = item.Id,
                        Date = item.Date,
                        GameId = item.GameId,
                        GameName = item.GameName,
                        DurationSeconds = item.DurationSeconds,
                        SessionCount = item.SessionCount,
                        Platform = item.Platform,
                        PlatformId = item.PlatformId
                    };

                    var mapping = await _repo.GetGameMappingAsync(record.GameId, provider.ProviderName);

                    // 自动创建游戏条目
                    if (mapping == null)
                    {
                        try
                        {
                            var game = await _repo.GetGameByIdAsync(record.GameId);
                            if (game != null)
                            {
                                var remoteId = await provider.CreateGameEntryAsync(game);
                                if (!string.IsNullOrWhiteSpace(remoteId))
                                {
                                    await _repo.UpsertGameMappingAsync(record.GameId, provider.ProviderName, remoteId, game.Name);
                                    mapping = await _repo.GetGameMappingAsync(record.GameId, provider.ProviderName);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"[SyncOrchestrator] 为 {provider.DisplayName} 自动创建游戏映射失败 ({record.GameName}): {ex.Message}");
                        }
                    }

                    var res = await provider.SyncDailyAsync(record, mapping);

                    if (res.Success)
                    {
                        await _repo.UpsertSyncRecordAsync(
                            record.DailySummaryId,
                            provider.ProviderName,
                            "synced",
                            res.RemoteId);
                        providerSuccess++;
                    }
                    else
                    {
                        await _repo.UpsertSyncRecordAsync(
                            record.DailySummaryId,
                            provider.ProviderName,
                            "error",
                            res.RemoteId,
                            res.ErrorMessage);
                    }
                }

                // 针对受影响的游戏更新聚合统计元数据（供 Obsidian 等无 Rollup 平台写入 frontmatter）
                var affectedGameIds = pending.Select(r => r.GameId).Distinct().ToList();
                foreach (var gameId in affectedGameIds)
                {
                    try
                    {
                        var game = await _repo.GetGameByIdAsync(gameId);
                        if (game != null)
                        {
                            var stats = await _repo.GetAggregateStatsAsync(gameId);
                            await provider.SyncGameMetadataAsync(game, stats);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"[SyncOrchestrator] 更新 {provider.DisplayName} 游戏聚合元数据异常: {ex.Message}");
                    }
                }

                totalSynced += providerSuccess;
                SyncStatusChanged?.Invoke(this, $"{provider.DisplayName} 同步完成（成功 {providerSuccess} 条）");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[SyncOrchestrator] {provider.DisplayName} 批量同步异常: {ex.Message}");
                SyncStatusChanged?.Invoke(this, $"{provider.DisplayName} 同步异常: {ex.Message}");
            }
        }

        return totalSynced;
    }

    public async Task<bool> TestProviderConnectionAsync(string providerName)
    {
        var provider = GetProvider(providerName);
        if (provider == null) return false;
        return await provider.TestConnectionAsync();
    }
}
