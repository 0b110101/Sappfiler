using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FuzzySharp;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.Services;

public class GameMatcher : IGameMatcher
{
    private static readonly (Regex Pattern, string Replacement)[] RomanNumeralPatterns = new[]
    {
        (new Regex(@"\bVIII\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "8"),
        (new Regex(@"\bVII\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "7"),
        (new Regex(@"\bVI\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "6"),
        (new Regex(@"\bIV\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "4"),
        (new Regex(@"\bV\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "5"),
        (new Regex(@"\bIX\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "9"),
        (new Regex(@"\bX\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "10"),
        (new Regex(@"\bIII\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "3"),
        (new Regex(@"\bII\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "2"),
        (new Regex(@"\bI\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "1"),
    };

    private static readonly Regex ApostropheRegex = new(@"['’`]", RegexOptions.Compiled);
    private static readonly Regex PunctuationRegex = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SteamCoverAppIdRegex = new(@"/apps/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 标题末尾**括号包裹**的年份，例如「Valheim (2020)」「Doom (2016)」「Prey [2017]」。
    /// </summary>
    /// <remarks>
    /// 为什么要剥掉：总表的「别名」常写成带年份的形式，而进程名/游戏名不带。
    /// 不剥的话归一化后是 `valheim 2020` 而不是 `valheim`，
    /// 精确匹配失败，模糊相似度也只有约 74% —— 卡在 80% 阈值之下，
    /// 于是候选列表为空、被判定成"新游戏"。2026-09-18 QA 实测就是这个原因。
    ///
    /// **只剥括号形式**（`(2020)` / `[2020]`），不剥裸年份 ——
    /// 「Football Manager 2024」「F1 2023」这类游戏名里年份是有意义的一部分，
    /// 剥掉会让 2023 和 2024 两代互相误匹配。
    /// </remarks>
    private static readonly Regex TrailingBracketedYearRegex =
        new(@"\s*[\(\[]\s*(?:19|20)\d{2}\s*[\)\]]\s*$", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string?> _steamCnCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly double _fuzzyThreshold;
    private readonly double _scoreGapThreshold;

    public GameMatcher(HttpClient? httpClient = null, double fuzzyThreshold = 80.0, double scoreGapThreshold = 15.0)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _fuzzyThreshold = fuzzyThreshold;
        _scoreGapThreshold = scoreGapThreshold;
    }

    /// <summary>
    /// 从封面图 URL 中提取 Steam AppID，例如
    /// "https://.../steam/apps/1347970/library_hero.jpg" -> "1347970"。
    /// 总表若未填写「游戏标识」属性，这是唯一可靠的跨语言匹配信号。
    /// </summary>
    public static string? ExtractSteamAppId(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return null;
        var match = SteamCoverAppIdRegex.Match(coverUrl);
        return match.Success ? match.Groups[1].Value : null;
    }

    public string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        // NFKC normalization & lowercase
        var normalized = title.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

        // 剥掉末尾括号里的年份（Valheim (2020) → Valheim）。
        // 必须在去标点之前做 —— 去标点会把括号换成空格，那时就认不出括号形式了。
        normalized = TrailingBracketedYearRegex.Replace(normalized, "");

        // Strip apostrophes
        normalized = ApostropheRegex.Replace(normalized, "");

        // Replace Roman numerals
        foreach (var (pattern, replacement) in RomanNumeralPatterns)
        {
            normalized = pattern.Replace(normalized, replacement);
        }

        // Remove punctuation and special symbols (keeps unicode letters/digits for CJK)
        normalized = PunctuationRegex.Replace(normalized, " ");

        // Collapse multi-spaces
        normalized = MultiSpaceRegex.Replace(normalized, " ").Trim();

        return normalized;
    }

    public async Task<string?> ResolveSteamChineseTitleAsync(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsDigit))
        {
            return null;
        }

        lock (_steamCnCache)
        {
            if (_steamCnCache.TryGetValue(appId, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese";
            var response = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty(appId, out var appData) &&
                appData.TryGetProperty("success", out var success) && success.GetBoolean() &&
                appData.TryGetProperty("data", out var data) &&
                data.TryGetProperty("name", out var nameProp))
            {
                var cnName = nameProp.GetString();
                lock (_steamCnCache)
                {
                    _steamCnCache[appId] = cnName;
                }
                return cnName;
            }
        }
        catch
        {
            // Network fallback / offline tolerance
        }

        lock (_steamCnCache)
        {
            _steamCnCache[appId] = null;
        }
        return null;
    }

    public IReadOnlyList<GameCandidate> MatchGame(
        string gameTitle,
        IReadOnlyList<NotionGameCatalogItem> catalog,
        string? steamAppId = null)
    {
        var results = new List<GameCandidate>();
        if (catalog == null || catalog.Count == 0 || string.IsNullOrWhiteSpace(gameTitle))
        {
            return results;
        }

        var cleanQuery = gameTitle.Trim();
        var normQuery = NormalizeTitle(cleanQuery);

        // 1. Identifier match (e.g. steam:1086940)
        if (!string.IsNullOrWhiteSpace(steamAppId))
        {
            var appId = steamAppId.Trim();
            var steamKey = $"steam:{appId}";

            // 1a. 显式标识：常见于总表已填写「游戏标识」属性的条目
            foreach (var item in catalog)
            {
                if (item.Identifiers.Any(id => string.Equals(id.Trim(), steamKey, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(id.Trim(), appId, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(new GameCandidate
                    {
                        PageId = item.PageId,
                        Title = item.Name,
                        MatchType = "identifier_match",
                        Score = 100.0
                    });
                    return results;
                }
            }

            // 1b. 封面图 URL 里内嵌的 Steam AppID。
            //     多数总表条目并未填写「游戏标识」，但封面常来自 Steam CDN
            //     （.../steam/apps/1347970/...），这是唯一可靠的跨语言匹配信号。
            foreach (var item in catalog)
            {
                var coverAppId = ExtractSteamAppId(item.CoverUrl);
                if (!string.IsNullOrEmpty(coverAppId) &&
                    string.Equals(coverAppId, appId, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new GameCandidate
                    {
                        PageId = item.PageId,
                        Title = item.Name,
                        MatchType = "identifier_match",
                        Score = 100.0
                    });
                    return results;
                }
            }
        }

        // 2. Exact match on title or aliases
        foreach (var item in catalog)
        {
            if (string.Equals(item.Name.Trim(), cleanQuery, StringComparison.OrdinalIgnoreCase) ||
                item.Aliases.Any(a => string.Equals(a.Trim(), cleanQuery, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new GameCandidate
                {
                    PageId = item.PageId,
                    Title = item.Name,
                    MatchType = "exact",
                    Score = 100.0
                });
                return results;
            }
        }

        // 3. Normalized match
        foreach (var item in catalog)
        {
            if (NormalizeTitle(item.Name) == normQuery ||
                item.Aliases.Any(a => NormalizeTitle(a) == normQuery))
            {
                results.Add(new GameCandidate
                {
                    PageId = item.PageId,
                    Title = item.Name,
                    MatchType = "normalized",
                    Score = 98.0
                });
                return results;
            }
        }

        // 4. Fuzzy candidate scoring
        var candidates = new List<GameCandidate>();
        foreach (var item in catalog)
        {
            var bestScore = 0.0;
            var itemNorm = NormalizeTitle(item.Name);
            var ratio = Fuzz.TokenSortRatio(normQuery, itemNorm);
            if (ratio > bestScore) bestScore = ratio;

            foreach (var alias in item.Aliases)
            {
                var aliasNorm = NormalizeTitle(alias);
                var aRatio = Fuzz.TokenSortRatio(normQuery, aliasNorm);
                if (aRatio > bestScore) bestScore = aRatio;
            }

            if (bestScore >= _fuzzyThreshold)
            {
                candidates.Add(new GameCandidate
                {
                    PageId = item.PageId,
                    Title = item.Name,
                    MatchType = "fuzzy_candidate",
                    Score = bestScore
                });
            }
        }

        return candidates.OrderByDescending(c => c.Score).ToList();
    }
}
