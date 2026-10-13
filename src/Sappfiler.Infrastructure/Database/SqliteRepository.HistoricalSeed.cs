using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>历史候选的置信度（只影响报告与人工复核，不参与合并规则）。</summary>
public enum HistoricalSeedConfidence
{
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>
/// 一条**历史候选**：由调用方（人工/测试）显式给出，绝不自动扫描生成。
///
/// <para>
/// 语义：<see cref="DurationSeconds"/> 是"这条 declaration 的完整声明值"，
/// **不是**"<c>known − current</c> 的差额" ——
/// 例：9/23 Deathloop 声明 7896（整段），而不是 7896 − 7860 = 36。
/// </para>
/// </summary>
public sealed record HistoricalSeedCandidate(
    string GameGlobalId,
    string GameName,
    string StatisticalDate,
    int DurationSeconds,
    string Source,
    HistoricalSeedConfidence Confidence,
    string Reason,
    bool SeedReady);

/// <summary><b>dry-run 计划</b>里的一条（不写库）。</summary>
public sealed class HistoricalAdjustmentSeedPlanItem
{
    public string GameName { get; init; } = "";
    public string GameGlobalId { get; init; } = "";
    public long GameId { get; init; }
    public string StatisticalDate { get; init; } = "";
    public string Source { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public int DurationSeconds { get; init; }
    public string AdjustmentGlobalId { get; init; } = "";
    public string Reason { get; init; } = "";
    public HistoricalSeedConfidence Confidence { get; init; }

    /// <summary>该 identity 在本地是否**已经存在**（存在 ⇒ seed 会跳过，绝不重复累加）。</summary>
    public bool AlreadyExists { get; init; }

    /// <summary>该 identity 当前已存在的秒数（用于核对"修正"而不是"叠加"）。</summary>
    public int ExistingSeconds { get; init; }

    /// <summary>能否安全写库（游戏能解析 + 时长 &gt; 0 + 候选被标为 seed-ready）。</summary>
    public bool Writable { get; init; }

    /// <summary>不建议写库时的原因（不可写时非空）。</summary>
    public string? BlockedReason { get; init; }
}

/// <summary>dry-run 的完整结果。</summary>
public sealed class HistoricalAdjustmentSeedPlan
{
    public IReadOnlyList<HistoricalAdjustmentSeedPlanItem> Items { get; init; } = Array.Empty<HistoricalAdjustmentSeedPlanItem>();
    public int WritableCount => Items.Count(i => i.Writable && !i.AlreadyExists);
    public int AlreadyExistsCount => Items.Count(i => i.AlreadyExists);
    public int BlockedCount => Items.Count(i => !i.Writable);
}

/// <summary>
/// <b>历史 Adjustment 播种（默认关闭）</b>。
///
/// <para>
/// 🚫 本类的方法**没有任何生产调用点**：不在启动流程、不在 migration、不在 DeviceSyncService、
/// 不在 Notion Sync 里被调用。只有测试/显式内部入口才会调它 —— 这一点由
/// <c>HistoricalAdjustmentSeedTests</c> 的"无自动调用点"断言与本文件的显式文档共同保证。
/// </para>
///
/// <para>行为（用户 2026-10-09 冻结）：</para>
/// <list type="bullet">
///   <item><b>只创建 Adjustment</b>：绝不改 <c>daily_summary</c> / Session / Game，绝不删除既有 Adjustment；</item>
///   <item><b>单调且幂等</b>：identity 稳定 + <c>ON CONFLICT DO NOTHING</c> ⇒ 重复播种不增长、不重复累加、不重复入队；</item>
///   <item><b>identity 是 declaration identity</b>（device+game+date+source），不是 additive event identity；</item>
///   <item>可写时按既有 outbox 口径**入队一条 v2 事实**（identity 不变 ⇒ 队列不重复生成）。</item>
/// </list>
/// </summary>
public partial class SqliteRepository
{
    /// <summary>
    /// <b>dry-run</b>：只读地算出"如果播种会发生什么"（含 identity、是否已存在、可否写入）。**不写任何表。**
    /// </summary>
    public async Task<HistoricalAdjustmentSeedPlan> BuildHistoricalAdjustmentSeedPlanAsync(
        IReadOnlyList<HistoricalSeedCandidate> candidates)
    {
        using var conn = CreateConnection();
        var deviceId = ReadLocalDeviceId(conn, null) ?? string.Empty;
        var items = new List<HistoricalAdjustmentSeedPlanItem>(candidates.Count);

        foreach (var c in candidates)
        {
            // 🛡️ 2026-10-11（身份协议兼容核查）：候选里的 game identity 可能是本机某一行的别名
            //    ⇒ 按等价类解析（否则会误判为"本地没有该游戏"而跳过播种）。
            var resolvedGameId = ResolveLocalGameIdByGlobalId(conn, null, c.GameGlobalId);
            var gameId = resolvedGameId is null
                ? null
                : conn.ExecuteScalar<long?>("SELECT id FROM games WHERE id = @id;", new { id = resolvedGameId.Value });

            var globalId = DailyAdjustmentGlobalId.For(
                deviceId, c.GameGlobalId, c.StatisticalDate, c.Source);

            var existing = conn.ExecuteScalar<int?>(
                "SELECT duration_seconds FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
                new { id = globalId });

            string? blocked = null;
            if (gameId is null) blocked = "本库找不到该 game_global_id（不能凭空造 Game）";
            else if (c.DurationSeconds <= 0) blocked = "声明值必须为正（0/负值不播种）";
            else if (!c.SeedReady) blocked = "候选未被标为 seed-ready（provenance 未定）";

            items.Add(new HistoricalAdjustmentSeedPlanItem
            {
                GameName = c.GameName,
                GameGlobalId = c.GameGlobalId,
                GameId = gameId ?? 0,
                StatisticalDate = c.StatisticalDate,
                Source = c.Source,
                DeviceId = deviceId,
                DurationSeconds = c.DurationSeconds,
                AdjustmentGlobalId = globalId,
                Reason = c.Reason,
                Confidence = c.Confidence,
                AlreadyExists = existing is not null,
                ExistingSeconds = existing ?? 0,
                Writable = blocked is null,
                BlockedReason = blocked
            });
        }

        await Task.CompletedTask;
        return new HistoricalAdjustmentSeedPlan { Items = items };
    }

    /// <summary>
    /// <b>真正播种（显式调用才执行）</b>：只为"尚不存在"的 identity 写一条 Adjustment（不动 daily_summary），
    /// 并按既有 outbox 口径入队一条 v2 事实。
    ///
    /// <para>⚠️ 已存在的 identity <b>一律跳过</b> —— 绝不覆盖、绝不相加（即使候选值更大）。</para>
    /// </summary>
    /// <returns>实际新建的 Adjustment 条数。</returns>
    public async Task<int> SeedHistoricalAdjustmentsAsync(
        IReadOnlyList<HistoricalSeedCandidate> candidates, string sourceTag = DailyAdjustmentSources.HistoricalResidual)
    {
        var plan = await BuildHistoricalAdjustmentSeedPlanAsync(candidates);
        var created = 0;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            foreach (var item in plan.Items)
            {
                if (!item.Writable || item.AlreadyExists) continue;   // 幂等：已存在即跳过

                var now = UtcNowIso();

                var inserted = conn.Execute(
                    """
                    INSERT INTO daily_duration_adjustments
                        (adjustment_global_id, game_id, statistical_date, device_id, duration_seconds, source, created_at_utc)
                    VALUES (@globalId, @gameId, @date, @deviceId, @duration, @source, @now)
                    ON CONFLICT(adjustment_global_id) DO NOTHING;
                    """,
                    new
                    {
                        globalId = item.AdjustmentGlobalId,
                        gameId = (int)item.GameId,
                        date = item.StatisticalDate,
                        deviceId = item.DeviceId,
                        duration = item.DurationSeconds,
                        source = item.Source,
                        now
                    },
                    tx);

                if (inserted == 0) continue;   // 并发下别人先写了 → 同样跳过

                // 只为**新事实**入队一次（identity 不变 ⇒ 重复播种不会重复入队）。
                await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
                {
                    QueueId = Guid.NewGuid().ToString("N"),
                    EntityType = nameof(SyncEntityType.DailyAdjustment),
                    EntityGlobalId = item.AdjustmentGlobalId,
                    Operation = SyncOperation.Create,
                    BaseVersion = 1,
                    Payload = SyncPayloadFactory.DailyAdjustment(
                        item.AdjustmentGlobalId, item.GameGlobalId, item.StatisticalDate,
                        item.DeviceId, item.DurationSeconds, item.Source, now),
                    CreatedAtUtc = now,
                    Status = SyncQueueStatus.Pending
                });

                created++;
            }

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }

        return created;
    }
}
