using System.Net;
using System.Text;
using System.Xml.Linq;

namespace GameTimeTracker.Infrastructure.WebDav;

/// <summary>
/// 基于 <see cref="HttpClient"/> 的标准 WebDAV 协议实现（Basic 认证）。
///
/// <b>有意不做的事</b>：
///   · <b>不做内部重试</b>：坚果云免费档只有 600 次请求 / 30 分钟，
///     重试由 <c>DeviceSyncEngine</c> 的退避统一负责（2h 设计拍板 v2 §3 / §9）。
///     唯一例外是 PUT 遇到"父目录缺失"时补建目录后重试一次 —— 那是补齐前置条件，不是重试失败请求。
///   · 不使用 DELETE / LOCK / MOVE / COPY / PROPPATCH（坚果云官方文档未承诺这些能力）。
///   · 不跟随 3xx 跳转：跟随跳转可能把 <c>Authorization</c> 头带到别的源上去。
///   · 不做任何同步语义判断（不知道 manifest / batch / cursor 是什么）。
///
/// <b>凭据处理</b>：账号与密码只在构造时被拼成一次 Basic 头存于内存，**不写日志、不进异常消息、不进 URL**。
/// </summary>
public sealed class WebDavClient : IWebDavClient, IDisposable
{
    /// <summary>默认单请求超时（与既有 NotionClient 的 20 秒保持一致）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>默认同步目录名（用户可改）。</summary>
    public const string DefaultSyncDirectory = "Sappfiler";

    private const string PropFindBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:propfind xmlns:D="DAV:">
          <D:prop>
            <D:resourcetype/>
            <D:getcontentlength/>
            <D:getetag/>
            <D:getlastmodified/>
          </D:prop>
        </D:propfind>
        """;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _authorizationHeader;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// 本实例已经确保存在的集合（含同步目录自身的各级父目录）。
    /// 用途：MKCOL 的父目录必须先存在，逐级补齐会产生大量"已存在"的重复 MKCOL；
    /// 记住结果后，每个集合在**一个进程内最多只 MKCOL 一次**。
    /// </summary>
    private readonly HashSet<string> _ensuredCollections = new(StringComparer.Ordinal);

    private readonly object _ensuredLock = new();

    /// <param name="serverUrl">
    /// **服务器地址**（用户填写的那一段），如 <c>https://dav.jianguoyun.com/dav/</c>。
    /// 注意不含同步目录 —— 同步目录通过 <paramref name="syncDirectory"/> 单独给出。
    /// </param>
    /// <param name="userName">账号（如注册邮箱）。</param>
    /// <param name="password">应用密码。**只在这里出现一次**，之后只以 Basic 头形式存在于内存。</param>
    /// <param name="syncDirectory">同步目录（相对服务器地址），如 <c>Sappfiler</c>。所有操作都被限制在它之内。</param>
    /// <param name="timeout">单请求超时；null 用 <see cref="DefaultTimeout"/>。</param>
    /// <param name="handler">仅测试注入用；为 null 时自建 handler 并负责释放。</param>
    public WebDavClient(
        string serverUrl,
        string userName,
        string password,
        string syncDirectory = DefaultSyncDirectory,
        TimeSpan? timeout = null,
        HttpMessageHandler? handler = null)
    {
        BaseUrl = NormalizeBaseUrl(serverUrl, out var insecure);
        IsInsecureHttp = insecure;
        SyncRoot = NormalizeSyncDirectory(syncDirectory);

        if (string.IsNullOrEmpty(userName)) throw new ArgumentException("账号不能为空。", nameof(userName));
        if (password is null) throw new ArgumentException("密码不能为 null。", nameof(password));

        _authorizationHeader = "Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{userName}:{password}"));

        _timeout = timeout ?? DefaultTimeout;

        _ownsHttpClient = handler is null;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            // 不跟随跳转：避免把 Authorization 头带到另一个源上。
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }, disposeHandler: _ownsHttpClient)
        {
            // 超时由每次请求的 linked CTS 控制，这样能把"调用方取消"与"超时"区分开。
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc />
    public string BaseUrl { get; }

    /// <inheritdoc />
    public string SyncRoot { get; }

    /// <inheritdoc />
    public bool IsInsecureHttp { get; }

    // ==================== OPTIONS ====================

    /// <inheritdoc />
    public async Task<WebDavCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(new HttpMethod("OPTIONS"), new Uri(BaseUrl));
        req.Headers.TryAddWithoutValidation("Authorization", _authorizationHeader);

        var res = await SendAsync(req, "OPTIONS", string.Empty, cancellationToken);

        if (res.Status is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
        {
            ThrowForStatus("OPTIONS", string.Empty, res);
        }

        var dav = res.DavHeader;
        var allow = res.AllowHeader?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray() ?? Array.Empty<string>();

        return new WebDavCapabilities(dav, allow);
    }

    // ==================== PROPFIND ====================

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebDavResource>> ListAsync(
        string relativePath = "",
        WebDavDepth depth = WebDavDepth.One,
        CancellationToken cancellationToken = default)
    {
        var rel = NormalizeRelativePath(relativePath);

        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), BuildUri(rel));
        req.Headers.TryAddWithoutValidation("Authorization", _authorizationHeader);
        req.Headers.TryAddWithoutValidation("Depth", depth == WebDavDepth.Zero ? "0" : "1");
        req.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");

        var res = await SendAsync(req, "PROPFIND", rel, cancellationToken);

        // 路径不存在 —— 三种表现都表示"没有"，不是错误：
        //   · 404：RFC 教科书语义（资源不存在）；
        //   · 207 空结果：部分服务端对不存在路径也给空 multistatus；
        //   · **409：坚果云实测行为** —— 当**祖先目录不存在**时，PROPFIND 返回 409 而不是 404。
        //     （2026-10-04 真机验证发现：只认 404 会让"首次启用时探测叶子目录"直接失败，
        //      这是全新账户第一次启用就崩的路径。读操作用 409 表达"不存在"是安全的 ——
        //      PROPFIND 本身不会产生真正的冲突。）
        if (res.Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            return Array.Empty<WebDavResource>();
        }

        if (res.Status != (HttpStatusCode)207 && res.Status != HttpStatusCode.OK)
        {
            ThrowForStatus("PROPFIND", rel, res);
        }

        return ParseMultiStatus(res.Body, rel);
    }

    // ==================== GET ====================

    /// <inheritdoc />
    public async Task<WebDavFile?> TryGetAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var rel = NormalizeRelativePath(relativePath);

        using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(rel));
        req.Headers.TryAddWithoutValidation("Authorization", _authorizationHeader);

        var res = await SendAsync(req, "GET", rel, cancellationToken);

        // 不存在是正常状态。与 PROPFIND 同理：坚果云在"祖先目录不存在"时对 GET 也回 **409**
        // （真机实测），因此这里同样把 409 视为"文件不存在"。
        if (res.Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return null;

        if (res.Status != HttpStatusCode.OK)
        {
            ThrowForStatus("GET", rel, res);
        }

        return new WebDavFile(res.Body, res.ETag, res.LastModifiedUtc);
    }

    // ==================== 二进制 Blob（内容寻址的封面/图标） ====================

    /// <inheritdoc />
    public async Task<byte[]?> TryGetBytesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var rel = NormalizeRelativePath(relativePath);

        using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(rel));
        req.Headers.TryAddWithoutValidation("Authorization", _authorizationHeader);

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token);

            // 与 TryGetAsync 同理：不存在是正常状态（坚果云对"祖先目录不存在"的 GET 也回 409）。
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return null;

            if (res.StatusCode != HttpStatusCode.OK)
            {
                var body = await res.Content.ReadAsStringAsync(linked.Token);
                ThrowForStatus("GET", rel, new SendResult(
                    res.StatusCode, body, res.Headers.ETag?.Tag, res.Content.Headers.LastModified,
                    GetHeader(res, "DAV"), GetHeader(res, "Allow"), ParseRetryAfter(res)));
            }

            return await res.Content.ReadAsByteArrayAsync(linked.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new WebDavTransportException($"GET 超时（{_timeout.TotalSeconds:0} 秒）：{rel}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new WebDavTransportException($"GET 网络失败（{ex.GetType().Name}）：{rel}", ex);
        }
    }

    /// <inheritdoc />
    public async Task PutBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken = default)
    {
        var rel = NormalizeRelativePath(relativePath);

        var res = await PutBytesOnceAsync(rel, content, cancellationToken);

        // 父目录缺失 → 补建后重试一次（与文本 PUT 同一策略，有界）。
        if (res.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            var parent = ParentOf(rel);
            if (parent.Length > 0)
            {
                await EnsureCollectionAsync(parent, cancellationToken);
                res = await PutBytesOnceAsync(rel, content, cancellationToken);
            }
        }

        if (res.Status is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent))
        {
            ThrowForStatus("PUT", rel, res);
        }
    }

    private async Task<SendResult> PutBytesOnceAsync(string rel, byte[] content, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, BuildUri(rel));
        req.Headers.TryAddWithoutValidation("Authorization", _authorizationHeader);

        var body = new ByteArrayContent(content);
        // 不做 Base64、不做 JSON 包装：blob 就是原始字节（内容寻址，SHA-256 即身份）。
        body.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        req.Content = body;

        return await SendAsync(req, "PUT", rel, cancellationToken);
    }

    // ==================== PUT ====================

    /// <inheritdoc />
    public async Task PutAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var rel = NormalizeRelativePath(relativePath);

        var res = await PutOnceAsync(rel, content, cancellationToken);

        // 父目录缺失 → 补齐前置条件后重试**一次**。
        // 两种状态都可能是这个意思：409（RFC 4918 的规范答案）与 404（部分服务端/反向代理这样回）。
        // 这不是"重试失败请求"，而是把缺失的目录建出来；若父目录本身也建不出来，
        // EnsureCollectionAsync 会抛出明确异常（有界：只补一次，绝不无限尝试）。
        if (res.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            var parent = ParentOf(rel);
            if (parent.Length > 0)
            {
                await EnsureCollectionAsync(parent, cancellationToken);
                res = await PutOnceAsync(rel, content, cancellationToken);
            }
        }

        if (res.Status is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent))
        {
            // 403 / 405 / 409 / 404 在真实服务端上很常见的含义是**"这个位置不允许写文件"**
            // （Koofr 真机实测：账号根只能读，必须指定到某个可写挂载点）。
            // 这类情况下只报一个状态码会让用户对着 405 猜，因此在失败路径上补一次 OPTIONS，
            // 把服务端自报的 DAV / Allow 写进错误信息（正常路径 0 次额外请求）。
            if (res.Status is HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                throw new WebDavProtocolException(
                    $"PUT 返回 HTTP {(int)res.Status}，无法写入：{rel}"
                    + "（其父目录已确保存在，因此该位置不允许写入）"
                    + await DescribeCapabilitiesAsync("PUT", cancellationToken));
            }

            ThrowForStatus("PUT", rel, res);
        }
    }

    private async Task<SendResult> PutOnceAsync(string rel, string content, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, BuildUri(rel));
        req.Headers.TryAddWithoutValidation("Authorization", _authorizationHeader);
        // 远端对象一律是 JSON（manifest / batch / protocol）；不写 BOM。
        req.Content = new StringContent(content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "application/json");

        return await SendAsync(req, "PUT", rel, cancellationToken);
    }

    // ==================== MKCOL ====================

    /// <inheritdoc />
    public async Task<bool> EnsureCollectionAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        // ⚠️ 通用兼容性（Koofr 真机实测暴露）：RFC 4918 要求 MKCOL 的**父目录必须已存在**，
        // 严格实现的服务端（Koofr）确实会返回 409；而坚果云会**隐式创建**中间目录，
        // 所以只测一家服务商时这个问题不会暴露。用户可以把同步目录配成多段（如 "资料/Sappfiler"），
        // 因此这里**从最外层开始逐级补齐**（含同步目录自身的各级父目录，再进入目录内部）。
        var rel = NormalizeRelativePath(relativePath, allowSyncRootAncestors: true);

        var createdAny = false;

        foreach (var prefix in PrefixesOf(rel))
        {
            lock (_ensuredLock)
            {
                if (_ensuredCollections.Contains(prefix)) continue;   // 本进程已确保过 → 不再重复请求
            }

            if (await EnsureSingleCollectionAsync(prefix, cancellationToken)) createdAny = true;

            lock (_ensuredLock)
            {
                _ensuredCollections.Add(prefix);
            }
        }

        return createdAny;
    }

    /// <summary>从 <c>a</c> → <c>a/b</c> → <c>a/b/c</c> 的所有前缀（含自身），用于逐级补齐父目录。</summary>
    private static IEnumerable<string> PrefixesOf(string rel)
    {
        var segments = rel.Split('/');

        for (var i = 1; i <= segments.Length; i++)
        {
            yield return string.Join('/', segments.Take(i));
        }
    }

    /// <summary>
    /// MKCOL 单个集合。调用方（<see cref="EnsureCollectionAsync"/>）保证**父目录已先被确保**，
    /// 因此这里遇到 409/404 时可以给出准确的诊断信息，而不是笼统地猜"父目录可能不存在"。
    /// </summary>
    private async Task<bool> EnsureSingleCollectionAsync(string path, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), BuildUri(path));
        req.Headers.TryAddWithoutValidation("Authorization", _authorizationHeader);

        var res = await SendAsync(req, "MKCOL", path, cancellationToken);

        if (res.Status == HttpStatusCode.Created) return true;
        if (res.Status is HttpStatusCode.OK or HttpStatusCode.NoContent) return false;

        // 405 / 409 / 404 都是**有歧义**的：RFC 里 405 表示"已存在"，409 表示"父目录缺失"，
        // 但真实服务端会混用（也有用 405 表达"不支持 MKCOL"、用 409 表达"已存在"的）——
        // 按 2h 设计拍板 v2"冲突不得简单吞掉"的要求，这里用一次 PROPFIND 把歧义消掉。
        if (res.Status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            if (IsInsideSyncRoot(path))
            {
                var existing = await ListAsync(path, WebDavDepth.Zero, cancellationToken);
                if (existing.Any(r => r.IsCollection && PathEquals(r.Path, path))) return false;
            }
            else if (res.Status == HttpStatusCode.MethodNotAllowed)
            {
                // 同步目录自身的各级父目录：不允许对它们发 PROPFIND（越界读取被刻意禁止）。
                // 父目录链是被**逐级**确保的，因此这条路径上 405 唯一合理的解释就是"已存在"。
                return false;
            }

            throw new WebDavProtocolException(
                $"MKCOL 返回 HTTP {(int)res.Status}，无法创建集合：{path}"
                + "（其父目录已确保存在，因此该位置不允许创建目录）"
                + await DescribeCapabilitiesAsync("MKCOL", cancellationToken));
        }

        ThrowForStatus("MKCOL", path, res);
        return false;   // 不可达（ThrowForStatus 必抛）
    }

    /// <summary>
    /// **失败路径专用**：补一次 OPTIONS，把服务端**自报**的能力写进错误信息，让错误可诊断。
    ///
    /// 理由（Koofr 真机实测）：同一个服务商的不同**位置**能力不同 —— 账号根（`/dav/`）只能读，
    /// 必须指定到可写目录/挂载点（`/dav/Koofr`）才能创建。只报一个 HTTP 状态码会让用户
    /// 对着 405 猜半天；带上 <c>DAV</c> / <c>Allow</c> 就能一眼定位。
    /// 探测自身失败绝不影响主错误（返回空串）。正常路径 0 次额外请求。
    /// </summary>
    private async Task<string> DescribeCapabilitiesAsync(string requiredMethod, CancellationToken cancellationToken)
    {
        try
        {
            var caps = await ProbeAsync(cancellationToken);
            var allow = caps.AllowedMethods.Count > 0 ? string.Join(" ", caps.AllowedMethods) : "（未返回）";

            var hint = caps.SupportsMethod(requiredMethod)
                ? $"（Allow 里**列了** {requiredMethod}，说明限制来自这个具体位置而不是方法本身）"
                : $"（Allow 里**没有** {requiredMethod}）";

            return $"。服务端 OPTIONS 自报：DAV={caps.DavHeader ?? "（未返回）"}；Allow={allow}；{hint}";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>路径是否落在同步目录之内（<c>SyncRoot</c> 自身或其后代）。</summary>
    private bool IsInsideSyncRoot(string path)
        => path.Equals(SyncRoot, StringComparison.Ordinal)
           || path.StartsWith(SyncRoot + "/", StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task EnsureCollectionChainAsync(
        IReadOnlyList<string> relativePathsInOrder, CancellationToken cancellationToken = default)
    {
        foreach (var path in relativePathsInOrder)
        {
            await EnsureCollectionAsync(path, cancellationToken);
        }
    }

    // ==================== 内部：发送与解析 ====================

    private async Task<SendResult> SendAsync(
        HttpRequestMessage req, string method, string path, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token);
            var body = await res.Content.ReadAsStringAsync(linked.Token);

            return new SendResult(
                res.StatusCode,
                body,
                res.Headers.ETag?.Tag,
                res.Content.Headers.LastModified,
                // DAV / Allow 都可能被 .NET 归到任一侧（Allow 在 BCL 里被当作 **content** 头），
                // 所以两边都要看，否则真实服务端的 Allow 会静默读成空。
                GetHeader(res, "DAV"),
                GetHeader(res, "Allow"),
                ParseRetryAfter(res));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // 调用方主动取消：原样传播，不当成网络故障
        }
        catch (OperationCanceledException ex)
        {
            throw new WebDavTransportException($"{method} 超时（{_timeout.TotalSeconds:0} 秒）：{path}", ex);
        }
        catch (HttpRequestException ex)
        {
            // 不含凭据：凭据从不进 URL，也从不在消息里。
            throw new WebDavTransportException($"{method} 网络失败（{ex.GetType().Name}）：{path}", ex);
        }
    }

    /// <summary>
    /// 在"响应头"和"内容头"两侧查找同名头，返回逗号拼接的值（找不到返回 null）。
    /// 必须两侧都查：<c>Allow</c> 在 BCL 的头分类里属于 content 头，
    /// 只查 <c>res.Headers</c> 会在真实服务端上静默拿到空值。
    /// </summary>
    private static string? GetHeader(HttpResponseMessage res, string name)
    {
        var parts = new List<string>();

        if (res.Headers.NonValidated.TryGetValues(name, out var inResponse))
        {
            parts.AddRange(inResponse.Select(v => v.ToString()));
        }

        if (res.Content.Headers.NonValidated.TryGetValues(name, out var inContent))
        {
            parts.AddRange(inContent.Select(v => v.ToString()));
        }

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage res)
    {
        var raw = res.Headers.RetryAfter;
        if (raw is null) return null;
        if (raw.Delta is { } delta) return delta;
        if (raw.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static void ThrowForStatus(string method, string path, SendResult res)
    {
        switch (res.Status)
        {
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                throw new WebDavAuthException(method, path, res.Status);

            case HttpStatusCode.NotFound:
                throw new WebDavNotFoundException(method, path, res.Status);

            case (HttpStatusCode)429:
                throw new WebDavRateLimitedException(method, path, res.RetryAfter);

            case HttpStatusCode.Conflict or (HttpStatusCode)412:
                throw new WebDavConflictException(method, path, res.Status);

            default:
                if ((int)res.Status >= 500) throw new WebDavServerException(method, path, res.Status);
                throw new WebDavProtocolException($"{method} 出现意外状态码 HTTP {(int)res.Status}：{path}");
        }
    }

    /// <summary>
    /// 解析 207 Multi-Status。
    /// 用 LocalName 匹配而不是完整命名空间匹配：真实服务端的 DAV 命名空间前缀与属性齐全度差异很大，
    /// 硬按命名空间匹配会在一些服务端上静默返回空列表（那比抛错更危险）。
    /// </summary>
    private IReadOnlyList<WebDavResource> ParseMultiStatus(string xml, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<WebDavResource>();

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new WebDavProtocolException($"PROPFIND 响应不是合法 XML：{requestedPath}", ex);
        }

        var root = doc.Root;
        if (root is null || !NameIs(root, "multistatus"))
        {
            throw new WebDavProtocolException($"PROPFIND 响应不是 multistatus：{requestedPath}");
        }

        var result = new List<WebDavResource>();

        foreach (var response in Elements(root, "response"))
        {
            var hrefRaw = Elements(response, "href").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(hrefRaw)) continue;

            var isCollection = false;
            long? contentLength = null;
            string? etag = null;
            DateTimeOffset? lastModified = null;

            // 只认"成功"的那一份 propstat（失败属性的 propstat 里没有我们要的字段）。
            foreach (var propstat in Elements(response, "propstat"))
            {
                if (!PropStatIsSuccess(propstat)) continue;

                foreach (var prop in Elements(propstat, "prop"))
                {
                    foreach (var el in prop.Elements())
                    {
                        if (NameIs(el, "resourcetype"))
                        {
                            isCollection = el.Elements().Any(c => NameIs(c, "collection"));
                        }
                        else if (NameIs(el, "getcontentlength") && long.TryParse(el.Value.Trim(), out var len))
                        {
                            contentLength = len;
                        }
                        else if (NameIs(el, "getetag"))
                        {
                            var v = el.Value.Trim();
                            if (v.Length > 0) etag = v;
                        }
                        else if (NameIs(el, "getlastmodified"))
                        {
                            lastModified = ParseHttpDate(el.Value.Trim());
                        }
                    }
                }
            }

            result.Add(new WebDavResource
            {
                Href = hrefRaw.Trim(),
                Path = HrefToRelativePath(hrefRaw),
                IsCollection = isCollection,
                ContentLength = contentLength,
                ETag = etag,
                LastModifiedUtc = lastModified
            });
        }

        return result;
    }

    private static bool PropStatIsSuccess(XElement propstat)
    {
        var status = Elements(propstat, "status").FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(status)) return false;
        // 形如 "HTTP/1.1 200 OK"
        return status.Contains(" 200 ", StringComparison.Ordinal)
            || status.Contains(" 200", StringComparison.Ordinal)
            || status.Contains(" 207", StringComparison.Ordinal);
    }

    private static bool NameIs(XElement el, string localName)
        => string.Equals(el.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<XElement> Elements(XElement parent, string localName)
        => parent.Elements().Where(e => NameIs(e, localName));

    private static DateTimeOffset? ParseHttpDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : null;
    }

    /// <summary>把服务端给的 href 归一化成"相对同步根"的路径（已解码、无首尾斜杠）。</summary>
    private string HrefToRelativePath(string href)
    {
        var value = href.Trim();

        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out var abs))
        {
            path = abs.AbsolutePath;
        }
        else
        {
            var q = value.IndexOfAny(new[] { '?', '#' });
            path = q >= 0 ? value[..q] : value;
        }

        path = Uri.UnescapeDataString(path);

        var basePath = new Uri(BaseUrl).AbsolutePath;
        if (path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            path = path[basePath.Length..];
        }

        return path.Trim('/');
    }

    // ==================== 路径处理 ====================

    /// <summary>同步目录自身的规范化（不允许嵌套、不允许空、不允许 <c>..</c>）。</summary>
    private static string NormalizeSyncDirectory(string syncDirectory)
    {
        var p = (syncDirectory ?? string.Empty).Trim().Trim('/');

        if (p.Length == 0)
            throw new ArgumentException("同步目录不能为空。", nameof(syncDirectory));

        if (p.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("同步目录不能是绝对 URL。", nameof(syncDirectory));

        foreach (var seg in p.Split('/'))
        {
            if (seg.Length == 0 || seg == "." || seg == "..")
                throw new ArgumentException($"同步目录含非法段：{syncDirectory}", nameof(syncDirectory));
        }

        return p;
    }

    /// <summary>
    /// 规范化 + **越界防护**：拒绝绝对 URL、<c>..</c>、查询串，
    /// 并要求路径必须落在 <see cref="SyncRoot"/> 之内（见接口注释里的理由）。
    /// </summary>
    /// <param name="allowSyncRootAncestors">
    /// 是否允许"同步目录自身的各级父目录"。**只有 MKCOL 会传 true**：
    /// RFC 4918 要求 MKCOL 的父目录已存在，而用户可以把同步目录配成多段路径，
    /// 因此必须先把这些父目录建出来才能使用该目录（严格实现的服务端否则直接 409）。
    /// 读写（GET/PUT/PROPFIND）永远传 false —— 它们严格限制在同步目录之内。
    /// </param>
    private string NormalizeRelativePath(string relativePath, bool allowSyncRootAncestors = false)
    {
        var p = (relativePath ?? string.Empty).Trim();

        if (p.Contains("://", StringComparison.Ordinal))
            throw new WebDavProtocolException("路径不能是绝对 URL（只接受相对同步目录的路径）。");

        if (p.Contains('?') || p.Contains('#'))
            throw new WebDavProtocolException("路径不能包含 ? 或 #。");

        p = p.Trim('/');

        if (p.Length == 0)
            throw new WebDavProtocolException($"路径不能为空（至少要以同步目录 {SyncRoot} 开头）。");

        foreach (var seg in p.Split('/'))
        {
            if (seg.Length == 0)
                throw new WebDavProtocolException("路径包含空目录段（可能有多余的 /）。");
            if (seg == "." || seg == "..")
                throw new WebDavProtocolException("路径不能包含 . 或 ..（不允许越出同步目录）。");
        }

        // 允许集合：同步目录自身、其后代；以及（**仅 MKCOL**）同步目录自身的各级父目录。
        var ancestorOfSyncRoot = allowSyncRootAncestors
                                 && SyncRoot.StartsWith(p + "/", StringComparison.Ordinal);

        if (!ancestorOfSyncRoot && !IsInsideSyncRoot(p))
        {
            throw new WebDavProtocolException(
                $"路径必须位于同步目录 {SyncRoot}/ 之内，拒绝越界访问：{p}");
        }

        return p;
    }

    private static string ParentOf(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i <= 0 ? string.Empty : rel[..i];
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(a.Trim('/'), b.Trim('/'), StringComparison.Ordinal);

    private Uri BuildUri(string rel)
    {
        if (rel.Length == 0) return new Uri(BaseUrl);

        var encoded = string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
        return new Uri(BaseUrl + encoded);
    }

    private static string NormalizeBaseUrl(string baseUrl, out bool insecure)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("服务器地址不能为空。", nameof(baseUrl));

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("服务器地址不是合法 URL。", nameof(baseUrl));

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("服务器地址必须以 http:// 或 https:// 开头。", nameof(baseUrl));

        insecure = uri.Scheme == Uri.UriSchemeHttp;

        var normalized = uri.GetLeftPart(UriPartial.Path);
        if (!normalized.EndsWith('/')) normalized += "/";
        return normalized;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    private readonly record struct SendResult(
        HttpStatusCode Status,
        string Body,
        string? ETag,
        DateTimeOffset? LastModifiedUtc,
        string? DavHeader,
        string? AllowHeader,
        TimeSpan? RetryAfter);
}
