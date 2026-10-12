namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 同步 payload 的**构造入口**（唯一真相）。
///
/// 存在的理由：同一份快照会在两条路径上产生 ——
///   ① 会话结束时（<c>EndSessionWithOutboxAsync</c> 的事务内）；
///   ② 历史播种（backfill）时从库里 SELECT 出来。
/// 两边各写一份"字段怎么摆"的代码，早晚漂移；这里统一收口，
/// 真正决定 JSON 形状的仍然是 <see cref="SyncPayloads"/> 的序列化。
/// </summary>
public static class SyncPayloadFactory
{
    /// <summary>
    /// Game = **最小可落地身份快照**。
    ///
    /// <para>
    /// ⚠️ 2026-10-12 起**不再只是"每游戏一条"**：用户删除游戏后又在某台设备上重新启动它时，
    /// 该设备会再发一条 Game Create 表达"删除被撤销"（接收端按信封时间晚于墓碑判定，
    /// 见 <c>ApplyRemoteChangesWithCursorAsync</c> 的墓碑分支）。载荷形状与
    /// <c>schema_version</c> 均未变化，旧版本对端仍按原语义处理（因本地墓碑而延后，不会更坏）。
    /// </para>
    ///
    /// ⚠️ <c>executable</c>/<c>executable_path</c> 只是**快照**（远端新建行需要它才有意义），
    /// 不构成"字段级实时同步"：本机改这两个字段**不会**触发新的 Game change，
    /// canonical 行的字段仍以本机为准（2e 冻结：对账不动 canonical 的 exe/path）。
    /// </summary>
    public static string Game(
        string globalId, string name, string platform, string platformId,
        string executable, string executablePath, string? notionPageId,
        string? coverBlobHash = null, string? iconBlobHash = null)
        => SyncPayloads.Serialize(new GameSyncPayload
        {
            GlobalId = globalId,
            Name = name,
            Platform = platform,
            PlatformId = platformId,
            Executable = executable,
            ExecutablePath = executablePath,
            // 仅用于"双方绑定了不同页面 → Deferred"的冲突检测；远端**不会**回写本机 notion_page_id。
            NotionPageId = string.IsNullOrWhiteSpace(notionPageId) ? null : notionPageId,
            // v2：只带 hash（图片走独立 blob 通道）；本地没有封面/图标时保持为空。
            CoverBlobHash = string.IsNullOrWhiteSpace(coverBlobHash) ? null : coverBlobHash,
            IconBlobHash = string.IsNullOrWhiteSpace(iconBlobHash) ? null : iconBlobHash
        });

    /// <summary>Session = 已完成会话的不可变事实（只引用 <paramref name="gameGlobalId"/>，绝不含本地 INTEGER id）。</summary>
    public static string Session(
        string globalId, string gameGlobalId, string deviceId, string processName,
        string startedAtUtc, string? endedAtUtc, int durationSeconds, string timePrecision)
        => SyncPayloads.Serialize(new SessionSyncPayload
        {
            GlobalId = globalId,
            GameGlobalId = gameGlobalId,
            DeviceId = deviceId,
            ProcessName = processName,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = endedAtUtc,
            DurationSeconds = durationSeconds,
            TimePrecision = string.IsNullOrWhiteSpace(timePrecision)
                ? GameTimeTracker.Core.Models.TimePrecision.Exact
                : timePrecision
        });

    /// <summary>
    /// DailyAdjustment = **设备产生的不可变历史事实**（v2）：某设备在某统计日为某游戏声明了 <paramref name="durationSeconds"/> 秒增量。
    ///
    /// ⚠️ <paramref name="adjustmentGlobalId"/> 必须由 <see cref="DailyAdjustmentGlobalId"/> 生成（稳定、含设备身份），
    /// 调用方**不要**自己拼 Guid —— 否则重复导入会变成两条事实、跨设备相加后翻倍。
    /// </summary>
    public static string DailyAdjustment(
        string adjustmentGlobalId, string gameGlobalId, string statisticalDate,
        string deviceId, int durationSeconds, string source, string createdAtUtc)
        => SyncPayloads.Serialize(new DailyAdjustmentSyncPayload
        {
            GlobalId = adjustmentGlobalId,
            GameGlobalId = gameGlobalId,
            StatisticalDate = statisticalDate,
            DeviceId = deviceId,
            DurationSeconds = durationSeconds,
            Source = string.IsNullOrWhiteSpace(source) ? DailyAdjustmentSources.Manual : source,
            CreatedAtUtc = createdAtUtc
        });

    /// <summary>
    /// DailyPlayExclusion = **每日游玩分片排除事实**：<paramref name="sessionGlobalId"/> 这条会话在
    /// <paramref name="statisticalDate"/> 这个会计日上的时长分片**不计入统计**。
    ///
    /// ⚠️ <paramref name="exclusionGlobalId"/> 必须由 <see cref="DailyPlayExclusionGlobalId"/> 生成
    /// （稳定、含设备身份），调用方**不要**自己拼 Guid。
    /// </summary>
    public static string DailyPlayExclusion(
        string exclusionGlobalId, string gameGlobalId, string statisticalDate,
        string sessionGlobalId, string deviceId, string createdAtUtc)
        => SyncPayloads.Serialize(new DailyPlayExclusionSyncPayload
        {
            GlobalId = exclusionGlobalId,
            GameGlobalId = gameGlobalId,
            StatisticalDate = statisticalDate,
            SessionGlobalId = sessionGlobalId,
            DeviceId = deviceId,
            CreatedAtUtc = createdAtUtc
        });
}

/// <summary>
/// 日时长调整的**稳定 identity**（唯一实现，禁止各处自己拼）。
///
/// <code>sha256("adjustment|" + deviceId + "|" + gameGlobalId + "|" + 统计日 + "|" + source)[..32]</code>
///
/// 为什么这样设计：
///   · **稳定** ⇒ 同一条事实永远同一个 id ⇒ 重复 Pull / 重复导入天然幂等（不会相加两遍）；
///   · **含 deviceId** ⇒ A 设备的 (+30min) 与 B 设备的 (+20min) 是**不同事实**，落地后各自一条、相加得 +50min；
///   · **含 source** ⇒ Notion 导入与手工录入互不覆盖（同一设备同一天可以两者都有）。
/// </summary>
public static class DailyAdjustmentGlobalId
{
    public static string For(string deviceId, string gameGlobalId, string statisticalDate, string source)
        => SyncHashing.Sha256Hex(
            $"adjustment|{(deviceId ?? string.Empty).Trim()}|{(gameGlobalId ?? string.Empty).Trim()}"
            + $"|{(statisticalDate ?? string.Empty).Trim()}|{(source ?? string.Empty).Trim()}")[..32];
}

/// <summary>
/// 每日游玩分片排除事实的**稳定 identity**（唯一实现，禁止各处自己拼）。
///
/// <code>sha256("play-exclusion|" + deviceId + "|" + gameGlobalId + "|" + 统计日 + "|" + sessionGlobalId)[..32]</code>
///
/// 为什么这样设计：
///   · **含 sessionGlobalId** ⇒ 删除粒度为「(会计日, 会话) 分片」，
///     既不会连带作废同一会话在其他会计日的分片，也不会作废别的会话；
///   · **含 deviceId** ⇒ 两台设备各自表达自己的删除意图，互不覆盖（并集收敛）；
///   · **稳定** ⇒ 重复入队 / 重复 Pull / 重复删除天然幂等（同一条事实永远同一个 id）。
/// </summary>
public static class DailyPlayExclusionGlobalId
{
    public static string For(string deviceId, string gameGlobalId, string statisticalDate, string sessionGlobalId)
        => SyncHashing.Sha256Hex(
            $"play-exclusion|{(deviceId ?? string.Empty).Trim()}|{(gameGlobalId ?? string.Empty).Trim()}"
            + $"|{(statisticalDate ?? string.Empty).Trim()}|{(sessionGlobalId ?? string.Empty).Trim()}")[..32];
}
