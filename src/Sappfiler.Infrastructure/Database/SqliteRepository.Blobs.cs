using System.Security.Cryptography;
using Dapper;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>blob 引用的种类。</summary>
public static class BlobKinds
{
    public const string Cover = "cover";
    public const string Icon = "icon";
}

/// <summary>blob 引用状态：<see cref="Pending"/> = 待补齐（可重试），<see cref="Ready"/> = 本地已有内容。</summary>
public static class BlobRefStatus
{
    public const int Pending = 0;
    public const int Ready = 1;
}

/// <summary>还没推到远端的 blob（内容寻址：同一 sha256 只需上传一次）。</summary>
public sealed record PendingBlobUpload(string Sha256, string? LocalPath, long Size);

/// <summary>等待补齐内容的 blob 引用（实体 → blob）。</summary>
public sealed record PendingBlobRef(string EntityType, string EntityGlobalId, string Kind, string Sha256);

/// <summary>
/// 封面/图标的**内容寻址 blob** 存储层（schema v2）。
///
/// <para>
/// 语义（2026-10-08 用户批准）：
/// <list type="bullet">
///   <item>内容寻址：文件按 <c>sha256</c> 命名，**相同内容只存一份**（远端 / 本地都是如此）；</item>
///   <item>JSON batch 只带 <c>cover_blob_hash</c> / <c>icon_blob_hash</c>，图片本体走独立二进制通道；</item>
///   <item>上传幂等（同 hash 重复 PUT 覆盖同一路径，不新增远端对象）；</item>
///   <item>下载失败**只**改引用状态（<c>待补齐 / 可重试</c>），绝不影响 Game/Session/Adjustment 事实；</item>
///   <item>本地没有封面/图标 ⇒ 不产生 hash、不产生 blob（绝不造假）。</item>
/// </list>
/// </para>
///
/// ⚠️ 与 `main` 的 <c>DailyIconUploadService</c>（Notion File Upload）**完全分离**：
///    那条通道把图片传到 Notion 页面当 icon；这里只把图片放进用户自己的网盘，两者互不认识。
/// </summary>
public partial class SqliteRepository
{
    // ==================== 路径与哈希 ====================

    /// <summary>本地 blob 目录（内容寻址；与封面缓存目录分开：这里是"原始字节"，与展示无关）。</summary>
    public static string BlobDirectory
        => Path.Combine(GameTimeTracker.Core.Services.AppPaths.DataDir, "cache", "blobs");

    public static string BlobFilePath(string sha256) => Path.Combine(BlobDirectory, sha256);

    public static bool BlobExistsLocally(string sha256)
        => !string.IsNullOrWhiteSpace(sha256) && File.Exists(BlobFilePath(sha256));

    /// <summary>封面缓存目录（<c>CoverCacheService</c> 用的同一个目录）。</summary>
    public static string CoverDirectory => GameTimeTracker.Core.Services.AppPaths.CoversDir;

    /// <summary>
    /// 本地封面文件的既有路径（没有则 null）。
    /// ⚠️ 命名规则与 <c>CoverCacheService.GetCoverPath</c> 一致（<c>{platform}_{platformId}.png|jpg</c>），
    /// 这里不新建第二套缓存，只做"找到它"。
    /// </summary>
    public static string? ResolveCoverPath(string platform, string platformId)
    {
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(platformId)) return null;

        var safePlatform = platform.ToLowerInvariant();
        var safeId = string.Join("_", platformId.ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));

        var png = Path.Combine(CoverDirectory, $"{safePlatform}_{safeId}.png");
        if (File.Exists(png)) return png;

        var jpg = Path.Combine(CoverDirectory, $"{safePlatform}_{safeId}.jpg");
        return File.Exists(jpg) ? jpg : null;
    }

    /// <summary>把远端封面写入本地时应落到的文件名（与封面缓存命名一致，UI/统计会直接用它）。</summary>
    public static string CoverTargetPath(string platform, string platformId)
    {
        var safePlatform = platform.ToLowerInvariant();
        var safeId = string.Join("_", platformId.ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(CoverDirectory, $"{safePlatform}_{safeId}.png");
    }

    public static string Sha256FileHex(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public static string Sha256BytesHex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ==================== 本地产生：注册封面 blob ====================

    /// <summary>
    /// 若该游戏**本地已有封面**，把它登记成可上传 blob 并返回其 sha256（否则返回 null）。
    /// 返回的 hash 会被写进 Game 载荷的 <c>cover_blob_hash</c> ⇒ 对端据此下载同一张图。
    /// </summary>
    internal static string? TryRegisterLocalCoverBlob(
        SqliteConnection conn, SqliteTransaction? tx, string platform, string platformId)
    {
        var cover = ResolveCoverPath(platform, platformId);
        if (cover is null) return null;   // 没有本地封面 ⇒ 绝不制造假 blob

        try
        {
            var hash = Sha256FileHex(cover);
            var size = new FileInfo(cover).Length;

            conn.Execute(
                """
                INSERT INTO sync_blobs (sha256, size, local_path, uploaded, upload_attempts, created_at_utc)
                VALUES (@hash, @size, @path, 0, 0, @now)
                ON CONFLICT(sha256) DO UPDATE SET local_path = excluded.local_path;
                """,
                new { hash, size, path = cover, now = UtcNowIso() }, tx);

            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读文件失败只是"这次不带图"，同步本体不受影响。
            AppLog.Warn($"[设备同步] 计算封面哈希失败，本次不带 cover_blob_hash：{ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// 同上，但自带连接与写锁（供没有事务上下文的调用方使用，例如 Gap Repair 的入队路径）。
    /// </summary>
    internal async Task<string?> TryRegisterLocalCoverBlobAsync(string platform, string platformId)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            return TryRegisterLocalCoverBlob(conn, null, platform, platformId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ==================== 远端落地：登记引用（待补齐） ====================

    /// <summary>
    /// 远端 Game 载荷带了 blob hash ⇒ 登记一条**待补齐**引用。
    /// 本地已存在该内容时直接标就绪（已存在就不重复下载）。
    /// </summary>
    internal static void RegisterRemoteBlobRef(
        SqliteConnection conn, SqliteTransaction? tx,
        string entityType, string entityGlobalId, string kind, string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256) || string.IsNullOrWhiteSpace(entityGlobalId)) return;

        var status = BlobExistsLocally(sha256) ? BlobRefStatus.Ready : BlobRefStatus.Pending;

        conn.Execute(
            """
            INSERT INTO sync_blob_refs (entity_type, entity_global_id, kind, sha256, status, attempts, updated_at_utc)
            VALUES (@entityType, @entityGlobalId, @kind, @sha256, @status, 0, @now)
            ON CONFLICT(entity_type, entity_global_id, kind) DO UPDATE SET
                sha256         = excluded.sha256,
                status         = CASE WHEN sync_blob_refs.sha256 = excluded.sha256
                                      THEN sync_blob_refs.status ELSE excluded.status END,
                updated_at_utc = excluded.updated_at_utc;
            """,
            new { entityType, entityGlobalId, kind, sha256, status, now = UtcNowIso() }, tx);
    }

    // ==================== 上传队列 ====================

    internal async Task<IReadOnlyList<PendingBlobUpload>> GetPendingBlobUploadsAsync(int limit = 20)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<PendingBlobUpload>(
            """
            SELECT sha256 AS Sha256, local_path AS LocalPath, size AS Size
            FROM sync_blobs
            WHERE uploaded = 0 AND upload_attempts < 5
            ORDER BY created_at_utc ASC
            LIMIT @limit;
            """,
            new { limit });

        return rows.ToList();
    }

    internal async Task MarkBlobUploadedAsync(string sha256)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE sync_blobs SET uploaded = 1, last_error = NULL WHERE sha256 = @sha256;",
                new { sha256 });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async Task MarkBlobUploadFailedAsync(string sha256, string error)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE sync_blobs
                SET upload_attempts = upload_attempts + 1, last_error = @error
                WHERE sha256 = @sha256;
                """,
                new { sha256, error = Truncate(error, 400) });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>把本地内容登记进 blob 表（下载成功后调用；同 hash 只保留一行 ⇒ 天然去重）。</summary>
    internal async Task RegisterLocalBlobContentAsync(string sha256, string localPath, long size)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO sync_blobs (sha256, size, local_path, uploaded, upload_attempts, created_at_utc)
                VALUES (@sha256, @size, @localPath, 0, 0, @now)
                ON CONFLICT(sha256) DO UPDATE SET local_path = excluded.local_path, size = excluded.size;
                """,
                new { sha256, size, localPath, now = UtcNowIso() });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ==================== 下载（待补齐）队列 ====================

    internal async Task<IReadOnlyList<PendingBlobRef>> GetPendingBlobRefsAsync(int limit = 20)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<PendingBlobRef>(
            """
            SELECT entity_type AS EntityType, entity_global_id AS EntityGlobalId,
                   kind AS Kind, sha256 AS Sha256
            FROM sync_blob_refs
            WHERE status = @pending AND attempts < 8
            ORDER BY updated_at_utc ASC
            LIMIT @limit;
            """,
            new { pending = BlobRefStatus.Pending, limit });

        return rows.ToList();
    }

    internal async Task MarkBlobRefReadyAsync(string entityType, string entityGlobalId, string kind)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE sync_blob_refs
                SET status = @ready, last_error = NULL, updated_at_utc = @now
                WHERE entity_type = @entityType AND entity_global_id = @entityGlobalId AND kind = @kind;
                """,
                new { ready = BlobRefStatus.Ready, entityType, entityGlobalId, kind, now = UtcNowIso() });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async Task MarkBlobRefFailedAsync(string entityType, string entityGlobalId, string kind, string error)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE sync_blob_refs
                SET attempts = attempts + 1, last_error = @error, updated_at_utc = @now
                WHERE entity_type = @entityType AND entity_global_id = @entityGlobalId AND kind = @kind;
                """,
                new { entityType, entityGlobalId, kind, error = Truncate(error, 400), now = UtcNowIso() });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>游戏在本机封面缓存里的落点（远端封面下载成功后写这里）。找不到该游戏返回 null。</summary>
    internal async Task<string?> GetCoverTargetForGameAsync(string gameGlobalId)
    {
        using var conn = CreateConnection();

        // 🛡️ 2026-10-11（身份协议兼容核查）：来件的 identity 可能是本机某一行的**别名**
        //    （canonical 那一行在对端）⇒ 必须按等价类解析，否则封面/图标永远挂不上本地游戏。
        var localGameId = ResolveLocalGameIdByGlobalId(conn, null, gameGlobalId);
        if (localGameId is null) return null;

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT platform, platform_id FROM games WHERE id = @id LIMIT 1;",
            new { id = localGameId.Value });

        if (row is null) return null;

        string? platform = row.platform as string;
        string? platformId = row.platform_id as string;

        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(platformId)) return null;

        return CoverTargetPath(platform, platformId);
    }

    /// <summary>待补齐的 blob 引用条数（诊断 / 测试）。</summary>
    public async Task<int> CountPendingBlobRefsAsync()
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_blob_refs WHERE status = @pending;",
            new { pending = BlobRefStatus.Pending });
    }

    private static string Truncate(string? text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max]);
}
