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

    public CoverCacheService(string? cacheDir = null, HttpClient? httpClient = null)
    {
        if (cacheDir != null)
        {
            _cacheDirectory = cacheDir;
        }
        else
        {
            // 封面缓存与数据库同处一个数据目录（默认 %LocalAppData%\Sappfiler，可自定义）
            _cacheDirectory = GameTimeTracker.Core.Services.AppPaths.CoversDir;
        }

        if (!Directory.Exists(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
        }

        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public string GetCoverPath(string platform, string platformId)
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

        return Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId}.png");
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

    public bool HasCover(string platform, string platformId)
    {
        var p = GetCoverPath(platform, platformId);
        return HasCoverFile(p);
    }

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

    public bool ExtractAndSaveExecutableIcon(string exePath, string platform, string platformId)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return false;
        }

        try
        {
            var safePlatform = platform.ToLowerInvariant();
            var safeId = string.Join("_", platformId.ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));
            var pngPath = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId}.png");

            // 已有封面就复用（必须幂等，否则会引发无限递归，见 HasCoverFile 注释）。
            // 若分辨率不足、且本次运行尚未尝试过升级，则继续往下走一次，尝试换成更大的图标。
            if (HasCoverFile(pngPath))
            {
                if (GetMinSide(pngPath) >= MinCrispSide || !_upgradeAttempted.Add(pngPath))
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
                                var altPath = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId.Substring(safePlatform.Length + 1)}.png");
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
                                var altPath = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId.Substring(safePlatform.Length + 1)}.png");
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
                        var altPath = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId.Substring(safePlatform.Length + 1)}.png");
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

    public async Task<string?> EnsureCoverAsync(string platform, string platformId, string? exePath = null)
    {
        var currentPath = GetCoverPath(platform, platformId);
        if (HasCoverFile(currentPath))
        {
            return currentPath;
        }

        // 1. Try extracting desktop icon or folder logo first
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            if (ExtractAndSaveExecutableIcon(exePath, platform, platformId))
            {
                return GetCoverPath(platform, platformId);
            }
        }

        // 2. Try Steam CDN if it's a Steam game
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

        return HasCoverFile(currentPath) ? currentPath : null;
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
                if (HasCover(g.Platform, g.PlatformId)) continue;

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

    /// <summary>下载图片到缓存目录（统一 .jpg 扩展名，WinUI 按内容解码不受扩展名影响）。失败返回 null。</summary>
    private async Task<string?> DownloadToCacheAsync(string platform, string platformId, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            var safePlatform = platform.ToLowerInvariant();
            var safeId = string.Join("_", platformId.ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(_cacheDirectory, $"{safePlatform}_{safeId}.jpg");

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
