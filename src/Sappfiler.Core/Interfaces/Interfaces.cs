using GameTimeTracker.Core.DeviceSync;
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

    /// <summary>
    /// **删除一条历史游玩记录**（某 (游戏, 会计日) 的记录，2026-10-11 粒度重写）：
    ///
    /// <para>
    /// 事实层按 <c>(游戏, 会计日, 会话)</c> 粒度**作废该日的时长分片**（<c>daily_play_exclusions</c>
    /// + 同步实体 <c>DailyPlayExclusion</c>）；同一条会话在**其它**会计日的分片分毫不动
    /// （<c>sessions</c> 是整段事实、其秒数按结算点被拆到最多两个会计日，所以删除粒度不可能等于整条会话）。
    /// </para>
    ///
    /// <para>
    /// 同时撤回该日外部声明（<c>historical_residual</c> 受保护、不撤）、为所有受影响日期写墓碑并入队、
    /// **在同一个 SQLite 事务里**重算全部受影响日期（目标日 + 被作废分片所在的其它日）。
    /// 若删除后该日仍有无法归属的残差（或存在进行中的会话），结果会标记 <c>FullyDeleted = false</c>，
    /// 界面必须显示"部分删除"而不是完全成功。
    /// </para>
    ///
    /// <para>
    /// <b>删除范围（产品语义，必须与界面提示一致）</b>：删除的是**删除时刻本机已知的**、
    /// 归属于该会计日的**已结束**会话分片。因此：
    /// <list type="bullet">
    ///   <item>其它设备上**尚未同步到本机**的会话分片不在范围内 —— 它们同步过来后会**正常计入**该日期
    ///         （那是此前未同步的合法时长，**不是**"被删除的数据复活"）；</item>
    ///   <item>进行中的会话不在范围内（时长尚未定型）—— 结束游戏后可**再次删除**该记录以完成清理；</item>
    ///   <item>"删除"**不是**"永久禁止该日出现时长"：之后真正发生的新游玩照常记录与统计。</item>
    /// </list>
    /// </para>
    /// </summary>
    Task<PlayRecordDeletionResult> DeletePlayRecordAsync(int dailySummaryId);

    /// <summary>
    /// 删除对账 A 段专用：Notion 页面已确认删除 ⇒ 在**同一事务**里完成
    /// 「撤回本机声明（墓碑 + 队列广播，本机声明已不存在也照做）」+「事实层删除（会话作废 + 删派生行）」。
    /// 要么全部提交、要么全部回滚；本地照删，不依赖"跳过删除"换取重试（撤销靠队列重试到 ACK）。
    /// </summary>
    Task<PlayRecordDeletionResult> ApplyRemoteDeletedDailyAsync(int dailySummaryId);

    /// <summary>
    /// <b>撤回本机某条外部时长声明</b>（2026-10-11，schema 3）。
    ///
    /// <para>
    /// 语义：这条声明的**外部来源已经不存在**（例如 Notion 每日记录页面被用户删除）⇒
    /// 它不该继续作为有效统计事实存在。实现：删除该 identity（设备+游戏+统计日+来源）的声明、
    /// 按事实重算 <c>daily_summary</c>（派生缓存可以因此变小）、并把撤销作为**同一 identity**
    /// 的同步事实入队，让对端也收敛（对端只删这一条 identity，不会碰别的来源/日期/设备，也不会碰 Session）。
    /// </para>
    ///
    /// <para>幂等：本机没有这条声明 ⇒ no-op 返回 false；队列已有该 identity ⇒ 不重复入队。</para>
    /// </summary>
    Task<bool> RevokeLocalDailyAdjustmentAsync(int gameId, string statisticalDate, string source);
    /// <summary>把被删除的数据写入本地归档表（物理删除前留底，便于人工找回）。</summary>
    Task ArchiveDeletedAsync(string kind, string source, string reference, string payloadJson);
    /// <summary>清理目录缓存中已不在 Notion 总表里的行（让 game_catalog 成为总表的忠实快照）。</summary>
    Task<int> DeleteCatalogItemsNotInAsync(IEnumerable<string> pageIds);

    // Sessions
    Task<GameSession> CreateSessionAsync(int gameId, int pid, string processName, DateTime startTime);
    Task<IReadOnlyList<GameSession>> GetActiveSessionsAsync();
    Task UpdateSessionHeartbeatAsync(int sessionId, DateTime heartbeatTime, int durationSeconds);
    Task EndSessionAsync(int sessionId, DateTime endTime, int durationSeconds);

    /// <summary>
    /// **结束会话 + 落尾段每日时长 + 重算场次 + 生成同步 Outbox，全部在一个 SQLite 事务里**（Phase 2d）。
    ///
    /// 事务内顺序（用户 2026-10-03 冻结）：
    /// 确保 game.global_id → 确保 session.global_id / device_id → 写 started/ended UTC
    /// → 落尾段 daily_summary → 重算 session_count → 结束 session
    /// → **用事务内最终确定的数据**生成 immutable payload 快照 → 写 sync_queue → COMMIT。
    ///
    /// 三条铁律：已存在的 global_id 绝不重新生成；payload 绝不能用事务前读到的旧数据生成；
    /// 任一步失败 → 整体回滚，且会话仍由现有恢复机制处理（**本地计时永远优先于同步**）。
    ///
    /// ⚠️ 心跳路径不经过这里：运行中的会话只落本地 daily_summary，结束时才产生同步事实。
    /// </summary>
    Task<EndSessionOutboxResult> EndSessionWithOutboxAsync(
        int sessionId,
        DateTime endTime,
        int durationSeconds,
        IReadOnlyList<DailyDurationDelta> dailyDeltas);
    Task CleanupStaleSessionsAsync(TimeSpan staleThreshold);

    /// <summary>
    /// **日统计一致性自愈**（2026-10-11 A/B 实机对差根因修复）。
    ///
    /// <para>
    /// 从 canonical 事实（<c>sessions</c> + <c>daily_duration_adjustments</c>）重新派生
    /// <c>daily_summary</c> 的**缺行与偏差**：只升不降（来源不明的历史残差原样保留并报告）、
    /// 孤儿事实只报告不推断归属、重复运行幂等；返回**实际修复的行数**。
    /// </para>
    ///
    /// <para>
    /// 默认**不恢复**"被 Notion 删除对账有意删除"的行（避免把用户已经删掉的 Notion 页面又建回来），
    /// 这类行只进入审计报告，等人工裁定。
    /// </para>
    /// </summary>
    Task<int> ReconcileDailySummariesAsync(bool apply = true);

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
    Task<int> GetConsecutiveStreakDaysAsync(DateTime referenceDate, int cutoffHour = 24);
    Task<IReadOnlyDictionary<int, string>> GetEarliestPlayDatesAsync();

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

    /// <summary>删除一个 settings 键（用于让失效的「封面 → FileUpload」映射能重新上传）。</summary>
    Task DeleteSettingAsync(string key);

    /// <summary>
    /// 按 <c>sessions.process_name</c> 统计次数（本体裁决的**历史证据**，只读，不做任何写入、不改 schema）。
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetSessionProcessCountsAsync();
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
    /// <remarks>
    /// canonical seconds（2026-10-09）：<paramref name="durationSeconds"/> 非空时**以秒为准**，
    /// 一次换算成小时（<c>round(seconds/3600, 2)</c>）再写标题与「时长」属性；
    /// 为 null（历史/标题规范化等不写数值的路径）才退回旧的分钟口径。
    /// </remarks>
    /// <param name="iconFileUploadId">
    /// Daily 页面图标走 **Notion File Upload**（2026-10-06）：非空时把该 file_upload 附着为页面图标；
    /// 此时 <paramref name="iconUrl"/> 应为 null（两者互斥，Daily 图标只来自本地封面文件）。
    /// </param>
    Task<string> CreateDailyRecordAsync(string dailyDbId, string date, string gameName, int durationMinutes, string? gamePageId, string? iconUrl = null, int? durationSeconds = null, string? iconFileUploadId = null);

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
    /// <param name="durationSeconds">
    /// canonical seconds（2026-10-09）：非空时以秒为准换算小时写入标题与「时长」属性。
    /// </param>
    /// <param name="iconFileUploadId">
    /// Daily 页面图标走 Notion File Upload（2026-10-06）：非空时把该 file_upload 附着为页面图标；
    /// 此时 <paramref name="iconUrl"/> 应为 null。
    /// </param>
    Task<bool> UpdateDailyRecordAsync(string pageId, int durationMinutes, string? gamePageId, string? gameName = null, string? iconUrl = null, bool writeDuration = false, string? titleOverride = null, int? durationSeconds = null, string? iconFileUploadId = null);

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
    Task<bool> UpdateDailyRecordTitleAsync(
        string pageId, string title, string? iconUrl = null, string titlePropertyName = "游戏动态",
        string? iconFileUploadId = null);
    /// <summary>更新每日记录的「绑定状态」属性（已绑定 / 未绑定），绝不修改时长、日期或标题。</summary>
    Task<bool> UpdateDailyBindingStatusAsync(string pageId, string status);
    Task<string> CreateGameMasterPageAsync(string gameDbId, string gameTitle, string? iconUrl = null, string? coverUrl = null);
    /// <summary>把页面移入 Notion 回收站（archived）。这是 Notion API 唯一的"删除"方式，30 天内可恢复。</summary>
    Task<bool> ArchivePageAsync(string pageId);

    /// <summary>把总表页面的 page icon 设置为 external 图片链接（仅对未设图标的页面调用）。</summary>
    Task<bool> SetPageIconAsync(string pageId, string imageUrl);

    /// <summary>
    /// 2j 属性探测：该用户的总表里是否存在「已忽略」Checkbox 属性。
    /// 只有成功查询过总表之后才有意义；<c>false</c> = 不存在 → 忽略状态同步**整段跳过**。
    /// </summary>
    bool SupportsGameIgnoredProperty { get; }

    /// <summary>
    /// 2j：把总表页面的「已忽略」Checkbox 设为指定值。
    /// 属性不存在时**直接返回 false，绝不写入**（老用户零影响）。
    /// </summary>
    Task<bool> UpdateGameMasterIgnoredAsync(string pageId, bool ignored);

    /// <summary>
    /// 新建 Notion File Upload（<c>POST /v1/file_uploads</c>，single_part）。
    /// 返回 <c>file_upload_id</c>；失败返回 null。
    /// </summary>
    /// <remarks>2026-10-06 实测：在 Notion-Version <c>2022-06-28</c> 下即可用，**无需升级 API pin**。</remarks>
    Task<string?> CreateFileUploadAsync(string filename, string contentType);

    /// <summary>
    /// 把本地文件内容发送到该 File Upload（<c>POST /v1/file_uploads/{id}/send</c>，
    /// multipart/form-data，字段名固定 <c>file</c>）。返回是否最终 <c>status = uploaded</c>。
    /// </summary>
    Task<bool> SendFileUploadAsync(string fileUploadId, string filePath, string contentType);

    /// <summary>
    /// 查询 File Upload 状态（<c>GET /v1/file_uploads/{id}</c>）。
    /// 查询失败或对象不存在时返回 null —— 调用方据此判定"该映射已不可用"。
    /// </summary>
    Task<string?> GetFileUploadStatusAsync(string fileUploadId);

    /// <summary>
    /// 检查指定 page_id 的页面是否已在 Notion 侧被删除（移入回收站或已不存在）。
    /// 用于对账时二次确权，防止因为 Notion 索引延迟或查询过滤导致本地记录被误删。
    /// </summary>
    Task<bool> IsPageDeletedAsync(string pageId);
}

public record DailyTitleNormalizationResult(
    int TotalProcessed,
    int UpdatedCount,
    int SkippedCount,
    int ErrorCount,
    string SummaryMessage);

public interface INotionSyncService
{
    event EventHandler<string>? SyncStatusChanged;

    /// <summary>Notion 是否已配置（token 与两个 database id 齐全）。删除确认文案需要它。</summary>
    bool IsNotionConfigured { get; }

    Task<int> SyncPendingDailyRecordsAsync();
    Task<int> PullDailyRecordsFromNotionAsync();
    Task<int> BackfillRelationsAsync();

    Task<int> AutoLinkGamesFromCatalogAsync();
    Task RefreshGameCatalogCacheAsync();

    /// <summary>
    /// 2j：把最近一次总表快照里的「已忽略」应用到本地（幂等、不访问网络）。
    /// **必须在 Pull 之后也调用一次**：新机首次恢复时游戏行是 Pull 期间才创建的。
    /// </summary>
    Task<int> ApplyCatalogIgnoredStateAsync();

    /// <summary>
    /// 2j：本地忽略/恢复后，把状态写回总表的「已忽略」Checkbox。
    /// 设计为 **fire-and-forget** 调用（UI 不等待）；失败只记日志、绝不上抛，
    /// 游戏未绑定总表或该库没有此属性时返回 false（这是正常的降级，不是错误）。
    /// </summary>
    Task<bool> PushGameIgnoredStateAsync(int gameId, bool ignored);
    Task LinkGameRelationAsync(int gameId, string notionPageId);
    Task<string> CreateGameMasterAndLinkAsync(int gameId, string gameName);

    /// <summary>
    /// 手工触发：一键扫描远端每日时长表中的历史记录，将记录标题规范化为「游戏名 · 时长 h」标准格式，
    /// 并对齐总表游戏名与页面图标。绝不修改时长数值与日期，未关联总表的记录安全跳过。
    /// </summary>
    Task<DailyTitleNormalizationResult> NormalizeHistoricalDailyTitlesAsync(
        IProgress<(int current, int total, string currentItem)>? progress = null,
        CancellationToken ct = default);

    // ---- 双向删除 ----
    /// <summary>删除一个游戏：本地连记录一起删，并把该游戏在 Notion 每日表的记录（可选：总表条目）一并归档。</summary>
    Task<NotionDeleteResult> DeleteGameEverywhereAsync(int gameId, bool deleteMasterEntry);
    /// <summary>删除某天的每日记录：本地删除该行，并归档 Notion 上对应的页面。</summary>
    Task<NotionDeleteResult> DeleteDailyRecordEverywhereAsync(int dailySummaryId);
    /// <summary>对账 Notion 侧被删掉的记录，本地跟着删（在刷新目录缓存之后调用）。</summary>
    Task<NotionReconcileResult> ReconcileNotionDeletionsAsync();
}
