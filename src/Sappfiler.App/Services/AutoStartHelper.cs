using Microsoft.Win32;
using GameTimeTracker.Infrastructure;

namespace GameTimeTracker.App.Services;

/// <summary>
/// Windows 开机自启管理服务（基于 HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。
/// 普通用户权限即可读写，无需管理员提权。
/// </summary>
public static class AutoStartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Sappfiler";
    private const string LegacyAppName = "GameTimeTracker";

    /// <summary>
    /// 检查当前是否已开启开机自启。
    /// </summary>
    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var val = (key?.GetValue(AppName) as string) ?? (key?.GetValue(LegacyAppName) as string);
            return !string.IsNullOrWhiteSpace(val);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[自启动] 检查开机自启状态失败: {ex.Message}");
            return false;
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

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    var cmd = $"\"{exePath}\" --autostart";
                    key.SetValue(AppName, cmd);
                    key.DeleteValue(LegacyAppName, false);
                    AppLog.Info($"[自启动] 已成功设置开机自启: {cmd}");
                    return true;
                }

                AppLog.Warn("[自启动] 无法获取当前进程可执行文件路径");
                return false;
            }
            else
            {
                key.DeleteValue(AppName, false);
                key.DeleteValue(LegacyAppName, false);
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
