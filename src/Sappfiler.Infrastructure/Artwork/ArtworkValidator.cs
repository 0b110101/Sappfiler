using System;
using System.Drawing;
using System.IO;
using System.Runtime.Versioning;

namespace GameTimeTracker.Infrastructure.Artwork;

[SupportedOSPlatform("windows")]
public static class ArtworkValidator
{
    public const int MinFileSizeBytes = 2048; // 过滤 0KB、空白或破损文件
    public const int MinWidth = 200;
    public const int MinHeight = 100;
    public const int MaxAllowedWidth = 2560;  // 严格杜绝 3840×1240 等 4K LibraryHero 大图
    public const int MaxAllowedHeight = 1600;

    /// <summary>
    /// 校验本地图片文件是否真实有效、能够解码，且尺寸满足卡片横版背景需求。
    /// 排除破损文件、0KB 临时文件以及超大 4K 原图。
    /// </summary>
    public static bool TryValidateImage(string filePath, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length < MinFileSizeBytes)
                return false;

            // 严禁将 3840x1240 这种大图拉入内存：若文件名包含 hero 且体积很大直接跳过
            var fileName = Path.GetFileName(filePath).ToLowerInvariant();
            if (fileName.Contains("hero") && !fileName.Contains("blur") && info.Length > 800 * 1024)
            {
                return false;
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var img = Image.FromStream(fs, false, false);

            width = img.Width;
            height = img.Height;

            // 尺寸校验：横版背景图至少 200×100，且严禁超过 2560×1600（彻底排除 3840×1240 的 LibraryHero）
            if (width < MinWidth || height < MinHeight)
                return false;

            if (width > MaxAllowedWidth || height > MaxAllowedHeight)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }
}
