using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Core.Services;

namespace GameTimeTracker.Infrastructure.Artwork;

[SupportedOSPlatform("windows")]
public sealed class NotionArtworkProvider : IGameArtworkProvider
{
    public string ProviderName => "Notion";
    public int Priority => 20;

    private readonly IDatabaseRepository _repo;
    private readonly IGameMatcher _matcher;

    public NotionArtworkProvider(IDatabaseRepository repo, IGameMatcher? matcher = null)
    {
        _repo = repo;
        _matcher = matcher ?? new GameMatcher();
    }

    public bool CanHandle(GameIdentity game) => true;

    public async Task<GameArtwork?> TryGetArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default)
    {
        // 1. 已绑定 Notion 页面
        if (!string.IsNullOrEmpty(game.NotionPageId))
        {
            var catItem = await _repo.GetCatalogItemByPageIdAsync(game.NotionPageId);
            if (catItem != null && !string.IsNullOrWhiteSpace(catItem.CoverUrl))
            {
                return new GameArtwork
                {
                    FilePathOrUrl = catItem.CoverUrl,
                    IsLocalFile = false,
                    Source = ArtworkSource.Notion,
                    Type = ArtworkType.NotionCover
                };
            }
        }

        // 2. 未显式绑定时，尝试根据库内匹配链查找（例如中英同名映射）
        try
        {
            var catalogItems = await _repo.GetCatalogItemsAsync();
            if (catalogItems.Count > 0)
            {
                var hit = _matcher.MatchGame(game.Name, catalogItems, game.PlatformId).FirstOrDefault();
                if (hit != null)
                {
                    var matched = catalogItems.FirstOrDefault(c => c.PageId == hit.PageId);
                    if (matched != null && !string.IsNullOrWhiteSpace(matched.CoverUrl))
                    {
                        return new GameArtwork
                        {
                            FilePathOrUrl = matched.CoverUrl,
                            IsLocalFile = false,
                            Source = ArtworkSource.Notion,
                            Type = ArtworkType.NotionCover
                        };
                    }
                }
            }
        }
        catch { }

        return null;
    }
}
