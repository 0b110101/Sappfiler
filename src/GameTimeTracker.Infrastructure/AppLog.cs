using System.Text;

namespace GameTimeTracker.Infrastructure;

/// <summary>
/// 轻量文件日志，供 QA 报 bug 时回溯问题。
/// 写入 exe 目录下的 data\logs\app.log（与数据库同处 data\，跟程序一起走）；
/// 单文件超过 2MB 轮转为 app.old.log，只保留一代，不无限膨胀。
/// 线程安全（全局锁）；日志失败静默吞掉——记录器绝不能反过来弄崩应用。
/// </summary>
public static class AppLog
{
    private static readonly object _lock = new();
    private static readonly string LogDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "logs");
    private static string LogPath => Path.Combine(LogDir, "app.log");
    private static string OldPath => Path.Combine(LogDir, "app.old.log");
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex)
        => Write("ERROR", $"{message} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    File.Copy(LogPath, OldPath, overwrite: true);
                    File.Delete(LogPath);
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }
}
