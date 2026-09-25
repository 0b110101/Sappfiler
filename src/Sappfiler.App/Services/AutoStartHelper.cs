using System.IO;
using Microsoft.Win32;
using GameTimeTracker.Infrastructure;

namespace GameTimeTracker.App.Services;

/// <summary>
/// Windows 开机自启管理服务（基于 HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。
/// 普通用户权限即可读写，无需管理员提权。
/// 支持从旧版 GameTimeTracker 自动静默无感迁移至 Sappfiler。
/// </summary>
public static class AutoStartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Sappfiler";
    private const string LegacyAppName = "GameTimeTracker";

    /// <summary>
    /// 获取当前应用可执行文件真实完整路径（兼容 dotnet run 与独立发布产物）。
    /// </summary>
    public static string? GetExecutablePath()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(exePath)) return null;

        var fileName = Path.GetFileName(exePath);
        // 单测运行环境不解析
        if (fileName.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 如果处于 dotnet run 调试环境（dotnet.exe），尝试定位同目录输出的 Sappfiler.exe
        if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "Sappfiler.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return exePath;
    }

    /// <summary>
    /// 检查当前是否已开启开机自启。
    /// 若发现旧版残留或路径漂移，顺便自动同步矫正。
    /// </summary>
    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return false;

            var currentVal = key.GetValue(AppName) as string;
            var legacyVal = key.GetValue(LegacyAppName) as string;

            var hasAny = !string.IsNullOrWhiteSpace(currentVal) || !string.IsNullOrWhiteSpace(legacyVal);
            if (hasAny)
            {
                // 如果发现旧项存在，或者注册路径与当前 exe 不一致，执行静默矫正
                if (!string.IsNullOrWhiteSpace(legacyVal) || NeedsPathUpdate(currentVal))
                {
                    SyncAutoStartRegistration();
                }
            }

            return hasAny;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[自启动] 检查开机自启状态失败: {ex.Message}");
            return false;
        }
    }

    private static bool NeedsPathUpdate(string? registeredCmd)
    {
        if (string.IsNullOrWhiteSpace(registeredCmd)) return true;
        var exe = GetExecutablePath();
        if (string.IsNullOrWhiteSpace(exe)) return false;

        var expectedPrefix = $"\"{exe}\"";
        return !registeredCmd.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            && !registeredCmd.StartsWith(exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 应用启动或设置页载入时调用：自动同步和清理自启动注册项。
    /// 1. 彻底清除旧版 GameTimeTracker 注册表遗留项；
    /// 2. 如果此前已开启自启（存在旧项、新项，或数据库设置为 "1"），确保注册表 Sappfiler 项指向当前实际 exe 绝对路径；
    /// 3. 如果数据库明确设置为 "0"，清理所有自启项。
    /// </summary>
    public static void SyncAutoStartRegistration(string? dbSetting = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            var currentVal = key.GetValue(AppName) as string;
            var legacyVal = key.GetValue(LegacyAppName) as string;

            // 无论如何，彻底清理旧版 GameTimeTracker 注册表项
            if (legacyVal != null)
            {
                try
                {
                    key.DeleteValue(LegacyAppName, false);
                    AppLog.Info($"[自启动] 已清除旧版注册表启动项: {LegacyAppName}");
                }
                catch { }
            }

            // 判断是否应该开启自启
            bool shouldBeEnabled;
            if (dbSetting == "0")
            {
                shouldBeEnabled = false;
            }
            else if (dbSetting == "1")
            {
                shouldBeEnabled = true;
            }
            else
            {
                // 未指定数据库配置时，以注册表现有状态为准（只要此前开过就延续）
                shouldBeEnabled = !string.IsNullOrWhiteSpace(currentVal) || !string.IsNullOrWhiteSpace(legacyVal);
            }

            if (shouldBeEnabled)
            {
                var exePath = GetExecutablePath();
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    var cmd = $"\"{exePath}\" --autostart";
                    if (!string.Equals(currentVal, cmd, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(AppName, cmd);
                        AppLog.Info($"[自启动] 已自动校准/更新自启动项为: {cmd}");
                    }
                }
            }
            else if (currentVal != null && dbSetting == "0")
            {
                key.DeleteValue(AppName, false);
                AppLog.Info("[自启动] 根据配置关闭自启动项");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[自启动] 同步自启动注册项失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 启用或禁用开机自启。
    /// 启用时写入带 --autostart 参数的执行路径，开机后默认静默收进托盘运行。
    /// </summary>
    public static bool SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
            {
                AppLog.Warn("[自启动] 打开注册表 Run 键失败（key is null）");
                return false;
            }

            // 彻底清理旧项
            try { key.DeleteValue(LegacyAppName, false); } catch { }

            if (enable)
            {
                var exePath = GetExecutablePath();
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    var cmd = $"\"{exePath}\" --autostart";
                    key.SetValue(AppName, cmd);
                    AppLog.Info($"[自启动] 已成功设置开机自启: {cmd}");
                    return true;
                }

                AppLog.Warn("[自启动] 无法获取当前进程有效可执行文件路径");
                return false;
            }
            else
            {
                key.DeleteValue(AppName, false);
                AppLog.Info("[自启动] 已关闭开机自启");
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("[自启动] 设置开机自启状态异常", ex);
            return false;
        }
    }
}
