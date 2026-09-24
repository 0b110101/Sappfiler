using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Core.Services;

public class GameSessionManager
{
    private readonly IDatabaseRepository _repo;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private class GameSessionState
    {
        public GameSession Session { get; set; } = null!;
        public DateTime LastDailyFlushTime { get; set; }
        public HashSet<int> ActivePids { get; } = new();
    }

    private readonly Dictionary<int, GameSessionState> _gameSessions = new(); // Key: GameId
    private readonly Dictionary<int, int> _pidToGameId = new(); // Key: PID -> GameId

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
                if (!_gameSessions.TryGetValue(session.GameId, out var state))
                {
                    state = new GameSessionState
                    {
                        Session = session,
                        LastDailyFlushTime = session.LastHeartbeat
                    };
                    _gameSessions[session.GameId] = state;
                }
                state.ActivePids.Add(session.Pid);
                _pidToGameId[session.Pid] = session.GameId;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyList<GameSession> GetActiveSessions()
    {
        lock (_gameSessions)
        {
            return _gameSessions.Values.Select(v => v.Session).ToList();
        }
    }

    public IReadOnlyCollection<int> GetTrackedPids()
    {
        lock (_pidToGameId)
        {
            return _pidToGameId.Keys.ToList();
        }
    }

    public bool IsPidActive(int pid)
    {
        lock (_pidToGameId)
        {
            return _pidToGameId.ContainsKey(pid);
        }
    }

    public bool IsGameActive(int gameId)
    {
        lock (_gameSessions)
        {
            return _gameSessions.ContainsKey(gameId);
        }
    }

    public async Task<GameSession> StartSessionAsync(GameRecord game, DetectedProcess process, DateTime? startTime = null)
    {
        await _lock.WaitAsync();
        try
        {
            if (_gameSessions.TryGetValue(game.Id, out var existingState))
            {
                // 该游戏已有活跃会话！将该子进程 PID 关联加入同一会话，不再重复建立独立会话，
                // 彻底杜绝多进程（如《蝴蝶收藏家》4个子进程）导致的 N 倍时长暴增问题
                existingState.ActivePids.Add(process.Pid);
                _pidToGameId[process.Pid] = game.Id;
                return existingState.Session;
            }

            var start = startTime ?? DateTime.Now;
            var session = await _repo.CreateSessionAsync(game.Id, process.Pid, process.ProcessName, start);
            var state = new GameSessionState
            {
                Session = session,
                LastDailyFlushTime = start
            };
            state.ActivePids.Add(process.Pid);

            _gameSessions[game.Id] = state;
            _pidToGameId[process.Pid] = game.Id;

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
            if (!_pidToGameId.TryGetValue(pid, out var gameId) || !_gameSessions.TryGetValue(gameId, out var state))
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
                state.LastDailyFlushTime = now;

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
            if (!_pidToGameId.TryGetValue(pid, out var gameId) || !_gameSessions.TryGetValue(gameId, out var state))
            {
                return;
            }

            state.ActivePids.Remove(pid);
            _pidToGameId.Remove(pid);

            // ⚠️ 只要该游戏还有其他进程在运行（例如主进程仍在、只是启动器/子进程退出），就不要终止会话！
            if (state.ActivePids.Count > 0)
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
            _gameSessions.Remove(gameId);

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
        lock (_pidToGameId)
        {
            foreach (var pid in _pidToGameId.Keys)
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    if (proc.HasExited)
                    {
                        zombiePids.Add(pid);
                    }
                }
                catch (ArgumentException)
                {
                    // Process does not exist
                    zombiePids.Add(pid);
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

