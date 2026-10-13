namespace GameTimeTracker.Core.Backup;

/// <summary>
/// 导入的两种语义（2026-10-11，产品语义固定，不可混用）：
/// <list type="bullet">
///   <item><see cref="Restore"/> = <b>恢复备份</b>：把本机数据库**整体**换回备份时的状态
///         （含 device_id / 同步游标 / 待处理队列），用于本机数据丢失、损坏或误操作。
///         采用"暂存 + 启动早期替换 + 校验 + 失败回滚"，**不在线替换正在使用的数据库**。</item>
///   <item><see cref="Migrate"/> = <b>迁移到新设备</b>：把旧电脑的**事实**合并进本机现有数据，
///         保留本机 device_id 与全部凭据配置，**不覆盖、不替换**，导入后立即可用（无需重启）。</item>
/// </list>
/// </summary>
public enum BackupImportMode
{
    /// <summary>恢复备份：整库替换，重启生效。</summary>
    Restore = 0,

    /// <summary>迁移到新设备：在线合并事实，保留本机身份与凭据。</summary>
    Migrate = 1
}

/// <summary>备份文件校验与预览结果（**只读**：校验完成前调用方不得改动任何现有数据）。</summary>
public sealed class BackupPreview
{
    /// <summary>被检查的备份文件绝对路径。</summary>
    public string FilePath { get; init; } = "";

    /// <summary>备份文件本身的字节数。</summary>
    public long FileSizeBytes { get; init; }

    // ---------------- 清单原文（供预览展示） ----------------

    public int FormatVersion { get; init; }
    public string AppVersion { get; init; } = "";
    public string ExportedAtUtc { get; init; } = "";
    public string ExportedFromMachine { get; init; } = "";
    public long DatabaseSizeBytes { get; init; }

    /// <summary>清单声明的 <c>database.db</c> SHA-256（小写 hex）。恢复暂存时据此复核解包结果。</summary>
    public string DatabaseSha256 { get; init; } = "";

    public int SchemaMigrationsMax { get; init; }
    public BackupCounts Counts { get; init; } = new();
    public BackupDateRange? DateRange { get; init; }

    // ---------------- 校验结论 ----------------

    /// <summary>容器结构完整（清单可解析 + 数据库条目存在 + SHA-256 一致）。</summary>
    public bool ArchiveOk { get; init; }

    /// <summary>快照数据库可打开且 <c>PRAGMA integrity_check</c> 通过。</summary>
    public bool IntegrityOk { get; init; }

    /// <summary>备份的 schema 版本不高于当前应用支持的版本（否则拒绝，提示升级应用）。</summary>
    public bool SchemaCompatible { get; init; }

    /// <summary>当前应用支持的最高 schema 版本（用于提示"备份来自更新版本"）。</summary>
    public int CurrentSchemaMax { get; init; }

    /// <summary>是否可安全用于导入（三项校验全通过）。</summary>
    public bool IsUsable => ArchiveOk && IntegrityOk && SchemaCompatible;

    /// <summary>不合格时的可读原因（供 UI 直接展示，不允许用"操作失败"代替）。</summary>
    public string RejectionReason { get; init; } = "";

    /// <summary>预览时读到的实际库内概况（以库内读数为准，与清单不符时用这个显示）。</summary>
    public BackupCounts ActualCounts { get; init; } = new();

    /// <summary>备份来源设备与当前设备是否相同（仅影响提示文案，不影响校验结论）。</summary>
    public bool SameMachineAsCurrent { get; init; }
}

/// <summary>导出结果。</summary>
public sealed class BackupExportResult
{
    public bool Success { get; init; }
    public string FilePath { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public string ExportedAtUtc { get; init; } = "";
    public BackupCounts Counts { get; init; } = new();

    /// <summary>备份覆盖的日期范围（无每日记录时为 <c>null</c>）。</summary>
    public BackupDateRange? DateRange { get; init; }

    public string Error { get; init; } = "";
}

/// <summary>迁移合并时发现的、**无法安全自动合并**的冲突（必须阻止导入并说明原因，不允许静默丢数据）。</summary>
public sealed class MigrationConflict
{
    /// <summary>冲突类别，例如 <c>game_platform_id_taken</c>。</summary>
    public string Kind { get; init; } = "";

    /// <summary>人类可读说明（直接给用户看）。</summary>
    public string Description { get; init; } = "";
}

/// <summary>导入结果（恢复与迁移共用外壳，语义由 <see cref="Mode"/> 区分）。</summary>
public sealed class BackupImportResult
{
    public bool Success { get; init; }
    public BackupImportMode Mode { get; init; }

    /// <summary><b>恢复</b>语义：已写入暂存并等待重启生效（本次不替换正在使用的数据库）。</summary>
    public bool StagedForRestart { get; init; }

    // ---------------- 迁移语义的逐类计数 ----------------

    public int GamesImported { get; init; }
    public int GamesReused { get; init; }
    public int SessionsImported { get; init; }
    public int SessionsSkipped { get; init; }
    public int AdjustmentsImported { get; init; }
    public int AdjustmentsSkipped { get; init; }
    public int ExclusionsImported { get; init; }
    public int ExclusionsSkipped { get; init; }
    public int TombstonesImported { get; init; }
    public int TombstonesSkipped { get; init; }
    public int SupersessionsImported { get; init; }
    public int SupersessionsSkipped { get; init; }

    /// <summary>被重算的 (game, 会计日) 派生行个数。</summary>
    public int DailyRowsRecomputed { get; init; }

    /// <summary>冲突清单（非空 ⇒ <see cref="Success"/> 为 false，且**什么都没写入**）。</summary>
    public IReadOnlyList<MigrationConflict> Conflicts { get; init; } = Array.Empty<MigrationConflict>();

    /// <summary>结果说明（成功与失败都必须有具体内容）。</summary>
    public string Message { get; init; } = "";

    /// <summary>导入后是否需要重新配置凭据（备份从不包含凭据，因此恒为"需要时提示"）。</summary>
    public bool RequiresNotionReauth { get; init; }

    public string Error { get; init; } = "";
}

/// <summary>启动早期恢复状态机的终态。</summary>
public enum RestoreOutcomeKind
{
    /// <summary>没有待应用的恢复（正常启动）。</summary>
    None = 0,

    /// <summary>恢复成功应用（数据库已替换并通过校验）。</summary>
    Applied = 1,

    /// <summary>恢复失败，但当前数据库已完好回滚（数据未受影响）。</summary>
    FailedRolledBack = 2,

    /// <summary>恢复失败，且回滚未能确认成功（**需要人工介入**，绝不谎报成功）。</summary>
    FailedRollbackUnconfirmed = 3,

    /// <summary>上一次启动的恢复处于中间态、本次继续完成（状态机可重入的正常表现）。</summary>
    Resumed = 4
}

/// <summary>启动早期恢复的执行结果（由 App 层读取并展示给用户）。</summary>
public sealed class RestoreOutcome
{
    public RestoreOutcomeKind Kind { get; init; } = RestoreOutcomeKind.None;

    /// <summary>人类可读说明（成功与失败都必须具体，不允许模糊文案）。</summary>
    public string Message { get; init; } = "";

    /// <summary>替换前对当前数据库所做安全快照的路径（失败回滚的依据；成功时保留供人工回退）。</summary>
    public string SafetySnapshotPath { get; init; } = "";

    public bool IsSuccess => Kind is RestoreOutcomeKind.Applied;

    public bool NeedsUserAttention => Kind is RestoreOutcomeKind.FailedRolledBack
        or RestoreOutcomeKind.FailedRollbackUnconfirmed;
}
