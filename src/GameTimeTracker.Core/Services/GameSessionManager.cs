using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.Services;

public class GameSessionManager
{
    private readonly IDatabaseRepository _repo;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<int, (GameSession Session, DateTime LastDailyFlushTime)> _activeSessions = new();

    public event EventHandler<GameSession>? SessionStarted;
    public event EventHandler<GameSession>? SessionEnded;
    public event EventHandler<GameSession>? SessionHeartbeat;

    public GameSessionManager(IDatabaseRepository repo)
    {
        _repo = repo;
    }

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Recover any stale sessions left over from unexpected process shutdown
            await _repo.CleanupStaleSessionsAsync(TimeSpan.FromMinutes(3));

            // Load existing active sessions if any
            var existing = await _repo.GetActiveSessionsAsync();
            foreach (var session in existing)
            {
                _activeSessions[session.Pid] = (session, session.LastHeartbeat);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyList<GameSession> GetActiveSessions()
    {
        lock (_activeSessions)
        {
            return _activeSessions.Values.Select(v => v.Session).ToList();
        }
    }

    public async Task<GameSession> StartSessionAsync(GameRecord game, DetectedProcess process, DateTime? startTime = null)
    {
        await _lock.WaitAsync();
        try
        {
            if (_activeSessions.TryGetValue(process.Pid, out var existing))
            {
                return existing.Session;
            }

            var start = startTime ?? DateTime.Now;
            var session = await _repo.CreateSessionAsync(game.Id, process.Pid, process.ProcessName, start);
            _activeSessions[process.Pid] = (session, start);

            SessionStarted?.Invoke(this, session);
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task HeartbeatSessionAsync(int pid, DateTime? nowTime = null)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_activeSessions.TryGetValue(pid, out var state))
            {
                return;
            }

            var now = nowTime ?? DateTime.Now;
            var session = state.Session;
            var lastFlush = state.LastDailyFlushTime;

            var totalDuration = (int)Math.Max(0, (now - session.StartTime).TotalSeconds);
            var deltaSeconds = (int)Math.Max(0, (now - lastFlush).TotalSeconds);

            if (deltaSeconds > 0)
            {
                // Check if midnight was crossed between lastFlush and now
                if (lastFlush.Date == now.Date)
                {
                    // Same day
                    await _repo.AddSessionDurationToDailyAsync(now.ToString("yyyy-MM-dd"), session.GameId, deltaSeconds);
                }
                else
                {
                    // Crossed midnight! Split into yesterday and today
                    var midnight = now.Date; // 00:00:00 of today
                    var yesterdayDelta = (int)Math.Max(0, (midnight - lastFlush).TotalSeconds);
                    var todayDelta = (int)Math.Max(0, (now - midnight).TotalSeconds);

                    if (yesterdayDelta > 0)
                    {
                        await _repo.AddSessionDurationToDailyAsync(lastFlush.ToString("yyyy-MM-dd"), session.GameId, yesterdayDelta);
                    }
                    if (todayDelta > 0)
                    {
                        await _repo.AddSessionDurationToDailyAsync(now.ToString("yyyy-MM-dd"), session.GameId, todayDelta);
                    }
                }

                session.DurationSeconds = totalDuration;
                session.LastHeartbeat = now;
                _activeSessions[pid] = (session, now);

                await _repo.UpdateSessionHeartbeatAsync(session.Id, now, totalDuration);
                SessionHeartbeat?.Invoke(this, session);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task EndSessionAsync(int pid, DateTime? endTime = null)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_activeSessions.TryGetValue(pid, out var state))
            {
                return;
            }

            var end = endTime ?? DateTime.Now;
            var session = state.Session;
            var lastFlush = state.LastDailyFlushTime;

            var totalDuration = (int)Math.Max(0, (end - session.StartTime).TotalSeconds);
            var deltaSeconds = (int)Math.Max(0, (end - lastFlush).TotalSeconds);

            if (deltaSeconds > 0)
            {
                if (lastFlush.Date == end.Date)
                {
                    await _repo.AddSessionDurationToDailyAsync(end.ToString("yyyy-MM-dd"), session.GameId, deltaSeconds);
                }
                else
                {
                    var midnight = end.Date;
                    var yesterdayDelta = (int)Math.Max(0, (midnight - lastFlush).TotalSeconds);
                    var todayDelta = (int)Math.Max(0, (end - midnight).TotalSeconds);

                    if (yesterdayDelta > 0)
                    {
                        await _repo.AddSessionDurationToDailyAsync(lastFlush.ToString("yyyy-MM-dd"), session.GameId, yesterdayDelta);
                    }
                    if (todayDelta > 0)
                    {
                        await _repo.AddSessionDurationToDailyAsync(end.ToString("yyyy-MM-dd"), session.GameId, todayDelta);
                    }
                }
            }

            session.EndTime = end;
            session.DurationSeconds = totalDuration;
            session.IsActive = false;

            await _repo.EndSessionAsync(session.Id, end, totalDuration);
            _activeSessions.Remove(pid);

            SessionEnded?.Invoke(this, session);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CleanupZombieSessionsAsync()
    {
        List<int> zombiePids = new();
        lock (_activeSessions)
        {
            foreach (var kvp in _activeSessions)
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(kvp.Key);
                    if (proc.HasExited)
                    {
                        zombiePids.Add(kvp.Key);
                    }
                }
                catch (ArgumentException)
                {
                    // Process does not exist
                    zombiePids.Add(kvp.Key);
                }
                catch
                {
                    // Other access exceptions
                }
            }
        }

        foreach (var pid in zombiePids)
        {
            await EndSessionAsync(pid);
        }
    }
}
