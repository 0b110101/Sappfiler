using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 出网 / 入网的**变更单位**。
///
/// ⚠️ 术语：这里的 Backend 是"设备同步的存储后端"（Cloudflare / WebDAV / 测试用 Mock），
/// 不是笔记 Provider（Notion / Obsidian / 思源）。两者完全解耦。
/// </summary>
public sealed class SyncChange
{
    /// <summary>
    /// 变更 ID = Outbox 的 <c>queue_id</c>。
    /// **重试时必须保持不变** —— 服务端以它为幂等键：同一次变更推两次只应产生一份数据。
    /// （客户端永远无法区分"请求没到"与"响应丢了"，所以幂等键必须由客户端生成并在重试中复用。）
    /// </summary>
    public string ChangeId { get; set; } = "";

    public string EntityType { get; set; } = "";

    /// <summary>一律是 <b>global_id</b>（跨设备身份），绝不是本地 INTEGER id。</summary>
    public string EntityGlobalId { get; set; } = "";

    public SyncOperation Operation { get; set; }

    /// <summary>业务实体版本号，供服务端做冲突判定（**不要用本地时间判冲突**）。</summary>
    public long BaseVersion { get; set; }

    /// <summary>实体快照 JSON，内含 <c>schema_version</c>。</summary>
    public string Payload { get; set; } = "";

    public string DeviceId { get; set; } = "";

    public string CreatedAtUtc { get; set; } = "";
}

/// <summary>服务端对单条变更的拒绝。<see cref="Permanent"/> = true 表示重试也没有意义。</summary>
public sealed record PushRejection(string ChangeId, string Reason, bool Permanent);

/// <summary>Push 结果。**只有出现在 <see cref="AcceptedChangeIds"/> 里的才算落库成功。**</summary>
public sealed class PushOutcome
{
    public List<string> AcceptedChangeIds { get; init; } = new();

    public List<PushRejection> Rejected { get; init; } = new();
}

/// <summary>Pull 结果。游标语义：**只反映已成功返回的变更**，客户端应用成功后才允许推进。</summary>
public sealed class PullOutcome
{
    public List<SyncChange> Changes { get; init; } = new();

    /// <summary>本批之后的新游标；null / 空表示"没有更多"。</summary>
    public string? NextCursor { get; init; }

    public bool HasMore { get; init; }
}

/// <summary>
/// 设备同步的存储后端抽象（Cloudflare / WebDAV / 测试用 Mock）。
///
/// 实现必须满足的**硬性契约**（SyncEngine 依赖它们，测试会逐条钉住）：
///   1. <b>Push 幂等</b>：同一个 <see cref="SyncChange.ChangeId"/> 重复推送不得产生重复数据。
///      ACK 丢失时客户端**一定会重推**，服务端不能因此造出两份。
///   2. <b>Pull 有序</b>：返回的变更按服务端序号严格递增排列，游标单调、不回退。
///   3. <b>墓碑优先</b>：对已墓碑的实体，Push 必须拒绝（<c>Permanent = true</c>）；
///      Pull 侧墓碑要先于其它变更可见，长期离线的设备不能把已删除实体复活。
///   4. 网络 / 服务错误**用异常表达**，由 SyncEngine 决定退避重试 ——
///      不要返回"成功但什么都没做"，那会让客户端把未落库的变更标记为已完成。
///
/// ⚠️ <b>契约边界（2f 冻结）</b>：
///   · <b>Backend 只有三种结果</b>：Push 的 <c>accepted</c> / <c>rejected</c>，或传输层的
///     <b>异常</b>；Pull 只有"变更 + 游标"或异常。
///   · <b>Backend 永不返回 Deferred</b>：Deferred 是**客户端 Apply / Reconciliation 层的状态**
///     （见 <c>sync_deferred_changes</c> 与 DeferredReasons）。服务端**不允许**理解
///     <c>strong_key_conflict</c> / <c>local_ignored_protected</c> / <c>immutable_fact_mismatch</c>
///     这类客户端一致性语义 —— 那会把本机冲突判定泄漏进云端。
///   · <b>接口不含版本字段</b>：Backend 协议版本由具体实现的 wire format 承载
///     （见 <see cref="DeviceSyncConstants.CurrentProtocolVersion"/>）；
///     不兼容时由该实现拒绝会话（抛异常），引擎语义不变。
///   · <b>未知字段可忽略，未知版本不得猜</b>：payload 里多出未来字段应被忽略；
///     版本不认识则必须拒绝（<c>schema_version</c> 的现有语义见 <see cref="SyncPayloads"/>）。
///
/// 通用契约测试见 <c>DeviceSyncBackendContractTests.cs</c>：任何新 Backend 都必须通过它。
/// </summary>
public interface ISyncBackend
{
    /// <summary>后端标识，写入 <c>sync_state.backend</c>。**切换后端绝不复用游标。**</summary>
    string BackendId { get; }

    Task<PushOutcome> PushAsync(
        IReadOnlyList<SyncChange> changes,
        CancellationToken cancellationToken = default);

    Task<PullOutcome> PullAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);
}
