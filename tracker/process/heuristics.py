"""Heuristic game detection engine for standalone and non-platform games."""

import ctypes
import os
from ctypes import wintypes
from dataclasses import dataclass, field
from typing import List, Optional, Set, Tuple

import psutil

from tracker.logger import logger

# Graphics & 3D Rendering APIs typically loaded by games
GRAPHICS_DLLS: Set[str] = {
    "d3d9.dll",
    "d3d11.dll",
    "d3d12.dll",
    "dxgi.dll",
    "vulkan-1.dll",
    "opengl32.dll",
}

# Game engine & gaming middleware signatures (DLL names or directory markers)
GAME_ENGINE_MARKERS: Set[str] = {
    "unityplayer.dll",
    "unitycrashhandler64.exe",
    "unitycrashhandler32.exe",
    "monobleedingedge",
    "mono-2.0-bdwgc.dll",
    "fmod.dll",
    "fmod64.dll",
    "fmodstudio.dll",
    "bink2w64.dll",
    "steam_api.dll",
    "steam_api64.dll",
    "galaxy.dll",
    "galaxy64.dll",
    "eos_sdk.dll",
    "xinput1_4.dll",
    "xinput1_3.dll",
    "xaudio2_9.dll",
    "xaudio2_7.dll",
}

# Standard game directory keywords
STANDARD_GAME_PATH_KEYWORDS = (
    "\\steamapps\\common\\",
    "\\epic games\\",
    "\\gog games\\",
    "\\gog galaxy\\games\\",
    "\\heyboxapps\\common\\",
    "\\heyboxapps\\",
    "\\xboxgames\\",
    "\\games\\",
    "\\game\\",
    "\\indie\\",
    "\\itch.io\\",
)


@dataclass
class HeuristicResult:
    is_likely_game: bool
    confidence_score: float      # 0 to 100
    detected_graphics: List[str] = field(default_factory=list)
    detected_engine: List[str] = field(default_factory=list)
    is_in_game_path: bool = False
    is_fullscreen: bool = False
    details: str = ""


def get_loaded_modules(pid: int) -> Set[str]:
    """Retrieves filenames of all loaded DLL modules for a given process PID."""
    modules: Set[str] = set()
    try:
        proc = psutil.Process(pid)
        for m in proc.memory_maps():
            p = m.path
            if p:
                modules.add(os.path.basename(p).lower())
    except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
        pass
    except Exception as e:
        logger.debug("Failed inspecting memory maps for PID %d: %s", pid, e)
    return modules


def check_directory_engine_markers(exe_path: str) -> List[str]:
    """Checks if the executable's directory contains known game engine files or subfolders."""
    detected = []
    try:
        exe_dir = os.path.dirname(exe_path)
        if not os.path.isdir(exe_dir):
            return detected

        # Scan files in executable directory
        with os.scandir(exe_dir) as entries:
            for entry in entries:
                name_lower = entry.name.lower()
                if name_lower in GAME_ENGINE_MARKERS:
                    detected.append(entry.name)

        # Check for Unity Data folder: <GameName>_Data
        base_name = os.path.splitext(os.path.basename(exe_path))[0]
        unity_data = os.path.join(exe_dir, f"{base_name}_Data")
        if os.path.isdir(unity_data):
            detected.append(f"{base_name}_Data")

        # Check for Unreal Engine directory structure (Engine / Content / Binaries)
        if os.path.isdir(os.path.join(exe_dir, "..", "Content")) or os.path.isdir(os.path.join(exe_dir, "Content")):
            detected.append("UE_Content")

    except Exception as e:
        logger.debug("Error checking directory engine markers for %s: %s", exe_path, e)

    return detected


def check_is_fullscreen(pid: int) -> bool:
    """Checks whether the process owns a visible window occupying the full primary monitor."""
    user32 = getattr(ctypes.windll, "user32", None)
    if not user32:
        return False

    try:
        screen_w = user32.GetSystemMetrics(0)  # SM_CXSCREEN
        screen_h = user32.GetSystemMetrics(1)  # SM_CYSCREEN

        is_fullscreen = False

        def enum_cb(hwnd, _):
            nonlocal is_fullscreen
            if user32.IsWindowVisible(hwnd):
                win_pid = wintypes.DWORD()
                user32.GetWindowThreadProcessId(hwnd, ctypes.byref(win_pid))
                if win_pid.value == pid:
                    rect = wintypes.RECT()
                    user32.GetWindowRect(hwnd, ctypes.byref(rect))
                    w = rect.right - rect.left
                    h = rect.bottom - rect.top
                    # Matches screen resolution
                    if w >= screen_w and h >= screen_h:
                        is_fullscreen = True
                        return False  # Stop enumeration
            return True

        WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
        user32.EnumWindows(WNDENUMPROC(enum_cb), 0)
        return is_fullscreen
    except Exception:
        return False


def evaluate_game_heuristics(
    pid: int,
    proc_name: str,
    exe_path: str,
    custom_game_dirs: Optional[List[str]] = None,
) -> HeuristicResult:
    """Evaluates behavioral and environmental heuristics to determine if an unknown process is a game.
    
    Scoring weights:
    - Located in recognized game folder / user game folder: +35 points
    - Loads 3D graphics APIs (D3D11/D3D12/Vulkan): +30 points
    - Has game engine signatures (Unity/Unreal/FMOD/Steam API): +30 points
    - Running in fullscreen mode: +15 points
    
    Total >= 65 is deemed likely to be a game candidate.
    """
    custom_dirs = custom_game_dirs or []
    norm_path = exe_path.lower().replace("/", "\\")

    # 1. Path check
    in_game_path = False
    for custom in custom_dirs:
        c_norm = custom.lower().replace("/", "\\").rstrip("\\")
        if norm_path.startswith(c_norm + "\\") or norm_path == c_norm:
            in_game_path = True
            break

    if not in_game_path:
        in_game_path = any(kw in norm_path for kw in STANDARD_GAME_PATH_KEYWORDS)

    # 2. Loaded modules check (DirectX / Vulkan / OpenGL)
    modules = get_loaded_modules(pid)
    detected_graphics = [dll for dll in GRAPHICS_DLLS if dll in modules]

    # 3. Game Engine check
    engine_from_modules = [m for m in GAME_ENGINE_MARKERS if m in modules]
    engine_from_dir = check_directory_engine_markers(exe_path)
    detected_engine = list(set(engine_from_modules + engine_from_dir))

    # 4. Fullscreen check
    is_fullscreen = check_is_fullscreen(pid)

    # Calculate Confidence Score
    score = 0.0
    reasons = []

    if in_game_path:
        score += 35.0
        reasons.append("游戏目录")

    if detected_graphics:
        score += 30.0
        reasons.append(f"加载3D图形库({','.join(detected_graphics[:2])})")

    if detected_engine:
        score += 30.0
        reasons.append(f"游戏引擎特征({','.join(detected_engine[:2])})")

    if is_fullscreen:
        score += 15.0
        reasons.append("全屏运行")

    is_likely = score >= 65.0
    details = f"得分: {score:.0f} [{', '.join(reasons)}]" if reasons else "未发现特征"

    return HeuristicResult(
        is_likely_game=is_likely,
        confidence_score=score,
        detected_graphics=detected_graphics,
        detected_engine=detected_engine,
        is_in_game_path=in_game_path,
        is_fullscreen=is_fullscreen,
        details=details,
    )
