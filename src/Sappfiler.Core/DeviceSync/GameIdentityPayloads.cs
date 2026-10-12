using System.Text.Json;
using System.Text.Json.Serialization;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 跨设备身份关联（<see cref="SyncEntityType.GameIdentity"/>）的线格式载荷。
///
/// <para>
/// 表达"<see cref="GlobalId"/> 这个游戏 identity 等同于 <see cref="CanonicalGlobalId"/>"。
/// ⚠️ canonical 由 <c>GameIdentityRules.ElectCanonical</c> 在**每台设备上独立复算**；
/// 载荷里的 <see cref="CanonicalGlobalId"/> 只是"发送方算出的结果"，接收方仍会复算，
/// 因此乱序/重复/并发到达都不会产生分叉。
/// </para>
/// </summary>
public sealed class GameIdentitySyncPayload
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = DeviceSyncConstants.CurrentSchemaVersion;

    /// <summary>被并入（别名）的 game global_id —— 也是本变更的 <c>entity_global_id</c>（幂等键）。</summary>
    [JsonPropertyName("global_id")]
    public string GlobalId { get; set; } = "";

    /// <summary>发送方按确定性规则算出的 canonical game global_id。</summary>
    [JsonPropertyName("canonical_global_id")]
    public string CanonicalGlobalId { get; set; } = "";

    /// <summary>关联来源（审计用；接收方不会用它做裁决）。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>身份关联载荷的序列化 / 解析（与项目既有 payload 同风格：显式 snake_case 字段名）。</summary>
public static class GameIdentityPayloads
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Create(string globalId, string canonicalGlobalId, string? reason)
        => JsonSerializer.Serialize(new GameIdentitySyncPayload
        {
            GlobalId = globalId,
            CanonicalGlobalId = canonicalGlobalId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason
        }, Options);

    public static string Serialize(GameIdentitySyncPayload payload) => JsonSerializer.Serialize(payload, Options);

    public static bool TryParse(
        string? json, out GameIdentitySyncPayload? payload, out int schemaVersion, out string? error)
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
            var parsed = JsonSerializer.Deserialize<GameIdentitySyncPayload>(json, Options);
            if (parsed is null)
            {
                error = "payload 反序列化为空";
                return false;
            }

            schemaVersion = parsed.SchemaVersion;

            if (!DeviceSyncConstants.IsSupportedSchemaVersion(parsed.SchemaVersion))
            {
                error = $"不支持的 schema_version={parsed.SchemaVersion}（本机支持 "
                      + $"{DeviceSyncConstants.MinSupportedSchemaVersion}~{DeviceSyncConstants.CurrentSchemaVersion}）";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.GlobalId) || string.IsNullOrWhiteSpace(parsed.CanonicalGlobalId))
            {
                error = "身份关联必须带 global_id 与 canonical_global_id";
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
