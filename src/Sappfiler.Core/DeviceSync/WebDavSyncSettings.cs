using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// WebDAV 同步的**非敏感**配置（服务器地址 / 账号 / 同步目录 / 间隔 / 启用标志 / 播种进度）。
///
/// ⚠️ <b>密码绝不在这里</b>：它只进 <c>ISecretStore</c>。
/// 本类可以被日志、UI、诊断随意打印而不泄漏任何凭据。
///
/// ⚠️ 落在既有 <c>settings</c> 表（<c>GetSettingAsync</c>/<c>SetSettingAsync</c>），
/// **不新增表、不改 schema**（2h 设计 §14）。
/// </summary>
public sealed class WebDavSyncSettings
{
    /// <summary>坚果云预设端点（用户可直接用，也可改成自建 WebDAV）。</summary>
    public const string JianguoYunServerUrl = "https://dav.jianguoyun.com/dav/";

    /// <summary>默认同步目录（用户可改）。</summary>
    public const string DefaultSyncDirectory = "Sappfiler";

    /// <summary>默认自动同步间隔（分钟）。受坚果云 600 请求 / 30 分钟限制约束。</summary>
    public const int DefaultIntervalMinutes = 15;

    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 24 * 60;

    /// <summary>首次启用的"大量上传"确认阈值（超过则先让用户确认）。</summary>
    public const int BackfillConfirmThreshold = 200;

    /// <summary>每步播种最多入队多少条（等本轮推送成功后再继续下一步）。</summary>
    public const int BackfillStepSize = 200;

    public string ServerUrl { get; set; } = JianguoYunServerUrl;

    /// <summary>账号（坚果云为注册邮箱）。**属于个人信息，不写日志**。</summary>
    public string Account { get; set; } = "";

    public string SyncDirectory { get; set; } = DefaultSyncDirectory;

    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    public bool Enabled { get; set; }

    /// <summary>settings 表的键名（唯一真相，避免各处拼字符串）。</summary>
    public static class Keys
    {
        public const string Enabled = "device_sync_enabled";
        public const string ServerUrl = "device_sync_webdav_server_url";
        public const string Account = "device_sync_webdav_account";
        public const string SyncDirectory = "device_sync_webdav_directory";
        public const string IntervalMinutes = "device_sync_interval_minutes";
        public const string BackfillState = "device_sync_backfill_state";

        // ===== 最近一次同步结果（**仅追加**）=====
        // 用途：把"真实一轮 DeviceSyncRunResult"投影成可显示的计数与结论，落在既有 settings 表。
        // ⚠️ 「停用同步」**不清除**这些键：它们是历史结果，停用只关开关。

        /// <summary>最近一次同步结果的时刻（ISO-8601 UTC）。</summary>
        public const string LastOutcomeAtUtc = "device_sync_last_outcome_at_utc";

        /// <summary>最近一次同步的结论文案（成功 / 有延迟 / 存在 Deferred / 失败 / 未同步）。</summary>
        public const string LastOutcome = "device_sync_last_outcome";

        /// <summary>最近一轮推送成功的条数（= DeviceSyncRunResult.Completed）。</summary>
        public const string LastUploaded = "device_sync_last_uploaded";

        /// <summary>最近一轮远端落地条数（= DeviceSyncRunResult.Applied）。</summary>
        public const string LastDownloaded = "device_sync_last_downloaded";

        /// <summary>最近一轮"延迟重试"条数（= PushFailed + PushRejected；**不是**完全失败）。</summary>
        public const string LastDelayed = "device_sync_last_delayed";

        /// <summary>最近一轮 Deferred 条数（依赖缺失/身份冲突等，**不是**网络失败）。</summary>
        public const string LastDeferred = "device_sync_last_deferred";

        /// <summary>最近一轮被服务端永久拒绝的条数（= DeviceSyncRunResult.PushRejected）。</summary>
        public const string LastFailed = "device_sync_last_failed";
    }

    /// <summary>夹取到合法区间的自动同步间隔。</summary>
    public static int ClampInterval(int minutes)
        => minutes < MinIntervalMinutes ? MinIntervalMinutes
            : minutes > MaxIntervalMinutes ? MaxIntervalMinutes
            : minutes;

    /// <summary>
    /// 凭据在 <c>ISecretStore</c> 里的 key：<c>webdav/&lt;端点哈希12&gt;/password</c>。
    /// **端点变了 key 就变**（与 BackendId 同源）→ 天然隔离不同服务器的密码，且不会误用旧密码。
    /// </summary>
    public static string SecretKeyFor(string serverUrl, string syncDirectory)
        => $"webdav/{EndpointHash(serverUrl, syncDirectory)}/password";

    /// <summary>端点哈希（12 位 hex）：与 <c>WebDavSyncBackend.BackendId</c> 的后缀同源同算法。</summary>
    public static string EndpointHash(string serverUrl, string syncDirectory)
    {
        var normalized = (serverUrl ?? string.Empty).Trim() + "|" + (syncDirectory ?? string.Empty).Trim();
        return GameTimeTracker.Core.DeviceSync.SyncHashing.Sha256Hex(normalized)[..12];
    }
}

/// <summary>
/// 历史播种（backfill）进度：<b>可中断、可续跑、幂等</b>。
///
/// <code>
/// { "version":1, "done":false, "phase":"games", "last_game_id":123, "last_session_id":456, "enqueued":789 }
/// </code>
///
/// ⚠️ 严格两阶段：先全部 Game（按 id 升序），再全部 Session（按 id 升序）。
/// 顺序不能颠倒 —— 远端落地 Session 需要先有对应 Game（否则 Deferred(dependency_missing)）。
/// </summary>
public sealed class BackfillState
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    /// <summary><c>games</c> 或 <c>sessions</c>。</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = BackfillPhases.Games;

    [JsonPropertyName("last_game_id")]
    public long LastGameId { get; set; }

    [JsonPropertyName("last_session_id")]
    public long LastSessionId { get; set; }

    [JsonPropertyName("enqueued")]
    public int Enqueued { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static BackfillState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BackfillState();

        try
        {
            var parsed = JsonSerializer.Deserialize<BackfillState>(json);
            return parsed is null || parsed.Version != CurrentVersion ? new BackfillState() : parsed;
        }
        catch (JsonException)
        {
            // 进度丢失 = 重跑一遍。因为 change_id 是确定性的、入队幂等，重跑**安全**。
            return new BackfillState();
        }
    }
}

public static class BackfillPhases
{
    public const string Games = "games";
    public const string Sessions = "sessions";
}

/// <summary>哈希工具（确定性 change_id / 端点哈希共用同一实现，避免两处漂移）。</summary>
public static class SyncHashing
{
    public static string Sha256Hex(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// 播种用的**确定性 change_id / queue_id**：
    /// <code>sha256("backfill|Game|&lt;global_id&gt;")[..32]</code>
    /// 即便重复播种，也生成同一个 queue_id → <c>INSERT … ON CONFLICT DO NOTHING</c> → 端到端幂等。
    /// </summary>
    public static string BackfillQueueId(string entityType, string globalId)
        => Sha256Hex($"backfill|{entityType}|{globalId}")[..32];
}
