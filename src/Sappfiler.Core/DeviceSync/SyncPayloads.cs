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

            if (parsed.SchemaVersion != DeviceSyncConstants.CurrentSchemaVersion)
            {
                error = $"不支持的 schema_version={parsed.SchemaVersion}（本机支持 {DeviceSyncConstants.CurrentSchemaVersion}）";
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

            if (parsed.SchemaVersion != DeviceSyncConstants.CurrentSchemaVersion)
            {
                error = $"不支持的 schema_version={parsed.SchemaVersion}（本机支持 {DeviceSyncConstants.CurrentSchemaVersion}）";
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
}
