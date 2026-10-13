namespace GameTimeTracker.Core.Services;

/// <summary>
/// <b>「首次同步」的并发保护与代次闸门</b>（2026-10-13，真机故障修复）。
///
/// <para>
/// 背景：用户在设置页保存 Notion 配置后会触发一条耗时链（刷新总表 → 对账 → 拉取 → 回填 →
/// 推送 → 回刷）。原先这条链**全程禁用保存按钮**，而 Token 无效时链内的逐条回写会重试到分钟级
/// ⇒ 用户既不能解绑也不能重新绑定（真机实测：13+ 条记录各重试 3 次，保存按钮一直灰着）。
/// </para>
///
/// <para>
/// 因此把两件"纯判断"下沉到这里，界面层只管照做：
/// <list type="number">
///   <item><b>并发保护</b>：同一时刻只允许一次首次同步 ——
///         <see cref="TryBegin"/> 返回 <c>null</c> 表示"已有一次在跑，不要重复触发"；</item>
///   <item><b>代次作废</b>：用户**每次保存配置**都会 <see cref="Invalidate"/> ——
///         被作废的那次同步不得再更新界面状态（否则它完成时会盖掉"保存成功"等新提示），
///         也不得再占用名额（用户随后重新绑定可以立刻再启动一次）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 本闸门**不阻塞保存**：修改或清除配置是用户的自由，任何时候都必须能立刻完成。
/// 闸门只回答"要不要为这次保存启动一次首次同步"和"某次同步是否已被取代"，
/// 它没有任何"阻止保存"的语义 —— 调用方也**不得**用 <see cref="IsRunning"/> 去禁用保存按钮。
/// </para>
/// </summary>
public sealed class FirstSyncGate
{
    private int _generation;
    private bool _running;

    /// <summary>当前配置代次（仅供日志与测试观察）。</summary>
    public int Generation => _generation;

    /// <summary>是否已有一次首次同步在跑（**不得**用来禁用保存按钮，只用于避免重复触发）。</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// 保存配置时调用：令**正在跑的**那次首次同步作废，并释放名额。
    /// 作废后它的 <see cref="IsCurrent"/> 变为 <c>false</c>，其 <see cref="End"/> 变成 no-op。
    /// </summary>
    public void Invalidate()
    {
        _generation++;
        _running = false;
    }

    /// <summary>
    /// 尝试开始一次首次同步。已有一次在跑 ⇒ 返回 <c>null</c>（**并发保护**，不重复触发）；
    /// 否则返回本次的代次票据，供 <see cref="IsCurrent"/> / <see cref="End"/> 使用。
    /// </summary>
    public int? TryBegin()
    {
        if (_running) return null;
        _running = true;
        return _generation;
    }

    /// <summary>该代次是否仍是当前那次（未被后续保存作废）⇒ 是否有资格更新界面状态。</summary>
    public bool IsCurrent(int generation) => generation == _generation;

    /// <summary>
    /// 结束该次首次同步。<b>只有它仍是当前那次**且**仍是占用者</b>才真正释放名额；
    /// 已被作废的旧任务调用这里必须是 no-op —— 否则会把新一次同步的名额误清掉，
    /// 让"并发保护"失效。
    /// </summary>
    public bool End(int generation)
    {
        if (!_running || generation != _generation) return false;
        _running = false;
        return true;
    }
}
