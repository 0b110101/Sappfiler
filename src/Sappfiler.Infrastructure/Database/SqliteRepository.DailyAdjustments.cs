using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// 日时长调整（DailyDurationAdjustment，schema v2）的存储层。
///
/// <para>
/// 语义（2026-10-08 用户批准并冻结）：
/// <code>
/// daily_summary(game, 统计日) = Σ 会话派生秒数 + Σ adjustments(该 game 该日, 全部设备相加)
/// </code>
/// · 同一统计日同一游戏的 adjustment **相加**（A +30min、B +20min ⇒ +50min），**不取 max、不做 LWW**；
/// · adjustment 是**设备产生的历史事实**（identity 稳定 = device + game + 日 + source）；
/// · 它**不改写任何 Session 事实**，也不把 <c>daily_summary</c> 变成跨设备实体。
/// </para>
///
/// <para>
/// ⚠️ 关于"同 identity 再次到达"：对 Session，事实不可变 ⇒ 同 id 不同事实只能 Blocked；
///    adjustment 的语义是"这台设备对这一天追加了多少秒"，因此**同 id 不同值 = 该设备修正了自己的声明**，
///    按差值收敛（并把差值同步到 daily）—— 这样 Σ adjustments 恒等于各设备声明的和，
///    重复 Pull 幂等、修正也能收敛，不会留下永久陈旧的对端数字。
/// </para>
/// </summary>
public partial class SqliteRepository
{
    /// <summary>
    /// <b>唯一重算入口</b>：把 (game, 统计日) 的派生行设为
    /// <c>Σ 会话派生秒数 + Σ 当前有效 Adjustment declarations</c>（§6）。
    ///
    /// <para>
    /// 为什么用"设"而不是"加"：adjustment 是**独立事实**，派生行只是缓存 ——
    /// 用事实重算天然幂等（同一事实重复到达、乱序到达、修正，都得到同一个结果），
    /// 不会因"上一次加了哪一笔"的记账漂移而重复累加。
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item>不动 Session、不改 <c>session_count</c>（保持既有日历日语义）；</item>
    ///   <item>不产生同步事件、不入队、不改事实表；</item>
    ///   <item>无事实且无行 ⇒ 不建空行；有事实无行 ⇒ 建行（<c>&lt;60s</c> 的日子也必须可见）；</item>
    ///   <item>可重复运行，结果稳定。</item>
    /// </list>
    /// </summary>
    /// <param name="preservedResidualSeconds">
    /// **受保护残差**（2026-10-11 审查新增）：调用方在**改动事实之前**量到的
    /// "已有 daily 值 − 当时的事实值"，代表"既无会话也无声明支撑的历史值"（C 类）。
    /// 重算会把这段残差原样带上，避免静默抹掉来源不明的历史时长；撤销/下调仍能精确生效，
    /// 因为被撤掉的那一段属于"算得出来的部分"，不在残差里。默认 0 = 不保护（等于旧行为）。
    /// </param>
    internal static void RecomputeDailySummaryForGameDate(
        SqliteConnection conn, SqliteTransaction? tx, int gameId, string statisticalDate, int cutoffHour,
        int preservedResidualSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(statisticalDate)) return;

        var total = ComputeEffectiveDailySeconds(conn, tx, gameId, statisticalDate, cutoffHour);
        var minutes = total / 60;

        // 🛡️ C 类保护（2026-10-11 审查）：**重算只允许抹掉它算得出来的那部分**。
        //    库里存在"既无会话、也无声明支撑"的历史值（例如 Notion 直接写入 daily、从未落成声明的行）。
        //    若在这里静默按事实覆盖，这些值会被无声抹掉 —— 那是不可逆的数据语义变更。
        //    因此：把"已有值 − 本次改动前的事实值"当作**受保护残差**保留下来（并留日志待人工裁定）。
        //    ⚠️ 残差是在调用方**改动事实之前**算出来的（调用方通过 preservedResidualSeconds 传入）；
        //       于是撤销/下调仍能精确生效（被撤掉的那一段属于"算得出来的部分"，不在残差里）。
        if (preservedResidualSeconds > 0)
        {
            total += preservedResidualSeconds;
            minutes = total / 60;
        }

        if (!DailyRowExists(conn, tx, gameId, statisticalDate))
        {
            if (total <= 0) return;

            conn.Execute(
                """
                INSERT INTO daily_summary (date, game_id, duration_seconds, duration_minutes, session_count, sync_status)
                VALUES (@date, @gameId, @total, @minutes, 0, 'pending');
                """,
                new { date = statisticalDate, gameId, total, minutes },
                tx);
            return;
        }

        conn.Execute(
            """
            UPDATE daily_summary
            SET duration_seconds = @total,
                duration_minutes = @minutes,
                sync_status      = 'pending'
            WHERE date = @date AND game_id = @gameId;
            """,
            new { total, minutes, date = statisticalDate, gameId },
            tx);
    }

    /// <summary>
    /// <b>有效日时长（唯一公式）</b>：
    /// <c>Σ 会话派生秒数 + Σ 当前有效 Adjustment declarations</c>。
    /// </summary>
    internal static int ComputeEffectiveDailySeconds(
        SqliteConnection conn, SqliteTransaction? tx, int gameId, string statisticalDate, int cutoffHour)
        => SumSessionDerivedSeconds(conn, tx, gameId, statisticalDate, cutoffHour)
           + SumAdjustments(conn, tx, gameId, statisticalDate);






    private static bool DailyRowExists(
        SqliteConnection conn, SqliteTransaction? tx, int gameId, string statisticalDate)
        => conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM daily_summary WHERE date = @date AND game_id = @gameId;",
            new { date = statisticalDate, gameId }, tx) > 0;

    /// <summary>某游戏某统计日上**全部设备/来源**的 adjustment 秒数之和。</summary>
    internal static int SumAdjustments(
        SqliteConnection conn, SqliteTransaction? tx, long gameId, string statisticalDate)
        => conn.ExecuteScalar<int>(
            """
            SELECT COALESCE(SUM(duration_seconds), 0) FROM daily_duration_adjustments
            WHERE game_id = @gameId AND statistical_date = @date;
            """,
            new { gameId, date = statisticalDate }, tx);

    // ============================ 读 ============================

    /// <summary>某游戏某统计日上**全部设备**的 adjustment 秒数之和（最终统计口径的一半）。</summary>
    public async Task<int> SumAdjustmentsAsync(int gameId, string statisticalDate)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(SUM(duration_seconds), 0) FROM daily_duration_adjustments
            WHERE game_id = @gameId AND statistical_date = @date;
            """,
            new { gameId, date = statisticalDate });
    }

    /// <summary>读 adjustment 台账（诊断 / 测试）。</summary>
    public async Task<IReadOnlyList<DailyAdjustmentRow>> GetDailyAdjustmentsAsync(string? statisticalDate = null)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<DailyAdjustmentRow>(
            """
            SELECT adjustment_global_id AS AdjustmentGlobalId, game_id AS GameId,
                   statistical_date AS StatisticalDate, device_id AS DeviceId,
                   duration_seconds AS DurationSeconds, source AS Source, created_at_utc AS CreatedAtUtc
            FROM daily_duration_adjustments
            WHERE (@date IS NULL OR statistical_date = @date)
            ORDER BY statistical_date ASC, game_id ASC;
            """,
            new { date = statisticalDate });

        return rows.ToList();
    }

    // ============================ 共用口径 ============================

    /// <summary>
    /// 某游戏在某**会计日**上的"会话派生秒数"。
    ///
    /// ⚠️ 这是跨日拆分的**唯一实现**（与本机心跳/结束路径、远端会话落地共用
    /// <see cref="GameTimeTracker.Core.Services.GameSessionManager.ComputeDailyDeltas"/>）：
    /// 跨结算点的会话会被精确拆到两个日期上 —— 绝不能把整段 duration 记到一个日期。
    /// 被"僵尸清理"提前收尾的会话按 <c>last_heartbeat</c> 截断（与它写进 daily 的口径一致）。
    ///
    /// <para>
    /// ⚠️ 2026-10-11：必须**跳过被作废的会话分片**（<c>daily_play_exclusions</c>）——
    /// 用户删除某会计日记录后，该日上那条会话的分片不再计入；同一会话在**别的**会计日的分片
    /// 不受影响（这是"按日删除"能与"整段会话"共存的关键）。
    /// 凡绕过本方法自己循环 <c>ComputeDailyDeltas</c> 的实现，都必须自行应用同一份排除集。
    /// </para>
    /// </summary>
    internal static int SumSessionDerivedSeconds(
        SqliteConnection conn, SqliteTransaction? tx, long gameId, string statisticalDate, int cutoffHour)
    {
        var excluded = LoadExcludedSessionGlobalIds(conn, tx, gameId, statisticalDate);

        // ⚠️ 只读 canonical 事实：`sessions.duration_seconds` 是秒数唯一来源，
        //    结束时刻由 `start_time + duration_seconds` 推出。
        //    这样本机（心跳/结束）、远端落地、重算三条路径对**同一份 Session facts**
        //    必然得到完全相同的统计日分片（不再依赖 end_time/last_heartbeat 的墙上时间，
        //    那会在远端落地或不一致的历史行上算出 0）。
        var sessions = conn.Query<SessionSpanRow>(
            """
            SELECT start_time AS StartTime, duration_seconds AS DurationSeconds,
                   COALESCE(global_id, '') AS GlobalId
            FROM sessions
            WHERE game_id = @gameId;
            """,
            new { gameId }, tx);

        var total = 0;
        foreach (var s in sessions)
        {
            // 该会话在本会计日的分片已被用户显式删除 ⇒ 不计入（其他日期不受影响）。
            if (excluded.Count > 0 && !string.IsNullOrEmpty(s.GlobalId) && excluded.Contains(s.GlobalId)) continue;

            if (!TryParseWallClock(s.StartTime, out var start)) continue;

            var span = Math.Max(0, s.DurationSeconds);
            if (span <= 0) continue;

            var end = start.AddSeconds(span);

            foreach (var delta in GameTimeTracker.Core.Services.GameSessionManager.ComputeDailyDeltas(
                         start, end, span, cutoffHour))
            {
                if (string.Equals(delta.Date, statisticalDate, StringComparison.Ordinal))
                {
                    total += delta.DurationSeconds;
                }
            }
        }

        return total;
    }

    internal static bool TryParseWallClock(string? text, out DateTime value)
        => DateTime.TryParseExact(
            text, "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out value);

    private sealed class SessionSpanRow
    {
        public string? StartTime { get; set; }
        public int DurationSeconds { get; set; }
        public string GlobalId { get; set; } = "";
    }
}

/// <summary>日时长调整台账行（读模型）。</summary>
public sealed class DailyAdjustmentRow
{
    public string AdjustmentGlobalId { get; set; } = "";
    public long GameId { get; set; }
    public string StatisticalDate { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public int DurationSeconds { get; set; }
    public string Source { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}
