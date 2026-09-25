namespace GameTimeTracker.Core.Interfaces;

using GameTimeTracker.Core.Models;

public interface IGameArtworkProvider
{
    string ProviderName { get; }
    int Priority { get; }
    bool CanHandle(GameIdentity game);
    Task<GameArtwork?> TryGetArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default);
}

public interface IGameArtworkService
{
    Task<GameArtwork?> ResolveArtworkAsync(GameIdentity game, CancellationToken cancellationToken = default);
    Task CleanExpiredCacheAsync(long maxSizeBytes = 300 * 1024 * 1024, CancellationToken cancellationToken = default);
}
