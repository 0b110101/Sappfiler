namespace GameTimeTracker.Core.Interfaces;

/// <summary>
/// 敏感信息（目前只有 WebDAV 应用密码）的存储抽象。
///
/// ⚠️ <b>纪律</b>：
///   · 密码**只**进这里；<c>settings</c> 表只存非敏感项（服务器地址 / 同步目录 / 账号名 / 启用标志）；
///   · 实现必须保证：读不到 / 解不开一律返回 <c>null</c>（**不抛**），让调用方给出"请重新输入凭据"的提示；
///   · 任何日志、异常消息、UI 文案都不得包含密文或明文。
///
/// ⚠️ 与 Provider（Notion）凭据无关：本阶段**不迁移** Notion token。
/// </summary>
public interface ISecretStore
{
    /// <summary>读取密钥。不存在 / 无法解密（换机器、换用户、文件损坏）→ 返回 <c>null</c>。</summary>
    Task<string?> GetAsync(string key);

    /// <summary>写入（覆盖）密钥。</summary>
    Task SetAsync(string key, string secret);

    /// <summary>删除密钥；不存在返回 <c>false</c>（不算错误）。</summary>
    Task<bool> DeleteAsync(string key);
}
