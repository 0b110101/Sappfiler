using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// GOG Galaxy 平台检测器
/// 数据来源：HKLM\SOFTWARE\GOG.com\Games\*
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GogDetector : IPlatformDetector
{
    private const string GogRegistryPath = @"SOFTWARE\GOG.com\Games";

    public string PlatformName => "gog";

    /// <summary>
    /// 打开 GOG 注册表键，**两个视图都试**。
    /// </summary>
    /// <remarks>
    /// GOG Galaxy 是 32 位程序，在 64 位 Windows 上它写 HKLM\SOFTWARE\... 会被
    /// 重定向到 WOW6432Node\SOFTWARE\...；而本程序是 x64，直接读
    /// Registry.LocalMachine 拿到的是 64 位视图，**根本看不到 GOG 的游戏**。
    /// 症状与 Steam 那次 StateFlags 事故一样：主流平台游戏检测不到。
    /// （SteamDetector 早就循环了两个视图，WeGameDetector 也显式列了 WOW6432Node，
    /// 只有 GOG / Ubisoft 漏了。）
    /// </remarks>
    private static RegistryKey? OpenGogKey(bool writable = false)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                var key = baseKey.OpenSubKey(GogRegistryPath, writable);
                if (key is not null)
                {
                    baseKey.Dispose();
                    return key;
                }
                baseKey.Dispose();
            }
            catch { /* 该视图不可用时试下一个 */ }
        }
        return null;
    }

    public bool IsInstalled()
    {
        using var key = OpenGogKey();
        return key is not null;
    }

    public IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        using var gogKey = OpenGogKey();
        if (gogKey is null) return [];

        var games = new List<InstalledGame>();

        foreach (var gameId in gogKey.GetSubKeyNames())
        {
            try
            {
                using var gameKey = gogKey.OpenSubKey(gameId);
                if (gameKey is null) continue;

                var name       = gameKey.GetValue("gameName")?.ToString();
                var exePath    = gameKey.GetValue("exe")?.ToString();
                var installDir = gameKey.GetValue("path")?.ToString();
                var productId  = gameKey.GetValue("productId")?.ToString() ?? gameId;

                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) continue;

                games.Add(new InstalledGame(
                    Platform:   "gog",
                    PlatformId: productId,
                    Name:       name ?? "Unknown",
                    InstallDir: installDir.TrimEnd('\\', '/'),
                    ExePath:    File.Exists(exePath) ? exePath : null
                ));
            }
            catch { /* 跳过损坏的注册表项 */ }
        }

        return games;
    }
}
