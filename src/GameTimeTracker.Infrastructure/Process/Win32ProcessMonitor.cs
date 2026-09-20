using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameTimeTracker.Core.Interfaces;
using GameTimeTracker.Core.Models;

namespace GameTimeTracker.Infrastructure.Process;

public class Win32ProcessMonitor : IProcessMonitor
{
    private static readonly HashSet<string> SystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "services", "lsass", "svchost",
        "fontdrvhost", "winlogon", "dwm", "spoolsv", "explorer", "sihost", "taskhostw",
        "ctfmon", "SearchApp", "ShellExperienceHost", "StartMenuExperienceHost", "SecurityHealthSystray",
        "RuntimeBroker", "smartscreen", "ApplicationFrameHost", "TextInputHost", "conhost",
        "WmiPrvSE", "dllhost", "taskmgr", "cmd", "powershell", "pwsh", "devenv", "Code",
        "steam", "steamwebhelper", "EpicGamesLauncher", "UnrealCEFSubProcess", "RiotClientServices",
        "dotnet", "msbuild", "GameTimeTracker.App", "GameTimeTracker"
    };

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, [Out] StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public Task<IReadOnlyList<DetectedProcess>> ScanRunningProcessesAsync()
    {
        return Task.Run<IReadOnlyList<DetectedProcess>>(() =>
        {
            var results = new List<DetectedProcess>();
            var processes = System.Diagnostics.Process.GetProcesses();

            foreach (var p in processes)
            {
                try
                {
                    var name = p.ProcessName;
                    var exePath = TryGetProcessPath(p.Id);
                    if (string.IsNullOrEmpty(exePath))
                    {
                        continue;
                    }

                    if (ProcessFilter.IsBlacklisted(name, exePath))
                    {
                        continue;
                    }

                    var windowTitle = !string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.MainWindowTitle : null;

                    results.Add(new DetectedProcess(
                        Pid: p.Id,
                        ProcessName: name,
                        ExecutablePath: exePath,
                        WindowTitle: windowTitle
                    ));
                }
                catch
                {
                    // Ignore inaccessible processes
                }
                finally
                {
                    p.Dispose();
                }
            }

            return results;
        });
    }

    private static string? TryGetProcessPath(int pid)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var capacity = 1024;
            var sb = new StringBuilder(capacity);
            if (QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
            {
                return sb.ToString();
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return null;
    }
}
