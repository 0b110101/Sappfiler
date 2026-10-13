using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// <b>删除一条历史游玩记录</b>（2026-10-11 重写：正确粒度 + 单事务 + 部分删除可识别）。
///
/// <para>
/// 产品语义：界面上"一条历史记录" = 某 (游戏, 会计日) 的一条 <c>daily_summary</c> 记录
/// （<c>HistoryPage</c> 列的是 <c>GetRecentDailyRecordsAsync</c> 的每日行，
/// 且 <c>daily_summary</c> 有 <c>UNIQUE(date, game_id)</c> ⇒ 该 (game, 日) 只有一条记录）。
/// 用户删除的是**这一天的时长**，不是"某一段独立 Session"，也不是"禁止这款游戏再产生记录"。
/// </para>
///
/// <para>
/// <b>为什么必须先看清 Session 生命周期</b>（实测代码证据）：
/// <list type="number">
///   <item>一次连续游玩在 <c>sessions</c> 里是**一条整段事实**（<c>GameSessionManager.EndSessionAsync</c>
///         只更新同一条行的 <c>duration_seconds</c>；<c>HeartbeatSessionAsync</c> 也只更新同一行）；
///         跨结算点**不会**拆行。</item>
///   <item>但它的秒数被 <c>GameSessionManager.ComputeDailyDeltas</c> 按结算点**拆分到最多两个会计日**
///         （<c>SqliteRepository.DailyAdjustments.SumSessionDerivedSeconds</c> 是唯一统计实现）。
///         例：cutoff=28（结算点 04:00），会话 03:00→05:30 ⇒ 分片落在 D-1（1h）与 D（1.5h）。</item>
///   <item>因此"按整条 Session 删除"（旧实现）会**误删**另一个会计日的有效时长，
///         并且不会重算那个日期（留下陈旧统计）——见 <c>PlayRecordDeletionTests</c> 的跨日用例。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>本实现的删除链路（**全程一个 SQLite 事务**，不存在"声明已撤、事实未删"的半完成态）</b>：
/// <list type="number">
///   <item><b>选择集</b>：枚举该游戏全部**已结束**会话，用<b>唯一拆分实现</b>算出每条会话的会计日分片；
///         凡是"在目标会计日有时长分片"的会话，都是本次删除的对象（含从上一日延续、以及延续到下一日的会话分片）。</item>
///   <item><b>事实侧</b>：为每个 (game, 会计日, 会话) 写一条**排除事实**
///         （<c>daily_play_exclusions</c>，同步实体 <c>DailyPlayExclusion</c>）。
///         ⚠️ 不删除 <c>sessions</c> 行 —— 同一会话在**别的**会计日的分片必须原样保留。</item>
///   <item><b>声明侧</b>：撤回该 (game, 日) 的全部外部时长声明（<c>source = historical_residual</c> **除外**），
///         每条写墓碑（挡旧声明重放）+ 入队 Delete（跨设备传播）。</item>
///   <item><b>残差测量（必须在改事实之前）</b>：对**每一个受影响会计日**量出
///         "已有 daily 值 − 当时的事实值" = 既无会话也无声明支撑的历史残差；
///         重算时原样带回 ⇒ 删除只抹掉"算得出来的那部分"，绝不误删别日的来源不明时长。</item>
///   <item><b>重算</b>：重算**所有受影响会计日**（目标日 + 被删会话分片所在的其它日），
///         保证"删除目标日"不会让任何日期留下陈旧统计。</item>
///   <item><b>留底</b>：同一事务写 <c>deleted_archive</c>（含是否**完全删除**、剩余残差秒数、被跳过的活跃会话数）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 与"Notion 页面被删 ⇒ 本地对账"路径**刻意不同**：后者是远端信号，冻结语义是
/// "页面被删 ≠ 没玩过"（只撤回外部声明、保留会话事实）。本方法只用于**用户显式删除记录**的入口。
/// </para>
/// </summary>
public partial class SqliteRepository
{
    /// <summary>
    /// 删除某条历史游玩记录（用户显式操作）。**单事务**：声明撤回 / 分片排除 / 墓碑 / 同步入队 /
    /// 全部受影响日期的重算 / 审计留底，要么全做要么全不做。
    ///
    /// <para>幂等：重复调用不会产生重复排除行 / 重复墓碑 / 重复队列项，结果稳定。</para>
    /// </summary>
    public async Task<PlayRecordDeletionResult> DeletePlayRecordAsync(int dailySummaryId)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            // 定位 (game, 会计日) 与删除**同一个事务**：绝不出现"读到 A 行、删到 B 行"。
            var locator = conn.QuerySingleOrDefault<PlayRecordLocator>(
                "SELECT game_id AS GameId, date AS Date FROM daily_summary WHERE id = @id LIMIT 1;",
                new { id = dailySummaryId }, tx);

            if (locator is null)
            {
                tx.Commit();
                return new PlayRecordDeletionResult { Note = "记录不存在（可能已被删除）" };
            }

            var result = await DeletePlayRecordFactsCoreAsync(conn, tx, (int)locator.GameId, locator.Date);

            tx.Commit();
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 事实层删除（调用方已持有写锁与事务；绝不自开连接 / 自开事务 / 再取写锁）。
    /// </summary>
    private async Task<PlayRecordDeletionResult> DeletePlayRecordFactsCoreAsync(
        SqliteConnection conn, SqliteTransaction tx, int gameId, string date)
    {
        var gameRow = await conn.QuerySingleOrDefaultAsync<(long Id, string? Name, string? GlobalId)>(
            "SELECT id AS Id, name AS Name, global_id AS GlobalId FROM games WHERE id = @id;",
            new { id = gameId }, tx);

        if (gameRow.Name is null && gameRow.Id == 0)
        {
            return new PlayRecordDeletionResult { Note = "游戏行不存在 ⇒ 无记录可删" };
        }

        var gameName = gameRow.Name ?? string.Empty;
        var gameGlobalId = (gameRow.GlobalId ?? string.Empty).Trim();
        var cutoff = ReadDailyCutoffHour(conn, tx);
        var localDeviceId = ReadLocalDeviceId(conn, tx);
        var now = UtcNowIso();

        // ---------- ① 选择集：在目标会计日**有时长分片**的、已结束的会话 ----------
        var sessions = (await conn.QueryAsync<SessionRowForDeletion>(
            """
            SELECT id AS Id, COALESCE(global_id, '') AS GlobalId, COALESCE(device_id, '') AS DeviceId,
                   COALESCE(process_name, '') AS ProcessName, start_time AS StartTime, end_time AS EndTime,
                   COALESCE(started_at_utc, '') AS StartedAtUtc, COALESCE(ended_at_utc, '') AS EndedAtUtc,
                   COALESCE(duration_seconds, 0) AS DurationSeconds,
                   COALESCE(is_active, 0) AS IsActive
            FROM sessions WHERE game_id = @g;
            """,
            new { g = gameId }, tx)).ToList();

        var sliceDatesBySession = new List<(SessionRowForDeletion Row, IReadOnlyList<string> SliceDates)>();
        var affectedDates = new SortedSet<string>(StringComparer.Ordinal) { date };
        var activeSessionsSkipped = 0;

        foreach (var s in sessions)
        {
            if (!TryParseWallClock(s.StartTime, out var start)) continue;

            var span = Math.Max(0, s.DurationSeconds);
            if (span <= 0) continue;

            var sliceDates = SliceAccountingDatesOfSession(start, span, cutoff);
            if (sliceDates.Count == 0 || !sliceDates.Contains(date, StringComparer.Ordinal)) continue;

            // 🛡️ 进行中的会话**不定型**（duration 只是"到目前为止"、还会继续增长）：
            //    无法界定"删除前 / 删除后"的分片 ⇒ 保守跳过，并在结果里如实报告"部分删除"。
            if (s.IsActive != 0)
            {
                activeSessionsSkipped++;
                continue;
            }

            sliceDatesBySession.Add((s, sliceDates));
            foreach (var d in sliceDates) affectedDates.Add(d);
        }

        // ---------- ② 残差测量：必须在**改动任何事实之前** ----------
        //   "已有 daily 值 − 当时的事实值" = 既无会话、也无声明支撑的历史值。
        //   ⚠️ 只对**其它受影响会计日**做保护：
        //     · 目标会计日是用户**显式删除**的那一天 ⇒ 该日旧存量（含无事实支撑的脏值）都应清掉，
        //       否则"删除一条记录"会永远删不干净（用户意图明确，这是 C 类保护的例外）；
        //     · 其它会计日用户没有要求动 ⇒ 只精确减掉被作废的分片，别日的来源不明时长原样保留
        //       （否则删除跨日记录会顺手抹掉另一个会计日的历史残差 —— 那是误删）。
        var residualBefore = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in affectedDates)
        {
            residualBefore[d] = string.Equals(d, date, StringComparison.Ordinal)
                ? 0
                : MeasureResidualSeconds(conn, tx, gameId, d, cutoff);
        }

        // ---------- ③ 事实侧：写"每日游玩分片排除事实"（不删 sessions 行） ----------
        var sessionsExcluded = 0;
        var exclusionsWritten = 0;
        var exclusionsQueued = 0;

        foreach (var (row, _) in sliceDatesBySession)
        {
            var sessionGlobalId = row.GlobalId.Trim();
            sessionsExcluded++;

            // 没有跨设备身份的历史会话（极少见；正常库由 migration 001 全量回填了 global_id）
            // ⇒ 无法表达"哪条会话的哪日分片"，本机也不能同步。此时不做排除，
            //    该日会因此保留这部分时长，并由下面的"是否完全删除"判定如实报告（绝不谎报成功）。
            if (sessionGlobalId.Length == 0 || gameGlobalId.Length == 0) continue;

            var exclusionGlobalId = DailyPlayExclusionGlobalId.For(
                localDeviceId, gameGlobalId, date, sessionGlobalId);

            if (!InsertDailyPlayExclusionCore(
                    conn, tx, exclusionGlobalId, gameId, date, sessionGlobalId, localDeviceId, now))
            {
                continue;   // 已存在（重复删除 / 早已删除过）⇒ 幂等，不重复入队
            }

            exclusionsWritten++;

            var queueId = $"play-exclusion:{exclusionGlobalId}";
            var alreadyQueued = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sync_queue WHERE queue_id = @id;", new { id = queueId }, tx) > 0;

            if (!alreadyQueued)
            {
                await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
                {
                    QueueId = queueId,
                    EntityType = nameof(SyncEntityType.DailyPlayExclusion),
                    EntityGlobalId = exclusionGlobalId,
                    Operation = SyncOperation.Create,
                    BaseVersion = 1,
                    Payload = SyncPayloadFactory.DailyPlayExclusion(
                        exclusionGlobalId, gameGlobalId, date, sessionGlobalId, localDeviceId, now),
                    CreatedAtUtc = now,
                    Status = SyncQueueStatus.Pending
                });
                exclusionsQueued++;
            }
        }

        // ---------- ④ 声明侧：撤回该日全部外部声明（受保护的 historical_residual 除外） ----------
        var declarationsRevoked = await RevokeAdjustmentsForRecordDeletionCoreAsync(
            conn, tx, gameId, date, localDeviceId, gameGlobalId, now);

        // ---------- ⑤ 重算**所有受影响会计日**（目标日 + 被删分片所在的其它日） ----------
        var remainingSeconds = 0;
        var remainingCount = 0;
        var dailyRowRemoved = false;

        foreach (var d in affectedDates)
        {
            var (secs, count, removed) = RefreshDailyRowFromRemainingFactsCore(
                conn, tx, gameId, d, cutoff, residualBefore.GetValueOrDefault(d));

            if (string.Equals(d, date, StringComparison.Ordinal))
            {
                remainingSeconds = secs;
                remainingCount = count;
                dailyRowRemoved = removed;
            }
        }

        var fullyDeleted = remainingSeconds <= 0;

        await conn.ExecuteAsync(
            """
            INSERT INTO deleted_archive (kind, source, reference, payload_json)
            VALUES ('play-record', 'app', @reference, @payload);
            """,
            new
            {
                reference = $"{date} {gameName}",
                payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Date = date,
                    GameId = gameId,
                    GameName = gameName,
                    GameGlobalId = gameGlobalId,
                    SessionsExcluded = sessionsExcluded,
                    SessionGlobalIds = sliceDatesBySession
                        .Select(t => t.Row.GlobalId)
                        .Where(g => !string.IsNullOrWhiteSpace(g))
                        .ToList(),
                    ExclusionsWritten = exclusionsWritten,
                    DeclarationsRevoked = declarationsRevoked,
                    AffectedDates = affectedDates.ToList(),
                    ActiveSessionsSkipped = activeSessionsSkipped,
                    RemainingSeconds = remainingSeconds,
                    FullyDeleted = fullyDeleted,
                    DailyRowRemoved = dailyRowRemoved,
                    AtUtc = now
                })
            }, tx);

        AppLog.Info($"[记录删除] {date} {gameName}：作废会话分片 {sessionsExcluded} 条"
            + $"（新增排除 {exclusionsWritten}、入队 {exclusionsQueued}）、撤回声明 {declarationsRevoked} 条、"
            + $"重算日期 {affectedDates.Count} 个、剩余 {remainingSeconds}s"
            + (activeSessionsSkipped > 0 ? $"、跳过进行中的会话 {activeSessionsSkipped} 条" : "")
            + (fullyDeleted ? "（完全删除）" : "（**部分删除**：该日仍有无法归属/进行中的时长）"));

        return new PlayRecordDeletionResult
        {
            Found = true,
            Date = date,
            GameName = gameName,
            GameGlobalId = gameGlobalId,
            SessionsExcluded = sessionsExcluded,
            ExclusionsWritten = exclusionsWritten,
            ExclusionsQueued = exclusionsQueued,
            DeclarationsRevoked = declarationsRevoked,
            AffectedDates = affectedDates.Count,
            RemainingSeconds = remainingSeconds,
            RemainingSessions = remainingCount,
            DailyRowRemoved = dailyRowRemoved,
            FullyDeleted = fullyDeleted,
            ActiveSessionsSkipped = activeSessionsSkipped,
            Note = BuildDeletionNote(sessionsExcluded, exclusionsWritten, declarationsRevoked,
                remainingSeconds, activeSessionsSkipped, fullyDeleted)
        };
    }

    private static string BuildDeletionNote(
        int sessionsExcluded, int exclusionsWritten, int declarationsRevoked,
        int remainingSeconds, int activeSessionsSkipped, bool fullyDeleted)
    {
        if (sessionsExcluded == 0 && declarationsRevoked == 0)
        {
            return remainingSeconds > 0
                ? "部分删除（未生效）：该记录没有可删除的会话/声明事实，"
                  + "剩余时长全部来自无法归属的历史残差（已保留，不会被销毁）"
                : "该记录没有可删除的事实（可能已被删除）";
        }

        if (fullyDeleted)
        {
            return "已删除该会计日的全部可归属时长并重算所有受影响日期";
        }

        var reason = activeSessionsSkipped > 0
            ? $"该日有 {activeSessionsSkipped} 条进行中的会话未计入删除"
            : "该日仍有无法归属的历史残差";

        return $"部分删除：{reason}，剩余 {remainingSeconds} 秒仍参与统计（未删除）";
    }

    /// <summary>
    /// 撤回某 (game, 会计日) 的**全部外部时长声明**（<c>historical_residual</c> 除外），
    /// 每条：写墓碑（挡旧声明重放）+ 入队 Delete（跨设备传播）+ 删除行。
    ///
    /// <para>
    /// ⚠️ 与 <see cref="RevokeLocalDailyAdjustmentAsync"/> 的区别：后者的 identity 是
    /// "本机 + 游戏 + 日 + 来源"，只能撤**本机产生**的声明；而"删除一条每日记录"必须撤掉该日
    /// **所有设备**的声明（否则对端声明的时长会把这条记录撑住）。删除标识用**行自身的
    /// <c>adjustment_global_id</c>**（与同步协议同一身份键），绝不按游戏名/游戏 id 猜。
    /// </para>
    ///
    /// <para>调用方已持有事务 ⇒ 与事实侧删除同生共死。</para>
    /// </summary>
    private static async Task<int> RevokeAdjustmentsForRecordDeletionCoreAsync(
        SqliteConnection conn, SqliteTransaction tx, int gameId, string date,
        string localDeviceId, string gameGlobalId, string now)
    {
        var leftovers = (await conn.QueryAsync<AdjustmentRowForDeletion>(
            """
            SELECT adjustment_global_id AS AdjustmentGlobalId, COALESCE(device_id, '') AS DeviceId,
                   COALESCE(source, '') AS Source, COALESCE(created_at_utc, '') AS CreatedAtUtc,
                   COALESCE(duration_seconds, 0) AS DurationSeconds
            FROM daily_duration_adjustments
            WHERE game_id = @g AND statistical_date = @d AND source <> @historical;
            """,
            new { g = gameId, d = date, historical = DailyAdjustmentSources.HistoricalResidual }, tx)).ToList();

        var revoked = 0;

        foreach (var row in leftovers)
        {
            AddTombstoneCore(conn, tx, new SyncTombstone
            {
                TombstoneId = Guid.NewGuid().ToString("N"),
                EntityType = nameof(SyncEntityType.DailyAdjustment),
                EntityGlobalId = row.AdjustmentGlobalId,
                DeviceId = string.IsNullOrWhiteSpace(localDeviceId) ? row.DeviceId : localDeviceId,
                DeletedAtUtc = now,
                CreatedAtUtc = now
            });

            var queueId = $"delete-adjustment:{row.AdjustmentGlobalId}";
            var alreadyQueued = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sync_queue WHERE queue_id = @id;", new { id = queueId }, tx) > 0;

            if (!alreadyQueued)
            {
                await EnqueueOutboxCoreAsync(conn, tx, new SyncQueueItem
                {
                    QueueId = queueId,
                    EntityType = nameof(SyncEntityType.DailyAdjustment),
                    EntityGlobalId = row.AdjustmentGlobalId,
                    Operation = SyncOperation.Delete,
                    BaseVersion = 1,
                    Payload = SyncPayloadFactory.DailyAdjustment(
                        row.AdjustmentGlobalId, gameGlobalId, date, row.DeviceId,
                        Math.Max(0, row.DurationSeconds), row.Source,
                        string.IsNullOrWhiteSpace(row.CreatedAtUtc) ? now : row.CreatedAtUtc),
                    CreatedAtUtc = now,
                    Status = SyncQueueStatus.Pending
                });
            }

            await conn.ExecuteAsync(
                "DELETE FROM daily_duration_adjustments WHERE adjustment_global_id = @id;",
                new { id = row.AdjustmentGlobalId }, tx);

            revoked++;
        }

        return revoked;
    }

    /// <summary>
    /// 删除接收端本机的一条会话（收到**旧版本对端**发来的 Session 删除时），
    /// 并按剩余事实重算其所在会计日的派生行。
    ///
    /// <para>
    /// ⚠️ 新版本不再用"整条 Session 墓碑"表达每日记录删除（那会误删跨日会话在另一个会计日的
    /// 分片，见 <see cref="DeletePlayRecordAsync"/> 的说明）；本方法只用于**兼容旧的删除协议**。
    /// </para>
    /// </summary>
    internal static bool RemoveLocalSessionByGlobalIdCore(
        SqliteConnection conn, SqliteTransaction tx, string sessionGlobalId)
    {
        if (string.IsNullOrWhiteSpace(sessionGlobalId)) return false;

        var row = conn.QuerySingleOrDefault<SessionRowForDeletion>(
            """
            SELECT id AS Id, COALESCE(global_id, '') AS GlobalId, COALESCE(device_id, '') AS DeviceId,
                   COALESCE(process_name, '') AS ProcessName, start_time AS StartTime, end_time AS EndTime,
                   COALESCE(started_at_utc, '') AS StartedAtUtc, COALESCE(ended_at_utc, '') AS EndedAtUtc,
                   COALESCE(duration_seconds, 0) AS DurationSeconds,
                   game_id AS GameId
            FROM sessions WHERE global_id = @gid LIMIT 1;
            """,
            new { gid = sessionGlobalId }, tx);

        if (row is null) return false;

        var cutoff = ReadDailyCutoffHour(conn, tx);
        var date = AccountingDateOfSession(row.StartTime, cutoff);

        // 🛡️ 残差测量必须在删除之前：只想抹掉"算得出来的部分"。
        var residual = MeasureResidualSeconds(conn, tx, (int)row.GameId, date, cutoff);

        conn.Execute("DELETE FROM sessions WHERE id = @id;", new { id = row.Id }, tx);

        RefreshDailyRowFromRemainingFactsCore(conn, tx, (int)row.GameId, date, cutoff, residual);
        AppLog.Info($"[设备同步] 落地会话删除 {sessionGlobalId}：已删除本机会话并按剩余事实重算 {date}");
        return true;
    }

    /// <summary>
    /// 某 (game, 会计日) 上"已有 daily 值 − 当时事实值" = 受保护的历史残差（&lt;=0 时为 0）。
    ///
    /// <para>
    /// ⚠️ 只用于"用户没要求删除的那些会计日"（跨日会话的另一个分片日期）：删除目标日时**不**保护
    /// —— 用户显式删除该日记录，该日的旧存量必须随之退出统计。
    /// </para>
    /// </summary>
    private static int MeasureResidualSeconds(
        SqliteConnection conn, SqliteTransaction? tx, int gameId, string date, int cutoff)
    {
        var stored = conn.ExecuteScalar<int?>(
            "SELECT duration_seconds FROM daily_summary WHERE game_id = @g AND date = @d LIMIT 1;",
            new { g = gameId, d = date }, tx) ?? 0;

        if (stored <= 0) return 0;

        var facts = ComputeEffectiveDailySeconds(conn, tx, gameId, date, cutoff);
        return Math.Max(0, stored - facts);
    }

    /// <summary>
    /// 按**剩余事实**重算某 (game, 会计日) 的派生行：无剩余 ⇒ 删除该行；有剩余 ⇒ 精确等于
    /// <c>Σ 会话派生秒数（跳过被作废分片） + Σ 有效声明 + <paramref name="preservedResidualSeconds"/></c>，
    /// 并同步重算场次（统一口径）。
    ///
    /// <para>
    /// ⚠️ <paramref name="preservedResidualSeconds"/> 由调用方在**改动事实之前**量出并传入：
    /// 删除只允许抹掉"算得出来的那部分"，"既无会话也无声明支撑的历史值"必须原样保留
    /// （否则删除跨日记录会连带抹掉另一个会计日的来源不明时长）。
    /// </para>
    /// </summary>
    private static (int RemainingSeconds, int RemainingCount, bool Removed) RefreshDailyRowFromRemainingFactsCore(
        SqliteConnection conn, SqliteTransaction tx, int gameId, string date, int cutoff,
        int preservedResidualSeconds = 0)
    {
        var total = SumSessionDerivedSeconds(conn, tx, gameId, date, cutoff)
                    + SumAdjustments(conn, tx, gameId, date);

        var residual = Math.Max(0, preservedResidualSeconds);
        if (residual > 0) total += residual;

        var rowId = conn.QuerySingleOrDefault<long?>(
            "SELECT id FROM daily_summary WHERE game_id = @g AND date = @d LIMIT 1;",
            new { g = gameId, d = date }, tx);

        if (total <= 0)
        {
            if (rowId is not null)
            {
                conn.Execute("DELETE FROM daily_summary WHERE id = @id;", new { id = rowId.Value }, tx);
                return (0, 0, true);
            }

            return (0, 0, false);
        }

        // 删除之后派生值必须精确等于"剩余有效事实 + 受保护残差"（这里刻意不留别的残差）。
        RecomputeDailySummaryForGameDate(conn, tx, gameId, date, cutoff, residual);
        ApplyUnifiedSessionCount(conn, tx, gameId, date, cutoff, residual);

        var count = conn.ExecuteScalar<int>(
            "SELECT COALESCE(session_count, 0) FROM daily_summary WHERE game_id = @g AND date = @d LIMIT 1;",
            new { g = gameId, d = date }, tx);

        return (total, count, false);
    }

    /// <summary>会话起始时刻所在的会计日（统一走既有会计日逻辑）。</summary>
    private static string AccountingDateOfSession(string startTime, int cutoff)
    {
        if (!TryParseWallClock(startTime, out var start)) return string.Empty;
        return AccountingDateHelper.GetAccountingDateString(start, cutoff);
    }

    /// <summary>
    /// 一条会话的时长被拆分到的**全部会计日**（唯一实现 <c>ComputeDailyDeltas</c>，最多两个）。
    /// 删除的"受影响日期"集合就是由它算出来的 —— 绝不按 <c>start_time[..10]</c>（日历日）猜。
    /// </summary>
    private static IReadOnlyList<string> SliceAccountingDatesOfSession(DateTime start, int span, int cutoff)
    {
        var dates = new List<string>(2);
        foreach (var delta in GameSessionManager.ComputeDailyDeltas(
                     start, start.AddSeconds(span), span, cutoff))
        {
            if (string.IsNullOrWhiteSpace(delta.Date)) continue;
            if (!dates.Contains(delta.Date, StringComparer.Ordinal)) dates.Add(delta.Date);
        }

        return dates;
    }

    /// <summary>优先用已存的 UTC 串；缺失时用本地墙上时间换算（payload 只用于 schema 校验/审计）。</summary>
    private static string ToUtcIso(string existingUtc, string wallClock)
    {
        if (!string.IsNullOrWhiteSpace(existingUtc)) return existingUtc;

        return TryParseWallClock(wallClock, out var parsed)
            ? parsed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
            : UtcNowIso();
    }

    private sealed class PlayRecordLocator
    {
        public long GameId { get; set; }
        public string Date { get; set; } = "";
    }

    private sealed class AdjustmentRowForDeletion
    {
        public string AdjustmentGlobalId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Source { get; set; } = "";
        public string CreatedAtUtc { get; set; } = "";
        public int DurationSeconds { get; set; }
    }

    private sealed class SessionRowForDeletion
    {
        public long Id { get; set; }
        public long GameId { get; set; }
        public string GlobalId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
        public string StartedAtUtc { get; set; } = "";
        public string EndedAtUtc { get; set; } = "";
        public int DurationSeconds { get; set; }
        public long IsActive { get; set; }
    }
}
