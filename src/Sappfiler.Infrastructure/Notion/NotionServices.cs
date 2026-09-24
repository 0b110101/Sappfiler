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
/// **写**标题的 <see cref="NotionClient"/> 和**比对**标题的
/// <c>NotionSyncService.RefreshDailyTitlesFromMasterAsync</c> 必须共用同一套规则。
/// 两边各留一份实现的话，只要格式有一点漂移（比如改小数位数、换分隔符），
/// 回刷就会永远判定"标题不一致"，每轮同步都白打一次 Notion。
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
        new(@"\s*(?:(?:[·|]\s*)?\d+(?:\.\d+)?\s*(?:min|h)|\(\s*\d+\s*分\s*\))\s*$",
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
    /// 拼装每日记录标题：时长以小时显示（2 位小数），如「Master Key · 0.7 h」。
    /// 参数是**游戏名**，不是拼好的标题 —— 传完整标题进来会拼出「X · 0.7 h · 0.7 h」。
    /// </summary>
    internal static string Build(string gameName, int durationMinutes)
        => $"{gameName} · {MinutesToHours(durationMinutes)} h";

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
}

public class NotionClient : INotionClient
{
    private const string BaseUrl = "https://api.notion.com/v1";
    private const string NotionVersion = "2022-06-28";

    private readonly HttpClient _httpClient;
    private string _token;
    private readonly int _maxRetries;

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

                    throw new HttpRequestException(
                        $"Notion API {(int)response.StatusCode}: {(string.IsNullOrEmpty(detail) ? response.ReasonPhrase : detail)}");
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

                    var name = ExtractTitle(props);
                    var aliases = ExtractMultiSelectOrText(props, "别名", "Aliases");
                    var idents = ExtractMultiSelectOrText(props, "游戏标识", "Identifiers", "Steam ID");
                    var genres = ExtractMultiSelectOrText(props, "类型", "游戏类型", "Genre", "Genres", "分类");
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

                    var rawTitle = ExtractTitle(props);
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
                    var gameMasterPageId = ExtractRelationId(props, "关联游戏", "游戏", "游戏总表", "Game");
                    var status = ExtractSelect(props, "绑定状态", "Status");

                    if (!string.IsNullOrWhiteSpace(date) && (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(gameMasterPageId)))
                    {
                        results.Add(new NotionDailyRecordItem
                        {
                            PageId = pageId,
                            GameTitle = title,
                            RawTitle = rawTitle.Trim(),
                            Date = date,
                            DurationMinutes = duration,
                            DurationUnitIsMinutes = durationUnit == DurationUnit.Minutes,
                            GameMasterPageId = gameMasterPageId,
                            Status = status
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
        string? iconUrl = null)
    {
        var cleanDbId = dailyDbId.Replace("-", "");
        var titleText = DailyRecordTitle.Build(gameName, durationMinutes);
        // ⚠️ 每日时长表的属性名：游戏动态 ／ 时长 ／ 关联游戏 ／ 绑定状态
        //    写错名字 Notion 会返回 400（property does not exist），同步直接失败。
        //    注意**总表**的 title 属性仍叫「游戏名称」，别一起改了。
        var properties = new Dictionary<string, object>
        {
            ["游戏动态"] = new { title = new[] { new { text = new { content = titleText } } } },
            ["日期"] = new { date = new { start = date } },
            // 值是**小时**（2 位小数），不是分钟 —— 与标题后缀保持一致。
            ["时长"] = new { number = DailyRecordTitle.MinutesToHours(durationMinutes) },
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
            // 顺带把总表的 page icon 用到每日记录页面上（外部 URL，无需上传）。
            icon = string.IsNullOrWhiteSpace(iconUrl)
                ? null
                : new { type = "external", external = new { url = iconUrl } }
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
        string? titleOverride = null)
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
            // 值是**小时**（2 位小数），与创建时以及标题后缀保持一致
            properties["时长"] = new { number = DailyRecordTitle.MinutesToHours(durationMinutes) };
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

        var payload = string.IsNullOrWhiteSpace(iconUrl)
            ? (object)new { properties }
            : new { properties, icon = new { type = "external", external = new { url = iconUrl } } };

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
    public async Task<bool> UpdateDailyRecordTitleAsync(string pageId, string title, string? iconUrl = null)
    {
        var cleanPageId = pageId.Replace("-", "");

        // 只带「游戏动态」一个属性 —— PATCH 只改传进去的属性，不传的保持原样。
        var properties = new Dictionary<string, object>
        {
            ["游戏动态"] = new { title = new[] { new { text = new { content = title } } } }
        };

        var payload = string.IsNullOrWhiteSpace(iconUrl)
            ? (object)new { properties }
            : new { properties, icon = new { type = "external", external = new { url = iconUrl } } };

        using var req = CreateRequest(HttpMethod.Patch, $"/pages/{cleanPageId}", payload);
        var res = await SendWithRetryAsync(req);
        return res.TryGetProperty("id", out _);
    }

    public async Task<string> CreateGameMasterPageAsync(string gameDbId, string gameTitle)
    {
        var cleanDbId = gameDbId.Replace("-", "");
        var payload = new
        {
            parent = new { database_id = cleanDbId },
            properties = new Dictionary<string, object>
            {
                ["游戏名称"] = new { title = new[] { new { text = new { content = gameTitle } } } }
            }
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

            if (res.TryGetProperty("archived", out var archived) && archived.GetBoolean())
            {
                return true;
            }
            if (res.TryGetProperty("in_trash", out var inTrash) && inTrash.GetBoolean())
            {
                return true;
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

    private static string ExtractTitle(JsonElement props)
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
                    return sb.ToString().Trim();
                }
            }
        }
        return string.Empty;
    }

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
    private static int RawDurationToMinutes(double? raw, string? title)
    {
        if (raw is not { } value || value <= 0) return 0;

        switch (DetectDurationUnit(title))
        {
            case DurationUnit.Minutes: return (int)Math.Round(value);
            case DurationUnit.Hours: return (int)Math.Round(value * 60.0);
        }

        // 标题里没有可识别的单位：只能按数量级猜
        const double MaxPlausibleHours = 24.0;
        return value > MaxPlausibleHours
            ? (int)Math.Round(value)            // 像是旧格式的分钟
            : (int)Math.Round(value * 60.0);    // 像是小时
    }

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
    private List<NotionDailyRecordItem> _lastPulledDailyRecords = new();

    /// <summary>
    /// 把原始异常翻译成用户能行动的文案：401 是 Token 无效（复制不全/过期），
    /// 403 才是数据库没 Connect 给 Integration；SSL/DNS 类是网络或代理问题。
    /// </summary>
    internal static string FriendlyNotionError(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("401")) return "Notion Token 无效或已过期——请到设置页重新复制 Internal Access Secret";
        if (msg.Contains("403")) return "Notion 拒绝访问——请确认两个数据库都已在 ··· → Connections 里连接该 Integration";
        if (msg.Contains("archived")) return "该记录在 Notion 已被删除，本地将跟随清理";
        if (msg.Contains("SSL connection") || msg.Contains("不知道这样的主机") ||
            msg.Contains("No such host") || msg.Contains("timed out") || msg.Contains("name resolver"))
            return "无法连接 Notion 服务器——请检查网络或代理（api.notion.com 在部分地区需要代理访问）";
        return msg;
    }

    /// <summary>
    /// 上一次拉取是否成功。默认视为成功，避免在"还没拉过"时误挡上传。
    /// 用途：上传前必须先拿到 Notion 现有记录 —— 用户可能在使用本程序之前
    /// 就已经手工记录过时长，那些记录必须先把 notion_page_id 认回来，
    /// 否则本地会为同一个 (日期, 游戏) 再建一条，Notion 里就出现重复行。
    /// 拉取失败时宁可这轮不上传（下一轮网络恢复后会补），也不要冒重号的风险。
    /// </summary>
    private bool _lastPullSucceeded = true;

    public event EventHandler<string>? SyncStatusChanged;

    public bool IsNotionConfigured => _config.IsNotionConfigured;

    public NotionSyncService(IDatabaseRepository repo, INotionClient client, TrackerConfig config, IGameMatcher? matcher = null)
    {
        _repo = repo;
        _client = client;
        _config = config;
        _matcher = matcher ?? new GameMatcher();
    }

    /// <summary>
    /// 推送每日记录时，标题该用哪个名字、page icon 该用哪张图。
    /// </summary>
    /// <remarks>
    /// 设计意图（用户要求）：
    /// 每日记录的「游戏名称」以前一直用本地进程名（`games.name`）。但游戏一旦绑定到总表，
    /// 用户在总表里改过的名字（可能已本地化成中文）才是他真正想看到的，
    /// 而 relation 指向的就是那个条目 —— 所以已绑定时以总表名称为准。
    ///
    /// page icon 同理：优先用总表条目的方形小图（`IconUrl`），而不是横幅 `CoverUrl`
    /// （横幅是给 Hero 背景用的，塞进列表图标会糊）。总表条目没设图标就什么都不写，
    /// 不去猜一个 Steam 图标 —— 免得覆盖用户自己的选择。
    ///
    /// 未绑定时保持原样（用进程名、不写 icon），避免"没绑定就先改了名字"的意外。
    /// </remarks>
    private async Task<(string DisplayName, string? IconUrl)> ResolveDailyDisplayAsync(
        string localName, string? gameNotionPageId)
    {
        if (string.IsNullOrWhiteSpace(gameNotionPageId)) return (localName, null);

        try
        {
            var cat = await _repo.GetCatalogItemByPageIdAsync(gameNotionPageId);
            if (cat == null) return (localName, null);

            var displayName = string.IsNullOrWhiteSpace(cat.Name) ? localName : cat.Name;
            var iconUrl = string.IsNullOrWhiteSpace(cat.IconUrl) ? null : cat.IconUrl;
            return (displayName, iconUrl);
        }
        catch (Exception ex)
        {
            // 取目录失败不该阻断同步本身；记录警告并退回进程名即可。
            AppLog.Warn($"[ResolveDailyDisplayAsync] 读取总表条目异常 ({gameNotionPageId}): {ex.Message}");
            return (localName, null);
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
        string pageId, string displayName, int durationMinutes, string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return;

        try
        {
            await _repo.UpdateDailyRecordFromNotionAsync(
                pageId, DailyRecordTitle.Build(displayName, durationMinutes), iconUrl);
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

                // 之前这里只增不删，导致总表删掉的条目、以及早期测试留下的假 page_id 一直沉淀在本地，
                // 还会出现在手动绑定的候选列表里（选中就 404）。
                // 现在让 game_catalog 成为总表的忠实快照，删除对账也依赖这份快照。
                var removed = await _repo.DeleteCatalogItemsNotInAsync(items.Select(i => i.PageId));
                SyncStatusChanged?.Invoke(this,
                    removed > 0 ? $"游戏总表已更新（清理 {removed} 条已失效条目）" : "游戏总表已更新");
                return;
            }

            SyncStatusChanged?.Invoke(this, "游戏总表已更新");
        }
        catch (Exception ex)
        {
            SyncStatusChanged?.Invoke(this, $"更新失败: {FriendlyNotionError(ex)}");
        }
    }

    public async Task<int> PullDailyRecordsFromNotionAsync()
    {
        if (!_config.IsNotionConfigured) return 0;

        try
        {
            SyncStatusChanged?.Invoke(this, "正在从每日时长表拉取记录...");
            var rawRecords = await _client.QueryDailyRecordsAsync(_config.DailyDatabaseId);
            _lastPullSucceeded = true;
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

            var catalogNames = catalogItems
                .Where(c => !string.IsNullOrWhiteSpace(c.PageId) && !string.IsNullOrWhiteSpace(c.Name))
                .ToDictionary(c => NormalizePageId(c.PageId), c => c.Name.Trim().ToLowerInvariant());

            var validRecords = new List<NotionDailyRecordItem>();
            foreach (var r in rawRecords)
            {
                if (catalogIds.Count > 0 &&
                    !string.IsNullOrWhiteSpace(r.GameMasterPageId) &&
                    !catalogIds.Contains(NormalizePageId(r.GameMasterPageId)))
                {
                    skippedOrphan++;
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
                var normMasterId = NormalizePageId(r.GameMasterPageId);
                if (!string.IsNullOrEmpty(normMasterId) && catalogNames.TryGetValue(normMasterId, out var catName))
                {
                    gameKey = catName;
                }
                else if (!string.IsNullOrEmpty(normMasterId))
                {
                    gameKey = $"m:{normMasterId}";
                }
                else
                {
                    gameKey = DailyRecordTitle.ExtractBaseName(r.GameTitle ?? "").Trim().ToLowerInvariant();
                }

                var key = $"{date}|{gameKey}";
                if (!groupedRecords.TryGetValue(key, out var list))
                {
                    list = new List<NotionDailyRecordItem>();
                    groupedRecords[key] = list;
                }
                list.Add(r);
            }

            var deduplicatedRecords = new List<NotionDailyRecordItem>();
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

                var canonical = matchedLocal != null
                    ? group.First(g => NormalizePageId(g.PageId) == NormalizePageId(matchedLocal.NotionPageId))
                    : group.OrderByDescending(g => g.DurationMinutes).First();

                var duplicates = group.Where(g => g.PageId != canonical.PageId).ToList();

                // 2. 累计合并总时长
                int sumMinutes = group.Sum(g => g.DurationMinutes);
                int localMinutes = matchedLocal?.DurationMinutes ?? 0;
                int totalMinutes = Math.Max(sumMinutes, localMinutes);

                // 3. 将多余的重复页面归档移入 Notion 回收站
                foreach (var dup in duplicates)
                {
                    try
                    {
                        await _client.ArchivePageAsync(dup.PageId);
                        AppLog.Info($"[同步] 自动归档 Notion 冗余每日记录页面: {dup.PageId} (「{dup.GameTitle}」{dup.Date} {dup.DurationMinutes}m)");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"[同步] 归档 Notion 重复页面失败 ({dup.PageId}): {ex.Message}");
                    }
                }

                // 4. 更新权威页面为合并后的总时长
                var masterPageId = !string.IsNullOrEmpty(canonical.GameMasterPageId)
                    ? canonical.GameMasterPageId
                    : group.FirstOrDefault(g => !string.IsNullOrEmpty(g.GameMasterPageId))?.GameMasterPageId;

                var (displayName, iconUrl) = await ResolveDailyDisplayAsync(canonical.GameTitle, masterPageId);
                try
                {
                    await _client.UpdateDailyRecordAsync(
                        canonical.PageId,
                        totalMinutes,
                        masterPageId,
                        displayName,
                        iconUrl,
                        writeDuration: true);
                    AppLog.Info($"[同步] 自动合并 Notion 同日重复记录：「{displayName}」{canonical.Date} 合并 {group.Count} 个页面为单个页面（总时长 {totalMinutes} 分钟），已更新远端页面 {canonical.PageId}");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[同步] 更新 Notion 合并记录页面失败: {ex.Message}");
                }

                var mergedRecord = new NotionDailyRecordItem
                {
                    PageId = canonical.PageId,
                    Date = canonical.Date,
                    DurationMinutes = totalMinutes,
                    GameTitle = displayName,
                    GameMasterPageId = masterPageId,
                    RawTitle = DailyRecordTitle.Build(displayName, totalMinutes),
                    DurationUnitIsMinutes = false
                };

                deduplicatedRecords.Add(mergedRecord);
            }

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
            if (skippedOrphan > 0) parts.Add($"跳过 {skippedOrphan} 条总表已不存在的游戏");
            if (failed > 0) parts.Add($"{failed} 条失败（{lastError}）");

            SyncStatusChanged?.Invoke(this, string.Join("，", parts));

            return synced;
        }
        catch (Exception ex)
        {
            _lastPullSucceeded = false;
            SyncStatusChanged?.Invoke(this, $"每日时长表拉取失败: {FriendlyNotionError(ex)}");
            return 0;
        }
    }

    /// <summary>
    /// 判定一个日期是否属于历史日期（早于 7 天前）。
    /// 历史日期在 Notion 上的单次时长与日期为绝对权威数据，程序绝不能向 Notion 回推写时长。
    /// </summary>
    private static bool IsHistoricalDate(string dateStr)
    {
        if (DateTime.TryParse(dateStr, out var dt))
        {
            return dt.Date < DateTime.Today.AddDays(-7);
        }
        return false;
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

                // 标题与图标以总表（relation 指向的条目）为准：已绑定时用户在总表里
                // 改过的名字（多为中文名）才是他真正想看的。未绑定则退回进程名、不写图标。
                var (displayName, iconUrl) = await ResolveDailyDisplayAsync(item.GameName, gameNotionId);

                // 🚨 铁律：历史日期（早于 7 天前）的时长与日期在 Notion 上是绝对权威，程序绝不能向远端回推改写单次时长！
                // 只有近期（7 天内）由本地心跳累加的活跃会话，才允许更新 Notion 上的单次时长。
                bool isHistorical = IsHistoricalDate(item.Date);

                if (isHistorical && !string.IsNullOrEmpty(item.NotionPageId))
                {
                    // 历史记录远端已有页面，绝对不推回覆盖 Notion 权威数据，直接在本地置为已同步/未绑定
                    await _repo.UpdateDailySyncStatusAsync(item.Id, isBound ? "synced" : "unmapped");
                    continue;
                }

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
                    var newPageId = await _client.CreateDailyRecordAsync(
                        _config.DailyDatabaseId,
                        item.Date,
                        displayName,
                        item.DurationMinutes,
                        gameNotionId,
                        iconUrl);

                    await _repo.UpdateDailySyncStatusAsync(item.Id, isBound ? "synced" : "unmapped", newPageId);
                    await RecordRemoteSnapshotAsync(newPageId, displayName, item.DurationMinutes, iconUrl);
                }
                else
                {
                    await _client.UpdateDailyRecordAsync(
                        item.NotionPageId, item.DurationMinutes, gameNotionId, displayName, iconUrl,
                        writeDuration: !isHistorical);
                    await _repo.UpdateDailySyncStatusAsync(item.Id, isBound ? "synced" : "unmapped");
                    await RecordRemoteSnapshotAsync(item.NotionPageId, displayName, item.DurationMinutes, iconUrl);
                }
                count++;
            }
            catch (Exception ex)
            {
                // 页面在 Notion 已被删除（进回收站）→ 本地跟随删除语义清理，
                // 否则这条记录会永远对着一个 archived 页面重试，卡死在 error。
                if (ex.Message.Contains("archived"))
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

        // 先补总表 page icon，再回填每日记录。
        // 顺序不能反：下面的 ResolveDailyDisplayAsync 是从 game_catalog 的 IconUrl 取图标的，
        // 而 Steam 图标恰恰是 EnsureMasterPageIconsAsync 写进目录缓存和总表页面的。
        // 放在循环后面的话，每款游戏第一次回填时 icon 还是空的 —— 用户会看到
        // "图标要等下一轮同步才出现"，看起来像坏了。
        await EnsureMasterPageIconsAsync();

        var unmapped = await _repo.GetUnmappedDailySummariesAsync();
        int backfilled = 0;

        foreach (var item in unmapped)
        {
            var game = await _repo.GetGameByIdAsync(item.GameId);
            if (game == null) continue;

            if (!string.IsNullOrEmpty(game.NotionPageId))
            {
                try
                {
                    // 与 SyncPendingDailyRecordsAsync 保持一致：改用总表名称 + 总表 page icon。
                    var (displayName, iconUrl) = await ResolveDailyDisplayAsync(item.GameName, game.NotionPageId);

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
                            iconUrl);

                        await _repo.UpdateDailySyncStatusAsync(item.Id, "synced", newPageId);
                        await RecordRemoteSnapshotAsync(newPageId, displayName, item.DurationMinutes, iconUrl);
                    }
                    else
                    {
                        var originalSuffix = DailyRecordTitle.ExtractSuffix(item.NotionTitle ?? string.Empty);
                        var expectedTitle = originalSuffix.Length > 0
                            ? displayName + originalSuffix
                            : DailyRecordTitle.Build(displayName, item.DurationMinutes);

                        // writeDuration: false —— 回填 relation 与呈现，绝不修改远端时长数值
                        await _client.UpdateDailyRecordAsync(
                            item.NotionPageId, item.DurationMinutes, game.NotionPageId, displayName, iconUrl,
                            writeDuration: false,
                            titleOverride: expectedTitle);
                        await _repo.UpdateDailySyncStatusAsync(item.Id, "synced");
                        await RecordRemoteSnapshotAsync(item.NotionPageId, displayName, item.DurationMinutes, iconUrl);
                    }
                    backfilled++;
                }
                catch (Exception ex)
                {
                    // 页面在 Notion 已被删除（进回收站）→ 本地跟随删除语义清理，
                    // 否则这条记录会永远对着一个 archived 页面重试，卡死在 error。
                    if (ex.Message.Contains("archived"))
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
            else if (!string.IsNullOrEmpty(item.NotionPageId))
            {
                // 该游戏尚未绑定总表，若 Notion 每日记录上的「绑定状态」尚未标明为「未绑定」，则写入「未绑定」
                try
                {
                    var remote = _lastPulledDailyRecords?.FirstOrDefault(r => NormalizePageId(r.PageId) == NormalizePageId(item.NotionPageId));
                    if (remote == null || remote.Status != "未绑定")
                    {
                        await _client.UpdateDailyBindingStatusAsync(item.NotionPageId, "未绑定");
                        if (remote != null) remote.Status = "未绑定";
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[BackfillRelationsAsync] 补全未绑定状态失败 ({item.NotionPageId}): {ex.Message}");
                }
            }
        }

        // 统一对齐远端每日记录的绑定状态（例如历史记录或直接在 Notion 中添加但尚未有绑定状态的行）
        if (_lastPulledDailyRecords != null)
        {
            foreach (var r in _lastPulledDailyRecords)
            {
                if (string.IsNullOrEmpty(r.PageId)) continue;

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
                    }
                }
            }
        }

        if (backfilled > 0)
        {
            SyncStatusChanged?.Invoke(this, $"已为 {backfilled} 条每日记录关联游戏总表");
        }

        return backfilled;
    }

    /// <summary>
    /// 回刷：总表改名或补了 page icon 之后，把已同步的每日记录的标题与图标跟着改过来。
    /// </summary>
    /// <remarks>
    /// 为什么需要这一步：
    /// 每日记录的标题里嵌着游戏名（「不思议迷宫 · 0.7 h」）。用户在总表里改名
    /// （例如把英文进程名改成中文）后，relation 已经指向新名字，但**已经推送过的每日记录**标题
    /// 还是旧名 —— 拉取（Pull）只同步时长，不会碰标题，
    /// 于是历史记录会永远停在旧名上。这里补上这条回刷链路。
    /// 图标同理：绑定之后用户才给总表条目设图标的话，历史记录页面上是空的。
    ///
    /// 为什么不能每轮无条件 PATCH：
    /// 记录条数会随天数线性增长，每轮同步都全量 PATCH 既慢又浪费 Notion 配额。
    /// 因此逐条比对本地记录的快照（<c>notion_title</c> + <c>notion_icon_url</c>）与"期望值"，
    /// 只有真的不一致才发 PATCH。首轮快照为空的记录会被判为"需要回刷"，
    /// 之后就有了快照，后续轮次即为空操作。
    ///
    /// **标题与图标必须一起比对、一起写快照**。只比标题的话，
    /// "总表有条目设了图标"会让每一轮都判定需要更新（`iconUrl` 非空 ≠ 页面图标不对），
    /// 所有历史记录被反复 PATCH —— 正好把这个设计本来要解决的问题又引入回来。
    ///
    /// 只处理已绑定（notion_page_id 非空）的记录：未绑定的压根没有 relation 可读，
    /// 名字只能保持在进程名上。
    /// </remarks>
    public async Task<int> RefreshDailyTitlesFromMasterAsync()
    {
        if (!_config.IsNotionConfigured) return 0;

        IReadOnlyList<DailySummary> synced;
        try
        {
            synced = await _repo.GetSyncedDailySummariesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"回刷每日记录标题失败（读取本地记录）: {ex.Message}");
            return 0;
        }
        if (synced.Count == 0) return 0;

        // 总表快照一次性读进内存：不要放在循环里逐条查库 —— 记录多时是 N 次查询。
        var games = await _repo.GetAllGamesAsync();
        var gameById = games.ToDictionary(g => g.Id);

        int refreshed = 0;
        foreach (var item in synced)
        {
            try
            {
                if (!gameById.TryGetValue(item.GameId, out var game)) continue;
                if (string.IsNullOrWhiteSpace(game.NotionPageId)) continue;
                if (string.IsNullOrWhiteSpace(item.NotionPageId)) continue;

                // ⚠️ 时长为 0 的记录**一律不参与回刷**。
                // 回刷没法"只改标题"：标题串里就含时长（「游戏名 · X h」），
                // UpdateDailyRecordAsync 必须同时写标题和「单次时长」属性。
                // 于是拿本地的 0 去回刷 = 把 Notion 上原本的时长覆盖成 0，
                // 页面标题也变成「游戏名 · 0 h」——**时长数据就这么没了**。
                // 2026-09-19 QA 反馈的「喵门镖局 7/17 时长被清零」正是这个特征
                // （该行标题恰好是程序格式「喵门镖局 · 0 h」，不是她手写的格式）。
                if (item.DurationMinutes <= 0) continue;

                var (displayName, iconUrl) = await ResolveDailyDisplayAsync(item.GameName, game.NotionPageId);

                // ⚠️ 期望标题 = 总表的名字 + **原标题里原本的时长后缀**。
                //
                //    **绝不**用本地时长重新拼（DailyRecordTitle.Build）—— 那是在改 Notion 上的数据。
                //    2026-09-19 就是这么把 QA 的 786 条历史记录改掉的：
                //    「9/16 英灵神殿 2.2h」被写成了本地那份值（本地那份甚至可能是错的 ——
                //    单位换算那次把 5 分钟读成 5 小时，本地一直带着错值）。
                //
                //    Notion 侧的历史数值是**权威**，本地只是缓存。回刷只负责"呈现"：
                //    把游戏名换成总表的名字，时长文本原样保留。
                var originalSuffix = DailyRecordTitle.ExtractSuffix(item.NotionTitle ?? string.Empty);
                var expectedTitle = originalSuffix.Length > 0
                    // 有原标题 → 只把"游戏名"那一段换成总表的名字，时长文本**原样保留**
                    ? displayName + originalSuffix
                    // 没有原标题可参考（老库升级）→ 只能按本地时长拼；
                    // 但依然**不写**「单次时长」属性，数值属性始终以 Notion 上的为准。
                    : DailyRecordTitle.Build(displayName, item.DurationMinutes);

                // 标题与图标**都要**比对。只比标题的话，"总表设了图标"会让每一轮都判定
                // 需要更新（因为 iconUrl 非空 ≠ 页面图标不对），于是所有历史记录被反复 PATCH，
                // 正好把回刷本来要解决的性能问题又引入回来。
                var titleChanged = !string.Equals(item.NotionTitle, expectedTitle, StringComparison.Ordinal);
                var iconChanged = !string.Equals(item.NotionIconUrl, iconUrl, StringComparison.Ordinal);

                // 都是目标状态 → 什么都不做（绝大多数轮次都会走到这里）。
                if (!titleChanged && !iconChanged) continue;

                // 传**游戏名**（displayName），不是拼好的 expectedTitle：
                // UpdateDailyRecordAsync 内部会自己调 DailyRecordTitle.Build 拼标题，
                // 传完整标题进去会拼成「新名字 · 0.7 h · 0.7 h」——后缀重复。
                // expectedTitle 只用于上面的比对。
                //
                // ⚠️ 2026-09-19 起这里改用 **UpdateDailyRecordTitleAsync**：只写标题与图标，
                //    不写「单次时长」。原先用 UpdateDailyRecordAsync 会把本地时长一并写进
                //    Notion，等于拿缓存覆盖权威数据（毁灭性错误）。
                await _client.UpdateDailyRecordTitleAsync(
                    item.NotionPageId,
                    expectedTitle,
                    iconUrl);

                // 两个快照一起落库，否则下一轮还会认为"不一致"、又打一次 Notion。
                // 写回 0 行意味着本地找不到这条 page_id —— 属于数据异常，要留下痕迹，
                // 不然症状只是"同步一直很慢"，极难定位。
                var snapshotRows = await _repo.UpdateDailyRecordFromNotionAsync(
                    item.NotionPageId, expectedTitle, iconUrl);
                if (snapshotRows == 0)
                {
                    AppLog.Warn($"回刷：快照写回 0 行（notion_page_id={item.NotionPageId} 本地未匹配），下轮会重复请求");
                }
                refreshed++;
            }
            catch (Exception ex)
            {
                // 单条失败不阻断其余记录：回刷是尽力而为的补偿动作，下轮还会再来。
                AppLog.Warn($"回刷每日记录标题失败（{item.Date} {item.GameName}）: {ex.Message}");
            }
        }

        if (refreshed > 0)
        {
            SyncStatusChanged?.Invoke(this, $"已按总表更新 {refreshed} 条每日记录的标题");
        }

        return refreshed;
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
    /// 把本地未绑定游戏自动关联到 Notion 总表条目。
    /// 只接受确定性信号（identifier_match / exact / normalized）；
    /// 模糊候选一律不自动绑定，留给用户在映射库/Pending 页人工确认。
    /// </summary>
    public async Task<int> AutoLinkGamesFromCatalogAsync()
    {
        var allGames = await _repo.GetAllGamesAsync();
        var unlinked = allGames
            .Where(g => string.IsNullOrEmpty(g.NotionPageId) && g.Status != "ignored")
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
                    // 刚绑定总表 → 这批记录的名字/图标也应该按总表来，而不是继续用进程名。
                    var (displayName, iconUrl) = await ResolveDailyDisplayAsync(item.GameName, notionPageId);

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
                            iconUrl);

                        await _repo.UpdateDailySyncStatusAsync(item.Id, "synced", newPageId);
                        await RecordRemoteSnapshotAsync(newPageId, displayName, item.DurationMinutes, iconUrl);
                    }
                    else
                    {
                        var originalSuffix = DailyRecordTitle.ExtractSuffix(item.NotionTitle ?? string.Empty);
                        var expectedTitle = originalSuffix.Length > 0
                            ? displayName + originalSuffix
                            : DailyRecordTitle.Build(displayName, item.DurationMinutes);

                        // writeDuration: false —— 这里只是在补 relation 与改标题，
                        // 时长以 Notion 上的为准，不能用本地缓存去覆盖（2026-09-19 事故的教训）。
                        await _client.UpdateDailyRecordAsync(
                            item.NotionPageId, item.DurationMinutes, notionPageId, displayName, iconUrl,
                            writeDuration: false,
                            titleOverride: expectedTitle);
                        await _repo.UpdateDailySyncStatusAsync(item.Id, "synced");
                        await RecordRemoteSnapshotAsync(item.NotionPageId, displayName, item.DurationMinutes, iconUrl);
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

        // 1. Create page in Notion Game Master Database
        var newPageId = await _client.CreateGameMasterPageAsync(_config.GameDatabaseId, gameName);
        if (string.IsNullOrEmpty(newPageId))
        {
            throw new Exception("Notion 未返回新建页面的 ID");
        }

        // 2. Cache new game in local catalog
        await _repo.UpsertCatalogItemsAsync(new[]
        {
            new NotionGameCatalogItem
            {
                PageId = newPageId,
                Name = gameName,
                LastSyncedAt = DateTime.UtcNow
            }
        });

        // 3. Link and backfill all daily records for this game
        await LinkGameRelationAsync(gameId, newPageId);
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

        result.LocalDailyRecordsDeleted = await _repo.DeleteDailySummariesAsync(new[] { dailySummaryId });

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

                foreach (var row in orphanRows)
                {
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

                if (orphanRows.Count > 0)
                {
                    result.DeletedDailyRecords =
                        await _repo.DeleteDailySummariesAsync(orphanRows.Select(r => r.Id));
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
