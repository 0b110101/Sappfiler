using GameTimeTracker.Core.DeviceSync;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.WebDav;

/// <summary>
/// 把「一批 <see cref="SyncChange"/>」翻译成 WebDAV 资源操作的 <see cref="ISyncBackend"/> 实现。
///
/// <b>Push</b>（v2 §F.2，顺序固定）：
/// <code>
/// ① PUT devices/&lt;me&gt;/batches/&lt;batchId&gt;.json     ← 不可变；文件名 = change_id 集合的内容哈希
/// ② PUT devices/&lt;me&gt;/manifest.json                ← 追加该条；**这是"完成"的唯一依据**
/// ③ 返回 AcceptedChangeIds = 本批全部
/// </code>
/// 任一步失败 → 抛异常 → 引擎整批退回 Pending + 退避重推；①②都会重做，且都幂等 ✓
/// （**先 batch 后 manifest 才 ACK** ⇒ 绝不会留下"清单里有、文件没有"的孤儿）
///
/// <b>Pull</b>（v2 §F.3）：
/// <code>
/// PROPFIND devices/            → 远端设备目录（跳过自己）
///   每个设备：GET manifest.json → batches[]（**顺序即消费顺序**）
///     ├─ 404 / 坏 JSON → 有界修复：PROPFIND batches/ 后按文件名序消费（只此一次）
///     对 cursor 之后的每一项：GET → 自校验 batch_id == 文件名 → 展开为 SyncChange[]
///   cursor[device] = 最后一个**成功取回**的 batch 文件名
/// </code>
///
/// ⚠️ <b>不变量</b>：每个设备只写自己的目录（单写者 → 无并发写冲突）；
/// batch 不可变且内容决定文件名（重推幂等）；游标基于**清单顺序**而非时间/序号。
///
/// ⚠️ <b>墓碑校验的边界（明示）</b>：Push 的"拒绝复活"依据是**本进程已知的墓碑集合**
/// （自己推过的 Delete + Pull 时在远端 feed 里见过的 Delete）。跨进程的极端情形
/// （设备重启后从未 Pull 就推一条陈旧 Create）仍会被<strong>消费侧</strong>的墓碑优先逻辑兜住 ——
/// 服务端不会"复活"任何东西，因为 Delete 已经是 feed 里的不可变事实。
/// 这样做的原因是坚果云免费档 600 请求 / 30 分钟的硬预算：不做"每次 Push 前全量扫描 feed"。
/// </summary>
public sealed class WebDavSyncBackend : ISyncBackend
{
    private const string DevicesFolder = "devices";
    private const string BatchesFolder = "batches";
    private const string ManifestFileName = "manifest.json";
    private const string ProtocolFileName = "protocol";

    private readonly IWebDavClient _client;
    private readonly string _deviceId;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 本进程已知的墓碑（<c>"entityType|globalId"</c> → 墓碑的 <c>deleted_at_utc</c>）。
    /// 只在 Delete 经过本后端时增长，永不缩小。
    ///
    /// <para>
    /// ⚠️ 2026-10-12：必须**带时间**。此前只存 key，于是"Delete 是终态"被无条件执行 ——
    /// 用户删除后又在某台设备上**重新启用**同一实体（发一条**更新的** Create）时，会被这里**永久拒绝**
    /// （真机实测：<c>last_error="远端已有该实体的墓碑（已删除实体不得复活）"</c>），
    /// 对端因此永远收不到"撤销"，表现为对端一直显示已删除、且它其后产生的会话全部延后。
    /// 现在改成与接收端（<c>ApplyRemoteChangesWithCursorAsync</c> 的墓碑分支）**同构**的判定：
    /// 只有比墓碑更新的变更才放行；陈旧 Create 仍被拒。
    /// </para>
    /// </summary>
    private readonly Dictionary<string, string> _knownTombstones = new(StringComparer.Ordinal);

    private readonly object _cacheLock = new();

    /// <summary>本机 manifest 的内存缓存（本机目录只有本机写 → 缓存安全，省掉每次 Push 的一次 GET）。</summary>
    private WebDavManifest? _ownManifest;

    /// <summary>本进程已知"本机远端目录里还没有清单"（省掉每轮一次 404 探测）。</summary>
    private bool _ownManifestAbsent;

    /// <summary>
    /// 本进程**推送出去**的 batch 文件名。
    /// 用途：同一轮的 Pull 不必把自己刚推出去的 batch 再下载回来 ——
    /// 内容是我们自己生成的，且里面的变更按 <c>device_id</c> 一定会被过滤掉。
    /// ⚠️ 跳过下载的同时**仍然推进游标**（见 <c>PullAsync</c>），否则重启后会重拉自己的全部历史。
    /// </summary>
    private readonly HashSet<string> _ownPushedBatches = new(StringComparer.Ordinal);

    private bool _ownFoldersEnsured;

    public WebDavSyncBackend(
        IWebDavClient client,
        string deviceId,
        string deviceName,
        Func<DateTimeOffset>? clock = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("device_id 不能为空。", nameof(deviceId));

        // device_id 会直接进入远端路径 → 只允许安全字符，避免路径注入（客户端虽已挡 ..，但目录段本身要干净）。
        _deviceId = deviceId.Trim();
        foreach (var ch in _deviceId)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            {
                throw new ArgumentException($"device_id 含非法字符（只允许字母/数字/-/_）：{deviceId}", nameof(deviceId));
            }
        }

        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Windows Computer" : deviceName.Trim();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        // 换服务器/换同步目录 → BackendId 变化 → 换一条独立游标（2f：绝不复用游标）。
        BackendId = "webdav:" + WebDavWire.Sha256Hex($"{client.BaseUrl}|{client.SyncRoot}")[..12];
    }

    /// <inheritdoc />
    public string BackendId { get; }

    /// <summary>本机 device_id（= 远端目录名）。</summary>
    public string DeviceId => _deviceId;

    /// <summary>本机设备名（写进 manifest，仅供人阅读）。</summary>
    public string DeviceName { get; }

    // ==================== 远端布局 ====================

    private string DevicesPath => $"{_client.SyncRoot}/{DevicesFolder}";

    private string OwnPath => $"{DevicesPath}/{_deviceId}";

    private string OwnBatchesPath => $"{OwnPath}/{BatchesFolder}";

    private string OwnManifestPath => $"{OwnPath}/{ManifestFileName}";

    private string ProtocolPath => $"{_client.SyncRoot}/{ProtocolFileName}";

    /// <summary>OPTIONS：用于"测试连接"（能力探测）。</summary>
    public Task<WebDavCapabilities> TestConnectionAsync(CancellationToken cancellationToken = default)
        => _client.ProbeAsync(cancellationToken);

    /// <summary>
    /// 首次启用时建立远端骨架 + 写/校验 <c>protocol</c>：
    /// <code>
    /// MKCOL {root} → {root}/devices → {root}/devices/&lt;me&gt; → .../batches
    /// protocol 不存在 → 写入 {"protocol_version":1}
    /// protocol 存在但不兼容 → **拒绝启用并明确报错**（不尝试兼容猜测）
    /// </code>
    /// </summary>
    public async Task EnsureRemoteLayoutAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOwnFoldersAsync(cancellationToken, force: true);

        var existing = await _client.TryGetAsync(ProtocolPath, cancellationToken);
        if (existing is null)
        {
            await _client.PutAsync(ProtocolPath,
                WebDavWire.Serialize(new WebDavProtocolFile()), cancellationToken);
            return;
        }

        var parsed = WebDavWire.TryDeserialize<WebDavProtocolFile>(existing.Content);
        if (parsed is null)
        {
            throw new WebDavProtocolException($"远端 {ProtocolPath} 不是合法的协议文件（拒绝猜测兼容）");
        }

        if (parsed.ProtocolVersion != DeviceSyncConstants.CurrentProtocolVersion)
        {
            throw new WebDavProtocolException(
                $"远端协议版本 {parsed.ProtocolVersion} 与本机 {DeviceSyncConstants.CurrentProtocolVersion} 不兼容：拒绝启用同步");
        }
    }

    /// <summary>
    /// 确保本机目录骨架存在。
    /// 常见路径只花 **1 次请求**（PROPFIND 叶子目录）：叶子在 → 祖先必然在 ✓；
    /// 叶子缺失才逐级补齐（一次性成本）。
    /// </summary>
    private async Task EnsureOwnFoldersAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (_ownFoldersEnsured && !force) return;

        var leaf = await _client.ListAsync(OwnBatchesPath, WebDavDepth.Zero, cancellationToken);
        if (!leaf.Any(r => r.IsCollection))
        {
            foreach (var path in new[] { _client.SyncRoot, DevicesPath, OwnPath, OwnBatchesPath })
            {
                var items = await _client.ListAsync(path, WebDavDepth.Zero, cancellationToken);
                if (items.Any(r => r.IsCollection)) continue;      // 已存在
                await _client.EnsureCollectionAsync(path, cancellationToken);
            }
        }

        _ownFoldersEnsured = true;
    }

    // ==================== Push ====================

    /// <inheritdoc />
    public async Task<PushOutcome> PushAsync(
        IReadOnlyList<SyncChange> changes, CancellationToken cancellationToken = default)
    {
        var outcome = new PushOutcome();
        if (changes is null || changes.Count == 0) return outcome;

        var accepted = new List<SyncChange>(changes.Count);

        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.ChangeId))
            {
                outcome.Rejected.Add(new PushRejection(change.ChangeId ?? "", "change_id 为空", Permanent: true));
                continue;
            }

            // 墓碑优先：绝不把**陈旧**的变更重新推回远端（防止远端旧数据把已删除实体复活）。
            //
            // ⚠️ 2026-10-12：Delete 不再是绝对终态 —— 用户删除后又在**同一台设备**上重新启用同一实体时，
            //    那条**更新的**变更必须能出去，对端据此撤销删除（与接收端规则对称）。
            //    判据：本次变更的时间必须**晚于**已知墓碑时间；缺时间 / 解析失败 / 更早 ⇒ 照旧拒绝。
            if (change.Operation != SyncOperation.Delete
                && _knownTombstones.TryGetValue(TombstoneKey(change.EntityType, change.EntityGlobalId), out var tombstonedAt)
                && !IsNewerThanTombstone(change.CreatedAtUtc, tombstonedAt))
            {
                outcome.Rejected.Add(new PushRejection(change.ChangeId,
                    "远端已有该实体的墓碑（已删除实体不可复活；除非本次变更比墓碑更新）", Permanent: true));
                continue;
            }

            accepted.Add(change);
        }

        if (accepted.Count == 0) return outcome;

        var batchId = WebDavBatchId.Compute(accepted.Select(c => c.ChangeId));
        var fileName = batchId + ".json";
        var now = WebDavWire.UtcNow(_clock());

        await EnsureOwnFoldersAsync(cancellationToken);

        var batch = new WebDavBatchFile
        {
            ProtocolVersion = DeviceSyncConstants.CurrentProtocolVersion,
            BatchId = batchId,
            DeviceId = _deviceId,
            CreatedAtUtc = now,
            Changes = accepted.Select(ToWire).ToList()
        };

        // ① 先写 batch（不可变 + 内容决定文件名 → 重推就是覆盖同一个文件）
        await _client.PutAsync($"{OwnBatchesPath}/{fileName}", WebDavWire.Serialize(batch), cancellationToken);

        // ② 再写 manifest（**唯一完成标志**）
        var manifest = await LoadOwnManifestAsync(cancellationToken);

        if (!manifest.Batches.Any(b => string.Equals(b.BatchId, batchId, StringComparison.Ordinal)))
        {
            manifest.Batches.Add(new WebDavManifestBatch
            {
                File = fileName,
                BatchId = batchId,
                Seq = manifest.Batches.Count + 1,     // 仅供人阅读；不参与游标
                ChangeCount = accepted.Count,
                MaxCreatedAtUtc = MaxCreatedAt(accepted)
            });
        }

        manifest.ManifestVersion = WebDavManifest.CurrentManifestVersion;
        manifest.ProtocolVersion = DeviceSyncConstants.CurrentProtocolVersion;
        manifest.DeviceId = _deviceId;
        manifest.DeviceName = DeviceName;
        manifest.UpdatedAtUtc = now;

        await _client.PutAsync(OwnManifestPath, WebDavWire.Serialize(manifest), cancellationToken);

        lock (_cacheLock)
        {
            _ownManifest = manifest;
            _ownManifestAbsent = false;

            // ⚠️ 只有"整批都是本机产生的变更"才登记为可跳过。
            // 判据与 Pull 的过滤条件**完全一致**（change.device_id == 本机 device_id）——
            // 否则"同一个后端实例里存在的其它写入者"（2f 契约套件就是这么构造的）
            // 推出去的 batch 也会被误跳过，导致对端数据永远拉不到。
            if (accepted.All(c => string.Equals(c.DeviceId, _deviceId, StringComparison.Ordinal)))
            {
                _ownPushedBatches.Add(fileName);
            }
        }

        foreach (var change in accepted.Where(c => c.Operation == SyncOperation.Delete))
        {
            RememberTombstone(change.EntityType, change.EntityGlobalId, change.CreatedAtUtc);
        }

        outcome.AcceptedChangeIds.AddRange(accepted.Select(c => c.ChangeId));
        return outcome;
    }

    /// <summary>读本机 manifest（内存缓存优先；404 → 新建；坏 JSON → 重建并记 Warn）。</summary>
    private async Task<WebDavManifest> LoadOwnManifestAsync(CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_ownManifest is not null) return _ownManifest;
        }

        var existing = await _client.TryGetAsync(OwnManifestPath, cancellationToken);

        if (existing is not null)
        {
            var parsed = WebDavWire.TryDeserialize<WebDavManifest>(existing.Content);
            if (parsed is not null) return parsed;

            // ⚠️ 损坏时必须**从远端 batches/ 重建**，而不是从空清单开始：
            // 从空开始会让已经写出去的旧 batch 从清单里消失 —— 消费侧的"有界修复路径"只在
            // 清单**缺失/损坏**时救回来，而重建后的清单是"存在但不完整"，那张网兜不住 ✗。
            // 结果会是：未来加入的新设备永远看不到这段历史。重建只多花 1 次 PROPFIND（仅异常路径）。
            AppLog.Warn("[WebDAV] 本机 manifest 无法解析 → 从远端 batches/ 重建（保住旧 batch）");
            return await RebuildOwnManifestFromRemoteAsync(cancellationToken);
        }

        return new WebDavManifest { DeviceId = _deviceId, DeviceName = DeviceName };
    }

    /// <summary>损坏自愈：列出自己的 <c>batches/</c> 重建清单（按文件名字典序，只此一次 PROPFIND）。</summary>
    private async Task<WebDavManifest> RebuildOwnManifestFromRemoteAsync(CancellationToken cancellationToken)
    {
        var manifest = new WebDavManifest { DeviceId = _deviceId, DeviceName = DeviceName };

        var items = await _client.ListAsync(OwnBatchesPath, WebDavDepth.One, cancellationToken);

        foreach (var file in items
            .Where(r => !r.IsCollection && r.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Path[(r.Path.LastIndexOf('/') + 1)..])
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            manifest.Batches.Add(new WebDavManifestBatch
            {
                File = file,
                BatchId = file[..^5],
                Seq = manifest.Batches.Count + 1,
                // 重建时不去 GET 每个 batch（不值得为可读性字段多花请求）
                ChangeCount = 0
            });
        }

        return manifest;
    }

    // ==================== Pull ====================

    /// <inheritdoc />
    public async Task<PullOutcome> PullAsync(
        string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) limit = SyncOutboxPolicy.PullBatchSize;

        var state = WebDavCursor.Parse(cursor);
        var changes = new List<SyncChange>();
        var seenChangeIds = new HashSet<string>(StringComparer.Ordinal);
        var reachedLimit = false;

        var remoteDevices = await ListRemoteDevicesAsync(cancellationToken);
        var isOwnDevice = false;   // 每个设备循环里重设（见下）

        foreach (var device in remoteDevices)
        {
            isOwnDevice = string.Equals(device, _deviceId, StringComparison.Ordinal);

            var files = await ListBatchFilesAsync(device, cancellationToken);
            var startIndex = ResolveStartIndex(device, state, files);

            for (var i = startIndex; i < files.Count; i++)
            {
                // limit 是"变更条数"上限；以**整批**为原子单位，绝不在 batch 内部截断
                // （否则游标无法表达"这批只消费了一半"）。
                if (changes.Count >= limit)
                {
                    reachedLimit = true;
                    break;
                }

                var file = files[i];

                // 自己刚推出去的 batch：内容已知、变更一定会被过滤 → **不发 GET**，只推进游标。
                if (isOwnDevice && IsOwnPushedBatch(file))
                {
                    state.Devices[device] = file;
                    continue;
                }

                var batch = await FetchAndValidateBatchAsync(device, file, cancellationToken);

                foreach (var wire in batch.Changes)
                {
                    if (wire.Operation == (int)SyncOperation.Delete)
                    {
                        // 在远端 feed 里看见墓碑 → 连**时间**一起记下来：之后本机**陈旧**的 Create 推送
                        // 才会被拒，而"更新的重新启用"必须能出去（2026-10-12）。
                        RememberTombstone(wire.EntityType, wire.EntityGlobalId, wire.CreatedAtUtc);
                    }

                    if (!seenChangeIds.Add(wire.ChangeId)) continue;   // 同一条变更只投递一次

                    // 本机自己产生的变更不再回灌本地（本地早已是它的来源；回灌只会
                    // 制造 already_exists 的 Deferred 噪音，白白污染台账与日志）。
                    var originDevice = string.IsNullOrWhiteSpace(wire.DeviceId) ? device : wire.DeviceId;
                    if (string.Equals(originDevice, _deviceId, StringComparison.Ordinal)) continue;

                    changes.Add(ToChange(wire, device));
                }

                // 只有整批成功取回并解析后，游标才允许推进到它。
                state.Devices[device] = file;
            }

            if (reachedLimit) break;
        }

        return new PullOutcome
        {
            Changes = changes,
            NextCursor = WebDavWire.Serialize(state),
            HasMore = reachedLimit
        };
    }

    /// <summary>
    /// PROPFIND devices/：远端设备目录集合（**包含本机自己的目录**）。
    ///
    /// ⚠️ 为什么不跳过自己的目录：契约面（2f 套件）只保证"一个后端实例 + 一个全局 feed"，
    /// 以目录名当身份会把"同一后端的多个写入者"误判成自己而丢数据。真正该过滤的是
    /// **变更自身的 <c>device_id</c>**（见 <see cref="PullAsync"/>）—— 那才是"这条变更由本机产生"的正确定义，
    /// 而且顺带覆盖了"复制数据目录导致 device_id 冲突"的异常情形。
    /// </summary>
    private async Task<List<string>> ListRemoteDevicesAsync(CancellationToken cancellationToken)
    {
        var items = await _client.ListAsync(DevicesPath, WebDavDepth.One, cancellationToken);

        // ⚠️ Depth:1 的结果**包含被列目录自身**（Path == DevicesPath，末段是 "devices"）。
        // 必须按前缀严格筛掉它，否则每轮会多问两个不存在的设备（浪费请求）并把假设备写进游标。
        var prefix = DevicesPath + "/";

        return items
            .Where(r => r.IsCollection)
            .Select(r => r.Path.Trim('/'))
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p[prefix.Length..])
            .Where(name => name.Length > 0 && !name.Contains('/'))   // 只要直接子目录
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)   // 确定性遍历顺序（与时间无关）
            .ToList();
    }

    /// <summary>
    /// 取某设备的**有序** batch 文件名列表：正常路径读 manifest；
    /// manifest 缺失 / 坏 JSON → **有界修复路径**（PROPFIND batches/ 后按文件名字典序）。
    /// </summary>
    private async Task<List<string>> ListBatchFilesAsync(string device, CancellationToken cancellationToken)
    {
        // 本机自己的目录：内存缓存优先 —— 省掉每轮一次 GET。
        // 本机目录只有本机写（单写者不变量），因此缓存是安全的。
        var isOwnDevice = string.Equals(device, _deviceId, StringComparison.Ordinal);

        if (isOwnDevice)
        {
            lock (_cacheLock)
            {
                if (_ownManifest is not null)
                {
                    return _ownManifest.Batches
                        .Select(b => b.File)
                        .Where(f => !string.IsNullOrWhiteSpace(f))
                        .ToList();
                }

                // 本进程已知"本机目录里连清单都还没有" → 不必每轮再探一次。
                // （本机目录只有本机写，所以这个判断在进程内是可靠的。）
                if (_ownManifestAbsent) return new List<string>();
            }
        }

        var manifestPath = $"{DevicesPath}/{device}/{ManifestFileName}";
        var file = await _client.TryGetAsync(manifestPath, cancellationToken);

        if (file is null && isOwnDevice)
        {
            // 本机目录：清单不存在 ⇒ 里面不可能有"我们没见过的 batch"（我们只写自己的目录），
            // 因此跳过修复路径的 PROPFIND，并记住这个事实。
            lock (_cacheLock) _ownManifestAbsent = true;

            return new List<string>();
        }

        if (file is not null)
        {
            var manifest = WebDavWire.TryDeserialize<WebDavManifest>(file.Content);

            if (manifest is not null)
            {
                if (manifest.ProtocolVersion != DeviceSyncConstants.CurrentProtocolVersion)
                {
                    throw new WebDavProtocolException(
                        $"设备 {device} 的 manifest 协议版本 {manifest.ProtocolVersion} 与本机 "
                        + $"{DeviceSyncConstants.CurrentProtocolVersion} 不兼容（拒绝猜测：{manifestPath}）");
                }

                return manifest.Batches
                    .Select(b => b.File)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .ToList();
            }

            AppLog.Warn($"[WebDAV] 设备 {device} 的 manifest 无法解析 → 走有界修复路径（列 batches/）");
        }

        // 修复路径：**只在异常时走一次**，正常路径不列举 batches/。
        // 顺序只是"确定性的"（字典序），不保证写入顺序 —— 顺序权威始终是 manifest（v2 §A.2 #6）。
        var items = await _client.ListAsync($"{DevicesPath}/{device}/{BatchesFolder}", WebDavDepth.One, cancellationToken);

        return items
            .Where(r => !r.IsCollection && r.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Path[(r.Path.LastIndexOf('/') + 1)..])
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>cursor 指向的文件在列表中的下一位；找不到（外部改动过远端）则从头消费（幂等，安全）。</summary>
    private static int ResolveStartIndex(string device, WebDavCursor state, List<string> files)
    {
        if (!state.Devices.TryGetValue(device, out var consumed) || string.IsNullOrWhiteSpace(consumed))
        {
            return 0;
        }

        var index = files.IndexOf(consumed);
        if (index >= 0) return index + 1;

        AppLog.Warn($"[WebDAV] 游标指向的 batch 已不在设备 {device} 的清单里 → 从头消费（按 change_id/global_id 幂等）");
        return 0;
    }

    /// <summary>GET + 自校验一个 batch。任何不自洽都**抛**（绝不把坏数据当空数据）。</summary>
    private async Task<WebDavBatchFile> FetchAndValidateBatchAsync(
        string device, string file, CancellationToken cancellationToken)
    {
        var path = $"{DevicesPath}/{device}/{BatchesFolder}/{file}";

        var got = await _client.TryGetAsync(path, cancellationToken);
        if (got is null)
        {
            throw new WebDavProtocolException($"manifest 列出的 batch 文件不存在：{path}");
        }

        var batch = WebDavWire.TryDeserialize<WebDavBatchFile>(got.Content);
        if (batch is null)
        {
            throw new WebDavProtocolException($"batch 不是合法 JSON（不消费）：{path}");
        }

        if (batch.FileVersion != WebDavBatchFile.CurrentFileVersion)
        {
            throw new WebDavProtocolException($"不认识的 batch file_version={batch.FileVersion}：{path}");
        }

        if (batch.ProtocolVersion != DeviceSyncConstants.CurrentProtocolVersion)
        {
            throw new WebDavProtocolException(
                $"不兼容的 protocol_version={batch.ProtocolVersion}（本机 {DeviceSyncConstants.CurrentProtocolVersion}）：{path}");
        }

        // 自校验：batch_id 必须等于文件名 → 能发现"PUT 被截断 / 文件被外部改名"。
        var expectedId = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? file[..^5] : file;
        if (!string.Equals(batch.BatchId, expectedId, StringComparison.Ordinal))
        {
            throw new WebDavProtocolException($"batch_id 与文件名不符（疑似传输被截断）：{path}");
        }

        if (!string.IsNullOrEmpty(batch.DeviceId) && !string.Equals(batch.DeviceId, device, StringComparison.Ordinal))
        {
            throw new WebDavProtocolException($"batch 声明的写入设备与所在目录不符：{path}");
        }

        return batch;
    }

    // ==================== 映射 ====================

    private static string TombstoneKey(string entityType, string globalId) => $"{entityType}|{globalId}";

    /// <summary>登记墓碑，保留**最新**的删除时间（墓碑时间会随再次删除前进，绝不后退）。</summary>
    private void RememberTombstone(string entityType, string globalId, string? deletedAtUtc)
    {
        var key = TombstoneKey(entityType, globalId);
        var value = (deletedAtUtc ?? string.Empty).Trim();

        lock (_cacheLock)
        {
            if (_knownTombstones.TryGetValue(key, out var existing)
                && ParseUtc(existing) is { } known
                && ParseUtc(value) is { } candidate
                && candidate <= known)
            {
                return;
            }

            _knownTombstones[key] = value;
        }
    }

    /// <summary>
    /// <paramref name="changeAtUtc"/> 是否**晚于**墓碑时间。
    /// 任一侧缺失 / 无法解析 ⇒ false（保守：仍然拒绝推送，绝不放行"陈旧复活"）。
    /// 时间一律按 UTC 解析，因此 ISO+Z、带毫秒、无时区标记等写法都能正确比较。
    /// </summary>
    private static bool IsNewerThanTombstone(string? changeAtUtc, string? tombstonedAtUtc)
        => ParseUtc(changeAtUtc) is { } changeAt
           && ParseUtc(tombstonedAtUtc) is { } tombstonedAt
           && changeAt > tombstonedAt;

    private static DateTimeOffset? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
            | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private bool IsOwnPushedBatch(string file)
    {
        lock (_cacheLock) return _ownPushedBatches.Contains(file);
    }

    private static WebDavWireChange ToWire(SyncChange change) => new()
    {
        ChangeId = change.ChangeId,
        EntityType = change.EntityType,
        EntityGlobalId = change.EntityGlobalId,
        Operation = (int)change.Operation,
        BaseVersion = change.BaseVersion,
        DeviceId = change.DeviceId,
        CreatedAtUtc = change.CreatedAtUtc,
        Payload = change.Payload
    };

    private static SyncChange ToChange(WebDavWireChange wire, string device) => new()
    {
        ChangeId = wire.ChangeId,
        EntityType = wire.EntityType,
        EntityGlobalId = wire.EntityGlobalId,
        Operation = Enum.IsDefined(typeof(SyncOperation), wire.Operation)
            ? (SyncOperation)wire.Operation
            : SyncOperation.Create,
        BaseVersion = wire.BaseVersion,
        Payload = wire.Payload,
        DeviceId = string.IsNullOrWhiteSpace(wire.DeviceId) ? device : wire.DeviceId,
        CreatedAtUtc = wire.CreatedAtUtc
    };

    private static string? MaxCreatedAt(IReadOnlyList<SyncChange> changes)
    {
        var max = changes
            .Select(c => c.CreatedAtUtc)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .OrderBy(v => v, StringComparer.Ordinal)
            .LastOrDefault();

        return string.IsNullOrWhiteSpace(max) ? null : max;
    }
}
