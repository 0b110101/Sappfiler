namespace GameTimeTracker.Infrastructure.Covers;

public class CoverCacheService
{
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;

    public event EventHandler<string>? CoverDownloaded; // passes platform_id

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
                                                   IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>低于该边长视为封面不够清晰，值得尝试升级一次。</summary>
    private const int MinCrispSide = 64;

    /// <summary>
    /// 本进程内已经尝试过「把已有小封面升级为高清」的路径。
    /// 用它把升级动作限制为每次运行每款游戏一次，避免反复提取造成 CPU/磁盘抖动。
    /// </summary>
    private readonly HashSet<string> _upgradeAttempted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v4：受信（本体裁决 PRIMARY）下允许"覆盖重写一次"的 exe 路径（每个路径最多一次）。</summary>
    private readonly HashSet<string> _extractUpgradeUsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取图片最短边；失败返回 0。</summary>
    private static int GetMinSide(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var img = System.Drawing.Image.FromStream(fs, false, false);
            return Math.Min(img.Width, img.Height);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 从 exe 资源里提取最大的图标。
    /// System.Drawing.Icon.ExtractAssociatedIcon 只会返回 32×32（系统小图标尺寸），
    /// 铺到 88px 的 Hero 卡片上明显发糊，因此这里用 PrivateExtractIcons 由大到小请求。
    /// </summary>
    private static System.Drawing.Bitmap? ExtractLargestIcon(string exePath)
    {
        foreach (var size in new[] { 256, 128, 96, 64, 48 })
        {
            var handles = new IntPtr[1];
            var ids = new uint[1];
            uint got;
            try
            {
                got = PrivateExtractIcons(exePath, 0, size, size, handles, ids, 1, 0);
            }
            catch
            {
                continue;
            }

            if (got == 0 || handles[0] == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                using var icon = System.Drawing.Icon.FromHandle(handles[0]);
                var bmp = icon.ToBitmap();
                if (bmp.Width >= 48 && bmp.Height >= 48)
                {
                    return bmp;
                }
                bmp.Dispose();
            }
            catch
            {
                // 换下一个尺寸继续试
            }
            finally
            {
                DestroyIcon(handles[0]);
            }
        }
        return null;
    }

    /// <summary>
    /// **封面缓存版本**。算法一变就 +1，缓存根目录随之变成 <c>…\covers\v{N}\</c>，旧目录自然失效。
    ///
    /// <para>
    /// 为什么必须有（2026-10-05 用户提出）：Sappfiler 已是长期运行的工具，封面来源与优先级会持续演进
    ///（exe 图标 / 目录 logo / 总表 icon / cover / Steam CDN …）。没有版本号时，每修一次封面逻辑
    /// 都要让用户手工删 <c>%LocalAppData%\Sappfiler\cache\covers</c>，否则错误封面会一直留着；
    /// 有了版本号，改版本 = 全局失效，且**不删除任何旧文件**（旧目录原地保留，只是不再被读取）。
    /// </para>
    ///
    /// <para>v2：exe 图标降级为**占位图**（不再覆盖总表/Steam 正规封面），并排除反作弊/启动器组件。</para>
    ///
    /// <para>v3（2026-10-07 产品取向变更 —— "本地优先"）：把**远端/历史来源**的封面挪进独立槽位
    /// <c>.history.jpg</c>，显示优先级变为 <c>.exeicon.png</c>（本地已发现）→ <c>.png/.jpg</c>（本地正规）
    /// → <c>.history.jpg</c>（Notion 总表历史 / Steam CDN）→ 默认占位。修复 Halo 这类
    /// "本地 EXE 图标已成功提取、却永远被远端封面压住"的问题。</para>
    ///
    /// <para>v4（2026-10-07 本体裁决 C4 安全气囊）：v3 把 <c>.exeicon.png</c> 提到首位后，
    /// 暴露出"目录里任意 exe 都能被抠图"的旧问题（Halo 实测抠出了 EasyAntiCheat 图标）。
    /// 本版：① 抠图前做一次 T5/T4 否决（未受信调用者）② 允许"仅一次"的受信升级重写
    /// ③ 旧 v3 目录（含错误图标）整体失效、**不删除**。</para>
    /// </summary>
    public const int CacheVersion = 4;

    public CoverCacheService(string? cacheDir = null, HttpClient? httpClient = null)
    {
        if (cacheDir != null)
        {
            // 显式传入的目录（测试用）保持原样，不追加版本号
            _cacheDirectory = cacheDir;
        }
        else
        {
            // 封面缓存与数据库同处一个数据目录（默认 %LocalAppData%\Sappfiler，可自定义）
            _cacheDirectory = Path.Combine(GameTimeTracker.Core.Services.AppPaths.CoversDir, $"v{CacheVersion}");
        }

        if (!Directory.Exists(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
        }

        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// **显示用**封面路径。优先级 —— "本地优先"（2026-10-07 v3 起，见 <see cref="CacheVersion"/> 注释）：
    /// <code>
    /// ① 本地已发现的游戏图标（exe / 游戏安装目录 logo → .exeicon.png）
    ///     ↓ 没有
    /// ② 本地正规封面（.png / .jpg，本地产物）
    ///     ↓ 没有
    /// ③ 历史 / 远端封面（Notion 总表历史 icon/cover、Steam CDN → .history.jpg）
    ///     ↓ 没有
    /// ④ 该游戏"应当"写在哪的默认路径（可能还不存在 → UI 走无封面分支）
    /// </code>
    ///
    /// ⚠️ 为什么①在②之前：只有**通过了生态组件过滤**（<see cref="ExtractAndSaveExecutableIcon"/> 里的
    /// <c>ProcessFilter.IsEcosystemComponent</c>）的真实游戏图标才可能写成 <c>.exeicon.png</c>，
    /// 所以"本地发现优先"不会把 EasyAntiCheat / BattlEye 之类的 logo 放进来。
    ///
    /// ⚠️ 旧行为（v2）是"正规封面永远赢"，结果是：远端种子一旦写入 <c>.jpg</c>，
    /// 本地已成功提取的 exe 图标就永远显示不出来（Halo MCC 实测）。
    /// </summary>
    public string GetCoverPath(string platform, string platformId)
        => FindPlaceholderIcon(platform, platformId)
           ?? FindRealCover(platform, platformId)
           ?? FindHistoryCover(platform, platformId)
           ?? DefaultRealCoverPath(platform, platformId);

    private static (string SafePlatform, string SafeId) Normalize(string platform, string platformId)
        => (platform.ToLowerInvariant(),
            string.Join("_", platformId.ToLowerInvariant().Split(Path.GetInvalidFileNameChars())));

    /// <summary>该游戏"应当"写正规封面的路径（不一定已存在）。</summary>
    private string DefaultRealCoverPath(string platform, string platformId)
    {
        var (safePlatform, safeId) = Normalize(platform, platformId);
        return Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId}.png");
    }

    /// <summary>
    /// **正规封面**（总表 icon / cover / Steam 官方封面）的现存文件；没有则 null。
    /// 这里的查找顺序与扩展名兼容规则**与修复前逐字一致**，只是不再兜底返回不存在的路径。
    /// </summary>
    private string? FindRealCover(string platform, string platformId)
    {
        var safePlatform = platform.ToLowerInvariant();
        var safeId = string.Join("_", platformId.ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));

        var dirs = new List<string> { _cacheDirectory };        foreach (var dir in dirs)
        {
            // 1. Direct standard format: platform_id.png / .jpg
            var pngPath = Path.Combine(dir, $"{safePlatform}_{safeId}.png");
            if (File.Exists(pngPath) && new FileInfo(pngPath).Length > 0) return pngPath;
            var jpgPath = Path.Combine(dir, $"{safePlatform}_{safeId}.jpg");
            if (File.Exists(jpgPath) && new FileInfo(jpgPath).Length > 0) return jpgPath;

            // 2. If id already starts with platform (e.g. platformId="xbox_heroes...", avoid double prefix)
            if (safeId.StartsWith(safePlatform + "_"))
            {
                var strippedId = safeId.Substring(safePlatform.Length + 1);
                var strippedPng = Path.Combine(dir, $"{safePlatform}_{strippedId}.png");
                if (File.Exists(strippedPng) && new FileInfo(strippedPng).Length > 0) return strippedPng;
                var strippedJpg = Path.Combine(dir, $"{safePlatform}_{strippedId}.jpg");
                if (File.Exists(strippedJpg) && new FileInfo(strippedJpg).Length > 0) return strippedJpg;

                var rawPng = Path.Combine(dir, $"{safeId}.png");
                if (File.Exists(rawPng) && new FileInfo(rawPng).Length > 0) return rawPng;
            }
            else
            {
                // 3. What if cached as double prefix: platform_platform_id.png
                var doublePng = Path.Combine(dir, $"{safePlatform}_{safePlatform}_{safeId}.png");
                if (File.Exists(doublePng) && new FileInfo(doublePng).Length > 0) return doublePng;
                var doubleJpg = Path.Combine(dir, $"{safePlatform}_{safePlatform}_{safeId}.jpg");
                if (File.Exists(doubleJpg) && new FileInfo(doubleJpg).Length > 0) return doubleJpg;
            }

            // 4. Fallback for previous casing on disk
            var rawId = string.Join("_", platformId.Split(Path.GetInvalidFileNameChars()));
            var legacyPng = Path.Combine(dir, $"{platform}_{rawId}.png");
            if (File.Exists(legacyPng) && new FileInfo(legacyPng).Length > 0) return legacyPng;
        }

        return null;
    }

    /// <summary>
    /// **历史 / 远端封面**（Notion 总表历史 icon/cover、Steam CDN）的现存文件；没有则 null。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="FindRealCover"/> 不同，这里**只认一个规范文件名**：
    /// <c>{platform}_{id}.history.jpg</c> —— 因为历史槽全由
    /// <see cref="DownloadToCacheAsync"/> 用同一套 <c>Normalize</c> 规则写出，
    /// 不需要（也不应该）兼容"去前缀 / 双前缀 / 旧大小写"等历史变体。
    /// </remarks>
    private string? FindHistoryCover(string platform, string platformId)
    {
        var (safePlatform, safeId) = Normalize(platform, platformId);
        var path = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId}.history.jpg");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    /// <summary>占位图（exe 图标 / 目录 logo，<c>*.exeicon.png</c>）的现存文件；没有则 null。</summary>
    private string? FindPlaceholderIcon(string platform, string platformId)
    {
        var (safePlatform, safeId) = Normalize(platform, platformId);

        var candidates = new List<string> { Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId}.exeicon.png") };

        // 与写入侧保持一致：platformId 自带平台前缀时还会写一份"去掉前缀"的副本
        if (safeId.StartsWith(safePlatform + "_"))
        {
            var strippedId = safeId.Substring(safePlatform.Length + 1);
            candidates.Add(Path.Combine(_cacheDirectory, $"{safePlatform}_{strippedId}.exeicon.png"));
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        }

        return null;
    }

    public string? GetSplashBackgroundPath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
        try
        {
            var exeDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(exeDir))
            {
                var candidateDirs = new List<string> { exeDir };
                var parent = Directory.GetParent(exeDir)?.FullName;
                if (!string.IsNullOrEmpty(parent))
                {
                    candidateDirs.Add(parent);
                    var contentSub = Path.Combine(parent, "Content");
                    if (Directory.Exists(contentSub)) candidateDirs.Add(contentSub);
                }

                string[] splashNames = {
                    "SplashScreenImage.png",
                    "SplashScreen.png",
                    "Wide310x150Logo.png",
                    "WideLogo.png",
                    "hero.png",
                    "hero.jpg",
                    "header.jpg",
                    "background.png",
                    "background.jpg"
                };

                foreach (var dir in candidateDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var sName in splashNames)
                    {
                        var sPath = Path.Combine(dir, sName);
                        if (File.Exists(sPath) && new FileInfo(sPath).Length > 2000)
                        {
                            return sPath;
                        }
                    }

                    var splashes = Directory.GetFiles(dir, "*Splash*.png");
                    if (splashes.Length > 0)
                    {
                        var best = splashes.OrderByDescending(f => new FileInfo(f).Length).First();
                        if (new FileInfo(best).Length > 2000) return best;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 是否已有**本地可显示的封面**（① 本地已发现的图标 <c>.exeicon.png</c> 或 ② 本地正规封面 <c>.png/.jpg</c>）。
    /// </summary>
    /// <remarks>
    /// <see cref="EnsureLibraryCoversAsync"/> 用它决定"要不要去取历史/远端封面"：
    /// **只要本地已经有可显示的图，就完全不去取远端**（= "本地优先"，2026-10-07 产品取向）。
    ///
    /// ⚠️ 不把 exe 图标算作"有封面"是 v2 的语义，已被上述产品取向取代：
    /// 那时的目标是"exe 图标不许盖过正经封面"，结果却变成**无条件**去拉远端封面并写进正规槽位，
    /// 于是本地已提取的 exe 图标永远显示不出来（Halo MCC 实测）。
    /// 安全性不受影响：<c>.exeicon.png</c> 只有在通过生态组件过滤后才会存在。
    /// </remarks>
    public bool HasCover(string platform, string platformId)
        => HasCoverFile(FindPlaceholderIcon(platform, platformId))
           || HasCoverFile(FindRealCover(platform, platformId));

    /// <summary>
    /// 是否已有**任何可显示的封面文件**（本地三档里任意一档存在）。用于"还需要不需要去下载封面"的判定。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="HasCover"/> 的区别：历史槽（<c>.history.jpg</c>）也算。
    /// 编排器用它做守卫 ⇒ 一旦已经有东西可显示（哪怕只是历史兜底），就**不会每轮同步重复下载**。
    /// </remarks>
    public bool HasAnyDisplayableCover(string platform, string platformId)
        => HasCoverFile(GetCoverPath(platform, platformId));

    /// <summary>
    /// 判断本地是否已存在可用的封面文件。
    ///
    /// 【重要】这里刻意不再使用「字节数 &gt; 1500」这种门槛。
    /// 从 exe 提取出的图标 PNG 常常只有 1~3 KB（例如 1330 字节），
    /// 用字节数当门槛会导致「已存在」判定永远不成立，于是每次调用都重新提取。
    /// 而提取流程会【同步】触发 CoverDownloaded 事件，事件处理里又会走一遍
    /// 数据刷新 → 提取 → 事件，构成无限递归，最终栈溢出直接崩溃进程。
    /// </summary>
    private static bool HasCoverFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 0;

    /// <param name="trusted">
    /// 该 exe 是否已由**本体裁决**（<c>GameLibraryManager.BeginScanTick</c>）认定为 PRIMARY。
    /// 只有受信调用者才允许绕过 T4（启动器角色）否决、并触发一次升级重写；
    /// **T5（反作弊/系统组件/旁路目录）任何情况都不允许**。
    /// </param>
    public bool ExtractAndSaveExecutableIcon(string exePath, string platform, string platformId, bool trusted = false)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return false;
        }

        // ⚠️ 防御层（与 GameLibraryManager 的守卫同源）：反作弊 / 启动器 / 辅助进程的 exe
        // **永远不作为封面来源**。这类 exe 的 logo 与游戏毫无关系，而它们偏偏常常住在游戏目录里
        //（Halo MCC 实测：显示成了 Easy Anti-Cheat 的图标）。判据复用 ProcessFilter，避免两处漂移。
        if (GameTimeTracker.Infrastructure.Process.ProcessFilter.IsEcosystemComponent(exePath))
        {
            return false;
        }

        // 🛡️ v4 安全气囊（只行使**否决权**，不复制本体裁决算法）：
        //   · T5（旁路目录等）—— 不论是否受信，一律不抠图；
        //   · T4（启动器/安装器/辅助角色）—— 仅**未受信**调用者被拒；
        //     受信调用者说明本体裁决已确认它是 PRIMARY（例如"本体就叫 XxxLauncher.exe"的合法情形）。
        if (Platforms.PrimaryExeResolver.IsDenied(exePath)) return false;
        if (!trusted && Platforms.PrimaryExeResolver.IsDowngradedRole(exePath)) return false;

        try
        {
            var safePlatform = platform.ToLowerInvariant();
            var safeId = string.Join("_", platformId.ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));

            // ⚠️ 写的是 **占位图**（`.exeicon.png`），不是正规封面（`.png`/`.jpg`）：
            // exe 图标只配当"没有正经封面时的兜底"，绝不能盖过总表 icon / Steam 官方封面。
            var pngPath = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId}.exeicon.png");

            // 已有封面就复用（必须幂等，否则会引发无限递归，见 HasCoverFile 注释）。
            // 若分辨率不足、且本次运行尚未尝试过升级，则继续往下走一次，尝试换成更大的图标。
            if (HasCoverFile(pngPath))
            {
                // v4：受信（本体裁决已确认 PRIMARY）时，允许**覆盖重写一次** —— 用于纠正
                // 早期由未受信路径写入的错误图标（每个 exe 路径最多一次）。
                var trustedUpgrade = trusted && _extractUpgradeUsed.Add(pngPath);
                if (!trustedUpgrade && (GetMinSide(pngPath) >= MinCrispSide || !_upgradeAttempted.Add(pngPath)))
                {
                    return true;
                }
            }

            // 1. Check directory for high-res Xbox / UWP / Steam package logos
            var exeDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(exeDir))
            {
                var candidateDirs = new List<string> { exeDir };
                var parent = Directory.GetParent(exeDir)?.FullName;
                if (!string.IsNullOrEmpty(parent))
                {
                    candidateDirs.Add(parent);
                    var contentSub = Path.Combine(parent, "Content");
                    if (Directory.Exists(contentSub)) candidateDirs.Add(contentSub);
                }

                string[] logoNames = {
                    "Square480x480Logo.png",
                    "Square150x150Logo.png",
                    "Square44x44Logo.targetsize-256.png",
                    "Square44x44Logo.targetsize-256_altform-unplated.png",
                    "StoreLogo.png",
                    "Square44x44Logo.targetsize-64.png",
                    "Square44x44Logo.png",
                    "Logo.png",
                    "icon.png"
                };

                foreach (var dir in candidateDirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    foreach (var logoName in logoNames)
                    {
                        var logoFile = Path.Combine(dir, logoName);
                        if (File.Exists(logoFile) && new FileInfo(logoFile).Length > 1500)
                        {
                            File.Copy(logoFile, pngPath, true);
                            if (safeId.StartsWith(safePlatform + "_"))
                            {
                                var altPath = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId.Substring(safePlatform.Length + 1)}.exeicon.png");
                                try { File.Copy(logoFile, altPath, true); } catch { }
                            }
                            CoverDownloaded?.Invoke(this, platformId);
                            return true;
                        }
                    }

                    // Fallback to any *Logo*.png file
                    var allLogos = Directory.GetFiles(dir, "*Logo*.png");
                    if (allLogos.Length > 0)
                    {
                        var bestLogo = allLogos.OrderByDescending(f => new FileInfo(f).Length).First();
                        if (new FileInfo(bestLogo).Length > 1500)
                        {
                            File.Copy(bestLogo, pngPath, true);
                            if (safeId.StartsWith(safePlatform + "_"))
                            {
                                var altPath = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId.Substring(safePlatform.Length + 1)}.exeicon.png");
                                try { File.Copy(bestLogo, altPath, true); } catch { }
                            }
                            CoverDownloaded?.Invoke(this, platformId);
                            return true;
                        }
                    }
                }
            }

            // 2. 从 exe 资源取最大的图标（优先 256×256，其次 128/96/64/48）
            var bitmap = ExtractLargestIcon(exePath);

            if (bitmap == null)
            {
                // 兜底：该 exe 确实没有可用图标时，退回系统关联图标（32×32）
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null)
                    {
                        bitmap = icon.ToBitmap();
                    }
                }
                catch
                {
                    // 忽略，交由下面统一判断
                }
            }

            if (bitmap != null)
            {
                using (bitmap)
                {
                    bitmap.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                    if (safeId.StartsWith(safePlatform + "_"))
                    {
                        // ⚠️ 必须写 **占位槽**（.exeicon.png）：这里是从 exe 抠出来的图标，
                        //    若写成 {platform}_{去前缀id}.png 就会落进"正规封面"槽、越权参与优先级判定
                        //    （与 FindPlaceholderIcon 的查找规则 = 上面两处一致）。
                        var altPath = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId.Substring(safePlatform.Length + 1)}.exeicon.png");
                        try { bitmap.Save(altPath, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                    }
                }
                CoverDownloaded?.Invoke(this, platformId);
                return true;
            }
        }
        catch
        {
            // Silent fallback
        }

        return false;
    }

    public async Task<string?> EnsureCoverAsync(string platform, string platformId, string? exePath = null, bool trusted = false)
    {
        // 本地已有可显示封面（① 本地已发现的图标 / ② 本地正规封面）→ 直接返回，
        // **不再**去取远端（"本地优先"，见 HasCover 注释）。
        if (FindPlaceholderIcon(platform, platformId) is { } localIcon) return localIcon;
        if (FindRealCover(platform, platformId) is { } localCover) return localCover;

        // ③ 历史槽已有 → 说明之前兜底过，直接用，避免每轮重复下载远端封面。
        if (FindHistoryCover(platform, platformId) is { } historyCover) return historyCover;

        // 1. 先落一张**占位图**（exe 图标 / 目录 logo）：有它至少不是空白，
        //    但它不会被当成"已有封面"，所以下面仍会继续去取正经封面。
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            ExtractAndSaveExecutableIcon(exePath, platform, platformId, trusted);
        }

        // 2. 正规封面：Steam 官方 CDN（steam + 纯数字 AppID）
        if (string.Equals(platform, "steam", StringComparison.OrdinalIgnoreCase) && platformId.All(char.IsDigit))
        {
            var jpg = await DownloadToCacheAsync(platform, platformId,
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{platformId}/header.jpg");
            if (jpg != null)
            {
                CoverDownloaded?.Invoke(this, platformId);
                return jpg;
            }
        }

        // 3. 本地与远端都拿不到 → 退回历史槽（有就显示，没有就 null，让 UI 走无封面分支）
        return FindHistoryCover(platform, platformId);
    }

    /// <summary>
    /// 库级封面补全：为本地没有 exe 图标可提取的游戏（Notion 导入、手动记录等）拉取封面。
    /// 优先级：总表 cover_url → Steam 官方 CDN（steam + 纯数字 AppID）。
    /// 幂等：已有缓存的直接跳过，可放心在每轮同步后调用。
    /// </summary>
    public async Task EnsureLibraryCoversAsync(GameTimeTracker.Core.Interfaces.IDatabaseRepository repo)
    {
        try
        {
            var games = await repo.GetAllGamesAsync();
            var catalog = await repo.GetCatalogItemsAsync();
            var catByPageId = catalog
                .Where(c => !string.IsNullOrWhiteSpace(c.PageId))
                .GroupBy(c => c.PageId.Replace("-", "", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var g in games)
            {
                if (string.Equals(g.Status, "ignored", StringComparison.OrdinalIgnoreCase)) continue;
                // 已有任何可显示封面（本地图标 / 本地正规 / 历史兜底）→ 无需再取远端。
                // ⚠️ 2026-10-07 v3：这里以前用 HasCover（只看正规封面）⇒ 本地已提取的 exe 图标
                // 从不算数，于是每轮都会把远端封面写进 .jpg 并压住本地图标（Halo 实测）。
                if (HasAnyDisplayableCover(g.Platform, g.PlatformId)) continue;

                // 封面优先级：总表页面 icon（正方形，最合适）→ cover（横幅，凑合）→ Steam CDN
                string? iconUrl = null, coverUrl = null;
                if (!string.IsNullOrWhiteSpace(g.NotionPageId) &&
                    catByPageId.TryGetValue(g.NotionPageId.Replace("-", "", StringComparison.OrdinalIgnoreCase), out var cat))
                {
                    iconUrl = cat.IconUrl;
                    coverUrl = cat.CoverUrl;
                }

                var saved = await DownloadToCacheAsync(g.Platform, g.PlatformId,
                    !string.IsNullOrWhiteSpace(iconUrl) ? iconUrl : coverUrl);
                if (saved != null)
                {
                    CoverDownloaded?.Invoke(this, g.PlatformId);
                    continue;
                }

                // 总表没给可用图片：steam 游戏直接用官方 CDN
                if (string.Equals(g.Platform, "steam", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(g.PlatformId) && g.PlatformId.All(char.IsDigit))
                {
                    await EnsureCoverAsync(g.Platform, g.PlatformId);
                }
            }
        }
        catch (Exception ex)
        {
            GameTimeTracker.Infrastructure.AppLog.Warn($"库级封面补全失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 下载**历史 / 远端**封面到缓存目录，写入独立槽位 <c>{platform}_{id}.history.jpg</c>
    /// （统一 .jpg 扩展名，WinUI 按内容解码不受扩展名影响）。失败返回 null。
    /// </summary>
    /// <remarks>
    /// 本方法只被**远端来源**调用（Steam CDN、Notion 总表 icon/cover），
    /// 因此绝不能写进 <c>.jpg</c>——那会让远端图冒充"本地正规封面"、压住本地已发现的图标
    /// （2026-10-07 v3 的产品取向："远端/历史只能兜底"）。
    /// </remarks>
    private async Task<string?> DownloadToCacheAsync(string platform, string platformId, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            var safePlatform = platform.ToLowerInvariant();
            var safeId = string.Join("_", platformId.ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId}.history.jpg");

            var bytes = await _httpClient.GetByteArrayAsync(url);
            if (bytes == null || bytes.Length == 0) return null;

            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
