using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Tests;

/// <summary>
/// 可控的假 Notion 客户端：不联网，只按脚本返回数据并记录被归档的页面。
/// </summary>
internal sealed class FakeNotionClient : INotionClient
{
    public List<NotionGameCatalogItem> GameMasterItems { get; } = new();
    public List<NotionDailyRecordItem> DailyRecords { get; } = new();

    /// <summary>被归档（移入回收站）的页面 id。</summary>
    public List<string> ArchivedPageIds { get; } = new();

    /// <summary>被新建的每日记录（标题），用于断言"拉取失败时不应上传"。</summary>
    public List<string> CreatedDailyRecordTitles { get; } = new();

    /// <summary>设为 true 时归档调用抛异常，用于验证失败路径。</summary>
    public bool FailArchive { get; set; }

    /// <summary>设为 true 时查询每日记录抛异常，用于验证"拉取失败就不上传"。</summary>
    public bool FailQueryDailyRecords { get; set; }

    public void UpdateToken(string token) { }

    public Task<bool> TestConnectionAsync() => Task.FromResult(true);

    public Task<IReadOnlyList<NotionGameCatalogItem>> QueryGameMasterAsync(string databaseId)
        => Task.FromResult<IReadOnlyList<NotionGameCatalogItem>>(GameMasterItems.ToList());

    public Task<IReadOnlyList<NotionDailyRecordItem>> QueryDailyRecordsAsync(string databaseId)
    {
        if (FailQueryDailyRecords) throw new InvalidOperationException("模拟 Notion 查询失败");
        return Task.FromResult<IReadOnlyList<NotionDailyRecordItem>>(DailyRecords.ToList());
    }

    public Task<string> CreateDailyRecordAsync(
        string dailyDbId, string date, string gameName, int durationMinutes, string? gamePageId,
        string? iconUrl = null)
    {
        CreatedDailyRecordTitles.Add(gameName);
        CreatedDailyRecordIcons.Add(iconUrl);
        return Task.FromResult("created-page");
    }

    /// <summary>每次 CreateDailyRecordAsync 收到的 page icon，用于断言"顺带设了总表图标"。</summary>
    public List<string?> CreatedDailyRecordIcons { get; } = new();

    public List<(string PageId, int DurationMinutes)> UpdatedPages { get; } = new();

    /// <summary>每次 UpdateDailyRecordAsync 收到的**游戏名**（不是拼好的标题），用于断言"用的是总表名"。</summary>
    public List<string?> UpdatedTitles { get; } = new();

    /// <summary>每次 UpdateDailyRecordAsync 收到的 page icon。</summary>
    public List<string?> UpdatedIcons { get; } = new();

    public Task<bool> UpdateDailyRecordAsync(
        string pageId, int durationMinutes, string? gamePageId, string? gameName = null,
        string? iconUrl = null)
    {
        UpdatedPages.Add((pageId, durationMinutes));
        UpdatedTitles.Add(gameName);
        UpdatedIcons.Add(iconUrl);
        return Task.FromResult(true);
    }

    public List<(string PageId, string IconUrl)> IconUpdates { get; } = new();

    public Task<bool> SetPageIconAsync(string pageId, string imageUrl)
    {
        IconUpdates.Add((pageId, imageUrl));
        return Task.FromResult(true);
    }

    public Task<string> CreateGameMasterPageAsync(string gameDbId, string gameTitle)
        => Task.FromResult("created-master");

    public Task<bool> ArchivePageAsync(string pageId)
    {
        if (FailArchive) throw new InvalidOperationException("模拟 Notion 归档失败");
        ArchivedPageIds.Add(pageId);
        return Task.FromResult(true);
    }
}
