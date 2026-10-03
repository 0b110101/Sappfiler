using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Tests;

/// <summary>
/// 内存版设备同步后端。**只用于验证协议本身**（幂等 / 游标 / 墓碑优先 / 失败注入），
/// 刻意不联网、不落盘 —— 真实 Cloudflare / WebDAV 属于后续阶段。
///
/// 它兑现 <see cref="ISyncBackend"/> 的四条硬性契约：
///   1. Push 幂等：以 <c>ChangeId</c> 去重，重复推送只确认、不追加第二份数据。
///   2. Pull 有序：change feed 按服务端序号（下标 + 1）严格递增。
///   3. 墓碑优先：已墓碑实体再推 Create/Update 一律 Permanent 拒绝。
///   4. 错误用异常表达（可注入），不返回"成功但什么都没做"。
///
/// ⚠️ 只存在于 `feature/multi-device-sync` 分支（csproj 条件按 DeviceSync 前缀排除）。
/// </summary>
public sealed class MockSyncBackend : ISyncBackend
{
    private readonly List<SyncChange> _log = new();
    private readonly HashSet<string> _seenChangeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tombstones = new(StringComparer.Ordinal);

    public MockSyncBackend(string backendId = "mock")
    {
        BackendId = backendId;
    }

    public string BackendId { get; }

    // ---------------- 观测点（测试断言用） ----------------

    public IReadOnlyList<SyncChange> Log => _log;

    public int PushCallCount { get; private set; }

    public int PullCallCount { get; private set; }

    /// <summary>因幂等被抑制的重复推送次数（ACK 丢失后重推就会命中这里）。</summary>
    public int DuplicateSuppressedCount { get; private set; }

    public List<int> PushBatchSizes { get; } = new();

    public List<int> PullBatchSizes { get; } = new();

    /// <summary>服务端 change feed 里某类实体的**去重后**实体数（用来证明"没有产生重复数据"）。</summary>
    public int DistinctEntityCount(string entityType)
        => _log.Where(c => c.EntityType == entityType && c.Operation != SyncOperation.Delete)
               .Select(c => c.EntityGlobalId)
               .Distinct(StringComparer.Ordinal)
               .Count();

    public bool HasEntity(string entityType, string globalId)
        => _log.Any(c => c.EntityType == entityType && c.EntityGlobalId == globalId
                         && c.Operation != SyncOperation.Delete);

    public bool HasTombstone(string entityType, string globalId)
        => _tombstones.ContainsKey(Key(entityType, globalId));

    // ---------------- 故障注入 ----------------

    /// <summary>下一次 Push 直接抛异常（服务端什么都没做）。</summary>
    public bool ThrowOnNextPush { get; set; }

    /// <summary>下一次 Pull 直接抛异常。</summary>
    public bool ThrowOnNextPull { get; set; }

    /// <summary>
    /// 模拟 **ACK 丢失**：服务端**已经落库**，但响应在回程丢了。
    /// 这是同步协议里最关键的一种失败 —— 客户端必定重推，此时必须靠幂等键保证不产生重复数据。
    /// </summary>
    public bool DropAckOnNextPush { get; set; }

    // ---------------- ISyncBackend ----------------

    public Task<PushOutcome> PushAsync(IReadOnlyList<SyncChange> changes, CancellationToken cancellationToken = default)
    {
        PushCallCount++;
        PushBatchSizes.Add(changes.Count);

        if (ThrowOnNextPush)
        {
            ThrowOnNextPush = false;
            throw new IOException("模拟网络中断（推送：服务端未收到）");
        }

        var outcome = Process(changes);

        if (DropAckOnNextPush)
        {
            DropAckOnNextPush = false;
            throw new IOException("模拟 ACK 丢失（服务端已落库，响应未到达客户端）");
        }

        return Task.FromResult(outcome);
    }

    public Task<PullOutcome> PullAsync(string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        PullCallCount++;
        PullBatchSizes.Add(limit);

        if (ThrowOnNextPull)
        {
            ThrowOnNextPull = false;
            throw new IOException("模拟网络中断（拉取）");
        }

        var start = ParseCursor(cursor);
        var slice = _log.Skip(start).Take(limit).ToList();
        var consumed = start + slice.Count;

        return Task.FromResult(new PullOutcome
        {
            Changes = slice,
            // 游标 = 服务端当前高水位：没有新变更时返回同一个值（不回退、不跳变）。
            NextCursor = consumed.ToString(),
            HasMore = consumed < _log.Count
        });
    }

    // ---------------- 服务端侧逻辑 ----------------

    private PushOutcome Process(IReadOnlyList<SyncChange> changes)
    {
        var accepted = new List<string>();
        var rejected = new List<PushRejection>();

        foreach (var change in changes)
        {
            var key = Key(change.EntityType, change.EntityGlobalId);

            // 墓碑优先：已删除实体不允许被旧设备复活。
            if (change.Operation != SyncOperation.Delete && _tombstones.ContainsKey(key))
            {
                rejected.Add(new PushRejection(change.ChangeId, "实体云端已有墓碑（墓碑优先）", Permanent: true));
                continue;
            }

            // 幂等键：同一条变更重复推送只确认，不追加数据。
            if (!_seenChangeIds.Add(change.ChangeId))
            {
                DuplicateSuppressedCount++;
                accepted.Add(change.ChangeId);
                continue;
            }

            _log.Add(Clone(change));

            if (change.Operation == SyncOperation.Delete)
            {
                _tombstones[key] = change.DeviceId;
            }

            accepted.Add(change.ChangeId);
        }

        return new PushOutcome { AcceptedChangeIds = accepted, Rejected = rejected };
    }

    /// <summary>
    /// 直接往服务端 change feed 里塞一条变更 —— 等价于"另一台设备推送成功"。
    /// </summary>
    public string SeedRemoteChange(
        string entityType,
        string globalId,
        SyncOperation operation,
        string payload,
        string deviceId = "other-device")
    {
        var change = new SyncChange
        {
            ChangeId = "seed-" + Guid.NewGuid().ToString("N"),
            EntityType = entityType,
            EntityGlobalId = globalId,
            Operation = operation,
            BaseVersion = 1,
            Payload = payload,
            DeviceId = deviceId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        };

        _log.Add(change);
        _seenChangeIds.Add(change.ChangeId);

        if (operation == SyncOperation.Delete)
        {
            _tombstones[Key(entityType, globalId)] = deviceId;
        }

        return change.ChangeId;
    }

    private static string Key(string entityType, string globalId) => entityType + "|" + globalId;

    private static int ParseCursor(string? cursor)
        => int.TryParse(cursor, out var value) && value > 0 ? value : 0;

    private static SyncChange Clone(SyncChange source) => new()
    {
        ChangeId = source.ChangeId,
        EntityType = source.EntityType,
        EntityGlobalId = source.EntityGlobalId,
        Operation = source.Operation,
        BaseVersion = source.BaseVersion,
        Payload = source.Payload,
        DeviceId = source.DeviceId,
        CreatedAtUtc = source.CreatedAtUtc
    };
}
