using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTimeTracker.Core.Models;

/// <summary>
/// Sappfiler 内部标准游玩时长记录，所有笔记同步 Provider 从此模型进行目标格式转换。
/// 保持 SQLite 为唯一真实数据源。
/// </summary>
public sealed class PlaytimeRecord
{
    public int DailySummaryId { get; init; }
    public string Date { get; init; } = "";
    public int GameId { get; init; }
    public string GameName { get; init; } = "";
    public int DurationSeconds { get; init; }
    public int DurationMinutes => (int)Math.Round(DurationSeconds / 60.0);
    public int SessionCount { get; init; } = 1;
    public string? CoverUrl { get; init; }
    public string? IconUrl { get; init; }
    public string? Platform { get; init; }
    public string? PlatformId { get; init; }

    /// <summary>格式化持续时间字符串（例如 "2h 35m" 或 "45m"）</summary>
    public string FormattedDuration
    {
        get
        {
            var hours = DurationSeconds / 3600;
            var minutes = (DurationSeconds % 3600) / 60;
            if (hours > 0)
            {
                return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
            }
            return $"{Math.Max(1, minutes)}m";
        }
    }
}

/// <summary>
/// 单次同步执行结果。
/// </summary>
public record SyncResult(
    bool Success,
    string? RemoteId = null,
    string? ErrorMessage = null)
{
    public static SyncResult Ok(string? remoteId = null) => new(true, remoteId, null);
    public static SyncResult Fail(string error) => new(false, null, error);
    public static SyncResult SuccessResult(string? remoteId = null) => new(true, remoteId, null);
    public static SyncResult FailedResult(string error) => new(false, null, error);
}

/// <summary>
/// 游戏聚合统计数据（供无法自动 Rollup 的笔记平台如 Obsidian 使用）。
/// 由 Sappfiler 本地 SQLite 计算并同步至远端。
/// </summary>
public record GameAggregateStats(
    double TotalHours,
    string? LastPlayedDate,
    int TotalSessions
)
{
    public string FormattedTotalDuration
    {
        get
        {
            var totalMinutes = (int)Math.Round(TotalHours * 60);
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            if (hours > 0)
            {
                return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
            }
            return $"{Math.Max(1, minutes)}m";
        }
    }
}

/// <summary>
/// 游戏在各笔记/平台后端的映射关系（替代原有 games 表中硬编码的 notion_page_id）。
/// </summary>
public sealed class GameMappingRecord
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public string Provider { get; set; } = "";
    public string RemoteId { get; set; } = "";
    public string? RemoteLocator { get; set; }
    public string? RemoteName { get; set; }
    public string? MatchType { get; set; } // manual, external_id, alias, exact_title, normalized_title, fuzzy
    public double? MatchConfidence { get; set; }
    public DateTime? LastVerified { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 远端候选游戏实体（统一抽象 Notion / Obsidian / 思源 等平台的已有游戏笔记或数据库行）
/// </summary>
public sealed class RemoteGameCandidate
{
    public string Provider { get; init; } = "";
    public string RemoteId { get; init; } = "";
    public string RemoteName { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public string? Location { get; init; }
    public string? Platform { get; init; }
    public string? ExternalId { get; init; } // 例如 "steam:2246340"
}

/// <summary>
/// 匹配类型分类
/// </summary>
public enum GameMatchType
{
    ExistingMapping,
    ExternalId,
    Alias,
    ExactTitle,
    NormalizedTitle,
    Fuzzy,
    Manual
}

/// <summary>
/// 匹配判定结果与证据链
/// </summary>
public sealed class MatchResult
{
    public RemoteGameCandidate Candidate { get; init; } = null!;
    public double Score { get; init; }
    public GameMatchType MatchType { get; init; }
    public string Evidence { get; init; } = "";
    public bool CanAutoBind { get; init; }
}

/// <summary>
/// 每日记录在各笔记后端的同步状态跟踪。
/// (daily_summary_id, provider) 唯一对应一行。
/// </summary>
public sealed class SyncRecordItem
{
    public int Id { get; set; }
    public int DailySummaryId { get; set; }
    public string Provider { get; set; } = "";
    public string? RemoteId { get; set; }
    public string Status { get; set; } = "pending"; // "pending" | "synced" | "failed"
    public int RetryCount { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 各同步后端的持久化开关与配置。
/// </summary>
public sealed class ProviderConfigItem
{
    public string Provider { get; set; } = "";
    public bool Enabled { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Notion 同步配置
/// </summary>
public sealed class NotionProviderConfig
{
    [JsonIgnore]
    public bool Enabled { get; set; }
    public string Token { get; set; } = "";
    public string GameDatabaseId { get; set; } = "";
    public string DailyDatabaseId { get; set; } = "";
}

/// <summary>
/// Obsidian 同步配置
/// </summary>
public sealed class ObsidianProviderConfig
{
    [JsonIgnore]
    public bool Enabled { get; set; }

    /// <summary>Obsidian Vault 本地根目录绝对路径</summary>
    public string VaultPath { get; set; } = "";

    /// <summary>同步模式："file"（直接读写文件）或 "api"（Local REST API 插件）</summary>
    public string Mode { get; set; } = "file";

    /// <summary>别名兼容</summary>
    [JsonIgnore]
    public string SyncMode { get => Mode; set => Mode = value; }

    /// <summary>API 模式端口，默认 27124</summary>
    public int ApiPort { get; set; } = 27124;

    /// <summary>API 模式 Key</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>每日记录存放相对目录，默认 "Sappfiler/Daily"</summary>
    public string DailyFolder { get; set; } = "Sappfiler/Daily";

    /// <summary>别名兼容</summary>
    [JsonIgnore]
    public string DailySubfolder { get => DailyFolder; set => DailyFolder = value; }

    /// <summary>游戏笔记存放相对目录，默认 "Sappfiler/Games"</summary>
    public string GamesFolder { get; set; } = "Sappfiler/Games";

    /// <summary>别名兼容</summary>
    [JsonIgnore]
    public string GamesSubfolder { get => GamesFolder; set => GamesFolder = value; }
}

/// <summary>
/// 思源笔记同步配置
/// </summary>
public sealed class SiYuanProviderConfig
{
    [JsonIgnore]
    public bool Enabled { get; set; }

    /// <summary>思源 HTTP API 地址，默认 http://127.0.0.1:6806</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:6806";

    /// <summary>API Token 认证凭据</summary>
    public string Token { get; set; } = "";

    /// <summary>目标笔记本 ID</summary>
    public string NotebookId { get; set; } = "";

    /// <summary>游戏总表数据库 Block ID</summary>
    public string MasterDatabaseId { get; set; } = "";

    /// <summary>每日打卡数据库 Block ID</summary>
    public string DailyDatabaseId { get; set; } = "";

    /// <summary>根文档路径（兼容保留）</summary>
    public string RootDocPath { get; set; } = "/Sappfiler";
}

/// <summary>
/// 同步目标配置字段描述（供 UI 动态渲染或验证）
/// </summary>
public sealed class ProviderConfigField
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsSecret { get; init; }
    public bool IsRequired { get; init; }
    public string FieldType { get; init; } = "text"; // "text", "password", "folder", "select"
    public string DefaultValue { get; init; } = "";
    public IReadOnlyList<string>? Options { get; init; }
}
