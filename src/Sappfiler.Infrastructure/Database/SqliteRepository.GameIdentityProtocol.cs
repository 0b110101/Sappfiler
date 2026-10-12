using Dapper;
using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameTimeTracker.Infrastructure.Database;

/// <summary>一次身份关联建立的产物。</summary>
/// <param name="CanonicalGlobalId">按确定性裁决算出的 canonical identity。</param>
/// <param name="ClassMembers">该等价类里的全部 game identity。</param>
/// <param name="MappingsChanged">本次**新写/改写**的映射（需要传播给对端）。</param>
public sealed record GameIdentityLinkOutcome(
    string CanonicalGlobalId,
    IReadOnlyList<string> ClassMembers,
    IReadOnlyList<(string Superseded, string Canonical)> MappingsChanged);

/// <summary>
/// <b>跨设备实体身份协议</b>（2026-10-11）。
///
/// <para>
/// 根因（沿真实调用链确认）：
/// <list type="number">
/// <item><c>sync_identity_supersessions</c> **不是同步实体** ⇒ 身份关联只在本机生效；</item>
/// <item>canonical 裁决是"<b>本地 identity 优先</b>"（<c>ReconcileRemoteGameAsync</c> 保留本机
///       <c>global_id</c>；显式确认也以用户选中的本机行为 canonical）⇒ 两台设备各自算出**相反**的
///       <c>gB→gA</c> / <c>gA→gB</c>，传播后即形成**反向映射/环**；</item>
/// <item><c>ResolveLocalGameIdByGlobalId</c> 只解析一跳、且要求本地存在 canonical 那一行 ⇒
///       身份分裂时解析失败，删除/会话/声明落地随之失效。</item>
/// </list>
/// </para>
///
/// <para>
/// 本文件实现的协议（最小扩展，复用既有存储与同步链路）：
/// <list type="number">
/// <item><b>确定性 canonical</b>：等价类内取 <c>global_id</c> 最小者
///       （<see cref="GameIdentityRules.ElectCanonical"/>）——不依赖设备、时间、到达顺序；</item>
/// <item><b>等价类**星形归一化**</b>：类内除 canonical 外每个 identity 都写一条
///       <c>superseded→canonical</c>，并删除"把 canonical 指向别处"的非法行 ⇒ 结构上不可能成环；</item>
/// <item><b>关联可传播</b>：新写/改写的映射入 <c>sync_queue</c>（<see cref="SyncEntityType.GameIdentity"/>，
///       确定性 queue_id ⇒ 重试/重放幂等），对端落地时**复算** canonical（因此乱序、并发、重复都收敛）；</item>
/// <item><b>落地守卫不放松</b>：对端落地前若类内本地行之间存在强键冲突或 Notion 身份冲突，
///       一律**延后**（沿用既有冻结规则），不做折叠、不写映射；</item>
/// <item><b>解析按等价类</b>：<c>ResolveLocalGameIdByGlobalId</c> 先归一到类 canonical，
///       再按"canonical 行 → 类内任意一行"回退定位本机实体；</item>
/// <item><b>旧数据归一化</b>：migration 014 对既有反向/环状映射做一次确定性重写
///       （只改映射，不动任何事实）。</item>
/// </list>
/// </para>
/// </summary>
public partial class SqliteRepository
{
    // ==================== 等价类读取 / 裁决 ====================

    /// <summary>读取全部 Game 身份映射（superseded → canonical）。</summary>
    internal static IReadOnlyList<(string Superseded, string Canonical)> ReadGameIdentityMappingsCore(
        SqliteConnection conn, SqliteTransaction? tx)
        => conn.Query<(string Superseded, string Canonical)>(
            """
            SELECT superseded_global_id AS Superseded, canonical_global_id AS Canonical
            FROM sync_identity_supersessions
            WHERE entity_type = @type;
            """,
            new { type = nameof(SyncEntityType.Game) }, tx).ToList();

    /// <summary>
    /// 求给定 identity 所在的**等价类**（含全部种子）：在映射图上做双向闭包。
    ///
    /// <para>
    /// 双向遍历是必要的：历史数据里可能存在 <c>gB→gA</c> 与 <c>gA→gB</c> 这类反向/环状映射，
    /// 只按一个方向走会得到两个"类"，从而各自算出不同 canonical（正是要修掉的根因）。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> ReadIdentityClassCore(
        SqliteConnection conn, SqliteTransaction? tx, IEnumerable<string?> seeds)
    {
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        void Link(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.Ordinal)) return;
            if (!adjacency.TryGetValue(a, out var setA)) adjacency[a] = setA = new HashSet<string>(StringComparer.Ordinal);
            if (!adjacency.TryGetValue(b, out var setB)) adjacency[b] = setB = new HashSet<string>(StringComparer.Ordinal);
            setA.Add(b);
            setB.Add(a);
        }

        foreach (var (superseded, canonical) in ReadGameIdentityMappingsCore(conn, tx))
        {
            Link((superseded ?? string.Empty).Trim(), (canonical ?? string.Empty).Trim());
        }

        var members = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var seed in seeds)
        {
            var id = (seed ?? string.Empty).Trim();
            if (id.Length == 0 || !members.Add(id)) continue;
            queue.Enqueue(id);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var neighbours)) continue;

            foreach (var neighbour in neighbours)
            {
                if (members.Add(neighbour)) queue.Enqueue(neighbour);
            }
        }

        return members.OrderBy(m => m, StringComparer.Ordinal).ToList();
    }

    // ==================== 建立关联（唯一实现） ====================

    /// <summary>
    /// 把给定 identity 收敛成**一个等价类**：canonical = 类内最小 <c>global_id</c>，
    /// 其余每个 identity 写一条 <c>superseded→canonical</c>，并删除把 canonical 指向别处的非法行。
    ///
    /// <para>
    /// 幂等、可重放：重复调用不会新增行；返回的 <see cref="GameIdentityLinkOutcome.MappingsChanged"/>
    /// 只包含**本次真正发生变化**的映射（调用方据此决定传播什么）。
    /// </para>
    /// </summary>
    internal static GameIdentityLinkOutcome EstablishIdentityLinkCore(
        SqliteConnection conn, SqliteTransaction? tx, IEnumerable<string?> ids, string reason)
    {
        var seeds = ids.Select(i => (i ?? string.Empty).Trim())
            .Where(i => i.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var members = ReadIdentityClassCore(conn, tx, seeds);
        var canonical = GameIdentityRules.ElectCanonical(members) ?? string.Empty;
        var changed = new List<(string Superseded, string Canonical)>();

        if (canonical.Length == 0)
        {
            return new GameIdentityLinkOutcome(string.Empty, members, changed);
        }

        var now = UtcNowIso();
        var deviceId = ReadLocalDeviceId(conn, tx);
        var existingMappings = ReadGameIdentityMappingsCore(conn, tx)
            .ToDictionary(m => (m.Superseded ?? string.Empty).Trim(), m => (m.Canonical ?? string.Empty).Trim(),
                StringComparer.Ordinal);

        foreach (var member in members)
        {
            if (string.Equals(member, canonical, StringComparison.Ordinal)) continue;

            if (existingMappings.TryGetValue(member, out var current)
                && string.Equals(current, canonical, StringComparison.Ordinal))
            {
                continue;   // 已是目标形态
            }

            AddSupersessionCore(conn, tx, new SyncIdentitySupersession
            {
                SupersessionId = Guid.NewGuid().ToString("N"),
                EntityType = nameof(SyncEntityType.Game),
                SupersededGlobalId = member,
                CanonicalGlobalId = canonical,
                DeviceId = deviceId,
                Reason = reason,
                CreatedAtUtc = now
            });

            changed.Add((member, canonical));
        }

        // 🛡️ canonical 永远不能被 superseded（这是"反向映射/环"在存储层的最后一道防线）：
        //    任何指向别处的 canonical 行都必须删除 —— 归一化后的映射必然是**星形**，结构上无环。
        var removedDeviant = conn.Execute(
            """
            DELETE FROM sync_identity_supersessions
            WHERE entity_type = @type AND superseded_global_id = @canonical;
            """,
            new { type = nameof(SyncEntityType.Game), canonical }, tx);

        if (removedDeviant > 0)
        {
            AppLog.Warn($"[设备同步] 身份归一化：删除 {removedDeviant} 条把 canonical（{canonical}）指向别处的非法映射");
        }

        return new GameIdentityLinkOutcome(canonical, members, changed);
    }

    /// <summary>
    /// 把"新建/改写的身份映射"入队传播（<see cref="SyncEntityType.GameIdentity"/>）。
    /// 用**确定性 queue_id**（<c>identity-map:{superseded}:{canonical}</c>）⇒ 重试/重放天然幂等。
    /// </summary>
    internal static int EnqueueIdentityMappingsCore(
        SqliteConnection conn, SqliteTransaction? tx,
        IReadOnlyList<(string Superseded, string Canonical)> mappings, string reason)
    {
        var queued = 0;

        foreach (var (superseded, canonical) in mappings)
        {
            if (superseded.Length == 0 || canonical.Length == 0) continue;
            if (string.Equals(superseded, canonical, StringComparison.Ordinal)) continue;

            var queueId = $"identity-map:{superseded}:{canonical}";
            var exists = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sync_queue WHERE queue_id = @id;", new { id = queueId }, tx) > 0;

            if (exists) continue;   // Pending / InFlight / Completed 都算"已经入过队"

            var item = new SyncQueueItem
            {
                QueueId = queueId,
                EntityType = nameof(SyncEntityType.GameIdentity),
                EntityGlobalId = superseded,
                Operation = SyncOperation.Create,
                BaseVersion = 1,
                Payload = GameIdentityPayloads.Create(superseded, canonical, reason),
                CreatedAtUtc = UtcNowIso(),
                Status = SyncQueueStatus.Pending
            };

            // 复用唯一的入队实现（避免两套 INSERT 口径分叉）。
            EnqueueOutboxCoreAsync(conn, tx, item).GetAwaiter().GetResult();
            queued++;
        }

        return queued;
    }

    /// <summary>
    /// 为给定的一组 identity 各入队一条 <c>Delete</c>。
    ///
    /// <para>
    /// <b>为什么需要它（旧版本兼容）</b>：旧版本设备不认识 <see cref="SyncEntityType.GameIdentity"/>，
    /// 也**无法按等价类解析** —— 它只会用"来件的 global_id 精确命中本机行"。若我们只发一条
    /// （canonical 那套 identity 的）Delete，旧设备那行带着**另一个** identity 就归档不掉，
    /// 表现为"界面已删、对端还在"。给等价类里每个 identity 各发一条 Delete 后，
    /// <b>任何版本</b>都能用它自己那套 identity 精确命中 ⇒ 删除同样收敛。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 2026-10-12：传入 <paramref name="deletedAtUtc"/> 会让 <c>queue_id</c> **按删除事件唯一**。
    /// 只用 <c>delete:{identity}</c> 时，"删除 → 重新启用 → **再删除**"的第二次删除会因为
    /// "队列里还留着那条老行"而被静默丢掉 ⇒ 对端永远不知道该再次归档（真机实测的
    /// "反复删除时对端不收敛"）。同一次删除动作重复点击**不会**产生第二行（调用方已有
    /// <c>archivedCount &gt; 0</c> 门控），因此不需要靠 id 去重。
    /// 未传时刻时保持旧的确定性 id（向后兼容）。
    /// </para>
    /// </summary>
    internal static int EnqueueIdentityClassDeletesCore(
        SqliteConnection conn, SqliteTransaction? tx, IEnumerable<string?> identities,
        string name, string platform, string platformId, string executable, string executablePath, string? notionPageId,
        string? deletedAtUtc = null)
    {
        var queued = 0;

        // 同一批 identity 共用同一个删除时刻（与墓碑时间保持一致）。
        var deletedAt = string.IsNullOrWhiteSpace(deletedAtUtc) ? UtcNowIso() : deletedAtUtc!.Trim();

        // ⚠️ 按**删除事件**唯一：只要调用方给了删除时刻（真实用户删除路径都给了），
        //    就用"时刻 + 随机后缀"。**不能只用时刻**：本机时间戳是**秒精度**，
        //    "删除 → 重新启用 → 再删除"若挤在同一秒内会算出同一个 id 而被静默去重
        //    （实测：同一秒内的三次删除只剩一条）。
        //    同一状态重复点击删除不会走到这里（调用方有 archivedCount>0 门控），因此不需要靠 id 去重。
        var perEventId = !string.IsNullOrWhiteSpace(deletedAtUtc);

        foreach (var raw in identities)
        {
            var identity = (raw ?? string.Empty).Trim();
            if (identity.Length == 0) continue;

            var queueId = perEventId
                ? $"delete:{identity}:{deletedAt}:{Guid.NewGuid().ToString("N")[..8]}"
                : $"delete:{identity}";
            var exists = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sync_queue WHERE queue_id = @id;", new { id = queueId }, tx) > 0;

            if (exists) continue;

            var item = new SyncQueueItem
            {
                QueueId = queueId,
                EntityType = nameof(SyncEntityType.Game),
                EntityGlobalId = identity,
                Operation = SyncOperation.Delete,
                BaseVersion = 1,
                // 载荷沿用与 Create 相同的 Game 快照（含 schema_version）；接收端在 Delete 分支
                // 只读 EntityType / EntityGlobalId，不会用 payload 回写任何字段。
                Payload = SyncPayloadFactory.Game(identity, name, platform, platformId,
                    executable, executablePath, notionPageId),
                // 信封时间 = 墓碑时间：接收端用它写本地墓碑的 deleted_at_utc，本机推送守卫也用它
                // 判定"这条删除是否比远端已知墓碑更新"。
                CreatedAtUtc = deletedAt,
                Status = SyncQueueStatus.Pending
            };

            EnqueueOutboxCoreAsync(conn, tx, item).GetAwaiter().GetResult();
            queued++;
        }

        return queued;
    }

    // ==================== 类内本地行折叠 / 归档 ====================

    /// <summary>读取等价类内**本机**尚未归档的游戏行。</summary>
    private static IReadOnlyList<GameReconcileRow> ReadLocalRowsInIdentityClassCore(
        SqliteConnection conn, SqliteTransaction? tx, IReadOnlyList<string> classMembers)
    {
        if (classMembers.Count == 0) return Array.Empty<GameReconcileRow>();

        return conn.Query<GameReconcileRow>(
            $"""
            SELECT * FROM games
            WHERE global_id IN @members AND {GameStatuses.NormalGamePredicate}
            ORDER BY id ASC;
            """,
            new { members = classMembers }, tx).ToList();
    }

    /// <summary>
    /// 落地守卫：类内本地行之间若存在**冻结规则冲突**，返回对应原因码（调用方据此延后，不做任何写入）。
    /// 这保证"新机制不会绕过强键冲突 / Notion 身份冲突"。
    /// </summary>
    private static string? DetectIdentityClassConflict(
        IReadOnlyList<GameReconcileRow> localRows)
    {
        for (var i = 0; i < localRows.Count; i++)
        {
            for (var j = i + 1; j < localRows.Count; j++)
            {
                var a = new GameIdentityFacts(localRows[i].Platform, localRows[i].PlatformId,
                    localRows[i].Executable, localRows[i].ExecutablePath, localRows[i].Name, localRows[i].NotionPageId);
                var b = new GameIdentityFacts(localRows[j].Platform, localRows[j].PlatformId,
                    localRows[j].Executable, localRows[j].ExecutablePath, localRows[j].Name, localRows[j].NotionPageId);

                if (GameIdentityRules.StrongKeyAvailable(a) && GameIdentityRules.StrongKeyAvailable(b)
                    && !GameIdentityRules.StrongKeyEqual(a, b))
                {
                    return IdentityDecisionReasons.StrongKeyConflict;
                }

                if (GameIdentityRules.NotionPageConflicts(a, b))
                {
                    return IdentityDecisionReasons.NotionPageConflict;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 把等价类内的本机重复行折叠成一行：优先保留 <c>global_id == canonical</c> 的行
    /// （这样"规范身份"在本机真实存在，解析与推送都最直接），否则按
    /// 「有 exe 路径 &gt; 有 Notion 页 &gt; id 更小」选一行。事实全部搬到保留行，**不丢不重**。
    /// </summary>
    internal static int FoldIdentityClassRowsCore(
        SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<string> classMembers, string canonicalGlobalId)
    {
        var rows = ReadLocalRowsInIdentityClassCore(conn, tx, classMembers);
        if (rows.Count <= 1) return 0;

        var survivor = rows.FirstOrDefault(r => string.Equals(r.GlobalId, canonicalGlobalId, StringComparison.Ordinal))
                       ?? rows.OrderByDescending(r => !string.IsNullOrWhiteSpace(r.ExecutablePath))
                              .ThenByDescending(r => !string.IsNullOrWhiteSpace(r.NotionPageId))
                              .ThenBy(r => r.Id)
                              .First();

        var folded = 0;
        foreach (var dup in rows.Where(r => r.Id != survivor.Id))
        {
            MigrateGameDataToCanonical(conn, tx, survivor.Id, dup.Id);
            conn.Execute("DELETE FROM games WHERE id = @id;", new { id = dup.Id }, tx);
            folded++;
        }

        if (folded > 0)
        {
            AppLog.Info($"[设备同步] 身份等价类折叠：保留 game_id={survivor.Id}（identity {survivor.GlobalId}），"
                + $"并入/删除 {folded} 行（canonical={canonicalGlobalId}）");
        }

        return folded;
    }

    /// <summary>
    /// 归档（删除）等价类内的**全部**本机行 —— 删除的是"逻辑实体"，不是某一个 identity。
    ///
    /// <para>
    /// ⚠️ 2026-10-12（本机更新意图优先）：远端 Delete **不得无条件覆盖本机较新的重新激活意图**。
    /// 用户删除游戏后未来仍可能重新启动它（<c>GetOrCreateGameAsync</c> 会复用同一身份并重新激活）；
    /// 若此时一条**更早**的远端 Delete 到达并把它归档，游戏就会在"激活 ↔ 归档"之间抖动。
    /// </para>
    ///
    /// <para>
    /// 判据：<c>games.reactivated_at_utc</c>（migration 016，**专用**重新激活意图时间戳）与本次删除
    /// 的时间都用 <c>julianday()</c> 归一化后比较 —— 本机该行**晚于**这次删除被重新激活过，
    /// 即视为"本机有新意图"，跳过归档（墓碑仍由调用方照常写入，因此**远端旧 Create 的复活仍然被挡**）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>不得</b>改回 <c>updated_at</c>：该列被监听循环每 5 秒刷新，会让运行中的游戏永久免疫远端
    /// 删除（详见 <c>LocalIntentWinsSql</c> 的说明）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <paramref name="deletionAtUtc"/> 为空（老调用方 / 无从判定）⇒ 保持既有行为（无条件归档），
    /// 绝不因为缺参数就把删除语义削弱。
    /// </para>
    /// </summary>
    internal static int ArchiveIdentityClassRowsCore(
        SqliteConnection conn, SqliteTransaction? tx, IReadOnlyList<string> classMembers,
        string? deletionAtUtc = null)
    {
        if (classMembers.Count == 0) return 0;

        // 条件片段与远端 Delete 落地的各处兜底**共用同一定义**（SqliteRepository.DeviceSync.cs），
        // 避免"主路径受保护、兜底路径又无条件归档"这类分叉。
        return conn.Execute(
            $"""
            UPDATE games SET status = @archived, updated_at = CURRENT_TIMESTAMP
            WHERE global_id IN @members AND {GameStatuses.NormalGamePredicate}
            {LocalIntentWinsSql};
            """,
            new { archived = GameStatuses.Archived, members = classMembers, deletionAtUtc }, tx);
    }

    /// <summary>给等价类内**每个** identity 写墓碑 —— 旧 Create/Update 的重放因此被确定性地挡下。</summary>
    internal static int WriteIdentityClassTombstonesCore(
        SqliteConnection conn, SqliteTransaction? tx, IReadOnlyList<string> classMembers, string deviceId)
    {
        var written = 0;
        foreach (var member in classMembers)
        {
            if (member.Length == 0) continue;
            AddTombstoneCore(conn, tx, new SyncTombstone
            {
                TombstoneId = Guid.NewGuid().ToString("N"),
                EntityType = nameof(SyncEntityType.Game),
                EntityGlobalId = member,
                DeviceId = deviceId,
                DeletedAtUtc = UtcNowIso(),
                CreatedAtUtc = UtcNowIso()
            });
            written++;
        }

        return written;
    }

    // ==================== 远端身份关联落地 ====================

    /// <summary>
    /// 落地一条远端身份关联（<see cref="SyncEntityType.GameIdentity"/>）：
    /// 复算 canonical → 折叠本机重复行 → 若本机等价类更大则把结果回传（令两端收敛）。
    ///
    /// <para>
    /// 返回 <c>null</c> = 已落地；否则是**延后原因码**（沿用既有 Deferred 台账机制）。
    /// 冻结规则冲突（强键冲突 / Notion 身份冲突）在这里**照样延后**，新机制不绕过它们。
    /// </para>
    /// </summary>
    internal static string? TryApplyRemoteIdentityMapping(
        SqliteConnection conn, SqliteTransaction tx, SyncChange change, out int schemaVersion)
    {
        schemaVersion = 0;

        if (!GameIdentityPayloads.TryParse(change.Payload, out var payload, out schemaVersion, out _))
        {
            return ClassifyParseFailure(schemaVersion);
        }

        var incoming = new[] { payload!.GlobalId, payload.CanonicalGlobalId };

        // ① 先做**只读**闭包 + 冻结规则守卫（冲突时什么都不写，只留痕）。
        var classMembers = ReadIdentityClassCore(conn, tx, incoming);
        var localRows = ReadLocalRowsInIdentityClassCore(conn, tx, classMembers);
        var conflict = DetectIdentityClassConflict(localRows);
        if (conflict is not null)
        {
            AppLog.Warn($"[设备同步] 身份关联 {payload.GlobalId} → {payload.CanonicalGlobalId} 被冻结规则拦下：{conflict}");
            return conflict;
        }

        // ② 归一化等价类（星形映射，canonical = 类内最小 identity）。
        var outcome = EstablishIdentityLinkCore(conn, tx, incoming, SupersessionReasons.RemoteIdentityMapping);

        // ③ 折叠本机重复行（把事实搬到保留行，不丢不重）。
        var folded = FoldIdentityClassRowsCore(conn, tx, outcome.ClassMembers, outcome.CanonicalGlobalId);

        // ④ 如果本机知道的 identity 更多（canonical 因此不同）⇒ 回传复算结果。
        //    这是"一处确认、两端收敛"的关键：单调、幂等、不会 ping-pong（复算结果一致时 changed 为空）。
        var queued = EnqueueIdentityMappingsCore(conn, tx, outcome.MappingsChanged, SupersessionReasons.RemoteIdentityMapping);

        AppLog.Info($"[设备同步] 落地身份关联 {payload.GlobalId} → {outcome.CanonicalGlobalId}"
            + $"（等价类 {outcome.ClassMembers.Count} 个 identity、折叠 {folded} 行、回传 {queued} 条）");
        return null;
    }

    // ==================== 旧数据归一化（migration 014） ====================

    /// <summary>
    /// 对既有身份映射做一次**确定性重写**：把所有连通分量归一成星形（canonical = 类内最小 id），
    /// 删除反向/环状行，并把真正发生变化的映射入队传播。
    ///
    /// <para>只改 <c>sync_identity_supersessions</c> 与 <c>sync_queue</c>，**不触碰任何游玩事实**。</para>
    /// </summary>
    internal static int NormalizeGameIdentityMappingsCore(SqliteConnection conn)
    {
        var mappings = ReadGameIdentityMappingsCore(conn, null);
        if (mappings.Count == 0) return 0;

        var allIds = mappings
            .SelectMany(m => new[] { (m.Superseded ?? string.Empty).Trim(), (m.Canonical ?? string.Empty).Trim() })
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var processed = new HashSet<string>(StringComparer.Ordinal);
        var rewritten = 0;

        foreach (var id in allIds)
        {
            if (processed.Contains(id)) continue;

            var classMembers = ReadIdentityClassCore(conn, null, new[] { id });
            foreach (var member in classMembers) processed.Add(member);

            var outcome = EstablishIdentityLinkCore(conn, null, classMembers,
                SupersessionReasons.StartupDedupDuplicateIdentity);

            rewritten += outcome.MappingsChanged.Count;

            // ⚠️ 入队**整颗星**（而不是只入队 changed）：归一化与入队是两个步骤，
            //    若上一次运行在"映射已写、入队还没跑"时失败（例如同步表尚未就绪 / IO 异常），
            //    第二次运行会发现"映射已经是目标形态 ⇒ changed 为空" ⇒ 永远不会补发 ⇒ 静默丢传播。
            //    整颗星入队靠确定性 queue_id 幂等（已存在的队列行直接跳过），所以重复执行安全。
            EnqueueIdentityMappingsCore(conn, null,
                classMembers
                    .Where(m => !string.Equals(m, outcome.CanonicalGlobalId, StringComparison.Ordinal))
                    .Select(m => (Superseded: m, Canonical: outcome.CanonicalGlobalId))
                    .ToList(),
                SupersessionReasons.StartupDedupDuplicateIdentity);
        }

        return rewritten;
    }
}
