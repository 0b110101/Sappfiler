namespace GameTimeTracker.Core.DeviceSync;

/// <summary>
/// 「这个 game 是否具备同步资格」—— **唯一真相**，刻意独立于 SyncEngine。
///
/// 冻结规则（用户 2026-10-03 确认）：
/// <code>
/// SyncableGame = executable 非空 OR executable_path 非空 OR 存在关联 sessions
/// </code>
///
/// 为什么必须只有一处定义：这条规则会被 DB 侧（筛选/回填/将来的推送查询）和
/// 内存侧（启动去重逐行判定）同时用到。两边各写一份 → 早晚漂移 →
/// 会出现"回填时算可同步、推送时算不可同步"这种极难排查的错。
/// <c>DeviceSyncGameIdentityTests</c> 用同一批样本把 SQL 与 C# 判定**钉在一起**。
///
/// 为什么纯幽灵行必须被判为"不可同步"：幽灵行（Notion 拉取产生、无 exe、无 session）
/// 不该进入同步体系 —— 给它 global_id 只会制造无意义的云端墓碑。
/// </summary>
public static class GameSyncEligibility
{
    /// <summary>
    /// DB 侧判定式。直接嵌进 <c>games</c> 的 WHERE（**不加表别名**，与回填/查询保持一致）。
    /// 空串与纯空白都算"没有"——现有 schema 是 <c>NOT NULL</c>，用空串表示缺失。
    /// </summary>
    public const string SqlPredicate =
        "(NULLIF(TRIM(games.executable), '') IS NOT NULL "
        + "OR NULLIF(TRIM(games.executable_path), '') IS NOT NULL "
        + "OR EXISTS (SELECT 1 FROM sessions s WHERE s.game_id = games.id))";

    /// <summary>
    /// 内存侧判定（启动去重逐行用）。语义必须与 <see cref="SqlPredicate"/> **完全一致**。
    /// </summary>
    /// <param name="sessionCount">该游戏关联的会话数；调用方负责先查出来。</param>
    public static bool IsSyncable(string? executable, string? executablePath, int sessionCount)
        => !string.IsNullOrWhiteSpace(executable)
        || !string.IsNullOrWhiteSpace(executablePath)
        || sessionCount > 0;
}
