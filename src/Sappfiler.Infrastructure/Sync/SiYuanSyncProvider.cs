using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.Sync;

/// <summary>
/// 思源笔记 (SiYuan) 同步提供者。
/// 通过思源官方本地 HTTP API (默认 127.0.0.1:6806) 同步游戏时长与文档记录。
/// </summary>
public sealed class SiYuanSyncProvider : ISyncProvider
{
    private readonly IDatabaseRepository _repo;
    private readonly HttpClient _httpClient;

    public string ProviderName => "siyuan";
    public string DisplayName => "思源笔记";

    public bool IsEnabled
    {
        get
        {
            var config = GetCurrentConfigAsync().GetAwaiter().GetResult();
            return config.Enabled && !string.IsNullOrWhiteSpace(config.NotebookId);
        }
    }

    public SiYuanSyncProvider(IDatabaseRepository repo, HttpClient? httpClient = null)
    {
        _repo = repo;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<SiYuanProviderConfig> GetCurrentConfigAsync()
    {
        var item = await _repo.GetProviderConfigAsync(ProviderName);
        if (item == null || string.IsNullOrWhiteSpace(item.ConfigJson))
        {
            return new SiYuanProviderConfig();
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<SiYuanProviderConfig>(item.ConfigJson);
            if (cfg != null)
            {
                cfg.Enabled = item.Enabled;
                return cfg;
            }
        }
        catch { }

        return new SiYuanProviderConfig { Enabled = item.Enabled };
    }

    public async Task<SyncResult> SyncDailyAsync(PlaytimeRecord record, GameMappingRecord? mapping)
    {
        var config = await GetCurrentConfigAsync();
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.NotebookId))
        {
            return SyncResult.Fail("思源笔记未配置笔记本 ID 或未启用");
        }

        try
        {
            // 优先检查是否配置了思源原生数据库 (Attribute View) 模式
            if (!string.IsNullOrWhiteSpace(config.DailyDatabaseId))
            {
                return await SyncDailyToDatabaseAsync(config, record, mapping);
            }

            // Fallback: 传统文档与 Markdown 表格模式
            return await SyncDailyToDocumentAsync(config, record);
        }
        catch (Exception ex)
        {
            return SyncResult.Fail($"思源笔记同步失败: {ex.Message}");
        }
    }

    private async Task<SyncResult> SyncDailyToDatabaseAsync(SiYuanProviderConfig config, PlaytimeRecord record, GameMappingRecord? mapping)
    {
        var dbId = config.DailyDatabaseId;
        var existingRecord = await _repo.GetSyncRecordAsync(record.DailySummaryId, ProviderName);
        string? rowId = existingRecord?.RemoteId;

        // 若本地无映射行 ID，通过 SQL 查找思源中是否已有同日同游戏的行
        if (string.IsNullOrWhiteSpace(rowId) && mapping != null && !string.IsNullOrWhiteSpace(mapping.RemoteId))
        {
            rowId = await QueryDailyRowIdAsync(config, dbId, record.Date, mapping.RemoteId);
        }

        if (string.IsNullOrWhiteSpace(rowId))
        {
            // 插入新行
            var insertPayload = new { avID = dbId };
            using var req = CreateRequest(config, HttpMethod.Post, "/api/av/insertAttrViewBlock", insertPayload);
            using var resp = await _httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    rowId = data.ValueKind == JsonValueKind.String
                        ? data.GetString()
                        : (data.TryGetProperty("id", out var idProp) ? idProp.GetString() : null);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(rowId))
        {
            // 设置/更新单元格属性
            await SetAttrViewCellAsync(config, dbId, rowId, "date", record.Date);
            await SetAttrViewCellAsync(config, dbId, rowId, "duration", Math.Round(record.DurationSeconds / 3600.0, 1));
            await SetAttrViewCellAsync(config, dbId, rowId, "session_count", record.SessionCount);
            if (mapping != null && !string.IsNullOrWhiteSpace(mapping.RemoteId))
            {
                await SetAttrViewRelationAsync(config, dbId, rowId, "game", mapping.RemoteId);
            }

            return SyncResult.Ok(rowId);
        }

        // 若无法通过 AV 插入，回退到 Markdown 文档模式
        return await SyncDailyToDocumentAsync(config, record);
    }

    private async Task<SyncResult> SyncDailyToDocumentAsync(SiYuanProviderConfig config, PlaytimeRecord record)
    {
        var daySummaries = await _repo.GetDailySummariesByDateAsync(record.Date);
        if (daySummaries.Count == 0)
        {
            daySummaries = new List<DailySummary>
            {
                new()
                {
                    Date = record.Date,
                    GameName = record.GameName,
                    DurationSeconds = record.DurationSeconds,
                    DurationMinutes = record.DurationMinutes,
                    SessionCount = record.SessionCount
                }
            };
        }

        var yearMonth = record.Date.Length >= 7 ? record.Date.Substring(0, 7) : record.Date;
        var docPath = $"{config.RootDocPath.TrimEnd('/')}/游戏记录/{yearMonth}/{record.Date}";

        var sbMd = new StringBuilder();
        sbMd.AppendLine($"# {record.Date} 游戏时长");
        sbMd.AppendLine();
        sbMd.AppendLine("| 游戏 | 时长 | 启动次数 |");
        sbMd.AppendLine("| --- | --- | --- |");

        foreach (var item in daySummaries)
        {
            var h = item.DurationMinutes / 60;
            var m = item.DurationMinutes % 60;
            var dur = h > 0 ? $"{h}h {m}m" : $"{m}m";
            sbMd.AppendLine($"| {item.GameName} | {dur} | {item.SessionCount} |");
        }

        var markdown = sbMd.ToString();

        var existingDocId = await QueryDocIdByHPathAsync(config, docPath);
        if (string.IsNullOrWhiteSpace(existingDocId))
        {
            var docId = await CreateDocWithMdAsync(config, docPath, markdown);
            return SyncResult.Ok(docId);
        }
        else
        {
            await UpdateDocContentAsync(config, existingDocId, markdown);
            return SyncResult.Ok(existingDocId);
        }
    }

    public async Task<string> CreateGameEntryAsync(GameRecord game)
    {
        var config = await GetCurrentConfigAsync();
        if (string.IsNullOrWhiteSpace(config.NotebookId))
        {
            throw new InvalidOperationException("未配置思源笔记本 ID");
        }

        // 优先检查是否配置了游戏总表数据库 (MasterDatabaseId)
        if (!string.IsNullOrWhiteSpace(config.MasterDatabaseId))
        {
            try
            {
                var insertPayload = new { avID = config.MasterDatabaseId };
                using var req = CreateRequest(config, HttpMethod.Post, "/api/av/insertAttrViewBlock", insertPayload);
                using var resp = await _httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        var rowId = data.ValueKind == JsonValueKind.String
                            ? data.GetString()
                            : (data.TryGetProperty("id", out var idProp) ? idProp.GetString() : null);

                        if (!string.IsNullOrWhiteSpace(rowId))
                        {
                            await SetAttrViewCellAsync(config, config.MasterDatabaseId, rowId, "name", game.Name);
                            await SetAttrViewCellAsync(config, config.MasterDatabaseId, rowId, "platform", game.Platform);
                            return rowId;
                        }
                    }
                }
            }
            catch { }
        }

        // Fallback: 建立游戏 Markdown 专属文档
        var docPath = $"{config.RootDocPath.TrimEnd('/')}/游戏/{SanitizeDocName(game.Name)}";
        var sb = new StringBuilder();
        sb.AppendLine($"# {game.Name}");
        sb.AppendLine();
        sb.AppendLine($"- **平台**: {game.Platform}");
        if (!string.IsNullOrWhiteSpace(game.PlatformId)) sb.AppendLine($"- **平台 ID**: {game.PlatformId}");
        sb.AppendLine($"- **创建时间**: {game.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **更新时间**: {game.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(game.CoverUrl))
        {
            sb.AppendLine($"![]({game.CoverUrl})");
            sb.AppendLine();
        }
        sb.AppendLine("## 游玩心得与笔记");

        var docId = await CreateDocWithMdAsync(config, docPath, sb.ToString());
        return docId;
    }

    public Task SyncGameMetadataAsync(GameRecord game, GameAggregateStats stats)
    {
        // 思源笔记原生通过游戏总表 Rollup 字段自动聚合每日打卡表时长，本地无需单独计算回写
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RemoteGameCandidate>> GetRemoteCatalogAsync(CancellationToken cancellationToken = default)
    {
        var config = await GetCurrentConfigAsync();
        var candidates = new List<RemoteGameCandidate>();
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.MasterDatabaseId))
        {
            return candidates;
        }

        try
        {
            // 优先通过 /api/av/renderAttributeView 获取游戏总表中的所有行及其标题
            var payload = new { id = config.MasterDatabaseId };
            using var req = CreateRequest(config, HttpMethod.Post, "/api/av/renderAttributeView", payload);
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("view", out var view) &&
                    view.TryGetProperty("rows", out var rows) &&
                    rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        var rowId = row.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        if (string.IsNullOrWhiteSpace(rowId)) continue;

                        string rowTitle = "";
                        // 获取主列（标题）文本
                        if (row.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cell in cells.EnumerateArray())
                            {
                                if (cell.TryGetProperty("value", out var valProp))
                                {
                                    if (valProp.ValueKind == JsonValueKind.String)
                                    {
                                        var s = valProp.GetString();
                                        if (!string.IsNullOrWhiteSpace(s)) { rowTitle = s; break; }
                                    }
                                    else if (valProp.TryGetProperty("content", out var contentProp))
                                    {
                                        var s = contentProp.GetString();
                                        if (!string.IsNullOrWhiteSpace(s)) { rowTitle = s; break; }
                                    }
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(rowTitle))
                        {
                            candidates.Add(new RemoteGameCandidate
                            {
                                Provider = ProviderName,
                                RemoteId = rowId,
                                RemoteName = rowTitle.Trim(),
                                Location = config.MasterDatabaseId
                            });
                        }
                    }
                }
            }

            // Fallback: 如果 AV API 未能获取到行，使用 SQL 查询该 AV 块下的子行 Block ID 与内容
            if (candidates.Count == 0)
            {
                var sql = $"SELECT id, content FROM blocks WHERE root_id = '{config.MasterDatabaseId}' OR parent_id = '{config.MasterDatabaseId}' LIMIT 200;";
                var sqlPayload = new { stmt = sql };
                using var sqlReq = CreateRequest(config, HttpMethod.Post, "/api/query/sql", sqlPayload);
                using var sqlResp = await _httpClient.SendAsync(sqlReq, cancellationToken);
                if (sqlResp.IsSuccessStatusCode)
                {
                    var json = await sqlResp.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in dataArr.EnumerateArray())
                        {
                            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            var content = item.TryGetProperty("content", out var contentProp) ? contentProp.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(content))
                            {
                                candidates.Add(new RemoteGameCandidate
                                {
                                    Provider = ProviderName,
                                    RemoteId = id,
                                    RemoteName = content.Trim(),
                                    Location = config.MasterDatabaseId
                                });
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return candidates;
    }

    public async Task<bool> TestConnectionAsync()
    {
        var config = await GetCurrentConfigAsync();
        try
        {
            using var req = CreateRequest(config, HttpMethod.Post, "/api/notebook/lsNotebooks", new { });
            using var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 0)
            {
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<(string Id, string Name)>> GetNotebooksAsync()
    {
        var config = await GetCurrentConfigAsync();
        var list = new List<(string Id, string Name)>();

        try
        {
            using var req = CreateRequest(config, HttpMethod.Post, "/api/notebook/lsNotebooks", new { });
            using var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return list;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 0 &&
                doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("notebooks", out var notebooks) &&
                notebooks.ValueKind == JsonValueKind.Array)
            {
                foreach (var nb in notebooks.EnumerateArray())
                {
                    var id = nb.GetProperty("id").GetString() ?? "";
                    var name = nb.GetProperty("name").GetString() ?? "";
                    list.Add((id, name));
                }
            }
        }
        catch { }

        return list;
    }

    public IReadOnlyList<ProviderConfigField> GetConfigFields()
    {
        return new List<ProviderConfigField>
        {
            new() { Key = "Endpoint", Label = "API 地址", Description = "思源笔记内核服务地址 (默认 http://127.0.0.1:6806)", DefaultValue = "http://127.0.0.1:6806", IsRequired = true },
            new() { Key = "Token", Label = "API Token", Description = "思源笔记 API Token (设置 → 关于 → API Token)", IsSecret = true },
            new() { Key = "NotebookId", Label = "笔记本 ID", Description = "用于保存 Sappfiler 记录的笔记本 ID", IsRequired = true },
            new() { Key = "MasterDatabaseId", Label = "游戏总表数据库 ID", Description = "思源游戏总表数据库 Block ID（用于支持 Rollup 自动汇总）" },
            new() { Key = "DailyDatabaseId", Label = "每日打卡数据库 ID", Description = "思源每日打卡数据库 Block ID（用于记录每日时长与 Relation 关联）" },
            new() { Key = "RootDocPath", Label = "根文档路径", Description = "文档模式下的根目录路径", DefaultValue = "/Sappfiler" }
        };
    }

    /// <summary>
    /// 在思源「每日打卡」属性视图中查找「同日期 + 同游戏」的既有行。
    ///
    /// ⚠️ 必须同时匹配日期与游戏。原实现是
    /// <c>SELECT id FROM blocks WHERE root_id = avId AND content LIKE '%date%' LIMIT 1</c>，
    /// 参数 <paramref name="gameBlockId"/> 被完全忽略 —— 同一天的第二款游戏会命中
    /// 第一款游戏那一行并被覆盖写入，属于**数据损坏**级别的问题（同一天多款游戏是常态）。
    ///
    /// 匹配规则：该行的某个单元格的 relation blockIDs 含 <paramref name="gameBlockId"/>，
    /// 且某个单元格的文本形态里出现 <paramref name="date"/>。
    /// 拿不准时**返回 null**（宁可新插一行，也绝不误改别人那一行）。
    /// </summary>
    private async Task<string?> QueryDailyRowIdAsync(SiYuanProviderConfig config, string avId, string date, string gameBlockId)
    {
        if (string.IsNullOrWhiteSpace(gameBlockId)) return null;

        try
        {
            using var req = CreateRequest(config, HttpMethod.Post, "/api/av/renderAttributeView", new { id = avId });
            using var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("view", out var view) ||
                !view.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var row in rows.EnumerateArray())
            {
                var rowId = row.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(rowId)) continue;
                if (!row.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array) continue;

                bool gameMatched = false;
                bool dateMatched = false;
                foreach (var cell in cells.EnumerateArray())
                {
                    if (!cell.TryGetProperty("value", out var val)) continue;
                    if (!gameMatched && ContainsBlockId(val, gameBlockId)) gameMatched = true;
                    if (!dateMatched && ContainsDateText(val, date)) dateMatched = true;
                    if (gameMatched && dateMatched) return rowId;
                }
            }

            AppLog.Warn($"[思源] 每日打卡数据库中未找到 {date} / 游戏 {gameBlockId} 的既有行，将新建一行");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[思源] 查询每日打卡行失败：{ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    /// <summary>relation 单元格的 blockIDs 里是否包含指定块 ID。</summary>
    private static bool ContainsBlockId(JsonElement value, string blockId)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("blockIDs", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in ids.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String &&
                    string.Equals(x.GetString(), blockId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static readonly string[] TextLikePropNames = { "content", "formattedContent", "text", "name" };

    /// <summary>
    /// 单元格值里是否出现了该日期文本。思源的日期列可能是字符串，也可能是
    /// 时间戳（content 为数字）或 formattedContent，这里逐个尽力识别。
    /// </summary>
    private static bool ContainsDateText(JsonElement value, string date)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString()?.Contains(date, StringComparison.Ordinal) == true;

            case JsonValueKind.Object:
                foreach (var name in TextLikePropNames)
                {
                    if (value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
                        p.GetString()?.Contains(date, StringComparison.Ordinal) == true)
                    {
                        return true;
                    }
                }

                if (value.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Number &&
                    c.TryGetInt64(out var raw))
                {
                    // 13 位按毫秒、10 位按秒（思源两种历史形态都出现过）
                    var ms = raw > 100_000_000_000L ? raw : raw * 1000L;
                    try
                    {
                        return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
                            .ToString("yyyy-MM-dd") == date;
                    }
                    catch
                    {
                        return false;
                    }
                }
                break;
        }

        return false;
    }

    private Task SetAttrViewCellAsync(SiYuanProviderConfig config, string avId, string rowId, string keyId, object value)
        => SendAttrViewAttrAsync(
            config,
            new { avID = avId, keyID = keyId, rowID = rowId, value },
            $"keyID={keyId}, rowID={rowId}");

    private Task SetAttrViewRelationAsync(SiYuanProviderConfig config, string avId, string rowId, string keyId, string targetBlockId)
        => SendAttrViewAttrAsync(
            config,
            new { avID = avId, keyID = keyId, rowID = rowId, value = new { blockIDs = new[] { targetBlockId } } },
            $"relation keyID={keyId}, rowID={rowId}");

    /// <summary>
    /// 调 /api/av/setAttrViewBlockAttr 写属性，并把失败暴露到日志。
    ///
    /// 原实现完全丢弃响应，而思源**即使逻辑失败也可能返回 HTTP 200 + <c>{"code":-1,"msg":"..."}</c>**
    /// （例如 keyID 传的是列名而不是列的块 ID 时）→ 表现为"同步成功但思源里什么都没有"，极难排查。
    /// 这里只做观测增强：仍然不抛异常，避免单个单元格失败中断整条同步。
    /// </summary>
    private async Task SendAttrViewAttrAsync(SiYuanProviderConfig config, object payload, string describe)
    {
        try
        {
            using var req = CreateRequest(config, HttpMethod.Post, "/api/av/setAttrViewBlockAttr", payload);
            using var resp = await _httpClient.SendAsync(req);
            var body = await SafeReadAsync(resp);

            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"[思源] 写入属性失败 ({describe}, HTTP={(int)resp.StatusCode})：{Truncate(body)}");
                return;
            }

            if (TryGetSiYuanCode(body, out var code, out var msg) && code != 0)
            {
                AppLog.Warn($"[思源] 写入属性被拒绝 ({describe}, code={code})：{msg}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[思源] 写入属性异常 ({describe})：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync(); }
        catch { return string.Empty; }
    }

    private static string Truncate(string s)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= 300 ? s : s[..300]);

    private static bool TryGetSiYuanCode(string json, out int code, out string msg)
    {
        code = 0;
        msg = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
            {
                code = c.GetInt32();
                msg = doc.RootElement.TryGetProperty("msg", out var m) ? (m.GetString() ?? string.Empty) : string.Empty;
                return true;
            }
        }
        catch { }

        return false;
    }

    private async Task<string?> QueryDocIdByHPathAsync(SiYuanProviderConfig config, string hpath)
    {
        try
        {
            var sql = $"SELECT id FROM blocks WHERE box = '{config.NotebookId}' AND hpath = '{hpath.Replace("'", "''")}' LIMIT 1;";
            using var req = CreateRequest(config, HttpMethod.Post, "/api/query/sql", new { stmt = sql });
            using var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 0 &&
                doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (first.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString();
                }
            }
        }
        catch { }

        return null;
    }

    private async Task<string> CreateDocWithMdAsync(SiYuanProviderConfig config, string path, string markdown)
    {
        var payload = new
        {
            notebook = config.NotebookId,
            path,
            markdown
        };

        using var req = CreateRequest(config, HttpMethod.Post, "/api/filetree/createDocWithMd", payload);
        using var resp = await _httpClient.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 0 &&
            doc.RootElement.TryGetProperty("data", out var data))
        {
            return data.GetString() ?? "";
        }

        var msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "创建文档失败";
        throw new InvalidOperationException($"思源返回错误: {msg}");
    }

    private async Task UpdateDocContentAsync(SiYuanProviderConfig config, string docId, string markdown)
    {
        // 查找文档下的所有子块
        var sql = $"SELECT id FROM blocks WHERE root_id = '{docId}' AND parent_id = '{docId}' AND type != 'd' ORDER BY sort ASC;";
        using var req = CreateRequest(config, HttpMethod.Post, "/api/query/sql", new { stmt = sql });
        using var resp = await _httpClient.SendAsync(req);

        if (resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                // 先更新第一个非标题块（如果有），或追加新块
                var blocks = data.EnumerateArray().ToList();
                if (blocks.Count > 0)
                {
                    var firstBlockId = blocks[0].GetProperty("id").GetString();
                    if (!string.IsNullOrWhiteSpace(firstBlockId))
                    {
                        var updatePayload = new
                        {
                            id = firstBlockId,
                            dataType = "markdown",
                            data = markdown
                        };
                        using var upReq = CreateRequest(config, HttpMethod.Post, "/api/block/updateBlock", updatePayload);
                        using var upResp = await _httpClient.SendAsync(upReq);
                        if (upResp.IsSuccessStatusCode) return;
                    }
                }
            }
        }

        // 若无法更新已有块，则直接向文档追加 Block
        var appendPayload = new
        {
            parentID = docId,
            dataType = "markdown",
            data = markdown
        };
        using var appReq = CreateRequest(config, HttpMethod.Post, "/api/block/appendBlock", appendPayload);
        using var appResp = await _httpClient.SendAsync(appReq);
        appResp.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(SiYuanProviderConfig config, HttpMethod method, string path, object body)
    {
        var endpoint = config.Endpoint.TrimEnd('/');
        var req = new HttpRequestMessage(method, $"{endpoint}{path}");
        if (!string.IsNullOrWhiteSpace(config.Token))
        {
            req.Headers.Add("Authorization", $"Token {config.Token}");
        }

        var json = JsonSerializer.Serialize(body);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private static string SanitizeDocName(string name)
    {
        var invalid = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString().Trim();
    }
}
