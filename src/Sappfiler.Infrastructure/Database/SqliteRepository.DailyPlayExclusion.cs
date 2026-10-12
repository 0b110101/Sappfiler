using Dapper;
using GameTimeTracker.Core.DeviceSync;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// <b>每日游玩分片排除事实</b>（2026-10-11）：删除一条 (游戏, 会计日) 历史记录的持久化事实层。
///
/// <para>
/// 为什么需要它（实测代码确认）：<c>sessions</c> 一条会话是**整段**事实，其秒数由
/// <c>GameSessionManager.ComputeDailyDeltas</c> 按结算点拆分到**最多两个会计日**
/// （见 <c>SqliteRepository.DailyAdjustments.SumSessionDerivedSeconds</c>）。
/// 因此"删除某会计日的时长"**不可能**用"删除整条 Session"表达 —— 那会把该会话在另一个
/// 会计日的有效时长一并抹掉（误删），且不会重算那个日期（留下陈旧统计）。
/// </para>
///
/// <para>
/// 本实体以 <c>(game, 会计日, session_global_id)</c> 为粒度作废分片：
/// <list type="bullet">
///   <item>只作废**明确列出**的会话分片 ⇒ 同一会话在别的会计日的分片分毫不动；</item>
///   <item>删除之后**新产生**的会话是新的 <c>global_id</c> ⇒ 永不被作废（新游玩正常统计）；</item>
///   <item>identity 含本机 <c>device_id</c> ⇒ 两台设备各自表达删除意图，并集收敛、互不覆盖；</item>
///   <item>普通不可变事实 + 稳定 identity ⇒ 重复删除 / 重复 Pull / 乱序到达全部幂等。</item>
/// </list>
/// </para>
/// </summary>
public partial class SqliteRepository
{
    /// <summary>
    /// 读出某 (game, 会计日) 上被作废的会话 identity 集合（统计口径必须**跳过**这些分片）。
    ///
    /// ⚠️ 这是"排除"参与统计的**唯一读取入口**：任何"按事实算某日时长/场次"的实现都必须经过它
    /// （删除后重算、启动自愈审计、场次计数），否则被删除的时长会被算回来。
    /// </summary>
    internal static HashSet<string> LoadExcludedSessionGlobalIds(
        SqliteConnection conn, SqliteTransaction? tx, long gameId, string statisticalDate)
    {
        if (string.IsNullOrWhiteSpace(statisticalDate)) return new HashSet<string>(StringComparer.Ordinal);

        var rows = conn.Query<string>(
            """
            SELECT session_global_id FROM daily_play_exclusions
            WHERE game_id = @g AND statistical_date = @d;
            """,
            new { g = gameId, d = statisticalDate }, tx);

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in rows)
        {
            if (!string.IsNullOrWhiteSpace(id)) set.Add(id);
        }

        return set;
    }

    /// <summary>
    /// 批量读出全部排除事实，供"自己循环 ComputeDailyDeltas"的实现（启动自愈审计、场次迁移）
    /// 应用同一份排除集。返回 <c>(game, 会计日) → 被作废的会话 identity 集合</c>。
    /// </summary>
    internal static Dictionary<(long GameId, string Date), HashSet<string>> LoadAllPlayExclusions(
        SqliteConnection conn, SqliteTransaction? tx)
    {
        var rows = conn.Query<PlayExclusionRow>(
            """
            SELECT game_id AS GameId, statistical_date AS Date, session_global_id AS SessionGlobalId
            FROM daily_play_exclusions;
            """, transaction: tx);

        var map = new Dictionary<(long, string), HashSet<string>>();
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.SessionGlobalId) || string.IsNullOrWhiteSpace(r.Date)) continue;

            var key = (r.GameId, r.Date);
            if (!map.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                map[key] = set;
            }

            set.Add(r.SessionGlobalId);
        }

        return map;
    }

    /// <summary>某 (game, 会计日) 的某条会话分片是否在排除集里。</summary>
    internal static bool IsSliceExcluded(
        Dictionary<(long GameId, string Date), HashSet<string>> exclusions,
        long gameId, string date, string sessionGlobalId)
        => !string.IsNullOrEmpty(sessionGlobalId)
           && exclusions.TryGetValue((gameId, date), out var set)
           && set.Contains(sessionGlobalId);

    private sealed class PlayExclusionRow
    {
        public long GameId { get; set; }
        public string Date { get; set; } = "";
        public string SessionGlobalId { get; set; } = "";
    }

    /// <summary>某条会话在某个会计日上的分片是否已被作废。</summary>
    internal static bool IsSessionSliceExcluded(
        SqliteConnection conn, SqliteTransaction? tx, string sessionGlobalId, string statisticalDate)
        => !string.IsNullOrWhiteSpace(sessionGlobalId)
           && conn.ExecuteScalar<int>(
               """
               SELECT COUNT(*) FROM daily_play_exclusions
               WHERE session_global_id = @gid AND statistical_date = @d;
               """,
               new { gid = sessionGlobalId, d = statisticalDate }, tx) > 0;



    private static string Truncate(string value)
        => string.IsNullOrEmpty(value) || value.Length <= 8 ? value : value[..8] + "…";
}
