namespace GameTimeTracker.Core.Models;

/// <summary>
/// 删除一条历史游玩记录的结果（用于审计、界面提示与测试断言）。
///
/// <para>
/// 放在 Core 是因为它要出现在 <c>IDatabaseRepository</c> 的签名里（与 <c>GameDeletionImpact</c> 同层）。
/// </para>
/// </summary>
public sealed class PlayRecordDeletionResult
{
    public bool Found { get; init; }
    public string Date { get; init; } = "";
    public string GameName { get; init; } = "";
    public string GameGlobalId { get; init; } = "";

    /// <summary>
    /// 被作废的会话分片条数（**按会话计**：凡是在该会计日有时长分片的已结束会话都算一条，
    /// 含从上一日延续到本日、以及从本日延续到下一日的会话）。
    /// </summary>
    public int SessionsExcluded { get; init; }

    /// <summary>新写入的排除事实条数（<c>daily_play_exclusions</c>；重复删除时为 0）。</summary>
    public int ExclusionsWritten { get; init; }

    /// <summary>入队的排除事实变更条数（确定性 queue_id ⇒ 幂等）。</summary>
    public int ExclusionsQueued { get; init; }

    /// <summary>被撤回的该日外部声明条数（<c>historical_residual</c> 受保护，不计入）。</summary>
    public int DeclarationsRevoked { get; init; }

    /// <summary>本次删除后重算的会计日个数（目标日 + 被删会话分片所在的其它日）。</summary>
    public int AffectedDates { get; init; }

    /// <summary>删除后该 (game, date) 仍剩余的派生秒数与场次（来自剩余事实 / 受保护残差 / 进行中的会话）。</summary>
    public int RemainingSeconds { get; init; }
    public int RemainingSessions { get; init; }

    /// <summary>派生行是否被删除（没有剩余事实时）。</summary>
    public bool DailyRowRemoved { get; init; }

    /// <summary>该日时长是否**已完全退出统计**（<c>RemainingSeconds == 0</c>）。</summary>
    public bool FullyDeleted { get; init; }

    /// <summary>因"进行中、尚未定型"而被跳过的会话条数（非零 ⇒ 只做到部分删除）。</summary>
    public int ActiveSessionsSkipped { get; init; }

    public string Note { get; init; } = "";
}
