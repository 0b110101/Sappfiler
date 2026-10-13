using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>
/// 「本机是否已删除这同一款游戏」的只读判定结果。
///
/// <para>
/// <paramref name="StrongIdentity"/> 区分两种命中：
/// <list type="bullet">
/// <item><c>true</c>（强身份）：墓碑 / global_id / (platform, platform_id) / notion_page_id 命中 ——
///       这些是**技术身份**，可以把"远端这一 identity 也被删除了"固化成墓碑；</item>
/// <item><c>false</c>（弱身份）：同平台 + 同规范化名称，且 platform_id 为空或两侧不一致 ——
///       典型形态是 <c>manual</c> 行在两台设备上各自随机生成 platform_id。
///       此时**只拒绝复活 + 延后留痕**，不写墓碑（名字相等不足以断言"就是同一 identity"，
///       避免误伤用户重新添加的同名游戏）。</item>
/// </list>
/// </para>
/// </summary>
public sealed record GameDeletionCheck(bool Deleted, long? MatchedGameId, string Evidence, bool StrongIdentity)
{
    public static readonly GameDeletionCheck NotDeleted = new(false, null, "", false);

    /// <summary>
    /// **弱匹配**（同平台同名、platform_id 为空或分歧）命中的本地归档行 id。
    ///
    /// <para>
    /// ⚠️ 身份模型约束（2026-10-11 审查）：名称**不是**实体身份。弱匹配只能用于**发现潜在冲突**，
    /// 绝不能单独作为「归档另一款游戏」「覆盖身份」「永久固化强删除标记」「把历史事实改归到别的游戏」
    /// 的依据。因此本字段与 <see cref="Deleted"/> **互斥**：有弱候选时 <c>Deleted</c> 必为 <c>false</c>。
    /// </para>
    /// </summary>
    public IReadOnlyList<long> WeakNameCandidates { get; init; } = Array.Empty<long>();

    /// <summary>是否存在"可能是同一款游戏、但身份未建立关联"的弱线索。</summary>
    public bool HasWeakNameCandidate => WeakNameCandidates.Count > 0;
}

/// <summary>
/// <b>删除状态判定闸门</b>（2026-10-11，多设备"已删除游戏复活"修复）。
///
/// <para>
/// 断点回顾（均由实机代码路径证实）：
/// <list type="number">
/// <item><c>ApplyRemoteGame</c> 落地前只由调用方按**精确 global_id** 查墓碑；两台设备各自随机生成
///       global_id（<c>manual</c> 行的 platform_id 也是本机随机）⇒ 旧 Create 既躲过墓碑、
///       也躲过 (platform, platform_id) 业务键，直接 <c>INSERT ... 'active'</c> ⇒ <b>复活</b>；</item>
/// <item>收到远端 Delete 时用 payload 的 (platform, platform_id) 兜底定位本地行，platform_id 分裂时
///       定位失败 ⇒ 接收端游戏"界面上还在"；</item>
/// <item>Notion 导入的查找带 <c>NormalGamePredicate</c>（排除 archived）⇒ 已删除游戏查不到就新建
///       一条 <c>active</c> 行 ⇒ 同一款游戏被另一个身份入口复活。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 本闸门**只读**：不建游戏、不改状态、不删事实。判定为"已删除"时由调用方决定
/// 「拒绝落地 + 延后留痕（deletion_conflict）」或「归档本地行」，历史 Session / daily_summary 一律不动。
/// </para>
/// </summary>
public partial class SqliteRepository
{
    /// <summary>
    /// 弱线索冲突（"可能是同一款游戏，但身份未建立关联"）在 <c>sync_deferred_changes</c> 中的
    /// 专用 entity_type。
    ///
    /// <para>
    /// ⚠️ 刻意**不是** <c>Game</c>：这类条目不是"某条实体变更没落地"，而是"需要人工建立身份关系"
    /// 的复核项。延后对账分派器对未知 entity_type 一律保持 unresolved（不重试、不改数据），
    /// 因此用独立类型能避免被对账流程误标成"已解决"。它只写在本机台账（该表不参与同步）。
    /// </para>
    /// </summary>
    internal const string IdentityReviewEntityType = "GameDeletionReview";

    /// <summary>规范化游戏名（trim + 折叠内部空白 + 小写）—— 弱线索比较用。</summary>
    internal static string NormalizeGameName(string? name)
    {
        var parts = (name ?? string.Empty).Trim()
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).ToLowerInvariant();
    }

    /// <summary>
    /// 判定「本机是否已删除这同一款游戏」。按强度从高到低逐级检查，命中即返回。
    ///
    /// <para>
    /// 传入的字段可以部分为空：例如远端 Game 载荷带 global_id/platform/platform_id/notion_page_id/name，
    /// 而 Session 载荷只能带 global_id。字段越少，能命中的级别越靠前（global_id/墓碑）。
    /// </para>
    /// </summary>
    internal static GameDeletionCheck CheckLocalGameDeletion(
        SqliteConnection conn, SqliteTransaction? tx,
        string? globalId, string? platform, string? platformId, string? notionPageId, string? name)
    {
        var gid = (globalId ?? string.Empty).Trim();
        var plat = (platform ?? string.Empty).Trim();
        var pid = (platformId ?? string.Empty).Trim();
        var npid = (notionPageId ?? string.Empty).Trim();
        var normalizedName = NormalizeGameName(name);

        // ① 墓碑：这个 identity 已被删除（本机或对端传播而来）。最强证据。
        if (gid.Length > 0)
        {
            if (conn.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sync_tombstones WHERE entity_type = @type AND entity_global_id = @gid;",
                    new { type = nameof(SyncEntityType.Game), gid }, tx) > 0)
            {
                return new GameDeletionCheck(true, null, $"墓碑命中（global_id={gid}）", true);
            }

            // ② 同 identity 的本地行已归档（墓碑可能已被保留期清理，但归档状态仍在）。
            var archivedById = conn.QuerySingleOrDefault<long?>(
                """
                SELECT id FROM games
                WHERE global_id = @gid AND LOWER(COALESCE(status, '')) = @archived
                ORDER BY id ASC LIMIT 1;
                """,
                new { gid, archived = GameStatuses.Archived }, tx);
            if (archivedById is not null)
            {
                return new GameDeletionCheck(true, archivedById, $"同 global_id 的本地行已归档（game_id={archivedById}）", true);
            }
        }

        // ③ 强业务键：(platform, platform_id) 命中一条**已归档**行（identity 分裂但强键一致）。
        if (plat.Length > 0 && pid.Length > 0)
        {
            var archivedByBusinessKey = conn.QuerySingleOrDefault<long?>(
                """
                SELECT id FROM games
                WHERE LOWER(platform) = LOWER(@platform)
                  AND LOWER(COALESCE(platform_id, '')) = LOWER(@pid)
                  AND LOWER(COALESCE(status, '')) = @archived
                ORDER BY id ASC LIMIT 1;
                """,
                new { platform = plat, pid, archived = GameStatuses.Archived }, tx);
            if (archivedByBusinessKey is not null)
            {
                return new GameDeletionCheck(true, archivedByBusinessKey,
                    $"同 (platform={plat}, platform_id={pid}) 的本地行已归档（game_id={archivedByBusinessKey}）", true);
            }
        }

        // ④ notion_page_id：Notion 页面是跨设备共享身份，命中归档行即视为"已删除"。
        if (npid.Length > 0)
        {
            var archivedByNotion = conn.QuerySingleOrDefault<long?>(
                """
                SELECT id FROM games
                WHERE notion_page_id = @npid AND LOWER(COALESCE(status, '')) = @archived
                ORDER BY id ASC LIMIT 1;
                """,
                new { npid, archived = GameStatuses.Archived }, tx);
            if (archivedByNotion is not null)
            {
                return new GameDeletionCheck(true, archivedByNotion,
                    $"同 notion_page_id 的本地行已归档（game_id={archivedByNotion}）", true);
            }
        }

        // ⑤ 弱身份线索（**不是**删除判定！）：同平台 + 同规范化名称，且 platform_id 任一侧为空或两侧不一致。
        //    manual 行在每台设备上各自随机生成 platform_id，这是实机"删了 A 的 Ficket、B 的 Ficket 还在"的形态。
        //
        //    ⚠️ 身份模型铁律（2026-10-11 审查）：**名称不是实体身份**。因此这里
        //      · 不返回 Deleted（否则会把"同名的另一款游戏"当成已删除游戏，阻止它正常同步）；
        //      · 不写墓碑（否则会把错误的删除状态永久固化到另一个 identity 上）；
        //      · 不用于归档/改归属（调用方只允许把它记为"待人工确认的身份冲突"）。
        //    真正的跨设备身份必须靠 supersession alias 或强业务键建立，不能靠名字猜。
        var weakCandidates = new List<long>();
        if (plat.Length > 0 && normalizedName.Length > 0)
        {
            var candidates = conn.Query<(long Id, string? Name, string? PlatformId)>(
                """
                SELECT id AS Id, name AS Name, platform_id AS PlatformId FROM games
                WHERE LOWER(platform) = LOWER(@platform)
                  AND LOWER(COALESCE(status, '')) = @archived;
                """,
                new { platform = plat, archived = GameStatuses.Archived }, tx);

            foreach (var c in candidates)
            {
                if (!string.Equals(NormalizeGameName(c.Name), normalizedName, StringComparison.Ordinal)) continue;

                var candidatePid = (c.PlatformId ?? string.Empty).Trim();
                var diverged = candidatePid.Length == 0 || pid.Length == 0
                               || !string.Equals(candidatePid, pid, StringComparison.OrdinalIgnoreCase);

                if (diverged) weakCandidates.Add(c.Id);
            }
        }

        if (weakCandidates.Count > 0)
        {
            return new GameDeletionCheck(false, null,
                $"同平台同名（platform={plat}, name={name}）的本地归档行 {string.Join(",", weakCandidates)} "
                + $"与远端 identity 的 platform_id 分裂（远端='{pid}'）⇒ 身份未建立关联，只报冲突不判删除",
                false)
            { WeakNameCandidates = weakCandidates };
        }

        return GameDeletionCheck.NotDeleted;
    }

    /// <summary>
    /// 找出**未归档**且「同平台 + 同规范化名称」的本地行 id。
    ///
    /// <para>
    /// ⚠️ 仅供「发现潜在冲突」使用（收到远端 Delete 但身份解析失败、且弱线索存在时）：
    /// <b>绝不</b>用它来归档这些行 —— 同名不等于同一款游戏，误归档会造成不可逆的界面/语义错误。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<long> FindLocalGamesByPlatformAndName(
        SqliteConnection conn, SqliteTransaction? tx, string? platform, string? name)
    {
        var plat = (platform ?? string.Empty).Trim();
        var normalizedName = NormalizeGameName(name);
        if (plat.Length == 0 || normalizedName.Length == 0) return Array.Empty<long>();

        var candidates = conn.Query<(long Id, string? Name)>(
            """
            SELECT id AS Id, name AS Name FROM games
            WHERE LOWER(platform) = LOWER(@platform)
              AND LOWER(COALESCE(status, '')) <> @archived;
            """,
            new { platform = plat, archived = GameStatuses.Archived }, tx);

        var matches = new List<long>();
        foreach (var c in candidates)
        {
            if (string.Equals(NormalizeGameName(c.Name), normalizedName, StringComparison.Ordinal)) matches.Add(c.Id);
        }

        return matches;
    }

    /// <summary>
    /// 找出「已归档且同平台同名」的本地行 id（**弱线索**，供 Notion 导入等场景报告冲突用）。
    /// 与 <see cref="FindArchivedGameForImport"/> 严格区分：后者只认强身份，本方法只用于报告。
    /// </summary>
    internal static IReadOnlyList<long> FindArchivedGamesByPlatformAndNameWeak(
        SqliteConnection conn, string? platform, string? name)
    {
        var plat = (platform ?? string.Empty).Trim();
        var normalizedName = NormalizeGameName(name);
        if (plat.Length == 0 || normalizedName.Length == 0) return Array.Empty<long>();

        var candidates = conn.Query<(long Id, string? Name)>(
            """
            SELECT id AS Id, name AS Name FROM games
            WHERE LOWER(platform) = LOWER(@platform)
              AND LOWER(COALESCE(status, '')) = @archived;
            """,
            new { platform = plat, archived = GameStatuses.Archived });

        var matches = new List<long>();
        foreach (var c in candidates)
        {
            if (string.Equals(NormalizeGameName(c.Name), normalizedName, StringComparison.Ordinal)) matches.Add(c.Id);
        }

        return matches;
    }

    /// <summary>
    /// 把「本机已删除这款游戏」的结论**固化成墓碑**（幂等）。
    ///
    /// <para>
    /// 用于 <c>ApplyRemoteGame</c> 的强身份命中：远端旧记录带着一个本机从未见过的 global_id，
    /// 若不固化，同一条旧记录每次重放/Deferred 重试都要重新走一遍判定（虽无副作用，但每次都留下
    /// 一条待处理项）。固化后，后续重放会在"墓碑优先"这一步就被稳定挡下。
    /// </para>
    /// </summary>
    internal static void AdoptGameDeletionTombstone(
        SqliteConnection conn, SqliteTransaction? tx, string globalId, string deviceId, string evidence)
    {
        if (string.IsNullOrWhiteSpace(globalId)) return;

        AddTombstoneCore(conn, tx, new SyncTombstone
        {
            TombstoneId = Guid.NewGuid().ToString("N"),
            EntityType = nameof(SyncEntityType.Game),
            EntityGlobalId = globalId.Trim(),
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? "local-deletion-guard" : deviceId,
            DeletedAtUtc = UtcNowIso(),
            CreatedAtUtc = UtcNowIso()
        });

        AppLog.Info($"[设备同步] 拒绝复活已删除游戏：为远端 identity {globalId} 固化删除墓碑（{evidence}）");
    }

    /// <summary>
    /// Notion 导入时查找「已归档的**同一实体**」，命中则**复用它**而不是新建 active 行。
    ///
    /// <para>
    /// ⚠️ 只认**强身份**（2026-10-11 身份模型审查）：
    /// <list type="number">
    /// <item>强业务键 <c>(platform, platform_id)</c>（表上有 <c>UNIQUE(platform, platform_id)</c>，同值即同一实体）；</item>
    /// <item><c>notion_page_id</c>（跨设备共享的页面身份）。</item>
    /// </list>
    /// 刻意**不再**按名称匹配：同名可能是另一款游戏，复用它的归档行会把导入事实挂到错误的实体上。
    /// 弱线索（同平台同名）只用于报告冲突，见 <see cref="FindArchivedGamesByPlatformAndNameWeak"/>。
    /// 找不到返回 <c>null</c>（调用方按新游戏处理，但调用方还必须自行处理弱线索冲突）。
    /// </para>
    /// </summary>
    internal static long? FindArchivedGameForImport(
        SqliteConnection conn, string? platform, string? platformId, string? notionPageId, string? name)
    {
        var plat = (platform ?? string.Empty).Trim();
        var pid = (platformId ?? string.Empty).Trim();
        var npid = (notionPageId ?? string.Empty).Trim();
        _ = name;

        if (plat.Length > 0 && pid.Length > 0)
        {
            var byKey = conn.QuerySingleOrDefault<long?>(
                """
                SELECT id FROM games
                WHERE LOWER(platform) = LOWER(@platform)
                  AND LOWER(COALESCE(platform_id, '')) = LOWER(@pid)
                  AND LOWER(COALESCE(status, '')) = @archived
                ORDER BY id ASC LIMIT 1;
                """,
                new { platform = plat, pid, archived = GameStatuses.Archived });
            if (byKey is not null) return byKey;
        }

        if (npid.Length > 0)
        {
            var byNotion = conn.QuerySingleOrDefault<long?>(
                """
                SELECT id FROM games
                WHERE notion_page_id = @npid AND LOWER(COALESCE(status, '')) = @archived
                ORDER BY id ASC LIMIT 1;
                """,
                new { npid, archived = GameStatuses.Archived });
            if (byNotion is not null) return byNotion;
        }

        return null;
    }
}
