namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 设备同步的策略常量与纯计算规则（**无 IO、可单测**）。
///
/// 这些数字来自冻结规格，集中放在 Core 是为了让它们成为**明确的业务规则**，
/// 而不是散落在同步实现各处的魔术数字：
///   · 重试退避 5s → 15s → 30s → 1m → 5m → 15m → 30m（到顶后保持 30 分钟，绝不每秒疯狂请求）
///   · Push 批大小上限（避免"1 条变更 = 1 个 HTTP 请求"）
///   · Outbox 的 completed 记录保留 7 天后清理（Outbox 是"还没送出去的东西"，不是历史表）
///   · 墓碑至少保留 30 天（提前清除会让长期离线设备把"已删除"重新当成"新数据"上传）
/// </summary>
public static class SyncOutboxPolicy
{
    /// <summary>一次 Push 最多上传多少条变更。</summary>
    public const int PushBatchSize = 50;

    /// <summary>一次 Pull 最多拉取多少条变更。</summary>
    public const int PullBatchSize = 200;

    /// <summary>
    /// 一次 Deferred 重处理最多扫多少条（2e 步骤 7）。
    ///
    /// ⚠️ 这是**单遍上限**，不是"循环到清空"：本批结束后仍 unresolved 的条目留在台账里，
    /// 等下一次调用（启动时）再试。绝不能在这里自旋成一个隐性死循环。
    /// </summary>
    public const int ReconcileBatchSize = 200;

    /// <summary>已完成（收到服务器 ACK）的 Outbox 记录保留时长，过期即清理。</summary>
    public static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(7);

    /// <summary>墓碑最短保留时长。</summary>
    public static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(30);

    /// <summary>重试退避档位（秒）；最后一项为封顶值。</summary>
    private static readonly int[] BackoffSeconds = { 5, 15, 30, 60, 300, 900, 1800 };

    /// <summary>
    /// 第 <paramref name="retryCount"/> 次失败后应等待多久再重试（retryCount 从 0 开始）。
    /// 超出档位后固定为最后一档（30 分钟）。
    /// </summary>
    public static TimeSpan NextRetryDelay(int retryCount)
    {
        if (retryCount < 0) retryCount = 0;
        var index = Math.Min(retryCount, BackoffSeconds.Length - 1);
        return TimeSpan.FromSeconds(BackoffSeconds[index]);
    }
}
