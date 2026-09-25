using System.Text;

namespace GameTimeTracker.Core.Services;

/// <summary>
/// 数据存档位置解析。
/// 默认 %LocalAppData%\Sappfiler（不易被误删、不随解压目录丢失）；
/// 用户可在设置页更改位置——更改结果写入引导文件，重启后生效。
/// 引导文件固定放在默认位置（它自己不能跟着自定义位置走，否则找不到）。
/// </summary>
public static class AppPaths
{
    private const string BootstrapFileName = "data_location.txt";

    private static readonly string LegacyDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameTimeTracker");

    private static readonly string SappfilerDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sappfiler");

    public static string DefaultDataDir
    {
        get
        {
            if (Directory.Exists(SappfilerDataDir)) return SappfilerDataDir;
            if (Directory.Exists(LegacyDataDir))
            {
                try
                {
                    Directory.Move(LegacyDataDir, SappfilerDataDir);
                    return SappfilerDataDir;
                }
                catch
                {
                    return LegacyDataDir;
                }
            }
            return SappfilerDataDir;
        }
    }

    public static string BootstrapFile => Path.Combine(DefaultDataDir, BootstrapFileName);

    /// <summary>数据库路径。环境变量 GAMETIME_DB_PATH 优先（多实例/测试用），其次自定义位置，最后默认位置。</summary>
    public static string DbPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("GAMETIME_DB_PATH");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(DataDir, "gametime.db");
        }
    }

    public static string CoversDir => Path.Combine(DataDir, "cache", "covers");
    public static string ArtworkCacheDir => Path.Combine(DataDir, "cache", "artwork");

    /// <summary>当前生效的数据目录（引导文件 → 默认）。</summary>
    public static string DataDir { get; } = ResolveDataDir(ReadBootstrapFile());

    /// <summary>纯函数，便于单测：引导值 → 实际数据目录。</summary>
    public static string ResolveDataDir(string? customDir)
    {
        if (!string.IsNullOrWhiteSpace(customDir) && Directory.Exists(customDir.Trim()))
            return customDir.Trim();
        return DefaultDataDir;
    }

    public static string? ReadBootstrapFile()
    {
        try
        {
            if (!File.Exists(BootstrapFile)) return null;
            var line = File.ReadAllLines(BootstrapFile, Encoding.UTF8).FirstOrDefault();
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写入自定义数据目录引导文件（目录不存在则创建默认目录以承载引导文件）。</summary>
    public static void SetCustomDataDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空", nameof(path));
        Directory.CreateDirectory(DefaultDataDir);
        File.WriteAllText(BootstrapFile, path.Trim(), Encoding.UTF8);
    }

    public static void ClearCustomDataDir()
    {
        try { File.Delete(BootstrapFile); } catch { }
    }
}
