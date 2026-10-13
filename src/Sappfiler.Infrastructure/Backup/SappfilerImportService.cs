using GameTimeTracker.Core.Backup;
using GameTimeTracker.Infrastructure.Database;

namespace GameTimeTracker.Infrastructure.Backup;

/// <summary>
/// <b>导入备份</b>（2026-10-11）：静态预览校验 + 两种互不混用的导入语义。
///
/// <para>
/// · <see cref="ImportRestoreAsync"/> = 恢复备份：只把校验过的备份**暂存**，重启后由
///   <see cref="RestoreBootstrapper"/> 在启动早期替换（含前置安全快照与失败回滚）。
/// · <see cref="ImportMigrateAsync"/> = 迁移到新设备：把备份里的**事实**在线合并进本机
///   （单事务、保留本机 device_id 与凭据）。
/// </para>
///
/// <para>
/// ⚠️ <see cref="InspectAsync"/> **只读**：校验完成前不得修改当前数据库。
/// </para>
/// </summary>
public sealed class SappfilerImportService
{
    private readonly SqliteRepository _repo;
    private readonly string _dataDir;

    public SappfilerImportService(SqliteRepository repo, string dataDir)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _dataDir = string.IsNullOrWhiteSpace(dataDir)
            ? throw new ArgumentException("数据目录不能为空", nameof(dataDir))
            : dataDir;
    }

    /// <summary>
    /// 只读校验 + 预览。任何一步失败都会返回 <c>IsUsable = false</c> 的预览（含可读的拒绝原因），
    /// 而不是抛异常 —— UI 需要把"为什么不能用"讲清楚。
    /// </summary>
    public async Task<BackupPreview> InspectAsync(string backupFilePath, CancellationToken ct = default)
    {
        return await Task.Run(() => InspectCore(backupFilePath, ct), ct).ConfigureAwait(false);
    }

    private BackupPreview InspectCore(string backupFilePath, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(backupFilePath) ? "" : Path.GetFullPath(backupFilePath);
        var currentSchemaMax = _repo.ReadCurrentSchemaMax();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return Reject(path, currentSchemaMax, "找不到备份文件。", archiveOk: false, integrityOk: false);
        }

        var fileSize = BackupFileUtil.GetFileSizeOrZero(path);
        if (fileSize == 0)
        {
            return Reject(path, currentSchemaMax, "备份文件为空（可能复制未完成或被截断）。",
                archiveOk: false, integrityOk: false, fileSizeBytes: 0);
        }

        ct.ThrowIfCancellationRequested();

        // ① 容器结构（条目名 / 重名 / 条目数）——必须在读清单之前，
        //    否则"重名条目"会让后续每一步看到的内容都不可信。
        if (!BackupArchive.TryValidateStructure(path, out var structureError))
        {
            return Reject(path, currentSchemaMax, structureError ?? "备份容器结构异常。",
                archiveOk: false, integrityOk: false, fileSizeBytes: fileSize);
        }

        // ② 清单
        if (!BackupArchive.TryReadManifest(path, out var manifest, out var manifestError) || manifest is null)
        {
            return Reject(path, currentSchemaMax, manifestError ?? "备份清单无法读取。",
                archiveOk: false, integrityOk: false, fileSizeBytes: fileSize);
        }

        if (manifest.FormatVersion != BackupFormat.CurrentFormatVersion)
        {
            return Reject(path, currentSchemaMax,
                $"不支持的备份格式版本（备份为 v{manifest.FormatVersion}，本应用支持 v{BackupFormat.CurrentFormatVersion}）。"
                + "请升级 Sappfiler 后再导入。",
                archiveOk: false, integrityOk: false, fileSizeBytes: fileSize,
                manifest: manifest);
        }

        if (!BackupArchive.HasDatabaseEntry(path))
        {
            return Reject(path, currentSchemaMax, "备份文件中缺少数据库内容，无法导入。",
                archiveOk: false, integrityOk: false, fileSizeBytes: fileSize, manifest: manifest);
        }

        // ③ 校验值必须与清单声明一致（可发现截断、伪造清单、字节被改写）
        var actualSha = BackupArchive.ComputeDatabaseSha256(path);
        if (actualSha is null)
        {
            return Reject(path, currentSchemaMax, "备份文件损坏或截断，无法读取数据库内容。",
                archiveOk: false, integrityOk: false, fileSizeBytes: fileSize, manifest: manifest);
        }

        if (!string.Equals(actualSha, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(path, currentSchemaMax,
                "备份内容与清单声明的校验值不一致（文件已损坏或被改动）。",
                archiveOk: false, integrityOk: false, fileSizeBytes: fileSize, manifest: manifest);
        }

        // 清单声明的字节数也必须与实际一致：篡改清单时"改了哈希却忘了改大小"是最典型的迹象。
        var actualDbSize = BackupArchive.GetDatabaseEntrySize(path);
        if (manifest.DatabaseSize > 0 && actualDbSize != manifest.DatabaseSize)
        {
            return Reject(path, currentSchemaMax,
                "备份清单声明的数据库大小与实际内容不一致（清单已被改动）。",
                archiveOk: false, integrityOk: false, fileSizeBytes: fileSize, manifest: manifest);
        }

        ct.ThrowIfCancellationRequested();

        // ④ 解到临时文件做完整性检查与统计（不碰当前数据库）
        var tempDb = Path.Combine(Path.GetTempPath(), $"sappfiler-inspect-{Guid.NewGuid():N}.db");
        try
        {
            if (!BackupArchive.TryExtractDatabase(path, tempDb, out var extractError))
            {
                return Reject(path, currentSchemaMax, extractError ?? "无法从备份中解出数据库。",
                    archiveOk: false, integrityOk: false, fileSizeBytes: fileSize, manifest: manifest);
            }

            var stats = SqliteRepository.ReadBackupStats(tempDb);
            var schemaCompatible = stats.SchemaMax <= currentSchemaMax;

            var rejection = "";
            if (!stats.IntegrityOk)
            {
                rejection = "备份数据库未通过完整性检查（文件可能已损坏）。";
            }
            else if (!schemaCompatible)
            {
                rejection = $"备份来自更新的 Sappfiler 版本（数据结构版本 {stats.SchemaMax} > 本机 {currentSchemaMax}）。"
                    + "请先升级本应用，避免读到未知结构。";
            }

            var sameMachine = !string.IsNullOrWhiteSpace(manifest.ExportedFromMachine)
                && string.Equals(manifest.ExportedFromMachine, SafeMachineName(), StringComparison.OrdinalIgnoreCase);

            AppLog.Info($"[备份导入] 预览 {path}：games={stats.Counts.Games}，sessions={stats.Counts.Sessions}，"
                + $"daily={stats.Counts.DailySummary}，可用={stats.IntegrityOk && schemaCompatible}");

            return new BackupPreview
            {
                FilePath = path,
                FileSizeBytes = fileSize,
                FormatVersion = manifest.FormatVersion,
                AppVersion = manifest.AppVersion,
                ExportedAtUtc = manifest.ExportedAtUtc,
                ExportedFromMachine = manifest.ExportedFromMachine,
                DatabaseSizeBytes = manifest.DatabaseSize,
                DatabaseSha256 = manifest.DatabaseSha256,
                SchemaMigrationsMax = manifest.SchemaMigrationsMax,
                Counts = manifest.Counts,
                DateRange = manifest.DateRange,
                ActualCounts = stats.Counts,
                ArchiveOk = true,
                IntegrityOk = stats.IntegrityOk,
                SchemaCompatible = schemaCompatible,
                CurrentSchemaMax = currentSchemaMax,
                RejectionReason = rejection,
                SameMachineAsCurrent = sameMachine
            };
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    /// <summary><b>恢复备份</b>：暂存并等待重启（不替换正在使用的数据库）。</summary>
    public Task<BackupImportResult> ImportRestoreAsync(BackupPreview preview, CancellationToken ct = default)
    {
        if (preview is null || !preview.IsUsable)
        {
            return Task.FromResult(new BackupImportResult
            {
                Success = false,
                Mode = BackupImportMode.Restore,
                Message = "备份未通过校验，无法恢复。",
                Error = preview?.RejectionReason ?? "备份不可用"
            });
        }

        return Task.FromResult(RestoreBootstrapper.StageRestore(_dataDir, preview.FilePath, preview));
    }

    /// <summary><b>迁移到新设备</b>：在线合并事实（保留本机 device_id 与凭据配置）。</summary>
    public async Task<BackupImportResult> ImportMigrateAsync(BackupPreview preview, CancellationToken ct = default)
    {
        if (preview is null || !preview.IsUsable)
        {
            return new BackupImportResult
            {
                Success = false,
                Mode = BackupImportMode.Migrate,
                Message = "备份未通过校验，无法迁移。",
                Error = preview?.RejectionReason ?? "备份不可用"
            };
        }

        var tempDb = Path.Combine(Path.GetTempPath(), $"sappfiler-migrate-{Guid.NewGuid():N}.db");
        try
        {
            if (!BackupArchive.TryExtractDatabase(preview.FilePath, tempDb, out var extractError))
            {
                return new BackupImportResult
                {
                    Success = false,
                    Mode = BackupImportMode.Migrate,
                    Message = "无法从备份中解出数据库，迁移未开始。",
                    Error = extractError ?? "解包失败"
                };
            }

            var actualSha = BackupFileUtil.ComputeFileSha256(tempDb);
            if (!string.Equals(actualSha, preview.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new BackupImportResult
                {
                    Success = false,
                    Mode = BackupImportMode.Migrate,
                    Message = "解包后的数据库与备份校验值不一致，迁移未开始。",
                    Error = "checksum_mismatch_after_extract"
                };
            }

            return await _repo.ImportMigrationFactsAsync(tempDb, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new BackupImportResult
            {
                Success = false,
                Mode = BackupImportMode.Migrate,
                Message = "迁移已取消，本机数据未受影响。",
                Error = "cancelled"
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("[备份迁移] 失败", ex);
            return new BackupImportResult
            {
                Success = false,
                Mode = BackupImportMode.Migrate,
                Message = "迁移失败，本机数据未受影响。",
                Error = ex.Message
            };
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // ---------------- 辅助 ----------------

    private static BackupPreview Reject(
        string path, int currentSchemaMax, string reason,
        bool archiveOk, bool integrityOk, long fileSizeBytes = 0, BackupManifest? manifest = null)
        => new()
        {
            FilePath = path,
            FileSizeBytes = fileSizeBytes,
            FormatVersion = manifest?.FormatVersion ?? 0,
            AppVersion = manifest?.AppVersion ?? "",
            ExportedAtUtc = manifest?.ExportedAtUtc ?? "",
            ExportedFromMachine = manifest?.ExportedFromMachine ?? "",
            DatabaseSizeBytes = manifest?.DatabaseSize ?? 0,
            DatabaseSha256 = manifest?.DatabaseSha256 ?? "",
            SchemaMigrationsMax = manifest?.SchemaMigrationsMax ?? 0,
            Counts = manifest?.Counts ?? new BackupCounts(),
            DateRange = manifest?.DateRange,
            ArchiveOk = archiveOk,
            IntegrityOk = integrityOk,
            SchemaCompatible = false,
            CurrentSchemaMax = currentSchemaMax,
            RejectionReason = reason
        };

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return ""; }
    }
}
