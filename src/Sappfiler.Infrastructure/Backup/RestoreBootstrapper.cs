using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using GameTimeTracker.Core.Backup;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Backup;

/// <summary>
/// <b>恢复备份的持久化状态机</b>（2026-10-11）。
///
/// <para>
/// 设计前提（已核实）：<c>SqliteRepository</c> 不是 <c>IDisposable</c>、无 <c>Close</c>、
/// <c>_writeLock</c> 为 private 且进程退出走 <c>Environment.Exit(0)</c> ——
/// **没有任何"停写"API**，因此在线替换正在使用的数据库无法保证无并发写。
/// 所以恢复采用 <b>暂存 + 启动早期替换 + 校验 + 失败回滚</b>：
/// 真正的文件替换发生在 <b>下一次启动的最开始</b>（任何后台循环起来之前）。
/// </para>
///
/// <para>
/// 状态机（文件：<c>{DataDir}/restore/restore-state.json</c>，可重复执行）：
/// <code>
///   staged   --(校验 incoming + 给当前库做安全快照)-->  applying
///   applying --(删旧库三件套 + 复制 incoming)-------->  校验
///   校验通过 -------------------------------------------->  applied  ⇒ 清理暂存，报告成功
///   校验失败 -------------------------------------------->  回滚到安全快照
///   回滚确认 OK ---------------------------------------->  failed（数据完好，等用户重试）
///   回滚无法确认 --------------------------------------->  failed（**明确要求人工介入**）
/// </code>
/// </para>
///
/// <para>
/// ⚠️ <b>纪律</b>：
/// <list type="bullet">
///   <item>校验失败或回滚未完成时**绝不清除恢复标记、绝不报成功**；</item>
///   <item><c>failed</c> 状态**不自动重试**（避免每次启动反复折腾数据），保留证据等用户重新导入；
///         重新导入会覆盖状态文件，因此"重试"路径天然存在；</item>
///   <item>替换前先把状态落盘为 <c>applying</c> —— 断电/崩溃后重入能识别"可能已部分替换"。</item>
/// </list>
/// </para>
/// </summary>
public static class RestoreBootstrapper
{
    private const string StateFileName = "restore-state.json";
    private const string IncomingFileName = "incoming.db";
    private const string SafetyFileName = "pre-restore-safety.db";

    private const string StateStaged = "staged";
    private const string StateApplying = "applying";
    private const string StateApplied = "applied";
    private const string StateFailed = "failed";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>恢复暂存目录。<c>{DataDir}/restore</c>。</summary>
    public static string RestoreDir(string dataDir) => Path.Combine(dataDir, "restore");

    /// <summary>替换前安全快照的长期归档目录。<c>{DataDir}/backups</c>。</summary>
    public static string BackupsDir(string dataDir) => Path.Combine(dataDir, "backups");

    /// <summary>是否存在待应用的恢复（UI 可据此提示"重启后生效"）。</summary>
    public static bool HasPendingRestore(string dataDir)
    {
        try
        {
            var stateFile = Path.Combine(RestoreDir(dataDir), StateFileName);
            if (!File.Exists(stateFile)) return false;

            var state = ReadState(stateFile);
            return state is not null && state.State is StateStaged or StateApplying;
        }
        catch
        {
            return false;
        }
    }

    // =========================================================================================
    // 暂存（用户点击"导入 → 恢复备份"时调用；**不触碰当前数据库**）
    // =========================================================================================

    /// <summary>
    /// 把校验过的备份库解到暂存目录并落盘 <c>staged</c> 状态。
    /// ⚠️ 本方法只写 <c>{DataDir}/restore/</c>，**不影响正在使用的数据库**。
    /// </summary>
    public static BackupImportResult StageRestore(string dataDir, string backupFilePath, BackupPreview preview)
    {
        var dir = RestoreDir(dataDir);
        var incoming = Path.Combine(dir, IncomingFileName);
        var stateFile = Path.Combine(dir, StateFileName);

        try
        {
            Directory.CreateDirectory(dir);

            if (!BackupArchive.TryExtractDatabase(backupFilePath, incoming, out var extractError))
            {
                return new BackupImportResult
                {
                    Success = false,
                    Mode = BackupImportMode.Restore,
                    Message = "无法从备份中解出数据库，恢复未开始（当前数据未受影响）。",
                    Error = extractError ?? "解包失败"
                };
            }

            // 解包后复核校验值：容器里的字节与清单声明必须一致（解包过程本身也可能损坏）。
            var actualSha = BackupFileUtil.ComputeFileSha256(incoming);
            if (!string.Equals(actualSha, preview.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(incoming);
                return new BackupImportResult
                {
                    Success = false,
                    Mode = BackupImportMode.Restore,
                    Message = "解包后的数据库与备份校验值不一致，恢复未开始（当前数据未受影响）。",
                    Error = "checksum_mismatch_after_extract"
                };
            }

            // 最后一关：解出来的库必须能打开且完整性检查通过。
            if (!BackupFileUtil.VerifyDatabase(incoming, out var integrityError))
            {
                TryDelete(incoming);
                return new BackupImportResult
                {
                    Success = false,
                    Mode = BackupImportMode.Restore,
                    Message = "解包后的数据库完整性检查未通过，恢复未开始（当前数据未受影响）。",
                    Error = integrityError
                };
            }

            WriteState(stateFile, new RestoreState
            {
                State = StateStaged,
                CreatedAtUtc = DateTime.UtcNow.ToString("O"),
                IncomingSha256 = actualSha,
                BackupSchemaMax = preview.SchemaMigrationsMax,
                CurrentSchemaMaxAtStage = preview.CurrentSchemaMax,
                SourceBackupPath = backupFilePath,
                SourceAppVersion = preview.AppVersion,
                Message = "已暂存，等待重启生效"
            });

            AppLog.Info($"[备份恢复] 已暂存待恢复的数据库（sha256={Short(actualSha)}，来源 {backupFilePath}）");

            return new BackupImportResult
            {
                Success = true,
                Mode = BackupImportMode.Restore,
                StagedForRestart = true,
                RequiresNotionReauth = true,
                Message = "备份已通过校验并暂存。请重启 Sappfiler 使恢复生效"
                    + "（替换前会自动为当前数据库再做一次安全快照）。"
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("[备份恢复] 暂存失败（当前数据未受影响）", ex);
            return new BackupImportResult
            {
                Success = false,
                Mode = BackupImportMode.Restore,
                Message = "暂存恢复数据失败，当前数据未受影响。",
                Error = ex.Message
            };
        }
    }

    // =========================================================================================
    // 启动早期应用（App.OnLaunched 里、任何仓储/后台循环创建之前调用）
    // =========================================================================================

    /// <summary>
    /// 应用待处理的恢复。**必须**在任何 <c>SqliteRepository</c>（尤其后台 5 秒心跳）启动之前调用。
    ///
    /// <para>无待处理恢复时返回 <see cref="RestoreOutcomeKind.None"/>（正常启动路径，零开销）。</para>
    ///
    /// <para>
    /// ⚠️ 调用方必须把结果展示给用户：<c>FailedRolledBack</c> / <c>FailedRollbackUnconfirmed</c>
    /// 属于**必须告知**的结果，不允许静默。
    /// </para>
    /// </summary>
    public static RestoreOutcome ApplyPendingRestore(string dataDir, string dbPath)
    {
        var dir = RestoreDir(dataDir);
        var stateFile = Path.Combine(dir, StateFileName);

        try
        {
            if (!File.Exists(stateFile))
            {
                // 正常运行：restore 目录应不存在或为空。
                // 但若上一次"成功后的清理"被中断，可能只剩**可重建的**暂存副本 ⇒ 顺手清掉；
                // ⚠️ 安全快照绝不删除（它可能是用户数据的唯一副本），只归档并留痕。
                CleanupOrphans(dir);
                return new RestoreOutcome { Kind = RestoreOutcomeKind.None };
            }

            var state = ReadState(stateFile);
            if (state is null)
            {
                // 状态文件损坏：不动数据库、不清标记 ⇒ 必须人工介入（绝不猜测该不该替换）。
                return new RestoreOutcome
                {
                    Kind = RestoreOutcomeKind.FailedRollbackUnconfirmed,
                    Message = "恢复状态文件已损坏，无法判断上一次恢复的进度；"
                        + "为避免覆盖数据，本次未做任何改动。请在设置页重新导入备份，或手动检查数据目录下的 restore 文件夹。"
                };
            }

            switch (state.State)
            {
                case StateFailed:
                    // 上一次失败：**绝不自动重试替换**（危险），但要做两件安全且幂等的补救：
                    //   ① 数据库可用 ⇒ 说明回滚其实成功了，只是状态没写下去；
                    //   ② 数据库不可用却有安全快照 ⇒ 用快照补完回滚（绝不覆盖唯一可用副本）。
                    return ReconcileFailedState(stateFile, state, dir, dbPath);

                case StateApplied:
                    // 已完成但清理阶段被中断（如断电）：只做清理，绝不重复替换。
                    // ⚠️ 安全快照要**归档**出去，不能留在 restore 目录里变成用户找不到的孤儿。
                    var archived = MoveSafetyToBackups(dir, Path.Combine(dir, SafetyFileName), state.CreatedAtUtc);
                    Cleanup(dir, keepSafety: false);
                    return new RestoreOutcome
                    {
                        Kind = RestoreOutcomeKind.Applied,
                        Message = "数据恢复已生效。",
                        SafetySnapshotPath = archived
                    };

                case StateStaged:
                case StateApplying:
                    return ApplyAndFinalize(dir, stateFile, state, dbPath);

                default:
                    return new RestoreOutcome
                    {
                        Kind = RestoreOutcomeKind.FailedRollbackUnconfirmed,
                        Message = $"恢复状态文件包含未知状态「{state.State}」，为避免覆盖数据未做任何改动。"
                    };
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("[备份恢复] 应用待处理恢复时发生异常", ex);
            return new RestoreOutcome
            {
                Kind = RestoreOutcomeKind.FailedRollbackUnconfirmed,
                Message = $"应用恢复时发生异常：{ex.Message}。当前数据未被改动，但请检查数据目录。"
            };
        }
    }

    private static RestoreOutcome ApplyAndFinalize(string dir, string stateFile, RestoreState state, string dbPath)
    {
        var incoming = Path.Combine(dir, IncomingFileName);
        var safety = Path.Combine(dir, SafetyFileName);
        var resumed = state.State == StateApplying;

        if (!File.Exists(incoming))
        {
            return Fail(stateFile, state, dir,
                "备份数据文件缺失（无法继续恢复），当前数据库未被改动。请重新导入备份。",
                rollbackConfirmed: true, safetyKept: false);
        }

        if (!BackupFileUtil.VerifyDatabase(incoming, out var incomingError))
        {
            return Fail(stateFile, state, dir,
                $"暂存的备份数据未通过完整性检查（{incomingError}），已中止恢复；当前数据库未被改动。",
                rollbackConfirmed: true, safetyKept: false);
        }

        if (!string.Equals(BackupFileUtil.ComputeFileSha256(incoming), state.IncomingSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Fail(stateFile, state, dir,
                "暂存的备份数据与校验值不一致（可能被改动或损坏），已中止恢复；当前数据库未被改动。",
                rollbackConfirmed: true, safetyKept: false);
        }

        // ---- 首次进入（staged）：先给当前数据库留一条退路 ----
        var safetyKept = false;
        var safetyPath = "";
        if (!resumed)
        {
            var currentReadable = File.Exists(dbPath) && BackupFileUtil.VerifyDatabase(dbPath, out _);

            if (currentReadable)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    if (File.Exists(safety)) File.Delete(safety);
                    BackupFileUtil.SnapshotDatabase(dbPath, safety);
                    safetyKept = true;
                    safetyPath = safety;
                    AppLog.Info($"[备份恢复] 已为当前数据库创建安全快照：{safety}");
                }
                catch (Exception ex)
                {
                    // 当前库**可读**却做不出快照（如磁盘满）⇒ 中止，绝不冒险替换。
                    // ⚠️ 同时清掉可能写了一半的快照文件：半截数据库会误导人工排查。
                    TryDelete(safety);
                    return Fail(stateFile, state, dir,
                        $"无法为当前数据库创建安全快照（{ex.Message}），已中止恢复；当前数据库未被改动。",
                        rollbackConfirmed: true, safetyKept: false);
                }
            }
            else
            {
                // 当前库本来就不可读（例如上一次替换中途断电）：不生成快照也要继续，否则用户无法自救。
                AppLog.Warn("[备份恢复] 当前数据库不可读，跳过安全快照并直接应用备份");
            }
        }
        else if (File.Exists(safety))
        {
            safetyKept = true;
            safetyPath = safety;
        }

        // ---- 先把"正在替换"落盘，再动文件：断电后重入可识别中间态 ----
        state.State = StateApplying;
        state.Message = "正在替换数据库…";
        state.SafetySnapshotKept = safetyKept;
        WriteState(stateFile, state);

        // ---- 执行替换 ----
        try
        {
            BackupFileUtil.DeleteSqliteFileSet(dbPath);
            File.Copy(incoming, dbPath, overwrite: true);
        }
        catch (Exception ex)
        {
            return Rollback(stateFile, state, dir, dbPath, safety, safetyKept,
                $"替换数据库文件失败（{ex.Message}）");
        }

        // ---- 替换后立即校验（"成功"必须是验证过的，不是假设的） ----
        if (!BackupFileUtil.VerifyDatabase(dbPath, out var postError))
        {
            return Rollback(stateFile, state, dir, dbPath, safety, safetyKept,
                $"替换后的数据库未通过完整性检查（{postError}）");
        }

        var restoredSchemaMax = BackupFileUtil.ReadSchemaMax(dbPath);
        if (state.CurrentSchemaMaxAtStage > 0 && restoredSchemaMax > state.CurrentSchemaMaxAtStage)
        {
            return Rollback(stateFile, state, dir, dbPath, safety, safetyKept,
                $"备份的数据库结构版本（{restoredSchemaMax}）高于当前应用支持的版本"
                + $"（{state.CurrentSchemaMaxAtStage}）");
        }

        // ---- 成功 ----
        state.State = StateApplied;
        state.Message = "数据恢复已生效";
        WriteState(stateFile, state);

        var keptSafety = MoveSafetyToBackups(dir, safety, state.CreatedAtUtc);

        Cleanup(dir, keepSafety: false);

        AppLog.Info($"[备份恢复] 恢复成功并已清理暂存（安全快照保留于 {keptSafety}）");

        return new RestoreOutcome
        {
            Kind = resumed ? RestoreOutcomeKind.Resumed : RestoreOutcomeKind.Applied,
            Message = "数据恢复已生效。"
                + (string.IsNullOrWhiteSpace(keptSafety) ? "" : "替换前的数据已保留为安全快照，可在需要时人工取回。"),
            SafetySnapshotPath = keptSafety
        };
    }

    private static RestoreOutcome Rollback(
        string stateFile, RestoreState state, string dir, string dbPath, string safety, bool safetyKept, string reason)
    {
        var confirmed = false;
        var noDatabaseAvailable = false;

        try
        {
            BackupFileUtil.DeleteSqliteFileSet(dbPath);

            if (safetyKept && File.Exists(safety))
            {
                File.Copy(safety, dbPath, overwrite: true);
                confirmed = BackupFileUtil.VerifyDatabase(dbPath, out _);
            }
            else
            {
                // 没有安全快照可回滚（当前库本来就不存在/不可读）。
                // ⚠️ "没有半截数据库" ≠ "数据完好"：这种情况用户其实没有可用数据，
                //    必须报"需人工介入"，绝不能宣称"已回滚、数据完好"。
                confirmed = !File.Exists(dbPath);
                noDatabaseAvailable = confirmed;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("[备份恢复] 回滚失败", ex);
            confirmed = false;
        }

        state.State = StateFailed;
        state.RollbackConfirmed = confirmed && !noDatabaseAvailable;
        state.SafetySnapshotKept = safetyKept && !noDatabaseAvailable;
        // ⚠️ 保留快照路径：回滚没确认成功时，这是用户唯一能拿去人工恢复的线索。
        state.SafetySnapshotPath = safetyKept ? safety : "";

        state.Message = noDatabaseAvailable
            ? $"恢复失败（{reason}）；未发现可用的数据库文件，且没有可回滚的安全快照。"
              + "请重新导入备份，或检查数据目录。"
            : confirmed
                ? $"恢复失败（{reason}）；已回滚到替换前的数据，当前数据完好。可在设置页重新导入备份重试。"
                : $"恢复失败（{reason}）；⚠️ 回滚未能确认成功，请检查数据目录下的 restore 文件夹与安全快照。";
        WriteState(stateFile, state);

        AppLog.Warn($"[备份恢复] {state.Message}");

        return new RestoreOutcome
        {
            Kind = state.RollbackConfirmed
                ? RestoreOutcomeKind.FailedRolledBack
                : RestoreOutcomeKind.FailedRollbackUnconfirmed,
            Message = state.Message,
            SafetySnapshotPath = state.SafetySnapshotPath
        };
    }

    /// <summary>
    /// <c>failed</c> 状态的**安全且幂等**的补救（绝不重试替换）：
    /// <list type="number">
    ///   <item>数据库可用 ⇒ 回滚其实成功了，只是状态没写下去 ⇒ 修正状态并报告；</item>
    ///   <item>数据库不可用、但仍有安全快照 ⇒ 用快照补完回滚
    ///         （这是"回滚到一半崩溃"的中间态；此时快照是**唯一**可用副本，绝不能被忽略或覆盖）。</item>
    /// </list>
    /// </summary>
    private static RestoreOutcome ReconcileFailedState(
        string stateFile, RestoreState state, string dir, string dbPath)
    {
        var safety = Path.Combine(dir, SafetyFileName);

        if (state.RollbackConfirmed)
        {
            return new RestoreOutcome
            {
                Kind = RestoreOutcomeKind.FailedRolledBack,
                Message = string.IsNullOrWhiteSpace(state.Message)
                    ? "上一次恢复未成功。当前数据未被改动。"
                    : state.Message,
                SafetySnapshotPath = state.SafetySnapshotPath
            };
        }

        // ① 数据库已经可用 ⇒ 回滚实际成功（状态只是没来得及写）
        if (File.Exists(dbPath) && BackupFileUtil.VerifyDatabase(dbPath, out _))
        {
            state.RollbackConfirmed = true;
            state.Message = "上一次恢复未成功；当前数据库可用、数据未受影响。可在设置页重新导入备份重试。";
            WriteState(stateFile, state);

            return new RestoreOutcome
            {
                Kind = RestoreOutcomeKind.FailedRolledBack,
                Message = state.Message,
                SafetySnapshotPath = state.SafetySnapshotPath
            };
        }

        // ② 数据库不可用：若还有安全快照，补完回滚
        if (File.Exists(safety))
        {
            var restored = TryRestoreFromSafety(dbPath, safety, out var repairError);

            if (restored)
            {
                state.RollbackConfirmed = true;
                state.Message = "上一次恢复未成功；检测到数据库文件缺失，已用恢复前的安全快照自动还原，当前数据完好。"
                    + "可在设置页重新导入备份重试。";
                WriteState(stateFile, state);

                var archived = MoveSafetyToBackups(dir, safety, state.CreatedAtUtc);
                state.SafetySnapshotPath = archived;
                WriteState(stateFile, state);

                AppLog.Info($"[备份恢复] 已从安全快照自动还原数据库（归档于 {archived}）");

                return new RestoreOutcome
                {
                    Kind = RestoreOutcomeKind.FailedRolledBack,
                    Message = state.Message,
                    SafetySnapshotPath = archived
                };
            }

            // 自动还原失败：**保留安全快照**，明确要求人工介入，绝不清理。
            state.Message = $"上一次恢复未成功，且用安全快照自动还原也失败（{repairError}）。"
                + $"请先另行备份安全快照（{safety}）后再尝试重新导入。";
            WriteState(stateFile, state);

            return new RestoreOutcome
            {
                Kind = RestoreOutcomeKind.FailedRollbackUnconfirmed,
                Message = state.Message,
                SafetySnapshotPath = safety
            };
        }

        return new RestoreOutcome
        {
            Kind = RestoreOutcomeKind.FailedRollbackUnconfirmed,
            Message = string.IsNullOrWhiteSpace(state.Message)
                ? "上一次恢复未成功，且当前没有可用的数据库文件与安全快照。请重新导入备份。"
                : state.Message,
            SafetySnapshotPath = state.SafetySnapshotPath
        };
    }

    /// <summary>用安全快照还原数据库（只在"当前库不可用"时调用）。</summary>
    private static bool TryRestoreFromSafety(string dbPath, string safety, out string error)
    {
        error = "";
        try
        {
            BackupFileUtil.DeleteSqliteFileSet(dbPath);
            File.Copy(safety, dbPath, overwrite: true);

            if (BackupFileUtil.VerifyDatabase(dbPath, out var verifyError)) return true;

            error = verifyError;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 没有状态文件时的残留处理：删掉**可重建的**暂存副本（用户手里的 .sappfiler 才是权威），
    /// 把无主的**安全快照**归档到 backups 目录并留痕 —— 绝不删除它。
    /// </summary>
    private static void CleanupOrphans(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            TryDelete(Path.Combine(dir, IncomingFileName));

            var safety = Path.Combine(dir, SafetyFileName);
            if (File.Exists(safety))
            {
                var archived = MoveSafetyToBackups(dir, safety, "");
                AppLog.Warn($"[备份恢复] 发现无状态文件关联的残留安全快照，已归档到：{archived}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[备份恢复] 清理残留恢复文件失败：{ex.Message}");
        }
    }

    private static RestoreOutcome Fail(
        string stateFile, RestoreState state, string dir, string message, bool rollbackConfirmed, bool safetyKept)
    {
        state.State = StateFailed;
        state.RollbackConfirmed = rollbackConfirmed;
        state.SafetySnapshotKept = safetyKept;
        state.SafetySnapshotPath = "";
        state.Message = message;
        WriteState(stateFile, state);

        // ⚠️ 刻意**不清理** incoming 与状态文件：校验失败时保留证据，且"重新导入"会自然覆盖它。
        AppLog.Warn($"[备份恢复] {message}");

        return new RestoreOutcome
        {
            Kind = rollbackConfirmed ? RestoreOutcomeKind.FailedRolledBack : RestoreOutcomeKind.FailedRollbackUnconfirmed,
            Message = message
        };
    }

    private static string MoveSafetyToBackups(string dir, string safety, string createdAtUtc)
    {
        try
        {
            if (!File.Exists(safety)) return "";

            var dataDir = Directory.GetParent(dir)?.FullName ?? dir;
            var backups = BackupsDir(dataDir);
            Directory.CreateDirectory(backups);

            var stamp = DateTime.TryParse(createdAtUtc, out var parsed)
                ? parsed.ToLocalTime().ToString("yyyyMMdd-HHmmss")
                : DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(backups, $"pre-restore-{stamp}.db");

            File.Move(safety, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[备份恢复] 安全快照归档失败（不影响恢复结果）：{ex.Message}");
            return File.Exists(safety) ? safety : "";
        }
    }

    private static void Cleanup(string dir, bool keepSafety)
    {
        try
        {
            TryDelete(Path.Combine(dir, IncomingFileName));
            TryDelete(Path.Combine(dir, StateFileName));
            if (!keepSafety) TryDelete(Path.Combine(dir, SafetyFileName));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[备份恢复] 清理暂存目录失败（不影响恢复结果）：{ex.Message}");
        }
    }

    // ---------------- 状态文件 ----------------

    private static RestoreState? ReadState(string stateFile)
    {
        try
        {
            var json = File.ReadAllText(stateFile);
            return JsonSerializer.Deserialize<RestoreState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteState(string stateFile, RestoreState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tmp = stateFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, stateFile, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Short(string value)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= 8 ? value : value[..8] + "…";

    /// <summary>恢复状态文件的模型（仅 Infrastructure 内部使用，不进协议、不进备份）。</summary>
    private sealed class RestoreState
    {
        public string State { get; set; } = StateStaged;
        public string CreatedAtUtc { get; set; } = "";
        public string IncomingSha256 { get; set; } = "";
        public int BackupSchemaMax { get; set; }

        /// <summary>暂存时当前应用支持的 schema 最高版本（启动替换后据此判断"备份是否来自更新版本"）。</summary>
        public int CurrentSchemaMaxAtStage { get; set; }

        public string SourceBackupPath { get; set; } = "";
        public string SourceAppVersion { get; set; } = "";
        public string Message { get; set; } = "";

        /// <summary>失败时：回滚是否已被确认成功。</summary>
        public bool RollbackConfirmed { get; set; }

        /// <summary>失败时：安全快照是否仍然保留在 restore 目录。</summary>
        public bool SafetySnapshotKept { get; set; }

        /// <summary>失败时：安全快照的绝对路径（供 UI/日志指引用户手动取回）。</summary>
        public string SafetySnapshotPath { get; set; } = "";
    }
}

/// <summary>
/// 备份 / 恢复共用的文件级工具（SHA-256、SQLite 文件集删除、完整性校验、一致性快照）。
/// </summary>
internal static class BackupFileUtil
{
    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static long GetFileSizeOrZero(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    /// <summary>删除 SQLite 的 <c>.db</c>/<c>-wal</c>/<c>-shm</c> 三件套。</summary>
    public static void DeleteSqliteFileSet(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var path = dbPath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删不掉不致命：后续 Copy(overwrite) 会覆盖主库内容。
            }
        }
    }

    /// <summary>用 SQLite 官方 Backup API 复制一份一致快照（并切成自包含单文件）。</summary>
    public static void SnapshotDatabase(string sourcePath, string targetPath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(sourcePath),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        source.Open();

        // ⚠️ Pooling=false：连接池会在 Close() 后继续持有句柄，导致快照文件无法被后续步骤读写。
        using var dest = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(targetPath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        dest.Open();

        source.BackupDatabase(dest);
        dest.Execute("PRAGMA journal_mode=DELETE;");
        dest.Close();
    }

    public static bool VerifyDatabase(string dbPath, out string error)
    {
        error = "";
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(dbPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            conn.Open();

            var result = conn.ExecuteScalar<string?>("PRAGMA integrity_check;");
            if (!string.Equals(result?.Trim(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                error = "数据库完整性检查未通过";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static int ReadSchemaMax(string dbPath)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(dbPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            conn.Open();
            return conn.ExecuteScalar<int>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations;");
        }
        catch
        {
            return 0;
        }
    }
}
