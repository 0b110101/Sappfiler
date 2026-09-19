namespace GameTimeTracker.Core.Models;

public record GameIdentity(
    string Platform,
    string PlatformId,
    string Name,
    string Executable,
    string ExecutablePath
);

public class GameRecord : System.ComponentModel.INotifyPropertyChanged
{
    private string? _notionPageId;

    public int Id { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string PlatformId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;

    public string? NotionPageId
    {
        get => _notionPageId;
        set
        {
            if (_notionPageId != value)
            {
                _notionPageId = value;
                OnPropertyChanged(nameof(NotionPageId));
                OnPropertyChanged(nameof(IsNotionBound));
                OnPropertyChanged(nameof(NotionStatusText));
            }
        }
    }

    public string Status { get; set; } = "active"; // "active", "pending", "ignored"
    public string? CoverUrl { get; set; }

    /// <summary>
    /// 本地封面缓存文件的绝对路径。非数据库字段，由界面层填充（列表绑定用）。
    /// </summary>
    public string? CoverPath { get; set; }

    public bool HasCover => !string.IsNullOrEmpty(CoverPath) && System.IO.File.Exists(CoverPath);
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsNotionBound => !string.IsNullOrWhiteSpace(NotionPageId);
    public string NotionStatusText => IsNotionBound ? "● 已绑定" : "● 未绑定";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

public class GameSession
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public int DurationSeconds { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DailySummary
{
    public int Id { get; set; }
    public string Date { get; set; } = string.Empty; // Format: "yyyy-MM-dd"
    public int GameId { get; set; }
    public int DurationSeconds { get; set; }
    public int DurationMinutes { get; set; }
    public int SessionCount { get; set; } = 1;
    public string SyncStatus { get; set; } = "pending"; // "synced", "pending", "error"
    public string? NotionPageId { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 这条记录在 Notion 页面上的标题原文（形如「不思议迷宫 · 0.7 h」）。
    /// 只在"回刷标题"时用来比对，判断是否真的需要再 PATCH 一次 Notion。
    /// </summary>
    public string? NotionTitle { get; set; }

    /// <summary>
    /// 这条记录在 Notion 页面上的 page icon URL 快照。
    /// 与 <see cref="NotionTitle"/> 同理：只快照标题的话，"总表设了图标"会导致
    /// 每轮回刷都判定需要更新，从而反复 PATCH。
    /// </summary>
    public string? NotionIconUrl { get; set; }

    // Navigation / joined fields
    public string GameName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string PlatformId { get; set; } = string.Empty;
}

public class NotionGameCatalogItem
{
    public string PageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public List<string> Identifiers { get; set; } = new();
    public string? CoverUrl { get; set; }

    /// <summary>总表页面的 page icon（正方形小图），做列表封面的首选；cover 是横幅，留给背景图。</summary>
    public string? IconUrl { get; set; }

    /// <summary>page icon 的类型：external / file / emoji / null。null = 页面未设图标，可由程序写入。</summary>
    public string? IconType { get; set; }

    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;
}

public class GameCandidate
{
    public string PageId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string MatchType { get; set; } = string.Empty; // "exact" | "normalized" | "identifier_match"
    public double Score { get; set; } = 100.0;
}

public record DetectedProcess(
    int Pid,
    string ProcessName,
    string ExecutablePath,
    string? WindowTitle
);

public class NotionDailyRecordItem
{
    public string PageId { get; set; } = string.Empty;

    /// <summary>已剥掉末尾时长后缀的裸游戏名（「不思议迷宫 · 0.7 h」→「不思议迷宫」）。</summary>
    public string GameTitle { get; set; } = string.Empty;

    /// <summary>
    /// Notion 页面上标题的原文（「不思议迷宫 · 0.7 h」）。
    /// 与 <see cref="GameTitle"/> 的区别是它保留了时长后缀 —— 回刷标题时要用原文跟期望标题比对，
    /// 用裸名比会把每条记录都误判成"需要更新"。
    /// </summary>
    public string? RawTitle { get; set; }

    public string Date { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }

    /// <summary>
    /// 「单次时长」原始值的单位是不是**分钟**（从标题后缀判定，如「… · 42 min」）。
    /// 仅用于识别并修复"旧分钟值被当成小时"造成的历史膨胀 —— 那种行会被放大 60 倍，
    /// 必须在拉取时以远端为准压回去，否则本地会把放大后的值推回 Notion。
    /// 单位是小时（标题写着 h）或认不出单位时为 false。
    /// </summary>
    public bool DurationUnitIsMinutes { get; set; }

    public string? GameMasterPageId { get; set; }
    public string? Status { get; set; }
}

/// <summary>删除某个游戏前的影响面统计，用于删除确认对话框。</summary>
public sealed class GameDeletionImpact
{
    public int SessionCount { get; set; }
    public int DailyRecordCount { get; set; }

    /// <summary>其中已同步到 Notion 的条数（这些在 Notion 上有对应页面需要归档）。</summary>
    public int SyncedDailyRecordCount { get; set; }

    /// <summary>该游戏在 Notion 总表里的 page id，空表示未绑定。</summary>
    public string GameNotionPageId { get; set; } = string.Empty;

    public bool IsBoundToNotion => !string.IsNullOrWhiteSpace(GameNotionPageId);
}

/// <summary>一次删除操作的结果，用于回显。</summary>
public sealed class NotionDeleteResult
{
    /// <summary>本地删除的每日汇总条数。</summary>
    public int LocalDailyRecordsDeleted { get; set; }

    /// <summary>在 Notion 上成功归档的每日记录条数。</summary>
    public int NotionDailyRecordsArchived { get; set; }

    /// <summary>在 Notion 上归档失败的每日记录条数。</summary>
    public int NotionDailyRecordsFailed { get; set; }

    /// <summary>总表条目是否已归档。</summary>
    public bool MasterEntryArchived { get; set; }

    /// <summary>未配置 Notion（或用户选择只在本地删），Notion 侧未做任何动作。</summary>
    public bool NotionSkipped { get; set; }

    public List<string> Errors { get; } = new();
}

/// <summary>Notion 侧删除对账的结果。</summary>
public sealed class NotionReconcileResult
{
    /// <summary>已在 Notion 删除、本地跟着删掉的每日记录条数。</summary>
    public int DeletedDailyRecords { get; set; }

    /// <summary>总表条目已消失、本地跟着删掉的游戏数。</summary>
    public int DeletedGames { get; set; }

    /// <summary>远端结果不可信（未配置 / 拉取失败 / 返回空集）时跳过对账，避免误删本地数据。</summary>
    public bool Skipped { get; set; }

    public bool HasChanges => DeletedDailyRecords > 0 || DeletedGames > 0;
}

public class TrackerConfig
{
    public string NotionToken { get; set; } = string.Empty;
    public string GameDatabaseId { get; set; } = string.Empty;
    public string DailyDatabaseId { get; set; } = string.Empty;
    public int ProcessScanIntervalSeconds { get; set; } = 5;
    public int SessionHeartbeatIntervalSeconds { get; set; } = 15;
    public int SyncIntervalMinutes { get; set; } = 15;
    public bool AutoCreateGames { get; set; } = false;

    // ⚠️ 已废弃：模糊匹配于 2026-09-19 移除（只保留确定性匹配），
    //    这两个值**不再被任何代码读取**。按「不改配置键」的规则保留字段，
    //    若确认无人依赖，可在单独一轮清理中删除。
    //    详见 GameMatcher.MatchGame 里的说明。
    public double FuzzyCandidateThreshold { get; set; } = 80.0;
    public double FuzzyScoreGapThreshold { get; set; } = 15.0;

    /// <summary>
    /// 默认数据库路径：默认在 %LocalAppData%\GameTimeTracker（不易随解压目录被误删），
    /// 用户可在设置页更改存档位置（见 AppPaths）。环境变量 GAMETIME_DB_PATH 优先（多实例/测试用）。
    /// </summary>
    public static string DefaultDbPath
    {
        get
        {
            var dir = Services.AppPaths.DataDir;
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch { }

            return Services.AppPaths.DbPath;
        }
    }

    public string DbPath { get; set; } = DefaultDbPath;
    public string Theme { get; set; } = "System"; // "System", "Dark", "Light"

    public bool IsNotionConfigured =>
        !string.IsNullOrWhiteSpace(NotionToken) &&
        !NotionToken.StartsWith("secret_your_") &&
        !string.IsNullOrWhiteSpace(GameDatabaseId) &&
        !GameDatabaseId.StartsWith("your_") &&
        !string.IsNullOrWhiteSpace(DailyDatabaseId) &&
        !DailyDatabaseId.StartsWith("your_");
}
