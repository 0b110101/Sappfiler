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

    /// <summary>
    /// 跨日结算时间点（整点 24 ~ 30 点，默认 24 = 00:00）。
    /// </summary>
    public int DailyCutoffHour { get; set; } = 24;

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
                await FlushDurationToDailyAsync(lastFlush, now, session.GameId, deltaSeconds);

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
                await FlushDurationToDailyAsync(lastFlush, end, session.GameId, deltaSeconds);
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

    /// <summary>
    /// 将本次增量时间刷新至对应的每日汇总表中。
    /// 遵循 DailyCutoffHour 跨日结算点设定（24~30点）：
    /// 若跨越结算点，则精确将前一段与后一段的秒数分割写入对应的业务归属日期。
    /// </summary>
    private async Task FlushDurationToDailyAsync(DateTime lastFlush, DateTime current, int gameId, int deltaSeconds)
    {
        if (deltaSeconds <= 0) return;

        var date1 = AccountingDateHelper.GetAccountingDate(lastFlush, DailyCutoffHour);
        var date2 = AccountingDateHelper.GetAccountingDate(current, DailyCutoffHour);

        if (date1 == date2)
        {
            // 未跨越结算点，归入同一业务日期
            await _repo.AddSessionDurationToDailyAsync(date1.ToString("yyyy-MM-dd"), gameId, deltaSeconds);
        }
        else
        {
            // 跨越了跨日结算点！精确分割分界点前后的秒数
            var boundary = AccountingDateHelper.GetNextCutoffBoundary(lastFlush, DailyCutoffHour);
            var prevDelta = (int)Math.Max(0, (boundary - lastFlush).TotalSeconds);
            var nextDelta = (int)Math.Max(0, (current - boundary).TotalSeconds);

            if (prevDelta > 0)
            {
                await _repo.AddSessionDurationToDailyAsync(date1.ToString("yyyy-MM-dd"), gameId, prevDelta);
            }
            if (nextDelta > 0)
            {
                await _repo.AddSessionDurationToDailyAsync(date2.ToString("yyyy-MM-dd"), gameId, nextDelta);
            }
        }
    }
}

