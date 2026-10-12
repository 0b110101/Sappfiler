namespace GameTimeTracker.Core.DeviceSync;

/// <summary>哈希工具（确定性 change_id / 端点哈希共用同一实现，避免两处漂移）。</summary>
public static class SyncHashing
{
    public static string Sha256Hex(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// 播种用的**确定性 change_id / queue_id**：
    /// <code>sha256("backfill|Game|&lt;global_id&gt;")[..32]</code>
    /// 即便重复播种，也生成同一个 queue_id → <c>INSERT … ON CONFLICT DO NOTHING</c> → 端到端幂等。
    /// </summary>
    public static string BackfillQueueId(string entityType, string globalId)
        => Sha256Hex($"backfill|{entityType}|{globalId}")[..32];
}