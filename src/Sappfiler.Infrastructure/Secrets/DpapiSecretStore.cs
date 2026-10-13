using System.Security.Cryptography;
using System.Text;
using GameTimeTracker.Core.Interfaces;

namespace GameTimeTracker.Infrastructure.Secrets;

/// <summary>
/// 基于 Windows DPAPI（<see cref="DataProtectionScope.CurrentUser"/>）的最小密钥库。
///
/// <code>
/// 密文文件：{dataDir}/secrets/{sha256(key)[..32]}.bin
/// scope   ：CurrentUser（换机器 / 换用户就解不开 —— 这正是我们要的语义）
/// entropy ：UTF8(key)（防止密文在 key 之间被互换）
/// 写入    ：先写 *.tmp → File.Move(overwrite)（避免半写文件）
/// </code>
///
/// ⚠️ 日志脱敏（硬性）：永不记录密码或密文；读失败只记一句"凭据不可用，请重新输入"。
///
/// ⚠️ 文件名用 key 的哈希而不是 key 本身：key 里可能含设备 id / 反斜杠 / 冒号，
/// 直接当文件名会有合法性问题（尤其 Windows）。
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _directory;

    /// <param name="dataDir">数据目录（<c>AppPaths.DataDir</c>），跟随可移动数据目录。</param>
    public DpapiSecretStore(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("数据目录不能为空。", nameof(dataDir));
        _directory = Path.Combine(dataDir, "secrets");
    }

    /// <summary>密钥文件的目录（测试用）。</summary>
    public string Directory => _directory;

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;

            var cipher = await File.ReadAllBytesAsync(path);
            var plain = ProtectedData.Unprotect(cipher, Entropy(key), DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // 换机器 / 换用户 / 文件损坏 —— 都是"不可用"，不是"崩溃"。
            AppLog.Warn("[凭据] 同步凭据不可用（可能换了机器或用户，或文件损坏），请重新输入");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"[凭据] 读取失败（{ex.GetType().Name}），请重新输入");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string secret)
    {
        System.IO.Directory.CreateDirectory(_directory);

        var path = PathFor(key);
        var temp = path + ".tmp";

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy(key), DataProtectionScope.CurrentUser);

        await File.WriteAllBytesAsync(temp, cipher);
        File.Move(temp, path, overwrite: true);   // 原子替换，避免读到半写文件
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key 不能为空。", nameof(key));

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_directory, hash[..32] + ".bin");
    }

    private static byte[] Entropy(string key) => Encoding.UTF8.GetBytes(key);
}
