using GameTimeTracker.Core.Backup;
using GameTimeTracker.Infrastructure.Database;

namespace GameTimeTracker.Infrastructure.Backup;

/// <summary>
/// <b>导出备份</b>（2026-10-11）。
///
/// <para>
/// 流程：<b>一致性快照 → 剥离凭据 → 读概况 → 打包 → 生成后校验 → 原子替换</b>。
/// </para>
///
/// <para>
/// ⚠️ 关键纪律：
/// <list type="bullet">
///   <item>快照用 SQLite Online Backup API（后台 5 秒心跳仍在写入时也保证页面级一致），
///         **绝不**直接复制 <c>.db</c>/<c>-wal</c>/<c>-shm</c>；</item>
///   <item>先写 <c>*.tmp</c>，校验通过才 <c>File.Move</c> 到最终路径 ⇒ 失败不会留下"看似成功的残缺备份"；</item>
///   <item>凭据剥离后**复核**为零残留，复核不过就中止导出。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SappfilerBackupService
{
    private readonly SqliteRepository _repo;
    private readonly string _appVersion;

    public SappfilerBackupService(SqliteRepository repo, string appVersion)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion.Trim();
    }

    /// <summary>
    /// 导出到 <paramref name="targetFilePath"/>（扩展名由调用方保证为 <c>.sappfiler</c>）。
    /// </summary>
    public async Task<BackupExportResult> ExportAsync(string targetFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            return Fail("导出路径为空。");
        }

        var target = Path.GetFullPath(targetFilePath);
        var targetDir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(targetDir))
        {
            return Fail("无法解析导出目录。");
        }

        var exportedAt = DateTime.UtcNow.ToString("O");
        var workDir = Path.Combine(Path.GetTempPath(), "sappfiler-backup-" + Guid.NewGuid().ToString("N"));
        var snapshotPath = Path.Combine(workDir, "database.db");

        // 与最终文件同目录 ⇒ 同卷 ⇒ File.Move 是原子替换（不会出现半个备份文件）。
        var tempZip = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(targetDir);

            // ① 一致性快照（同步 API，放到线程池避免卡住 UI）
            await Task.Run(() => _repo.CreateConsistentSnapshot(snapshotPath), ct).ConfigureAwait(false);

            if (!File.Exists(snapshotPath) || new FileInfo(snapshotPath).Length == 0)
            {
                return Fail("生成数据库快照失败（快照文件为空）。");
            }

            // ② 剥离认证秘密（并在方法内部复核零残留；不通过会抛）
            var stripped = SqliteRepository.StripCredentialsFromSnapshot(snapshotPath);
            if (stripped > 0)
            {
                AppLog.Info($"[备份导出] 已从快照中剥离 {stripped} 条凭据设置（notion_token / webdav 账号）");
            }

            // ③ 读概况（同时复核快照可打开且完整）
            var stats = SqliteRepository.ReadBackupStats(snapshotPath);
            if (!stats.IntegrityOk)
            {
                return Fail("快照未通过完整性检查，已中止导出（未生成任何文件）。");
            }

            var sha = BackupFileUtil.ComputeFileSha256(snapshotPath);
            var snapshotSize = BackupFileUtil.GetFileSizeOrZero(snapshotPath);

            var manifest = new BackupManifest
            {
                FormatVersion = BackupFormat.CurrentFormatVersion,
                AppVersion = _appVersion,
                ExportedAtUtc = exportedAt,
                ExportedFromMachine = SafeMachineName(),
                DatabaseSha256 = sha,
                DatabaseSize = snapshotSize,
                SchemaMigrationsMax = stats.SchemaMax,
                Counts = stats.Counts,
                DateRange = stats.DateRange,
                ContainsCredentials = false,
                Notes = stripped > 0
                    ? "已剥离认证信息（Notion Token、WebDAV 账号）；恢复后需按需重新填写。"
                    : "不含认证信息。"
            };

            // ④ 打包到临时文件
            if (!BackupArchive.TryWrite(tempZip, manifest, snapshotPath, out var writeError))
            {
                return Fail(writeError ?? "写入备份文件失败。");
            }

            // ⑤ **生成后校验**：读回清单 + 重算数据库条目校验值，必须与快照逐字节一致
            if (!BackupArchive.TryReadManifest(tempZip, out var readBack, out var readError))
            {
                TryDelete(tempZip);
                return Fail($"生成后的备份无法读回（{readError}），已删除残留文件。");
            }

            if (readBack is null
                || readBack.FormatVersion != BackupFormat.CurrentFormatVersion
                || !string.Equals(readBack.DatabaseSha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempZip);
                return Fail("生成后的备份校验值不一致，已删除残留文件。");
            }

            var rereadSha = BackupArchive.ComputeDatabaseSha256(tempZip);
            if (!string.Equals(rereadSha, sha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempZip);
                return Fail("生成后的备份内容与数据库快照不一致，已删除残留文件。");
            }

            // ⑥ 原子替换到最终路径
            File.Move(tempZip, target, overwrite: true);

            var fileSize = BackupFileUtil.GetFileSizeOrZero(target);
            AppLog.Info($"[备份导出] 完成：{target}（{fileSize} 字节，games={stats.Counts.Games}，"
                + $"sessions={stats.Counts.Sessions}，daily={stats.Counts.DailySummary}）");

            // ⑦ 「上次成功导出备份」的事实时间戳（2026-10-12）。
            //    用途：既没绑 Notion 也没启用 WebDAV 时，主界面左下角状态胶囊显示它 —— 有依据。
            //    只在**原子替换成功之后**写；写失败不影响导出结果（用户要的是备份文件本身）。
            try
            {
                await _repo.SetSettingAsync(BackupFormat.SettingKeyLastExportUtc, exportedAt);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[备份导出] 记录导出时间失败（不影响备份本身）：{ex.Message}");
            }

            return new BackupExportResult
            {
                Success = true,
                FilePath = target,
                FileSizeBytes = fileSize,
                ExportedAtUtc = exportedAt,
                Counts = stats.Counts,
                DateRange = stats.DateRange
            };
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempZip);
            return Fail("导出已取消。");
        }
        catch (Exception ex)
        {
            TryDelete(tempZip);
            AppLog.Error("[备份导出] 失败", ex);
            return Fail($"导出失败：{ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static BackupExportResult Fail(string message)
        => new() { Success = false, Error = message };

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return ""; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
