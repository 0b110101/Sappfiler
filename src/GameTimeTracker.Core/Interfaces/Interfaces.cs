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
    /// 回写一条已同步每日记录在 Notion 侧的标题与图标快照。
    /// 用途：总表改名 / 补 icon 后回刷时，用它和期望值比对即可判断"是否需要 PATCH"，
    /// 避免每轮同步都无谓地打 Notion。两个快照必须一起写，否则会退化成每轮都 PATCH。
    /// </summary>
    Task<int> UpdateDailyRecordFromNotionAsync(string notionPageId, string title, string? iconUrl);

    // Notion Game Master Catalog Cache
    Task<IReadOnlyList<NotionGameCatalogItem>> GetCatalogItemsAsync();
    Task<NotionGameCatalogItem?> GetCatalogItemByPageIdAsync(string pageId);
    Task UpsertCatalogItemsAsync(IEnumerable<NotionGameCatalogItem> items);
    Task ClearCatalogCacheAsync();
    Task<Dictionary<string, List<string>>> GetGameGenresMapAsync();

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
    /// <summary>
    /// 新建每日记录。gameName 是**游戏名**（不是拼好的标题），标题由实现方按
    /// 「gameName · X h」拼装；iconUrl 非空时同时设置页面 icon。
    /// </summary>
    Task<string> CreateDailyRecordAsync(string dailyDbId, string date, string gameName, int durationMinutes, string? gamePageId, string? iconUrl = null);

    /// <summary>
    /// 更新每日记录。iconUrl 非空时一并把该页面 icon 设为 external 图片；
    /// gameName 非空时按「gameName + 当前时长」重算并写回页面标题
    /// （所以传 null 才是"只改时长、不动标题"）。
    /// </summary>
    /// <remarks>
    /// ⚠️ gameName 必须是**游戏名**，不是拼好的完整标题 ——
    /// 传完整标题进去会拼出「X · 0.7 h · 0.7 h」（后缀重复）。
    /// 该参数原名 gameTitle，这个歧义导致过真实 bug，故改名。
    /// </remarks>
    /// <param name="writeDuration">
    /// 是否写「单次时长」。**只有本地时长确实领先的推送路径才该写 true** ——
    /// 历史记录在 Notion 上的数值是权威，拿本地缓存覆盖它属于毁灭性错误。
    /// 只改"呈现"（标题 / 关系 / 图标）的调用必须传 false。
    /// </param>
    /// <param name="titleOverride">
    /// 可选的完整标题。若传入则直接使用，不按 gameName + durationMinutes 重拼（用于回刷保留原有时长后缀）。
    /// </param>
    Task<bool> UpdateDailyRecordAsync(string pageId, int durationMinutes, string? gamePageId, string? gameName = null, string? iconUrl = null, bool writeDuration = false, string? titleOverride = null);

    /// <summary>
    /// **只**改每日记录的标题（以及可选图标）—— 绝不碰「单次时长」「日期」这类数据属性。
    /// </summary>
    /// <remarks>
    /// 🚨 2026-09-19 事故：回刷标题原先走 <c>UpdateDailyRecordAsync</c>，而它**必然同时写
    /// 「单次时长」**（当年设计如此，因为标题串里含时长）。于是回刷拿**本地**分钟数覆盖了
    /// Notion 上原本的数值，一次运行改写 786 条，QA 表里的历史时长被篡改。
    ///
    /// 原则：Notion 侧的历史记录是**权威数据**，程序只允许改"呈现"（标题 / 图标），
    /// 不允许改"数值"。要改数值只能走 <c>UpdateDailyRecordAsync</c>（推送路径），
    /// 那条路径写的是本地确实领先的时长。
    /// </remarks>
    Task<bool> UpdateDailyRecordTitleAsync(string pageId, string title, string? iconUrl = null);
    /// <summary>更新每日记录的「绑定状态」属性（已绑定 / 未绑定），绝不修改时长、日期或标题。</summary>
    Task<bool> UpdateDailyBindingStatusAsync(string pageId, string status);
    Task<string> CreateGameMasterPageAsync(string gameDbId, string gameTitle);
    /// <summary>把页面移入 Notion 回收站（archived）。这是 Notion API 唯一的"删除"方式，30 天内可恢复。</summary>
    Task<bool> ArchivePageAsync(string pageId);

    /// <summary>把总表页面的 page icon 设置为 external 图片链接（仅对未设图标的页面调用）。</summary>
    Task<bool> SetPageIconAsync(string pageId, string imageUrl);

    /// <summary>
    /// 检查指定 page_id 的页面是否已在 Notion 侧被删除（移入回收站或已不存在）。
    /// 用于对账时二次确权，防止因为 Notion 索引延迟或查询过滤导致本地记录被误删。
    /// </summary>
    Task<bool> IsPageDeletedAsync(string pageId);
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
