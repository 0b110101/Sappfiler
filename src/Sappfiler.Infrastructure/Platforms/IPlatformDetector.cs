using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>统一的已安装游戏信息</summary>
public record InstalledGame(
    string Platform,       // "steam" | "epic" | "gog" | "ubisoft" | "ea" | "wegame" | "xbox" | "manual"
    string PlatformId,     // 平台内部 ID（Steam AppID / Epic CatalogId 等）
    string Name,           // 游戏名称
    string InstallDir,     // 安装目录（末尾不带斜杠）
    string? ExePath        // 主 exe 路径（部分平台可直接获得）
);

public interface IPlatformDetector
{
    string PlatformName { get; }
    bool IsInstalled();
    IReadOnlyList<InstalledGame> GetInstalledGames();
}
