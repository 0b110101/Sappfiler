using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTimeTracker.Core.Notion;

/// <summary>
/// settings 表里「CoverHash → file_upload_id」映射的键名。
/// </summary>
/// <remarks>
/// 刻意复用现有 <c>settings</c> 表（<c>key/value</c>），**不做任何数据库 migration**。
/// 键名风格与 <c>WebDavSyncSettings.Keys</c> 的 <c>device_sync_*</c> 一致：前缀 + 小写。
/// </remarks>
public static class NotionIconUploadKeys
{
    /// <summary>所有映射键的前缀。</summary>
    public const string Prefix = "notion_icon_upload.";

    /// <summary>某个封面内容哈希对应的 settings 键。</summary>
    public static string Key(string coverHash) => Prefix + coverHash;
}

/// <summary>
/// 一个「封面内容哈希 → Notion File Upload id」的持久化映射。
/// </summary>
/// <remarks>
/// ⚠️ 只有 <see cref="FileUploadId"/> 是**持久标识**。
/// Notion 读回页面图标时给的是临时的 <c>icon.file.url</c>（1 小时有效），
/// **绝不能**把它当作标识写回这里 —— 见本类字段注释与 <c>DailyIconUploadService</c> 的说明。
/// </remarks>
public sealed class NotionIconMapping
{
    /// <summary>结构版本号，便于未来兼容旧值。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Notion File Upload id（持久、可复用到多个页面；附着后原始上传不再过期）。</summary>
    [JsonPropertyName("file_upload_id")]
    public string FileUploadId { get; set; } = string.Empty;

    /// <summary>上传时声明的 content type（按**文件内容**判定，不按扩展名）。</summary>
    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>上传时的文件字节数。</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>封面**内容**的 SHA-256（十六进制小写），即映射键里的 CoverHash。</summary>
    [JsonPropertyName("bytes_hash")]
    public string BytesHash { get; set; } = string.Empty;

    /// <summary>首次上传时间（UTC）。</summary>
    [JsonPropertyName("uploaded_at_utc")]
    public DateTimeOffset UploadedAtUtc { get; set; }

    /// <summary>最后一次被复用的时间（UTC）。</summary>
    [JsonPropertyName("last_used_at_utc")]
    public DateTimeOffset LastUsedAtUtc { get; set; }

    /// <summary>被复用（附着到页面）的累计次数，便于将来做清理策略时参考。</summary>
    [JsonPropertyName("attached_count")]
    public int AttachedCount { get; set; }

    /// <summary>首次上传的来源设备标识（取不到时为 null）。</summary>
    [JsonPropertyName("source_device")]
    public string? SourceDevice { get; set; }

    /// <summary>被复用一次：刷新 last_used_at 并累加 attached_count。</summary>
    public void Touch()
    {
        LastUsedAtUtc = DateTimeOffset.UtcNow;
        AttachedCount++;
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>解析 settings 里的 JSON；任何异常/空值都返回 null（损坏值按"没有映射"处理）。</summary>
    public static NotionIconMapping? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<NotionIconMapping>(json);
            return string.IsNullOrWhiteSpace(m?.FileUploadId) ? null : m;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 「映射存取」这一小块能力的窄接口：由 <c>SqliteRepository</c> 基于现有 <c>settings</c> 表实现。
/// </summary>
/// <remarks>
/// 抽这么窄是为了让上传/复用逻辑可以脱离整个 <c>IDatabaseRepository</c> 被单测，
/// 同时**不改变**任何既有仓储结构（无 migration、无新表）。
/// </remarks>
public interface INotionIconMappingStore
{
    Task<string?> GetSettingAsync(string key);
    Task SetSettingAsync(string key, string value);
    Task DeleteSettingAsync(string key);
}
