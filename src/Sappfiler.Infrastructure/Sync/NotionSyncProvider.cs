using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Notion;

namespace GameTimeTracker.Infrastructure.Sync;

/// <summary>
/// Notion 同步提供者。
/// 包装现有的 Notion 客户端与同步服务，保持与已有 Notion 逻辑 100% 兼容。
/// </summary>
public sealed class NotionSyncProvider : ISyncProvider
{
    private readonly IDatabaseRepository _repo;
    private readonly INotionClient _client;
    private readonly TrackerConfig _config;
    private readonly INotionSyncService _notionSyncService;

    public string ProviderName => "notion";
    public string DisplayName => "Notion";
    public bool IsEnabled => _config.IsNotionConfigured;

    public NotionSyncProvider(
        IDatabaseRepository repo,
        INotionClient client,
        TrackerConfig config,
        INotionSyncService notionSyncService)
    {
        _repo = repo;
        _client = client;
        _config = config;
        _notionSyncService = notionSyncService;
    }

    public async Task<SyncResult> SyncDailyAsync(PlaytimeRecord record, GameMappingRecord? mapping)
    {
        if (!IsEnabled)
        {
            return SyncResult.Fail("Notion 尚未配置完成");
        }

        try
        {
            var gameMappingPageId = mapping?.RemoteId;
            var (displayName, iconUrl) = await ResolveDailyDisplayAsync(record.GameName, gameMappingPageId);

            var existingRecord = await _repo.GetSyncRecordAsync(record.DailySummaryId, ProviderName);
            string? pageId = existingRecord?.RemoteId;

            if (!string.IsNullOrWhiteSpace(pageId))
            {
                var updated = await _client.UpdateDailyRecordAsync(
                    pageId,
                    record.DurationMinutes,
                    gameMappingPageId,
                    displayName,
                    iconUrl,
                    writeDuration: true);

                if (updated)
                {
                    return SyncResult.Ok(pageId);
                }
                return SyncResult.Fail("更新 Notion 每日记录失败");
            }
            else
            {
                pageId = await _client.CreateDailyRecordAsync(
                    _config.DailyDatabaseId,
                    record.Date,
                    displayName,
                    record.DurationMinutes,
                    gameMappingPageId,
                    iconUrl);

                if (!string.IsNullOrWhiteSpace(pageId))
                {
                    return SyncResult.Ok(pageId);
                }
                return SyncResult.Fail("创建 Notion 每日记录失败");
            }
        }
        catch (Exception ex)
        {
            return SyncResult.Fail(NotionSyncService.FriendlyNotionError(ex));
        }
    }

    public async Task<string> CreateGameEntryAsync(GameRecord game)
    {
        if (!IsEnabled) throw new InvalidOperationException("Notion 尚未配置完成");
        return await _client.CreateGameMasterPageAsync(_config.GameDatabaseId, game.Name, game.CoverUrl, game.CoverUrl);
    }

    public Task SyncGameMetadataAsync(GameRecord game, GameAggregateStats stats)
    {
        // Notion 原生通过总表 Rollup 字段自动聚合每日记录时长，本地无需单独计算回写
        return Task.CompletedTask;
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            return await _client.TestConnectionAsync();
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<RemoteGameCandidate>> GetRemoteCatalogAsync(CancellationToken cancellationToken = default)
    {
        var items = await _repo.GetCatalogItemsAsync();
        if (items.Count == 0 && _notionSyncService != null && IsEnabled)
        {
            try
            {
                await _notionSyncService.RefreshGameCatalogCacheAsync();
                items = await _repo.GetCatalogItemsAsync();
            }
            catch { }
        }

        return items.Select(item => new RemoteGameCandidate
        {
            Provider = ProviderName,
            RemoteId = item.PageId,
            RemoteName = item.Name,
            Aliases = item.Aliases,
            ExternalId = item.Identifiers.FirstOrDefault()
        }).ToList();
    }

    public IReadOnlyList<ProviderConfigField> GetConfigFields()
    {
        return new List<ProviderConfigField>
        {
            new() { Key = "Token", Label = "Notion API Token", Description = "Internal Integration Secret (以 secret_ 开头)", IsSecret = true, IsRequired = true },
            new() { Key = "DailyDatabaseId", Label = "每日时长表 ID", Description = "Daily Summary Database ID (32位十六进制)", IsSecret = false, IsRequired = true },
            new() { Key = "GameDatabaseId", Label = "游戏总表 ID", Description = "Game Master Database ID (32位十六进制)", IsSecret = false, IsRequired = true }
        };
    }

    private async Task<(string DisplayName, string? IconUrl)> ResolveDailyDisplayAsync(string localName, string? gameMasterPageId)
    {
        if (string.IsNullOrWhiteSpace(gameMasterPageId)) return (localName, null);

        try
        {
            var cat = await _repo.GetCatalogItemByPageIdAsync(gameMasterPageId);
            if (cat == null) return (localName, null);

            var displayName = string.IsNullOrWhiteSpace(cat.Name) ? localName : cat.Name;
            var iconUrl = string.IsNullOrWhiteSpace(cat.IconUrl) ? null : cat.IconUrl;
            return (displayName, iconUrl);
        }
        catch
        {
            return (localName, null);
        }
    }
}
