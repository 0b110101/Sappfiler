using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;

namespace GameTimeTracker.Infrastructure.Notion;

/// <summary>
/// 每日记录标题的拼装与解析（格式 `{游戏名} · {X} h`）。
/// </summary>
/// <remarks>
/// 单独抽成一个类，而不是塞在 <see cref="NotionClient"/> 里当私有方法，是因为
/// **写**标题的 <see cref="NotionClient"/> 与**规范化**历史标题的
/// <c>NotionSyncService.NormalizeHistoricalDailyTitlesAsync</c>（设置页手工入口）必须共用同一套规则。
/// 两边各留一份实现的话，只要格式有一点漂移（比如改小数位数、换分隔符），
/// 手工规范化就会永远判定"不一致"，把同一批历史记录反复重写。
/// </remarks>
internal static class DailyRecordTitle
{
    /// <summary>
    /// 标题里的时长后缀。三种形态都要认，因为用户的表里混着程序写的和手工写的：
    ///   ① 程序写的新格式  「不思议迷宫 · 0.7 h」
    ///   ② 程序写的旧格式  「不思议迷宫 (42分)」—— Notion 里可能还留着旧行
    ///   ③ **用户手工写的紧贴格式** 「致命躯壳2.2h」「黑旗10.1h」—— 没有分隔符
    /// 从右往左锚定：游戏名自身可能含 · 或 |，不能从左切。
    ///
    /// ⚠️ ③ 是 2026-09-19 补的。原先只认 ①②，于是 "致命躯壳2.2h" 整串被当成游戏名，
    ///    拉取时每条这样的记录都会新建一个**永远绑不上总表**的游戏行 ——
    ///    用户看到的「待处理」堆积和「映射库全是未绑定」就是这么来的。
    ///
    /// 为什么这么写是安全的：数字后面必须紧跟 min/h，所以
    /// "三国志11" / "F1 2023" / "Half-Life 2" 这类以数字结尾的真名不会被误剥。
    /// </summary>
    internal static readonly System.Text.RegularExpressions.Regex SuffixRegex =
        new(@"\s*(?:(?:[·|\-:]\s*)?\d+(?:\.\d+)?\s*(?:min|mins|h|hr|hrs|小时|分钟|分)|\(\s*\d+(?:\.\d+)?\s*(?:分|分钟|h|小时)\s*\))\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 分钟 → 小时（保留 2 位小数）。「单次时长」数值属性与标题后缀共用这一个换算，
    /// 保证两者永远一致。
    /// </summary>
    /// <remarks>
    /// **为什么是 2 位而不是 1 位**（2026-09-19 定）：
    /// 1 位小数的粒度是 6 分钟，1440 个可能的分钟值里有 1200 个往返不回来
    /// （15 分 → 0.2 h → 12 分，差 3 分钟），并会连带引发"本地永远领先 → 每轮重推"。
    /// 2 位小数的粒度是 0.6 分钟，**1..1440 全部分钟值都能精确往返**（最大误差 0），
    /// 所以 15 分 → 0.25 h → 15 分，不丢精度。
    /// </remarks>
    internal static double MinutesToHours(int durationMinutes)
        => Math.Round(durationMinutes / 60.0, 2);

    /// <summary>
    /// **canonical seconds → 小时（2 位小数）**：把本地秒级事实写向 Notion 时**唯一**允许的换算入口。
    ///
    /// ⚠️ 不要用 <see cref="MinutesToHours"/> 处理本地秒数：那会先 `seconds / 60` 截断一次、
    /// 再 `round(/60, 2)`，形成"两套独立取整"（9/23 的 7872s 就是这么被写成 2.18 h 的；
    /// 按秒一次换算应为 <c>round(7872/3600, 2) = 2.19 h</c>）。
    /// 事实层永远是秒：<c>sessions.duration_seconds</c> / <c>daily_summary.duration_seconds</c>。
    /// </summary>
    internal static double SecondsToHours(int durationSeconds)
        => Math.Round(durationSeconds / 3600.0, 2);

    /// <summary>
    /// 拼装每日记录标题：时长以小时显示（2 位小数），如「Master Key · 0.7 h」。
    /// 参数是**游戏名**，不是拼好的标题 —— 传完整标题进来会拼出「X · 0.7 h · 0.7 h」。
    /// </summary>
    internal static string Build(string gameName, int durationMinutes)
        => BuildFromHours(gameName, MinutesToHours(durationMinutes));

    /// <summary>
    /// 同上，但直接用**小时数**拼标题（canonical seconds 路径专用：避免"先按分钟截断、再 round"的两次取整）。
    /// </summary>
    internal static string BuildFromHours(string gameName, double hours)
        => $"{gameName} · {hours} h";

    /// <summary>剥掉标题末尾的时长后缀，得到裸游戏名。</summary>
    internal static string StripSuffix(string rawTitle)
        => SuffixRegex.Replace(rawTitle, string.Empty).Trim();

    /// <summary>提取裸游戏名（同 StripSuffix）。</summary>
    internal static string ExtractBaseName(string rawTitle)
        => StripSuffix(rawTitle);

    /// <summary>
    /// 取出标题末尾的时长后缀**原文**（如「 · 2.2 h」「2.2h」「(42分)」），没有则返回空串。
    /// 回刷标题时靠它保留用户原本写的时长文本 —— 不能拿本地值重新拼。
    /// </summary>
    internal static string ExtractSuffix(string rawTitle)
    {
        var m = SuffixRegex.Match(rawTitle);
        return m.Success ? m.Value : string.Empty;
    }

    /// <summary>
    /// 用「游戏名 + 原标题里的时长」拼出**标准格式**标题：`名字 · X h`（小时、最多 2 位小数）。
    ///
    /// 时长数值始终以 Notion 侧为权威 —— 本方法只把原标题里的时长**文本**重新规范化，
    /// 换算全程只用后缀自身携带的数字与单位，**绝不引用本地缓存值**，
    /// 也**绝不写「单次时长」属性**（2026-09-19 那 786 条历史记录就是这么被写坏的）。
    ///
    /// 为什么必须做（两件事叠加）：
    ///   ① <see cref="ExtractSuffix"/> 为了兼容用户手工录入，允许后缀**不带分隔符**，
    ///      所以「黑旗10.1h」取出的是 `10.1h`；原实现直接 <c>displayName + originalSuffix</c>，
    ///      于是写出「黑旗10.1h」—— 看起来就像漏了中间的 ·（2026-10 QA 反馈的正是这个）。
    ///   ② 用户的表里混着手写紧贴格式（`10.1h`）、空格格式（`2.2 h`）、程序旧括号格式（`(42分)`）
    ///      三种写法，用户要求**统一成优雅的标准格式**（含数字与单位之间的空格）。
    ///
    /// 后缀换算不出正数分钟时（例如「(0.001h)」会四舍五入成 0 分钟），退回
    /// 「保留原文 + 补上 ` · `」而不做换算 —— **宁可不统一，也绝不把时长抹成 0**。
    /// </summary>
    internal static string BuildNormalizedTitle(string gameName, string originalSuffix, int fallbackDurationMinutes)
    {
        var minutes = ParseSuffixMinutes(originalSuffix);
        if (minutes.HasValue) return Build(gameName, minutes.Value);

        var body = StripSuffixSeparator(originalSuffix);
        return body.Length > 0
            ? $"{gameName} · {body}"
            : Build(gameName, fallbackDurationMinutes);
    }

    /// <summary>
    /// 解析时长后缀，返回**分钟数**。认 <see cref="SuffixRegex"/> 的全部形态：
    /// 「 · 0.7 h」「10.1h」「2.2 h」「45min」「42分钟」「(42分)」「(2.5h)」「2小时」。
    /// 认不出、或换算后 ≤ 0 分钟时返回 null（调用方退回"保留原文"）。
    /// 返回分钟数是为了复用 <see cref="Build"/>，保证输出与程序标准格式逐字节一致；
    /// 2 位小数的粒度是 0.6 分钟，所以标准标题「往返解析一次完全相等」→ 本方法对标准标题幂等。
    /// </summary>
    private static int? ParseSuffixMinutes(string suffix)
    {
        var body = StripSuffixSeparator(suffix);
        if (body.Length == 0) return null;

        // 程序旧括号格式先脱括号（括号只表示"包起来"，单位仍在里面）
        if (body.Length > 2 && body[0] == '(' && body[body.Length - 1] == ')')
        {
            body = body.Substring(1, body.Length - 2).Trim();
        }

        var m = SuffixCoreRegex.Match(body);
        if (!m.Success) return null;
        if (!double.TryParse(
                m.Groups["num"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var num))
        {
            return null;
        }

        var unit = m.Groups["unit"].Value.ToLowerInvariant();
        var isMinutes = unit is "min" or "mins" || unit.Contains('分');
        var minutes = (int)Math.Round(isMinutes ? num : num * 60.0);
        return minutes > 0 ? minutes : null;
    }

    /// <summary>
    /// 时长后缀的"核心"部分（分隔符与括号已由调用方剥掉）。
    /// 单位只列 <see cref="SuffixRegex"/> 里出现过的那些，避免"能提取却解析不了"的两套规则漂移。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SuffixCoreRegex = new(
        @"^(?<num>\d+(?:\.\d+)?)\s*(?<unit>min|mins|h|hr|hrs|小时|分钟|分)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>剥掉后缀开头的分隔符（· | - :）与空白，得到裸的「数值+单位」文本；空后缀返回空串。</summary>
    internal static string StripSuffixSeparator(string suffix)
    {
        var s = suffix.Trim();
        if (s.Length == 0) return string.Empty;
        return s[0] is '·' or '|' or '-' or ':' ? s.Substring(1).TrimStart() : s;
    }
}

public class NotionClient : INotionClient
{
    private const string BaseUrl = "https://api.notion.com/v1";
    private const string NotionVersion = "2022-06-28";

    private readonly HttpClient _httpClient;
    private string _token;
    private readonly int _maxRetries;
    private string? _lastGameMasterTitlePropName;

    /// <summary>
    /// 2j 属性探测：最近一次查询总表时，实际命中的「已忽略」属性名
    /// （用户可能写成「已忽略」或「Ignored」，写回时必须用**他实际用的那个名字**）。
    /// null / 空 = 该库没有这个属性 → 忽略状态同步**整段跳过**。
    /// </summary>
    private string? _lastGameMasterIgnoredPropName;

    /// <summary>见 <see cref="INotionClient.SupportsGameIgnoredProperty"/>。</summary>
    public bool SupportsGameIgnoredProperty => !string.IsNullOrWhiteSpace(_lastGameMasterIgnoredPropName);

    public NotionClient(string token, HttpClient? httpClient = null, int maxRetries = 3)
    {
        _token = token;
        _maxRetries = maxRetries;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public void UpdateToken(string token)
    {
        _token = token;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint, object? body = null)
    {
        var cleanEndpoint = endpoint.TrimStart('/');
        var req = new HttpRequestMessage(method, $"{BaseUrl}/{cleanEndpoint}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.Add("Notion-Version", NotionVersion);

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return req;
    }

    /// <summary>
    /// 克隆请求用于重试：一个 HttpRequestMessage 只能发送一次（发送后内容流被消费），
    /// 复用同一实例重试会抛 "The request message was already sent"。
    /// </summary>
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Version = req.Version };
        foreach (var h in req.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (req.Content != null)
        {
            var bytes = await req.Content.ReadAsByteArrayAsync();
            var content = new ByteArrayContent(bytes);
            foreach (var h in req.Content.Headers)
                content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            clone.Content = content;
        }
        return clone;
    }

    private async Task<JsonElement> SendWithRetryAsync(HttpRequestMessage Function)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                // 每次尝试都用克隆的新请求实例，保证重试真正生效
                using var request = await CloneRequestAsync(Function);
                using var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    // Notion 的 400/404 body 里带具体原因（如哪个属性名/类型不对），
                    // EnsureSuccessStatusCode 只会给状态码 —— 必须读出来，否则无法诊断配置问题。
                    string detail = "";
                    try
                    {
                        using var errDoc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                        if (errDoc.RootElement.TryGetProperty("message", out var m))
                            detail = m.GetString() ?? "";
                    }
                    catch { /* body 不是 JSON 时忽略 */ }

                    if (attempt < _maxRetries && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
                    {
                        await Task.Delay(response.StatusCode == HttpStatusCode.TooManyRequests
                            ? response.Headers.RetryAfter?.Delta ?? delay
                            : delay);
                        delay *= 1.5;
                        continue;
                    }

                    // ⚠️ 2026-10-13：把状态码一并带进异常（消息**逐字不变**）——
                    //    致命错误判定（IsFatalAuthError）需要机器可读的状态码，
                    //    靠解析文案去猜 401/403 太脆。
                    throw new HttpRequestException(
                        $"Notion API {(int)response.StatusCode}: {(string.IsNullOrEmpty(detail) ? response.ReasonPhrase : detail)}",
                        inner: null,
                        statusCode: response.StatusCode);
                }

                var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                return doc.RootElement.Clone();
            }
            catch (Exception ex) when (attempt < _maxRetries)
            {
                AppLog.Warn($"Notion 请求失败（第 {attempt}/{_maxRetries} 次，将重试）: {ex.Message}");
                await Task.Delay(delay);
                delay *= 1.5;
            }
            catch (Exception ex)
            {
                AppLog.Error("Notion 请求最终失败", ex);
                throw;
            }
        }

        throw new HttpRequestException($"Notion API request failed after {_maxRetries} attempts");
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "/users/me");
            using var resp = await _httpClient.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<NotionGameCatalogItem>> QueryGameMasterAsync(string databaseId)
    {
        var results = new List<NotionGameCatalogItem>();
        string? cursor = null;
        var cleanDbId = databaseId.Replace("-", "");

        do
        {
            var body = new Dictionary<string, object> { { "page_size", 100 } };
            if (!string.IsNullOrEmpty(cursor))
            {
                body["start_cursor"] = cursor;
            }

            using var req = CreateRequest(HttpMethod.Post, $"/databases/{cleanDbId}/query", body);
            var root = await SendWithRetryAsync(req);

            if (root.TryGetProperty("results", out var pageArray))
            {
                foreach (var page in pageArray.EnumerateArray())
                {
                    var pageId = page.GetProperty("id").GetString() ?? "";
                    var props = page.GetProperty("properties");

                    var (name, titlePropName) = ExtractTitleWithPropertyName(props);
                    if (!string.IsNullOrEmpty(titlePropName))
                    {
                        _lastGameMasterTitlePropName = titlePropName;
                    }
                    var aliases = ExtractMultiSelectOrText(props, "别名", "Aliases");
                    var idents = ExtractMultiSelectOrText(props, "游戏标识", "Identifiers", "Steam ID");
                    var genres = ExtractMultiSelectOrText(props, "类型", "游戏类型", "Genre", "Genres", "分类");
                    // 2j：属性探测。「已忽略」Checkbox 不存在 → null（不是 false）→ 整段忽略同步跳过。
                    var (ignored, ignoredPropName) = ExtractCheckbox(props, "已忽略", "Ignored");
                    if (!string.IsNullOrWhiteSpace(ignoredPropName))
                    {
                        // 记住用户实际使用的属性名 —— 写回时必须用它，否则会 400。
                        _lastGameMasterIgnoredPropName = ignoredPropName;
                    }
                    var coverUrl = ExtractCoverUrl(page, props);
                    var (iconUrl, iconType) = ExtractPageIcon(page);

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        results.Add(new NotionGameCatalogItem
                        {
                            PageId = pageId,
                            Name = name,
                            Aliases = aliases,
                            Identifiers = idents,
                            Genres = genres,
                            CoverUrl = coverUrl,
                            IconUrl = iconUrl,
                            IconType = iconType,
                            IsIgnored = ignored,
                            LastSyncedAt = DateTime.UtcNow
                        });
                    }
                }
            }

            cursor = root.TryGetProperty("has_more", out var hm) && hm.GetBoolean()
                ? root.GetProperty("next_cursor").GetString()
                : null;

        } while (!string.IsNullOrEmpty(cursor));

        return results;
    }

    public async Task<IReadOnlyList<NotionDailyRecordItem>> QueryDailyRecordsAsync(string databaseId)
    {
        var results = new List<NotionDailyRecordItem>();
        string? cursor = null;
        var cleanDbId = databaseId.Replace("-", "");

        do
        {
            var body = new Dictionary<string, object> { { "page_size", 100 } };
            if (!string.IsNullOrEmpty(cursor))
            {
                body["start_cursor"] = cursor;
            }

            using var req = CreateRequest(HttpMethod.Post, $"/databases/{cleanDbId}/query", body);
            var root = await SendWithRetryAsync(req);

            if (root.TryGetProperty("results", out var pageArray))
            {
                foreach (var page in pageArray.EnumerateArray())
                {
                    var pageId = page.GetProperty("id").GetString() ?? "";
                    var props = page.GetProperty("properties");

                    var (rawTitle, titlePropName) = ExtractTitleWithPropertyName(props);
                    // 标题形如「游戏名 · 42 min」（旧行是「游戏名 (42分)」），只剔除末尾的时长后缀。
                    var title = DailyRecordTitle.StripSuffix(rawTitle);
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = rawTitle.Trim();
                    }

                    var date = ExtractDate(props);
                    // 新名优先，旧名兜底 —— 2026-09-18 用户把属性改名了
                    // （时长 → 单次时长、游戏 → 关联游戏），但别人的表可能还没改，
                    // 读的时候两种都认，免得拉不回老数据。
                    //
                    // ⚠️ 「单次时长」的值是**小时**（2 位小数），本地模型一律用**分钟**，
                    //    所以这里必须换算，并且要兼容"旧行存的是分钟"。
                    //    单位**优先从标题判定**（标题里一直带着 h/min），
                    //    只按数值大小猜会把旧行的 5 分钟读成 5 小时（放大 60 倍）。
                    var durationUnit = DetectDurationUnit(rawTitle);
                    var durationRaw = ExtractDouble(props, "时长", "单次时长", "时长(分)", "Duration", "DurationMinutes");
                    var duration = RawDurationToMinutes(durationRaw, rawTitle);
                    // canonical seconds：外部声明**一次**换算成秒（分钟字段只留给显示/旧字段）
                    var durationSeconds = RawDurationToSeconds(durationRaw, rawTitle);
                    var gameMasterPageId = ExtractRelationId(props, "关联游戏", "游戏", "游戏总表", "Game");
                    var status = ExtractSelect(props, "绑定状态", "Status");
                    // 页面当前 Page Icon（2026-10-13）：用于"只在页面没有图标时才补图标"的判定。
                    var (pageIconUrl, pageIconType) = ExtractPageIcon(page);

                    if (!string.IsNullOrWhiteSpace(date) && (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(gameMasterPageId)))
                    {
                        results.Add(new NotionDailyRecordItem
                        {
                            PageId = pageId,
                            GameTitle = title,
                            RawTitle = rawTitle.Trim(),
                            TitlePropertyName = string.IsNullOrWhiteSpace(titlePropName) ? "游戏动态" : titlePropName,
                            Date = date,
                            DurationMinutes = duration,
                            DurationSeconds = durationSeconds,
                            DurationUnitIsMinutes = durationUnit == DurationUnit.Minutes,
                            GameMasterPageId = gameMasterPageId,
                            Status = status,
                            IconType = pageIconType,
                            IconUrl = pageIconUrl
                        });
                    }
                }
            }

            cursor = root.TryGetProperty("has_more", out var hm) && hm.GetBoolean()
                ? root.GetProperty("next_cursor").GetString()
                : null;

        } while (!string.IsNullOrEmpty(cursor));

        return results;
    }

    public async Task<string> CreateDailyRecordAsync(
        string dailyDbId,
        string date,
        string gameName,
        int durationMinutes,
        string? gamePageId,
        string? iconUrl = null,
        int? durationSeconds = null,
        string? iconFileUploadId = null)
    {
        var cleanDbId = dailyDbId.Replace("-", "");

        // canonical seconds（2026-10-09）：调用方给了秒就以秒为准，**一次**换算成小时；
        // 没给（历史/标题规范化等不写数值的路径）才退回旧的分钟口径。
        var hours = durationSeconds is { } secs
            ? DailyRecordTitle.SecondsToHours(secs)
            : DailyRecordTitle.MinutesToHours(durationMinutes);

        var titleText = DailyRecordTitle.BuildFromHours(gameName, hours);
        // ⚠️ 每日时长表的属性名：游戏动态 ／ 时长 ／ 关联游戏 ／ 绑定状态
        //    写错名字 Notion 会返回 400（property does not exist），同步直接失败。
        //    注意**总表**的 title 属性仍叫「游戏名称」，别一起改了。
        var properties = new Dictionary<string, object>
        {
            ["游戏动态"] = new { title = new[] { new { text = new { content = titleText } } } },
            ["日期"] = new { date = new { start = date } },
            // 值是**小时**（2 位小数），不是分钟 —— 与标题后缀保持一致（同上面那次换算，绝不第二次取整）。
            ["时长"] = new { number = hours },
            ["绑定状态"] = new { select = new { name = !string.IsNullOrEmpty(gamePageId) ? "已绑定" : "未绑定" } }
        };

        if (!string.IsNullOrEmpty(gamePageId))
        {
            properties["关联游戏"] = new { relation = new[] { new { id = gamePageId } } };
        }

        var payload = new
        {
            parent = new { database_id = cleanDbId },
            properties,
            // page icon：优先用本地上传得到的 file_upload（Daily 链路），没有才退回 external 链接。
            icon = BuildIconObject(iconFileUploadId, iconUrl)
        };

        using var req = CreateRequest(HttpMethod.Post, "/pages", payload);
        var res = await SendWithRetryAsync(req);
        return res.GetProperty("id").GetString() ?? "";
    }

    public async Task<bool> UpdateDailyRecordAsync(
        string pageId,
        int durationMinutes,
        string? gamePageId,
        string? gameName = null,
        string? iconUrl = null,
        bool writeDuration = false,
        string? titleOverride = null,
        int? durationSeconds = null,
        string? iconFileUploadId = null)
    {
        var cleanPageId = pageId.Replace("-", "");
        // 属性名同 CreateDailyRecordAsync：游戏动态 / 时长 / 关联游戏
        var properties = new Dictionary<string, object>();

        if (writeDuration)
        {
            // ⚠️ 只有**推送路径**（本地时长确实领先）才写这个属性。
            //    历史记录在 Notion 上的数值是权威 —— 拿本地缓存去覆盖它属于毁灭性错误
            //    （2026-09-19 回刷链路就是这么篡改了 QA 786 条记录）。
            //    只改"呈现"（标题 / 关系 / 图标）的调用必须传 writeDuration: false。
            // 值是**小时**（2 位小数），与创建时以及标题后缀保持一致。
            // canonical seconds：给了秒就一次换算成小时（避免分钟截断 + round 的两次取整）。
            properties["时长"] = new
            {
                number = durationSeconds is { } secs
                    ? DailyRecordTitle.SecondsToHours(secs)
                    : DailyRecordTitle.MinutesToHours(durationMinutes)
            };
        }

        if (!string.IsNullOrEmpty(titleOverride))
        {
            // 优先使用传入的显式标题（例如保留原标题时长后缀后的「新游戏名 · 2.2 h」）
            properties["游戏动态"] = new { title = new[] { new { text = new { content = titleOverride } } } };
        }
        else if (!string.IsNullOrEmpty(gameName))
        {
            // 永远按「gameName + 当前时长」重算整个标题，不做"只改游戏名那一段"的局部替换。
            // 理由：时长本身也在标题里，且老行可能还是「(42分)」这种旧格式 ——
            // 局部替换反而会拼出「新名字 · 42 min」残留旧后缀的怪东西。
            //
            // ⚠️ 传进来的必须是**游戏名**。传拼好的完整标题会得到「X · 0.7 h · 0.7 h」。
            // 这个参数以前叫 gameTitle，正是这个歧义导致过真实 bug（alpha18 回刷链路），故改名。
            var titleText = DailyRecordTitle.Build(gameName, durationMinutes);
            properties["游戏动态"] = new { title = new[] { new { text = new { content = titleText } } } };
        }

        if (!string.IsNullOrEmpty(gamePageId))
        {
            properties["关联游戏"] = new { relation = new[] { new { id = gamePageId } } };
            properties["绑定状态"] = new { select = new { name = "已绑定" } };
        }
        else
        {
            properties["绑定状态"] = new { select = new { name = "未绑定" } };
        }

        var icon = BuildIconObject(iconFileUploadId, iconUrl);
        var payload = icon == null
            ? (object)new { properties }
            : new { properties, icon };

        using var req = CreateRequest(HttpMethod.Patch, $"/pages/{cleanPageId}", payload);
        var res = await SendWithRetryAsync(req);
        return res.TryGetProperty("id", out _);
    }

    /// <summary>
    /// **只**改每日记录的「绑定状态」属性（已绑定 / 未绑定）—— 绝不碰「单次时长」「日期」或标题等属性。
    /// </summary>
    public async Task<bool> UpdateDailyBindingStatusAsync(string pageId, string status)
    {
        var cleanPageId = pageId.Replace("-", "");
        var properties = new Dictionary<string, object>
        {
            ["绑定状态"] = new { select = new { name = status } }
        };

        try
        {
            using var req = CreateRequest(HttpMethod.Patch, $"/pages/{cleanPageId}", new { properties });
            var res = await SendWithRetryAsync(req);
            return res.TryGetProperty("id", out _);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[UpdateDailyBindingStatusAsync] 更新页面 {pageId} 绑定状态失败: {ex.Message}");

            // ⚠️ 2026-10-13（真机故障修复）：认证/权限错误（401/403）**必须让调用方知道** ——
            //    逐条回写循环靠它立刻中断；这里若照旧 `return false`，异常被吞掉，
            //    循环的 catch 永远不触发，就会继续对着同一条死路逐条重试到分钟级
            //    （真机实测：无效 Token 时这里把首次同步拖到分钟级，保存按钮被禁用）。
            //    其它错误（网络/超时/限流/5xx）沿用既有契约：记日志 + 返回 false，不抛。
            if (NotionSyncService.IsFatalAuthError(ex)) throw;

            return false;
        }
    }

    /// <summary>
    /// **只**改每日记录的标题（以及可选图标）—— 绝不碰「单次时长」「日期」等数据属性。
    /// </summary>
    /// <remarks>
    /// 🚨 2026-09-19 事故：回刷标题原先走 <see cref="UpdateDailyRecordAsync"/>，
    ///    而那个方法**必然同时写「单次时长」**（当年设计如此：标题串里含时长，
    ///    于是更新就顺带把时长也写了）。结果回刷拿**本地的**分钟数去覆盖 Notion 上
    ///    原本的数值 —— QA 那次一次运行就改写了 **786 条**，历史时长被篡改。
    ///
    ///    原则：Notion 侧的历史记录是**权威数据**，程序只允许改"呈现"（标题 / 图标），
    ///    不允许改"数值"。要改数值只能走 <see cref="UpdateDailyRecordAsync"/>（推送路径），
    ///    那条路径写的是本地确实领先的时长。
    /// </remarks>
    public async Task<bool> UpdateDailyRecordTitleAsync(
        string pageId, string title, string? iconUrl = null, string titlePropertyName = "游戏动态",
        string? iconFileUploadId = null)
    {
        var cleanPageId = pageId.Replace("-", "");
        var propName = string.IsNullOrWhiteSpace(titlePropertyName) ? "游戏动态" : titlePropertyName;

        // 只带指定 title 属性 —— PATCH 只改传进去的属性，不传的保持原样。
        var properties = new Dictionary<string, object>
        {
            [propName] = new { title = new[] { new { text = new { content = title } } } }
        };

        // 图标同样"不传就不写"：调用方只有在页面当前**没有图标**时才会传 file_upload id。
        // ⚠️ 这是页面图标（Page Icon），不是封面（Cover），也不是页面内容。
        var icon = BuildIconObject(iconFileUploadId, iconUrl);
        var payload = icon is null
            ? (object)new { properties }
            : new { properties, icon };

        using var req = CreateRequest(HttpMethod.Patch, $"/pages/{cleanPageId}", payload);
        var res = await SendWithRetryAsync(req);
        return res.TryGetProperty("id", out _);
    }

    public async Task<string> GetDatabaseTitlePropertyNameAsync(string cleanDbId)
    {
        if (!string.IsNullOrWhiteSpace(_lastGameMasterTitlePropName))
        {
            return _lastGameMasterTitlePropName;
        }

        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"/databases/{cleanDbId}");
            var root = await SendWithRetryAsync(req);
            if (root.TryGetProperty("properties", out var propsObj))
            {
                foreach (var prop in propsObj.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("type", out var type) && type.GetString() == "title")
                    {
                        _lastGameMasterTitlePropName = prop.Name;
                        return prop.Name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[Notion] 查询总表数据库 schema 失败，使用默认属性名「游戏名称」: {ex.Message}");
        }

        return "游戏名称";
    }

    public async Task<string> CreateGameMasterPageAsync(
        string gameDbId,
        string gameTitle,
        string? iconUrl = null,
        string? coverUrl = null)
    {
        var cleanDbId = gameDbId.Replace("-", "");
        var titleProp = await GetDatabaseTitlePropertyNameAsync(cleanDbId);

        var properties = new Dictionary<string, object>
        {
            [titleProp] = new { title = new[] { new { text = new { content = gameTitle } } } }
        };

        var payload = new
        {
            parent = new { database_id = cleanDbId },
            properties,
            icon = string.IsNullOrWhiteSpace(iconUrl)
                ? null
                : new { type = "external", external = new { url = iconUrl } },
            cover = string.IsNullOrWhiteSpace(coverUrl)
                ? null
                : new { type = "external", external = new { url = coverUrl } }
        };

        using var req = CreateRequest(HttpMethod.Post, "/pages", payload);
        var res = await SendWithRetryAsync(req);
        return res.GetProperty("id").GetString() ?? "";
    }

    /// <summary>
    /// 把页面移入 Notion 回收站（archived = true）。这是 Notion API 提供的"删除"方式，
    /// 并非物理删除 —— 30 天内可在 Notion 的回收站里恢复。
    /// </summary>
    public async Task<bool> ArchivePageAsync(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return false;

        var cleanPageId = pageId.Replace("-", "");
        using var req = CreateRequest(HttpMethod.Patch, $"/pages/{cleanPageId}", new { archived = true });
        var res = await SendWithRetryAsync(req);
        return res.TryGetProperty("id", out _);
    }

    public async Task<bool> SetPageIconAsync(string pageId, string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageId) || string.IsNullOrWhiteSpace(imageUrl)) return false;

        var cleanPageId = pageId.Replace("-", "");
        using var req = CreateRequest(HttpMethod.Patch, $"/pages/{cleanPageId}", new
        {
            icon = new { type = "external", external = new { url = imageUrl } }
        });
        var res = await SendWithRetryAsync(req);
        return res.TryGetProperty("id", out _);
    }

    /// <summary>
    /// page icon 的对象：**优先** file_upload（本地封面上传后的持久 id），
    /// 退回 external（总表 / Steam CDN 的临时 URL）；两者都为空则返回 null（不写 icon）。
    /// </summary>
    private static object? BuildIconObject(string? fileUploadId, string? externalUrl)
    {
        if (!string.IsNullOrWhiteSpace(fileUploadId))
            return new { type = "file_upload", file_upload = new { id = fileUploadId } };

        if (!string.IsNullOrWhiteSpace(externalUrl))
            return new { type = "external", external = new { url = externalUrl } };

        return null;
    }

    /// <summary>
    /// 新建 Notion File Upload（<c>POST /v1/file_uploads</c>，single_part）。
    /// </summary>
    /// <remarks>2026-10-06 实测：Notion-Version <c>2022-06-28</c> 下即可用，无需升级 pin。</remarks>
    public async Task<string?> CreateFileUploadAsync(string filename, string contentType)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Post, "/file_uploads", new
            {
                mode = "single_part",
                filename,
                content_type = contentType
            });
            var res = await SendWithRetryAsync(req);
            return res.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[DailyIcon] 创建 File Upload 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 发送文件内容：<c>POST /v1/file_uploads/{id}/send</c>，multipart/form-data，字段名固定 <c>file</c>。
    /// </summary>
    public async Task<bool> SendFileUploadAsync(string fileUploadId, string filePath, string contentType)
    {
        if (string.IsNullOrWhiteSpace(fileUploadId) || !File.Exists(filePath)) return false;

        var cleanId = fileUploadId.Replace("-", "");
        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var form = new MultipartFormDataContent();
                var bytes = await File.ReadAllBytesAsync(filePath);
                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                form.Add(fileContent, "file", Path.GetFileName(filePath));

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/file_uploads/{cleanId}/send")
                {
                    Content = form
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                req.Headers.Add("Notion-Version", NotionVersion);

                using var resp = await _httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    return doc.RootElement.TryGetProperty("status", out var st) && st.GetString() == "uploaded";
                }

                if (attempt < _maxRetries && (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500))
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt));
                    continue;
                }

                AppLog.Warn($"[DailyIcon] /send 失败: HTTP {(int)resp.StatusCode}");
                return false;
            }
            // 过滤器分支只负责"退避后重试"，不使用异常对象；下方分支才记录原因 ⇒ 不声明变量（CS0168）
            catch (Exception) when (attempt < _maxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[DailyIcon] /send 异常: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    /// <summary>查询 File Upload 状态；失败或不存在返回 null（调用方据此认定"该映射不可用"）。</summary>
    public async Task<string?> GetFileUploadStatusAsync(string fileUploadId)
    {
        if (string.IsNullOrWhiteSpace(fileUploadId)) return null;

        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"/file_uploads/{fileUploadId.Replace("-", "")}");
            var res = await SendWithRetryAsync(req);
            return res.TryGetProperty("status", out var st) ? st.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 检查指定 page_id 的页面是否已在 Notion 侧被删除（移入回收站或已不存在）。
    /// </summary>
    /// <summary>
    /// 2j：把总表页面的「已忽略」Checkbox 设为指定值。
    ///
    /// ⚠️ **属性探测**：该用户的总表没有「已忽略」属性时直接返回 false、绝不写入 ——
    /// 这样"要不要用这个能力"完全由用户在自己库里加不加属性决定，老用户行为零变化。
    /// </summary>
    public async Task<bool> UpdateGameMasterIgnoredAsync(string pageId, bool ignored)
    {
        if (!SupportsGameIgnoredProperty) return false;
        if (string.IsNullOrWhiteSpace(pageId)) return false;

        var cleanPageId = pageId.Replace("-", "");
        var propName = _lastGameMasterIgnoredPropName!;
        using var req = CreateRequest(HttpMethod.Patch, $"/pages/{cleanPageId}", new
        {
            properties = new Dictionary<string, object>
            {
                [propName] = new { checkbox = ignored }
            }
        });
        var res = await SendWithRetryAsync(req);
        return res.TryGetProperty("id", out _);
    }

    /// <summary>
    /// 检查指定 page_id 的页面是否已在 Notion 侧被删除（移入回收站或已不存在）。
    /// </summary>
    public async Task<bool> IsPageDeletedAsync(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return true;

        var cleanPageId = pageId.Replace("-", "");
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"/pages/{cleanPageId}");
            var res = await SendWithRetryAsync(req);

            // 🔒 2026-10-11 收紧（用户裁定：**归档 ≠ 删除**，不得未经产品语义确认就等价处理）：
            //    · in_trash = true ⇒ 用户在 Notion 里把页面扔进了回收站 ⇒ 认定删除；
            //    · archived = true ⇒ 仅表示"归档/移出当前视图"。它**可能是"暂时不想看"**，
            //      而不是"这条声明不存在了"。若在这里认定删除，会连带撤销该 (游戏, 日期) 的外部声明、
            //      把时长从两机统计里减掉 —— 属于不可逆的数据语义变更，必须由产品决策。
            //      因此判为**未删除**，只落一条 Warn 供人工确认。
            if (res.TryGetProperty("in_trash", out var inTrash) && inTrash.GetBoolean())
            {
                return true;
            }

            if (res.TryGetProperty("archived", out var archived) && archived.GetBoolean())
            {
                AppLog.Warn($"[删除对账] Notion 页面 {pageId} 处于**归档**状态（非回收站）："
                    + "按'归档 ≠ 删除'处理，本轮**不撤销**任何声明；若确认该页已不再使用，请人工确认后处理");
                return false;
            }

            return false;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404") || ex.Message.Contains("Could not find page") || ex.Message.Contains("object_not_found"))
        {
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[删除对账] 检查 Notion 页面 {pageId} 状态失败: {ex.Message}，为防误删判定为未删除");
            return false;
        }
    }

    private static (string Title, string PropertyName) ExtractTitleWithPropertyName(JsonElement props)
    {
        foreach (var prop in props.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("type", out var type) && type.GetString() == "title")
            {
                if (prop.Value.TryGetProperty("title", out var titleArray))
                {
                    var sb = new StringBuilder();
                    foreach (var item in titleArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("plain_text", out var pt))
                        {
                            sb.Append(pt.GetString());
                        }
                    }
                    return (sb.ToString().Trim(), prop.Name);
                }
                return (string.Empty, prop.Name);
            }
        }
        return (string.Empty, "游戏动态");
    }

    private static string ExtractTitle(JsonElement props) => ExtractTitleWithPropertyName(props).Title;

    private static List<string> ExtractMultiSelectOrText(JsonElement props, params string[] propertyNames)
    {
        var list = new List<string>();
        foreach (var pName in propertyNames)
        {
            if (props.TryGetProperty(pName, out var pObj) && pObj.TryGetProperty("type", out var type))
            {
                var tStr = type.GetString();
                if (tStr == "multi_select" && pObj.TryGetProperty("multi_select", out var ms))
                {
                    foreach (var opt in ms.EnumerateArray())
                    {
                        var name = opt.GetProperty("name").GetString();
                        if (!string.IsNullOrWhiteSpace(name)) list.Add(name.Trim());
                    }
                }
                else if (tStr == "rich_text" && pObj.TryGetProperty("rich_text", out var rt))
                {
                    foreach (var item in rt.EnumerateArray())
                    {
                        if (item.TryGetProperty("plain_text", out var pt))
                        {
                            var s = pt.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                        }
                    }
                }
            }
        }
        return list;
    }

    /// <summary>
    /// 解析总表页面的 page icon。icon 是正方形小图，做 88px 方形封面比横幅 cover 合适。
    /// 返回 (url, type)；emoji 图标没有 url；页面未设图标时 type = null（可由程序写入）。
    /// </summary>
    /// <summary>
    /// 2j：读取 Checkbox 型属性，并回传**实际命中的属性名**（写回时要用同一个名字）。
    /// 找不到该属性时返回 <c>(null, null)</c> —— "没有这个属性"与"用户没勾选(false)"必须严格区分。
    /// </summary>
    private static (bool? Value, string? PropertyName) ExtractCheckbox(JsonElement props, params string[] propertyNames)
    {
        foreach (var pName in propertyNames)
        {
            if (!props.TryGetProperty(pName, out var pObj)) continue;
            if (!pObj.TryGetProperty("type", out var type)) continue;
            if (type.GetString() != "checkbox") continue;
            if (!pObj.TryGetProperty("checkbox", out var value)) continue;

            var value2 = (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                ? (bool?)value.GetBoolean()
                : null;
            return (value2, pName);
        }

        return (null, null);
    }

    private static (string? Url, string? Type) ExtractPageIcon(JsonElement page)
    {
        if (!page.TryGetProperty("icon", out var icon) || icon.ValueKind != JsonValueKind.Object)
            return (null, null);

        if (!icon.TryGetProperty("type", out var type))
            return (null, null);

        var t = type.GetString();
        if (t == "external" && icon.TryGetProperty("external", out var ext) && ext.TryGetProperty("url", out var extUrl))
            return (extUrl.GetString(), t);
        if (t == "file" && icon.TryGetProperty("file", out var file) && file.TryGetProperty("url", out var fileUrl))
            return (fileUrl.GetString(), t);

        return (null, t);
    }

    private static string? ExtractCoverUrl(JsonElement page, JsonElement props)
    {
        // 1. Notion page cover property
        if (page.TryGetProperty("cover", out var coverProp) && coverProp.ValueKind == JsonValueKind.Object)
        {
            if (coverProp.TryGetProperty("external", out var ext) && ext.TryGetProperty("url", out var extUrl))
            {
                return extUrl.GetString();
            }
            if (coverProp.TryGetProperty("file", out var fileProp) && fileProp.TryGetProperty("url", out var fileUrl))
            {
                return fileUrl.GetString();
            }
        }

        // 2. Custom page properties like "封面", "Cover", "cover"
        foreach (var pName in new[] { "封面", "Cover", "cover", "Image", "海报" })
        {
            if (props.TryGetProperty(pName, out var pObj) && pObj.TryGetProperty("type", out var type))
            {
                var tStr = type.GetString();
                if (tStr == "files" && pObj.TryGetProperty("files", out var filesArr))
                {
                    foreach (var f in filesArr.EnumerateArray())
                    {
                        if (f.TryGetProperty("file", out var fileObj) && fileObj.TryGetProperty("url", out var u))
                            return u.GetString();
                        if (f.TryGetProperty("external", out var extObj) && extObj.TryGetProperty("url", out var u2))
                            return u2.GetString();
                    }
                }
                else if (tStr == "url" && pObj.TryGetProperty("url", out var urlVal))
                {
                    return urlVal.GetString();
                }
            }
        }

        return null;
    }

    private static string ExtractDate(JsonElement props)
    {
        foreach (var pName in new[] { "日期", "Date", "date" })
        {
            if (props.TryGetProperty(pName, out var pObj) && pObj.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "date" && pObj.TryGetProperty("date", out var dObj) && dObj.ValueKind == JsonValueKind.Object)
                {
                    if (dObj.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.String)
                    {
                        var str = s.GetString();
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            return str.Length >= 10 ? str.Substring(0, 10) : str;
                        }
                    }
                }
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 读取 Number 属性为 double。取不到返回 null。
    /// </summary>
    /// <remarks>
    /// 必须用 GetDouble 而不是 GetInt32：「单次时长」现在是**小时且带 1 位小数**（如 0.7），
    /// 对小数调用 GetInt32 会直接抛异常，整条记录拉不回来。
    /// </remarks>
    private static double? ExtractDouble(JsonElement props, params string[] propertyNames)
    {
        foreach (var pName in propertyNames)
        {
            if (props.TryGetProperty(pName, out var pObj) && pObj.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "number" && pObj.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number)
                {
                    return n.GetDouble();
                }
            }
        }
        return null;
    }

    /// <summary>「单次时长」原始值的单位，从标题后缀判定。</summary>
    internal enum DurationUnit { Unknown, Hours, Minutes }

    /// <summary>
    /// 从标题尾部认时长单位，形如「… 0.08 h」「… 42 min」「… (42分)」「…10.1h」。
    /// 必须锚定在**结尾**、且单位词**紧跟在数字后面**，
    /// 否则会把游戏名里的 h / min / 分 误当单位（例如「Half-Life」「三国志11」）。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TitleDurationUnitRegex =
        new(@"(?<num>\d+(?:[\.,]\d+)?)\s*(?<unit>小时|分钟|时|分|hours|hour|hrs|hr|h|minutes|minute|mins|min|m)\s*[)）]?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>标题里明确写了单位时，按其判定；认不出返回 Unknown。</summary>
    internal static DurationUnit DetectDurationUnit(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return DurationUnit.Unknown;

        var m = TitleDurationUnitRegex.Match(title);
        if (!m.Success) return DurationUnit.Unknown;

        return m.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "小时" or "时" or "hours" or "hour" or "hrs" or "hr" or "h" => DurationUnit.Hours,
            "分钟" or "分" or "minutes" or "minute" or "mins" or "min" or "m" => DurationUnit.Minutes,
            _ => DurationUnit.Unknown
        };
    }

    /// <summary>
    /// 把「单次时长」属性里的原始值换算成**分钟**（本地模型一律用分钟）。
    /// </summary>
    /// <remarks>
    /// 属性现在的单位是**小时**（2 位小数），但历史行里存的是**分钟**。
    ///
    /// ⚠️ 判单位**必须优先看标题**，不能只看数值大小 ——
    ///    2026-09-19 雾山报的严重错误就是这么来的：原先只按"≤24 当小时"判，
    ///    旧行里的 5（分钟）被读成 5 小时 = 300 分钟，**整整放大 60 倍**。
    ///
    /// 好在标题里一直带着单位，可以确定性地判：
    ///   · 本程序写的：「游戏名 · 0.08 h」
    ///   · 更早的版本：「游戏名 · 42 min」／「游戏名 (42分)」
    ///   · 用户手写的：「黑旗10.1h」（他按小时写）／「致命躯壳 45min」
    ///
    /// 只有标题里认不出单位时（用户只写了名字），才回落到数值大小猜测：
    /// 单日单游戏不可能超过 24 小时，故 &gt; 24 视为分钟。
    /// </remarks>
    /// <summary>
    /// 把 Notion 的「时长/单次时长」原始值**一次**换算成 canonical seconds。
    ///
    /// <para>
    /// ⚠️ 外部声明只有 0.01h（≈36 秒）分辨率，所以必须直接 <c>round(value × 3600)</c>；
    /// **绝不**"小时 → 分钟 → 秒"中转 —— 那会把 7.79h 先截断成 467 分钟，回推时变成 7.78h。
    /// 旧行可能存的是**分钟**（标题带 min，或数值 &gt; 24），那时按 <c>round(value × 60)</c>。
    /// </para>
    /// </summary>
    private static int RawDurationToSeconds(double? raw, string? title)
    {
        if (raw is { } value && value > 0)
        {
            switch (DetectDurationUnit(title))
            {
                case DurationUnit.Minutes: return (int)Math.Round(value * 60.0);
                case DurationUnit.Hours: return (int)Math.Round(value * 3600.0);
            }

            // 标题里没有可识别的单位：只能按数量级猜
            const double MaxPlausibleHours = 24.0;
            return value > MaxPlausibleHours
                ? (int)Math.Round(value * 60.0)     // 像是旧格式的分钟
                : (int)Math.Round(value * 3600.0);  // 像是小时
        }

        // 若远端数值属性未填或为 0，尝试从标题中提取时长（兼容用户只手写在标题的情况）
        if (!string.IsNullOrWhiteSpace(title))
        {
            var m = TitleDurationUnitRegex.Match(title);
            if (m.Success && double.TryParse(m.Groups["num"].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedVal) && parsedVal > 0)
            {
                var unit = m.Groups["unit"].Value.ToLowerInvariant();
                return unit switch
                {
                    "小时" or "时" or "hours" or "hour" or "hrs" or "hr" or "h" => (int)Math.Round(parsedVal * 3600.0),
                    "分钟" or "分" or "minutes" or "minute" or "mins" or "min" or "m" => (int)Math.Round(parsedVal * 60.0),
                    _ => 0
                };
            }
        }

        return 0;
    }

    /// <summary>
    /// 兼容保留（旧字段/显示用）：从 canonical seconds 派生的分钟，**不参与任何事实计算**。
    /// </summary>
    private static int RawDurationToMinutes(double? raw, string? title)
        => (int)Math.Round(RawDurationToSeconds(raw, title) / 60.0);

    private static string? ExtractRelationId(JsonElement props, params string[] propertyNames)
    {
        foreach (var pName in propertyNames)
        {
            if (props.TryGetProperty(pName, out var pObj) && pObj.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "relation" && pObj.TryGetProperty("relation", out var relArr) && relArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in relArr.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var id))
                        {
                            return id.GetString();
                        }
                    }
                }
            }
        }
        return null;
    }

    private static string? ExtractSelect(JsonElement props, params string[] propertyNames)
    {
        foreach (var pName in propertyNames)
        {
            if (props.TryGetProperty(pName, out var pObj) && pObj.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "select" && pObj.TryGetProperty("select", out var sel) && sel.ValueKind == JsonValueKind.Object)
                {
                    if (sel.TryGetProperty("name", out var n)) return n.GetString();
                }
            }
        }
        return null;
    }
}

public class NotionSyncService : INotionSyncService
{
    private readonly IDatabaseRepository _repo;
    private readonly INotionClient _client;
    private readonly TrackerConfig _config;
    private readonly IGameMatcher _matcher;
    private readonly Covers.CoverCacheService _coverCache;
    private readonly DailyIconUploadService _iconUploads;
    private List<NotionDailyRecordItem> _lastPulledDailyRecords = new();

    /// <summary>
    /// 本轮 Pull 里**原始查询**返回的 Page ID 集合（未经过解析丢弃 / 孤儿过滤 / 重复合并）。
    /// </summary>
    /// <remarks>
    /// 🛡️ 2026-10-14：用于区分两种"快照缺项"：
    /// · 在原始集合里、却不在业务快照里 ⇒ 页面**存在**，只是被解析丢弃/孤儿过滤/合并折叠；
    /// · 连原始集合里都没有 ⇒ 归档 / 未共享 / 无权限 / 查询不完整 —— **无法**据此认定页面不存在。
    /// 两种情况都不得直接覆盖远端（见 <see cref="SyncPendingDailyRecordsAsync"/> 的安全分支）。
    /// </remarks>
    private HashSet<string> _lastPulledRawPageIds = new();

    /// <summary>没有映射存储时的兜底（只会在测试里出现）：永远当作"没有映射"，不做持久化。</summary>
    private sealed class NullIconMappingStore : GameTimeTracker.Core.Notion.INotionIconMappingStore
    {
        public static readonly NullIconMappingStore Instance = new();
        public Task<string?> GetSettingAsync(string key) => Task.FromResult<string?>(null);
        public Task SetSettingAsync(string key, string value) => Task.CompletedTask;
        public Task DeleteSettingAsync(string key) => Task.CompletedTask;
    }

    /// <summary>
    /// 把原始异常翻译成用户能行动的文案：401 是 Token 无效（复制不全/过期），
    /// 403 才是数据库没 Connect 给 Integration；SSL/DNS 类是网络或代理问题。
    /// </summary>
    internal static string FriendlyNotionError(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("401")) return "Notion Token 无效或已过期——请到设置页重新复制 Internal Access Secret";
        if (msg.Contains("403")) return "Notion 拒绝访问——请确认两个数据库都已在 ··· → Connections 里连接该 Integration";

        // ⚠️ 2026-10-13：404 原先原样透出（"Notion API 404: Could not find database with ID: …"），
        //    用户看不懂它指什么，也不知道该改哪里。这里给出可行动的说明；
        //    具体是哪个 id 由调用方连同这一步的上下文写进日志（见 RefreshGameCatalogCacheAsync）。
        if (msg.Contains("404"))
            return "Notion 找不到该数据库或页面——它可能已被删除，或 Integration 没有被授权访问"
                + "（请在 Notion 里打开该页面/数据库 → ··· → Connections 连接该 Integration，"
                + "再到设置页确认对应的 ID 是最新的）";

        if (msg.Contains("archived")) return "该记录在 Notion 已被删除，本地将跟随清理";
        if (msg.Contains("SSL connection") || msg.Contains("不知道这样的主机") ||
            msg.Contains("No such host") || msg.Contains("timed out") || msg.Contains("name resolver"))
            return "无法连接 Notion 服务器——请检查网络或代理（api.notion.com 在部分地区需要代理访问）";
        return msg;
    }

    /// <summary>
    /// 是否为**致命**的 Notion 认证/权限错误（401 Token 无效 / 403 拒绝访问）。
    /// 判据与 <see cref="FriendlyNotionError"/> 完全一致（它把 401/403 归为"用户必须去改配置"的两类），
    /// 区别只是这里要的是**布尔结论**而不是给用户看的文案。
    ///
    /// <para>
    /// 用途：逐条回写循环（绑定状态回写、关系回填、标题回刷）遇到它应当**立刻中断** ——
    /// 同一个 Token 对后续每一条记录都必然同样失败，继续下去只是把几百条各重试 3 次
    /// （真机故障：无效 Token 时首次同步被拖到分钟级，期间设置页保存按钮被禁用，
    /// 用户既不能解绑也不能重新绑定）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>只认 401/403</b>：网络异常、超时、限流（429）、5xx 等**可恢复**错误一律返回 false，
    /// 继续沿用既有的"单条失败不阻断其余记录 + 下一轮重试"策略。
    /// </para>
    /// </summary>
    internal static bool IsFatalAuthError(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            // 首选：结构化状态码（NotionClient 抛出的 HttpRequestException 已带上）。
            if (e is HttpRequestException
                { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            {
                return true;
            }

            // 兜底：只留下文案的包装异常。用 NotionClient 自己拼的固定前缀，
            // 不会与 page id、时长数字之类混淆。
            var msg = e.Message;
            if (msg.Contains("Notion API 401") || msg.Contains("Notion API 403")) return true;
        }

        return false;
    }

    /// <summary>
    /// 上一次拉取是否成功。默认视为成功，避免在"还没拉过"时误挡上传。
    /// 用途：上传前必须先拿到 Notion 现有记录 —— 用户可能在使用本程序之前
    /// 就已经手工记录过时长，那些记录必须先把 notion_page_id 认回来，
    /// 否则本地会为同一个 (日期, 游戏) 再建一条，Notion 里就出现重复行。
    /// 拉取失败时宁可这轮不上传（下一轮网络恢复后会补），也不要冒重号的风险。
    /// </summary>
    private bool _lastPullSucceeded = true;

    /// <summary>
    /// 「删除对账」记住的**已确认同步目标**（归一化后的 Daily 库 id，存 settings 表）。
    ///
    /// <para>
    /// 用途见 <see cref="ReconcileNotionDeletionsAsync"/> 的环境可信判断：库被重建 / 换 workspace /
    /// 换 Token / 配置被指向另一个同结构库之后，远端查询**仍会成功**（新库合法且已共享），
    /// 但本地旧 page id 的 <c>GET /pages/{id}</c> 会全部返回 404 —— 不能据此判定"用户删了这些页面"。
    /// 记住"上一轮确认可信的库"后即可识别这种环境错位；首轮没有历史可比时只记录、不阻拦。
    /// </para>
    /// </summary>
    private const string SettingKeyDailyReconcileTargetDb = "notion_daily_reconcile_target_db";

    /// <summary>
    /// 「同日同游戏合并」的**待完成吸收记录**（JSON 数组，存 settings 表）。
    ///
    /// <para>
    /// 语义：把重复页的时长并进权威页**之前**先在这里登记"这一页已被权威页吸收、但归档还没成功"，
    /// 条目带 <c>CanonicalSecondsAfterAbsorb</c>（吸收完成后的权威页秒数）。
    /// 下一轮判断该页是否真的被吸收，一律**以远端权威页的当前秒数是否已达到该值为准** ——
    /// 于是"写完权威页但进程崩溃"也能靠远端状态判定，不依赖内存、也不依赖本地写入顺序。
    /// 归档成功后立刻删除条目；页面消失（归档成功/被删）时也会被清理。
    /// </para>
    /// </summary>
    private const string SettingKeyPendingMergeAbsorb = "notion_daily_pending_merge_absorb";

    /// <summary>游戏总表快照最近一次**成功**刷新的 UTC 时间（ISO-8601）。用于判断目录快照是否可信。</summary>
    private const string SettingKeyGameCatalogRefreshedAtUtc = "notion_game_catalog_refreshed_at_utc";

    /// <summary>目录快照的"新鲜"窗口：超过它就不再拿这份快照做"总表已不存在"的判定（宁可多导入，不误丢）。</summary>
    private static readonly TimeSpan CatalogFreshnessWindow = TimeSpan.FromHours(24);

    public event EventHandler<string>? SyncStatusChanged;

    public bool IsNotionConfigured => _config.IsNotionConfigured;

    public NotionSyncService(
        IDatabaseRepository repo,
        INotionClient client,
        TrackerConfig config,
        IGameMatcher? matcher = null,
        Covers.CoverCacheService? coverCache = null,
        DailyIconUploadService? iconUploads = null)
    {
        _repo = repo;
        _client = client;
        _config = config;
        _matcher = matcher ?? new GameMatcher();
        _coverCache = coverCache ?? new Covers.CoverCacheService();
        _iconUploads = iconUploads ?? new DailyIconUploadService(
            client,
            repo as GameTimeTracker.Core.Notion.INotionIconMappingStore ?? NullIconMappingStore.Instance);
    }

    /// <summary>
    /// 推送每日记录时，标题该用哪个名字。
    /// </summary>
    /// <remarks>
    /// 设计意图（用户要求）：
    /// 每日记录的「游戏名称」以前一直用本地进程名（`games.name`）。但游戏一旦绑定到总表，
    /// 用户在总表里改过的名字（可能已本地化成中文）才是他真正想看到的，
    /// 而 relation 指向的就是那个条目 —— 所以已绑定时以总表名称为准。
    ///
    /// ⚠️ 2026-10-06 变更：本方法**不再**决定 page icon。
    /// 以前 Daily 图标取的是总表条目的方形小图（`game_catalog.icon_url`）；现在改为
    /// **本地封面文件 → SHA-256 → 复用/上传得到的 file_upload**（见 <see cref="ResolveDailyIconUploadIdAsync"/>），
    /// 与 Master（总表）图标**完全解耦** —— 总表图标怎么变都不会驱动 Daily 图标。
    ///
    /// 未绑定时保持原样（用进程名）。
    /// </remarks>
    private async Task<string> ResolveDailyDisplayAsync(string localName, string? gameNotionPageId)
    {
        if (string.IsNullOrWhiteSpace(gameNotionPageId)) return localName;

        try
        {
            var cat = await _repo.GetCatalogItemByPageIdAsync(gameNotionPageId);
            if (cat == null) return localName;

            return string.IsNullOrWhiteSpace(cat.Name) ? localName : cat.Name;
        }
        catch (Exception ex)
        {
            // 取目录失败不该阻断同步本身；记录警告并退回进程名即可。
            AppLog.Warn($"[ResolveDailyDisplayAsync] 读取总表条目异常 ({gameNotionPageId}): {ex.Message}");
            return localName;
        }
    }

    /// <summary>
    /// **Daily 图标的唯一来源**：本地 CoverCache 文件 → SHA-256 → 复用/上传 → <c>file_upload_id</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 Master 链路**完全独立**：这里**绝不**读 <c>game_catalog.icon_url</c>（总表页图标）。
    /// 本地没有可用封面文件时返回 null —— 本次就不设图标，既不回退 Master 图标，也不猜 Steam CDN。
    /// </remarks>
    private async Task<string?> ResolveDailyIconUploadIdAsync(string? gameNotionPageId, string? localGameName = null)
    {
        try
        {
            var game = !string.IsNullOrWhiteSpace(gameNotionPageId)
                ? await _repo.GetGameByNotionIdAsync(gameNotionPageId!)
                : null;

            if (game == null && !string.IsNullOrWhiteSpace(localGameName))
            {
                var all = await _repo.GetAllGamesAsync();
                game = all.FirstOrDefault(g => string.Equals(g.Name, localGameName, StringComparison.OrdinalIgnoreCase));
            }

            if (game == null || string.IsNullOrWhiteSpace(game.Platform) || string.IsNullOrWhiteSpace(game.PlatformId))
                return null;

            var coverPath = _coverCache.GetCoverPath(game.Platform, game.PlatformId);
            if (!GameTimeTracker.Core.Covers.CoverFileProbe.IsUsable(coverPath)) return null;

            return await _iconUploads.EnsureUploadedAsync(coverPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[DailyIcon] 解析本地封面失败（page={gameNotionPageId}）: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 推送成功后，把"远端现在长什么样"记进本地快照。
    /// </summary>
    /// <remarks>
    /// 不记的话本地状态就是**明知故犯地错**：明明刚把图标写上去，快照还写着"没有图标"，
    /// 于是下一轮回刷会为这条记录多做一次完全多余的 PATCH。
    /// 记下来之后，不变式成立：`notion_title` / `notion_icon_url` 始终等于
    /// "我们最近一次写入或观察到的远端值"。
    ///
    /// 快照只是优化手段，写失败不该影响推送本身 —— 下一轮回刷会自然补上，所以这里吞异常。
    /// </remarks>
    private async Task RecordRemoteSnapshotAsync(
        string pageId, string displayName, int durationMinutes, string? iconUrl, int? durationSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return;

        try
        {
            // 快照必须与刚才**实际写上去**的标题逐字一致（否则下一轮回刷会以为要更新，反复 PATCH）。
            var expectedTitle = durationSeconds is { } secs
                ? DailyRecordTitle.BuildFromHours(displayName, DailyRecordTitle.SecondsToHours(secs))
                : DailyRecordTitle.Build(displayName, durationMinutes);

            await _repo.UpdateDailyRecordFromNotionAsync(pageId, expectedTitle, iconUrl);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入每日记录快照失败（page={pageId}）: {ex.Message}");
        }
    }

    public async Task RefreshGameCatalogCacheAsync()
    {
        if (!_config.IsNotionConfigured) return;

        try
        {
            SyncStatusChanged?.Invoke(this, "正在刷新游戏总表...");
            var items = await _client.QueryGameMasterAsync(_config.GameDatabaseId);

            // 只有拿到「非空且完整」的结果才动缓存：
            // 空结果通常意味着权限/网络异常，此时清理缓存会把整个库清空。
            if (items != null && items.Count > 0)
            {
                await _repo.UpsertCatalogItemsAsync(items);

                // 🛡️ 2026-10-14：记录"这份总表快照是刚刚成功刷新过的" ——
                //    每日记录导入只会用**新鲜**快照做"总表里已不存在"的判定（见 PullDailyRecordsFromNotionAsync），
                //    否则会把"本地还没同步到"误判成"总表已删除"而静默丢弃手录记录。
                try
                {
                    await _repo.SetSettingAsync(SettingKeyGameCatalogRefreshedAtUtc, DateTime.UtcNow.ToString("O"));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[游戏总表] 记录快照刷新时间失败（下一步会按「不新鲜」处理）: {ex.Message}");
                }

                // 2j：把总表「已忽略」落到本地（该库没有这个属性时整段跳过，老用户零影响）。
                // 放在这里而不是 Pull 之后：被忽略的游戏不该先被拉出每日记录再被忽略。
                await ApplyCatalogIgnoredStateAsync(items);

                // 之前这里只增不删，导致总表删掉的条目、以及早期测试留下的假 page_id 一直沉淀在本地，
                // 还会出现在手动绑定的候选列表里（选中就 404）。
                // 现在让 game_catalog 成为总表的忠实快照，删除对账也依赖这份快照。
                var removed = await _repo.DeleteCatalogItemsNotInAsync(items.Select(i => i.PageId));
                SyncStatusChanged?.Invoke(this,
                    removed > 0 ? $"游戏总表快照已同步（清理 {removed} 条已失效条目）" : $"游戏总表快照已是最新（共 {items.Count} 款）");
                return;
            }

            SyncStatusChanged?.Invoke(this, "游戏总表快照已是最新");
        }
        catch (Exception ex)
        {
            // ⚠️ 2026-10-13：这一步**只做一件事** —— 查询游戏总表数据库（`POST /databases/{id}/query`），
            //    因此失败原因必然出在"总表数据库 ID / 授权"上（每日时长表那条链路是独立的，可能仍然全绿，
            //    症状就是"别的都正常，只有总表刷新一闪而过地失败"）。
            //    把"是哪一步 + 是哪个 ID"记进日志，避免下次只能靠猜。
            AppLog.Warn($"[游戏总表] 刷新失败（数据库 id={Fingerprint(_config.GameDatabaseId)}）：{ex.Message}"
                + "；本机已有的总表快照与游戏映射**不受影响**，下一轮同步会自动重试");

            SyncStatusChanged?.Invoke(this, $"更新失败: {FriendlyNotionError(ex)}");
        }
    }

    /// <summary>
    /// 给日志用的 ID 指纹：只取前 8 位（够区分是哪个库，又不必把整串 id 铺在日志里）。
    /// 空/过短时原样返回，避免"看起来像有个 id"。
    /// </summary>
    private static string Fingerprint(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "（未配置）";
        var clean = id.Replace("-", "");
        return clean.Length <= 8 ? clean : clean[..8] + "…";
    }

    /// <summary>一条"已并入权威页、但重复页归档尚未成功"的吸收记录（持久化在 settings 里）。</summary>
    /// <remarks>
    /// ⚠️ 2026-10-14 阶段 A（独立差异审查 F1/F2/F3）：**不再用"远端秒数 ≥ 目标"判定写入成功** ——
    /// 用户手工把时长改大/改小都会落进那个不等式里，导致误归档或覆盖用户手改值。
    /// 现在记录三样东西，用**整条远端口径指纹**做证据：
    ///   <see cref="LastSeenSignature"/>        写入前本机看到的远端样子（= 写入没落库时远端应该还是它）；
    ///   <see cref="ExpectedSignatureAfterWrite"/> 本机写入**之后**远端应有的样子（= 写入确认落库的证据）；
    /// 两者都不匹配 ⇒ 远端被本机之外改过（用户手改 / 无法确认）⇒ 停止破坏性步骤、保留状态等下轮复核。
    /// </remarks>
    private sealed class PendingMergeAbsorb
    {
        public string PageId { get; set; } = "";
        public string CanonicalPageId { get; set; } = "";
        public int CanonicalSecondsAfterAbsorb { get; set; }
        public double WrittenHours { get; set; }
        public string LastSeenSignature { get; set; } = "";
        public string ExpectedSignatureAfterWrite { get; set; } = "";
    }

    /// <summary>合并用的"秒"口径（老数据/测试构造的 item 没有 DurationSeconds 时退回 minutes × 60）。</summary>
    private static int EffectiveSeconds(NotionDailyRecordItem item)
        => item.DurationSeconds > 0 ? item.DurationSeconds : item.DurationMinutes * 60;

    /// <summary>
    /// 远端权威页的**指纹**：秒数 | 标题 | Relation | 图标类型。
    /// 只做"是否与某个已知状态完全一致"的比较，不用大小关系推断"谁写的"。
    /// 图标只取类型（file_upload 的临时 URL 每天会变，取 URL 会造成假"被改过"）。
    /// </summary>
    private static string CanonicalSignatureOf(NotionDailyRecordItem item)
        => $"{EffectiveSeconds(item)}|{(item.RawTitle ?? item.GameTitle ?? "").Trim()}"
           + $"|{NormalizePageId(item.GameMasterPageId)}|{item.IconType ?? "-"}";

    /// <summary>
    /// 本机写入**之后**远端应有的指纹：秒数按 0.01h 分辨率量化、标题按写入 payload 的规则拼装。
    /// 只有远端整条指纹等于它，才算"本机写入已确认"（也才允许归档重复页）。
    /// </summary>
    private static string ExpectedSignatureAfterWrite(
        NotionDailyRecordItem canonical, int totalSeconds, int totalMinutes,
        string displayName, string? masterPageId, bool iconWillBeWritten)
    {
        var expectedSeconds = (int)Math.Round(DailyRecordTitle.SecondsToHours(totalSeconds) * 3600);
        var expectedTitle = DailyRecordTitle.Build(displayName, totalMinutes);
        var expectedIcon = iconWillBeWritten ? "file_upload" : (canonical.IconType ?? "-");
        return $"{expectedSeconds}|{expectedTitle}|{NormalizePageId(masterPageId)}|{expectedIcon}";
    }

    private async Task<List<PendingMergeAbsorb>> LoadPendingMergeAbsorbAsync()
    {
        try
        {
            var json = await _repo.GetSettingAsync(SettingKeyPendingMergeAbsorb);
            if (string.IsNullOrWhiteSpace(json)) return new List<PendingMergeAbsorb>();
            return System.Text.Json.JsonSerializer.Deserialize<List<PendingMergeAbsorb>>(json)
                   ?? new List<PendingMergeAbsorb>();
        }
        catch (Exception ex)
        {
            // 解析失败按"没有待完成记录"处理：下一轮会重新按远端值推断吸收状态，不会凭坏数据删改任何东西。
            AppLog.Warn($"[同步] 读取待完成合并记录失败（按空处理）: {ex.Message}");
            return new List<PendingMergeAbsorb>();
        }
    }

    private async Task SavePendingMergeAbsorbAsync(List<PendingMergeAbsorb> entries)
    {
        try
        {
            // 单条 settings upsert（仓库写入锁内原子完成）：要么整份旧值、要么整份新值，不会写半份。
            await _repo.SetSettingAsync(SettingKeyPendingMergeAbsorb,
                System.Text.Json.JsonSerializer.Serialize(entries));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[同步] 保存待完成合并记录失败（不影响本轮，下一轮会按远端值重新推断）: {ex.Message}");
        }
    }

    /// <summary>
    /// 本地总表快照是否**新鲜**（最近一次成功刷新在 <see cref="CatalogFreshnessWindow"/> 内）。
    /// 不新鲜 / 从未刷新 ⇒ 不能拿它判定"总表里没有这个游戏"。
    /// </summary>
    private async Task<(bool Fresh, string Description)> IsGameCatalogFreshAsync()
    {
        var raw = await _repo.GetSettingAsync(SettingKeyGameCatalogRefreshedAtUtc);
        if (string.IsNullOrWhiteSpace(raw) ||
            !DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
        {
            return (false, "从未成功刷新过（无时间戳）");
        }

        var fresh = DateTime.UtcNow - at.ToUniversalTime() <= CatalogFreshnessWindow;
        return (fresh, $"最近成功刷新 {at.ToUniversalTime():yyyy-MM-dd HH:mm} UTC（{(fresh ? "新鲜" : "已过期")}）");
    }

    public async Task<int> PullDailyRecordsFromNotionAsync()
    {
        if (!_config.IsNotionConfigured) return 0;

        try
        {
            SyncStatusChanged?.Invoke(this, "正在从每日时长表拉取记录...");
            var rawRecords = await _client.QueryDailyRecordsAsync(_config.DailyDatabaseId);
            _lastPullSucceeded = true;
            // 🛡️ 先记录"原始查询返回了什么"，再让后续解析/过滤/合并去动业务快照。
            _lastPulledRawPageIds = rawRecords
                .Select(r => NormalizePageId(r.PageId))
                .Where(id => id.Length > 0)
                .ToHashSet();
            int synced = 0;
            int skippedOrphan = 0;
            int failed = 0;
            string? lastError = null;

            // 防"复活"：每日记录若指向一个已不在总表里的游戏（总表条目被删了），
            // 就不能再拉回本地 —— 否则 SyncDailyRecordFromNotionAsync 会凭空把游戏造回来，
            // 于是你在程序里刚删掉的游戏下一轮同步又冒出来。
            var catalogItems = await _repo.GetCatalogItemsAsync();
            var catalogIds = catalogItems
                .Select(c => NormalizePageId(c.PageId))
                .Where(id => id.Length > 0)
                .ToHashSet();

            // 🛡️ 2026-10-14（P5）：目录快照的**新鲜度**决定它能不能用来判定"总表里没有这个游戏"。
            //    快照不新鲜 / 刷新失败 / 从未刷新 ⇒ **不做孤儿过滤**（宁可多导入，也不静默丢弃手录记录）。
            var (catalogFresh, catalogFreshnessReason) = await IsGameCatalogFreshAsync();
            var useOrphanFilter = catalogIds.Count > 0 && catalogFresh;
            if (catalogIds.Count > 0 && !catalogFresh)
            {
                AppLog.Warn($"[同步] 本地游戏总表快照不可信（{catalogFreshnessReason}）⇒ 本轮**不做**"
                            + "「总表已不存在」过滤，所有每日记录照常导入（避免把「本地尚未同步」误判成「总表已删除」）");
            }

            var validRecords = new List<NotionDailyRecordItem>();
            foreach (var r in rawRecords)
            {
                if (useOrphanFilter &&
                    !string.IsNullOrWhiteSpace(r.GameMasterPageId) &&
                    !catalogIds.Contains(NormalizePageId(r.GameMasterPageId)))
                {
                    skippedOrphan++;
                    // 逐条诊断（最多 10 条明细），并明确写出判定依据与快照状态，便于人工核对是否误跳。
                    if (skippedOrphan <= 10)
                    {
                        AppLog.Warn($"[同步] 每日页面 {r.PageId}（{r.Date}）的关联游戏 {r.GameMasterPageId} "
                                    + $"不在**新鲜**的总表快照中（{catalogFreshnessReason}）⇒ 本轮跳过导入"
                                    + "（防「已删游戏复活」；页面本身未被修改，快照刷新后下一轮会重新导入）");
                    }
                    continue;
                }
                validRecords.Add(r);
            }

            // 自动检测并合并 Notion 远端同日同一游戏的多个重复页面（累计合并并清理冗余页面）
            var groupedRecords = new Dictionary<string, List<NotionDailyRecordItem>>();
            foreach (var r in validRecords)
            {
                var date = r.Date?.Trim() ?? "";
                string gameKey;
                // 🛡️ 2026-10-14（P7）：游戏身份**以 Relation 指向的主表页面 ID 为准** ——
                //    显示名可能重名，不能替代可用的页面 ID（否则"不同游戏同名"会被并成一条）。
                //    完全没有 Relation 的记录才退回"标题基名"归组，而且这类组还要额外的归属证据才允许合并/归档。
                var normMasterId = NormalizePageId(r.GameMasterPageId);
                gameKey = !string.IsNullOrEmpty(normMasterId)
                    ? $"m:{normMasterId}"
                    : "nolink|" + DailyRecordTitle.ExtractBaseName(r.GameTitle ?? "").Trim().ToLowerInvariant();

                var key = $"{date}|{gameKey}";
                if (!groupedRecords.TryGetValue(key, out var list))
                {
                    list = new List<NotionDailyRecordItem>();
                    groupedRecords[key] = list;
                }
                list.Add(r);
            }

            var deduplicatedRecords = new List<NotionDailyRecordItem>();
            var pendingAbsorb = await LoadPendingMergeAbsorbAsync();
            var pendingChanged = false;
            var mergeIncomplete = 0;          // 写入或归档没走完的重复记录（已保留原值，下一轮重试）
            var mergeSkippedNoIdentity = 0;   // 归属无法确认 ⇒ 既不合并也不归档的组
            var mergeIndeterminate = 0;       // 权威页在本机待确认期间被改过 ⇒ 本轮停止破坏性步骤的组

            // 清理已经不用再管的吸收记录：重复页或权威页已不在本轮查询结果里（归档成功 / 页面已消失）。
            var pendingBeforePrune = pendingAbsorb.Count;
            pendingAbsorb.RemoveAll(e =>
                !_lastPulledRawPageIds.Contains(NormalizePageId(e.PageId))
                || !_lastPulledRawPageIds.Contains(NormalizePageId(e.CanonicalPageId)));
            if (pendingAbsorb.Count != pendingBeforePrune) pendingChanged = true;

            foreach (var entry in groupedRecords)
            {
                var group = entry.Value;
                if (group.Count == 1)
                {
                    deduplicatedRecords.Add(group[0]);
                    continue;
                }

                // 同一天同一游戏在 Notion 存在多个页面（例如用户截图中的 3 条记录）！
                // 1. 选取权威主页面：优先匹配本地已绑定的 pageId，否则选时长最大的
                var groupDate = group[0].Date;
                var localRows = await _repo.GetDailySummariesByDateAsync(groupDate);
                var matchedLocal = localRows.FirstOrDefault(l => group.Any(g => NormalizePageId(g.PageId) == NormalizePageId(l.NotionPageId)));

                // 🛡️ 2026-10-14 归属证据闸门（P6/P7）：
                //    有 Relation 的组：游戏身份 = Relation 指向的主表页面 ID（归组键已按它分组）⇒ 归属已证明；
                //    没有 Relation 的组：**只有"组内至少一条页面被本地每日行引用"才能证明归属**
                //    （本地行=本机创建/认领过这条页面 ⇒ 属于同一个本地游戏）。
                //    两者都不满足 ⇒ 无法确认是否同一条业务记录 ⇒ 本组**既不合并也不归档**，原样保留并留诊断。
                var groupHasRelation = !string.IsNullOrEmpty(NormalizePageId(group[0].GameMasterPageId));
                if (!groupHasRelation && matchedLocal == null)
                {
                    mergeSkippedNoIdentity++;
                    AppLog.Warn($"[同步] 同一日期「{groupDate}」下有 {group.Count} 条**没有关联游戏**的每日页面"
                                + $"（{string.Join(", ", group.Select(g => g.PageId))}），且没有任何一条被本机每日记录引用 —— "
                                + "无法确认它们属于同一个游戏，本组既不合并不归档（页面与时长原样保留）");
                    deduplicatedRecords.AddRange(group);
                    continue;
                }

                var canonical = matchedLocal != null
                    ? group.First(g => NormalizePageId(g.PageId) == NormalizePageId(matchedLocal.NotionPageId))
                    : group.OrderByDescending(EffectiveSeconds).First();

                var duplicates = group.Where(g => g.PageId != canonical.PageId).ToList();

                // 2. 已经在**更早轮次**被吸收、但归档还没成功的重复页：不再计入合计（否则每轮 +1 份 ⇒ 膨胀）。
                //    阶段 A（F1/F2/F3）：证据从"秒数 ≥ 目标"改成**整条远端口径指纹的三态判定** ——
                //      · 等于"写入后的预期"   ⇒ 本机写入确认落库 ⇒ 已吸收（可归档）；
                //      · 等于"写入前看到的样子" ⇒ 本机那次写入确实没落库，且用户没动过 ⇒ 作废条目、重新计入（安全重试）；
                //      · 两者都不是          ⇒ 远端被本机之外改过（用户手改时长/标题/Relation/图标，或无法确认）
                //                             ⇒ **整组停止破坏性步骤**：不写权威页、不归档重复页、保留条目等下轮复核。
                var canonicalSecondsNow = EffectiveSeconds(canonical);
                var canonicalSignatureNow = CanonicalSignatureOf(canonical);
                var alreadyAbsorbed = new List<NotionDailyRecordItem>();
                var groupLatched = false;
                var latchReason = "";
                foreach (var dup in duplicates)
                {
                    var dupKey = NormalizePageId(dup.PageId);
                    var rec = pendingAbsorb.FirstOrDefault(e =>
                        NormalizePageId(e.PageId) == dupKey
                        && NormalizePageId(e.CanonicalPageId) == NormalizePageId(canonical.PageId));
                    if (rec == null) continue;

                    if (rec.ExpectedSignatureAfterWrite.Length > 0
                        && canonicalSignatureNow == rec.ExpectedSignatureAfterWrite)
                    {
                        alreadyAbsorbed.Add(dup);
                    }
                    else if (rec.LastSeenSignature.Length > 0
                             && canonicalSignatureNow == rec.LastSeenSignature)
                    {
                        // 权威页与本机上次看到的一模一样 ⇒ 那次写入确实没落库 ⇒ 作废条目，让本轮重新计入合计。
                        pendingAbsorb.Remove(rec);
                        pendingChanged = true;
                        AppLog.Info($"[同步] 待完成合并记录作废（权威页 {canonical.PageId} 与写入前一致"
                                    + $"「{rec.LastSeenSignature}」）⇒ 重复页 {dup.PageId} 重新计入合计");
                    }
                    else
                    {
                        groupLatched = true;
                        latchReason = $"当前「{canonicalSignatureNow}」既不是写入后的预期「{rec.ExpectedSignatureAfterWrite}」，"
                                      + $"也不是写入前看到的「{rec.LastSeenSignature}」";
                    }
                }

                if (groupLatched)
                {
                    mergeIndeterminate++;
                    AppLog.Warn($"[同步] 权威页 {canonical.PageId} 在本机待确认合并期间被改过（{latchReason}）—— "
                                + "无法确认程序合计是否已经落库，本轮**不写权威页、不归档重复页**，"
                                + "保留待处理状态（页面与时长原样保留，下轮继续复核；不会重复累加）");
                    deduplicatedRecords.Add(canonical);
                    continue;
                }

                var newDuplicates = duplicates.Except(alreadyAbsorbed).ToList();

                // 3. 累计合并总时长：**按秒**求和/取大，最后只换算一次
                //    （逐页按分钟截断再相加会让每次合并都吃掉最多 59 秒；已吸收未归档的那部分不再重复相加）
                int sumSeconds = canonicalSecondsNow + newDuplicates.Sum(EffectiveSeconds);
                int localSeconds = matchedLocal?.DurationSeconds ?? 0;
                int totalSeconds = Math.Max(sumSeconds, localSeconds);
                int totalMinutes = GameTimeTracker.Core.Services.DurationMath.MinutesFromSeconds(totalSeconds);

                var masterPageId = !string.IsNullOrEmpty(canonical.GameMasterPageId)
                    ? canonical.GameMasterPageId
                    : group.FirstOrDefault(g => !string.IsNullOrEmpty(g.GameMasterPageId))?.GameMasterPageId;

                var displayName = await ResolveDailyDisplayAsync(canonical.GameTitle, masterPageId);
                // Daily 图标只来自本地封面文件（与 Master/总表图标完全独立）。
                // 合并到已有页面时，页面已有任何 Page Icon（emoji / external / file_upload）都不得覆盖；
                // 只有页面当前没有图标时才补本机封面。
                var iconUploadId = canonical.HasIcon
                    ? null
                    : await ResolveDailyIconUploadIdAsync(masterPageId, canonical.GameTitle);

                // 4. 先把"待吸收"落盘（在写权威页**之前**）：写完就崩溃也能靠远端指纹判定吸收是否真的发生。
                //    同时记下"写入前的样子"与"写入后的预期样子"，恢复时用它们区分
                //    「本机写入没落库」/「本机写入已落库」/「用户改过」三种情形。
                if (newDuplicates.Count > 0)
                {
                    var expectedSignature = ExpectedSignatureAfterWrite(
                        canonical, totalSeconds, totalMinutes, displayName, masterPageId,
                        iconWillBeWritten: !canonical.HasIcon && iconUploadId != null);
                    foreach (var dup in newDuplicates)
                    {
                        pendingAbsorb.Add(new PendingMergeAbsorb
                        {
                            PageId = NormalizePageId(dup.PageId),
                            CanonicalPageId = NormalizePageId(canonical.PageId),
                            CanonicalSecondsAfterAbsorb = totalSeconds,
                            WrittenHours = DailyRecordTitle.SecondsToHours(totalSeconds),
                            LastSeenSignature = canonicalSignatureNow,
                            ExpectedSignatureAfterWrite = expectedSignature
                        });
                    }
                    pendingChanged = true;
                    await SavePendingMergeAbsorbAsync(pendingAbsorb);
                }

                // 5. 写权威页：**成功才继续归档**。写失败 ⇒ 不归档、也不把未确认的合计值当作远端现状。
                //    没有新吸收、合计也没变（例如"已吸收未归档"的续轮）⇒ 不必再写一次远端，只继续重试归档。
                var needWrite = newDuplicates.Count > 0 || totalSeconds != canonicalSecondsNow;
                var writeOk = !needWrite;
                if (needWrite)
                try
                {
                    await _client.UpdateDailyRecordAsync(
                        canonical.PageId,
                        totalMinutes,
                        masterPageId,
                        displayName,
                        iconUrl: null,
                        writeDuration: true,
                        durationSeconds: totalSeconds,
                        iconFileUploadId: iconUploadId);
                    writeOk = true;
                    AppLog.Info($"[同步] 自动合并 Notion 同日重复记录：「{displayName}」{canonical.Date} 合并 {group.Count} 个页面为单个页面（总时长 {totalMinutes} 分钟），已更新远端页面 {canonical.PageId}");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[同步] 更新 Notion 合并记录页面失败（本轮不归档重复页，下轮重试）: {ex.Message}");
                }

                if (!writeOk)
                {
                    mergeIncomplete++;
                    // ⚠️ **故意保留**本轮预登记的"待吸收"记录（不撤销）：
                    //    "写失败"与"请求已发出但响应丢失"在本机看来是同一种异常，无法区分。
                    //    · 若远端确实没写成功 ⇒ 下一轮权威页秒数仍 < 记录里的"吸收后秒数" ⇒ 自动作废、重新计入（不丢失）；
                    //    · 若远端其实写成功了 ⇒ 下一轮权威页秒数达标 ⇒ 判定为已吸收（不重复累加）。
                    //    两条路都不依赖内存与写入顺序，靠**远端值**兜底。
                    if (newDuplicates.Count > 0) await SavePendingMergeAbsorbAsync(pendingAbsorb);

                    // ⚠️ 关键（D2）：写入未确认 ⇒ 合计值**不是**远端现状 ⇒ 只把**权威页原值**交给本地导入；
                    //    否则本地会持有一个远端从未确认的值，下一轮 Pull 会把这个差异当成"用户下调声明"而收敛掉，
                    //    已经归档的重复页时长就再也找不回来了。
                    deduplicatedRecords.Add(canonical);
                    continue;
                }

                // 6. 合计已在远端确认 ⇒ 归档重复页（成功即删除对应吸收记录；失败则保留记录、下一轮只重试归档）
                foreach (var dup in duplicates)
                {
                    try
                    {
                        await _client.ArchivePageAsync(dup.PageId);
                        AppLog.Info($"[同步] 自动归档 Notion 冗余每日记录页面: {dup.PageId} (「{dup.GameTitle}」{dup.Date} {dup.DurationMinutes}m)");
                        var dupKey = NormalizePageId(dup.PageId);
                        var removed = pendingAbsorb.RemoveAll(e => NormalizePageId(e.PageId) == dupKey);
                        if (removed > 0) pendingChanged = true;
                    }
                    catch (Exception ex)
                    {
                        mergeIncomplete++;
                        AppLog.Warn($"[同步] 归档 Notion 重复页面失败 ({dup.PageId}): {ex.Message}"
                                    + "；其时长已并入权威页，本机已记住「已吸收未归档」，下一轮只重试归档、不重复累加");
                    }
                }

                var mergedRecord = new NotionDailyRecordItem
                {
                    PageId = canonical.PageId,
                    Date = canonical.Date,
                    DurationMinutes = totalMinutes,
                    // ⚠️ 必须同时带上**秒**：否则本地导入会退回"分钟 × 60"，把合计里不足 1 分钟的部分抹掉
                    //    （1 秒 + 1 分钟会变成 60 秒；逐页相加时每一页最多丢 59 秒）。
                    //    远端写入用的也是 totalSeconds，两边口径必须一致。
                    DurationSeconds = totalSeconds,
                    GameTitle = displayName,
                    GameMasterPageId = masterPageId,
                    RawTitle = DailyRecordTitle.Build(displayName, totalMinutes),
                    DurationUnitIsMinutes = false
                };

                deduplicatedRecords.Add(mergedRecord);
            }

            if (pendingChanged) await SavePendingMergeAbsorbAsync(pendingAbsorb);

            _lastPulledDailyRecords = deduplicatedRecords;

            foreach (var r in deduplicatedRecords)
            {
                try
                {
                    await _repo.SyncDailyRecordFromNotionAsync(r);
                    synced++;
                }
                catch (Exception ex)
                {
                    // 以前这里是空的 catch{}：单条失败会被完全吞掉，
                    // 表现为"同步成功"但数据没进来（executable_path NOT NULL 那个 bug 就是这么藏的）。
                    failed++;
                    lastError ??= $"{r.GameTitle}: {ex.Message}";
                }
            }

            var parts = new List<string>();
            parts.Add($"已从每日时长表同步 {synced} 条记录");
            if (skippedOrphan > 0)
            {
                parts.Add(catalogFresh
                    ? $"跳过 {skippedOrphan} 条关联游戏不在总表快照中的记录"
                    : $"跳过 {skippedOrphan} 条（目录快照不可信时也会在这里提示）");
            }
            if (mergeIncomplete > 0) parts.Add($"{mergeIncomplete} 条重复记录合并/归档未完成（已保留原值，下轮自动重试）");
            if (mergeSkippedNoIdentity > 0) parts.Add($"{mergeSkippedNoIdentity} 组重复记录因无法确认归属未合并（原样保留）");
            if (mergeIndeterminate > 0) parts.Add($"{mergeIndeterminate} 组重复记录因权威页被改动而暂缓（页面与时长原样保留，下轮复核）");
            if (failed > 0) parts.Add($"{failed} 条失败（{lastError}）");

            SyncStatusChanged?.Invoke(this, string.Join("，", parts));

            return synced;
        }
        catch (Exception ex)
        {
            _lastPullSucceeded = false;
            // 拉取失败时清空原始集合：不能让"上一轮的残留"看起来像"本轮查到了"。
            _lastPulledRawPageIds = new();
            SyncStatusChanged?.Invoke(this, $"每日时长表拉取失败: {FriendlyNotionError(ex)}");
            return 0;
        }
    }

    /// <summary>
    /// 在已拉取的 Notion 每日记录中查找是否存在指定游戏和日期的页面（防止向远端重复创建）
    /// </summary>
    private static NotionDailyRecordItem? FindMatchingNotionDailyRecord(
        IEnumerable<NotionDailyRecordItem>? records,
        string gameName,
        string? gameNotionId,
        string date)
    {
        if (records == null) return null;
        string targetDate = date.Trim();
        string targetMasterId = NormalizePageId(gameNotionId);
        string targetBaseName = DailyRecordTitle.ExtractBaseName(gameName).Trim();

        foreach (var r in records)
        {
            if (r.Date?.Trim() != targetDate) continue;

            if (!string.IsNullOrEmpty(targetMasterId) &&
                !string.IsNullOrEmpty(r.GameMasterPageId) &&
                string.Equals(NormalizePageId(r.GameMasterPageId), targetMasterId, StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }

            var rBaseName = DailyRecordTitle.ExtractBaseName(r.GameTitle ?? "").Trim();
            if (!string.IsNullOrEmpty(targetBaseName) &&
                !string.IsNullOrEmpty(rBaseName) &&
                string.Equals(rBaseName, targetBaseName, StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }

        return null;
    }

    public async Task<int> SyncPendingDailyRecordsAsync()
    {
        if (!_config.IsNotionConfigured) return 0;

        // 1. 先拉取 Notion 现有记录。
        //    用户可能在用本程序之前就手工记过时长，这些记录要把 notion_page_id 认回来；
        //    否则下面按"本地没有 notion_page_id 就新建"的判断会在 Notion 里造出重复行。
        await PullDailyRecordsFromNotionAsync();

        if (!_lastPullSucceeded)
        {
            // 拿不到 Notion 的现状就不上传：宁可这轮空跑，也不冒险产生重复记录。
            SyncStatusChanged?.Invoke(this, "拉取 Notion 记录失败，本次跳过上传（下轮自动重试）");
            return 0;
        }

        SyncStatusChanged?.Invoke(this, "正在同步本地记录到 Notion...");
        var pending = await _repo.GetPendingDailySummariesAsync();
        int count = 0;

        foreach (var item in pending)
        {
            try
            {
                var game = await _repo.GetGameByIdAsync(item.GameId);
                var gameNotionId = game?.NotionPageId;
                var isBound = !string.IsNullOrEmpty(gameNotionId);

                // 标题以总表（relation 指向的条目）为准：已绑定时用户在总表里改过的名字（多为中文名）
                // 才是他真正想看的。未绑定则退回进程名。
                // ⚠️ 2026-10-06：图标**不再**来自总表（Master）—— 只来自本地封面文件。
                var displayName = await ResolveDailyDisplayAsync(item.GameName, gameNotionId);

                // 查重：若本地尚未记录 notion_page_id，先在刚拉取的 Notion 记录中寻找同日同游戏的页面
                if (string.IsNullOrEmpty(item.NotionPageId))
                {
                    var existingRemote = FindMatchingNotionDailyRecord(_lastPulledDailyRecords, item.GameName, gameNotionId, item.Date);
                    if (existingRemote != null)
                    {
                        item.NotionPageId = existingRemote.PageId;
                        await _repo.UpdateDailySyncStatusAsync(item.Id, isBound ? "synced" : "unmapped", existingRemote.PageId);
                    }
                }

                if (string.IsNullOrEmpty(item.NotionPageId))
                {
                    // 新页面（本地无 page id、远端也没有同日同游戏记录）⇒ 直接带本机封面图标创建。
                    var iconForCreate = await ResolveDailyIconUploadIdAsync(gameNotionId, item.GameName);
                    var newPageId = await _client.CreateDailyRecordAsync(
                        _config.DailyDatabaseId,
                        item.Date,
                        displayName,
                        item.DurationMinutes,
                        gameNotionId,
                        iconUrl: null,
                        durationSeconds: item.DurationSeconds,
                        iconFileUploadId: iconForCreate);

                    await _repo.UpdateDailySyncStatusAsync(item.Id, isBound ? "synced" : "unmapped", newPageId);
                    // 快照只记"呈现"用的标题：file_upload 图标没有 URL 可存，故记 null；
                    // canonical seconds 仍要记录（标题/时长口径的追溯依据）。
                    await RecordRemoteSnapshotAsync(
                        newPageId, displayName, item.DurationMinutes, null, item.DurationSeconds);
                }
                else
                {
                    // ⚖️ 是否推送与"记录日期"**无关**（2026-10-13 规则变更）：
                    //    判据 = 本地是否存在**尚未同步的数据变动**（本地时长确实领先远端）。
                    //    · 本地领先 ⇒ 推送（标题按项目统一格式重建 + 时长）；
                    //    · 本地不领先 / 拿不到远端现状 ⇒ 一律不写远端（标题、图标、时长都不动），
                    //      只把本地状态对齐为 synced/unmapped。
                    var remoteDaily = _lastPulledDailyRecords?
                        .FirstOrDefault(x => NormalizePageId(x.PageId) == NormalizePageId(item.NotionPageId));

                    // 🛡️ 快照缺项保护（2026-10-14）：已绑定页面不在本次业务快照里 ⇒ **一律不写远端任何字段**。
                    //    "不在快照里" ≠ "页面不存在"：可能是解析丢弃（空日期/无标题）、孤儿过滤、重复合并折叠、
                    //    归档/回收站、未共享、403/404，或查询不完整 —— 只凭本次查询结果**无法**区分这些情况。
                    //    缺项时既拿不到远端现状，也无法判定"本地是否领先" ⇒ 绝不覆盖标题/时长/图标；
                    //    本地保持 pending（**不**置 synced/unmapped），等快照恢复后下一轮按规则推送。
                    if (remoteDaily is null)
                    {
                        var inRawQuery = _lastPulledRawPageIds.Contains(NormalizePageId(item.NotionPageId));
                        AppLog.Warn($"[同步] 已绑定页面 {item.NotionPageId} 不在本次业务快照中"
                                    + $"（{(inRawQuery ? "原始查询返回了该页，但被解析/孤儿/合并过滤" : "原始查询未返回该页：归档/未共享/无权限/查询不完整，均不可据此认定不存在")}）"
                                    + $"，为防止误覆盖远端，本轮跳过推送「{item.GameName} {item.Date}」并保持待同步状态");
                        continue;
                    }

                    var localSeconds = item.DurationSeconds > 0 ? item.DurationSeconds : item.DurationMinutes * 60;
                    var remoteSeconds = remoteDaily.DurationSeconds > 0
                        ? remoteDaily.DurationSeconds
                        : remoteDaily.DurationMinutes * 60;

                    // 能读到远端现状且本地**不领先** ⇒ 判定为"没有未同步变动"：本轮不写远端任何字段。
                    if (localSeconds <= remoteSeconds)
                    {
                        await _repo.UpdateDailySyncStatusAsync(item.Id, isBound ? "synced" : "unmapped");
                        continue;
                    }

                    // Page Icon：**只在页面当前没有任何图标时**才补；已有 emoji / external / file_upload
                    // 图标一律原样保留，绝不覆盖用户手动设置的内容。
                    var iconForUpdate = remoteDaily is { HasIcon: false }
                        ? await ResolveDailyIconUploadIdAsync(gameNotionId, item.GameName)
                        : null;

                    await _client.UpdateDailyRecordAsync(
                        item.NotionPageId, item.DurationMinutes, gameNotionId, displayName, iconUrl: null,
                        writeDuration: true,
                        durationSeconds: item.DurationSeconds,
                        iconFileUploadId: iconForUpdate);
                    await _repo.UpdateDailySyncStatusAsync(item.Id, isBound ? "synced" : "unmapped");
                    await RecordRemoteSnapshotAsync(
                        item.NotionPageId, displayName, item.DurationMinutes, null, item.DurationSeconds);
                }
                count++;
            }
            catch (Exception ex)
            {
                // 🛡️ 2026-10-14（数据丢失防护，与 BackfillRelationsAsync 同规则）：
                // 本分支的异常可能来自 CreateDailyRecordAsync（此时还没有 page id）、
                // UpdateDailyRecordAsync、图标上传/解析等多个环节，而异常文本在权限错误、
                // 网关错误或 Notion 文案调整时都可能带上 "archived" 字样。
                // 仅凭文本就执行 ArchiveDeletedAsync + DeleteDailySummariesAsync 会**不可逆地删掉本地时长**。
                // 现在：先判空 page id，再取可靠证据（404 / 回收站；归档 ≠ 删除）；
                // 取不到证据 ⇒ 保留记录并落 error 状态（保留原诊断能力），绝不删除。
                var confirmedDeleted = !string.IsNullOrEmpty(item.NotionPageId)
                    && await _client.IsPageDeletedAsync(item.NotionPageId!);
                if (ex.Message.Contains("archived") && !confirmedDeleted)
                {
                    AppLog.Warn($"[SyncPendingDailyRecordsAsync] 异常文本提到 archived，但未取得可靠的归档/删除证据"
                                + $"（{(string.IsNullOrEmpty(item.NotionPageId) ? "(无 page id：本条是新建失败)" : item.NotionPageId)}）"
                                + $"⇒ 保留本地记录与时长，不删除「{item.GameName} {item.Date}」");
                }

                if (confirmedDeleted)
                {
                    await _repo.ArchiveDeletedAsync("daily", "notion",
                        item.NotionPageId ?? item.Date,
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            item.Date,
                            Game = item.GameName,
                            item.DurationMinutes
                        }));
                    await _repo.DeleteDailySummariesAsync(new[] { item.Id });
                    SyncStatusChanged?.Invoke(this, $"「{item.GameName} {item.Date}」在 Notion 已被删除，本地记录已跟随清理");
                    continue;
                }

                await _repo.UpdateDailySyncStatusAsync(item.Id, "error", errorMessage: ex.Message);
            }
        }

        SyncStatusChanged?.Invoke(this, count > 0 ? $"已同步 {count} 条记录" : "全部记录已是最新");
        return count;
    }

    public async Task<int> BackfillRelationsAsync()
    {
        if (!_config.IsNotionConfigured) return 0;

        // 先补总表 page icon，再回填每日记录（Master 链路自身行为保持不变）。
        // ⚠️ 2026-10-06：每日记录的图标已改为"本地封面上传的 file_upload"，**不再**来自总表图标，
        // 所以这里的先后顺序对 Daily 图标已无影响；仍保留先补 Master 图标是为了不改变既有同步行为。
        await EnsureMasterPageIconsAsync();

        var unmapped = await _repo.GetUnmappedDailySummariesAsync();
        int backfilled = 0;

        // ⚠️ 2026-10-13：一旦判定为**致命**认证/权限错误（401/403），立即中止本轮逐条回填 ——
        //    同一个 Token 对后续每条记录都必然同样失败，继续只会把几百条各重试 3 次
        //    （真机故障：无效 Token 时这条链被拖到分钟级，期间设置页保存按钮被禁用）。
        var fatalAuthAborted = false;

        // 🛡️ 2026-10-14：「绑定状态」写入的单位是**页面**，而本轮有两个入口会覆盖同一批页面 ——
        //    ① 逐条回填里的"未绑定"分支（本地 unmapped 行）；
        //    ② 末尾的"远端绑定状态对齐"循环（直接遍历业务快照）。
        //    这里按归一化 page id 记账，保证**每个页面本轮只尝试写一次**：
        //    一条记录一轮打两次 Notion 没有意义（失败也不会因为立刻重打就成功），
        //    且"401/403 立即中止、普通网络错误逐条续跑"的既有语义不变 —— 每条记录本轮仍会被尝试一次。
        var bindingAttemptedPageIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in unmapped)
        {
            var game = await _repo.GetGameByIdAsync(item.GameId);
            if (game == null) continue;

            if (!string.IsNullOrEmpty(game.NotionPageId))
            {
                try
                {
                    // 与 SyncPendingDailyRecordsAsync 保持一致：改用总表名称 + 本地封面作为图标来源。
                    var displayName = await ResolveDailyDisplayAsync(item.GameName, game.NotionPageId);
                    var iconUploadId = await ResolveDailyIconUploadIdAsync(game.NotionPageId, item.GameName);

                    if (string.IsNullOrEmpty(item.NotionPageId))
                    {
                        var existingRemote = FindMatchingNotionDailyRecord(_lastPulledDailyRecords, item.GameName, game.NotionPageId, item.Date);
                        if (existingRemote != null)
                        {
                            item.NotionPageId = existingRemote.PageId;
                            await _repo.UpdateDailySyncStatusAsync(item.Id, "synced", existingRemote.PageId);
                        }
                    }

                    if (string.IsNullOrEmpty(item.NotionPageId))
                    {
                        var newPageId = await _client.CreateDailyRecordAsync(
                            _config.DailyDatabaseId,
                            item.Date,
                            displayName,
                            item.DurationMinutes,
                            game.NotionPageId,
                            iconUrl: null,
                            iconFileUploadId: iconUploadId);

                        await _repo.UpdateDailySyncStatusAsync(item.Id, "synced", newPageId);
                        await RecordRemoteSnapshotAsync(newPageId, displayName, item.DurationMinutes, null);
                    }
                    else
                    {
                        // 🛡️ 2026-10-14 快照缺项保护：已绑定页面不在本轮业务快照中 ⇒
                        // 既不创建替代页面、也不 PATCH 它（缺项 ≠ 页面已删除）。
                        // ⚠️ 历史行为在这里直接写：一旦远端因归档/回收站返回含 "archived" 的错误，
                        //    下面的 catch(1991) 会把**本地每日记录连同时长一起删除** ⇒ 属于不可逆的数据丢失。
                        if (_lastPulledDailyRecords?.Any(r => NormalizePageId(r.PageId) == NormalizePageId(item.NotionPageId)) != true)
                        {
                            AppLog.Warn($"[BackfillRelationsAsync] 已绑定页面 {item.NotionPageId} 不在本轮快照中"
                                        + $"（可能被解析/孤儿/合并过滤，或归档/未共享/查询不完整——不可据此认定已删除）"
                                        + $"⇒ 跳过写入并保留待处理「{item.GameName} {item.Date}」");
                            continue;
                        }

                        // 普通同步只补「关联游戏 / 绑定状态」这类**数据变化**：
                        // 已有历史 Daily 页面的标题与 Page Icon **一律不自动改写**，
                        // 唯一允许批量修改它们的入口是设置页「开始格式规范化」。
                        // writeDuration: false —— 时长以 Notion 上的为权威，绝不用本地缓存覆盖。
                        await _client.UpdateDailyRecordAsync(
                            item.NotionPageId, item.DurationMinutes, game.NotionPageId,
                            gameName: null, iconUrl: null, writeDuration: false,
                            titleOverride: null, durationSeconds: null, iconFileUploadId: null);
                        await _repo.UpdateDailySyncStatusAsync(item.Id, "synced");
                    }
                    backfilled++;
                }
                catch (Exception ex)
                {
                    // 🛡️ 2026-10-14（数据丢失防护）：不再仅凭"异常文本含 archived"就删除本地记录 ——
                    // 该文本在权限错误 / 网关错误 / Notion 文案调整时同样可能出现，
                    // 一旦误判就会**不可逆地删掉本地每日记录与其中的时长**。
                    // 现在只认可靠的远端证据：404 或回收站（IsPageDeletedAsync 的既有语义，
                    // 且它已明确"归档 ≠ 删除"）。取不到证据 ⇒ 保留记录并落 error，绝不删。
                    var confirmedDeleted = !string.IsNullOrEmpty(item.NotionPageId)
                        && await _client.IsPageDeletedAsync(item.NotionPageId!);
                    if (ex.Message.Contains("archived") && !confirmedDeleted)
                    {
                        AppLog.Warn($"[BackfillRelationsAsync] 异常文本提到 archived，但未取得可靠的归档/删除证据"
                                    + $"（{(string.IsNullOrEmpty(item.NotionPageId) ? "(无 page id)" : item.NotionPageId)}）"
                                    + "⇒ 保留本地记录，不删除");
                    }

                    if (confirmedDeleted)
                    {
                        await _repo.ArchiveDeletedAsync("daily", "notion",
                            item.NotionPageId ?? item.Date,
                            System.Text.Json.JsonSerializer.Serialize(new
                            {
                                item.Date,
                                Game = item.GameName,
                                item.DurationMinutes
                            }));
                        await _repo.DeleteDailySummariesAsync(new[] { item.Id });
                        SyncStatusChanged?.Invoke(this, $"「{item.GameName} {item.Date}」在 Notion 已被删除，本地记录已跟随清理");
                        continue;
                    }

                    await _repo.UpdateDailySyncStatusAsync(item.Id, "error", errorMessage: ex.Message);

                    // 致命认证/权限错误 ⇒ 中止整个循环（后续记录必然同样失败，不再逐条重试）。
                    if (IsFatalAuthError(ex))
                    {
                        fatalAuthAborted = true;
                        AppLog.Warn("[BackfillRelationsAsync] 检测到 Notion 认证/权限错误（401/403）⇒ "
                            + "中止本轮逐条回填（后续记录必然同样失败）");
                        break;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(item.NotionPageId))
            {
                // 该游戏尚未绑定总表，若 Notion 每日记录上的「绑定状态」尚未标明为「未绑定」，则写入「未绑定」
                var unboundPageKey = NormalizePageId(item.NotionPageId);
                var remote = _lastPulledDailyRecords?.FirstOrDefault(r => NormalizePageId(r.PageId) == unboundPageKey);

                // 🛡️ 2026-10-14 快照缺项保护（与 SyncPendingDailyRecordsAsync / 本方法"已绑定分支"同一规则）：
                //    本分支写的是**本地库里的 notion_page_id**，而它是否真的还在当前配置的每日库里、
                //    是否还存在，只能由本轮业务快照回答：缺项可能是解析丢弃（空日期/无标题）、孤儿过滤、
                //    重复合并折叠，也可能是归档 / 未共享 / 无权限 / 查询不完整 —— 只凭缺项无法区分。
                //    缺项 ⇒ 不 PATCH（不冒险打到一个与本机配置无关或已不在库里的页面）、
                //    不创建替代页面、不删除本地记录，保持本条待处理（unmapped / pending 不变），
                //    等快照恢复后下一轮按规则补写。
                if (remote == null)
                {
                    AppLog.Warn($"[BackfillRelationsAsync] 未绑定页面 {item.NotionPageId} 不在本轮快照中"
                                + $"（被解析/孤儿/合并过滤，或归档/未共享/无权限/查询不完整——缺项不足以认定页面存在或已删除）"
                                + $"⇒ 跳过「绑定状态」写入并保留待处理「{item.GameName} {item.Date}」");
                    continue;
                }

                // 远端「绑定状态」已经是「未绑定」⇒ 无需写入（远端状态本来就是对的，跳过以免每轮白打 Notion）。
                if (remote.Status == "未绑定") continue;

                try
                {
                    bindingAttemptedPageIds.Add(unboundPageKey);
                    await _client.UpdateDailyBindingStatusAsync(item.NotionPageId, "未绑定");
                    remote.Status = "未绑定";
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[BackfillRelationsAsync] 补全未绑定状态失败 ({item.NotionPageId}): {ex.Message}");

                    if (IsFatalAuthError(ex))
                    {
                        fatalAuthAborted = true;
                        AppLog.Warn("[BackfillRelationsAsync] 检测到 Notion 认证/权限错误（401/403）⇒ "
                            + "中止本轮逐条回写（后续记录必然同样失败）");
                        break;
                    }
                }
            }
        }

        // 统一对齐远端每日记录的绑定状态（例如历史记录或直接在 Notion 中添加但尚未有绑定状态的行）
        // ⚠️ 上面若已因致命认证/权限错误中止，这里整段跳过：否则等于把同一批无效请求再来一遍。
        // 🛡️ 2026-10-14 快照规则复核（与上面"未绑定分支"同一条规则，不得只保护其中一个入口）：
        //    · 本循环遍历的就是本轮业务快照（_lastPulledDailyRecords）⇒ 天然满足
        //      "仅当目标页出现在有效业务快照中才允许 PATCH"（页面就在集合里，不存在缺项）；
        //    · 但快照必须来自**本轮成功的拉取**：拉取失败时 _lastPulledDailyRecords 还留着上一轮的残留值，
        //      拿旧快照当"远端现状"回写绑定状态，同样属于"未经本轮确认就写远端" ⇒ 整段跳过；
        //    · 已在上面的逐条回填里尝试过的页面（bindingAttemptedPageIds）本轮不重复 PATCH。
        if (!fatalAuthAborted && _lastPullSucceeded && _lastPulledDailyRecords != null)
        {
            foreach (var r in _lastPulledDailyRecords)
            {
                if (string.IsNullOrEmpty(r.PageId)) continue;
                if (!bindingAttemptedPageIds.Add(NormalizePageId(r.PageId))) continue;

                if (string.IsNullOrEmpty(r.GameMasterPageId) && r.Status != "未绑定")
                {
                    try
                    {
                        await _client.UpdateDailyBindingStatusAsync(r.PageId, "未绑定");
                        r.Status = "未绑定";
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"[BackfillRelationsAsync] 同步远端未绑定状态失败 ({r.PageId}): {ex.Message}");

                        if (IsFatalAuthError(ex))
                        {
                            fatalAuthAborted = true;
                            AppLog.Warn("[BackfillRelationsAsync] 检测到 Notion 认证/权限错误（401/403）⇒ "
                                + "中止本轮远端绑定状态对齐（后续记录必然同样失败）");
                            break;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(r.GameMasterPageId) && r.Status != "已绑定")
                {
                    try
                    {
                        await _client.UpdateDailyBindingStatusAsync(r.PageId, "已绑定");
                        r.Status = "已绑定";
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"[BackfillRelationsAsync] 同步远端已绑定状态失败 ({r.PageId}): {ex.Message}");

                        if (IsFatalAuthError(ex))
                        {
                            fatalAuthAborted = true;
                            AppLog.Warn("[BackfillRelationsAsync] 检测到 Notion 认证/权限错误（401/403）⇒ "
                                + "中止本轮远端绑定状态对齐（后续记录必然同样失败）");
                            break;
                        }
                    }
                }
            }
        }

        if (fatalAuthAborted)
        {
            // 与"单条记录失败"区分：这是**整轮被致命错误中断**。
            AppLog.Warn($"[BackfillRelationsAsync] 本轮因 Notion 认证/权限错误提前结束（已回填 {backfilled} 条）。"
                + "Token 修复后下一轮同步会自动继续，本机数据未受影响");
        }

        if (backfilled > 0)
        {
            SyncStatusChanged?.Invoke(this, $"已为 {backfilled} 条每日记录关联游戏总表");
        }

        return backfilled;
    }

    /// <summary>
    /// 手工触发：一键扫描远端每日时长表中的历史记录，将记录标题规范化为「游戏名 · 时长 h」标准格式，
    /// 并对齐总表游戏名与页面图标。绝不修改时长数值与日期，未关联总表的记录安全跳过。
    /// </summary>
    public async Task<DailyTitleNormalizationResult> NormalizeHistoricalDailyTitlesAsync(
        IProgress<(int current, int total, string currentItem)>? progress = null,
        CancellationToken ct = default)
    {
        if (!_config.IsNotionConfigured)
        {
            return new DailyTitleNormalizationResult(0, 0, 0, 0, "Notion 未配置，请先在设置中填写配置");
        }

        try
        {
            SyncStatusChanged?.Invoke(this, "正在拉取 Notion 每日时长表进行历史格式检查...");
            var dailyRecords = await _client.QueryDailyRecordsAsync(_config.DailyDatabaseId);
            if (dailyRecords == null || dailyRecords.Count == 0)
            {
                return new DailyTitleNormalizationResult(0, 0, 0, 0, "Notion 每日时长表中无任何记录");
            }

            var catalogItems = await _client.QueryGameMasterAsync(_config.GameDatabaseId);
            var localGames = await _repo.GetAllGamesAsync();

            // 建立总表目录索引
            var catalogByPageId = new Dictionary<string, NotionGameCatalogItem>(StringComparer.OrdinalIgnoreCase);
            var catalogByName = new Dictionary<string, NotionGameCatalogItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in catalogItems)
            {
                if (!string.IsNullOrWhiteSpace(c.PageId))
                {
                    catalogByPageId[NormalizePageId(c.PageId)] = c;
                }
                if (!string.IsNullOrWhiteSpace(c.Name))
                {
                    catalogByName[c.Name.Trim()] = c;
                }
                foreach (var alias in c.Aliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        catalogByName.TryAdd(alias.Trim(), c);
                    }
                }
            }

            // 本地游戏快速索引 (name -> game, notionId -> game)
            var localGameByName = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in localGames)
            {
                if (!string.IsNullOrWhiteSpace(g.Name))
                {
                    localGameByName[g.Name.Trim()] = g;
                }
            }

            int total = dailyRecords.Count;
            int current = 0;
            int updated = 0;
            int skipped = 0;
            int errors = 0;

            foreach (var r in dailyRecords)
            {
                ct.ThrowIfCancellationRequested();
                current++;

                if (string.IsNullOrWhiteSpace(r.PageId))
                {
                    skipped++;
                    continue;
                }

                // 1. 寻找对应的总表条目
                NotionGameCatalogItem? matchedCatalog = null;

                // 1.1 先通过 relation 页面 ID 查找
                var masterId = NormalizePageId(r.GameMasterPageId);
                if (!string.IsNullOrEmpty(masterId) && catalogByPageId.TryGetValue(masterId, out var catByRel))
                {
                    matchedCatalog = catByRel;
                }

                // 1.2 若未通过 relation 找到，通过游戏名匹配总表名称或别名
                var baseName = DailyRecordTitle.ExtractBaseName(r.GameTitle ?? "").Trim();
                if (matchedCatalog == null && !string.IsNullOrEmpty(baseName))
                {
                    if (catalogByName.TryGetValue(baseName, out var catByName))
                    {
                        matchedCatalog = catByName;
                    }
                    else if (localGameByName.TryGetValue(baseName, out var localG) &&
                             !string.IsNullOrEmpty(localG.NotionPageId) &&
                             catalogByPageId.TryGetValue(NormalizePageId(localG.NotionPageId), out var catByLocal))
                    {
                        matchedCatalog = catByLocal;
                    }
                }

                // 1.3 若未匹配到总表游戏，绝对不盲目篡改！100% 保持原样跳过
                if (matchedCatalog == null)
                {
                    skipped++;
                    progress?.Report((current, total, $"跳过未关联游戏：{r.RawTitle}"));
                    continue;
                }

                // 1.4 若记录无有效时长（<= 0 分钟），安全跳过，避免写出「xxx · 0 h」
                if (r.DurationMinutes <= 0)
                {
                    skipped++;
                    progress?.Report((current, total, $"跳过无时长记录：{r.RawTitle}"));
                    continue;
                }

                // 2. 目标标题格式：{总表游戏名} · {时长} h
                var targetGameName = matchedCatalog.Name.Trim();
                var expectedTitle = DailyRecordTitle.Build(targetGameName, r.DurationMinutes);

                // 2b. Page Icon（用户裁定 · 原始需求）：**只在页面当前没有任何图标时**才补充本机当前封面的图标。
                //     页面已有 emoji / external / file_upload 图标时一律原样保留 ——
                //     绝不替换或覆盖用户手录的图标（"不修改其他属性"针对的是 时长/日期/关联/绑定状态等字段）。
                //     只在确实要补图标时才解析/上传（复用上传缓存：同一张图不会重复上传）。
                var iconUploadId = r.HasIcon
                    ? null
                    : await ResolveDailyIconUploadIdAsync(matchedCatalog.PageId, targetGameName);

                // 3. 是否需要动这条记录：标题不符 ⇒ 要改；标题已标准但页面缺图标 ⇒ 也要补图标。
                //    （原来只比标题，会在"标题已标准 + 缺图标"时整条跳过，导致图标永远补不上。）
                var titleAlreadyStandard = string.Equals(r.RawTitle?.Trim(), expectedTitle, StringComparison.Ordinal);
                if (titleAlreadyStandard && r.HasIcon)
                {
                    skipped++;
                    progress?.Report((current, total, $"格式已规范：{expectedTitle}"));
                    continue;
                }

                if (titleAlreadyStandard && iconUploadId is null)
                {
                    skipped++;
                    progress?.Report((current, total, $"格式已规范（本机无可补图标）：{expectedTitle}"));
                    continue;
                }

                // 4. 调用 UpdateDailyRecordTitleAsync 执行安全更新：**只改标题 + 可选图标**，
                //    绝不写「时长」「日期」「关联游戏」「绑定状态」。
                try
                {
                    progress?.Report((current, total, iconUploadId is null
                        ? $"正在规范化：{r.RawTitle} -> {expectedTitle}"
                        : $"正在规范化并补图标：{r.RawTitle} -> {expectedTitle}"));
                    var success = await _client.UpdateDailyRecordTitleAsync(
                        r.PageId,
                        expectedTitle,
                        iconUrl: null,
                        r.TitlePropertyName,
                        iconFileUploadId: iconUploadId);

                    if (success)
                    {
                        updated++;
                        // 同步更新本地记录快照，避免后续常规同步判定状态不一致
                        await RecordRemoteSnapshotAsync(r.PageId, targetGameName, r.DurationMinutes, null);
                    }
                    else
                    {
                        errors++;
                    }

                    // 速率限制：每次更新后延时 350ms，防止 Notion HTTP 429 速率限制
                    await Task.Delay(350, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors++;
                    AppLog.Warn($"[格式规范化] 更新记录失败 ({r.PageId} - {expectedTitle}): {ex.Message}");
                }
            }

            var summary = $"历史记录格式规范化完成！共扫描 {total} 条：规范化 {updated} 条，无需修改 {skipped} 条" +
                          (errors > 0 ? $"，失败 {errors} 条" : "");
            SyncStatusChanged?.Invoke(this, summary);
            return new DailyTitleNormalizationResult(total, updated, skipped, errors, summary);
        }
        catch (OperationCanceledException)
        {
            var msg = "历史数据格式规范化已取消";
            SyncStatusChanged?.Invoke(this, msg);
            return new DailyTitleNormalizationResult(0, 0, 0, 0, msg);
        }
        catch (Exception ex)
        {
            var msg = $"历史数据格式规范化失败: {FriendlyNotionError(ex)}";
            SyncStatusChanged?.Invoke(this, msg);
            return new DailyTitleNormalizationResult(0, 0, 0, 1, msg);
        }
    }

    /// <summary>
    /// 把各游戏的 Steam 官方图标 URL 同步写入 Notion 总表页面的 page icon。
    /// 只处理：已绑定总表 + steam + 纯数字 AppID + 页面**尚未设置图标**（避免覆盖用户手设的图标）。
    /// 说明：Notion API（项目锁定版本）的 icon 只接受 external URL / emoji，无法上传本地文件，
    /// 因此本机提取的桌面图标走不了这条路；对 Steam 游戏用官方 CDN 图标 URL 是最稳的等价实现。
    /// </summary>
    public async Task EnsureMasterPageIconsAsync()
    {
        try
        {
            var games = await _repo.GetAllGamesAsync();
            var catalog = await _repo.GetCatalogItemsAsync();
            var catByPageId = catalog
                .Where(c => !string.IsNullOrWhiteSpace(c.PageId))
                .GroupBy(c => c.PageId.Replace("-", "", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var game in games)
            {
                if (string.IsNullOrWhiteSpace(game.NotionPageId)) continue;
                if (!string.Equals(game.Platform, "steam", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(game.PlatformId) || !game.PlatformId.All(char.IsDigit)) continue;

                var key = game.NotionPageId.Replace("-", "", StringComparison.OrdinalIgnoreCase);
                if (!catByPageId.TryGetValue(key, out var cat)) continue;
                if (!string.IsNullOrEmpty(cat.IconType)) continue; // 页面已有图标（含 emoji），不覆盖

                var iconUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{game.PlatformId}/header.jpg";
                if (await _client.SetPageIconAsync(game.NotionPageId, iconUrl))
                {
                    cat.IconType = "external";
                    cat.IconUrl = iconUrl;
                    await _repo.UpsertCatalogItemsAsync(new[] { cat });
                    AppLog.Info($"已为总表页面「{game.Name}」写入 page icon");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"同步 page icon 失败（不影响其他同步）: {ex.Message}");
        }
    }

    /// <summary>
    /// 2j：把总表的「已忽略」落到本地 <c>games.status</c>。
    ///
    /// 语义（保守，详见附录 M）：
    ///   · 只做 **Notion true → 本地 ignored** 单向；Notion 的 false **不**反向覆盖本地
    ///     —— 否则"本地刚忽略、写回尚未生效"就会被下一轮拉取抹掉；
    ///   · 所有条目 <c>IsIgnored</c> 均为 null（该库没有「已忽略」属性）→ **整段跳过**，
    ///     老用户行为零变化；
    ///   · 只改 status，**绝不碰**时长 / 日期 / 绑定 / exe 路径。
    ///
    /// 为什么放在目录刷新之后：这一步依赖刚拉下来的总表快照，且必须在 Pull 之前
    /// （否则被忽略的游戏会先被拉出每日记录、再被忽略，出现一轮多余的"复活"）。
    /// </summary>
    /// <summary>
    /// 最近一次总表快照里"勾了忽略"的条目（2j）。
    ///
    /// 为什么要缓存：**新机首次恢复时，游戏行是 Pull 期间才被创建的** ——
    /// 目录刷新那一步执行时本地还没有对象可标记。所以启动链必须在 Pull 之后再调用
    /// 一次 <see cref="ApplyCatalogIgnoredStateAsync()"/>，否则"换机后忽略失效"这个
    /// 我们要修的行为恰恰会在最需要它的场景（全新机器）下复现。
    /// </summary>
    private readonly List<NotionGameCatalogItem> _catalogIgnoredItems = new();

    private async Task ApplyCatalogIgnoredStateAsync(IReadOnlyList<NotionGameCatalogItem> items)
    {
        _catalogIgnoredItems.Clear();
        _catalogIgnoredItems.AddRange(
            items.Where(i => i.IsIgnored == true && !string.IsNullOrWhiteSpace(i.PageId)));

        await ApplyCatalogIgnoredStateAsync();
    }

    /// <summary>2j：用最近一次总表快照里的「已忽略」条目对齐本地状态。**幂等**，可在 Pull 之后再调用。</summary>
    public async Task<int> ApplyCatalogIgnoredStateAsync()
    {
        try
        {
            var ignoredItems = _catalogIgnoredItems;

            // 属性不存在（全 null）或无人勾选 → 什么都不做（零行为变化）。
            if (ignoredItems.Count == 0) return 0;

            var games = await _repo.GetAllGamesAsync();
            var byPageId = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
            {
                if (!string.IsNullOrWhiteSpace(g.NotionPageId))
                {
                    byPageId.TryAdd(NormalizePageId(g.NotionPageId), g);
                }
            }

            int applied = 0;
            foreach (var item in ignoredItems)
            {
                if (!byPageId.TryGetValue(NormalizePageId(item.PageId), out var game)) continue;
                if (string.Equals(game.Status, "ignored", StringComparison.Ordinal)) continue;

                await _repo.UpdateGameStatusAsync(game.Id, "ignored");
                applied++;
                AppLog.Info($"[Notion] 总表标记「已忽略」→ 本地同步忽略：「{game.Name}」");
            }

            if (applied > 0)
            {
                SyncStatusChanged?.Invoke(this, $"已按总表「已忽略」同步 {applied} 款游戏");
            }

            return applied;
        }
        catch (Exception ex)
        {
            // 旁路能力：失败绝不允许影响换机恢复主链，更不允许影响本地计时。
            AppLog.Warn($"[Notion] 同步「已忽略」状态失败（不影响其他同步）：{ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 2j：本地忽略/恢复后把状态写回总表。**fire-and-forget 调用，绝不上抛、绝不阻塞 UI。**
    /// </summary>
    public async Task<bool> PushGameIgnoredStateAsync(int gameId, bool ignored)
    {
        if (!_config.IsNotionConfigured) return false;

        try
        {
            var game = await _repo.GetGameByIdAsync(gameId);
            if (game is null) return false;

            if (string.IsNullOrWhiteSpace(game.NotionPageId))
            {
                // 未绑定总表 = 没有可写的地方（未绑定游戏本就没有远端身份）。不是错误。
                return false;
            }

            if (!_client.SupportsGameIgnoredProperty)
            {
                // 属性探测未命中（该库没有「已忽略」属性，或本轮尚未查过总表）→ 绝不写。
                AppLog.Info("[Notion] 总表没有「已忽略」属性，跳过写回（正常降级）");
                return false;
            }

            var ok = await _client.UpdateGameMasterIgnoredAsync(game.NotionPageId, ignored);
            if (!ok)
            {
                AppLog.Warn($"[Notion] 写回「已忽略」失败：「{game.Name}」");
            }
            return ok;
        }
        catch (Exception ex)
        {
            // 调用方是 UI 的 fire-and-forget 路径：本地状态已经生效，这里的失败不改变它。
            AppLog.Warn($"[Notion] 写回「已忽略」异常（不影响本地状态）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把本地未绑定游戏自动关联到 Notion 总表条目。
    /// 只接受确定性信号（identifier_match / exact / normalized）；
    /// 模糊候选一律不自动绑定，留给用户在映射库/Pending 页人工确认。
    /// </summary>
    public async Task<int> AutoLinkGamesFromCatalogAsync()
    {
        var allGames = await _repo.GetAllGamesAsync();
        var unlinked = allGames
            .Where(g => string.IsNullOrEmpty(g.NotionPageId) && g.Status != "ignored"
                        && !GameTimeTracker.Core.DeviceSync.GameStatuses.IsArchived(g.Status))
            .ToList();
        if (unlinked.Count == 0) return 0;

        var catalog = await _repo.GetCatalogItemsAsync();
        if (catalog.Count == 0) return 0;

        // 只信任真正的 Notion page id，避免把历史遗留/测试写入的假 ID 当真。
        var usable = catalog.Where(c => IsNotionPageId(c.PageId)).ToList();
        if (usable.Count == 0) return 0;

        int linked = 0;

        foreach (var game in unlinked)
        {
            var steamAppId = string.Equals(game.Platform, "steam", StringComparison.OrdinalIgnoreCase)
                ? game.PlatformId
                : null;

            var candidates = _matcher.MatchGame(game.Name, usable, steamAppId);

            // 老数据里不少游戏名是**直接从 Notion 标题搬过来的**，带着用户手写的时长后缀
            // （「英灵神殿2.4h」「人鱼3h」「咖啡厅2h 白金」）—— 拿这种名字去匹配总表必然失败，
            // 于是这些行就一直挂着"未绑定 / manual"，哪怕总表里明明有这个游戏。
            // 没有确定性命中时，剥掉时长后缀再试一次（2026-09-19 QA 反馈）。
            if (!candidates.Any(c => c.MatchType is "identifier_match" or "exact" or "normalized")
                && !string.IsNullOrWhiteSpace(game.Name))
            {
                var stripped = DailyRecordTitle.StripSuffix(game.Name);
                if (!string.IsNullOrWhiteSpace(stripped)
                    && !string.Equals(stripped, game.Name, StringComparison.Ordinal))
                {
                    candidates = _matcher.MatchGame(stripped, usable, steamAppId);
                }
            }

            var deterministic = candidates
                .Where(c => c.MatchType is "identifier_match" or "exact" or "normalized")
                .ToList();

            // 若仍未匹配成功且属于 Steam 平台，尝试通过 SteamAPI 获取官方中文名自动二次匹配
            // （应对用户在总表中填写 Steam 中文游戏名，而本地进程识别为英文名的情况）
            if (deterministic.Count != 1 && !string.IsNullOrWhiteSpace(steamAppId))
            {
                try
                {
                    var cnTitle = await _matcher.ResolveSteamChineseTitleAsync(steamAppId);
                    if (!string.IsNullOrWhiteSpace(cnTitle))
                    {
                        var cnCandidates = _matcher.MatchGame(cnTitle, usable, steamAppId);
                        var cnDeterministic = cnCandidates
                            .Where(c => c.MatchType is "identifier_match" or "exact" or "normalized")
                            .ToList();
                        if (cnDeterministic.Count == 1)
                        {
                            deterministic = cnDeterministic;
                        }
                    }
                }
                catch
                {
                    // 网络异常或非官方应用，静默容错
                }
            }

            if (deterministic.Count != 1) continue;

            try
            {
                await _repo.UpdateGameNotionIdAsync(game.Id, deterministic[0].PageId);
                linked++;
            }
            catch
            {
                // 单条失败不影响其余游戏
            }
        }

        if (linked > 0)
        {
            SyncStatusChanged?.Invoke(this, $"已自动关联 {linked} 款游戏到 Notion 总表");
        }

        return linked;
    }

    /// <summary>
    /// Notion page id 形如 32 位十六进制（可带连字符）。
    /// </summary>
    private static bool IsNotionPageId(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return false;
        var compact = pageId.Replace("-", string.Empty);
        return compact.Length == 32 && compact.All(Uri.IsHexDigit);
    }

    public async Task LinkGameRelationAsync(int gameId, string notionPageId)
    {
        // 1. Update SQLite game's notion_page_id
        await _repo.UpdateGameNotionIdAsync(gameId, notionPageId);

        // 2. Fetch ALL daily records for this game and backfill to Notion
        var allSummaries = await _repo.GetDailySummariesByGameIdAsync(gameId);

        if (_config.IsNotionConfigured)
        {
            SyncStatusChanged?.Invoke(this, "正在关联并同步 Notion 每日游戏记录...");
            foreach (var item in allSummaries)
            {
                try
                {
                    // 刚绑定总表 → 这批记录改用总表名字；图标改为本地封面上传（与总表图标无关）。
                    var displayName = await ResolveDailyDisplayAsync(item.GameName, notionPageId);
                    var iconUploadId = await ResolveDailyIconUploadIdAsync(notionPageId, item.GameName);

                    if (string.IsNullOrEmpty(item.NotionPageId))
                    {
                        var existingRemote = FindMatchingNotionDailyRecord(_lastPulledDailyRecords, item.GameName, notionPageId, item.Date);
                        if (existingRemote != null)
                        {
                            item.NotionPageId = existingRemote.PageId;
                            await _repo.UpdateDailySyncStatusAsync(item.Id, "synced", existingRemote.PageId);
                        }
                    }

                    if (string.IsNullOrEmpty(item.NotionPageId))
                    {
                        var newPageId = await _client.CreateDailyRecordAsync(
                            _config.DailyDatabaseId,
                            item.Date,
                            displayName,
                            item.DurationMinutes,
                            notionPageId,
                            iconUrl: null,
                            iconFileUploadId: iconUploadId);

                        await _repo.UpdateDailySyncStatusAsync(item.Id, "synced", newPageId);
                        await RecordRemoteSnapshotAsync(newPageId, displayName, item.DurationMinutes, null);
                    }
                    else
                    {
                        // 🛡️ 2026-10-14 快照缺项保护：用户点击"关联"只表达意图，**不证明**远端页面存在、
                        //    也不证明它属于当前配置的每日库。页面不在本轮快照中 ⇒ 不创建替代页、不 PATCH。
                        if (_lastPulledDailyRecords?.Any(r => NormalizePageId(r.PageId) == NormalizePageId(item.NotionPageId)) != true)
                        {
                            AppLog.Warn($"[LinkGameRelationAsync] 跳过写入：已绑定页面 {item.NotionPageId} 不在本轮快照中"
                                        + $"（归属与状态均未经确认）「{item.GameName} {item.Date}」");
                            continue;
                        }

                        // 手动绑定总表后，历史条目**只补关联与绑定状态**：
                        // 标题与 Page Icon 仍不自动改写（唯一入口是设置页「开始格式规范化」）。
                        // 时长以 Notion 上的为权威，绝不用本地缓存覆盖。
                        await _client.UpdateDailyRecordAsync(
                            item.NotionPageId, item.DurationMinutes, notionPageId,
                            gameName: null, iconUrl: null, writeDuration: false,
                            titleOverride: null, durationSeconds: null, iconFileUploadId: null);
                        await _repo.UpdateDailySyncStatusAsync(item.Id, "synced");
                    }
                }
                catch (Exception ex)
                {
                    await _repo.UpdateDailySyncStatusAsync(item.Id, "error", errorMessage: ex.Message);
                }
            }
        }

        // 3. Backfill any remaining unmapped records
        await BackfillRelationsAsync();
        await SyncPendingDailyRecordsAsync();
    }

    public async Task<string> CreateGameMasterAndLinkAsync(int gameId, string gameName)
    {
        if (!_config.IsNotionConfigured)
        {
            throw new InvalidOperationException("Notion 未配置，请先在设置中填写 Notion Token 及游戏总表 ID");
        }

        var game = await _repo.GetGameByIdAsync(gameId);
        string? iconUrl = null;
        string? coverUrl = null;

        if (game != null && string.Equals(game.Platform, "steam", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(game.PlatformId) && game.PlatformId.All(char.IsDigit))
        {
            iconUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{game.PlatformId}/header.jpg";
            coverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{game.PlatformId}/library_hero.jpg";
        }

        // 1. Create page in Notion Game Master Database with title, icon, and cover
        var newPageId = await _client.CreateGameMasterPageAsync(
            _config.GameDatabaseId, gameName, iconUrl, coverUrl);
        if (string.IsNullOrEmpty(newPageId))
        {
            throw new Exception("Notion 未返回新建页面的 ID");
        }

        AppLog.Info($"[总表新增] 已在 Notion 游戏总表成功新建条目：「{gameName}」（页面 ID: {newPageId}）");

        // 2. Cache new game in local catalog
        await _repo.UpsertCatalogItemsAsync(new[]
        {
            new NotionGameCatalogItem
            {
                PageId = newPageId,
                Name = gameName,
                IconUrl = iconUrl,
                CoverUrl = coverUrl,
                LastSyncedAt = DateTime.UtcNow
            }
        });

        // 3. Link and backfill all daily records for this game
        await LinkGameRelationAsync(gameId, newPageId);
        SyncStatusChanged?.Invoke(this, $"已在游戏总表新建「{gameName}」");
        return newPageId;
    }

    // ==================================================================
    //  双向删除
    //  · 程序里删 → 本地删 + Notion 侧归档（进回收站，30 天内可恢复）
    //  · Notion 里删 → 对账后本地跟着物理删除
    //  物理删除前一律先写 deleted_archive 留底，误判时能人工找回。
    // ==================================================================

    /// <summary>Notion 的 page id 带连字符，本地存的写法不统一；比较前统一去连字符并转小写。</summary>
    private static string NormalizePageId(string? pageId)
        => string.IsNullOrWhiteSpace(pageId)
            ? string.Empty
            : pageId.Replace("-", string.Empty).Trim().ToLowerInvariant();

    /// <summary>删除一个游戏：Notion 侧归档其每日记录（可选：总表条目），本地连会话与汇总一起删。</summary>
    public async Task<NotionDeleteResult> DeleteGameEverywhereAsync(int gameId, bool deleteMasterEntry)
    {
        var result = new NotionDeleteResult();

        var game = await _repo.GetGameByIdAsync(gameId);
        if (game == null) return result;

        var dailyRecords = (await _repo.GetDailySummariesByGameIdAsync(gameId)).ToList();
        var linkedRecords = dailyRecords.Where(d => !string.IsNullOrWhiteSpace(d.NotionPageId)).ToList();

        // ---- 1. Notion 侧归档 ----
        if (!_config.IsNotionConfigured)
        {
            result.NotionSkipped = true;
        }
        else
        {
            foreach (var rec in linkedRecords)
            {
                try
                {
                    if (await _client.ArchivePageAsync(rec.NotionPageId!))
                    {
                        result.NotionDailyRecordsArchived++;
                    }
                    else
                    {
                        result.NotionDailyRecordsFailed++;
                    }
                }
                catch (Exception ex)
                {
                    result.NotionDailyRecordsFailed++;
                    result.Errors.Add($"{rec.Date} {rec.NotionPageId}: {ex.Message}");
                }
            }

            if (deleteMasterEntry && !string.IsNullOrWhiteSpace(game.NotionPageId))
            {
                try
                {
                    result.MasterEntryArchived = await _client.ArchivePageAsync(game.NotionPageId!);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"总表条目 {game.NotionPageId}: {ex.Message}");
                }
            }
        }

        // ---- 2. 本地留底（物理删除前） ----
        await _repo.ArchiveDeletedAsync("game", "app", game.Name, BuildGameSnapshotJson(game, dailyRecords));

        // ---- 3. 本地删除（daily_summary / sessions 走 FK CASCADE） ----
        result.LocalDailyRecordsDeleted = dailyRecords.Count;
        await _repo.DeleteGameAsync(gameId);

        SyncStatusChanged?.Invoke(this, BuildGameDeleteSummary(game.Name, result));
        return result;
    }

    /// <summary>删除某天的每日记录：Notion 上对应页面归档，本地该行物理删除。</summary>
    public async Task<NotionDeleteResult> DeleteDailyRecordEverywhereAsync(int dailySummaryId)
    {
        var result = new NotionDeleteResult();

        var record = await _repo.GetDailySummaryByIdAsync(dailySummaryId);
        if (record == null) return result;

        if (string.IsNullOrWhiteSpace(record.NotionPageId))
        {
            result.NotionSkipped = true;
        }
        else if (!_config.IsNotionConfigured)
        {
            result.NotionSkipped = true;
        }
        else
        {
            try
            {
                if (await _client.ArchivePageAsync(record.NotionPageId))
                {
                    result.NotionDailyRecordsArchived = 1;
                }
                else
                {
                    result.NotionDailyRecordsFailed = 1;
                }
            }
            catch (Exception ex)
            {
                result.NotionDailyRecordsFailed = 1;
                result.Errors.Add($"{record.NotionPageId}: {ex.Message}");
            }
        }

        await _repo.ArchiveDeletedAsync("daily", "app",
            $"{record.Date} {record.GameName}",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                record.Id,
                record.Date,
                record.GameId,
                record.GameName,
                record.DurationMinutes,
                record.NotionPageId
            }));

        // 🛡️ 2026-10-11（记录删除粒度重写）：用户显式删除一条历史记录时，只删 `daily_summary`
        //    派生行是不够的（会话事实仍在 ⇒ 任何重算都会把这条记录原样派生回来，且删除无法跨设备传播）。
        //    现在走 <see cref="IDatabaseRepository.DeletePlayRecordAsync"/> 的**单事务**链路：
        //    按 (游戏, 会计日, 会话) 粒度作废时长分片 + 撤回该日声明 + 写墓碑/入队 + 重算所有受影响日期。
        //    ⚠️ Notion 网络操作**不在**该事务里（归档在上面已经做过）；本地失败一律整体回滚。
        var deletion = await _repo.DeletePlayRecordAsync(dailySummaryId);
        result.LocalDailyRecordsDeleted = deletion.Found ? 1 : 0;
        result.LocalPlayRecordFullyDeleted = deletion.FullyDeleted;
        result.LocalRemainingSeconds = deletion.RemainingSeconds;
        result.LocalActiveSessionsSkipped = deletion.ActiveSessionsSkipped;
        result.LocalDeletionNote = deletion.Note;

        SyncStatusChanged?.Invoke(this,
            result.NotionDailyRecordsArchived > 0
                ? $"已删除 {record.Date} 的记录，并在 Notion 归档对应页面"
                : $"已删除 {record.Date} 的记录");
        return result;
    }

    /// <summary>
    /// 对账 Notion 侧的删除，本地跟着删。
    /// 必须在 <see cref="RefreshGameCatalogCacheAsync"/> 之后调用 —— 它依赖 game_catalog 作为总表的忠实快照。
    /// 任何一个远端集合拿不到或为空时都整段跳过：宁可漏删，也不能把本地数据误清空。
    /// </summary>
    public async Task<NotionReconcileResult> ReconcileNotionDeletionsAsync()
    {
        var result = new NotionReconcileResult();
        if (!_config.IsNotionConfigured)
        {
            result.Skipped = true;
            return result;
        }

        try
        {
            // ---------- A. 每日表：远端没有的 page_id ----------
            var remoteDaily = await _client.QueryDailyRecordsAsync(_config.DailyDatabaseId);
            if (remoteDaily == null || remoteDaily.Count == 0)
            {
                result.Skipped = true;
            }
            else
            {
                var remoteIds = remoteDaily
                    .Select(r => NormalizePageId(r.PageId))
                    .Where(id => id.Length > 0)
                    .ToHashSet();

                var localSynced = await _repo.GetSyncedDailySummariesAsync();

                // ⚠️ 保护：远端记录数骤减一半以上 → 判定为"这轮拉取不完整"，整段跳过。
                //
                // 下面只凭"本地这条的 page_id 不在远端集合里"就归档 + 删除本地记录，
                // 前提是远端集合**完整**。而 QueryDailyRecordsAsync 会跳过解析不出来的行
                // （日期属性为空、或标题与关联游戏都为空）—— 那些行的 page_id 自然不在
                // remoteIds 里，本地对应记录就会被当成"Notion 已删除"而误删，
                // 连带把它记录的时长一起抹掉。
                // 宁可漏删（下一轮远端恢复正常后照样会删），也不能误删。
                // 样本少于 20 条时不启用，避免小数据量下的正常删除被拦住。
                if (localSynced.Count >= 20 && remoteDaily.Count * 2 < localSynced.Count)
                {
                    AppLog.Warn($"删除对账：远端只拉到 {remoteDaily.Count} 条，本地已同步 {localSynced.Count} 条，" +
                                "疑似拉取不完整，本轮跳过（宁可漏删，不误删）");
                    result.Skipped = true;
                    return result;
                }

                // 🛡️ 2026-10-14（删除对账误删收口 · 环境可信判断）：
                //    下面这段会**物理删除**本地每日记录并**撤回外部声明**（撤销会随设备同步传播到对端），
                //    所以在动手之前必须先确认：当前 Notion 环境仍然是这些记录当初同步到的那个库。
                //
                //    最危险的一类误删：库被重建 / 换 workspace / 换 Token / 配置被指向另一个同结构库之后，
                //    QueryDailyRecordsAsync 依然会成功（新库是合法且已共享的），但本地旧 page id 的
                //    GET /pages/{id} 全部返回 404 ⇒ 旧逻辑认定"用户删了这些页面"⇒ 删本地行 + 撤回声明 +
                //    跨设备传播。配置错误绝不能演变成数据删除。
                //
                //    判据 = ① 把最近一次"确认可信"的 Daily 库 id 记进 settings，与本轮配置比对（库身份）；
                //           ② 用本地绑定记录与本轮远端列表的**连续性证据**衡量环境可信度（不止"看到一条就算"）。
                //      · 目标库一致，或首次但连续性不足 20 条样本（正常的小库用法）⇒ 记下当前库，照常对账
                //        （必须保住"用户真的删了 Notion 页面 ⇒ 本地跟随删除"这条既有行为）；
                //      · 目标库**不一致** + 连续性证据不足 ⇒ 只 Warn + 整轮跳过（不删、不撤回、不重新基线）；
                //      · 目标库不一致 + 连续性证据充分（≥ 半数本地页面仍可见）⇒ Warn + 重新基线 + 照常对账（自愈）；
                //      · **首次建立基线** + 样本 ≥20 + 连续性证据不足 ⇒ 只 Warn + 整轮跳过，且**不写基线**
                //        （首次建立基线不得等同于首次获得删除授权）。
                var currentTargetDbKey = NormalizePageId(_config.DailyDatabaseId);
                var localBoundPageIds = localSynced
                    .Where(d => !string.IsNullOrWhiteSpace(d.NotionPageId))
                    .Select(d => NormalizePageId(d.NotionPageId))
                    .Where(id => id.Length > 0)
                    .ToHashSet();

                // 连续性证据（统计性）：本地已绑定页面里有多少条**真的出现在本轮远端列表**里。
                //   · 0 条 ⇒ 当前环境里看不到任何一条我们认识的页面；
                //   · 零星几条 ⇒ 也不足以证明"其余记录都该删"（旧库 100 条、新库里只有 1 条匹配，
                //     绝不能据此把其余 99 条当成"用户已删除"）。
                var visibleLocalPageCount = localBoundPageIds.Count(id => remoteIds.Contains(id));
                var seenKnownPageRemotely = visibleLocalPageCount > 0;
                var hasContinuityEvidence = visibleLocalPageCount * 2 >= localBoundPageIds.Count;
                // 样本门槛与既有"半数骤减保护"保持一致：绑定记录少于 20 条时不做统计性判断 ——
                // 否则"用户就一两条记录、他把其中一条删了"这种**正常用法**会被永久拦住（既有测试锁定了它）。
                const int continuitySampleThreshold = 20;
                var continuitySampleTooSmall = localBoundPageIds.Count < continuitySampleThreshold;

                var recordedTargetDbKey = NormalizePageId(await _repo.GetSettingAsync(SettingKeyDailyReconcileTargetDb));
                var targetDbChanged = recordedTargetDbKey.Length > 0
                    && !string.Equals(recordedTargetDbKey, currentTargetDbKey, StringComparison.Ordinal);

                // 日志用：让"仍待确认"的记录可见（不是只说一句"跳过"）。
                var unconfirmedPageSample = string.Join(", ",
                    localBoundPageIds.Where(id => !remoteIds.Contains(id)).Take(5));
                var unconfirmedCount = localBoundPageIds.Count - visibleLocalPageCount;

                // 闸门 A：「已记录的目标库」与本机配置不一致 = 换库 / 重建 / 换 workspace / 换 Token 的迹象。
                //   这种情况必须拿到**足够的**连续性证据才允许继续删除；只看到一两条本地页面不算证据。
                if (targetDbChanged && !hasContinuityEvidence)
                {
                    AppLog.Warn($"[删除对账] 同步目标库已由 {Fingerprint(recordedTargetDbKey)} 变为 "
                                + $"{Fingerprint(currentTargetDbKey)}，而本地 {localBoundPageIds.Count} 条绑定记录里"
                                + $"本轮远端只看到 {visibleLocalPageCount} 条（仍待确认 {unconfirmedCount} 条，例如 {unconfirmedPageSample}）——"
                                + "证据不足，本轮**跳过删除对账**：不删本地记录、不撤回声明、不重新基线"
                                + "（宁可漏删，不误删；等这些记录重新绑定到新库页面、或人工确认后再恢复自动对账）");
                    result.Skipped = true;
                    return result;
                }

                // 闸门 B：**首次建立基线 ≠ 首次获得删除授权**。
                //   升级 / 换机后第一次跑：settings 里还没有目标库记录，而本地可能已积累大量绑定记录，
                //   若当前配置恰好指向另一个库（新库可查但里面没有我们的页面），
                //   "没有基线可比对"绝不能被当成"环境可信"，否则一批本地记录会被一次性删光。
                //   ⚠️ 只对有统计意义的样本（≥20 条）生效：少量记录是"用户就删了那一条"的正常用法，保持既有行为。
                if (recordedTargetDbKey.Length == 0 && !continuitySampleTooSmall && !hasContinuityEvidence)
                {
                    AppLog.Warn($"[删除对账] 首次建立同步目标基线：本地已有 {localBoundPageIds.Count} 条绑定记录，"
                                + $"但本轮远端只看到 {visibleLocalPageCount} 条（仍待确认 {unconfirmedCount} 条，例如 {unconfirmedPageSample}）——"
                                + "证据不足**不等同于**用户删除了这些页面，本轮跳过删除对账且**不写入基线**"
                                + "（既不删本地数据，也不把不可信环境固化成「已确认同步目标」）");
                    result.Skipped = true;
                    return result;
                }

                if (targetDbChanged)
                {
                    AppLog.Warn($"[删除对账] 同步目标库已由 {Fingerprint(recordedTargetDbKey)} 变为 "
                                + $"{Fingerprint(currentTargetDbKey)}，本地 {localBoundPageIds.Count} 条绑定记录里"
                                + $"本轮远端能看到 {visibleLocalPageCount} 条（仍待确认 {unconfirmedCount} 条）"
                                + " ⇒ 视为已完成迁移，重新基线后继续对账");
                }

                if (!string.Equals(recordedTargetDbKey, currentTargetDbKey, StringComparison.Ordinal))
                {
                    try
                    {
                        await _repo.SetSettingAsync(SettingKeyDailyReconcileTargetDb, currentTargetDbKey);
                    }
                    catch (Exception ex)
                    {
                        // 记录基线失败不该阻断对账本身（下一轮再记）
                        AppLog.Warn($"[删除对账] 记录同步目标库失败（不影响本轮对账）: {ex.Message}");
                    }
                }
                else if (localBoundPageIds.Count > 0 && !seenKnownPageRemotely)
                {
                    // 诊断用（不改变行为）：库身份没变、本轮却看不到任何本地已绑定页面 ——
                    // 可能是索引延迟、权限变化，也可能用户确实把这些页面都删了。
                    // 是否删除仍以逐页二次确权（IsPageDeletedAsync）为准。
                    AppLog.Warn($"[删除对账] 本轮远端 {remoteIds.Count} 条页面里看不到本地已绑定的 {localBoundPageIds.Count} 条页面，"
                                + "将逐页二次确权后再决定是否删除（索引延迟 / 权限变化 / 批量删除均可能是原因）");
                }

                // 只处理「本地认为已同步」且有 NotionPageId 的行：pending / error 的行本来就可能还没进 Notion，
                // 不在远端集合里属于正常，不能删。
                var candidateOrphanRows = localSynced
                    .Where(d => d.SyncStatus == "synced" && !string.IsNullOrWhiteSpace(d.NotionPageId))
                    .Where(d => !remoteIds.Contains(NormalizePageId(d.NotionPageId)))
                    .ToList();

                var orphanRows = new List<DailySummary>();
                foreach (var row in candidateOrphanRows)
                {
                    // 🛡️ 关键保护（防 2026-09-19 误删事件重演）：
                    // Notion 刚创建/修改的页面，其检索索引与只读从库存在数秒至数十秒的延迟（eventual consistency）。
                    // 全量 Query 没查到的 page_id，必须向 Notion 主库 GET /pages/{id} 二次确权！
                    // 只有 Notion 明确返回 404 或 archived=true / in_trash=true 时，才确认远端已删除。
                    bool isDeleted = await _client.IsPageDeletedAsync(row.NotionPageId!);
                    if (isDeleted)
                    {
                        orphanRows.Add(row);
                    }
                    else
                    {
                        AppLog.Warn($"[删除对账] 本地记录「{row.Date} {row.GameName}」在每日表列表查询中未出现，但二次确认 Notion 页面 ({row.NotionPageId}) 依然存在且未归档，保留本地记录，阻止误删！");
                    }
                }

                // 🔒 2026-10-11（实机：A 删了 Notion 记录，B 却永远多算）：
                //    只删本地派生行是不够的 —— 必须**撤回对应的外部声明**，否则
                //    ① 本机按事实重算时会把这段时长再加回来；
                //    ② 对端（Device Sync）永远不知道它被删了 ⇒ 双机统计永久不一致。
                //    ⚠️ 顺序：**先删派生行、再撤回声明**。撤回内部会按事实重算该行，
                //    于是最终结果 = Σ 会话派生 + Σ 剩余声明 —— 外部声明那一段消失，
                //    会话派生的时长**原样留住**（页面被删 ≠ 没玩过）。
                //    撤销沿用同一 identity（设备+游戏+日期+来源），并按既有同步协议入队传播；
                //    只针对 Notion 来源，绝不误伤同一天同游戏的手工声明。
                if (orphanRows.Count > 0)
                {
                    result.DeletedDailyRecords =
                        await _repo.DeleteDailySummariesAsync(orphanRows.Select(r => r.Id));
                }

                foreach (var row in orphanRows)
                {
                    foreach (var source in new[]

                             {
                                 GameTimeTracker.Core.DeviceSync.DailyAdjustmentSources.Notion,
                                 GameTimeTracker.Core.DeviceSync.DailyAdjustmentSources.NotionExternal
                             })
                    {
                        try
                        {
                            await _repo.RevokeLocalDailyAdjustmentAsync(row.GameId, row.Date, source);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"[删除对账] 撤回声明失败（{row.Date} {row.GameName} / {source}）：{ex.Message}");
                        }
                    }

                    await _repo.ArchiveDeletedAsync("daily", "notion",
                        $"{row.Date} {row.GameName}",
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            row.Id,
                            row.Date,
                            row.GameId,
                            row.GameName,
                            row.DurationMinutes,
                            row.NotionPageId,
                            row.SyncStatus
                        }));
                }
            }

            // ---------- B. 总表：catalog 里已经没有的条目 ----------
            var catalogIds = (await _repo.GetCatalogItemsAsync())
                .Select(c => NormalizePageId(c.PageId))
                .Where(id => id.Length > 0)
                .ToHashSet();

            if (catalogIds.Count > 0)
            {
                var games = await _repo.GetAllGamesAsync();
                var candidateOrphanGames = games
                    .Where(g => !string.IsNullOrWhiteSpace(g.NotionPageId))
                    .Where(g => !catalogIds.Contains(NormalizePageId(g.NotionPageId)))
                    .ToList();

                var orphanGames = new List<GameRecord>();
                foreach (var game in candidateOrphanGames)
                {
                    bool isDeleted = await _client.IsPageDeletedAsync(game.NotionPageId!);
                    if (isDeleted)
                    {
                        orphanGames.Add(game);
                    }
                    else
                    {
                        AppLog.Warn($"[删除对账] 游戏「{game.Name}」在总表缓存中未出现，但二次确认 Notion 页面 ({game.NotionPageId}) 依然存在且未归档，保留本地游戏，阻止误删！");
                    }
                }

                foreach (var game in orphanGames)
                {
                    var dailies = (await _repo.GetDailySummariesByGameIdAsync(game.Id)).ToList();
                    await _repo.ArchiveDeletedAsync("game", "notion",
                        game.Name, BuildGameSnapshotJson(game, dailies));
                    await _repo.DeleteGameAsync(game.Id);
                    result.DeletedGames++;
                }
            }

            if (result.HasChanges)
            {
                SyncStatusChanged?.Invoke(this,
                    $"删除对账：本地清理了 {result.DeletedGames} 款游戏、{result.DeletedDailyRecords} 条每日记录");
            }

            return result;
        }
        catch (Exception ex)
        {
            SyncStatusChanged?.Invoke(this, $"删除对账失败: {FriendlyNotionError(ex)}");
            result.Skipped = true;
            return result;
        }
    }

    private static string BuildGameSnapshotJson(GameRecord game, IReadOnlyList<DailySummary> dailies)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            game.Id,
            game.Name,
            game.Platform,
            game.PlatformId,
            game.Executable,
            game.ExecutablePath,
            game.NotionPageId,
            game.Status,
            DailyRecords = dailies.Select(d => new
            {
                d.Id,
                d.Date,
                d.DurationMinutes,
                d.SyncStatus,
                d.NotionPageId
            }).ToList()
        });

    private static string BuildGameDeleteSummary(string gameName, NotionDeleteResult r)
    {
        var parts = new List<string> { $"已删除「{gameName}」" };
        if (r.NotionDailyRecordsArchived > 0) parts.Add($"Notion 归档 {r.NotionDailyRecordsArchived} 条每日记录");
        if (r.NotionDailyRecordsFailed > 0) parts.Add($"{r.NotionDailyRecordsFailed} 条归档失败");
        if (r.MasterEntryArchived) parts.Add("总表条目已归档");
        if (r.NotionSkipped) parts.Add("Notion 未连接，仅删本地");
        return string.Join("，", parts);
    }
}
