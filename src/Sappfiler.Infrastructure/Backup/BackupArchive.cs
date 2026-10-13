using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GameTimeTracker.Core.Backup;

namespace GameTimeTracker.Infrastructure.Backup;

/// <summary>
/// <c>.sappfiler</c> 容器（ZIP）的**唯一读写实现**（2026-10-11）。
///
/// <para>
/// 只负责"字节搬运 + 结构校验"，不含任何业务判断（版本兼容性、能否导入由调用方决定），
/// 这样"校验完成前不修改现有数据"这条纪律只有一个地方需要保证。
/// </para>
///
/// <para>
/// ⚠️ 所有读取都是**流式**的：损坏 / 截断的 ZIP 会在解压时抛 <see cref="InvalidDataException"/>，
/// 我们统一翻译成"备份文件损坏或截断"的可读错误，绝不让异常冒到 UI 变成"操作失败"。
/// </para>
/// </summary>
internal static class BackupArchive
{
    /// <summary>容器内条目数量的上限（本格式只需要 2 个；给未来扩展留余量，同时挡住条目爆炸）。</summary>
    private const int MaxEntries = 32;

    /// <summary>
    /// <b>容器结构校验</b>：条目数量、条目名合法性、重名。
    ///
    /// <para>
    /// ⚠️ 重名必须**明确拒绝**，不能"取第一个"：ZIP 允许同名条目，不同解压实现可能选中不同的那一份，
    /// 于是"校验的是 A、解出来的是 B"（ZIP 混淆）。条目名带路径分隔符 / <c>..</c> / 绝对路径
    /// 同样拒绝（Zip Slip 防护：我们虽然按固定名字取条目，但异常结构本身就是"这不是我们的备份"的证据）。
    /// </para>
    /// </summary>
    public static bool TryValidateStructure(string zipPath, out string? error)
    {
        error = null;

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            if (zip.Entries.Count == 0)
            {
                error = "备份容器为空（没有任何条目）";
                return false;
            }

            if (zip.Entries.Count > MaxEntries)
            {
                error = $"备份容器条目数量异常（{zip.Entries.Count} 个），已拒绝。";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;

                if (string.IsNullOrWhiteSpace(name)
                    || name.Contains('/')
                    || name.Contains('\\')
                    || name.Contains("..", StringComparison.Ordinal)
                    || Path.IsPathRooted(name))
                {
                    error = $"备份容器包含异常条目名「{name}」，已拒绝。";
                    return false;
                }

                if (!seen.Add(name))
                {
                    error = $"备份容器包含重复条目「{name}」，已拒绝（无法确定该以哪一份为准）。";
                    return false;
                }
            }

            if (!seen.Contains(BackupFormat.ManifestEntryName) || !seen.Contains(BackupFormat.DatabaseEntryName))
            {
                error = $"备份容器缺少必需条目（{BackupFormat.ManifestEntryName} / {BackupFormat.DatabaseEntryName}）。";
                return false;
            }

            return true;
        }
        catch (InvalidDataException)
        {
            error = "备份文件损坏或不是有效的 ZIP 容器（可能被截断或改写）";
            return false;
        }
        catch (IOException ex)
        {
            error = $"无法读取备份文件：{ex.Message}";
            return false;
        }
    }

    /// <summary>容器内数据库条目的**未压缩字节数**；条目缺失或容器损坏时返回 -1。</summary>
    public static long GetDatabaseEntrySize(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(BackupFormat.DatabaseEntryName);
            return entry is null ? -1 : entry.Length;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>读取清单。返回 <c>false</c> 时 <paramref name="error"/> 一定非空。</summary>
    public static bool TryReadManifest(string zipPath, out BackupManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(BackupFormat.ManifestEntryName);
            if (entry is null)
            {
                error = $"备份文件缺少 {BackupFormat.ManifestEntryName}（这不是一个有效的 Sappfiler 备份）";
                return false;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = reader.ReadToEnd();

            return BackupManifestCodec.TryParse(json, out manifest, out error);
        }
        catch (InvalidDataException)
        {
            error = "备份文件损坏或不是有效的 ZIP 容器（可能被截断或改写）";
            return false;
        }
        catch (IOException ex)
        {
            error = $"无法读取备份文件：{ex.Message}";
            return false;
        }
    }

    /// <summary>数据库条目是否存在于容器内。</summary>
    public static bool HasDatabaseEntry(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.GetEntry(BackupFormat.DatabaseEntryName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>流式计算容器内数据库条目的 SHA-256（小写 hex）。条目缺失或损坏时返回 <c>null</c>。</summary>
    public static string? ComputeDatabaseSha256(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(BackupFormat.DatabaseEntryName);
            if (entry is null) return null;

            using var stream = entry.Open();
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把容器内的数据库条目解到 <paramref name="targetDbPath"/>（覆盖）。</summary>
    public static bool TryExtractDatabase(string zipPath, string targetDbPath, out string? error)
    {
        error = null;

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(targetDbPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(BackupFormat.DatabaseEntryName);
            if (entry is null)
            {
                error = $"备份文件缺少 {BackupFormat.DatabaseEntryName}（无法恢复数据）";
                return false;
            }

            using var source = entry.Open();
            using var target = File.Create(targetDbPath);
            source.CopyTo(target);
            return true;
        }
        catch (InvalidDataException)
        {
            error = "备份文件损坏或截断，无法解出数据库";
            return false;
        }
        catch (Exception ex)
        {
            error = $"解出数据库失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>写容器（调用方须保证 <paramref name="tempZipPath"/> 与最终路径同卷，以保证原子替换）。</summary>
    public static bool TryWrite(string tempZipPath, BackupManifest manifest, string databasePath, out string? error)
    {
        error = null;

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(tempZipPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);

            using (var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                var manifestEntry = zip.CreateEntry(BackupFormat.ManifestEntryName, CompressionLevel.Optimal);
                using (var stream = manifestEntry.Open())
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(BackupManifestCodec.Serialize(manifest));
                }

                zip.CreateEntryFromFile(databasePath, BackupFormat.DatabaseEntryName, CompressionLevel.Optimal);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"写入备份文件失败：{ex.Message}";
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            return false;
        }
    }
}
