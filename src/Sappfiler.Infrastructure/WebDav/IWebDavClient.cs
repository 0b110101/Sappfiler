namespace GameTimeTracker.Infrastructure.WebDav;

/// <summary>
/// 标准 WebDAV 协议操作（**只做协议，不认识同步语义**）。
///
/// 分层（2h 设计拍板 v2 §1）：
/// <code>
/// DeviceSyncEngine → ISyncBackend → WebDavSyncBackend → IWebDavClient → HttpClient
/// </code>
/// <c>WebDavSyncBackend</c> 负责"变更集合 ↔ 远端资源"的翻译；
/// 本接口只负责"把一次 WebDAV 请求发出去、把状态码翻译成异常或结果"。
///
/// <b>只使用 4 个方法</b>：OPTIONS / PROPFIND / GET / PUT / MKCOL。
/// 刻意**不使用** DELETE、LOCK / UNLOCK、MOVE / COPY、PROPPATCH —— 这些能力坚果云官方文档未承诺，
/// 且 v1 的同步设计根本不需要它们（每设备单写者、batch 不可变、不做远端删除）。
///
/// ⚠️ 本接口继承 <see cref="IDisposable"/>：实现持有 <c>HttpClient</c>（连接池），
/// 生命周期由调用方（<c>DeviceSyncService</c>）负责释放。
///
/// 路径约定（**越界防护**）：所有 <c>relativePath</c> 都必须落在同步目录之内，
/// 形如 <c>{SyncRoot}/devices/&lt;deviceId&gt;/batches/&lt;batchId&gt;.json</c>。
/// 绝对 URL、<c>..</c>、<c>.</c>、查询串，以及**不以 <see cref="SyncRoot"/> 开头的路径都会被拒绝** ——
/// 这是刻意的：一个 WebDAV 账号（NAS / Nextcloud）里往往还存着用户其它重要文件，
/// 同步代码绝不能有任何一个路径写到同步目录之外。
/// </summary>
public interface IWebDavClient : IDisposable
{
    /// <summary>服务器地址（用户填写的那一段，已规范化，必定以 "/" 结尾）。不含任何凭据。</summary>
    string BaseUrl { get; }

    /// <summary>同步目录（相对 <see cref="BaseUrl"/> 的路径，如 <c>Sappfiler</c>）。所有操作都被限制在它之内。</summary>
    string SyncRoot { get; }

    /// <summary>是否使用了明文 http://（UI 应提示风险；https 为默认要求）。</summary>
    bool IsInsecureHttp { get; }

    /// <summary>OPTIONS：探测 DAV 能力。用于"测试连接"。失败抛 <see cref="WebDavException"/> 子类。</summary>
    Task<WebDavCapabilities> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// PROPFIND：列出资源。<paramref name="relativePath"/> 为空表示同步根自身。
    /// 路径不存在时返回**空集合**（部分服务端回 404、部分回 207 空结果，两者语义一致）。
    /// 注意：列目录时服务端会在结果里包含被列目录自身（调用方自行过滤）。
    /// </summary>
    Task<IReadOnlyList<WebDavResource>> ListAsync(
        string relativePath = "", WebDavDepth depth = WebDavDepth.One, CancellationToken cancellationToken = default);

    /// <summary>GET：读文件。404 返回 <c>null</c>（"不存在"是正常情况，不是错误）。</summary>
    Task<WebDavFile?> TryGetAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// PUT：整文件写入（UTF-8，无 BOM）。父目录缺失（409）时补建父目录后**重试一次** ——
    /// 这是"补齐前置条件"，不是"重试失败请求"。
    /// </summary>
    Task PutAsync(string relativePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// **二进制** PUT（内容寻址的封面/图标 blob）。
    /// ⚠️ 与 <see cref="PutAsync"/> 一样只使用 <c>PUT</c>，**不引入** DELETE / LOCK / MOVE / PROPPATCH；
    /// 内容寻址 ⇒ 同一路径同一内容重复 PUT 天然幂等（不新增任何远端对象）。
    /// </summary>
    Task PutBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>
    /// **二进制** GET。不存在（404 / 部分服务端的 409）返回 <c>null</c>；其它错误照常抛本层异常。
    /// </summary>
    Task<byte[]?> TryGetBytesAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// MKCOL：确保目录存在。已存在（405 / 409 且经 PROPFIND 确认为目录）视为成功。
    /// 返回 <c>true</c> = 本次真的创建了。
    /// </summary>
    Task<bool> EnsureCollectionAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>按顺序逐级确保目录存在（每级一次 MKCOL）。用于首次启用时建立同步目录骨架。</summary>
    Task EnsureCollectionChainAsync(IReadOnlyList<string> relativePathsInOrder, CancellationToken cancellationToken = default);
}
