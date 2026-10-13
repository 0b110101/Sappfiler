using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;
using GameTimeTracker.Infrastructure.Database;
using GameTimeTracker.Infrastructure.WebDav;

namespace GameTimeTracker.Infrastructure.DeviceSync;

/// <summary>连接测试结果（UI 直接用）。</summary>
public sealed record WebDavConnectionResult(bool Ok, string Message, IReadOnlyList<string> DavClasses, IReadOnlyList<string> AllowedMethods);

/// <summary>启用结果。</summary>
public sealed record DeviceSyncEnableResult(bool Ok, string Message);

/// <summary>播种进度（可中断、可续跑）。</summary>
public sealed record BackfillProgress(bool Done, int Enqueued, int GamesTotal, int SessionsTotal);

/// <summary>
/// 最近一次同步结果的固定文案（唯一真相：服务层写、UI 显示、测试断言同一组常量）。
/// ⚠️ <c>Deferred</c> / <c>有延迟</c> **都不是"失败"**：前者是依赖缺失/身份冲突，后者是会被重试的推送。
/// </summary>
public static class SyncOutcomeText
{
    public const string Success = "成功";
    public const string Delayed = "有延迟";
    public const string Deferred = "存在 Deferred";
    public const string Failed = "失败";

    /// <summary>
    /// 本轮没有传输、且**待发队列确实为空**（没有可同步的变更）—— 与
    /// <see cref="NotSynced"/> 严格区分：后者是"还有东西没同步出去"，绝不等于"已是最新"。
    /// </summary>
    public const string NoChanges = "已是最新";

    public const string NotSynced = "未同步";
}

/// <summary>
/// 「立即同步」结果文案的**唯一真相**（UI 只负责显示，不自己拼文案）。
///
/// <para>
/// 判定全部来自<b>真实一轮结果</b> + <b>待发队列真实状态</b>，不做任何推测：
/// 绝不因为"上传/下载计数为零"就宣称"已是最新"（那可能只是本轮被退避跳过、或引擎空转）。
/// </para>
/// </summary>
public static class SyncNowMessage
{
    /// <summary>本轮是否真的发生了传输（上传 / 下载落地 / 延后 / 墓碑传播任一项 &gt; 0）。</summary>
    public static bool HasTransfer(DeviceSyncRunResult result)
        => result.Completed > 0 || result.Applied > 0 || result.Deferred > 0
           || result.TombstonesPropagated > 0;

    /// <summary>
    /// 无错误时的提示文案。
    /// <paramref name="pendingOutbox"/> = 待发队列条数（来自 <c>DeviceSyncStatus.Pending</c>，
    /// 由 <c>CountPendingOutboxAsync</c> 真查库）。
    /// </summary>
    public static string Build(DeviceSyncRunResult result, int pendingOutbox)
    {
        if (HasTransfer(result))
        {
            return $"同步完成：上传 {result.Completed} 条、下载落地 {result.Applied} 条"
                   + (result.Deferred > 0 ? $"、延后 {result.Deferred} 条（已记录，稍后自动重试）" : string.Empty)
                   + "。";
        }

        return pendingOutbox > 0
            ? $"本轮没有传输，但仍有 {pendingOutbox} 条变更待同步（可能正在重试退避中）。"
            : "已是最新，没有需要同步的变更。";
    }
}

/// <summary>UI 只读状态快照。</summary>
public sealed record DeviceSyncStatus(
    bool Enabled,
    DateTimeOffset? LastSyncAtUtc,
    int Pending,
    int Failed,
    string? LastError,
    string ServerUrl,
    string SyncDirectory,
    string Account,
    int IntervalMinutes,
    bool InsecureHttp,
    string DeviceId,
    int Uploaded,
    int Downloaded,
    int Delayed,
    int Deferred,
    string? LastOutcome);

/// <summary>
/// <b>设备同步服务门面</b>（2h-6）：UI 与 <c>MainWindow</c> 只跟它打交道，
/// 它负责把「设置 + 凭据 + 后端 + 引擎 + 播种」缝起来。
///
/// <code>
/// UI (SyncPage) / MainWindow
///    │  只用本门面（不认识 WebDAV 协议，也不认识 SQL）
///    ▼
/// DeviceSyncService
///    ├─ ISyncBackend  → WebDavSyncBackend → IWebDavClient → HttpClient
///    ├─ DeviceSyncEngine（2c 冻结：Push → Pull → Apply）
///    ├─ ISecretStore（DPAPI，只存密码）
///    └─ settings 表（非敏感配置 + 播种进度）
/// </code>
///
/// ⚠️ <b>绝不抛异常给调用方</b>（除了 UI 显式调用的"测试连接/保存启用"，那里必须让用户看到原因）；
/// 游戏监听与 5 秒心跳**绝不**依赖本服务。
///
/// ⚠️ 本类**不启动**任何后台调度：15 分钟循环 / 会话结束去抖都在 <c>MainWindow</c>（v2 §E 冻结）。
/// </summary>
public sealed class DeviceSyncService
{
    private readonly SqliteRepository _repo;
    private readonly ISecretStore _secrets;
    private readonly Func<string, string, string, string, IWebDavClient> _clientFactory;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private WebDavSyncBackend? _backend;
    private DeviceSyncEngine? _engine;
    private IWebDavClient? _client;
    private string? _configSignature;

    private DateTimeOffset? _lastSyncAtUtc;
    private string? _lastError;

    // ===== 最近一次同步结果（真实一轮 DeviceSyncRunResult 的投影）=====
    // 首次读取时从 settings 恢复（新实例 / 重启后仍能显示）；之后以进程内值为准。
    private DateTimeOffset? _lastOutcomeAtUtc;
    private string? _lastOutcome;
    private int _lastUploaded;
    private int _lastDownloaded;
    private int _lastDelayed;
    private int _lastDeferred;
    private int _lastFailed;

    /// <param name="clientFactory">(serverUrl, account, password, syncDirectory) → 客户端。测试注入用。</param>
    public DeviceSyncService(
        SqliteRepository repo,
        ISecretStore secretStore,
        Func<string, string, string, string, IWebDavClient>? clientFactory = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _secrets = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _clientFactory = clientFactory ?? ((url, account, password, dir) => new WebDavClient(url, account, password, dir));
    }

    /// <summary>最近一次同步时间（内存态，供 UI 显示"刚刚 / 3 分钟前"）。</summary>
    public DateTimeOffset? LastSyncAtUtc => _lastSyncAtUtc;

    /// <summary>最近一次错误（内存态）。</summary>
    public string? LastError => _lastError;

    // ==================== 设置读写 ====================

    public async Task<WebDavSyncSettings> LoadSettingsAsync()
    {
        var settings = new WebDavSyncSettings
        {
            Enabled = string.Equals(await _repo.GetSettingAsync(WebDavSyncSettings.Keys.Enabled), "1", StringComparison.Ordinal),
            ServerUrl = await _repo.GetSettingAsync(WebDavSyncSettings.Keys.ServerUrl) ?? WebDavSyncSettings.JianguoYunServerUrl,
            Account = await _repo.GetSettingAsync(WebDavSyncSettings.Keys.Account) ?? string.Empty,
            SyncDirectory = await _repo.GetSettingAsync(WebDavSyncSettings.Keys.SyncDirectory) ?? WebDavSyncSettings.DefaultSyncDirectory
        };

        var intervalRaw = await _repo.GetSettingAsync(WebDavSyncSettings.Keys.IntervalMinutes);
        settings.IntervalMinutes = int.TryParse(intervalRaw, out var minutes)
            ? WebDavSyncSettings.ClampInterval(minutes)
            : WebDavSyncSettings.DefaultIntervalMinutes;

        await RestoreLastOutcomeAsync();

        return settings;
    }

    /// <summary>
    /// 恢复「最近一次同步结果」（**只做一次**：进程内已有比库里更新的值就不覆盖）。
    /// 这样"新实例 / 重启后"仍能显示上次结果，且「停用同步」后历史不会消失。
    /// </summary>
    private async Task RestoreLastOutcomeAsync()
    {
        if (_lastOutcomeAtUtc is not null) return;

        try
        {
            _lastOutcome = await _repo.GetSettingAsync(WebDavSyncSettings.Keys.LastOutcome);
            _lastUploaded = await ReadIntSettingAsync(WebDavSyncSettings.Keys.LastUploaded);
            _lastDownloaded = await ReadIntSettingAsync(WebDavSyncSettings.Keys.LastDownloaded);
            _lastDelayed = await ReadIntSettingAsync(WebDavSyncSettings.Keys.LastDelayed);
            _lastDeferred = await ReadIntSettingAsync(WebDavSyncSettings.Keys.LastDeferred);
            _lastFailed = await ReadIntSettingAsync(WebDavSyncSettings.Keys.LastFailed);

            var at = await _repo.GetSettingAsync(WebDavSyncSettings.Keys.LastOutcomeAtUtc);
            _lastOutcomeAtUtc = DateTimeOffset.TryParse(at, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
        }
        catch (Exception ex)
        {
            // 读不到 = 显示"未同步"，绝不影响同步本体。
            AppLog.Warn($"[设备同步] 读取最近一次同步结果失败：{ex.GetType().Name}");
        }
    }

    private async Task<int> ReadIntSettingAsync(string key)
        => int.TryParse(await _repo.GetSettingAsync(key), out var value) ? value : 0;

    private async Task SaveSettingsAsync(WebDavSyncSettings settings)
    {
        await _repo.SetSettingAsync(WebDavSyncSettings.Keys.ServerUrl, settings.ServerUrl.Trim());
        await _repo.SetSettingAsync(WebDavSyncSettings.Keys.Account, settings.Account.Trim());
        await _repo.SetSettingAsync(WebDavSyncSettings.Keys.SyncDirectory, settings.SyncDirectory.Trim());
        await _repo.SetSettingAsync(WebDavSyncSettings.Keys.IntervalMinutes,
            WebDavSyncSettings.ClampInterval(settings.IntervalMinutes).ToString());
        await _repo.SetSettingAsync(WebDavSyncSettings.Keys.Enabled, settings.Enabled ? "1" : "0");
    }

    /// <summary>可同步事实数量（启用前的"预计上传 N 条"确认用）。</summary>
    public Task<(int Games, int Sessions)> CountBackfillCandidatesAsync() => _repo.CountBackfillCandidatesAsync();

    // ==================== 测试连接 ====================

    /// <summary>
    /// OPTIONS 探测（**只读**：不建目录、不写任何东西）。
    /// <paramref name="password"/> 为 null/空时用已保存的凭据（"测试连接"复用已填内容）。
    /// </summary>
    public async Task<WebDavConnectionResult> TestConnectionAsync(
        WebDavSyncSettings settings, string? password, CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = string.IsNullOrEmpty(password)
                ? await _secrets.GetAsync(WebDavSyncSettings.SecretKeyFor(settings.ServerUrl, settings.SyncDirectory))
                : password;

            if (string.IsNullOrEmpty(secret))
            {
                return new WebDavConnectionResult(false, "没有可用凭据：请输入应用密码后重试。", Array.Empty<string>(), Array.Empty<string>());
            }

            // 走与正式同步同一个客户端工厂：测试可注入，生产即真实 HttpClient。
            using var client = _clientFactory(settings.ServerUrl, settings.Account, secret, settings.SyncDirectory);
            var caps = await client.ProbeAsync(cancellationToken);

            var supportsPropfind = caps.SupportsMethod("PROPFIND");
            var message = supportsPropfind
                ? "连接成功，服务器支持所需方法。"
                : $"连接成功，但未在 Allow 头里看到 PROPFIND（DAV: {caps.DavHeader ?? "无"}）。多数服务端不完整列出允许的方法，实际能否同步以首次同步结果为准。";

            return new WebDavConnectionResult(true, message, DavClassesOf(caps), caps.AllowedMethods);
        }
        catch (WebDavAuthException)
        {
            return new WebDavConnectionResult(false, "认证失败：请确认账号与应用密码（坚果云需在「安全选项 → 第三方应用管理」里生成应用密码，不是登录密码）。", Array.Empty<string>(), Array.Empty<string>());
        }
        catch (WebDavRateLimitedException ex)
        {
            return new WebDavConnectionResult(false, $"服务器限流，请稍后再试（{ex.RetryAfter?.TotalSeconds:0} 秒）。", Array.Empty<string>(), Array.Empty<string>());
        }
        catch (WebDavException ex)
        {
            return new WebDavConnectionResult(false, $"{ex.GetType().Name}：{ex.Message}", Array.Empty<string>(), Array.Empty<string>());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 测试连接失败：{ex.GetType().Name}");
            return new WebDavConnectionResult(false, $"无法连接：{ex.GetType().Name}（请检查服务器地址与网络）。", Array.Empty<string>(), Array.Empty<string>());
        }
    }

    private static IReadOnlyList<string> DavClassesOf(WebDavCapabilities caps)
        => (caps.DavHeader ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    // ==================== 保存并启用 ====================

    /// <summary>
    /// 保存配置 → 存密码（DPAPI）→ 建远端骨架 + 校验 protocol → 写启用标志 → **重置后台缓存**。
    ///
    /// ⚠️ 启用本身**不**自动跑播种：播种由 <see cref="RunBackfillAsync"/> 显式驱动
    /// （UI 需要在开始前先给出"预计上传 N 条"的确认）。
    /// </summary>
    public async Task<DeviceSyncEnableResult> SaveAndEnableAsync(
        WebDavSyncSettings settings, string? password, CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = string.IsNullOrEmpty(password)
                ? await _secrets.GetAsync(WebDavSyncSettings.SecretKeyFor(settings.ServerUrl, settings.SyncDirectory))
                : password;

            if (string.IsNullOrEmpty(secret))
            {
                return new DeviceSyncEnableResult(false, "请填写应用密码（坚果云需先生成应用密码）。");
            }

            if (string.IsNullOrWhiteSpace(settings.Account))
            {
                return new DeviceSyncEnableResult(false, "请填写账号。");
            }

            var deviceId = await _repo.GetLocalDeviceIdAsync();
            var deviceName = await GetLocalDeviceNameAsync();

            using var client = _clientFactory(settings.ServerUrl.Trim(), settings.Account.Trim(), secret, settings.SyncDirectory.Trim());
            var backend = new WebDavSyncBackend(client, deviceId, deviceName);

            // 建目录骨架 + 写/校验 protocol（版本不兼容会在这里明确拒绝）
            await backend.EnsureRemoteLayoutAsync(cancellationToken);

            await _secrets.SetAsync(WebDavSyncSettings.SecretKeyFor(settings.ServerUrl, settings.SyncDirectory), secret);

            settings.Enabled = true;
            await SaveSettingsAsync(settings);

            ResetBackend();
            _lastError = null;

            return new DeviceSyncEnableResult(true, "已启用设备同步。");
        }
        catch (WebDavAuthException)
        {
            return new DeviceSyncEnableResult(false, "认证失败：请确认账号与应用密码。");
        }
        catch (WebDavException ex)
        {
            return new DeviceSyncEnableResult(false, $"启用失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 启用失败：{ex.GetType().Name}");
            return new DeviceSyncEnableResult(false, $"启用失败：{ex.GetType().Name}");
        }
    }

    // ==================== 停用 ====================

    /// <summary>
    /// <b>停用设备同步</b>：把 <c>settings.Enabled</c> 写成 <c>"0"</c> —— 这是启动流程与同步轮次
    /// （<see cref="StartupAsync"/> / <see cref="SyncNowAsync"/>）的**唯一闸门**，之后两者都直接返回。
    ///
    /// ⚠️ <b>什么都不删</b>：sync_queue / tombstones / deferred 台账 / supersession / 游标 /
    /// 历史事实（games / sessions / daily_summary），以及 ServerUrl / Account / SyncDirectory /
    /// 最近一次同步结果 / 凭据（ISecretStore）全部原样保留 ⇒ 重新启用后从原状态继续，
    /// 不需要重新填服务器地址与应用密码。
    /// </summary>
    public async Task<DeviceSyncEnableResult> DisableAsync()
    {
        try
        {
            await _repo.SetSettingAsync(WebDavSyncSettings.Keys.Enabled, "0");
            return new DeviceSyncEnableResult(true, "已停用设备同步（同步数据保留，可随时重新启用）。");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 停用失败：{ex.GetType().Name}");
            return new DeviceSyncEnableResult(false, $"停用失败：{ex.GetType().Name}");
        }
    }

    private async Task<string> GetLocalDeviceNameAsync()
    {
        try
        {
            var devices = await _repo.GetDevicesAsync();
            var me = devices.FirstOrDefault(d => d.IsThisDevice);
            if (me is not null && !string.IsNullOrWhiteSpace(me.DeviceName)) return me.DeviceName;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 读取本机设备名失败（用机器名兜底）：{ex.GetType().Name}");
        }

        return Environment.MachineName;
    }

    // ==================== 启动 / 同步 ====================

    /// <summary>
    /// 应用启动时调用一次（v2 §E 冻结的四件事 + 2026-10-08 批准的第五件）：
    /// <code>
    /// engine.StartAsync()      ← 复位上次崩溃遗留的 InFlight（否则 2a 的恢复路径永远是休眠的）
    /// → RunOnceAsync()         ← 启动同步一次
    /// → ReconcileDeferredChangesAsync()  ← 2e 冻结的"仅启动触发"
    /// → RunBackfillAsync()     ← 播种未完成则续跑
    /// → RepairOutboxGapsAsync()← 历史 Outbox Gap Repair：只补"本地有、队列没有"的缺口
    ///                            （旧版本结束的会话 / 崩溃后自愈补了 identity 的会话 / 缺 identity 的游戏）
    /// </code>
    /// **永不抛异常**。
    /// </summary>
    public async Task StartupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await LoadSettingsAsync();
            if (!settings.Enabled) return;

            var engine = await EnsureEngineAsync(settings, cancellationToken);
            if (engine is null) return;

            await engine.StartAsync(cancellationToken);
            await SyncNowAsync(cancellationToken);
            await ReconcileDeferredAsync(cancellationToken);

            var backfill = BackfillState.Parse(await _repo.GetSettingAsync(WebDavSyncSettings.Keys.BackfillState));
            if (!backfill.Done)
            {
                AppLog.Info("[设备同步] 历史播种上次未完成 → 续跑");
                await RunBackfillAsync(cancellationToken);
            }

            // 升级语义兜底：播种 done 之后才出现、又没经过结束路径的历史事实，靠这一步补进队列。
            // 一次、有界、幂等（只补缺口，不重扫历史，不重复上传已完成的事实）。
            var repaired = await RepairOutboxGapsAsync(cancellationToken);
            if (repaired.Total > 0)
            {
                // 补出来的变更立刻推一轮（不等 15 分钟循环），让"启动即补齐"对用户可见
                await SyncNowAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 启动流程失败（本地计时不受影响）：{ex.GetType().Name}");
        }
    }

    // ==================== 历史 Outbox 缺口修复 ====================

    /// <summary>
    /// <b>历史 Outbox Gap Repair</b>（用户 2026-10-08 裁定）：一次**有界、幂等**的补齐 ——
    /// 只把"本地已经有、但 Device Sync 不知道"的历史事实补进 <c>sync_queue</c>。
    ///
    /// <para>
    /// 它**不是**重新播种：不重扫全部历史、不重置 <c>backfill_state.done</c>、
    /// 不会重复上传已经同步过的事实（判据 = 队列里已有该实体的变更就跳过，任何状态都算"有"）。
    /// </para>
    ///
    /// <para>
    /// 调用时机：同步**初始化阶段**（<see cref="StartupAsync"/>）自动一次；
    /// 启用流程（UI 的"保存并启用"）也可调用一次。未启用时直接返回空结果（不做任何写入）。
    /// **永不抛异常**。
    /// </para>
    /// </summary>
    public async Task<OutboxGapRepairResult> RepairOutboxGapsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await LoadSettingsAsync();
            if (!settings.Enabled) return OutboxGapRepairResult.Nothing;

            return await _repo.RepairOutboxGapsAsync(SyncOutboxPolicy.GapRepairBatchSize, cancellationToken);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 历史 Outbox 缺口修复失败（整体回滚，下次启动再试）：{ex.GetType().Name}");
            return OutboxGapRepairResult.Nothing;
        }
    }

    /// <summary>
    /// 跑一轮同步（手动「立即同步」/ 定时循环 / 会话结束去抖都走这里）。
    /// **永不抛异常**：引擎本身不抛；本方法额外兜住"设置读取 / 后端构造"的失败。
    /// </summary>
    public async Task<DeviceSyncRunResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await LoadSettingsAsync();
            if (!settings.Enabled) return DeviceSyncRunResult.NoOp("未启用设备同步");

            var engine = await EnsureEngineAsync(settings, cancellationToken);
            if (engine is null) return DeviceSyncRunResult.NoOp(_lastError ?? "同步后端不可用");

            // v2：先把本地封面/图标 blob 推上去（内容寻址、幂等、尽力而为），再跑引擎 ——
            // 这样本轮批次里引用的 hash 在远端一定读得到。上传失败只记 attempts，绝不影响数据同步。
            await UploadLocalBlobsAsync(cancellationToken);

            var result = await engine.RunOnceAsync(cancellationToken);

            // 事实落地之后，再尽力补齐"对端引用、本地还没有"的图片：
            // 失败只是"数据已同步、视觉资源待补齐"，下一轮重试。
            await DownloadMissingBlobsAsync(cancellationToken);

            _lastSyncAtUtc = DateTimeOffset.UtcNow;
            _lastError = result.Error;

            // 成功路径：把**真实一轮结果**投影成 UI 口径并持久化（计数全部来自 DeviceSyncRunResult）。
            await PersistOutcomeAsync(result);

            return result;
        }
        catch (Exception ex)
        {
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Warn($"[设备同步] 同步轮次失败（已吞掉，本地计时不受影响）：{_lastError}");

            // 异常路径：同样是"最近一次同步结果"，结论必须变成"失败"（不再显示上一次的成功）。
            await PersistOutcomeAsync(DeviceSyncRunResult.NoOp(_lastError));

            return DeviceSyncRunResult.NoOp(_lastError);
        }
    }

    /// <summary>
    /// 把真实一轮的 <see cref="DeviceSyncRunResult"/> 投影成计数 + 结论，写内存态与 settings 表。
    /// <b>只做投影，不新增任何同步语义</b>：
    /// <code>
    /// Uploaded   = Completed
    /// Downloaded = Applied
    /// Delayed    = PushFailed + PushRejected   （会被重试 / 已重试，≠ 完全失败）
    /// Deferred   = Deferred                    （依赖缺失 / 身份冲突，≠ 网络失败）
    /// Failed     = PushRejected                （服务端永久拒绝）
    /// </code>
    /// 持久化失败只记日志（内存态仍有效）：同步本体绝不因为"写显示状态"而失败。
    /// </summary>
    private async Task PersistOutcomeAsync(DeviceSyncRunResult result)
    {
        _lastOutcomeAtUtc = DateTimeOffset.UtcNow;

        // ⚠️ 2026-10-11：「已是最新」与「未同步」必须看**待发队列的真实状态**，
        //    绝不能因为本轮上传/下载计数为零就推断"没有需要同步的变更"
        //    （零计数还可能意味着：本轮被退避跳过、或引擎空转但队列里仍有待发条目）。
        var pendingOutbox = await SafeAsync(() => _repo.CountPendingOutboxAsync(), 0);
        _lastOutcome = DescribeOutcome(result, pendingOutbox);
        _lastUploaded = result.Completed;
        _lastDownloaded = result.Applied;
        _lastDelayed = result.PushFailed + result.PushRejected;
        _lastDeferred = result.Deferred;
        _lastFailed = result.PushRejected;

        try
        {
            await _repo.SetSettingAsync(WebDavSyncSettings.Keys.LastOutcomeAtUtc, NowIso());
            await _repo.SetSettingAsync(WebDavSyncSettings.Keys.LastOutcome, _lastOutcome);
            await _repo.SetSettingAsync(WebDavSyncSettings.Keys.LastUploaded, _lastUploaded.ToString());
            await _repo.SetSettingAsync(WebDavSyncSettings.Keys.LastDownloaded, _lastDownloaded.ToString());
            await _repo.SetSettingAsync(WebDavSyncSettings.Keys.LastDelayed, _lastDelayed.ToString());
            await _repo.SetSettingAsync(WebDavSyncSettings.Keys.LastDeferred, _lastDeferred.ToString());
            await _repo.SetSettingAsync(WebDavSyncSettings.Keys.LastFailed, _lastFailed.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 最近一次同步结果持久化失败（内存态仍然有效）：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 结论口径（用户 2026-10-11 细化，顺序即优先级）：
    /// <list type="number">
    ///   <item><c>IsClean</c> → <b>成功</b></item>
    ///   <item><c>PushFailed &gt; 0</c> → <b>有延迟</b>（退回待发、按退避重试）</item>
    ///   <item><c>Deferred &gt; 0</c> → <b>存在 Deferred</b>（台账留痕，稍后重处理）</item>
    ///   <item><c>PushRejected &gt; 0</c> 或 <c>Error</c> → <b>失败</b></item>
    ///   <item>整轮什么都没做（全 0 且无错误，= 引擎 <c>NoOp</c>）：
    ///         待发队列**为空** → <b>已是最新</b>（确实没有需要同步的变更）；
    ///         待发队列**仍有条目** → <b>未同步</b>（绝不谎报"已是最新"）。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ <paramref name="pendingOutbox"/> 必须是**真查库**得到的待发条数：
    /// "零计数"不等于"队列已清空"（可能只是本轮被退避跳过 / 引擎空转）。
    /// </para>
    /// </summary>
    internal static string DescribeOutcome(DeviceSyncRunResult result, int pendingOutbox)
    {
        var nothingHappened = result.Completed == 0 && result.PushFailed == 0 && result.PushRejected == 0
            && result.Pulled == 0 && result.Applied == 0 && result.Deferred == 0
            && result.TombstonesPropagated == 0 && result.Error is null;

        if (nothingHappened)
        {
            return pendingOutbox <= 0 ? SyncOutcomeText.NoChanges : SyncOutcomeText.NotSynced;
        }

        if (result.IsClean) return SyncOutcomeText.Success;
        if (result.PushFailed > 0) return SyncOutcomeText.Delayed;
        if (result.Deferred > 0) return SyncOutcomeText.Deferred;
        if (result.PushRejected > 0 || result.Error is not null) return SyncOutcomeText.Failed;
        return SyncOutcomeText.NotSynced;
    }

    /// <summary>
    /// Deferred 台账重处理（2e 冻结：**仅启动触发**）。
    ///
    /// ⚠️ 2e 的 <c>ReconcileDeferredChangesAsync</c> 异常口径**保持现状**（抛给调用方），
    /// 统一在**这一层** catch + 记日志 + 继续 —— 这是 v2 §E 明确的分工，2e 语义一字未改。
    /// </summary>
    /// <summary>
    /// <b>用户显式确认两条本地记录属于同一款游戏</b>（manual 游戏没有跨设备可比强键，只能人工确认）。
    ///
    /// <para>
    /// 复用既有身份合并机制（<c>MigrateGameDataToCanonical</c> + <c>sync_identity_supersessions</c>）
    /// 建立关联，并把被并入行的会话 / 声明 / 日统计搬到权威行；完成后立即跑一次 Deferred 重处理，
    /// 让依赖该 identity 的事实（例如之前因身份未确认而延后的会话）落到新的权威行上。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <c>sync_identity_supersessions</c> **不是同步实体** ⇒ 关联只在**本机**生效；
    /// 对端需要在它自己的候选列表里做同样的确认（两边各自显示候选）。冻结规则（强键冲突 /
    /// Notion 身份冲突 / ignored / 已归档）一律拒绝，确认接口不会放行它们。
    /// </para>
    /// </summary>
    public async Task<GameTimeTracker.Infrastructure.Database.GameIdentityLinkResult> ConfirmGameIdentityLinkAsync(
        int canonicalLocalId, int duplicateLocalId, CancellationToken cancellationToken = default)
    {
        var result = await _repo.ConfirmGameIdentityLinkAsync(canonicalLocalId, duplicateLocalId, null, cancellationToken);

        if (result.Confirmed)
        {
            // 重处理失败不影响已建立的身份关联（台账一致且可重放，下次启动会再跑）。
            await ReconcileDeferredAsync(cancellationToken);
        }

        return result;
    }

    public async Task<int> ReconcileDeferredAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var summary = await _repo.ReconcileDeferredChangesAsync(SyncOutboxPolicy.ReconcileBatchSize, cancellationToken);
            if (summary.Resolved > 0)
            {
                AppLog.Info($"[设备同步] Deferred 重处理：解决 {summary.Resolved} 条（仍待处理 {summary.StillDeferred}）");
            }

            return summary.Resolved;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] Deferred 重处理失败（状态一致且可重放，下次启动再试）：{ex.GetType().Name}");
            return 0;
        }
    }

    // ==================== 历史播种 ====================

    /// <summary>
    /// 历史播种（v2 §C）：**严格两阶段**（先全部 Game，再全部 Session），
    /// 每步最多入队 <see cref="WebDavSyncSettings.BackfillStepSize"/> 条，推成功后继续下一步；
    /// 进度写进 <c>settings</c>，可中断可续跑；重复执行因为确定性 queue_id 而完全幂等。
    ///
    /// ⚠️ 只 SELECT games/sessions + 只 INSERT sync_queue；绝不改动历史事实。
    /// </summary>
    public async Task<BackfillProgress> RunBackfillAsync(
        CancellationToken cancellationToken = default, int stepSize = WebDavSyncSettings.BackfillStepSize)
    {
        if (stepSize <= 0) stepSize = WebDavSyncSettings.BackfillStepSize;

        try
        {
            var settings = await LoadSettingsAsync();
            if (!settings.Enabled) return new BackfillProgress(true, 0, 0, 0);

            var (gamesTotal, sessionsTotal) = await _repo.CountBackfillCandidatesAsync();
            var state = BackfillState.Parse(await _repo.GetSettingAsync(WebDavSyncSettings.Keys.BackfillState));

            if (state.Done) return new BackfillProgress(true, state.Enqueued, gamesTotal, sessionsTotal);

            var engine = await EnsureEngineAsync(settings, cancellationToken);
            if (engine is null) return new BackfillProgress(false, state.Enqueued, gamesTotal, sessionsTotal);

            while (!state.Done && !cancellationToken.IsCancellationRequested)
            {
                var added = state.Phase == BackfillPhases.Games
                    ? await EnqueueGamesStepAsync(state, stepSize, cancellationToken)
                    : await EnqueueSessionsStepAsync(state, stepSize, cancellationToken);

                if (added == 0)
                {
                    // 本阶段已经没有更多数据 → 切阶段 / 收尾
                    if (state.Phase == BackfillPhases.Games)
                    {
                        state.Phase = BackfillPhases.Sessions;
                    }
                    else
                    {
                        state.Done = true;
                    }

                    await SaveBackfillStateAsync(state);
                    continue;
                }

                await SaveBackfillStateAsync(state);

                if (!await DrainOutboxAsync(engine, added, cancellationToken))
                {
                    // 推送不成功（网络/限流）→ 保留进度，下次启动续跑。**不丢数据、不空转**。
                    return new BackfillProgress(false, state.Enqueued, gamesTotal, sessionsTotal);
                }
            }

            // 收尾：把可能还挂着的待发（上一轮没推成功的）也推出去，避免"播种已完成但数据还堵在本地"。
            if (state.Done && !await DrainOutboxAsync(engine, stepSize, cancellationToken))
            {
                return new BackfillProgress(false, state.Enqueued, gamesTotal, sessionsTotal);
            }

            AppLog.Info($"[设备同步] 历史播种完成：本轮入队 {state.Enqueued} 条"
                + $"（可同步 Game {gamesTotal} / Session {sessionsTotal}）");

            return new BackfillProgress(state.Done, state.Enqueued, gamesTotal, sessionsTotal);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 历史播种中断（进度已保存，下次续跑）：{ex.GetType().Name}");
            return new BackfillProgress(false, 0, 0, 0);
        }
    }

    private async Task<int> EnqueueGamesStepAsync(BackfillState state, int stepSize, CancellationToken cancellationToken)
    {
        var rows = await _repo.GetBackfillGamesAsync(state.LastGameId, stepSize);
        var added = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var queueId = SyncHashing.BackfillQueueId(nameof(SyncEntityType.Game), row.GlobalId);
            if (await _repo.EnqueueOutboxIfAbsentAsync(new SyncQueueItem
            {
                QueueId = queueId,
                EntityType = nameof(SyncEntityType.Game),
                EntityGlobalId = row.GlobalId,
                Operation = SyncOperation.Create,
                BaseVersion = 1,
                Payload = SyncPayloadFactory.Game(
                    row.GlobalId, row.Name, row.Platform, row.PlatformId,
                    row.Executable, row.ExecutablePath, row.NotionPageId,
                    // v2：本地已有封面 ⇒ 带上内容 hash（登记为可上传 blob；没有则保持为空）
                    coverBlobHash: await _repo.TryRegisterLocalCoverBlobAsync(row.Platform, row.PlatformId)),
                CreatedAtUtc = NowIso(),
                Status = SyncQueueStatus.Pending
            }))
            {
                added++;
            }

            state.LastGameId = row.Id;
        }

        state.Enqueued += added;
        return rows.Count;
    }

    private async Task<int> EnqueueSessionsStepAsync(BackfillState state, int stepSize, CancellationToken cancellationToken)
    {
        var rows = await _repo.GetBackfillSessionsAsync(state.LastSessionId, stepSize);
        var added = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var queueId = SyncHashing.BackfillQueueId(nameof(SyncEntityType.Session), row.GlobalId);
            if (await _repo.EnqueueOutboxIfAbsentAsync(new SyncQueueItem
            {
                QueueId = queueId,
                EntityType = nameof(SyncEntityType.Session),
                EntityGlobalId = row.GlobalId,
                Operation = SyncOperation.Create,
                BaseVersion = 1,
                Payload = SyncPayloadFactory.Session(
                    row.GlobalId, row.GameGlobalId, row.DeviceId ?? string.Empty, row.ProcessName ?? string.Empty,
                    row.StartedAtUtc ?? string.Empty, row.EndedAtUtc, row.DurationSeconds,
                    row.TimePrecision ?? TimePrecision.Exact),
                CreatedAtUtc = NowIso(),
                Status = SyncQueueStatus.Pending
            }))
            {
                added++;
            }

            state.LastSessionId = row.Id;
        }

        state.Enqueued += added;
        return rows.Count;
    }

    /// <summary>把刚入队的这批推出去（引擎批大小 50 → 多跑几轮）。推送失败返回 false（保留进度）。</summary>
    private async Task<bool> DrainOutboxAsync(DeviceSyncEngine engine, int justEnqueued, CancellationToken cancellationToken)
    {
        // 播种出来的 Game 载荷可能引用封面 blob：先把内容推上去（幂等），否则对端只会看到"待补齐"。
        await UploadLocalBlobsAsync(cancellationToken);

        var maxRounds = (justEnqueued / SyncOutboxPolicy.PushBatchSize) + 2;

        for (var i = 0; i < maxRounds; i++)
        {
            var result = await engine.RunOnceAsync(cancellationToken);
            _lastSyncAtUtc = DateTimeOffset.UtcNow;

            if (result.Error is not null)
            {
                _lastError = result.Error;
                return false;
            }

            _lastError = null;

            if (result.Completed == 0) return true;   // 没有更多可推进的（其余在退避中，留给后续轮次）
        }

        return true;
    }

    private Task SaveBackfillStateAsync(BackfillState state)
        => _repo.SetSettingAsync(WebDavSyncSettings.Keys.BackfillState, state.ToJson());

    // ==================== Blob（内容寻址的封面/图标） ====================
    //
    // 为什么放在**服务层**而不是 ISyncBackend：ISyncBackend 的基础 contract 是冻结的
    // （只 Push/Pull SyncChange），图片是**附属资源**，不能在契约里加成员。
    // 于是：batch 里只走 hash 引用，真正的二进制由服务层用同一个 WebDavClient 独立搬运。
    // 这样也天然满足"图片失败不阻塞事实同步"：两条路径互不影响。

    /// <summary>
    /// 远端 blob 路径：<c>{同步目录}/blobs/&lt;sha256&gt;</c>。
    /// ⚠️ 必须带 <see cref="IWebDavClient.SyncRoot"/> 前缀 —— 客户端的越界防护要求
    /// 所有路径都落在同步目录之内（这是刻意的：同一个网盘里往往还有用户其它文件）。
    /// </summary>
    private string BlobRelativePath(string sha256) => $"{_client!.SyncRoot}/blobs/{sha256}";

    /// <summary>
    /// 把本地待上传的 blob 推上去（内容寻址 ⇒ 同 hash 重复 PUT 是幂等的，不新增远端对象）。
    /// **尽力而为**：任何失败只记 <c>attempts/last_error</c>，绝不影响事实同步。
    /// </summary>
    private bool _blobsFolderEnsured;

    private async Task<int> UploadLocalBlobsAsync(CancellationToken cancellationToken)
    {
        if (_client is null) return 0;

        // 先把 {同步目录}/blobs 建出来（本进程只做一次）：否则第一次 PUT 会因父目录缺失被拒（409），
        // 客户端补建目录后会**再 PUT 一次** —— 内容寻址下依然幂等，但白白多打一次请求。
        if (!_blobsFolderEnsured)
        {
            try
            {
                await _client.EnsureCollectionAsync($"{_client.SyncRoot}/blobs", cancellationToken);
                _blobsFolderEnsured = true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[设备同步] 创建远端 blobs 目录失败（将在首个 PUT 时按既有策略补建）：{ex.GetType().Name}");
            }
        }

        var uploaded = 0;

        try
        {
            foreach (var blob in await _repo.GetPendingBlobUploadsAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (string.IsNullOrWhiteSpace(blob.LocalPath) || !File.Exists(blob.LocalPath))
                    {
                        await _repo.MarkBlobUploadFailedAsync(blob.Sha256, "本地文件不存在");
                        continue;
                    }

                    var bytes = await File.ReadAllBytesAsync(blob.LocalPath, cancellationToken);

                    // 自校验：文件名就是内容哈希 ⇒ 本地文件被换掉时绝不把错误内容传上去。
                    if (!string.Equals(SqliteRepository.Sha256BytesHex(bytes), blob.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        await _repo.MarkBlobUploadFailedAsync(blob.Sha256, "内容哈希与 blob 身份不一致（本地文件已变化）");
                        continue;
                    }

                    await _client.PutBytesAsync(BlobRelativePath(blob.Sha256), bytes, cancellationToken);
                    await _repo.MarkBlobUploadedAsync(blob.Sha256);
                    uploaded++;
                }
                catch (Exception ex)
                {
                    await _repo.MarkBlobUploadFailedAsync(blob.Sha256, $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 上传封面/图标 blob 失败（不影响数据同步）：{ex.GetType().Name}");
        }

        return uploaded;
    }

    /// <summary>
    /// 补齐"远端已引用、本地还没有"的图片。
    ///
    /// 成功 → 写入内容寻址缓存（<c>cache/blobs/&lt;sha256&gt;</c>）+ 落到该游戏的封面缓存路径
    /// （现有 UI/统计直接就能显示，不需要第二套读取逻辑）+ 引用标就绪。
    /// 失败 / 远端还没有 → 只累加 <c>attempts</c> 与 <c>last_error</c>（下一轮重试），
    /// 表现为"数据已同步、视觉资源待补齐" —— **绝不影响** Game/Session/Adjustment 的事实。
    /// </summary>
    private async Task<int> DownloadMissingBlobsAsync(CancellationToken cancellationToken)
    {
        if (_client is null) return 0;

        var downloaded = 0;

        try
        {
            foreach (var refRow in await _repo.GetPendingBlobRefsAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var bytes = await _client.TryGetBytesAsync(BlobRelativePath(refRow.Sha256), cancellationToken);

                    if (bytes is null)
                    {
                        await _repo.MarkBlobRefFailedAsync(
                            refRow.EntityType, refRow.EntityGlobalId, refRow.Kind,
                            "远端还没有这个 blob（等对端上传后重试）");
                        continue;
                    }

                    // 自校验：内容必须与引用声明的 hash 一致，否则拒绝落地（宁可不显示图，也不写错内容）。
                    if (!string.Equals(SqliteRepository.Sha256BytesHex(bytes), refRow.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        await _repo.MarkBlobRefFailedAsync(
                            refRow.EntityType, refRow.EntityGlobalId, refRow.Kind, "内容哈希与引用不一致");
                        continue;
                    }

                    Directory.CreateDirectory(SqliteRepository.BlobDirectory);
                    var path = SqliteRepository.BlobFilePath(refRow.Sha256);
                    if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes, cancellationToken);

                    await _repo.RegisterLocalBlobContentAsync(refRow.Sha256, path, bytes.LongLength);

                    if (string.Equals(refRow.Kind, BlobKinds.Cover, StringComparison.Ordinal)
                        && string.Equals(refRow.EntityType, nameof(SyncEntityType.Game), StringComparison.Ordinal))
                    {
                        var target = await _repo.GetCoverTargetForGameAsync(refRow.EntityGlobalId);
                        if (target is not null)
                        {
                            Directory.CreateDirectory(SqliteRepository.CoverDirectory);
                            await File.WriteAllBytesAsync(target, bytes, cancellationToken);
                        }
                    }

                    await _repo.MarkBlobRefReadyAsync(refRow.EntityType, refRow.EntityGlobalId, refRow.Kind);
                    downloaded++;
                }
                catch (Exception ex)
                {
                    await _repo.MarkBlobRefFailedAsync(
                        refRow.EntityType, refRow.EntityGlobalId, refRow.Kind,
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[设备同步] 补齐封面/图标 blob 失败（数据同步不受影响）：{ex.GetType().Name}");
        }

        return downloaded;
    }

    // ==================== 状态查询 ====================

    /// <summary>UI 用的只读状态快照（**永不抛**）。</summary>
    public async Task<DeviceSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // LoadSettingsAsync 同时会把「最近一次同步结果」从 settings 恢复到内存态（新实例 / 重启后仍可显示）。
        var settings = await LoadSettingsAsync();
        var pending = await SafeAsync(() => _repo.CountPendingOutboxAsync(), 0);
        var deviceId = await SafeAsync(() => _repo.GetLocalDeviceIdAsync(), string.Empty);

        var insecure = settings.ServerUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        // 上次同步：优先本次进程内的值 →「最近一次同步结果」的持久化时刻 → 读库
        // （sync_state.last_sync_at_utc 只在"远端落地 + 游标"同一事务里写，纯上传轮次不更新它，
        //   所以先看结果时刻；两者都是"最近一次同步"，不存在口径冲突）
        var lastSync = _lastSyncAtUtc ?? _lastOutcomeAtUtc;
        if (lastSync is null)
        {
            var persisted = await SafeAsync(() => _repo.GetLastSyncAtUtcAsync(), (string?)null);
            if (!string.IsNullOrWhiteSpace(persisted)
                && DateTimeOffset.TryParse(persisted, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                lastSync = parsed;
            }
        }

        // Failed = 最近一轮被服务端永久拒绝（PushRejected）的条数 —— 与"最近一次同步结果"同口径；
        // 累计死信条数仍在仓储里（CountFailedOutboxAsync），只是不再混进这张"最近一轮"快照。
        return new DeviceSyncStatus(
            settings.Enabled, lastSync, pending, _lastFailed, _lastError,
            settings.ServerUrl, settings.SyncDirectory, settings.Account,
            settings.IntervalMinutes, insecure, deviceId,
            _lastUploaded, _lastDownloaded, _lastDelayed, _lastDeferred, _lastOutcome);
    }

    private static async Task<T> SafeAsync<T>(Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch
        {
            return fallback;
        }
    }

    // ==================== 后端缓存 ====================

    /// <summary>
    /// 按当前配置构造（或复用）后端 + 引擎。
    /// 配置指纹变化（服务器 / 目录 / 账号 / 密码变了）→ 重建，**绝不复用旧游标语义**。
    /// </summary>
    private async Task<DeviceSyncEngine?> EnsureEngineAsync(WebDavSyncSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var password = await _secrets.GetAsync(
                WebDavSyncSettings.SecretKeyFor(settings.ServerUrl, settings.SyncDirectory));

            if (string.IsNullOrEmpty(password))
            {
                _lastError = "同步凭据不可用，请在同步设置里重新输入应用密码";
                return null;
            }

            // 指纹里放密码的哈希而不是密码本身：既能感知变更，又不在内存里多留一份明文。
            var signature = $"{settings.ServerUrl.Trim()}|{settings.SyncDirectory.Trim()}|{settings.Account.Trim()}"
                + $"|{SyncHashing.Sha256Hex(password)[..8]}";

            if (_engine is not null && _backend is not null && string.Equals(_configSignature, signature, StringComparison.Ordinal))
            {
                return _engine;
            }

            ResetBackend();

            var deviceId = await _repo.GetLocalDeviceIdAsync();
            var deviceName = await GetLocalDeviceNameAsync();

            _client = _clientFactory(settings.ServerUrl.Trim(), settings.Account.Trim(), password, settings.SyncDirectory.Trim());
            _backend = new WebDavSyncBackend(_client, deviceId, deviceName);
            _engine = new DeviceSyncEngine(_repo, _backend);
            _configSignature = signature;

            return _engine;
        }
        catch (Exception ex)
        {
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Warn($"[设备同步] 构造同步后端失败：{_lastError}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ResetBackend()
    {
        _client?.Dispose();
        _client = null;
        _backend = null;
        _engine = null;
        _configSignature = null;
    }

    private static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
