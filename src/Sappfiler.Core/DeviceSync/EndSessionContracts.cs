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
