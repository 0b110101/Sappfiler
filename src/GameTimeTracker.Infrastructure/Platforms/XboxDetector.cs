using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// Xbox / Microsoft Store 平台检测器
/// 数据来源：Windows.Management.Deployment.PackageManager（WinRT API）
///
/// 注意事项：
/// 1. 需要在 .csproj 中引用 Windows 10/11 SDK 投影（已由 Windows App SDK 提供）
/// 2. Game Pass PC Win32 游戏（非 UWP）的路径通过额外注册表键获取
/// 3. 纯 UWP 游戏的实际 exe 路径受 Windows 保护，只能匹配包目录
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class XboxDetector : IPlatformDetector
{
    public string PlatformName => "xbox";

    public bool IsInstalled()
    {
        // Xbox 是系统内置的，只要是 Windows 10+ 就算"安装"
        return Environment.OSVersion.Version.Major >= 10;
    }

    public IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        var games = new List<InstalledGame>();
        var scan = new DetectorScanLog("xbox");

        // 方式一：Win32 Game Pass 游戏（通过注册表）
        foreach (var g in GetWin32GamePassGames())
        {
            scan.Seen();
            scan.Kept();
            games.Add(g);
        }

        // 方式二：UWP 打包游戏（通过 PackageManager）
        foreach (var g in GetUwpGames())
        {
            scan.Seen();
            scan.Kept();
            games.Add(g);
        }

        scan.Report();
        return games;
    }

    // ── Win32 Game Pass 游戏 ────────────────────────────────────────────────

    /// <summary>
    /// Game Pass PC 的 Win32 游戏注册在 GamingServices 下
    /// 路径：HKLM\SOFTWARE\Microsoft\GamingServices\PackageRepository\Root
    /// 或直接扫描 XboxGames 目录（用户安装时选择的路径）
    /// </summary>
    private static IEnumerable<InstalledGame> GetWin32GamePassGames()
    {
        var commonDirs = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Xbox Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Xbox Games"),
        };

        // 检查所有盘符根目录下的 XboxGames / Xbox Games（如 E:\XboxGames）
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    var d1 = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                    if (Directory.Exists(d1)) commonDirs.Add(d1);
                    var d2 = Path.Combine(drive.RootDirectory.FullName, "Xbox Games");
                    if (Directory.Exists(d2)) commonDirs.Add(d2);
                }
            }
            catch { }
        }

        // 也检查用户自定义路径（通过 GamingServices 注册表）
        var customDir = GetXboxGamesInstallDir();
        if (!string.IsNullOrEmpty(customDir))
        {
            commonDirs.Add(customDir);
        }

        foreach (var dir in commonDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var gameDir in Directory.EnumerateDirectories(dir))
            {
                // 每个子目录是一个游戏，找 MicrosoftGame.config 获取元数据
                var configPath = Path.Combine(gameDir, "MicrosoftGame.config");
                if (File.Exists(configPath))
                {
                    var game = ParseMicrosoftGameConfig(configPath, gameDir);
                    if (game is not null) yield return game;
                }
                else
                {
                    // Fallback：用目录名作为游戏名
                    yield return new InstalledGame(
                        Platform:   "xbox",
                        PlatformId: Path.GetFileName(gameDir),
                        Name:       Path.GetFileName(gameDir),
                        InstallDir: gameDir.TrimEnd('\\', '/'),
                        ExePath:    null
                    );
                }
            }
        }
    }

    private static string? GetXboxGamesInstallDir()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\GamingServices",
                "GamesFolder", null) as string;
        }
        catch { return null; }
    }

    private static InstalledGame? ParseMicrosoftGameConfig(string configPath, string gameDir)
    {
        try
        {
            var doc  = System.Xml.Linq.XDocument.Load(configPath);
            var root = doc.Root;
            if (root is null) return null;

            // 游戏名
            var name = root.Attribute("DisplayName")?.Value
                    ?? root.Element("ShellVisuals")?.Attribute("DefaultDisplayName")?.Value
                    ?? Path.GetFileName(gameDir);

            // 包名 / ID
            var identity = root.Element("Identity");
            var packageId = identity?.Attribute("Name")?.Value ?? Path.GetFileName(gameDir);

            // 主 exe
            var exeFile = root.Descendants("Executable")
                .FirstOrDefault()?.Attribute("Name")?.Value;
            var exePath = exeFile is not null
                ? Path.Combine(gameDir, exeFile)
                : null;

            return new InstalledGame(
                Platform:   "xbox",
                PlatformId: packageId,
                Name:       name,
                InstallDir: gameDir.TrimEnd('\\', '/'),
                ExePath:    File.Exists(exePath) ? exePath : null
            );
        }
        catch { return null; }
    }

    // ── UWP 打包游戏 ────────────────────────────────────────────────────────

    /// <summary>
    /// 通过 WinRT PackageManager 枚举 UWP 游戏
    /// 需要 Windows App SDK 或手动引用 Windows.winmd
    /// </summary>
    private static IEnumerable<InstalledGame> GetUwpGames()
    {
        // 使用 WinRT 投影（Windows App SDK 提供）
        // 如果编译报错，确保 .csproj 中有：
        //   <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
        Windows.Management.Deployment.PackageManager pm;
        try
        {
            pm = new Windows.Management.Deployment.PackageManager();
        }
        catch { yield break; }

        IEnumerable<Windows.ApplicationModel.Package> packages;
        try
        {
            packages = pm.FindPackagesForUser(string.Empty)
                         .Where(p => !p.IsFramework && !p.IsResourcePackage);
        }
        catch { yield break; }

        foreach (var pkg in packages)
        {
            string? installDir = null;
            string? displayName = null;

            try
            {
                installDir  = pkg.InstalledLocation?.Path;
                displayName = pkg.DisplayName;
            }
            catch { continue; }

            if (string.IsNullOrEmpty(installDir)) continue;

            // 通过 AppxManifest.xml 判断是否是游戏类别
            var manifestPath = Path.Combine(installDir, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) continue;

            if (!IsGamePackage(manifestPath)) continue;

            yield return new InstalledGame(
                Platform:   "xbox",
                PlatformId: pkg.Id.FamilyName,
                Name:       displayName ?? pkg.Id.Name,
                InstallDir: installDir.TrimEnd('\\', '/'),
                ExePath:    null   // UWP exe 受保护，通过目录匹配
            );
        }
    }

    private static bool IsGamePackage(string manifestPath)
    {
        try
        {
            var content = File.ReadAllText(manifestPath);
            // Xbox Game Pass 游戏通常在 AppxManifest 中有 "Game" 分类
            return content.Contains("\"Game\"", StringComparison.OrdinalIgnoreCase)
                || content.Contains(">Game<", StringComparison.OrdinalIgnoreCase)
                || content.Contains("xbox.com", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
