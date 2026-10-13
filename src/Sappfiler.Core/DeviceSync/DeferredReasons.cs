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

    /// <summary>
    /// **删除冲突**（E1，2026-10-07 用户批准）：这条远端 <c>Session</c> 指向的 <c>Game</c> 已被**删除**
    /// （本地存在该 Game 的墓碑），而来源设备在离线期间仍然产生了新的会话事实。
    ///
    /// <para>
    /// 语义：删除事实与已发生的会话事实 <b>都保留</b>，同步层不替用户裁决"删除还是恢复"。
    /// 因此它**不能**归入 <see cref="DependencyMissing"/> —— 那表示"依赖还没到"，
    /// 会被后续 Pull 反复重试、并可能把已删除的 Game 静默复活 ✗；也不能丢弃或改写这条 Session ✗。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 仅作为**客户端侧**原因码（写入本地 <c>sync_deferred_changes.reason</c> 与 PullSummary 日志），
    /// 不上 wire、不进 <c>ISyncBackend</c>、不改协议版本。恢复已删除 Game 必须走用户显式操作。
    /// </para>
    /// </summary>
    public const string DeletionConflict = "deletion_conflict";

    /// <summary>
    /// 删除/旧记录的目标身份**无法确认**，但存在"同平台同名"等**弱线索**。
    ///
    /// <para>
    /// ⚠️ 与 <see cref="DeletionConflict"/> 严格区分：后者是"身份明确命中了已删除实体"（事实），
    /// 前者只是"可能是同一款游戏，但身份关系尚未建立"（怀疑）。弱线索**只能发现冲突**，
    /// 不能作为归档、覆盖身份或固化删除标记的依据 ⇒ 一律保留现状 + 留痕等人工裁定。
    /// </para>
    /// </summary>
    public const string PossibleDeletedGameIdentityConflict = "possible_deleted_game_identity_conflict";

    /// <summary>本阶段不落地的实体类型（例如 GameMapping）。</summary>
    public const string UnsupportedEntityType = "unsupported_entity_type";

    /// <summary>payload 的 schema_version 不认识 → 宁可不同步，也不能按旧结构硬解。</summary>
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";

    /// <summary>payload 不是合法 JSON / 缺少必需字段。</summary>
    public const string InvalidPayload = "invalid_payload";
}
