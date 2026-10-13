using System.Net;

namespace GameTimeTracker.Infrastructure.WebDav;

/// <summary>
/// WebDAV 协议层异常基类。
///
/// ⚠️ <b>脱敏硬性要求</b>：这些异常的消息里**只允许**出现
/// 「HTTP 方法 + 状态码 + 相对路径」，绝不允许出现：
///   · 账号 / 密码 / 应用密码；
///   · <c>Authorization</c> 头的任何内容；
///   · 含凭据的完整 URL。
/// 相对路径是"同步目录内的路径"（可能含用户自选的目录名），不含主机名与账号。
///
/// 所有异常都只表示"本次操作失败"；何时重试由 <c>DeviceSyncEngine</c> 的退避机制统一决定
/// （协议层刻意**不做内部重试**：坚果云免费档 600 次请求 / 30 分钟，见 2h 设计拍板 v2 §3）。
/// </summary>
public abstract class WebDavException : Exception
{
    protected WebDavException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>401 / 403：凭据或权限问题（用户修正密码后重试即可能恢复）。</summary>
public sealed class WebDavAuthException : WebDavException
{
    public int StatusCode { get; }

    public WebDavAuthException(string method, string path, HttpStatusCode status)
        : base($"{method} 认证/权限失败（HTTP {(int)status}）：{path}")
    {
        StatusCode = (int)status;
    }
}

/// <summary>404：资源（或 404 语境下的父目录）不存在。</summary>
public sealed class WebDavNotFoundException : WebDavException
{
    public int StatusCode { get; }

    public WebDavNotFoundException(string method, string path, HttpStatusCode status, string? reason = null)
        : base($"{method} 未找到（HTTP {(int)status}）{(reason is null ? "" : reason + "：")}{path}")
    {
        StatusCode = (int)status;
    }
}

/// <summary>409 / 412：前置条件冲突（父目录缺失、If-Match 失败等）。**绝不静默吞掉**。</summary>
public sealed class WebDavConflictException : WebDavException
{
    public int StatusCode { get; }

    public WebDavConflictException(string method, string path, HttpStatusCode status, string? reason = null)
        : base($"{method} 冲突（HTTP {(int)status}）{(reason is null ? "" : reason + "：")}{path}")
    {
        StatusCode = (int)status;
    }
}

/// <summary>429：被限流。带上服务端给的 Retry-After（若有），供上层展示/节流参考。</summary>
public sealed class WebDavRateLimitedException : WebDavException
{
    public int StatusCode { get; } = 429;

    /// <summary>服务端建议的等待时长（解析自 Retry-After；无法解析时为 null）。</summary>
    public TimeSpan? RetryAfter { get; }

    public WebDavRateLimitedException(string method, string path, TimeSpan? retryAfter)
        : base($"{method} 被限流（HTTP 429）：{path}"
               + (retryAfter is null ? "" : $"（服务端建议等待 {retryAfter.Value.TotalSeconds:0} 秒）"))
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>5xx：服务端错误。</summary>
public sealed class WebDavServerException : WebDavException
{
    public int StatusCode { get; }

    public WebDavServerException(string method, string path, HttpStatusCode status)
        : base($"{method} 服务端错误（HTTP {(int)status}）：{path}")
    {
        StatusCode = (int)status;
    }
}

/// <summary>传输层失败：超时 / DNS / 连接重置等。不含主机名，避免把环境细节写进日志。</summary>
public sealed class WebDavTransportException : WebDavException
{
    public WebDavTransportException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 协议不符：响应不可解析、不是 multistatus、路径非法、意外状态码等。
/// 出现它意味着"双方对协议的理解不一致"，**不允许当成空数据**（那会伪装成同步成功）。
/// </summary>
public sealed class WebDavProtocolException : WebDavException
{
    public WebDavProtocolException(string message, Exception? inner = null) : base(message, inner) { }
}
