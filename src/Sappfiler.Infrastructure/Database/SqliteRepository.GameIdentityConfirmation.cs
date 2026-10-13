using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// 一个"可能是同一款游戏、但身份证据不足"的候选对（**只读发现结果，不修改任何数据**）。
///
/// <para>
/// ⚠️ 候选本身**不是**身份结论：名称相似只用于"把可能相关的两行摆到用户面前"。
/// 真正建立身份关系必须由用户显式确认（<see cref="SqliteRepository.ConfirmGameIdentityLinkAsync"/>），
/// 走的仍是既有 <c>sync_identity_supersessions</c> 机制。
/// </para>
/// </summary>
public sealed class GameIdentityLinkCandidate
{
    /// <summary>拟保留（权威）的本地行。</summary>
    public long CanonicalLocalId { get; init; }

    /// <summary>拟并入权威行的本地行（确认后会被删除，事实搬到权威行）。</summary>
    public long DuplicateLocalId { get; init; }

    public string Name { get; init; } = "";
    public string Platform { get; init; } = "";
    public string CanonicalPlatformId { get; init; } = "";
    public string DuplicatePlatformId { get; init; } = "";
    public string CanonicalGlobalId { get; init; } = "";
    public string DuplicateGlobalId { get; init; } = "";
    public string CanonicalExecutablePath { get; init; } = "";
    public string DuplicateExecutablePath { get; init; } = "";
    public string CanonicalNotionPageId { get; init; } = "";
    public string DuplicateNotionPageId { get; init; } = "";

    /// <summary>两行的会话/日统计/声明规模（确认后的影响面，供用户判断）。</summary>
    public int CanonicalSessions { get; init; }
    public int DuplicateSessions { get; init; }
    public int DuplicateDailyRows { get; init; }
    public int DuplicateAdjustments { get; init; }

    /// <summary>支持"可能是同一款游戏"的**可读证据**（例如 exe 文件名相同、Notion 页面相同）。</summary>
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    /// <summary>阻断原因（非空 ⇒ <see cref="CanConfirm"/> 为 false，确认接口会拒绝）。</summary>
    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();

    /// <summary>是否允许确认（没有任何冻结规则阻断）。</summary>
    public bool CanConfirm => Blockers.Count == 0;
}

/// <summary>显式身份确认的执行结果。</summary>
public sealed class GameIdentityLinkResult
{
    public bool Confirmed { get; init; }
    public bool AlreadyLinked { get; init; }
    public bool Refused { get; init; }
    public string Note { get; init; } = "";
    public string Reason { get; init; } = "";
    public string CanonicalGlobalId { get; init; } = "";
    public string SupersededGlobalId { get; init; } = "";
    public int SessionsMoved { get; init; }
    public int DailyRowsAffected { get; init; }
    public int AdjustmentsMoved { get; init; }

    public string Summary
        => Confirmed
            ? $"已确认：{SupersededGlobalId} → {CanonicalGlobalId}（会话 {SessionsMoved}、日统计 {DailyRowsAffected}、声明 {AdjustmentsMoved}）"
            : AlreadyLinked ? "已关联（幂等：无需改动）" : Refused ? $"已拒绝：{Note}" : Note;
}

/// <summary>
/// <b>manual 游戏的显式身份确认</b>（2026-10-11）。
///
/// <para>
/// 背景：<c>platform + platform_id</c> 是唯一跨设备可比强身份键，而手动登记的游戏在两台设备上
/// 各自随机生成 <c>platform_id</c> / <c>global_id</c> ⇒ 现有规则（A/B/C/D）无法安全判定它们是同一款游戏
/// （名称按契约不可作为身份依据）。因此补齐一个**最小的显式入口**：
/// 用户确认两条本地记录属于同一实体后，复用既有 <c>sync_identity_supersessions</c> 建立关联，
/// 并把重复行的事实搬到权威行 —— 不新建任何身份协议。
/// </para>
///
/// <para>
/// ⚠️ 冻结规则一律不变（确认接口同样受它们约束，只做阻塞、不做放行）：
/// <list type="bullet">
/// <item>两侧强键**都可用且不相等** ⇒ 拒绝（<c>strong_key_conflict_blocked</c>）；</item>
/// <item>两侧 Notion 页面**都非空且不同** ⇒ 拒绝（<c>notion_page_conflict</c>）；</item>
/// <item>任一侧 <c>ignored</c> ⇒ 拒绝（<c>local_ignored_protected</c>）；</item>
/// <item>任一侧已归档（用户已删除）⇒ 拒绝：删除语义由删除路径/墓碑负责，绝不用"身份确认"绕过。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ <c>sync_identity_supersessions</c> **不是同步实体**（<see cref="SyncEntityType"/> 没有该类型）
/// ⇒ 确认只在**本机**生效。对端要在它自己的候选列表里做同样的确认（UI 会在两台设备上各自显示候选）。
/// </para>
/// </summary>
public partial class SqliteRepository
{
    /// <summary>候选列表的默认上限（避免候选爆炸）。</summary>
    private const int MaxIdentityLinkCandidates = 100;

    /// <summary>
    /// 只读：列出"可能同一款游戏、但身份证据不足"的候选对。
    ///
    /// <para>
    /// 分组口径**仅用于发现**：同 <c>platform</c>（归一化）+ 同名称（trim/忽略大小写）+ 双方都不是
    /// "强键可用"的行（真正有可比强键的游戏由 A 级规则自动处理，不该出现在这里让人手工确认）。
    /// 证据（exe 文件名相同 / Notion 页面相同等）与影响面一并返回，供用户判断；
    /// 命中冻结规则冲突的候选会带上 <see cref="GameIdentityLinkCandidate.Blockers"/>。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GameIdentityLinkCandidate>> FindGameIdentityLinkCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        return await Task.FromResult(FindGameIdentityLinkCandidatesCore(conn, null));
    }

    internal static IReadOnlyList<GameIdentityLinkCandidate> FindGameIdentityLinkCandidatesCore(
        SqliteConnection conn, SqliteTransaction? tx)
    {
        var games = conn.Query<GameIdentityLinkRow>(
            $"""
            SELECT id AS Id, name AS Name, platform AS Platform, platform_id AS PlatformId,
                   COALESCE(global_id, '') AS GlobalId, COALESCE(executable, '') AS Executable,
                   COALESCE(executable_path, '') AS ExecutablePath, COALESCE(notion_page_id, '') AS NotionPageId,
                   COALESCE(status, '') AS Status,
                   (SELECT COUNT(*) FROM sessions s WHERE s.game_id = games.id) AS SessionCount,
                   (SELECT COUNT(*) FROM daily_summary d WHERE d.game_id = games.id) AS DailyRowCount,
                   (SELECT COUNT(*) FROM daily_duration_adjustments a WHERE a.game_id = games.id) AS AdjustmentCount
            FROM games
            WHERE {GameStatuses.NormalGamePredicate}
            ORDER BY id ASC;
            """,
            null, tx).ToList();

        var result = new List<GameIdentityLinkCandidate>();

        var groups = games
            .GroupBy(g => (Platform: (g.Platform ?? string.Empty).Trim().ToLowerInvariant(),
                           Name: NormalizeGameName(g.Name)))
            .Where(grp => grp.Key.Name.Length > 0 && grp.Count() > 1);

        foreach (var group in groups)
        {
            var rows = group
                .OrderByDescending(ScoreForCanonical)
                .ThenBy(r => r.Id)
                .ToList();

            var head = rows[0];
            foreach (var dup in rows.Skip(1))
            {
                if (result.Count >= MaxIdentityLinkCandidates) return result;

                // 两侧强键都可用 ⇒ 交给 A 级规则（自动对账 / 阻断），不该让人手工确认。
                if (GameIdentityRules.StrongKeyAvailable(new GameIdentityFacts(head.Platform, head.PlatformId))
                    && GameIdentityRules.StrongKeyAvailable(new GameIdentityFacts(dup.Platform, dup.PlatformId)))
                {
                    continue;
                }

                result.Add(BuildCandidate(head, dup));
            }
        }

        return result;
    }

    private static GameIdentityLinkCandidate BuildCandidate(GameIdentityLinkRow head, GameIdentityLinkRow dup)
    {
        var evidence = new List<string>();
        var blockers = new List<string>();

        var headFacts = new GameIdentityFacts(head.Platform, head.PlatformId, head.Executable, head.ExecutablePath, head.Name, head.NotionPageId);
        var dupFacts = new GameIdentityFacts(dup.Platform, dup.PlatformId, dup.Executable, dup.ExecutablePath, dup.Name, dup.NotionPageId);

        if (GameIdentityRules.StrongKeyAvailable(headFacts) && GameIdentityRules.StrongKeyAvailable(dupFacts))
        {
            blockers.Add(IdentityDecisionReasons.StrongKeyConflict
                + $"：两侧都有可比的平台强键且不相等（{head.Platform}/{head.PlatformId} vs {dup.Platform}/{dup.PlatformId}）");
        }
        else
        {
            if (GameIdentityRules.StrongKeyAvailable(headFacts) || GameIdentityRules.StrongKeyAvailable(dupFacts))
            {
                evidence.Add($"仅一侧有平台强键（{head.Platform}/{head.PlatformId} vs {dup.Platform}/{dup.PlatformId}）");
            }
            else
            {
                evidence.Add($"两侧都没有跨设备可比强键（platform_id：{head.PlatformId} vs {dup.PlatformId}）");
            }
        }

        if (GameIdentityRules.NotionPageConflicts(headFacts, dupFacts))
        {
            blockers.Add(IdentityDecisionReasons.NotionPageConflict
                + $"：绑定的是不同 Notion 页面（{head.NotionPageId} vs {dup.NotionPageId}）");
        }
        else if (head.NotionPageId.Length > 0 && string.Equals(
                     head.NotionPageId.Replace("-", ""), dup.NotionPageId.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add($"同一个 Notion 页面（{head.NotionPageId}）");
        }

        var headExe = GameIdentityRules.NormalizeExeName(head.Executable);
        var dupExe = GameIdentityRules.NormalizeExeName(dup.Executable);
        if (headExe.Length > 0 && headExe == dupExe) evidence.Add($"可执行文件名相同（{headExe}）");
        else if (headExe.Length > 0 && dupExe.Length > 0) evidence.Add($"可执行文件名不同（{headExe} vs {dupExe}）");

        if (GameIdentityRules.PathAvailable(headFacts) && GameIdentityRules.PathAvailable(dupFacts))
        {
            evidence.Add(GameIdentityRules.PathEqual(headFacts, dupFacts)
                ? "可执行文件路径完全相同"
                : "可执行文件路径不同（跨设备安装目录不同是正常现象，不作为身份依据）");
        }

        evidence.Add($"名称相同（{head.Name}）—— ⚠️ 名称**不是**身份依据，仅用于发现候选");
        evidence.Add($"影响面：会话 {head.SessionCount} vs {dup.SessionCount}、日统计 {head.DailyRowCount} vs {dup.DailyRowCount}、声明 {head.AdjustmentCount} vs {dup.AdjustmentCount}");

        return new GameIdentityLinkCandidate
        {
            CanonicalLocalId = head.Id,
            DuplicateLocalId = dup.Id,
            Name = head.Name ?? string.Empty,
            Platform = head.Platform ?? string.Empty,
            CanonicalPlatformId = head.PlatformId ?? string.Empty,
            DuplicatePlatformId = dup.PlatformId ?? string.Empty,
            CanonicalGlobalId = head.GlobalId,
            DuplicateGlobalId = dup.GlobalId,
            CanonicalExecutablePath = head.ExecutablePath,
            DuplicateExecutablePath = dup.ExecutablePath,
            CanonicalNotionPageId = head.NotionPageId,
            DuplicateNotionPageId = dup.NotionPageId,
            CanonicalSessions = head.SessionCount,
            DuplicateSessions = dup.SessionCount,
            DuplicateDailyRows = dup.DailyRowCount,
            DuplicateAdjustments = dup.AdjustmentCount,
            Evidence = evidence,
            Blockers = blockers
        };
    }

    /// <summary>候选排序用的评分（与启动去重的口径一致：有 exe 路径 &gt; 有强键 &gt; 有 Notion 页 &gt; id 更小）。</summary>
    private static int ScoreForCanonical(GameIdentityLinkRow row)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(row.ExecutablePath)) score += 100;
        if (GameIdentityRules.IsAuthoritativePlatformKey(row.Platform, row.PlatformId)) score += 50;
        if (!string.IsNullOrWhiteSpace(row.NotionPageId)) score += 20;
        return score;
    }

    /// <summary>
    /// <b>显式确认两条本地记录属于同一款游戏</b>：把 <paramref name="duplicateLocalId"/> 并入
    /// <paramref name="canonicalLocalId"/>，复用既有身份合并机制
    /// （<c>MigrateGameDataToCanonical</c> + <c>sync_identity_supersessions</c>），**不写删除墓碑**。
    ///
    /// <para>
    /// 冻结规则（A 级强键冲突 / Notion 身份冲突 / ignored / 已归档）一律**拒绝**，只做阻塞不做放行。
    /// 幂等：重复确认同一条已存在的关联不会产生第二次写入。
    /// </para>
    /// </summary>
    public async Task<GameIdentityLinkResult> ConfirmGameIdentityLinkAsync(
        int canonicalLocalId, int duplicateLocalId, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            var result = ConfirmGameIdentityLinkCore(conn, tx, canonicalLocalId, duplicateLocalId, reason);
            if (result.Confirmed)
            {
                tx.Commit();
            }
            else
            {
                tx.Rollback();
            }

            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static GameIdentityLinkResult ConfirmGameIdentityLinkCore(
        SqliteConnection conn, SqliteTransaction tx, int canonicalLocalId, int duplicateLocalId, string? reason)
    {
        if (canonicalLocalId == duplicateLocalId)
        {
            return Refuse("两条记录是同一条（id 相同）⇒ 无需确认");
        }

        var rows = conn.Query<GameIdentityLinkRow>(
            """
            SELECT id AS Id, name AS Name, platform AS Platform, platform_id AS PlatformId,
                   COALESCE(global_id, '') AS GlobalId, COALESCE(executable, '') AS Executable,
                   COALESCE(executable_path, '') AS ExecutablePath, COALESCE(notion_page_id, '') AS NotionPageId,
                   COALESCE(status, '') AS Status,
                   0 AS SessionCount, 0 AS DailyRowCount, 0 AS AdjustmentCount
            FROM games WHERE id IN (@canonicalId, @dupId);
            """,
            new { canonicalId = canonicalLocalId, dupId = duplicateLocalId }, tx).ToList();

        var canonical = rows.FirstOrDefault(r => r.Id == canonicalLocalId);
        var duplicate = rows.FirstOrDefault(r => r.Id == duplicateLocalId);

        if (canonical is null || duplicate is null)
        {
            return Refuse("目标记录不存在（可能已被删除或已被并入其它行）⇒ 请刷新候选后重试");
        }

        if (GameStatuses.IsIgnored(canonical.Status) || GameStatuses.IsIgnored(duplicate.Status))
        {
            return Refuse($"{IdentityDecisionReasons.LocalIgnored}：被忽略的游戏不由身份确认改变状态");
        }

        if (GameStatuses.IsArchived(canonical.Status) || GameStatuses.IsArchived(duplicate.Status))
        {
            return Refuse("有一侧已被删除（归档）⇒ 删除语义由删除路径与墓碑负责，"
                + "身份确认不会复活已删除游戏；如需删除请在保留的那一行上执行删除");
        }

        var canonicalFacts = new GameIdentityFacts(canonical.Platform, canonical.PlatformId,
            canonical.Executable, canonical.ExecutablePath, canonical.Name, canonical.NotionPageId);
        var duplicateFacts = new GameIdentityFacts(duplicate.Platform, duplicate.PlatformId,
            duplicate.Executable, duplicate.ExecutablePath, duplicate.Name, duplicate.NotionPageId);

        // 冻结规则：A 级强键冲突 → 拒绝（绝不因为用户点了确认就绕过身份规则）。
        if (GameIdentityRules.StrongKeyAvailable(canonicalFacts) && GameIdentityRules.StrongKeyAvailable(duplicateFacts)
            && !GameIdentityRules.StrongKeyEqual(canonicalFacts, duplicateFacts))
        {
            return Refuse($"{IdentityDecisionReasons.StrongKeyConflict}：两侧都有可比的平台强键且不相等"
                + $"（{canonical.Platform}/{canonical.PlatformId} vs {duplicate.Platform}/{duplicate.PlatformId}）"
                + " ⇒ 这是两款不同的游戏，不能合并");
        }

        // 冻结规则：Notion 身份冲突 → 拒绝（绝不替用户在两个 Provider 身份之间做选择）。
        if (GameIdentityRules.NotionPageConflicts(canonicalFacts, duplicateFacts))
        {
            return Refuse($"{IdentityDecisionReasons.NotionPageConflict}：两侧绑定的是不同 Notion 页面"
                + $"（{canonical.NotionPageId} vs {duplicate.NotionPageId}）⇒ 请先在两台设备上统一页面绑定");
        }

        var canonicalGlobalId = canonical.GlobalId;
        var supersededGlobalId = duplicate.GlobalId;

        // 幂等：已经把该 identity 关联到同一个权威行 ⇒ 什么都不做。
        if (canonicalGlobalId.Length > 0 && supersededGlobalId.Length > 0
            && !string.Equals(canonicalGlobalId, supersededGlobalId, StringComparison.Ordinal))
        {
            var existing = conn.QuerySingleOrDefault<string?>(
                """
                SELECT canonical_global_id FROM sync_identity_supersessions
                WHERE entity_type = @type AND superseded_global_id = @gid LIMIT 1;
                """,
                new { type = nameof(SyncEntityType.Game), gid = supersededGlobalId }, tx);

            if (string.Equals(existing, canonicalGlobalId, StringComparison.Ordinal))
            {
                return new GameIdentityLinkResult
                {
                    AlreadyLinked = true,
                    CanonicalGlobalId = canonicalGlobalId,
                    SupersededGlobalId = supersededGlobalId,
                    Note = "该 identity 已关联到同一权威行"
                };
            }
        }

        // 影响面（在搬运之前统计，供审计）。
        var sessionsMoved = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sessions WHERE game_id = @id;", new { id = duplicateLocalId }, tx);
        var dailyAffected = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM daily_summary WHERE game_id = @id;", new { id = duplicateLocalId }, tx);
        var adjustmentsMoved = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM daily_duration_adjustments WHERE game_id = @id;", new { id = duplicateLocalId }, tx);

        var inheritedIdentity = false;

        if (canonicalGlobalId.Length == 0 && supersededGlobalId.Length > 0)
        {
            // 权威行没有 identity ⇒ 延续被并入行的 identity（不产生第二条 identity，也就不需要 supersession）。
            conn.Execute(
                "UPDATE games SET global_id = @gid WHERE id = @id AND NULLIF(TRIM(global_id), '') IS NULL;",
                new { gid = supersededGlobalId, id = canonicalLocalId }, tx);
            canonicalGlobalId = supersededGlobalId;
            inheritedIdentity = true;
        }

        // 事实搬运（会话 / 声明 / 日统计）+ 删除重复行：与启动去重、2e 对账**同一份**实现。
        MigrateGameDataToCanonical(conn, tx, canonicalLocalId, duplicateLocalId);
        conn.Execute("DELETE FROM games WHERE id = @id;", new { id = duplicateLocalId }, tx);

        var writtenSupersession = false;
        var supersededResult = string.Empty;
        if (supersededGlobalId.Length > 0 && canonicalGlobalId.Length > 0)
        {
            // 🛡️ 2026-10-11（身份协议根因修复）：旧实现直接写"用户选中那一行 → 另一行"的方向映射。
            //    如果两台设备各自确认（或各自对账），就会写出**相反方向**的映射并在传播后成环。
            //    现在统一走协议入口：canonical = 等价类内最小的 global_id（两台设备独立复算一致），
            //    真正发生变化的映射**入队传播** ⇒ 一处确认即可让对端收敛，无需重复确认。
            var linkOutcome = EstablishIdentityLinkCore(conn, tx,
                new[] { canonicalGlobalId, supersededGlobalId },
                string.IsNullOrWhiteSpace(reason) ? SupersessionReasons.UserConfirmedSameEntity : reason!);

            EnqueueIdentityMappingsCore(conn, tx, linkOutcome.MappingsChanged, SupersessionReasons.UserConfirmedSameEntity);

            canonicalGlobalId = linkOutcome.CanonicalGlobalId.Length > 0 ? linkOutcome.CanonicalGlobalId : canonicalGlobalId;
            writtenSupersession = linkOutcome.MappingsChanged.Count > 0;
            supersededResult = linkOutcome.MappingsChanged.Count > 0
                ? linkOutcome.MappingsChanged[0].Superseded
                : string.Empty;

            // 该 identity 若曾被判为"身份未确认"，现在可以收敛/落地它依赖的事实了。
            var unresolved = conn.ExecuteScalar<int>(
                """
                SELECT COUNT(*) FROM sync_deferred_changes
                WHERE entity_global_id = @gid AND resolved_at_utc IS NULL;
                """,
                new { gid = supersededGlobalId }, tx);

            AppLog.Info($"[设备同步] 用户确认身份关联：{supersededGlobalId} → {canonicalGlobalId}"
                + $"（会话 {sessionsMoved}、日统计 {dailyAffected}、声明 {adjustmentsMoved}、"
                + $"待收敛台账 {unresolved} 条）");
        }

        return new GameIdentityLinkResult
        {
            Confirmed = true,
            CanonicalGlobalId = canonicalGlobalId,
            SupersededGlobalId = inheritedIdentity ? "" : supersededResult,
            SessionsMoved = sessionsMoved,
            DailyRowsAffected = dailyAffected,
            AdjustmentsMoved = adjustmentsMoved,
            Reason = writtenSupersession ? SupersessionReasons.UserConfirmedSameEntity : "",
            Note = inheritedIdentity
                ? "权威行原本没有 identity ⇒ 已延续被并入行的 identity（同一实体延续，不产生第二条身份）"
                : "已建立身份关联（并搬运事实、删除重复行）"
        };

        static GameIdentityLinkResult Refuse(string note)
            => new() { Refused = true, Note = note };
    }

    private sealed class GameIdentityLinkRow
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? Platform { get; set; }
        public string? PlatformId { get; set; }
        public string GlobalId { get; set; } = "";
        public string Executable { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string NotionPageId { get; set; } = "";
        public string Status { get; set; } = "";
        public int SessionCount { get; set; }
        public int DailyRowCount { get; set; }
        public int AdjustmentCount { get; set; }
    }
}
