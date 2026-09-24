using System.Text.RegularExpressions;

namespace GameTimeTracker.Infrastructure.Process;

public static class ProcessFilter
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe",
        "vivaldi.exe", "arc.exe", "msedgewebview2.exe", "browser.exe", "liebao.exe",
        "sogouexplorer.exe", "360se.exe", "360chrome.exe", "qqbrowser.exe"
    };

    private static readonly HashSet<string> ElectronAndWebApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.exe", "discordsystemhelper.exe", "slack.exe", "code.exe",
        "obsidian.exe", "notion.exe", "qq.exe", "wechat.exe", "weixin.exe", "telegram.exe",
        "workbuddy.exe", "dingtalk.exe", "feishu.exe", "lark.exe", "spotify.exe",
        "postman.exe", "apifox.exe", "figma.exe", "cursor.exe",
        "applemusicwin.exe", "applemusic.exe", "wechatappex.exe"
    };

    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "gameoverlayui.exe",
        "epicgameslauncher.exe", "epicwebhelper.exe", "unrealcefsubprocess.exe",
        "galaxyclient.exe", "galaxyclientservice.exe", "galaxycommunication.exe",
        "eadesktop.exe", "eabackgroundservice.exe", "origin.exe", "originwebhelperservice.exe",
        "eaconnect_microsoft.exe", "link2ea.exe",
        "ubisoftconnect.exe", "upc.exe", "uplay.exe", "uplaywebcore.exe",
        "battle.net.exe", "battle.net helper.exe", "agent.exe",
        "riotclientservices.exe", "riotclientcrashhandler.exe", "vgtray.exe",
        "rockstar-games-launcher.exe", "launcher.exe", "rockstarservice.exe",
        "gamingservices.exe", "gamingservicesnet.exe", "xboxapp.exe", "xboxpcapp.exe",
        "gamingapp.exe", "xboxgamingoverlay.exe", "gamebar.exe", "gamebarftserver.exe",
        "xboxpcappft.exe"
    };

    private static readonly HashSet<string> WallpapersAndOverlays = new(StringComparer.OrdinalIgnoreCase)
    {
        "wallpaper32.exe", "wallpaper64.exe", "ui32.exe",
        "obs64.exe", "obs32.exe", "streamlabs obs.exe",
        "nvspcaps64.exe", "nvcontainer.exe", "geforcenow.exe", "shadowplay.exe",
        "radeonsoftware.exe", "amdow.exe", "amdrsserv.exe",
        "rtss.exe", "rtsshops64.exe", "msiafterburner.exe", "aida64.exe", "hwinfo64.exe"
    };

    private static readonly HashSet<string> Creative3DApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "blender.exe", "3dsmax.exe", "maya.exe", "c4d.exe", "cinema 4d.exe",
        "unity.exe", "unrealeditor.exe", "godot.exe",
        "premiere.exe", "afterfx.exe", "photoshop.exe", "resolve.exe",
        "autocad.exe", "revit.exe", "fusion360.exe"
    };

    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "registry", "smss.exe", "csrss.exe", "wininit.exe",
        "services.exe", "lsass.exe", "svchost.exe", "fontdrvhost.exe", "winlogon.exe",
        "explorer.exe", "dwm.exe", "sihost.exe", "taskhostw.exe", "ctfmon.exe", "shellexperiencehost.exe",
        "searchapp.exe", "searchindexer.exe", "runtimebroker.exe", "applicationframehost.exe",
        "spoolsv.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wsl.exe",
        "taskmgr.exe", "perfmon.exe", "audiodg.exe", "wlanext.exe", "dllhost.exe",
        "securityhealthservice.exe", "smartscreen.exe", "msmpeng.exe", "nissrv.exe",
        "antigravity.exe", "devenv.exe", "py.exe", "python.exe", "pythonw.exe",
        "memcompression", "dotnet.exe", "msbuild.exe", "gametimetracker.app.exe", "sappfiler.exe", "sappfiler.app.exe"
    };

    private static readonly HashSet<string> AuxiliaryProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "easyanticheat.exe", "easyanticheat_eos.exe", "beservice.exe", "battleye.exe",
        "vgc.exe", "ace-base.exe", "anticheat_expert.exe", "start_protected_game.exe",
        "crashreportclient.exe", "unitycrashhandler32.exe", "unitycrashhandler64.exe",
        "unrealcrashreporter.exe", "werfault.exe", "werfaultsecure.exe", "crashpad_handler.exe",
        "windowsterminal.exe", "notepad.exe", "calculatorapp.exe", "calculator.exe",
        "phoneexperiencehost.exe", "widgets.exe", "widgetservice.exe", "pcmanager.exe", "msstart.exe",
        "mspcmanagerservice.exe", "widgetboard.exe", "openconsole.exe", "snippingtool.exe",
        "lockapp.exe", "searchhost.exe", "textinputhost.exe", "crossdeviceresume.exe",
        "startmenuexperiencehost.exe", "systemsettings.exe", "monotificationux.exe",
        "tabtip.exe", "appactions.exe", "chsime.exe", "useroobebroker.exe", "shellhost.exe",
        "searchprotocolhost.exe", "rtkauduservice64.exe", "sandbox-center.exe",
        "onedrive.exe", "filesynchelper.exe", "filecoauth.exe", "onedrive.sync.service.exe",
        "nutstoredriversvc.exe", "ntfswatcher.exe", "nutstore.comlocalserver.exe",
        "node.exe", "language_server.exe",
        "mihomo.exe", "clash party.exe", "steamcommunity_302.exe",
        "rvcontrolsvc.exe", "bettboxhelperservice.exe",
        "hipstray.exe", "hipsdaemon.exe", "popblock.exe", "wsctrlsvc.exe", "mpdefendercorereservice.exe",
        "wetype_server.exe", "wetype_update.exe", "wetype_renderer.exe", "wetype_service.exe",
        "youdaoeh.exe", "youdaodict.exe", "youdaodicthelper.exe",
        "mailclient.exe", "gameviewerservice.exe", "gameviewerhealthd.exe", "gameviewerserver.exe",
        "wslservice.exe", "gameinputredistservice.exe", "officeclicktorun.exe", "identity_helper.exe"
    };

    private static readonly string[] NonGamePathKeywords = new[]
    {
        @"\windows\",
        @"\programdata\",
        @"\common files\",
        @"\app.asar",
        @"\node_modules\",
        @"\binaries\node\",
        @"\resources\sidecar\",
        @"\resources\bin\",
        @"\workbuddy\",
        @"\clash",
        @"\huorong\",
        @"\sysdiag\",
        @"\wetype\",
        @"\youdao\",
        @"\em client\",
        @"\onedrive\",
        @"\nutstore\",
        @"\uuyc\",
        @"\gameviewer\",
        @"\steamcommunity_302\",
        @"\radmin vpn\",
        @"\antigravity\",
        @"\microsoft shared\",
        @"\microsoft edge",
        @"\wsl\",
        @"\windowsapps\microsoft",
        @"\windowsapps\microsoftwindows"
    };

    private static readonly Regex ToolNamePatterns = new(
        @"(svc|service|daemon|helper|server|handler|updater|update|broker|watcher|tray|host|hook|crashpad|renderer|sdk|center|plugin)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsBlacklisted(string procName, string exePath)
    {
        if (string.IsNullOrWhiteSpace(procName)) return true;

        var name = procName.Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        // Direct set checks
        if (BrowserProcesses.Contains(name) ||
            ElectronAndWebApps.Contains(name) ||
            Launchers.Contains(name) ||
            WallpapersAndOverlays.Contains(name) ||
            Creative3DApps.Contains(name) ||
            SystemProcesses.Contains(name) ||
            AuxiliaryProcesses.Contains(name))
        {
            return true;
        }

        // Pattern check
        if (ToolNamePatterns.IsMatch(name))
        {
            return true;
        }

        // Path keywords
        if (!string.IsNullOrEmpty(exePath))
        {
            var normPath = exePath.ToLowerInvariant();
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            if (normPath.StartsWith(windir)) return true;

            var progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).ToLowerInvariant();
            if (normPath.StartsWith(progData)) return true;

            foreach (var kw in NonGamePathKeywords)
            {
                if (normPath.Contains(kw)) return true;
            }
        }

        return false;
    }
}
