using System.Threading;
using System.Threading.Tasks;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.Artwork;

public sealed class DefaultArtworkProvider : IGameArtworkProvider
{
    public string ProviderName => "Default";
    public int Priority => 999;

    public bool CanHandle(GameIdentity game) => true;

    public Task<GameArtwork?> TryGetArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<GameArtwork?>(null);
    }
}
