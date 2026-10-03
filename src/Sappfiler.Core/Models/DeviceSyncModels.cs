using System.Text.Json.Serialization;

namespace GameTimeTracker.Core.Models;

/// <summary>
/// 多设备同步的领域模型（Device Sync Domain）。
///
/// ⚠️ **术语（不要混）**：
///   · <b>Provider</b> = 笔记后端（Notion / Obsidian / 思源）—— 对外展示层，见 <c>SyncModels.cs</c>。
///   · <b>Backend</b>  = 设备同步的存储后端（Cloudflare / WebDAV）—— 跨设备传输层，见本文件。
///   两者完全解耦：同步引擎不认识 Provider，笔记同步也不认识 Backend。
///
/// ⚠️ **跨设备身份一律用 global_id（UUID），绝不能把本地 INTEGER id 放进同步协议。**
///   同一台机器上 `sessions.id = 1827`，另一台也可能是 `1827`，两者毫无关系。
/// </summary>
public static class DeviceSyncConstants
{
    /// <summary>
    /// 同步载荷的 schema 版本。payload 结构任何变化都要 +1；
    /// 服务端遇到不认识的版本必须**拒绝同步并提示客户端升级**，不要静默损坏数据。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// **Backend 协议版本**（2f 冻结为 v1）。
    ///
    /// ⚠️ 与 <see cref="CurrentSchemaVersion"/> 职责**严格分离**，不要混用：
    ///   · 本常量回答"**双方会不会说这个 Backend 协议**"（客户端 ↔ Backend 的通信契约版本）；
    ///   · <see cref="CurrentSchemaVersion"/> 回答"**客户端能不能理解这个 Change Payload**"。
    /// 二者可独立演进：换了传输协议不一定换 payload 结构，反之亦然。
    ///
    /// ⚠️ 它**不是** <c>ISyncBackend</c> 接口上的字段：按 2f 冻结决策，接口形状不变，
    /// 协议版本由**具体 Backend 的 wire format**（HTTP 头 / 请求体）承载，
    /// 不兼容时由该实现自行**拒绝会话**（抛异常），引擎的 Pending / retry / exception 语义保持不变。
    ///
    /// ⚠️ 也不要拿 <c>SessionSyncPayload.client_version</c> 当版本用 —— 那是**客户端软件版本**。
    /// </summary>
    public const int CurrentProtocolVersion = 1;

    /// <summary>本机设备 ID 在 <c>settings</c> 表里的键（权威来源）。</summary>
    public const string SettingKeyDeviceId = "device_id";
}

/// <summary>需要跨设备同步的实体类型。</summary>
public enum SyncEntityType
{
    Session = 1,
    Game = 2,
    GameMapping = 3
}

/// <summary>同步操作。DELETE 走 tombstone，不做物理删除。</summary>
public enum SyncOperation
{
    Create = 1,
    Update = 2,
    Delete = 3
}

/// <summary>Outbox 记录状态。</summary>
public enum SyncQueueStatus
{
    Pending = 0,
    InFlight = 1,
    Completed = 2,
    Failed = 3
}

/// <summary>设备状态。Revoked 后禁止同步，但**不删除**任何历史数据。</summary>
public enum SyncDeviceStatus
{
    Active = 0,
    Inactive = 1,
    Revoked = 2
}

/// <summary>
/// 时间精度标记。历史数据是由"本地时间字符串"近似换算出来的，
/// **不能假装它精确**（既有实现只存墙上时间，时区信息从未保存过）。
/// </summary>
public static class TimePrecision
{
    /// <summary>由本地时区近似换算而来：迁移之前就存在的历史数据。</summary>
    public const string Estimated = "estimated";

    /// <summary>写入时就是严格 UTC：迁移之后产生的新数据。</summary>
    public const string Exact = "exact";
}

/// <summary>一台设备（本机或远端）。device_id 一经生成永不改变，改名只改 device_name。</summary>
public sealed class DeviceRecord
{
    public string DeviceId { get; set; } = "";

    /// <summary>显示名，用户可改。设备身份永远看 device_id，不看名字。</summary>
    public string DeviceName { get; set; } = "Windows Computer";

    public string Platform { get; set; } = "windows";
    public string AppVersion { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string? LastSeenAtUtc { get; set; }
    public string? LastSyncAtUtc { get; set; }
    public SyncDeviceStatus Status { get; set; } = SyncDeviceStatus.Active;

    /// <summary>本机标记。**只在本机数据库里为 true，不上传**（远端设备由各自判定）。</summary>
    public bool IsThisDevice { get; set; }
}

/// <summary>
/// Outbox 记录（还没成功送出去的变更）。
///
/// **保存 payload 快照**（用户明确要求）：队列存的是"变更发生那一刻的实体状态"，
/// 而不是"等推送时再去业务表读一次"。否则 10:00 写下的变更到 10:05 才推送时
/// 会被读成 10:01 之后的新状态 —— 中间发生过的状态在同步层彻底消失。
///
/// 同时它**不是历史表**：收到服务器 ACK 后置 <see cref="SyncQueueStatus.Completed"/>，
/// 由定期清理删除（真正的跨设备历史保存在云端 change feed）。
/// </summary>
public sealed class SyncQueueItem
{
    public string QueueId { get; set; } = "";

    /// <summary>实体类型名（可读，便于排查）。</summary>
    public string EntityType { get; set; } = "";

    /// <summary>⚠️ 一律是 <b>global_id</b>（跨设备身份），绝不是本地 INTEGER id。</summary>
    public string EntityGlobalId { get; set; } = "";

    public SyncOperation Operation { get; set; }

    /// <summary>业务实体版本号，供服务端做冲突判定（**不要用本地时间判冲突**）。</summary>
    public long BaseVersion { get; set; }

    /// <summary>JSON 快照，内含 <c>schema_version</c>。</summary>
    public string Payload { get; set; } = "";

    public string CreatedAtUtc { get; set; } = "";
    public int RetryCount { get; set; }
    public string? NextRetryAtUtc { get; set; }
    public SyncQueueStatus Status { get; set; } = SyncQueueStatus.Pending;
    public string? LastError { get; set; }
}

/// <summary>
/// 删除墓碑：告诉其它设备"这个同步实体已被删除"。
/// 与 <c>deleted_archive</c>（本机删除审计）**职责不同**，两张表都要写，但语义独立。
/// 墓碑不能立即物理清除：长期离线的设备上线后若看不到墓碑，
/// 会把这条数据当成"新数据"重新上传。第一版至少保留 30 天。
/// </summary>
public sealed class SyncTombstone
{
    public string TombstoneId { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityGlobalId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeletedAtUtc { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}

/// <summary>
/// **Identity Supersession**：表达"这个身份**等同于**那个身份"，用于跨设备身份合并。
///
/// ⚠️ 与 <see cref="SyncTombstone"/>（= 这个实体被**删除**了）**严格区分，不得混用**：
/// <code>
/// 真删除     → sync_tombstones              （对端收到后删除本地副本）
/// 身份合并   → sync_identity_supersessions  （对端收到后把引用重定向到 canonical）
/// </code>
/// 用墓碑表达身份合并，会让对端把"合并"误解成"删除"从而丢数据
/// （用户 2026-10-03 裁定，"这两个概念以后不得再混用"）。
/// </summary>
public sealed class SyncIdentitySupersession
{
    public string SupersessionId { get; set; } = "";

    public string EntityType { get; set; } = "";

    /// <summary>被并入的 identity（例如远端来的 BBB）。</summary>
    public string SupersededGlobalId { get; set; } = "";

    /// <summary>保留的权威 identity（例如本机的 AAA）。</summary>
    public string CanonicalGlobalId { get; set; } = "";

    /// <summary>做出该判断的设备。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>判定依据，见 <see cref="GameTimeTracker.Core.DeviceSync.SupersessionReasons"/>。</summary>
    public string Reason { get; set; } = "";

    public string CreatedAtUtc { get; set; } = "";
}

/// <summary>
/// <b>Deferred 台账</b>：远端变更"本轮没有落地、但同步游标已经越过它"时留痕。
///
/// ⚠️ 为什么必须有这张表（用户 2026-10-03 冻结）：
/// <c>Deferred ≠ Completed</c>。游标前进只代表"服务端那一批我已经消费过了"，
/// **不代表这条变更在本机成功同步**。若只计数不落库，这条数据就悄无声息地丢了 ——
/// 后续冲突/修复阶段再也找不到它。
///
/// 所以这里保留足够信息让**后续阶段能重新处理**：
/// change_id（幂等键）/ entity_type / entity_global_id / reason / schema_version /
/// 首次发现时间 / 最近一次发现时间（+ 发现次数，可判断是不是反复失败）。
///
/// 重新处理机制本身属于 2e（Identity / Conflict Resolution），本阶段只负责**留痕**，
/// 不在这里偷偷塞任何自动修复策略。
/// </summary>
public sealed class DeferredSyncChange
{
    public long Id { get; set; }

    /// <summary>远端变更 ID（服务端的幂等键）。重复发现时按它去重合并。</summary>
    public string ChangeId { get; set; } = "";

    /// <summary>来自哪个 Backend（多后端时用来区分）。</summary>
    public string Backend { get; set; } = "";

    public string EntityType { get; set; } = "";

    /// <summary>⚠️ global_id（跨设备身份），不是本地 INTEGER id。</summary>
    public string EntityGlobalId { get; set; } = "";

    /// <summary>原因码，见 <see cref="GameTimeTracker.Core.DeviceSync.DeferredReasons"/>。</summary>
    public string Reason { get; set; } = "";

    public int SchemaVersion { get; set; }

    public string FirstSeenAtUtc { get; set; } = "";

    public string LastSeenAtUtc { get; set; } = "";

    /// <summary>被反复发现了几次（值大说明长期无法落地，多半需要人工介入）。</summary>
    public int SeenCount { get; set; }

    /// <summary>这条远端变更的操作（Create/Update/Delete）。</summary>
    public SyncOperation Operation { get; set; } = SyncOperation.Create;

    /// <summary>**远端**设备 id（发起这条变更的那台设备）—— 不是本机。</summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// 当次 Pull 收到的**原始 payload 快照**（migration 003 新增）。
    ///
    /// ⚠️ 这是 2e 重处理的**唯一输入**：被延后的远端对象从未落地到本地库，
    /// 没有它就无法在离线状态下判断它到底是不是本地某个游戏。
    /// 重处理时**不得**重新去远端拉，也**不得**按本地当前状态重新构造。
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>
    /// 处理完成时间。**NULL = 仍待处理**；非 NULL 表示已经过 Identity Reconciliation 并有明确结论。
    /// ⚠️ **Resolved ≠ Completed**：审计记录必须保留。
    /// </summary>
    public string? ResolvedAtUtc { get; set; }

    /// <summary>处理结论（规范化取值），见方案文档附录 I.8。</summary>
    public string? Resolution { get; set; }
}

/// <summary>
/// 每个 Backend 独立维护的同步游标。
/// 切换后端时**绝不复用游标**（Cloudflare 的 cursor 与 WebDAV 的 cursor 毫无关系）。
/// </summary>
public sealed class SyncStateRecord
{
    public string Backend { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Cursor { get; set; } = "";
    public string? LastSyncAtUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// 同步载荷信封。所有出网载荷都走它，保证
/// <c>schema_version</c> / <c>client_version</c> / <c>device_id</c> 一定存在。
/// </summary>
public sealed class SyncEnvelope
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = DeviceSyncConstants.CurrentSchemaVersion;

    [JsonPropertyName("client_version")]
    public string ClientVersion { get; set; } = "";

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("entity_type")]
    public string EntityType { get; set; } = "";

    [JsonPropertyName("entity_global_id")]
    public string EntityGlobalId { get; set; } = "";

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";

    [JsonPropertyName("base_version")]
    public long BaseVersion { get; set; }

    /// <summary>实体快照。序列化后即 <c>payload</c> 字段。</summary>
    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}
