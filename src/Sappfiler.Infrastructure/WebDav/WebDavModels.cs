namespace GameTimeTracker.Infrastructure.WebDav;

/// <summary>
/// PROPFIND 的 Depth。
///
/// 只提供 0 / 1，**不提供 infinity**：多数服务端禁用 infinity，
/// 而我们也不需要 —— 远端布局刻意做成"每个目录都很小"（见 2h 设计拍板 v2 §A）。
/// </summary>
public enum WebDavDepth
{
    /// <summary>只问资源自己。</summary>
    Zero = 0,

    /// <summary>资源自己 + 直接子项（用于列设备目录）。</summary>
    One = 1
}

/// <summary>
/// 一次 PROPFIND 返回的资源条目。**纯协议字段，不含任何同步语义**
/// （不知道 manifest / batch / change / cursor 是什么）。
/// </summary>
public sealed record WebDavResource
{
    /// <summary>服务端返回的原始 href（可能是绝对 URL 或相对路径，且**仍带百分号编码**）。</summary>
    public required string Href { get; init; }

    /// <summary>
    /// 相对 base 的路径，**已解码**、无首尾斜杠、目录同样不带结尾斜杠
    /// （形如 <c>devices/abc123/manifest.json</c>）。
    /// </summary>
    public required string Path { get; init; }

    /// <summary>是否是一个 collection（目录）。</summary>
    public bool IsCollection { get; init; }

    /// <summary>getcontentlength（目录通常没有）。</summary>
    public long? ContentLength { get; init; }

    /// <summary>getetag 原样（含引号，若服务端给了）。仅用于"变了没有"的优化判断，**不参与游标**。</summary>
    public string? ETag { get; init; }

    /// <summary>getlastmodified（若可解析）。同样**不参与游标**。</summary>
    public DateTimeOffset? LastModifiedUtc { get; init; }
}

/// <summary>
/// OPTIONS 的能力探测结果。
///
/// ⚠️ **仅供参考**：很多服务端（含坚果云）的 <c>Allow</c> 头并不完整列出 MKCOL 等方法，
/// 因此**绝不能**用这里的 <see cref="SupportsMethod"/> 去拒绝一次操作 ——
/// 真正的判断标准是"发出去，看状态码"（见 <c>WebDavClient.EnsureCollectionAsync</c> 的 405 处理）。
/// </summary>
public sealed record WebDavCapabilities(string? DavHeader, IReadOnlyList<string> AllowedMethods)
{
    /// <summary>按 <c>Allow</c> 头判断（大小写不敏感）。</summary>
    public bool SupportsMethod(string method)
        => AllowedMethods.Any(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase));

    /// <summary>是否声明了 DAV 类（1 / 2 / 3）。仅信息用途。</summary>
    public bool DeclaresDavClass(string davClass)
        => (DavHeader ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(c => string.Equals(c, davClass, StringComparison.OrdinalIgnoreCase));
}

/// <summary>GET 到的文件内容 + 可选元数据。</summary>
public sealed record WebDavFile(string Content, string? ETag, DateTimeOffset? LastModifiedUtc);
