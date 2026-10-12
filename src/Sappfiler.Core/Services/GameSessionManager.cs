using GameTimeTracker.Core.DeviceSync;
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

    /// <summary>
    /// <b>结束指定游戏的全部活跃会话</b>（2026-10-12：删除游戏前调用）。
    ///
    /// <para>
    /// 目的：让"删除游戏 → 以后重新启动游戏"**必然新建 Session**，而不是复用删除前那条
    /// —— 复用会让新计时接在旧起点上，违反「本次新的计时必须从新 Session 的起点开始」。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 只动这**一个** game 的会话，其它游戏的活跃会话分毫不动。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 走的是既有完整结束路径（<see cref="EndSessionAsync"/>：落尾段 daily → 跨日切分 →
    /// 重算场次 → 同事务产出同步事实），**绝不**直接改数据库字段伪造会话结束。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 只对**内存里真实跟踪中**的会话生效；内存里没有就不动作（残留的库内活跃行由启动时的
    /// 僵尸清理负责，这里不越权）。
    /// </para>
    /// </summary>
    /// <returns>被摘除关联的 PID 数量（供调用方记日志）。</returns>
    public async Task<int> EndSessionsForGameAsync(int gameId)
    {
        List<int> pids;
        await _lock.WaitAsync();
        try
        {
            if (!_gameSessions.TryGetValue(gameId, out var state) || state.ActivePids.Count == 0)
            {
                return 0;
            }

            pids = state.ActivePids.ToList();
        }
        finally
        {
            _lock.Release();
        }

        var ended = 0;
        foreach (var pid in pids)
        {
            // 复用既有语义：该游戏还有别的 PID 在跑时 EndSessionAsync 会提前返回，
            // 全部 PID 都摘掉之后才真正结束会话（"最后一个 PID 消失才结束"）。
            await EndSessionAsync(pid);
            ended++;
        }

        return ended;
    }

    /// <summary>该 PID 当前被跟踪到的 GameId；未被跟踪返回 null。</summary>
    public int? GetTrackedGameId(int pid)
    {
        lock (_pidToGameId)
        {
            return _pidToGameId.TryGetValue(pid, out var gameId) ? gameId : null;
        }
    }

    /// <summary>
    /// 心跳（带**归属校验**）：只相信"本轮扫描得到的 GameId"，不再无条件相信 <c>_pidToGameId[pid]</c> 的旧映射。
    ///
    /// ⚠️ Windows 会**复用 PID**：某个游戏退出后，同一个 PID 可能被分配给另一个游戏。
    /// 旧映射一旦残留，就会把**新 Game 的时间记到旧 Game**头上。因此：
    /// <code>
    /// 扫描得到的 GameId == 已跟踪的 GameId  → 正常心跳（与 HeartbeatSessionAsync 行为完全一致）
    /// 扫描得到的 GameId != 已跟踪的 GameId  → 重归属：先摘掉旧关联，再按新 Game 建/并入会话
    /// </code>
    ///
    /// 重归属复用既有公开语义（各自加锁）：<see cref="EndSessionAsync"/> 在旧 Game 还有其它 PID 时
    /// 会提前返回 —— 旧 Session 照旧活跃，"最后一个 PID 消失才结束"的分支一字未改。
    ///
    /// ⚠️ 两次调用之间不做补偿、也不引入第二套去重状态：生产环境只有 5 秒扫描循环调用这些方法，
    /// 且逐个 <c>await</c> 串行，不存在并发插入。
    /// </summary>
    /// <returns>true = 发生了重归属（供调用方记日志）。</returns>
    public async Task<bool> HeartbeatOrReassignAsync(
        int pid, GameRecord game, DetectedProcess process, DateTime? nowTime = null)
    {
        var trackedGameId = GetTrackedGameId(pid);

        if (trackedGameId is null)
        {
            // 理论上调用方只在"已跟踪"时进来；真遇到未跟踪就按新 Game 正常建立关联。
            await StartSessionAsync(game, process, nowTime);
            return false;
        }

        if (trackedGameId.Value == game.Id)
        {
            await HeartbeatSessionAsync(pid, nowTime);
            return false;
        }

        await EndSessionAsync(pid, nowTime);
        await StartSessionAsync(game, process, nowTime);
        return true;
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

            // 先算"尾段增量该落进哪些业务日期"（纯函数，跨结算点会拆两笔），
            // 再交给**一个**原子方法完成：daily_summary + sessions + sync_queue 同事务提交。
            // ⚠️ 不能像以前那样"先写 daily、再结束会话"分两次写库 —— 中间崩溃会留下
            //    "会话已结束、daily 少算尾段增量"的不一致（Phase 2d 冻结的事务边界）。
            var dailyDeltas = ComputeDailyDeltas(lastFlush, end, deltaSeconds, DailyCutoffHour);

            // 🔒 2026-10-11（实机事故修复）：**先落库、确认成功后才改内存态**。
            //    此前无论写库成败都会 `_gameSessions.Remove(gameId)`：一旦那次原子写入整体回滚
            //    （同步轮次长时间持有写锁 / SQLITE_BUSY / 任何 IO 失败），内存里会话没了、
            //    数据库里该会话仍是 is_active = 1 —— 实机表现就是"正在玩的游戏不再被记录，重启才恢复"，
            //    而且下一拍还会为同一个进程另起一条会话（同一游戏两条 active 行）。
            //    写库失败时保持跟踪并返回：下一拍扫描会自然重试，秒数不会丢
            //    （LastDailyFlushTime 未推进，尾段增量由 lastFlush → now 重新计算）。
            var write = await _repo.EndSessionWithOutboxAsync(session.Id, end, totalDuration, dailyDeltas);

            if (!write.SessionEnded)
            {
                // 整体回滚已由仓储完成（失败详情已在仓储侧按 ERROR + 堆栈记录，可诊断）。
                state.ActivePids.Add(pid);
                _pidToGameId[pid] = gameId;
                return;
            }

            session.EndTime = end;
            session.DurationSeconds = totalDuration;
            session.IsActive = false;

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
    ///
    /// ⚠️ 这是**心跳路径**：行为与 Phase 2d 之前**逐字一致**（只是把"算日期"抽成了纯函数，
    /// SQLite 写入完全照旧）。正在运行的会话**只**落本地 daily_summary，**不进 outbox** ——
    /// 绝不能变成每 5 秒往云端同步一次。
    /// </summary>
    private async Task FlushDurationToDailyAsync(DateTime lastFlush, DateTime current, int gameId, int deltaSeconds)
    {
        foreach (var delta in ComputeDailyDeltas(lastFlush, current, deltaSeconds, DailyCutoffHour))
        {
            await _repo.AddSessionDurationToDailyAsync(delta.Date, gameId, delta.DurationSeconds);
        }
    }

    /// <summary>
    /// 计算一次增量应写入的「业务归属日期 + 秒数」——**纯函数，不碰数据库**。
    ///
    /// 抽它的目的：结束会话时要把这几笔写入搬进"结束会话 + 写 Outbox"的同一个事务里，
    /// 而心跳路径必须保持原样。两条路径共用**同一份**日期/拆分计算，
    /// 避免两边算出不同的归属日期（那是很难查的数据错位）。
    /// </summary>
    public static IReadOnlyList<DailyDurationDelta> ComputeDailyDeltas(
        DateTime lastFlush, DateTime current, int deltaSeconds, int cutoffHour)
    {
        if (deltaSeconds <= 0) return Array.Empty<DailyDurationDelta>();

        var date1 = AccountingDateHelper.GetAccountingDate(lastFlush, cutoffHour);
        var date2 = AccountingDateHelper.GetAccountingDate(current, cutoffHour);

        if (date1 == date2)
        {
            // 未跨越结算点，归入同一业务日期
            return new[] { new DailyDurationDelta(date1.ToString("yyyy-MM-dd"), deltaSeconds) };
        }

        // 跨越了跨日结算点！精确分割分界点前后的秒数
        var boundary = AccountingDateHelper.GetNextCutoffBoundary(lastFlush, cutoffHour);
        var prevDelta = (int)Math.Max(0, (boundary - lastFlush).TotalSeconds);
        var nextDelta = (int)Math.Max(0, (current - boundary).TotalSeconds);

        var deltas = new List<DailyDurationDelta>(2);
        if (prevDelta > 0) deltas.Add(new DailyDurationDelta(date1.ToString("yyyy-MM-dd"), prevDelta));
        if (nextDelta > 0) deltas.Add(new DailyDurationDelta(date2.ToString("yyyy-MM-dd"), nextDelta));

        // 边界恰好落在窗口端点（两侧都为 0）⇒ 整段归入起始日，绝不丢秒。
        if (deltas.Count == 0)
        {
            return new[] { new DailyDurationDelta(date1.ToString("yyyy-MM-dd"), deltaSeconds) };
        }

        // ---- 收口（2026-10-09 冻结）----
        // 分片之和必须**严格等于** canonical 秒数 `deltaSeconds`。
        // 上面按"墙上时间"切分，当调用方传入的秒数与墙上跨度不一致时
        // （远端落地用 canonical duration、心跳与结束路径有亚秒截断）会差几秒 ——
        // 差额从最后一个分片起向前吸收：绝不留丢失，也绝不改变时间归属日。
        var sum = 0;
        foreach (var d in deltas) sum += d.DurationSeconds;

        var diff = deltaSeconds - sum;
        for (var i = deltas.Count - 1; i >= 0 && diff != 0; i--)
        {
            var adjusted = deltas[i].DurationSeconds + diff;

            if (adjusted < 0)
            {
                diff = adjusted;                                              // 这一片不够扣，继续往前
                deltas[i] = new DailyDurationDelta(deltas[i].Date, 0);
            }
            else
            {
                deltas[i] = new DailyDurationDelta(deltas[i].Date, adjusted);
                diff = 0;
            }
        }

        // 理论上不可能（把所有片扣成 0 之后仍有余额）：兜底归入起始日，绝不静默丢秒。
        if (diff != 0)
        {
            deltas[0] = new DailyDurationDelta(deltas[0].Date, deltas[0].DurationSeconds + diff);
        }

        return deltas;
    }
}

