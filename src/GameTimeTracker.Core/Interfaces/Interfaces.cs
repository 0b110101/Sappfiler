using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.Interfaces;

public interface IDatabaseRepository
{
    // Game identities & records
    Task<GameRecord> GetOrCreateGameAsync(GameIdentity identity);
    Task<GameRecord?> GetGameByIdAsync(int id);
    Task<GameRecord?> GetGameByPlatformIdAsync(string platform, string platformId);
    Task<GameRecord?> GetGameByPathOrExeAsync(string executablePath, string executable);
    Task UpdateGameNotionIdAsync(int gameId, string notionPageId);
    Task UpdateGameStatusAsync(int gameId, string status);
    Task<IReadOnlyList<GameRecord>> GetAllGamesAsync();
    Task<IReadOnlyList<GameRecord>> GetPendingGamesAsync();
    Task DeleteGameAsync(int gameId);

    // Deletion bookkeeping (双向删除用)
    /// <summary>删除某游戏前的影响面统计（会话数 / 每日汇总数 / 其中已同步到 Notion 的条数）。</summary>
    Task<GameDeletionImpact> GetGameDeletionImpactAsync(int gameId);
    /// <summary>本地「已同步到 Notion」的每日汇总（notion_page_id 非空），用于对账远端删除。</summary>
    Task<IReadOnlyList<DailySummary>> GetSyncedDailySummariesAsync();
    /// <summary>物理删除若干条每日汇总。</summary>
    Task<int> DeleteDailySummariesAsync(IEnumerable<int> ids);
    /// <summary>把被删除的数据写入本地归档表（物理删除前留底，便于人工找回）。</summary>
    Task ArchiveDeletedAsync(string kind, string source, string reference, string payloadJson);
    /// <summary>清理目录缓存中已不在 Notion 总表里的行（让 game_catalog 成为总表的忠实快照）。</summary>
    Task<int> DeleteCatalogItemsNotInAsync(IEnumerable<string> pageIds);

    // Sessions
    Task<GameSession> CreateSessionAsync(int gameId, int pid, string processName, DateTime startTime);
    Task<IReadOnlyList<GameSession>> GetActiveSessionsAsync();
    Task UpdateSessionHeartbeatAsync(int sessionId, DateTime heartbeatTime, int durationSeconds);
    Task EndSessionAsync(int sessionId, DateTime endTime, int durationSeconds);
    Task CleanupStaleSessionsAsync(TimeSpan staleThreshold);

    // Daily summaries & Stats
    Task AddSessionDurationToDailyAsync(string date, int gameId, int durationSeconds);
    Task<IReadOnlyList<DailySummary>> GetDailySummariesByDateAsync(string date);
    Task<IReadOnlyList<DailySummary>> GetDailySummariesRangeAsync(string startDate, string endDate);
    Task<IReadOnlyList<DailySummary>> GetPendingDailySummariesAsync();
    Task<IReadOnlyList<DailySummary>> GetDailySummariesByGameIdAsync(int gameId);
    Task<IReadOnlyList<DailySummary>> GetUnmappedDailySummariesAsync();
    Task UpdateDailySyncStatusAsync(int id, string syncStatus, string? notionPageId = null, string? errorMessage = null);
    Task<IReadOnlyList<DailySummary>> GetRecentDailyRecordsAsync(int limit = 10);
    Task<GameRecord?> GetGameByNotionIdAsync(string notionPageId);
    Task<DailySummary?> GetDailySummaryByIdAsync(int id);
    Task<int> SyncDailyRecordFromNotionAsync(NotionDailyRecordItem item);
    Task<IReadOnlyList<DailySummary>> GetTopGamesByDateAsync(string date, int limit = 5);

    /// <summary>
    /// 回写一条已同步每日记录在 Notion 侧的标题。用途：总表改名后回刷每日记录标题，
    /// 用它和期望标题比对即可判断"是否需要 PATCH"，避免每轮同步都无谓地打 Notion。
    /// </summary>
    Task<int> UpdateDailyRecordFromNotionAsync(string notionPageId, string title);

    // Notion Game Master Catalog Cache
    Task<IReadOnlyList<NotionGameCatalogItem>> GetCatalogItemsAsync();
    Task<NotionGameCatalogItem?> GetCatalogItemByPageIdAsync(string pageId);
    Task UpsertCatalogItemsAsync(IEnumerable<NotionGameCatalogItem> items);
    Task ClearCatalogCacheAsync();

    // App Settings
    Task<string?> GetSettingAsync(string key);
    Task SetSettingAsync(string key, string value);
}

public interface IProcessMonitor
{
    Task<IReadOnlyList<DetectedProcess>> ScanRunningProcessesAsync();
}

public interface IGameDetector
{
    string PlatformName { get; }
    int Priority { get; } // lower = higher priority
    GameIdentity? DetectGame(DetectedProcess process);
}

public interface IGameMatcher
{
    string NormalizeTitle(string title);
    Task<string?> ResolveSteamChineseTitleAsync(string appId);
    IReadOnlyList<GameCandidate> MatchGame(string gameTitle, IReadOnlyList<NotionGameCatalogItem> catalog, string? steamAppId = null);
}

public interface INotionClient
{
    void UpdateToken(string token);
    Task<bool> TestConnectionAsync();
    Task<IReadOnlyList<NotionGameCatalogItem>> QueryGameMasterAsync(string databaseId);
    Task<IReadOnlyList<NotionDailyRecordItem>> QueryDailyRecordsAsync(string databaseId);
    Task<string> CreateDailyRecordAsync(string dailyDbId, string date, string gameTitle, int durationMinutes, string? gamePageId, string? iconUrl = null);

    /// <summary>
    /// 更新每日记录。iconUrl 非空时一并把该页面 icon 设为 external 图片；
    /// gameTitle 非空时按「gameTitle + 当前时长」重算并写回页面标题
    /// （所以传 null 才是"只改时长、不动标题"）。
    /// </summary>
    Task<bool> UpdateDailyRecordAsync(string pageId, int durationMinutes, string? gamePageId, string? gameTitle = null, string? iconUrl = null);
    Task<string> CreateGameMasterPageAsync(string gameDbId, string gameTitle);
    /// <summary>把页面移入 Notion 回收站（archived）。这是 Notion API 唯一的"删除"方式，30 天内可恢复。</summary>
    Task<bool> ArchivePageAsync(string pageId);

    /// <summary>把总表页面的 page icon 设置为 external 图片链接（仅对未设图标的页面调用）。</summary>
    Task<bool> SetPageIconAsync(string pageId, string imageUrl);
}

public interface INotionSyncService
{
    event EventHandler<string>? SyncStatusChanged;

    /// <summary>Notion 是否已配置（token 与两个 database id 齐全）。删除确认文案需要它。</summary>
    bool IsNotionConfigured { get; }

    Task<int> SyncPendingDailyRecordsAsync();
    Task<int> PullDailyRecordsFromNotionAsync();
    Task<int> BackfillRelationsAsync();

    /// <summary>
    /// 回刷：总表改名 / 补 page icon 后，把**已同步**的每日记录标题与图标跟着更新。
    /// 逐条比对本地快照，只有真的不一致才 PATCH，因此可以安全地放进每轮同步链。
    /// </summary>
    Task<int> RefreshDailyTitlesFromMasterAsync();
    Task<int> AutoLinkGamesFromCatalogAsync();
    Task RefreshGameCatalogCacheAsync();
    Task LinkGameRelationAsync(int gameId, string notionPageId);
    Task<string> CreateGameMasterAndLinkAsync(int gameId, string gameName);

    // ---- 双向删除 ----
    /// <summary>删除一个游戏：本地连记录一起删，并把该游戏在 Notion 每日表的记录（可选：总表条目）一并归档。</summary>
    Task<NotionDeleteResult> DeleteGameEverywhereAsync(int gameId, bool deleteMasterEntry);
    /// <summary>删除某天的每日记录：本地删除该行，并归档 Notion 上对应的页面。</summary>
    Task<NotionDeleteResult> DeleteDailyRecordEverywhereAsync(int dailySummaryId);
    /// <summary>对账 Notion 侧被删掉的记录，本地跟着删（在刷新目录缓存之后调用）。</summary>
    Task<NotionReconcileResult> ReconcileNotionDeletionsAsync();
}
