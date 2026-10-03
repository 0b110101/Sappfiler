using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;

namespace GameTimeTracker.Infrastructure.DeviceSync;

/// <summary>单轮同步的结果（用于日志、UI 提示与测试断言）。</summary>
public sealed record DeviceSyncRunResult(
    int Completed,
    int PushFailed,
    int PushRejected,
    int Pulled,
    int Applied,
    int Deferred,
    int TombstonesPropagated,
    bool CursorAdvanced,
    string? Error)
{
    /// <summary>本轮无任何错误、无落空。</summary>
    public bool IsClean => Error is null && PushFailed == 0 && PushRejected == 0 && Deferred == 0;

    public static DeviceSyncRunResult Cancelled { get; } =
        new(0, 0, 0, 0, 0, 0, 0, false, "已取消");
}

/// <summary>
/// 设备同步引擎：本地 Outbox ↔ <see cref="ISyncBackend"/>。
///
/// 单轮循环（**当前默认策略，不是写死的协议约束** —— 将来要改成"先拉后推"只需改这里）：
/// <code>
/// Push 本地待发（Pending → InFlight → ACK → Completed）
///   ↓
/// Pull 远端变更（从本 Backend 自己的 cursor 之后）
///   ↓
/// 落地（数据 + 游标在**同一个本地事务**里提交）
/// </code>
///
/// ⚠️ **本类绝不向外抛异常。**
/// Sappfiler 首先是一个可靠的**本地游戏时长记录器**，同步是附加能力：
/// 后端挂了、断网了、payload 坏了，都不允许影响游戏监听 / 5 秒心跳。
/// 所有失败都被捕获、记日志，并体现在返回值的 <c>Error</c> 上。
///
/// ⚠️ **本阶段不实现"通用 LWW"**：Game / Session / GameMapping / Tombstone 的冲突语义不同，
/// 见 <see cref="SqliteRepository.ApplyRemoteChangesWithCursorAsync"/> 的说明。
/// </summary>
public sealed class DeviceSyncEngine
{
    private readonly SqliteRepository _repo;
    private readonly ISyncBackend _backend;
    private readonly string _accountId;

    /// <summary>防止并发轮次（启动、游戏退出、手动点同步、窗口获得焦点都可能触发）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DeviceSyncEngine(SqliteRepository repo, ISyncBackend backend, string accountId = "default")
    {
        _repo = repo;
        _backend = backend;
        _accountId = accountId;
    }

    /// <summary>后端标识（日志用）。</summary>
    public string BackendId => _backend.BackendId;

    /// <summary>
    /// 进程启动时调用一次：把上次崩溃时停在 InFlight 的变更复位、清理过期记录与墓碑。
    /// 推送以 <c>change_id</c> 为幂等键，所以"复位后重推"是安全的。
    /// 同样**不抛异常**。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var reset = await _repo.ResetInFlightOutboxAsync();
                if (reset > 0)
                {
                    AppLog.Info($"[设备同步] 已把 {reset} 条上次未完成的推送复位为待发");
                }

                await _repo.PruneCompletedOutboxAsync();
                await _repo.PruneTombstonesAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 启动整理失败（不影响同步本体）：{ex.Message}");
        }
    }

    /// <summary>
    /// 跑一轮同步。**永不抛异常**。
    /// </summary>
    public async Task<DeviceSyncRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            // 已经有一轮在跑：直接返回，不排队、不并发。
            return new DeviceSyncRunResult(0, 0, 0, 0, 0, 0, 0, false, null);
        }

        try
        {
            if (cancellationToken.IsCancellationRequested) return DeviceSyncRunResult.Cancelled;

            var push = await PushAsync(cancellationToken);
            var pull = await PullAsync(cancellationToken);

            var result = new DeviceSyncRunResult(
                push.Completed, push.Failed, push.Rejected,
                pull.Pulled, pull.Applied, pull.Deferred, pull.Tombstones,
                pull.CursorAdvanced,
                push.Error ?? pull.Error);

            if (result.Error is not null)
            {
                AppLog.Warn($"[设备同步] 本轮未完全成功：{result.Error}（已完成 {result.Completed}，待重试 {result.PushFailed}）");
            }
            else if (result.Completed > 0 || result.Applied > 0 || result.TombstonesPropagated > 0)
            {
                AppLog.Info($"[设备同步] 本轮完成：推送 {result.Completed}、落地 {result.Applied}、墓碑 {result.TombstonesPropagated}"
                    + (result.Deferred > 0 ? $"、延后 {result.Deferred}" : string.Empty));
            }

            return result;
        }
        catch (Exception ex)
        {
            // 兜底：连 Push/Pull 之外的意外也不允许冒泡到调用方（可能是游戏监听相关的调用链）。
            AppLog.Error("[设备同步] 同步轮次发生未预期异常（已吞掉，本地计时不受影响）", ex);
            return new DeviceSyncRunResult(0, 0, 0, 0, 0, 0, 0, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================ Push ============================

    private sealed record PushSummary(int Completed, int Failed, int Rejected, string? Error);

    private async Task<PushSummary> PushAsync(CancellationToken cancellationToken)
    {
        int completed = 0, failed = 0, rejected = 0;

        try
        {
            var pending = await _repo.GetPendingOutboxAsync(SyncOutboxPolicy.PushBatchSize);
            if (pending.Count == 0) return new PushSummary(0, 0, 0, null);

            var deviceId = await _repo.GetLocalDeviceIdAsync();
            var ids = pending.Select(p => p.QueueId).ToList();
            var idSet = new HashSet<string>(ids, StringComparer.Ordinal);

            // Pending → InFlight：这样并发轮次与"崩溃后复位"都能正确区分。
            await _repo.MarkOutboxInFlightAsync(ids);

            var changes = pending.Select(p => ToChange(p, deviceId)).ToList();

            try
            {
                var outcome = await _backend.PushAsync(changes, cancellationToken);

                // 只认"确实属于本批"的 id，防止后端返回脏数据把别的记录标记成已完成。
                var accepted = outcome.AcceptedChangeIds
                    .Where(idSet.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                completed = await _repo.MarkOutboxCompletedAsync(accepted);

                var acceptedSet = new HashSet<string>(accepted, StringComparer.Ordinal);
                var rejectedSet = new HashSet<string>(StringComparer.Ordinal);

                foreach (var rejection in outcome.Rejected.Where(r => idSet.Contains(r.ChangeId)))
                {
                    rejectedSet.Add(rejection.ChangeId);

                    if (rejection.Permanent)
                    {
                        // 重试也没有意义（例如服务端已有该实体的墓碑）→ 死信，不再无限重试。
                        await _repo.MarkOutboxFailedAsync(rejection.ChangeId, rejection.Reason);
                    }
                    else
                    {
                        var item = pending.First(p => p.QueueId == rejection.ChangeId);
                        await _repo.RecordOutboxFailureAsync(
                            rejection.ChangeId, rejection.Reason, SyncOutboxPolicy.NextRetryDelay(item.RetryCount));
                    }
                }

                rejected = rejectedSet.Count;

                // 既没被接受也没被拒绝的（部分响应 / ACK 丢失的等价情形）
                // → **一律按失败退避重试**。绝不能当成"已完成"，否则这条变更永远发不出去。
                foreach (var item in pending.Where(p => !acceptedSet.Contains(p.QueueId) && !rejectedSet.Contains(p.QueueId)))
                {
                    await _repo.RecordOutboxFailureAsync(
                        item.QueueId, "后端未确认该条变更（按失败处理，将重试）",
                        SyncOutboxPolicy.NextRetryDelay(item.RetryCount));
                    failed++;
                }

                return new PushSummary(completed, failed, rejected, null);
            }
            catch (Exception ex)
            {
                // 网络失败 / 超时 / 后端 5xx：**记录不丢**，全部退回 Pending 并按退避重试。
                var error = DescribeFailure(ex);
                foreach (var item in pending)
                {
                    await _repo.RecordOutboxFailureAsync(
                        item.QueueId, error, SyncOutboxPolicy.NextRetryDelay(item.RetryCount));
                    failed++;
                }

                return new PushSummary(completed, failed, rejected, error);
            }
        }
        catch (Exception ex)
        {
            // 连"取待发列表"都失败了（数据库忙等）：同样不允许冒泡。
            var error = DescribeFailure(ex);
            AppLog.Warn($"[设备同步] 推送前置步骤失败：{error}");
            return new PushSummary(completed, failed, rejected, error);
        }
    }

    // ============================ Pull ============================

    private sealed record PullSummary(int Pulled, int Applied, int Deferred, int Tombstones, bool CursorAdvanced, string? Error);

    private async Task<PullSummary> PullAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 游标是**按 Backend 隔离**的：换后端绝不复用（Cloudflare 的 cursor 与 WebDAV 无关）。
            var state = await _repo.GetSyncStateAsync(_backend.BackendId, _accountId);

            var outcome = await _backend.PullAsync(state?.Cursor, SyncOutboxPolicy.PullBatchSize, cancellationToken);

            if (outcome.Changes.Count == 0 && string.IsNullOrEmpty(outcome.NextCursor))
            {
                return new PullSummary(0, 0, 0, 0, false, null);
            }

            var deviceId = await _repo.GetLocalDeviceIdAsync();

            // 落地 + 推进游标：**同一个本地事务**。
            // 若拆成"先推游标再写数据"，崩溃就会留下"云端已消费、本地没写"的永久丢数据；
            // 现在崩溃只会导致下次重拉同一批，而落地是按 global_id 幂等的。
            var apply = await _repo.ApplyRemoteChangesWithCursorAsync(
                _backend.BackendId, _accountId, outcome.Changes, outcome.NextCursor, deviceId, cancellationToken);

            if (apply.Deferred > 0)
            {
                AppLog.Warn($"[设备同步] 本轮有 {apply.Deferred} 条远端变更未落地"
                    + "（本地已有同 identity / 业务键已被占用需身份对账 / 依赖的游戏还没到 / schema 不认识）"
                    + " —— 未静默丢弃，留给冲突解决与身份对账阶段");
            }

            if (apply.TombstonesPropagated > 0)
            {
                AppLog.Info($"[设备同步] 已传播 {apply.TombstonesPropagated} 条远端删除墓碑");
            }

            return new PullSummary(outcome.Changes.Count, apply.Applied, apply.Deferred,
                apply.TombstonesPropagated, true, null);
        }
        catch (Exception ex)
        {
            // 拉取失败：**游标不推进**（异常发生在事务提交之前），下次会重拉同一批。
            var error = DescribeFailure(ex);
            AppLog.Warn($"[设备同步] 拉取失败（游标未推进，下次重拉同一批）：{error}");
            return new PullSummary(0, 0, 0, 0, false, error);
        }
    }

    // ============================ 工具 ============================

    private static SyncChange ToChange(SyncQueueItem item, string deviceId) => new()
    {
        // change_id = queue_id：重试时保持不变 → 服务端可据此幂等去重。
        ChangeId = item.QueueId,
        EntityType = item.EntityType,
        EntityGlobalId = item.EntityGlobalId,
        Operation = item.Operation,
        BaseVersion = item.BaseVersion,
        Payload = item.Payload,
        DeviceId = deviceId,
        CreatedAtUtc = item.CreatedAtUtc
    };

    private static string DescribeFailure(Exception ex)
        => ex is OperationCanceledException
            ? "已取消"
            : $"{ex.GetType().Name}: {ex.Message}";
}
