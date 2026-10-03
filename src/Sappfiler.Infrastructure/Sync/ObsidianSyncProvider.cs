using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.Sync;

/// <summary>
/// Obsidian 同步提供者。
/// 支持「本地文件直写模式」（默认）与「Local REST API 插件模式」。
/// 支持原子写入防冲突，以及标记区间增量合并，保留用户手写笔记。
/// </summary>
public sealed class ObsidianSyncProvider : ISyncProvider
{
    private readonly IDatabaseRepository _repo;
    private readonly HttpClient _httpClient;

    public string ProviderName => "obsidian";
    public string DisplayName => "Obsidian";

    public bool IsEnabled
    {
        get
        {
            var config = GetCurrentConfigAsync().GetAwaiter().GetResult();
            if (!config.Enabled) return false;

            if (string.Equals(config.SyncMode, "api", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(config.ApiKey);
            }
            return !string.IsNullOrWhiteSpace(config.VaultPath) && Directory.Exists(config.VaultPath);
        }
    }

    public ObsidianSyncProvider(IDatabaseRepository repo, HttpClient? httpClient = null)
    {
        _repo = repo;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (message.RequestUri != null &&
                    (message.RequestUri.Host == "127.0.0.1" || message.RequestUri.Host == "localhost"))
                {
                    return true;
                }
                return errors == System.Net.Security.SslPolicyErrors.None;
            }
        };
        return new HttpClient(handler);
    }

    public async Task<ObsidianProviderConfig> GetCurrentConfigAsync()
    {
        var item = await _repo.GetProviderConfigAsync(ProviderName);
        if (item == null || string.IsNullOrWhiteSpace(item.ConfigJson))
        {
            return new ObsidianProviderConfig();
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<ObsidianProviderConfig>(item.ConfigJson);
            if (cfg != null)
            {
                cfg.Enabled = item.Enabled;
                return cfg;
            }
        }
        catch { }

        return new ObsidianProviderConfig { Enabled = item.Enabled };
    }

    public async Task<SyncResult> SyncDailyAsync(PlaytimeRecord record, GameMappingRecord? mapping)
    {
        var config = await GetCurrentConfigAsync();
        if (!config.Enabled)
        {
            return SyncResult.Fail("Obsidian 同步未启用");
        }

        try
        {
            // 查询当天所有游戏的游玩时长记录，聚合为一张完整的 Markdown 日报
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

            var totalMinutes = daySummaries.Sum(s => s.DurationMinutes);
            var totalDurationStr = $"{totalMinutes / 60}h {totalMinutes % 60}m";
            var gameCount = daySummaries.Count;

            var frontmatter = $"""
                ---
                generated_by: sappfiler
                date: {record.Date}
                total_duration: {totalDurationStr}
                game_count: {gameCount}
                ---
                """.Trim();

            var sbTable = new StringBuilder();
            sbTable.AppendLine("<!-- ⚠️ 以下内容由 Sappfiler 自动生成，手动修改将在下次同步时被覆盖 -->");
            sbTable.AppendLine();
            sbTable.AppendLine("## 游戏时长");
            sbTable.AppendLine();
            sbTable.AppendLine("| 游戏 | 时长 | 次数 |");
            sbTable.AppendLine("|------|------|------|");

            foreach (var item in daySummaries)
            {
                var h = item.DurationMinutes / 60;
                var m = item.DurationMinutes % 60;
                var dur = h > 0 ? $"{h}h {m}m" : $"{m}m";

                string link;
                var itemMapping = await _repo.GetGameMappingAsync(item.GameId, ProviderName);
                if (itemMapping != null && !string.IsNullOrWhiteSpace(itemMapping.RemoteName))
                {
                    var linkTarget = itemMapping.RemoteName;
                    if (string.Equals(linkTarget, item.GameName, StringComparison.OrdinalIgnoreCase))
                    {
                        link = $"[[{linkTarget}]]";
                    }
                    else
                    {
                        link = $"[[{linkTarget}|{item.GameName}]]";
                    }
                }
                else
                {
                    link = $"[[{item.GameName}]]";
                }

                sbTable.AppendLine($"| {link} | {dur} | {item.SessionCount} |");
            }

            sbTable.AppendLine();
            sbTable.Append("<!-- Sappfiler End -->");
            var generatedSection = sbTable.ToString();

            var relativePath = Path.Combine(config.DailySubfolder, $"{record.Date}.md").Replace('\\', '/');

            if (string.Equals(config.SyncMode, "api", StringComparison.OrdinalIgnoreCase))
            {
                await SyncViaApiAsync(config, relativePath, frontmatter, generatedSection);
            }
            else
            {
                await SyncViaFileAsync(config, relativePath, frontmatter, generatedSection);
            }

            return SyncResult.Ok(relativePath);
        }
        catch (Exception ex)
        {
            return SyncResult.Fail($"Obsidian 同步失败: {ex.Message}");
        }
    }

    private async Task SyncViaFileAsync(ObsidianProviderConfig config, string relativePath, string frontmatter, string generatedSection)
    {
        if (string.IsNullOrWhiteSpace(config.VaultPath) || !Directory.Exists(config.VaultPath))
        {
            throw new DirectoryNotFoundException($"Vault 目录不存在: {config.VaultPath}");
        }

        var fullPath = Path.Combine(config.VaultPath, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string existingContent = "";
        if (File.Exists(fullPath))
        {
            existingContent = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        }

        var merged = MergeDailyContent(existingContent, frontmatter, generatedSection);

        // 原子写入：先写入临时文件，再移动覆盖，避免触发 Obsidian 中间态文件监控事件
        var tempPath = fullPath + ".sappfiler.tmp";
        await File.WriteAllTextAsync(tempPath, merged, Encoding.UTF8);
        File.Move(tempPath, fullPath, overwrite: true);
    }

    private async Task SyncViaApiAsync(ObsidianProviderConfig config, string relativePath, string frontmatter, string generatedSection)
    {
        var endpoint = $"https://127.0.0.1:{config.ApiPort}";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/vault/{Uri.EscapeDataString(relativePath)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey ?? "");

        string existingContent = "";
        try
        {
            using var resp = await _httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                existingContent = await resp.Content.ReadAsStringAsync();
            }
        }
        catch { }

        var merged = MergeDailyContent(existingContent, frontmatter, generatedSection);

        using var putReq = new HttpRequestMessage(HttpMethod.Put, $"{endpoint}/vault/{Uri.EscapeDataString(relativePath)}");
        putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey ?? "");
        putReq.Content = new StringContent(merged, Encoding.UTF8, "text/markdown");

        using var putResp = await _httpClient.SendAsync(putReq);
        putResp.EnsureSuccessStatusCode();
    }

    public static string MergeDailyContent(string existingContent, string frontmatter, string generatedSection)
    {
        const string startMarker = "<!-- ⚠️ 以下内容由 Sappfiler 自动生成";
        const string endMarker = "<!-- Sappfiler End -->";

        string body = existingContent;
        string? existingFmText = null;

        // 若已有 frontmatter，先剥离它以避免生成重复 frontmatter。
        // 注意：剥离下来的原文要留着 —— 用户写在顶部的自定义属性必须原样合并回去。
        if (existingContent.TrimStart().StartsWith("---"))
        {
            var trimmed = existingContent.TrimStart();
            var secondDash = trimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (secondDash >= 0)
            {
                existingFmText = trimmed.Substring(3, secondDash - 3);
                var endOfFm = secondDash + 4;
                if (endOfFm < trimmed.Length && trimmed[endOfFm] == '\r') endOfFm++;
                if (endOfFm < trimmed.Length && trimmed[endOfFm] == '\n') endOfFm++;
                body = trimmed.Substring(endOfFm);
            }
        }

        var startIdx = body.IndexOf(startMarker, StringComparison.Ordinal);
        var endIdx = body.IndexOf(endMarker, StringComparison.Ordinal);

        string mergedBody;
        if (startIdx >= 0 && endIdx >= 0 && endIdx >= startIdx)
        {
            var before = body.Substring(0, startIdx);
            var after = body.Substring(endIdx + endMarker.Length);
            mergedBody = before + generatedSection + after;
        }
        else
        {
            mergedBody = string.IsNullOrWhiteSpace(body)
                ? generatedSection
                : generatedSection + "\n\n" + body.TrimStart();
        }

        // 只覆盖 Sappfiler 自己管理的那几个键（generated_by / date / total_duration / game_count），
        // 用户手写的其它 frontmatter 属性一律保留。
        // 原实现直接丢弃整段旧 frontmatter，用户记录会在下次同步时静默消失，与
        // README「用户手写内容 100% 保留」的承诺不符。
        var mergedFrontmatter = MergeFrontmatter(existingFmText, frontmatter, _ => false);

        return mergedFrontmatter + "\n\n" + mergedBody.Trim() + "\n";
    }

    /// <summary>
    /// 合并 frontmatter：以 <paramref name="generatedFrontmatter"/>（Sappfiler 生成的键）为准，
    /// 其余用户原有行原样追加保留；<paramref name="isForeignManagedKey"/> 用于丢弃
    /// 属于 Sappfiler 命名空间但本次未重新生成的旧键（例如历史遗留的 sappfiler-* 行）。
    ///
    /// 按「整行」而非「key: value 字典」处理 —— 这样 YAML 列表（<c>aliases:</c> + <c>- Foo</c>）、
    /// 多行块值与注释都不会被压平或丢掉。
    /// </summary>
    private static string MergeFrontmatter(
        string? existingFmText,
        string generatedFrontmatter,
        Func<string, bool> isForeignManagedKey)
    {
        if (string.IsNullOrWhiteSpace(existingFmText)) return generatedFrontmatter;

        var generatedInner = SplitLines(generatedFrontmatter.Trim())
            .Where(l => l.Trim() != "---")
            .ToList();

        var managedKeys = generatedInner
            .Select(FrontmatterKeyOf)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<string>(generatedInner);
        foreach (var line in SplitLines(existingFmText))
        {
            var raw = line.TrimEnd();
            if (raw.Length == 0) continue;

            var key = FrontmatterKeyOf(raw);
            if (key.Length > 0 && (managedKeys.Contains(key) || isForeignManagedKey(key))) continue;

            result.Add(raw);
        }

        return "---\n" + string.Join("\n", result) + "\n---";
    }

    /// <summary>
    /// 取 frontmatter 行的键名；不是 <c>key: value</c> 形式（如列表项 <c>- Foo</c>）时返回空串，
    /// 这些行会被原样保留。
    /// </summary>
    private static string FrontmatterKeyOf(string line)
    {
        var idx = line.IndexOf(':');
        if (idx <= 0) return string.Empty;

        var key = line.Substring(0, idx).Trim();
        if (key.Length == 0) return string.Empty;
        if (key.StartsWith("-", StringComparison.Ordinal)) return string.Empty;
        return key;
    }

    private static List<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    public async Task<string> CreateGameEntryAsync(GameRecord game)
    {
        var config = await GetCurrentConfigAsync();
        var sanitizedName = SanitizeFileName(game.Name);
        var relativePath = Path.Combine(config.GamesSubfolder, $"{sanitizedName}.md").Replace('\\', '/');

        var stats = await _repo.GetAggregateStatsAsync(game.Id);
        await SyncGameMetadataAsync(game, stats);

        return relativePath;
    }

    public async Task SyncGameMetadataAsync(GameRecord game, GameAggregateStats stats)
    {
        var config = await GetCurrentConfigAsync();
        if (!config.Enabled) return;

        var sanitizedName = SanitizeFileName(game.Name);
        var relativePath = Path.Combine(config.GamesSubfolder, $"{sanitizedName}.md").Replace('\\', '/');

        string existingContent = "";
        if (string.Equals(config.SyncMode, "api", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = $"https://127.0.0.1:{config.ApiPort}";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/vault/{Uri.EscapeDataString(relativePath)}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey ?? "");
                using var resp = await _httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    existingContent = await resp.Content.ReadAsStringAsync();
                }
            }
            catch { }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.VaultPath) || !Directory.Exists(config.VaultPath))
            {
                return;
            }
            var fullPath = Path.Combine(config.VaultPath, relativePath);
            if (File.Exists(fullPath))
            {
                existingContent = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
            }
        }

        var merged = MergeGameContent(existingContent, game, stats);

        if (string.Equals(config.SyncMode, "api", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = $"https://127.0.0.1:{config.ApiPort}";
            using var putReq = new HttpRequestMessage(HttpMethod.Put, $"{endpoint}/vault/{Uri.EscapeDataString(relativePath)}");
            putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey ?? "");
            putReq.Content = new StringContent(merged, Encoding.UTF8, "text/markdown");
            using var resp = await _httpClient.SendAsync(putReq);
            resp.EnsureSuccessStatusCode();
        }
        else
        {
            var fullPath = Path.Combine(config.VaultPath, relativePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var tempPath = fullPath + ".sappfiler.tmp";
            await File.WriteAllTextAsync(tempPath, merged, Encoding.UTF8);
            File.Move(tempPath, fullPath, overwrite: true);
        }
    }

    public static string MergeGameContent(string existingContent, GameRecord game, GameAggregateStats stats)
    {
        const string startMarker = "<!-- ⚠️ 以下内容由 Sappfiler 自动生成";
        const string endMarker = "<!-- Sappfiler End -->";

        string body = existingContent;
        string? existingFmText = null;

        if (existingContent.TrimStart().StartsWith("---"))
        {
            var trimmed = existingContent.TrimStart();
            var secondDash = trimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (secondDash >= 0)
            {
                existingFmText = trimmed.Substring(3, secondDash - 3);
                var endOfFm = secondDash + 4;
                if (endOfFm < trimmed.Length && trimmed[endOfFm] == '\r') endOfFm++;
                if (endOfFm < trimmed.Length && trimmed[endOfFm] == '\n') endOfFm++;
                body = trimmed.Substring(endOfFm);
            }
        }

        // 只写自己的 sappfiler-* 命名空间。用户手填的 genre / rating / status / aliases 等
        // 由 MergeFrontmatter 按整行原样保留 —— 原来的实现把 frontmatter 解析成
        // Dictionary<string,string>，YAML 列表（`aliases:` + `- Foo`）会被压成空值。
        var sappfilerProps = new List<string>
        {
            $"sappfiler-game-id: {game.Id}",
            $"sappfiler-platform: {game.Platform}"
        };
        if (!string.IsNullOrWhiteSpace(game.PlatformId))
        {
            sappfilerProps.Add($"sappfiler-platform-id: \"{game.PlatformId}\"");
        }
        sappfilerProps.Add($"sappfiler-total-playtime: {stats.FormattedTotalDuration}");
        sappfilerProps.Add($"sappfiler-total-hours: {stats.TotalHours.ToString("0.#")}");
        sappfilerProps.Add($"sappfiler-last-played: {stats.LastPlayedDate ?? ""}");
        sappfilerProps.Add($"sappfiler-total-sessions: {stats.TotalSessions}");

        var generatedFrontmatter = "---\n" + string.Join("\n", sappfilerProps) + "\n---";

        var sbSummary = new StringBuilder();
        sbSummary.AppendLine("<!-- ⚠️ 以下内容由 Sappfiler 自动生成，手动修改将在下次同步时被覆盖 -->");
        sbSummary.AppendLine();
        sbSummary.AppendLine($"# {game.Name}");
        sbSummary.AppendLine();
        sbSummary.AppendLine($"- **平台**：{game.Platform}");
        sbSummary.AppendLine($"- **总时长**：{stats.FormattedTotalDuration}");
        sbSummary.AppendLine($"- **最后游玩**：{(string.IsNullOrWhiteSpace(stats.LastPlayedDate) ? "未记录" : stats.LastPlayedDate)}");
        sbSummary.AppendLine($"- **游玩次数**：{stats.TotalSessions}");
        if (!string.IsNullOrWhiteSpace(game.CoverUrl))
        {
            sbSummary.AppendLine();
            sbSummary.AppendLine($"![cover]({game.CoverUrl})");
        }
        sbSummary.AppendLine();
        sbSummary.Append("<!-- Sappfiler End -->");

        var generatedSection = sbSummary.ToString();

        var startIdx = body.IndexOf(startMarker, StringComparison.Ordinal);
        var endIdx = body.IndexOf(endMarker, StringComparison.Ordinal);

        string mergedBody;
        if (startIdx >= 0 && endIdx >= 0 && endIdx >= startIdx)
        {
            var before = body.Substring(0, startIdx);
            var after = body.Substring(endIdx + endMarker.Length);
            mergedBody = before + generatedSection + after;
        }
        else
        {
            mergedBody = string.IsNullOrWhiteSpace(body)
                ? generatedSection
                : generatedSection + "\n\n" + body.TrimStart();
        }

        var mergedFrontmatter = MergeFrontmatter(
            existingFmText,
            generatedFrontmatter,
            key => key.StartsWith("sappfiler-", StringComparison.OrdinalIgnoreCase));

        return mergedFrontmatter + "\n\n" + mergedBody.Trim() + "\n";
    }

    public async Task<IReadOnlyList<RemoteGameCandidate>> GetRemoteCatalogAsync(CancellationToken cancellationToken = default)
    {
        var config = await GetCurrentConfigAsync();
        var candidates = new List<RemoteGameCandidate>();

        if (string.Equals(config.SyncMode, "file", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config.VaultPath) || !Directory.Exists(config.VaultPath))
            {
                return candidates;
            }

            var targetFolder = Path.Combine(config.VaultPath, config.GamesSubfolder);
            if (!Directory.Exists(targetFolder))
            {
                targetFolder = config.VaultPath;
            }

            try
            {
                var files = Directory.GetFiles(targetFolder, "*.md", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    try
                    {
                        var relativePath = Path.GetRelativePath(config.VaultPath, file).Replace('\\', '/');
                        // 忽略每日记录目录下的笔记，防止将日记误当成游戏主笔记
                        if (!string.IsNullOrWhiteSpace(config.DailySubfolder) &&
                            relativePath.StartsWith(config.DailySubfolder.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var content = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken);
                        candidates.Add(ParseObsidianCandidate(relativePath, content));
                    }
                    catch { }
                }
            }
            catch { }
        }

        return candidates;
    }

    public static RemoteGameCandidate ParseObsidianCandidate(string relativePath, string content)
    {
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(relativePath);
        var title = fileNameWithoutExt;
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        aliases.Add(fileNameWithoutExt);

        if (content.TrimStart().StartsWith("---"))
        {
            var trimmed = content.TrimStart();
            var secondDash = trimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (secondDash >= 0)
            {
                var fmText = trimmed.Substring(3, secondDash - 3);
                var lines = fmText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                bool inAliasesBlock = false;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                    if (trimmedLine.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                    {
                        inAliasesBlock = false;
                        var val = trimmedLine.Substring("title:".Length).Trim().Trim('"', '\'');
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            title = val;
                            aliases.Add(val);
                        }
                    }
                    else if (trimmedLine.StartsWith("aliases:", StringComparison.OrdinalIgnoreCase) ||
                             trimmedLine.StartsWith("alias:", StringComparison.OrdinalIgnoreCase))
                    {
                        var colonIdx = trimmedLine.IndexOf(':');
                        var val = trimmedLine.Substring(colonIdx + 1).Trim();
                        if (string.IsNullOrWhiteSpace(val))
                        {
                            inAliasesBlock = true;
                        }
                        else
                        {
                            inAliasesBlock = false;
                            val = val.Trim('[', ']');
                            var parts = val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var p in parts)
                            {
                                var clean = p.Trim().Trim('"', '\'');
                                if (!string.IsNullOrWhiteSpace(clean)) aliases.Add(clean);
                            }
                        }
                    }
                    else if (inAliasesBlock && trimmedLine.StartsWith("-"))
                    {
                        var val = trimmedLine.Substring(1).Trim().Trim('"', '\'');
                        if (!string.IsNullOrWhiteSpace(val)) aliases.Add(val);
                    }
                    else if (!trimmedLine.StartsWith("-"))
                    {
                        inAliasesBlock = false;
                    }
                }
            }
        }

        return new RemoteGameCandidate
        {
            Provider = "obsidian",
            RemoteId = relativePath.Replace('\\', '/'),
            RemoteName = title,
            Aliases = aliases.ToList(),
            Location = Path.GetDirectoryName(relativePath)?.Replace('\\', '/')
        };
    }

    public async Task<bool> TestConnectionAsync()
    {
        var config = await GetCurrentConfigAsync();
        if (string.Equals(config.SyncMode, "api", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var endpoint = $"https://127.0.0.1:{config.ApiPort}";
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey ?? "");
                using var resp = await _httpClient.SendAsync(req);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(config.VaultPath) && Directory.Exists(config.VaultPath);
    }

    public IReadOnlyList<ProviderConfigField> GetConfigFields()
    {
        return new List<ProviderConfigField>
        {
            new() { Key = "VaultPath", Label = "Vault 路径", Description = "Obsidian 笔记库的本地根目录", IsRequired = true },
            new() { Key = "SyncMode", Label = "同步模式", Description = "file (文件模式) 或 api (Local REST API 插件模式)", DefaultValue = "file" },
            new() { Key = "DailySubfolder", Label = "日报子目录", Description = "日报 Markdown 保存子路径", DefaultValue = "Sappfiler/Daily" },
            new() { Key = "GamesSubfolder", Label = "游戏子目录", Description = "游戏单独笔记保存子路径", DefaultValue = "Sappfiler/Games" },
            new() { Key = "ApiPort", Label = "API 端口", Description = "Local REST API 插件端口 (默认 27124)", DefaultValue = "27124" },
            new() { Key = "ApiKey", Label = "API Key", Description = "Local REST API 插件密钥", IsSecret = true }
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString().Trim();
    }
}
