using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.Interfaces;

/// <summary>
/// 笔记同步提供者规范接口。
/// 每个同步目标（Notion / Obsidian / 思源笔记 等）各自实现此接口，独立负责格式转换与网络/文件通信。
/// </summary>
public interface ISyncProvider
{
    /// <summary>Provider 唯一系统标识："notion" | "obsidian" | "siyuan"</summary>
    string ProviderName { get; }

    /// <summary>面向用户的展示名称，如 "Notion"、"Obsidian"、"思源笔记"</summary>
    string DisplayName { get; }

    /// <summary>当前 Provider 是否已配置完备且开启启用</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 同步一条每日时长记录。必须具备幂等性：
    /// - 远端已有同日同游戏记录 → 增量更新时长
    /// - 远端尚未记录 → 新建记录
    /// </summary>
    Task<SyncResult> SyncDailyAsync(PlaytimeRecord record, GameMappingRecord? mapping);

    /// <summary>
    /// 在远端创建游戏条目/页面/文件（首次绑定或自动创建时使用）。
    /// 返回 remote_id（Notion page_id / Obsidian relative file path / 思源 block_id）。
    /// </summary>
    Task<string> CreateGameEntryAsync(GameRecord game);

    /// <summary>
    /// 同步游戏元数据（总时长、最后游玩等聚合数据）。
    /// Notion / 思源：由 Rollup 自动计算，此方法可空实现 (Task.CompletedTask)。
    /// Obsidian：由 Sappfiler 从 SQLite 预计算后写入 frontmatter 与摘要。
    /// </summary>
    Task SyncGameMetadataAsync(GameRecord game, GameAggregateStats stats);

    /// <summary>
    /// 连接可用性测试（供设置页面「测试连接」按钮调用）
    /// </summary>
    Task<bool> TestConnectionAsync();

    /// <summary>
    /// 获取远端现有游戏候选清单（用于异名映射发现与匹配）。
    /// </summary>
    Task<IReadOnlyList<RemoteGameCandidate>> GetRemoteCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 该 Provider 需要配置的字段定义（供 UI 反射或设置验证）
    /// </summary>
    IReadOnlyList<ProviderConfigField> GetConfigFields();
}

/// <summary>
/// 多后端同步调度中心接口。
/// 负责协调所有启用的 Provider，并行同步、隔离失败，将结果保存至 SQLite。
/// </summary>
public interface ISyncOrchestrator
{
    /// <summary>同步状态变更通知（用于主页与侧边栏提示）</summary>
    event EventHandler<string>? SyncStatusChanged;

    /// <summary>当前注册的所有 Provider 列表</summary>
    IReadOnlyList<ISyncProvider> Providers { get; }

    /// <summary>根据 ProviderName 获取对应的提供者</summary>
    ISyncProvider? GetProvider(string providerName);

    /// <summary>
    /// 同步一条记录至所有已启用的 Provider（并行执行，互相独立）
    /// </summary>
    Task<IReadOnlyDictionary<string, SyncResult>> SyncDailyRecordAsync(PlaytimeRecord record);

    /// <summary>
    /// 同步所有待同步记录至各已启用的 Provider
    /// </summary>
    Task<int> SyncAllPendingAsync();

    /// <summary>
    /// 测试指定 Provider 的连接状态
    /// </summary>
    Task<bool> TestProviderConnectionAsync(string providerName);
}
