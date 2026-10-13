using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTimeTracker.Core.Backup;

/// <summary>
/// <b>备份容器格式的唯一定义</b>（2026-10-11）。
///
/// <para>
/// 容器 = ZIP（扩展名 <c>.sappfiler</c>），内含：
/// <list type="number">
///   <item><c>manifest.json</c> —— 格式版本 / 应用版本 / 导出时间 / 数据概况 / 完整性校验值；</item>
///   <item><c>database.db</c> —— 用 SQLite Online Backup API 生成的**一致性快照**（已剥离凭据）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ <b>独立于数据库 schema 版本</b>：<see cref="CurrentFormatVersion"/> 只描述**容器布局**，
/// 备份内部数据库的兼容性由 <c>schema_migrations</c> 最高版本单独判断。两者都要校验，
/// 缺一不可（否则"新版容器 + 旧版库"或"旧版容器 + 新版库"都会被误判为可用）。
/// </para>
///
/// <para>
/// ⚠️ <b>凭据一律不入包</b>：导出的快照会剥离 <c>notion_token</c>；
/// WebDAV 密码本就是 DPAPI（<c>CurrentUser</c>）密文、跨机不可解，因此不进包也不迁移。
/// </para>
/// </summary>
public static class BackupFormat
{
    /// <summary>容器布局版本。改动 ZIP 内部结构时 +1，并在此处保留旧版本的读取分支。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>文件扩展名（含点）。</summary>
    public const string FileExtension = ".sappfiler";

    /// <summary>ZIP 内清单条目的名字。</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>ZIP 内数据库快照条目的名字。</summary>
    public const string DatabaseEntryName = "database.db";

    /// <summary>文件选择器用的类型说明。</summary>
    public const string FileTypeDescription = "Sappfiler 数据备份";

    /// <summary>
    /// <c>settings</c> 表里记录「上次**成功**导出备份」时刻的 key（值 = UTC，ISO8601 <c>O</c> 格式）。
    ///
    /// <para>
    /// 用途：用户既没绑定 Notion、也没启用 WebDAV 时，主界面左下角的状态胶囊显示它 ——
    /// 有依据的时间，而不是编一个出来。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 写入方（<c>SappfilerBackupService</c>，Infrastructure）与读取方（主窗口，App）
    /// **必须**共用这一个常量，否则会出现"写了没人读 / 读的没人写"的静默失效。
    /// </para>
    /// </summary>
    public const string SettingKeyLastExportUtc = "last_backup_export_utc";
}

/// <summary>
/// 备份清单（<c>manifest.json</c>）。
///
/// <para>
/// 它是"先校验、后写入"的关键：预览阶段只读它 + 重算 <see cref="DatabaseSha256"/>，
/// 校验通过前**绝不触碰当前数据库**。
/// </para>
/// </summary>
public sealed class BackupManifest
{
    /// <summary>容器布局版本（必须 == <see cref="BackupFormat.CurrentFormatVersion"/>）。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>导出方的应用版本（展示用，便于排查"备份来自哪个版本"）。</summary>
    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = "";

    /// <summary>导出时间（UTC，ISO-8601）。</summary>
    [JsonPropertyName("exported_at_utc")]
    public string ExportedAtUtc { get; set; } = "";

    /// <summary>导出时机器名（**不含**用户名与凭据；仅供用户辨认来源设备）。</summary>
    [JsonPropertyName("exported_from_machine")]
    public string ExportedFromMachine { get; set; } = "";

    /// <summary><c>database.db</c> 条目的 SHA-256（小写 hex）。导入前必须重算比对。</summary>
    [JsonPropertyName("database_sha256")]
    public string DatabaseSha256 { get; set; } = "";

    /// <summary><c>database.db</c> 的字节数（便于预览与快速完整性感知）。</summary>
    [JsonPropertyName("database_size")]
    public long DatabaseSize { get; set; }

    /// <summary>快照内 <c>schema_migrations</c> 的最高版本（判断"备份是否来自更新的应用"）。</summary>
    [JsonPropertyName("schema_migrations_max")]
    public int SchemaMigrationsMax { get; set; }

    /// <summary>数据概况（预览用；不可作为正确性依据，以库内实际读数为准）。</summary>
    [JsonPropertyName("counts")]
    public BackupCounts Counts { get; set; } = new();

    /// <summary>每日记录覆盖的日期范围（无每日记录时为 <c>null</c>）。</summary>
    [JsonPropertyName("date_range")]
    public BackupDateRange? DateRange { get; set; }

    /// <summary>恒为 <c>false</c>：容器**不含任何认证秘密**（导出时已剥离并复核）。</summary>
    [JsonPropertyName("contains_credentials")]
    public bool ContainsCredentials { get; set; }

    /// <summary>人类可读补充说明（例如"已剥离 notion_token"）。</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";
}

/// <summary>备份内的数据概况（全部为**行数**，不含任何内容）。</summary>
public sealed class BackupCounts
{
    [JsonPropertyName("games")] public int Games { get; set; }
    [JsonPropertyName("sessions")] public int Sessions { get; set; }
    [JsonPropertyName("daily_summary")] public int DailySummary { get; set; }
    [JsonPropertyName("daily_duration_adjustments")] public int DailyAdjustments { get; set; }
    [JsonPropertyName("daily_play_exclusions")] public int DailyPlayExclusions { get; set; }
    [JsonPropertyName("sync_tombstones")] public int SyncTombstones { get; set; }
    [JsonPropertyName("sync_identity_supersessions")] public int SyncSupersessions { get; set; }
    [JsonPropertyName("game_catalog")] public int GameCatalog { get; set; }

    /// <summary>总和（[导出成功] 提示里用的"数据概况"摘要数字）。</summary>
    [JsonIgnore]
    public int TotalFacts => Games + Sessions + DailyAdjustments + DailyPlayExclusions;
}

/// <summary>每日记录的日期覆盖范围（<c>yyyy-MM-dd</c>）。</summary>
public sealed class BackupDateRange
{
    [JsonPropertyName("min")] public string Min { get; set; } = "";
    [JsonPropertyName("max")] public string Max { get; set; } = "";
}

/// <summary>
/// 清单的序列化 / 解析（唯一实现，避免各处自己 new <see cref="JsonSerializerOptions"/>）。
/// </summary>
public static class BackupManifestCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Serialize(BackupManifest manifest)
        => JsonSerializer.Serialize(manifest, Options);

    /// <summary>
    /// 解析清单。**只做形状校验**（能被反序列化 + 关键字段齐备）；
    /// 版本与校验值的判定由调用方负责，这样错误信息才能精确到"哪一项不合格"。
    /// </summary>
    public static bool TryParse(string? json, out BackupManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "备份清单为空";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<BackupManifest>(json, Options);
            if (parsed is null)
            {
                error = "备份清单无法解析";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.DatabaseSha256))
            {
                error = "备份清单缺少 database_sha256（无法校验完整性）";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.ExportedAtUtc))
            {
                error = "备份清单缺少 exported_at_utc（无法确认备份来源）";
                return false;
            }

            manifest = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"备份清单不是合法 JSON：{ex.Message}";
            return false;
        }
    }
}
