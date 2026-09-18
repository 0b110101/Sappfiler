using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;

namespace GameTimeTracker.Infrastructure.Notion;

public class NotionClient : INotionClient
{
    private const string BaseUrl = "https://api.notion.com/v1";
    private const string NotionVersion = "2022-06-28";

    private readonly HttpClient _httpClient;
    private string _token;
    private readonly int _maxRetries;

    /// <summary>
    /// 每日记录标题里的时长后缀，形如「不思议迷宫 · 42 min」；
    /// 同时兼容早期格式「不思议迷宫 (42分)」——Notion 里可能还留着旧行。
    /// 从右往左锚定：游戏名自身可能含 · 或 |，不能从左切。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex DurationSuffixRegex =
        new(@"\s*(?:[·|]\s*\d+(?:\.\d+)?\s*(?:min|h)|\(\s*\d+\s*分\s*\))\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>拼装每日记录标题：时长以小时显示（1 位小数），如「Master Key · 0.7 h」。</summary>
    private static string BuildDailyRecordTitle(string gameTitle, int durationMinutes)
        => $"{gameTitle} · {Math.Round(durationMinutes / 60.0, 1)} h";

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
                    var title = DurationSuffixRegex.Replace(rawTitle, string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = rawTitle.Trim();
                    }

                    var date = ExtractDate(props);
                    var duration = ExtractNumber(props, "时长", "时长(分)", "Duration", "DurationMinutes");
                    var gameMasterPageId = ExtractRelationId(props, "游戏", "游戏总表", "Game");
                    var status = ExtractSelect(props, "绑定状态", "Status");

                    if (!string.IsNullOrWhiteSpace(date) && (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(gameMasterPageId)))
                    {
                        results.Add(new NotionDailyRecordItem
                        {
                            PageId = pageId,
                            GameTitle = title,
                            Date = date,
                            DurationMinutes = duration,
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
        string gameTitle,
        int durationMinutes,
        string? gamePageId)
    {
        var cleanDbId = dailyDbId.Replace("-", "");
        var titleText = BuildDailyRecordTitle(gameTitle, durationMinutes);
        var properties = new Dictionary<string, object>
        {
            ["游戏名称"] = new { title = new[] { new { text = new { content = titleText } } } },
            ["日期"] = new { date = new { start = date } },
            ["时长"] = new { number = durationMinutes },
            ["绑定状态"] = new { select = new { name = !string.IsNullOrEmpty(gamePageId) ? "已绑定" : "未绑定" } }
        };

        if (!string.IsNullOrEmpty(gamePageId))
        {
            properties["游戏"] = new { relation = new[] { new { id = gamePageId } } };
        }

        var payload = new
        {
            parent = new { database_id = cleanDbId },
            properties
        };

        using var req = CreateRequest(HttpMethod.Post, "/pages", payload);
        var res = await SendWithRetryAsync(req);
        return res.GetProperty("id").GetString() ?? "";
    }

    public async Task<bool> UpdateDailyRecordAsync(string pageId, int durationMinutes, string? gamePageId, string? gameTitle = null)
    {
        var cleanPageId = pageId.Replace("-", "");
        var properties = new Dictionary<string, object>
        {
            ["时长"] = new { number = durationMinutes }
        };

        if (!string.IsNullOrEmpty(gameTitle))
        {
            var titleText = BuildDailyRecordTitle(gameTitle, durationMinutes);
            properties["游戏名称"] = new { title = new[] { new { text = new { content = titleText } } } };
        }

        if (!string.IsNullOrEmpty(gamePageId))
        {
            properties["游戏"] = new { relation = new[] { new { id = gamePageId } } };
            properties["绑定状态"] = new { select = new { name = "已绑定" } };
        }

        using var req = CreateRequest(HttpMethod.Patch, $"/pages/{cleanPageId}", new { properties });
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

    private static int ExtractNumber(JsonElement props, params string[] propertyNames)
    {
        foreach (var pName in propertyNames)
        {
            if (props.TryGetProperty(pName, out var pObj) && pObj.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "number" && pObj.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number)
                {
                    return n.GetInt32();
                }
            }
        }
        return 0;
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
        _matcher = matcher ?? new GameMatcher(
            fuzzyThreshold: config.FuzzyCandidateThreshold,
            scoreGapThreshold: config.FuzzyScoreGapThreshold);
    }

    public async Task RefreshGameCatalogCacheAsync()
    {
        if (!_config.IsNotionConfigured) return;

        try
        {
            SyncStatusChanged?.Invoke(this, "正在刷新游戏库...");
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
                    removed > 0 ? $"游戏库已更新（清理 {removed} 条已失效条目）" : "游戏库已更新");
                return;
            }

            SyncStatusChanged?.Invoke(this, "游戏库已更新");
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
            SyncStatusChanged?.Invoke(this, "正在从 Notion 拉取每日记录...");
            var records = await _client.QueryDailyRecordsAsync(_config.DailyDatabaseId);
            _lastPullSucceeded = true;
            int synced = 0;
            int skippedOrphan = 0;
            int failed = 0;
            string? lastError = null;

            // 防"复活"：每日记录若指向一个已不在总表里的游戏（总表条目被删了），
            // 就不能再拉回本地 —— 否则 SyncDailyRecordFromNotionAsync 会凭空把游戏造回来，
            // 于是你在程序里刚删掉的游戏下一轮同步又冒出来。
            var catalogIds = (await _repo.GetCatalogItemsAsync())
                .Select(c => NormalizePageId(c.PageId))
                .Where(id => id.Length > 0)
                .ToHashSet();

            foreach (var r in records)
            {
                if (catalogIds.Count > 0 &&
                    !string.IsNullOrWhiteSpace(r.GameMasterPageId) &&
                    !catalogIds.Contains(NormalizePageId(r.GameMasterPageId)))
                {
                    skippedOrphan++;
                    continue;
                }

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
            parts.Add($"已从 Notion 同步 {synced} 条记录");
            if (skippedOrphan > 0) parts.Add($"跳过 {skippedOrphan} 条总表已不存在的游戏");
            if (failed > 0) parts.Add($"{failed} 条失败（{lastError}）");

            SyncStatusChanged?.Invoke(this, string.Join("，", parts));

            return synced;
        }
        catch (Exception ex)
        {
            _lastPullSucceeded = false;
            SyncStatusChanged?.Invoke(this, $"拉取记录失败: {FriendlyNotionError(ex)}");
            return 0;
        }
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

                if (string.IsNullOrEmpty(item.NotionPageId))
                {
                    var newPageId = await _client.CreateDailyRecordAsync(
                        _config.DailyDatabaseId,
                        item.Date,
                        item.GameName,
                        item.DurationMinutes,
                        gameNotionId);

                    await _repo.UpdateDailySyncStatusAsync(item.Id, isBound ? "synced" : "unmapped", newPageId);
                }
                else
                {
                    await _client.UpdateDailyRecordAsync(item.NotionPageId, item.DurationMinutes, gameNotionId, item.GameName);
                    await _repo.UpdateDailySyncStatusAsync(item.Id, isBound ? "synced" : "unmapped");
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

        var unmapped = await _repo.GetUnmappedDailySummariesAsync();
        int backfilled = 0;

        foreach (var item in unmapped)
        {
            var game = await _repo.GetGameByIdAsync(item.GameId);
            if (game == null || string.IsNullOrEmpty(game.NotionPageId)) continue;

            try
            {
                if (string.IsNullOrEmpty(item.NotionPageId))
                {
                    var newPageId = await _client.CreateDailyRecordAsync(
                        _config.DailyDatabaseId,
                        item.Date,
                        item.GameName,
                        item.DurationMinutes,
                        game.NotionPageId);

                    await _repo.UpdateDailySyncStatusAsync(item.Id, "synced", newPageId);
                }
                else
                {
                    await _client.UpdateDailyRecordAsync(item.NotionPageId, item.DurationMinutes, game.NotionPageId, item.GameName);
                    await _repo.UpdateDailySyncStatusAsync(item.Id, "synced");
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

        if (backfilled > 0)
        {
            SyncStatusChanged?.Invoke(this, $"已为 {backfilled} 条每日记录关联游戏总表");
        }

        await EnsureMasterPageIconsAsync();
        return backfilled;
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

            var deterministic = candidates
                .Where(c => c.MatchType is "identifier_match" or "exact" or "normalized")
                .ToList();

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
                    if (string.IsNullOrEmpty(item.NotionPageId))
                    {
                        var newPageId = await _client.CreateDailyRecordAsync(
                            _config.DailyDatabaseId,
                            item.Date,
                            item.GameName,
                            item.DurationMinutes,
                            notionPageId);

                        await _repo.UpdateDailySyncStatusAsync(item.Id, "synced", newPageId);
                    }
                    else
                    {
                        await _client.UpdateDailyRecordAsync(item.NotionPageId, item.DurationMinutes, notionPageId, item.GameName);
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
                // 只处理「本地认为已同步」的行：pending / error 的行本来就可能还没进 Notion，
                // 不在远端集合里属于正常，不能删。
                var orphanRows = localSynced
                    .Where(d => d.SyncStatus == "synced")
                    .Where(d => !remoteIds.Contains(NormalizePageId(d.NotionPageId)))
                    .ToList();

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
                var orphanGames = games
                    .Where(g => !string.IsNullOrWhiteSpace(g.NotionPageId))
                    .Where(g => !catalogIds.Contains(NormalizePageId(g.NotionPageId)))
                    .ToList();

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
