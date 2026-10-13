using System.Security.Cryptography;

namespace GameTimeTracker.Core.Covers;

/// <summary>
/// 封面文件的**内容级**探测：哈希按字节算，content type 按**文件头 magic bytes** 判。
/// </summary>
/// <remarks>
/// 为什么不能按扩展名判 content type（2026-10-06 实测）：
/// 本地封面缓存里存在扩展名是 <c>.jpg</c>、内容实际是 **PNG** 的文件
/// （实测首 3 字节 = <c>89 50 4E</c>）。按扩展名声明 <c>image/jpeg</c> 属于谎报，
/// 所以这里一律看真正的字节。
/// </remarks>
public static class CoverFileProbe
{
    /// <summary>文件存在且非空 —— 只有这种封面才允许参与上传。</summary>
    public static bool IsUsable(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path!).Length > 0;

    /// <summary>文件内容的 SHA-256（十六进制小写）。内容一变，哈希就变 —— 这是"同图复用"的唯一依据。</summary>
    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>按 magic bytes 判定 content type；无法识别时返回 application/octet-stream（不猜）。</summary>
    public static string DetectContentType(string path)
    {
        Span<byte> head = stackalloc byte[12];
        int read;
        using (var stream = File.OpenRead(path))
        {
            read = stream.Read(head);
        }

        if (read >= 8 &&
            head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 &&
            head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
        {
            return "image/png";
        }

        if (read >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "image/jpeg";
        if (read >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46) return "image/gif";
        if (read >= 12 &&
            head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 &&
            head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
        {
            return "image/webp";
        }

        if (read >= 2 && head[0] == 0x42 && head[1] == 0x4D) return "image/bmp";

        return "application/octet-stream";
    }

    /// <summary>由 content type 推出建议扩展名（用于上传时的文件名，避免扩展名与内容不符）。</summary>
    public static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".bin"
    };
}
