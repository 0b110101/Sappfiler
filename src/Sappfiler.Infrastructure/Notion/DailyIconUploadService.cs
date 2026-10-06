using GameTimeTracker.Core.Covers;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Notion;

namespace GameTimeTracker.Infrastructure.Notion;

/// <summary>
/// 「CoverHash → file_upload_id」的唯一入口：负责**复用已有的上传**，必要时才新上传一次。
/// </summary>
/// <remarks>
/// 三条铁律（对应 2026-10-06 的验收条件）：
/// <list type="number">
/// <item>命中映射就**复用**，绝不重复上传；</item>
/// <item>任何失败都只记日志并返回 <c>null</c> —— **绝不抛异常**，因此绝不会阻塞本地游戏时长记录；</item>
/// <item>映射里的 id 经 Notion 查询发现不可用时，**最多自动重传一次**。</item>
/// </list>
///
/// ⚠️ 持久标识只有 <c>file_upload_id</c>。
/// Notion 读回页面图标时给的是临时 <c>icon.file.url</c>（1 小时有效），
/// 本服务**从不读取、也从不写回**那种临时 URL —— 否则一次读取就会把真正的映射覆盖坏。
/// </remarks>
public sealed class DailyIconUploadService
{
    private readonly INotionClient _client;
    private readonly INotionIconMappingStore _store;

    /// <summary>本轮进程内已向 Notion 确认过"仍然可用"的 id，避免每次复用都多打一次 API。</summary>
    private readonly HashSet<string> _verifiedThisRun = new(StringComparer.OrdinalIgnoreCase);

    public DailyIconUploadService(INotionClient client, INotionIconMappingStore store)
    {
        _client = client;
        _store = store;
    }

    /// <summary>
    /// 保证该封面文件已上传，返回可用于 page icon 的 <c>file_upload_id</c>；
    /// 本地没有可用封面、或上传/复用失败时返回 <c>null</c>（调用方应当"这次就不设图标"）。
    /// </summary>
    public async Task<string?> EnsureUploadedAsync(string? coverPath, CancellationToken ct = default)
    {
        if (!CoverFileProbe.IsUsable(coverPath)) return null;

        try
        {
            var hash = CoverFileProbe.Sha256Hex(coverPath!);
            var size = new FileInfo(coverPath!).Length;
            var key = NotionIconUploadKeys.Key(hash);

            var mapping = NotionIconMapping.Parse(await _store.GetSettingAsync(key));
            if (mapping != null && !string.IsNullOrWhiteSpace(mapping.FileUploadId))
            {
                if (await IsReusableAsync(mapping, hash, size))
                {
                    mapping.Touch();
                    await SaveAsync(key, mapping);
                    return mapping.FileUploadId;
                }

                // 失效（或内容已变）→ 丢掉旧映射，走到下面**重传一次**。
                AppLog.Warn($"[DailyIcon] 已有映射不可复用（id={Short(mapping.FileUploadId)}），将重新上传一次");
                await SafeDeleteAsync(key);
            }

            var contentType = CoverFileProbe.DetectContentType(coverPath!);
            var fileUploadId = await UploadOnceAsync(coverPath!, hash, size, contentType);
            if (string.IsNullOrWhiteSpace(fileUploadId)) return null;

            await SaveAsync(key, new NotionIconMapping
            {
                FileUploadId = fileUploadId!,
                ContentType = contentType,
                Size = size,
                BytesHash = hash,
                UploadedAtUtc = DateTimeOffset.UtcNow,
                LastUsedAtUtc = DateTimeOffset.UtcNow,
                AttachedCount = 1,
                SourceDevice = null
            });

            AppLog.Info($"[DailyIcon] 封面已上传并记录映射（hash={Short(hash)} id={Short(fileUploadId!)} type={contentType} size={size}）");
            return fileUploadId;
        }
        catch (Exception ex)
        {
            // 铁律②：上传链路永不把异常抛给"记录游戏时长"的主流程。
            AppLog.Warn($"[DailyIcon] 封面复用/上传失败，本次不设图标（不影响本地记录）: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 本映射是否还能用：哈希必须一致（内容变了就不复用），并且 Notion 侧查询不到"不可用"。
    /// </summary>
    private async Task<bool> IsReusableAsync(NotionIconMapping mapping, string hash, long size)
    {
        // 内容级哈希是主键：不一致说明本地封面已换图 → 必须重新上传（旧 id 仍可能被别的页面用着，故不删远端）。
        if (!string.Equals(mapping.BytesHash, hash, StringComparison.OrdinalIgnoreCase)) return false;
        if (mapping.Size != 0 && mapping.Size != size) return false;

        if (_verifiedThisRun.Contains(mapping.FileUploadId)) return true;

        var status = await _client.GetFileUploadStatusAsync(mapping.FileUploadId);
        if (string.IsNullOrWhiteSpace(status))
        {
            // 查询失败/已不存在：保守起见当作"不可用"，走重传一次的分支。
            return false;
        }

        _verifiedThisRun.Add(mapping.FileUploadId);
        return true;
    }

    /// <summary>新建 File Upload → 发送内容 → 校验 status=uploaded。任一步失败返回 null（只上传一次，不循环重试）。</summary>
    private async Task<string?> UploadOnceAsync(string coverPath, string hash, long size, string contentType)
    {
        var fileName = $"cover-{Short(hash)}{CoverFileProbe.ExtensionFor(contentType)}";

        var fileUploadId = await _client.CreateFileUploadAsync(fileName, contentType);
        if (string.IsNullOrWhiteSpace(fileUploadId))
        {
            AppLog.Warn("[DailyIcon] 创建 File Upload 失败，本次不设图标（下次同步会重试）");
            return null;
        }

        var uploaded = await _client.SendFileUploadAsync(fileUploadId!, coverPath, contentType);
        if (!uploaded)
        {
            // 创建成功但 /send 失败：**不写映射**（下次重来一遍），也不阻塞调用方。
            AppLog.Warn($"[DailyIcon] /send 未成功（id={Short(fileUploadId!)}），不写映射，等待下次同步重试");
            return null;
        }

        return fileUploadId;
    }

    private async Task SaveAsync(string key, NotionIconMapping mapping)
    {
        try
        {
            await _store.SetSettingAsync(key, mapping.ToJson());
        }
        catch (Exception ex)
        {
            // 写 settings 失败不影响本次使用（id 已在内存里可用）；下轮会重新上传，属于可接受的降级。
            AppLog.Warn($"[DailyIcon] 写入映射失败（本次仍可用，下轮会重传）: {ex.Message}");
        }
    }

    private async Task SafeDeleteAsync(string key)
    {
        try
        {
            await _store.DeleteSettingAsync(key);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[DailyIcon] 删除失效映射失败: {ex.Message}");
        }
    }

    private static string Short(string s) => s.Length <= 12 ? s : s[..12] + "…";
}
