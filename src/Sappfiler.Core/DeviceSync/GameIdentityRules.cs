namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 一个 Game 的**身份事实** —— 判定"是不是同一个游戏"只需要这几个字段。
/// 刻意做成不含本地主键的纯数据，便于把规则做成可以穷举测试的纯函数。
/// </summary>
public sealed record GameIdentityFacts(
    string? Platform = null,
    string? PlatformId = null,
    string? Executable = null,
    string? ExecutablePath = null,
    string? Name = null,
    string? NotionPageId = null);

/// <summary>本地候选行（带本地主键，用于返回"应当并入哪一行"）。</summary>
public sealed record LocalGameCandidate(int LocalId, GameIdentityFacts Facts, bool IsIgnored = false);

/// <summary>判定类别。</summary>
public enum IdentityDecisionKind
{
    /// <summary>命中某一行，允许合并。</summary>
    Matched,

    /// <summary>**明确阻断**（强键冲突 / 命中本地 ignored）—— 这是一个确定性结论，不是"待办"。</summary>
    Blocked,

    /// <summary>数据不足或多候选 —— 留待下次（绝不猜）。</summary>
    Deferred,

    /// <summary>本地一个候选都没有（调用方按正常落地规则处理，不合并）。</summary>
    NoLocalCandidate
}

/// <summary>命中的身份键级别（对应契约里的 A / B / C / D）。</summary>
public enum IdentityMatchLevel
{
    None = 0,
    StrongKey = 1,          // A: platform + platform_id
    ExecutablePath = 2,     // B: executable_path（归一化）
    ExecutableName = 3,     // C: executable 文件名
    NormalizedName = 4      // D: 归一化名称（最保守兜底）
}

/// <summary>判定原因码（写入 Deferred 台账的 <c>resolution</c> 或 supersession 的 <c>reason</c>）。</summary>
public static class IdentityDecisionReasons
{
    /// <summary>命中本地 ignored 行 → 保持 ignored，不复活、不改状态、不自动重映射。</summary>
    public const string LocalIgnored = "local_ignored_protected";

    /// <summary>强键适用且相等 → 命中。</summary>
    public const string StrongKeyEqual = "strong_key_equal";

    /// <summary>
    /// ⚠️ **强键适用但不等 → 阻断**（本次 2e 最关键的规则）。
    /// 绝不允许"不同 Steam AppID + 恰好 exe 名相同"被弱键自动合并从而污染 global_id。
    /// </summary>
    public const string StrongKeyConflict = "strong_key_conflict_blocked";

    public const string ExecutablePathMatch = "executable_path_match";
    public const string ExecutableNameMatch = "executable_name_match";
    public const string NormalizedNameMatch = "normalized_name_match";

    /// <summary>同一级命中多个候选 → 不猜。</summary>
    public const string MultipleCandidates = "multiple_candidates";

    /// <summary>名称兜底时双方绑定了**不同**的 Notion 页面 → 合并会毁掉一个 Provider 身份。</summary>
    public const string NotionPageConflict = "notion_page_conflict";

    public const string NoCandidate = "no_candidate";
}

/// <summary>判定结果。</summary>
public sealed record GameIdentityDecision(
    IdentityDecisionKind Kind,
    IdentityMatchLevel Level,
    int? CanonicalLocalId,
    string Reason)
{
    /// <summary>是否允许把远端身份并入 <see cref="CanonicalLocalId"/> 指向的那一行。</summary>
    public bool AllowsMerge => Kind == IdentityDecisionKind.Matched && CanonicalLocalId is not null;
}

/// <summary>
/// Game 身份判定规则 —— **纯函数，无 IO、无数据库**（因此可以穷举测试）。
///
/// 判定顺序（用户 2026-10-03 冻结）：
/// <code>
/// Ignored（任何键级命中 ignored 行）  → BLOCK
/// A applicable + equal               → MATCH_A
/// A applicable + different           → BLOCK_STRONG_KEY_CONFLICT   ← 绝不继续 B/C/D
/// A not applicable                   → B
/// B unique / B ambiguous / B unavailable → MATCH_B / DEFERRED / C
/// C unique / C ambiguous / C unavailable → MATCH_C / DEFERRED / D
/// D 全部条件满足                      → MATCH_NAME
/// 否则                                → DEFERRED
/// </code>
///
/// **两条必须先记住的区分**：
///   ① 「A 不适用」≠「A 冲突」：前者允许落 B，后者**必须阻断**。
///   ② 只有**强键**冲突才阻断；B/C 不相等只是继续往下一级走
///      （跨设备盘符/目录不同是正常的，不该因此阻断）。
/// </summary>
public static class GameIdentityRules
{
    public static GameIdentityDecision Decide(
        GameIdentityFacts remote,
        IReadOnlyList<LocalGameCandidate> localCandidates,
        Func<string, string>? normalizeName = null)
    {
        var normalize = normalizeName ?? DefaultNormalizeName;

        if (localCandidates.Count == 0)
        {
            return new GameIdentityDecision(
                IdentityDecisionKind.NoLocalCandidate, IdentityMatchLevel.None, null,
                IdentityDecisionReasons.NoCandidate);
        }

        // ── 0. ignored 绝对优先 ─────────────────────────────────────────────
        // 只要**任何一条**本地 ignored 行在某一级身份键上会命中，就一律阻断：
        // 不能被 identity merge / exe match / name match 绕过（用户裁定）。
        if (localCandidates.Any(c => c.IsIgnored && MatchesAtAnyLevel(remote, c.Facts, normalize)))
        {
            return Blocked(IdentityMatchLevel.None, IdentityDecisionReasons.LocalIgnored);
        }

        // ── 1. A 级：强键 platform + platform_id ────────────────────────────
        if (StrongKeyAvailable(remote))
        {
            var strongLocals = localCandidates.Where(c => StrongKeyAvailable(c.Facts)).ToList();

            if (strongLocals.Count > 0)
            {
                var equal = strongLocals.Where(c => StrongKeyEqual(remote, c.Facts)).ToList();

                if (equal.Count == 0)
                {
                    // ⚠️ A 适用且不等 → 阻断，**不得落到 B/C/D**。
                    return Blocked(IdentityMatchLevel.StrongKey, IdentityDecisionReasons.StrongKeyConflict);
                }

                return UniqueOrAmbiguous(equal, IdentityMatchLevel.StrongKey, IdentityDecisionReasons.StrongKeyEqual);
            }
            // 本地没有任何"可用强键"（例如都是 manual / platform_id 是本地行 id）
            // → A 在本机不适用 → 允许落 B
        }

        // ── 2. B 级：可执行文件路径（归一化） ───────────────────────────────
        var bComparable = PathAvailable(remote) && localCandidates.Any(c => PathAvailable(c.Facts));
        if (bComparable)
        {
            var matches = localCandidates
                .Where(c => PathAvailable(c.Facts) && PathEqual(remote, c.Facts))
                .ToList();

            if (matches.Count == 1)
            {
                return Matched(matches[0], IdentityMatchLevel.ExecutablePath,
                    IdentityDecisionReasons.ExecutablePathMatch);
            }
            if (matches.Count > 1)
            {
                return Deferred(IdentityMatchLevel.ExecutablePath, IdentityDecisionReasons.MultipleCandidates);
            }
            // B 可用但都不相等 → 继续到 C（只有强键冲突才阻断）
        }

        // ── 3. C 级：可执行文件名 ──────────────────────────────────────────
        var cComparable = ExeAvailable(remote) && localCandidates.Any(c => ExeAvailable(c.Facts));
        if (cComparable)
        {
            var matches = localCandidates
                .Where(c => ExeAvailable(c.Facts) && ExeEqual(remote, c.Facts))
                .ToList();

            if (matches.Count == 1)
            {
                return Matched(matches[0], IdentityMatchLevel.ExecutableName,
                    IdentityDecisionReasons.ExecutableNameMatch);
            }
            if (matches.Count > 1)
            {
                return Deferred(IdentityMatchLevel.ExecutableName, IdentityDecisionReasons.MultipleCandidates);
            }
        }

        // ── 4. D 级：归一化名称（极保守兜底） ──────────────────────────────
        // 条件（用户冻结）：双方 A/B/C 都不可比 + 名称唯一命中 + Notion Page ID 不冲突
        // + 无 ignored 冲突（第 0 步已处理）+ 无其他候选。
        if (bComparable || cComparable)
        {
            // 曾经有可比的弱键却没对上 → 不允许退到"按名字合并"
            return Deferred(IdentityMatchLevel.NormalizedName, IdentityDecisionReasons.NoCandidate);
        }

        var remoteName = normalize(remote.Name ?? string.Empty);
        if (string.IsNullOrWhiteSpace(remoteName))
        {
            return Deferred(IdentityMatchLevel.NormalizedName, IdentityDecisionReasons.NoCandidate);
        }

        var nameMatches = localCandidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Facts.Name) && normalize(c.Facts.Name!) == remoteName)
            .ToList();

        if (nameMatches.Count != 1)
        {
            return Deferred(IdentityMatchLevel.NormalizedName,
                nameMatches.Count == 0 ? IdentityDecisionReasons.NoCandidate : IdentityDecisionReasons.MultipleCandidates);
        }

        // 双方绑定了**不同**的 Notion 页面 → 按名字合并会毁掉一个 Provider 身份
        if (NotionPageConflicts(remote, nameMatches[0].Facts))
        {
            return Deferred(IdentityMatchLevel.NormalizedName, IdentityDecisionReasons.NotionPageConflict);
        }

        return Matched(nameMatches[0], IdentityMatchLevel.NormalizedName,
            IdentityDecisionReasons.NormalizedNameMatch);
    }

    // ==================== 判定辅助 ====================

    /// <summary>
    /// 简单归一化：去首尾空白、压缩连续空白、转小写。
    /// 调用方应优先传入 <c>GameMatcher.NormalizeTitle</c>（与项目既有匹配口径一致）；
    /// 这里只是"保证纯函数可独立测试"的兜底实现。
    /// </summary>
    public static string DefaultNormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var parts = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).ToLowerInvariant();
    }

    /// <summary>
    /// 强键是否**可用**：platform 非空且不是 <c>manual</c>、platform_id 非空、
    /// 且不疑似"本地行 id / GUID"（长度 8 或 16）。
    ///
    /// 依据：`MainWindow` 在识别不到平台时会把**本地行 id** 写成 platform_id
    /// （`existing.Id.ToString()`），那种 platform_id 跨设备毫无意义；
    /// 长度 8/16 的判据与 <c>DeduplicateGamesAndDailySummaries</c> 评分里的既有启发式一致。
    /// </summary>
    public static bool StrongKeyAvailable(GameIdentityFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.Platform)) return false;
        if (string.Equals(facts.Platform.Trim(), "manual", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(facts.PlatformId)) return false;

        var id = facts.PlatformId.Trim();
        return id.Length != 8 && id.Length != 16;
    }

    public static bool StrongKeyEqual(GameIdentityFacts a, GameIdentityFacts b)
        => string.Equals(a.Platform!.Trim(), b.Platform!.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.PlatformId!.Trim(), b.PlatformId!.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool PathAvailable(GameIdentityFacts facts) => !string.IsNullOrWhiteSpace(facts.ExecutablePath);

    public static bool PathEqual(GameIdentityFacts a, GameIdentityFacts b)
        => string.Equals(NormalizePath(a.ExecutablePath), NormalizePath(b.ExecutablePath), StringComparison.Ordinal);

    public static bool ExeAvailable(GameIdentityFacts facts) => !string.IsNullOrWhiteSpace(facts.Executable);

    public static bool ExeEqual(GameIdentityFacts a, GameIdentityFacts b)
        => string.Equals(NormalizeExeName(a.Executable), NormalizeExeName(b.Executable), StringComparison.Ordinal);

    /// <summary>归一化路径：去空白、统一分隔符为 <c>\</c>、转小写。</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.Trim().Replace('/', '\\').ToLowerInvariant();
    }

    /// <summary>取文件名（去掉目录部分）：兼容 <c>\</c> 与 <c>/</c>。</summary>
    public static string NormalizeExeName(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return string.Empty;

        var value = executable.Trim();
        var separator = value.LastIndexOfAny(new[] { '\\', '/' });
        if (separator >= 0) value = value[(separator + 1)..];

        return value.ToLowerInvariant();
    }

    /// <summary>Notion 页面 ID 是否**明确冲突**（双方都有且不同）。</summary>
    public static bool NotionPageConflicts(GameIdentityFacts a, GameIdentityFacts b)
    {
        var left = NormalizeNotionId(a.NotionPageId);
        var right = NormalizeNotionId(b.NotionPageId);

        if (left.Length == 0 || right.Length == 0) return false;
        return !string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string NormalizeNotionId(string? pageId)
        => string.IsNullOrWhiteSpace(pageId)
            ? string.Empty
            : pageId.Replace("-", string.Empty).Trim().ToLowerInvariant();

    /// <summary>是否在**任意一级**身份键上命中（用于 ignored 的绝对优先判定）。</summary>
    private static bool MatchesAtAnyLevel(GameIdentityFacts remote, GameIdentityFacts local, Func<string, string> normalize)
    {
        if (StrongKeyAvailable(remote) && StrongKeyAvailable(local) && StrongKeyEqual(remote, local)) return true;
        if (PathAvailable(remote) && PathAvailable(local) && PathEqual(remote, local)) return true;
        if (ExeAvailable(remote) && ExeAvailable(local) && ExeEqual(remote, local)) return true;

        var remoteName = normalize(remote.Name ?? string.Empty);
        return remoteName.Length > 0
            && !string.IsNullOrWhiteSpace(local.Name)
            && normalize(local.Name!) == remoteName;
    }

    private static GameIdentityDecision UniqueOrAmbiguous(
        IReadOnlyList<LocalGameCandidate> matches, IdentityMatchLevel level, string reason)
        => matches.Count == 1
            ? Matched(matches[0], level, reason)
            : Deferred(level, IdentityDecisionReasons.MultipleCandidates);

    private static GameIdentityDecision Matched(LocalGameCandidate candidate, IdentityMatchLevel level, string reason)
        => new(IdentityDecisionKind.Matched, level, candidate.LocalId, reason);

    private static GameIdentityDecision Blocked(IdentityMatchLevel level, string reason)
        => new(IdentityDecisionKind.Blocked, level, null, reason);

    private static GameIdentityDecision Deferred(IdentityMatchLevel level, string reason)
        => new(IdentityDecisionKind.Deferred, level, null, reason);
}
