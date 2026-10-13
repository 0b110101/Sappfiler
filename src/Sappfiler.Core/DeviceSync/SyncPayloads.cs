using System.Text.Json;
using System.Text.Json.Serialization;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.DeviceSync;

/// <summary>Game 的同步载荷快照。</summary>
public sealed class GameSyncPayload
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = DeviceSyncConstants.CurrentSchemaVersion;

    /// <summary>跨设备 Game identity。</summary>
    [JsonPropertyName("global_id")]
    public string GlobalId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("platform_id")]
    public string PlatformId { get; set; } = "";

    [JsonPropertyName("executable")]
    public string Executable { get; set; } = "";

    [JsonPropertyName("executable_path")]
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// 该游戏在 **Provider（Notion）侧的远端身份**（可选字段，向后兼容）。
    ///
    /// ⚠️ 用途**只有两个**：
    ///   1. 身份对账时检测"双方绑定了**不同**的 Notion 页面" → 延后，绝不替用户自动选一个；
    ///   2. 未来诊断/展示。
    ///
    /// **不得**用它写入本地 <c>games.notion_page_id</c> —— Provider identity 由本机的
    /// Provider 逻辑（auto-link 等）本地决定，不能因为远端带了就照抄
    /// （Provider identity ≠ Sappfiler global_id）。
    /// </summary>
    [JsonPropertyName("notion_page_id")]
    public string? NotionPageId { get; set; }

    /// <summary>
    /// 封面图的**内容寻址引用**（schema v2，可选）：远端 <c>{根}/blobs/&lt;sha256&gt;</c>。
    ///
    /// ⚠️ 只保存 hash —— 图片本体走**独立二进制通道**，绝不 Base64 塞进 JSON batch。
    /// ⚠️ 本地没有封面时**不写该字段**（绝不制造假 blob / 空 blob）。
    /// </summary>
    [JsonPropertyName("cover_blob_hash")]
    public string? CoverBlobHash { get; set; }

    /// <summary>图标同理（本地没有图标文件时为空）。</summary>
    [JsonPropertyName("icon_blob_hash")]
    public string? IconBlobHash { get; set; }
}

/// <summary>
/// Session 的同步载荷快照。
/// ⚠️ 游戏只用 <c>game_global_id</c> 引用 —— 绝不能出现本地 INTEGER id。
/// </summary>
public sealed class SessionSyncPayload
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = DeviceSyncConstants.CurrentSchemaVersion;

    [JsonPropertyName("global_id")]
    public string GlobalId { get; set; } = "";

    [JsonPropertyName("game_global_id")]
    public string GameGlobalId { get; set; } = "";

    /// <summary>产生这条会话的设备（多设备归属的唯一依据）。</summary>
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("process_name")]
    public string ProcessName { get; set; } = "";

    [JsonPropertyName("started_at_utc")]
    public string StartedAtUtc { get; set; } = "";

    [JsonPropertyName("ended_at_utc")]
    public string? EndedAtUtc { get; set; }

    [JsonPropertyName("duration_seconds")]
    public int DurationSeconds { get; set; }

    /// <summary>exact / estimated，见 <see cref="Models.TimePrecision"/>。</summary>
    [JsonPropertyName("time_precision")]
    public string TimePrecision { get; set; } = GameTimeTracker.Core.Models.TimePrecision.Exact;
}

/// <summary>
/// 日时长调整的**来源**（写入 <c>daily_duration_adjustments.source</c> 与 v2 payload）。
/// 它是审计信息，不参与合并规则 —— 合并永远是"同 (game, 统计日) 上所有设备的 adjustment 相加"。
/// </summary>
public static class DailyAdjustmentSources
{
    /// <summary>用户在 UI 上手工录入的日时长增量。</summary>
    public const string Manual = "manual";

    /// <summary>Notion 导入路径产生的增量（<c>SyncDailyRecordFromNotionAsync</c>）。</summary>
    public const string Notion = "notion";

    /// <summary>
    /// **历史残差**（一次性迁移）：该 (game, 统计日) **本库已经没有任何会话可查**，
    /// 但当天确实落在"应用已有会话能力"的时间窗内 ⇒ 极可能是**曾经被会话统计出来、会话已不可恢复**的值
    /// （2026-10-09 只读取证：9/23 Deathloop 7872s 曾由真实会话产生，数据根切换后会话只留在另一份库里）。
    ///
    /// 依据公式：<c>max(0, daily.duration_seconds − Σ sessions.duration_seconds)</c>。
    /// ⚠️ 它是**混合语义**：既可能是丢失的会话时长，也可能混入了 Notion 往返的量化误差（±30~60s/行）。
    /// </summary>
    public const string HistoricalResidual = "historical_residual";

    /// <summary>
    /// **Notion 侧外部声明**（一次性迁移）：这个值不可能是本机算出来的 ——
    ///   · 该 (game, 统计日) **本库没有任何会话**，且日期**早于本库最早会话日**（当时应用还没有会话数据）；或
    ///   · 该日**已经有会话派生秒数**，而 <c>daily_summary</c> 仍高于它（会话之外另行声明的时间）。
    /// 例：9/13 Deathloop 7.78h（早于最早会话 9/17）、10/6 Halo 的 34 分钟（当天已有会话）。
    /// </summary>
    public const string NotionExternal = "notion_external";

    /// <summary>
    /// ⚠️ 已废弃的历史来源名（v2 早期只用过它，语义过粗：把"会话不可恢复的残差"与
    /// "Notion 侧声明"混成了一个词）。**不再用于新建**；migration 007 会把既有行改写为上两者之一。
    /// 保留常量只为让老库/老 payload 能被识别。
    /// </summary>
    public const string Legacy = "legacy";
}

/// <summary>
/// 日时长调整（DailyDurationAdjustment）的同步载荷快照（<b>schema v2</b>）。
///
/// 语义：**设备产生的不可变历史事实** —— "这台设备在某个统计日，为某个游戏额外声明了 N 秒"。
///
/// ⚠️ 它**不**改写 Session（不改 <c>duration_seconds</c>、不合并会话），
///   也**不**把 <c>daily_summary</c> 变成跨设备实体；最终统计仍是本地派生：
/// <code>
/// daily_summary(game, date) = Σ session-derived duration + Σ adjustments(game, date, 全部设备)
/// </code>
///
/// ⚠️ 合并规则是**相加**：A 设备 +30 分钟、B 设备 +20 分钟 → 最终 +50 分钟。
///    **不取 max、不做 LWW**（两台设备各自的手工时长都是真事实，不能被对方覆盖掉）。
/// </summary>
public sealed class DailyAdjustmentSyncPayload
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = DeviceSyncConstants.CurrentSchemaVersion;

    /// <summary>
    /// 跨设备稳定 identity：<c>sha256("adjustment|" + device_id + "|" + game_global_id + "|" + 统计日 + "|" + source)</c>。
    /// 稳定 ⇒ 重复 Pull / 重复导入天然幂等（同一条事实永远同一个 id）；含 device_id ⇒ 不同设备是不同事实。
    /// </summary>
    [JsonPropertyName("global_id")]
    public string GlobalId { get; set; } = "";

    /// <summary>所属游戏（跨设备身份，绝不含本地 INTEGER id）。</summary>
    [JsonPropertyName("game_global_id")]
    public string GameGlobalId { get; set; } = "";

    /// <summary>统计日（会计日，<c>yyyy-MM-dd</c>）。</summary>
    [JsonPropertyName("statistical_date")]
    public string StatisticalDate { get; set; } = "";

    /// <summary>产生这条事实的设备（多设备相加的依据，也是审计依据）。</summary>
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    /// <summary>增量秒数（**必须为正数**：adjustment 只做加法，负数会让对端统计倒退）。</summary>
    [JsonPropertyName("duration_seconds")]
    public int DurationSeconds { get; set; }

    /// <summary>manual / notion / legacy，见 <see cref="DailyAdjustmentSources"/>。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = DailyAdjustmentSources.Manual;

    [JsonPropertyName("created_at_utc")]
    public string CreatedAtUtc { get; set; } = "";

    /// <summary>
    /// <b>撤销标记</b>（2026-10-11 新增，schema 3）：这条声明已被其产生设备**撤回**
    /// （典型场景：用户在 Notion 侧删除了对应的每日记录页面）。
    ///
    /// <para>
    /// ⚠️ 撤销**沿用同一个 identity**（<c>device + game + 统计日 + source</c>）——
    /// 身份不变才能精确指向"被撤回的那一条"，绝不误伤别的来源、别的日期、别的设备；
    /// <see cref="DurationSeconds"/> 恒为 0。
    /// 对端收到后**删除该 identity 的声明并重算 `daily_summary`**（派生缓存因此可以变小）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 这不是"把删除偷偷写成 0 秒记录"：0 秒仍是一条**有效声明**（会参与累加与收敛），
    /// 而撤销表达的是"这条声明已不存在"。两者语义不同，必须在协议层可区分，
    /// 所以用独立字段而不是 0 值来表达。
    /// </para>
    /// </summary>
    [JsonPropertyName("revoked")]
    public bool Revoked { get; set; }
}

/// <summary>
/// <b>每日游玩分片排除事实</b>（2026-10-11，schema v2 实体扩展）。
///
/// <para>
/// 语义：<c>session_global_id</c> 这条会话在 <c>statistical_date</c> 这个**会计日**上的
/// **时长分片不计入统计**（既不计秒数，也不计场次）。
/// </para>
///
/// <para>
/// 为什么需要独立事实（而不是复用 Session 的 Delete / Adjustment）：
/// <list type="bullet">
///   <item><c>sessions</c> 一条会话是**整段**事实，其秒数按结算点被拆分到最多两个会计日。
///        用整条 Session 的墓碑表达"只删某一天的分片"，会连带删掉另一个会计日的有效时长 ✗；</item>
///   <item><c>daily_duration_adjustments</c> 是**正数加项**、identity 里没有会话维度，
///        既无法表达"作废某条会话的某日分片"，也违反"不得把 daily 旧值伪造成声明"的契约 ✗；</item>
///   <item><c>daily_summary</c> 是派生缓存，把它当同步实体同样违反契约 ✗。</item>
/// </list>
/// 所以引入这条**普通不可变事实**：它只描述"哪一分片不算"，不改写任何会话的游玩事实。
/// </para>
///
/// <para>
/// ⚠️ 只作废**明确列出**的会话分片：删除之后新产生的会话（新 <c>global_id</c>）永不受影响；
/// 同一会话在其他会计日的分片也永不受影响。合并语义是**并集**（同一 identity 重复到达收敛为一条）。
/// </para>
/// </summary>
public sealed class DailyPlayExclusionSyncPayload
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = DeviceSyncConstants.CurrentSchemaVersion;

    /// <summary>
    /// 跨设备稳定 identity：
    /// <c>sha256("play-exclusion|" + device_id + "|" + game_global_id + "|" + 统计日 + "|" + session_global_id)</c>。
    /// 含 device_id ⇒ 不同设备各自表达自己的删除意图、互不覆盖；稳定 ⇒ 重复 Pull 天然幂等。
    /// </summary>
    [JsonPropertyName("global_id")]
    public string GlobalId { get; set; } = "";

    /// <summary>所属游戏（跨设备身份，绝不含本地 INTEGER id）。</summary>
    [JsonPropertyName("game_global_id")]
    public string GameGlobalId { get; set; } = "";

    /// <summary>被作废分片所属的会计日（<c>yyyy-MM-dd</c>）。</summary>
    [JsonPropertyName("statistical_date")]
    public string StatisticalDate { get; set; } = "";

    /// <summary>被作废分片所属会话的跨设备 identity（<c>sessions.global_id</c>）。</summary>
    [JsonPropertyName("session_global_id")]
    public string SessionGlobalId { get; set; } = "";

    /// <summary>产生这条删除事实的设备（审计依据；也保证两端各自表达、互不覆盖）。</summary>
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("created_at_utc")]
    public string CreatedAtUtc { get; set; } = "";
}

/// <summary>
/// 同步载荷的序列化 / 解析。**唯一真相**：payload 的字段名与 schema 版本在这里定义一次，
/// 由 Outbox 写入方（2d）与 SyncEngine 的落地方共用，避免两边各写一套 JSON 形状。
///
/// 版本策略：解析时遇到不认识的 <c>schema_version</c> **一律拒绝**（返回 false + 原因），
/// 让调用方跳过该条而不是按旧结构硬解 —— 宁可不同步，也不能把数据解错。
/// </summary>
public static class SyncPayloads
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(GameSyncPayload payload) => JsonSerializer.Serialize(payload, Options);

    public static string Serialize(SessionSyncPayload payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>序列化 v2 的日时长调整事实。</summary>
    public static string Serialize(DailyAdjustmentSyncPayload payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>序列化每日游玩分片排除事实（schema v2 实体扩展）。</summary>
    public static string Serialize(DailyPlayExclusionSyncPayload payload) => JsonSerializer.Serialize(payload, Options);

    /// <param name="schemaVersion">payload 里声明的版本；无法解析时为 -1（用于 Deferred 台账留痕）。</param>
    public static bool TryParseGame(string? json, out GameSyncPayload? payload, out int schemaVersion, out string? error)
    {
        payload = null;
        schemaVersion = -1;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "payload 为空";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<GameSyncPayload>(json, Options);
            if (parsed is null)
            {
                error = "payload 反序列化为空";
                return false;
            }

            schemaVersion = parsed.SchemaVersion;

            // v1 与 v2 都接受：v2 只是"多了 DailyAdjustment 实体 + Game 的 blob hash 引用"，
            // Game 载荷本身的字段与语义逐字未变（两台设备混版本期间必须能互相消费）。
            if (!DeviceSyncConstants.IsSupportedSchemaVersion(parsed.SchemaVersion))
            {
                error = $"不支持的 schema_version={parsed.SchemaVersion}（本机支持 {DeviceSyncConstants.MinSupportedSchemaVersion}~{DeviceSyncConstants.CurrentSchemaVersion}）";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.GlobalId))
            {
                error = "payload 缺少 global_id";
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"payload 不是合法 JSON：{ex.Message}";
            return false;
        }
    }

    /// <param name="schemaVersion">payload 里声明的版本；无法解析时为 -1（用于 Deferred 台账留痕）。</param>
    /// <summary>
    /// 只把 <c>schema_version</c> 抠出来（读不到返回 0）。
    /// **仅供 Deferred 台账留痕**，不要用它判断 payload 是否可用 —— 那是 TryParse* 的职责。
    /// </summary>
    public static int TryReadSchemaVersion(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema_version", out var version)
                && version.TryGetInt32(out var value)
                ? value
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public static bool TryParseSession(string? json, out SessionSyncPayload? payload, out int schemaVersion, out string? error)
    {
        payload = null;
        schemaVersion = -1;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "payload 为空";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<SessionSyncPayload>(json, Options);
            if (parsed is null)
            {
                error = "payload 反序列化为空";
                return false;
            }

            schemaVersion = parsed.SchemaVersion;

            // 同上：v1 / v2 都接受（Session 的四项不可变事实与字段在 v2 里一字未改）。
            if (!DeviceSyncConstants.IsSupportedSchemaVersion(parsed.SchemaVersion))
            {
                error = $"不支持的 schema_version={parsed.SchemaVersion}（本机支持 {DeviceSyncConstants.MinSupportedSchemaVersion}~{DeviceSyncConstants.CurrentSchemaVersion}）";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.GlobalId))
            {
                error = "payload 缺少 global_id";
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"payload 不是合法 JSON：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 解析 v2 的日时长调整事实。
    ///
    /// ⚠️ <b>只接受 v2 及以上</b>：v1 客户端根本没有这个实体类型，
    /// 用 v1 形状去硬解只会把"手工时长"解释成错的东西 —— 宁可 Deferred 也不猜。
    /// </summary>
    /// <param name="schemaVersion">payload 里声明的版本；无法解析时为 -1（用于 Deferred 台账留痕）。</param>
    public static bool TryParseDailyAdjustment(
        string? json, out DailyAdjustmentSyncPayload? payload, out int schemaVersion, out string? error)
    {
        payload = null;
        schemaVersion = -1;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "payload 为空";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<DailyAdjustmentSyncPayload>(json, Options);
            if (parsed is null)
            {
                error = "payload 反序列化为空";
                return false;
            }

            schemaVersion = parsed.SchemaVersion;

            if (!DeviceSyncConstants.IsSupportedSchemaVersion(parsed.SchemaVersion) || parsed.SchemaVersion < 2)
            {
                error = $"日时长调整需要 schema_version>=2（收到 {parsed.SchemaVersion}）";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.GlobalId) || string.IsNullOrWhiteSpace(parsed.GameGlobalId))
            {
                error = "payload 缺少 global_id / game_global_id";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.DeviceId))
            {
                error = "payload 缺少 device_id（adjustment 必须能追溯到产生它的设备）";
                return false;
            }

            if (!IsIsoDate(parsed.StatisticalDate))
            {
                error = $"statistical_date 不是 yyyy-MM-dd：{parsed.StatisticalDate}";
                return false;
            }

            // 只允许正增量：负数会让对端统计倒退，且"撤销"应当由用户显式删除，而不是靠负 adjustment 抵消。
            // ⚠️ 2026-10-11：**撤销（revoked=true）允许 0 秒** —— 它表达"这条声明已不存在"，
            //    与"0 秒的有效声明"是两件不同的事（后者仍被拒绝）。其余 0 / 负数一律拒绝。
            if (parsed.DurationSeconds <= 0 && !(parsed.Revoked && parsed.DurationSeconds == 0))
            {
                error = $"duration_seconds 必须为正数（收到 {parsed.DurationSeconds}）";
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"payload 不是合法 JSON：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 解析<b>每日游玩分片排除事实</b>。
    ///
    /// ⚠️ 只接受 v2 及以上（与 <see cref="TryParseDailyAdjustment"/> 同一判据）：
    /// v1 客户端没有这个实体类型，用旧形状硬解只会把"删除"解释成别的东西 —— 宁可 Deferred 也不猜。
    /// </summary>
    /// <param name="schemaVersion">payload 里声明的版本；无法解析时为 -1（用于 Deferred 台账留痕）。</param>
    public static bool TryParseDailyPlayExclusion(
        string? json, out DailyPlayExclusionSyncPayload? payload, out int schemaVersion, out string? error)
    {
        payload = null;
        schemaVersion = -1;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "payload 为空";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<DailyPlayExclusionSyncPayload>(json, Options);
            if (parsed is null)
            {
                error = "payload 反序列化为空";
                return false;
            }

            schemaVersion = parsed.SchemaVersion;

            if (!DeviceSyncConstants.IsSupportedSchemaVersion(parsed.SchemaVersion) || parsed.SchemaVersion < 2)
            {
                error = $"每日游玩分片排除需要 schema_version>=2（收到 {parsed.SchemaVersion}）";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.GlobalId)
                || string.IsNullOrWhiteSpace(parsed.GameGlobalId)
                || string.IsNullOrWhiteSpace(parsed.SessionGlobalId))
            {
                error = "payload 缺少 global_id / game_global_id / session_global_id";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.DeviceId))
            {
                error = "payload 缺少 device_id（排除事实必须能追溯到产生它的设备）";
                return false;
            }

            if (!IsIsoDate(parsed.StatisticalDate))
            {
                error = $"statistical_date 不是 yyyy-MM-dd：{parsed.StatisticalDate}";
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"payload 不是合法 JSON：{ex.Message}";
            return false;
        }
    }

    /// <summary>严格 <c>yyyy-MM-dd</c>（统计日是**日期**，不是时间戳）。</summary>
    private static bool IsIsoDate(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && DateTime.TryParseExact(
               text, "yyyy-MM-dd",
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.None,
               out _);
}
