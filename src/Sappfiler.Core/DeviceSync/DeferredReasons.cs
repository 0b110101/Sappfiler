namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 远端变更"本轮没有落地"的原因码（写入 <c>sync_deferred_changes.reason</c>）。
///
/// 为什么要有原因码而不是一句中文：这些记录**要被后续阶段重新处理**，
/// 需要按原因分类（例如"依赖缺失"下次可能就好了，"业务键冲突"必须走身份对账）。
/// 用常量而不是散落的字符串，避免以后拼错导致分类失效。
///
/// ⚠️ **Deferred ≠ Completed**。它只表示"游标已经越过这条，但本地没有应用"。
/// 绝不能因为游标前进了就把它当成同步成功 —— 那是永久静默丢数据。
/// </summary>
public static class DeferredReasons
{
    /// <summary>本地已经有同一个 global_id 的实体 → 不覆盖（本阶段不做通用 LWW）。</summary>
    public const string AlreadyExistsLocal = "already_exists_local";

    /// <summary>
    /// 业务键（platform + platform_id）已被**另一个** global_id 占用。
    /// 这是**跨设备 Game identity 对账**问题，不是覆盖能解决的 → 归 <b>2e：Identity / Conflict Resolution</b>。
    /// </summary>
    public const string BusinessKeyTakenByOtherIdentity = "business_key_taken_by_other_identity";

    /// <summary>依赖缺失（例如 Session 指向的游戏还没同步到本地）。</summary>
    public const string DependencyMissing = "dependency_missing";

    /// <summary>本地已有该实体的墓碑 → 墓碑优先，陈旧数据不能把已删除实体复活。</summary>
    public const string TombstonedLocal = "tombstoned_local";

    /// <summary>本阶段不落地的实体类型（例如 GameMapping）。</summary>
    public const string UnsupportedEntityType = "unsupported_entity_type";

    /// <summary>payload 的 schema_version 不认识 → 宁可不同步，也不能按旧结构硬解。</summary>
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";

    /// <summary>payload 不是合法 JSON / 缺少必需字段。</summary>
    public const string InvalidPayload = "invalid_payload";
}
