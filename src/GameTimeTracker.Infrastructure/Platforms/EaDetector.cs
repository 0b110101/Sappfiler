using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// EA App（原 Origin）平台检测器
/// 数据来源：%ProgramData%\EA Desktop\InstallData\**\__Installer\installerdata.xml
/// 同时兼容旧版 Origin: %ProgramData%\Origin\LocalContent\
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EaDetector : IPlatformDetector
{
    private static readonly string EaAppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EA Desktop", "InstallData");

    private static readonly string OriginDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Origin", "LocalContent");

    public string PlatformName => "ea";

    public bool IsInstalled()
        => Directory.Exists(EaAppDataDir) || Directory.Exists(OriginDataDir);

    public IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        var games = new List<InstalledGame>();

        // EA App（新版）
        if (Directory.Exists(EaAppDataDir))
            games.AddRange(ScanDirectory(EaAppDataDir));

        // Origin（旧版兼容）
        if (Directory.Exists(OriginDataDir))
            games.AddRange(ScanDirectory(OriginDataDir));

        return games;
    }

    // ── 私有辅助 ────────────────────────────────────────────────────────────

    private static IEnumerable<InstalledGame> ScanDirectory(string root)
    {
        // 找所有 installerdata.xml（可能在 __Installer 子目录下）
        IEnumerable<string> xmlFiles;
        try
        {
            xmlFiles = Directory.EnumerateFiles(root, "installerdata.xml",
                SearchOption.AllDirectories);
        }
        catch { yield break; }

        foreach (var xmlPath in xmlFiles)
        {
            InstalledGame? game = null;
            try { game = ParseManifest(xmlPath); }
            catch { /* 跳过损坏的 XML */ }
            if (game is not null) yield return game;
        }
    }

    private static InstalledGame? ParseManifest(string xmlPath)
    {
        var doc = XDocument.Load(xmlPath);
        var ns  = doc.Root?.Name.Namespace ?? XNamespace.None;

        // 游戏名称
        var title = doc.Descendants(ns + "title").FirstOrDefault()?.Value
                 ?? doc.Descendants("title").FirstOrDefault()?.Value;

        // Content ID（EA 的游戏唯一标识）
        var contentId = doc.Descendants(ns + "contentID").FirstOrDefault()?.Value
                     ?? doc.Descendants("contentID").FirstOrDefault()?.Value;

        // 安装目录：从 launcher/baseDir 获取
        var baseDir = doc.Descendants(ns + "baseDir").FirstOrDefault()?.Value
                   ?? doc.Descendants("baseDir").FirstOrDefault()?.Value;

        // 主 exe
        var filePath = doc.Descendants(ns + "filePath").FirstOrDefault()?.Value
                    ?? doc.Descendants("filePath").FirstOrDefault()?.Value;

        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
            return null;

        var exePath = (!string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(baseDir))
            ? Path.Combine(baseDir, filePath)
            : null;

        return new InstalledGame(
            Platform:   "ea",
            PlatformId: contentId ?? Path.GetDirectoryName(xmlPath) ?? xmlPath,
            Name:       title ?? Path.GetFileName(baseDir.TrimEnd('\\', '/')),
            InstallDir: baseDir.TrimEnd('\\', '/'),
            ExePath:    File.Exists(exePath) ? exePath : null
        );
    }
}
