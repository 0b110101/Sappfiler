"""Categorized process and path filtering to exclude system processes, utilities, and non-game apps."""

import os
import re
from typing import List, Optional, Set, Tuple

# 1. 浏览器及其 GPU 进程 (Hardware acceleration eats 3D devices)
BROWSER_PROCESSES: Set[str] = {
    "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe",
    "vivaldi.exe", "arc.exe", "msedgewebview2.exe", "browser.exe", "liebao.exe",
    "sogouexplorer.exe", "360se.exe", "360chrome.exe", "qqbrowser.exe",
}

# 2. Electron 及 桌面 Web 应用 (Create D3D devices & GPU subprocesses)
ELECTRON_AND_WEBAPP_PROCESSES: Set[str] = {
    "discord.exe", "discordsystemhelper.exe", "slack.exe", "code.exe",
    "obsidian.exe", "notion.exe", "qq.exe", "wechat.exe", "telegram.exe",
    "workbuddy.exe", "dingtalk.exe", "feishu.exe", "lark.exe", "spotify.exe",
    "postman.exe", "apifox.exe", "figma.exe", "cursor.exe",
    "applemusicwin.exe", "applemusic.exe",
}

# 3. 游戏平台启动器与客户端 (Launchers that stay alive during gameplay)
LAUNCHER_PROCESSES: Set[str] = {
    # Steam
    "steam.exe", "steamwebhelper.exe", "steamservice.exe", "gameoverlayui.exe",
    # Epic Games
    "epicgameslauncher.exe", "epicwebhelper.exe", "unrealcefsubprocess.exe",
    # GOG Galaxy
    "galaxyclient.exe", "galaxyclientservice.exe", "galaxycommunication.exe",
    # EA App / Origin
    "eadesktop.exe", "eabackgroundservice.exe", "origin.exe", "originwebhelperservice.exe",
    "eaconnect_microsoft.exe", "link2ea.exe",
    # Ubisoft Connect
    "ubisoftconnect.exe", "upc.exe", "uplay.exe", "uplaywebcore.exe",
    # Battle.net
    "battle.net.exe", "battle.net helper.exe", "agent.exe",
    # Riot
    "riotclientservices.exe", "riotclientcrashhandler.exe", "vgtray.exe",
    # Rockstar
    "rockstar-games-launcher.exe", "launcher.exe", "rockstarservice.exe",
    # Xbox / Microsoft Gaming
    "gamingservices.exe", "gamingservicesnet.exe", "xboxapp.exe", "xboxpcapp.exe",
    "gamingapp.exe", "xboxgamingoverlay.exe", "gamebar.exe", "gamebarftserver.exe",
    "xboxpcappft.exe",
}

# 4. 壁纸 / 采集 / 录屏 / 帧率监控 / 悬浮窗 (Render constantly even when idle)
WALLPAPER_CAPTURE_OVERLAY_PROCESSES: Set[str] = {
    # Wallpaper Engine
    "wallpaper32.exe", "wallpaper64.exe", "ui32.exe",
    # OBS Studio & Streaming
    "obs64.exe", "obs32.exe", "streamlabs obs.exe",
    # NVIDIA & AMD Capture / Overlays
    "nvspcaps64.exe", "nvcontainer.exe", "geforcenow.exe", "shadowplay.exe",
    "radeonsoftware.exe", "amdow.exe", "amdrsserv.exe",
    # Hardware monitoring & Overlays
    "rtss.exe", "rtsshops64.exe", "msiafterburner.exe", "aida64.exe", "hwinfo64.exe",
}

# 5. 3D 建模 / 设计 / 视频制作软件 (Heavy 3D rendering, non-games)
CREATIVE_AND_3D_PROCESSES: Set[str] = {
    "blender.exe", "3dsmax.exe", "maya.exe", "c4d.exe", "cinema 4d.exe",
    "unity.exe", "unrealeditor.exe", "godot.exe",
    "premiere.exe", "afterfx.exe", "photoshop.exe", "resolve.exe",
    "autocad.exe", "revit.exe", "fusion360.exe",
}

# 6. Windows 核心系统及桌面组件
SYSTEM_PROCESSES: Set[str] = {
    "system", "system idle process", "registry", "smss.exe", "csrss.exe", "wininit.exe",
    "services.exe", "lsass.exe", "svchost.exe", "fontdrvhost.exe", "winlogon.exe",
    "explorer.exe", "dwm.exe", "sihost.exe", "taskhostw.exe", "ctfmon.exe", "shellexperiencehost.exe",
    "searchapp.exe", "searchindexer.exe", "runtimebroker.exe", "applicationframehost.exe",
    "spoolsv.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wsl.exe",
    "taskmgr.exe", "perfmon.exe", "audiodg.exe", "wlanext.exe", "dllhost.exe",
    "securityhealthservice.exe", "smartscreen.exe", "msmpeng.exe", "nissrv.exe",
    "antigravity.exe", "devenv.exe", "py.exe", "python.exe", "pythonw.exe",
    "memcompression",
}

# 7. 反作弊、崩溃分析与日常工具服务
AUXILIARY_PROCESSES: Set[str] = {
    # Anti-cheat
    "easyanticheat.exe", "easyanticheat_eos.exe", "beservice.exe", "battleye.exe",
    "vgc.exe", "ace-base.exe", "anticheat_expert.exe", "start_protected_game.exe",
    # Crash Handlers
    "crashreportclient.exe", "unitycrashhandler32.exe", "unitycrashhandler64.exe",
    "unrealcrashreporter.exe", "werfault.exe", "werfaultsecure.exe", "crashpad_handler.exe",
    # Windows Store Utilities
    "windowsterminal.exe", "notepad.exe", "calculatorapp.exe", "calculator.exe",
    "phoneexperiencehost.exe", "widgets.exe", "widgetservice.exe", "pcmanager.exe", "msstart.exe",
    "mspcmanagerservice.exe", "widgetboard.exe", "openconsole.exe",
    "lockapp.exe", "searchhost.exe", "textinputhost.exe", "crossdeviceresume.exe",
    "startmenuexperiencehost.exe", "systemsettings.exe", "monotificationux.exe",
    "tabtip.exe", "appactions.exe",
    # Cloud & Sync
    "onedrive.exe", "filesynchelper.exe", "filecoauth.exe", "onedrive.sync.service.exe",
    "nutstoredriversvc.exe", "ntfswatcher.exe", "nutstore.comlocalserver.exe",
    # Development / Runtime
    "node.exe", "language_server.exe",
    # Network / Proxy
    "mihomo.exe", "clash party.exe", "steamcommunity_302.exe", "steamcommunity_302.cli.exe",
    "rvcontrolsvc.exe", "bettboxhelperservice.exe",
    # Security / Antivirus
    "hipstray.exe", "hipsdaemon.exe", "popblock.exe", "wsctrlsvc.exe", "mpdefendercorereservice.exe",
    # Common Tools & IM
    "wetype_server.exe", "wetype_update.exe", "wetype_renderer.exe", "wetype_service.exe",
    "youdaoeh.exe", "youdaodict.exe", "youdaodicthelper.exe",
    "mailclient.exe", "gameviewerservice.exe", "gameviewerhealthd.exe", "gameviewerserver.exe",
    "wslservice.exe", "gameinputredistservice.exe", "officeclicktorun.exe", "identity_helper.exe",
}

# Substring keywords in paths that definitely indicate non-game software
NON_GAME_PATH_KEYWORDS = (
    "\\windows\\",
    "\\programdata\\",
    "\\common files\\",
    "\\app.asar",
    "\\node_modules\\",
    "\\binaries\\node\\",
    "\\resources\\sidecar\\",
    "\\resources\\bin\\",
    "\\workbuddy\\",
    "\\clash",
    "\\huorong\\",
    "\\sysdiag\\",
    "\\wetype\\",
    "\\youdao\\",
    "\\em client\\",
    "\\onedrive\\",
    "\\nutstore\\",
    "\\uuyc\\",
    "\\gameviewer\\",
    "\\steamcommunity_302\\",
    "\\radmin vpn\\",
    "\\antigravity\\",
    "\\microsoft shared\\",
    "\\microsoft edge",
    "\\wsl\\",
    "\\windowsapps\\microsoft",
    "\\windowsapps\\microsoftwindows",
)

# Name keyword patterns that indicate background system/tool services
TOOL_NAME_PATTERNS = re.compile(
    r"(svc|service|daemon|helper|server|handler|updater|update|broker|watcher|tray|host|hook|crashpad|renderer|sdk|center|plugin)\.exe$",
    re.IGNORECASE,
)


def is_blacklisted_process(proc_name: str, exe_path: str = "") -> bool:
    """Returns True if the process falls into any non-game blacklist category."""
    if not proc_name:
        return True

    name = proc_name.lower().strip()
    if not name.endswith(".exe"):
        return True

    # 1. Direct Set membership check across all non-game categories
    if (
        name in BROWSER_PROCESSES
        or name in ELECTRON_AND_WEBAPP_PROCESSES
        or name in LAUNCHER_PROCESSES
        or name in WALLPAPER_CAPTURE_OVERLAY_PROCESSES
        or name in CREATIVE_AND_3D_PROCESSES
        or name in SYSTEM_PROCESSES
        or name in AUXILIARY_PROCESSES
    ):
        return True

    # 2. Tool keyword regex pattern check (e.g. *service.exe, *daemon.exe)
    if TOOL_NAME_PATTERNS.search(name):
        return True

    # 3. Path-based exclusion check
    if exe_path:
        norm_path = exe_path.lower().replace("/", "\\")

        # Exclude entire Windows directory and all subfolders
        windir = os.environ.get("WINDIR", "C:\\Windows").lower().replace("/", "\\")
        if norm_path.startswith(windir):
            return True

        # Exclude ProgramData
        progdata = os.environ.get("PROGRAMDATA", "C:\\ProgramData").lower().replace("/", "\\")
        if norm_path.startswith(progdata):
            return True

        # Exclude known software path keywords
        for kw in NON_GAME_PATH_KEYWORDS:
            if kw in norm_path:
                return True

    return False


def is_potential_game_candidate(
    pid: int,
    proc_name: str,
    exe_path: str,
    custom_dirs: Optional[List[str]] = None,
) -> Tuple[bool, str]:
    """Heuristic check combining graphics modules, engine signatures, and directory path.
    
    Returns:
        Tuple[is_likely_game, reason_details]
    """
    if is_blacklisted_process(proc_name, exe_path):
        return False, "Blacklisted"

    if not exe_path:
        return False, "No exe path"

    from tracker.process.heuristics import evaluate_game_heuristics

    result = evaluate_game_heuristics(pid, proc_name, exe_path, custom_dirs)
    return result.is_likely_game, result.details
