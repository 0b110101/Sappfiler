namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 一次心跳 / 结束增量应当写入的「业务归属日期 + 秒数」。
/// 由 <c>GameSessionManager</c> 的纯函数算出（跨越跨日结算点时会拆成两笔）。
/// </summary>
public sealed record DailyDurationDelta(string Date, int DurationSeconds);

/// <summary>
/// <see cref="GameTimeTracker.Core.Interfaces.IDatabaseRepository.EndSessionWithOutboxAsync"/> 的结果。
/// 只用于日志 / 诊断 / 测试断言，不参与业务判断。
/// </summary>
public sealed record EndSessionOutboxResult(
    bool SessionEnded,
    bool OutboxEnqueued,
    string? SessionGlobalId,
    string? GameGlobalId,
    int DurationSeconds,
    string? EndedAtUtc);

/// <summary>
/// 「历史 Outbox Gap Repair」的结果（只用于日志 / 诊断 / 测试断言，不参与业务判断）。
///
/// <para>
/// 语义边界（用户 2026-10-08 裁定）：<b>只补缺口，不重扫历史</b> ——
/// 已经有对应 outbox 的事实（无论队列行处于 Pending / InFlight / Completed / Failed）
/// 一律**不碰**，因此永远不会重复上传已经同步过的东西。
/// </para>
/// </summary>
public sealed record OutboxGapRepairResult(
    int GamesIdentityAssigned,
    int GamesEnqueued,
    int SessionsEnqueued)
{
    /// <summary>
    /// 这次补了多少条 **DailyAdjustment** outbox（2026-10-10 新增；外部导入的独立时长声明）。
    /// 单独一个属性而不是加进位置参数，是为了不动既有构造点与断言。
    /// </summary>
    public int AdjustmentsEnqueued { get; init; }

    /// <summary>
    /// 这次补了多少条**依赖 Game** outbox（2026-10-11 新增）：补齐 Adjustment 缺口时，
    /// 若其依赖的 Game 从未入队（典型：Notion 手工录入的游戏一条会话都没有，② Game 类目覆盖不到），
    /// 必须先把 Game 补进队列，否则对端永远解析不到 <c>game_global_id</c>（永久 <c>dependency_missing</c>）。
    /// </summary>
    public int DependencyGamesEnqueued { get; init; }

    /// <summary>这次一共补了多少条 outbox（Game + Session + Adjustment + 依赖 Game）。</summary>
    public int Total => GamesEnqueued + SessionsEnqueued + AdjustmentsEnqueued + DependencyGamesEnqueued;

    /// <summary>什么都没补（正常情况：没有缺口）。</summary>
    public static OutboxGapRepairResult Nothing { get; } = new(0, 0, 0);
}
