using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTimeTracker.Infrastructure.Platforms;

/// <summary>
/// Epic Games 平台检测器
/// 数据来源：%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EpicDetector : IPlatformDetector
{
    private static readonly string ManifestDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public string PlatformName => "epic";

    public bool IsInstalled()
        => Directory.Exists(ManifestDir);

    public IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        if (!Directory.Exists(ManifestDir)) return [];

        var games = new List<InstalledGame>();
        var scan = new DetectorScanLog("epic");

        foreach (var itemFile in Directory.EnumerateFiles(ManifestDir, "*.item"))
        {
            scan.Seen();
            try
            {
                var json = File.ReadAllText(itemFile);
                var manifest = JsonSerializer.Deserialize<EpicManifest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (manifest is null) { scan.Skip("清单解析为空"); continue; }
                if (manifest.bIsIncompleteInstall) { scan.Skip("安装未完成"); continue; }
                if (string.IsNullOrEmpty(manifest.InstallLocation)) { scan.Skip("清单缺 InstallLocation"); continue; }
                if (!Directory.Exists(manifest.InstallLocation)) { scan.Skip("安装目录不存在"); continue; }

                var exePath = string.IsNullOrEmpty(manifest.LaunchExecutable)
                    ? null
                    : Path.Combine(manifest.InstallLocation, manifest.LaunchExecutable);

                games.Add(new InstalledGame(
                    Platform:   "epic",
                    PlatformId: manifest.CatalogItemId ?? manifest.AppName ?? itemFile,
                    Name:       manifest.DisplayName ?? manifest.AppName ?? "Unknown",
                    InstallDir: manifest.InstallLocation.TrimEnd('\\', '/'),
                    ExePath:    exePath
                ));
                scan.Kept();
            }
            catch { scan.Skip("清单解析失败"); }
        }

        scan.Report();
        return games;
    }

    // ── Epic manifest JSON 模型 ─────────────────────────────────────────────

    private sealed class EpicManifest
    {
        public string? AppName             { get; set; }
        public string? DisplayName         { get; set; }
        public string? CatalogItemId       { get; set; }
        public string? InstallLocation     { get; set; }
        public string? LaunchExecutable    { get; set; }
        public bool    bIsIncompleteInstall { get; set; }
    }
}
