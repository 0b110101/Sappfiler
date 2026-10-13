using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.WebDav;

/// <summary>
/// WebDAV 上的 <b>wire format v1</b>（2h 设计拍板 v2 §A/§F 冻结）。
///
/// 远端布局：
/// <code>
/// {同步目录}/protocol                      {"protocol_version":1}   —— 只建一次
/// {同步目录}/devices/{deviceId}/manifest.json                       —— 该设备清单（整文件 PUT 替换）
/// {同步目录}/devices/{deviceId}/batches/{batchId}.json              —— 不可变；文件名 = 内容哈希
/// </code>
///
/// ⚠️ <b>顺序的权威是 manifest 的 <c>batches[]</c> 数组顺序</b>，不是文件名、不是时间戳、不是序号连续性。
/// 文件名里刻意<b>不含</b>序号/时间戳/设备名 —— 唯一输入是"这批变更的 change_id 集合"，
/// 这样"同一批内容重推"必然命中同一个文件名 → PUT 覆盖 → 端到端幂等（v2 §A.6 修正 v1 的缺陷）。
///
/// ⚠️ 这些类型是**同步传输格式**，不是业务模型：`changes[].payload` 保持**原样字符串**
/// （不再嵌套解析一次，避免双重转义；解析与 schema 版本校验属 2e/2f 的职责）。
/// </summary>
public sealed class WebDavManifest
{
    public const int CurrentManifestVersion = 1;

    [JsonPropertyName("manifest_version")]
    public int ManifestVersion { get; set; } = CurrentManifestVersion;

    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; } = DeviceSyncConstants.CurrentProtocolVersion;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("updated_at_utc")]
    public string UpdatedAtUtc { get; set; } = "";

    /// <summary>
    /// **有序** batch 列表 = 消费顺序（追加式，永不删除）。
    /// 文件名不含序号，顺序只由这里的数组位置表达。
    /// </summary>
    [JsonPropertyName("batches")]
    public List<WebDavManifestBatch> Batches { get; set; } = new();
}

/// <summary>manifest 里的一条 batch 记录。</summary>
public sealed class WebDavManifestBatch
{
    /// <summary>文件名，形如 <c>9c1ab3d0e4f5a6b7.json</c>。</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("batch_id")]
    public string BatchId { get; set; } = "";

    /// <summary>仅用于人眼阅读与 manifest 排版 —— **不参与游标判断**。</summary>
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("change_count")]
    public int ChangeCount { get; set; }

    [JsonPropertyName("max_created_at_utc")]
    public string? MaxCreatedAtUtc { get; set; }
}

/// <summary>batch 文件（不可变）。</summary>
public sealed class WebDavBatchFile
{
    public const int CurrentFileVersion = 1;

    [JsonPropertyName("file_version")]
    public int FileVersion { get; set; } = CurrentFileVersion;

    /// <summary>Backend 协议版本；不兼容即拒绝消费（**不猜**）。</summary>
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; } = DeviceSyncConstants.CurrentProtocolVersion;

    /// <summary>= 文件名的 hash 部分，用于自校验"传输有没有被截断"（v1 §9 / v2 风险 3）。</summary>
    [JsonPropertyName("batch_id")]
    public string BatchId { get; set; } = "";

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("created_at_utc")]
    public string CreatedAtUtc { get; set; } = "";

    [JsonPropertyName("changes")]
    public List<WebDavWireChange> Changes { get; set; } = new();
}

/// <summary>batch 里的一条变更（字段与 2f 的 <c>SyncChange</c> 一一对应，不新增语义字段）。</summary>
public sealed class WebDavWireChange
{
    [JsonPropertyName("change_id")]
    public string ChangeId { get; set; } = "";

    [JsonPropertyName("entity_type")]
    public string EntityType { get; set; } = "";

    [JsonPropertyName("entity_global_id")]
    public string EntityGlobalId { get; set; } = "";

    [JsonPropertyName("operation")]
    public int Operation { get; set; } = (int)SyncOperation.Create;

    [JsonPropertyName("base_version")]
    public long BaseVersion { get; set; }

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("created_at_utc")]
    public string CreatedAtUtc { get; set; } = "";

    /// <summary>2f 的 payload，**原样字符串**。</summary>
    [JsonPropertyName("payload")]
    public string Payload { get; set; } = "";
}

/// <summary>协议版本文件。</summary>
public sealed class WebDavProtocolFile
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; } = DeviceSyncConstants.CurrentProtocolVersion;
}

/// <summary>
/// 引擎侧看到的 <b>不透明游标</b>（2f 契约：WebDAV 不自己维护语义游标）。
///
/// <code>
/// { "cursor_version": 1, "devices": { "&lt;deviceId&gt;": "&lt;最后已消费的 batch 文件名&gt;" } }
/// </code>
///
/// ⚠️ **绝不**用文件时间戳 / 序号连续性当游标：坏一个文件或出现空洞都不会永久卡死。
/// </summary>
public sealed class WebDavCursor
{
    public const int CurrentCursorVersion = 1;

    [JsonPropertyName("cursor_version")]
    public int CursorVersion { get; set; } = CurrentCursorVersion;

    [JsonPropertyName("devices")]
    public Dictionary<string, string> Devices { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 容错解析：空 / 非 JSON / 未知 cursor_version 一律视为**空游标**（从头消费）。
    /// 这是安全的：消费侧按 change_id / global_id 幂等吸收，最坏只是多读几个 batch。
    /// </summary>
    public static WebDavCursor Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new WebDavCursor();

        var parsed = WebDavWire.TryDeserialize<WebDavCursor>(raw);
        if (parsed is null || parsed.CursorVersion != CurrentCursorVersion)
        {
            AppLog.Warn("[WebDAV] 游标不可识别 → 视为空游标从头消费（消费侧幂等，安全）");
            return new WebDavCursor();
        }

        parsed.Devices ??= new Dictionary<string, string>(StringComparer.Ordinal);
        return parsed;
    }
}

/// <summary>wire format 的序列化与哈希规则（唯一真相，测试与假服务端复用同一份）。</summary>
public static class WebDavWire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>解析失败返回 null（**不抛**）：调用方决定"损坏"对该上下文意味着修复路径还是致命错误。</summary>
    public static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>UTC 时间串（既有约定：<c>yyyy-MM-ddTHH:mm:ssZ</c>）。</summary>
    public static string UtcNow(DateTimeOffset now) => now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>sha256 十六进制（小写）。</summary>
    public static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// batch 文件名 / batch_id 的**唯一计算规则**：
/// <code>batch_id = sha256(按字典序拼接的 change_id 列表)[..16]</code>
///
/// 用它就能证明：**同一批内容重推必然落到同一个文件名**（v2 §A.6 的核心修正）。
/// </summary>
public static class WebDavBatchId
{
    public const int Length = 16;

    public static string Compute(IEnumerable<string> changeIds)
    {
        // 用 '\n' 分隔再哈希：change_id 长度可能不同（GUID "N" 是 32，测试里可能更长），
        // 直接拼接在不同集合上存在歧义；分隔符让它成为单射。
        var joined = string.Join('\n', changeIds.OrderBy(id => id, StringComparer.Ordinal));
        return WebDavWire.Sha256Hex(joined)[..Length];
    }
}
