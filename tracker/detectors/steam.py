"""Steam game detector with multi-library and ACF manifest parsing."""

import os
import re
import sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from tracker.detectors.base import PlatformDetector
from tracker.logger import logger
from tracker.models import GameIdentity

if sys.platform == "win32":
    import winreg
else:
    winreg = None


def parse_vdf_simple(text: str) -> Dict[str, str]:
    """Extracts top-level and simple key-value pairs from Valve Data Format (VDF)."""
    result: Dict[str, str] = {}
    pattern = re.compile(r'"([^"\\]*(?:\\.[^"\\]*)*)"\s+"([^"\\]*(?:\\.[^"\\]*)*)"')
    for match in pattern.finditer(text):
        key = match.group(1).replace("\\\\", "\\").replace('\\"', '"')
        val = match.group(2).replace("\\\\", "\\").replace('\\"', '"')
        result[key.lower()] = val
    return result


class SteamAppInfo:
    def __init__(self, appid: str, name: str, install_dir: str, library_path: str):
        self.appid = appid
        self.name = name
        self.install_dir = install_dir
        self.library_path = library_path
        self.game_dir = os.path.normpath(os.path.join(library_path, "steamapps", "common", install_dir))


class SteamDetector(PlatformDetector):
    """Detects games launched via Steam across all configured and detected library folders."""

    def __init__(self, custom_libraries: Optional[List[str]] = None):
        self.custom_libraries = custom_libraries or []
        self.libraries: List[str] = []
        self.installed_games: Dict[str, SteamAppInfo] = {}  # appid -> SteamAppInfo
        self.refresh()

    @property
    def platform_name(self) -> str:
        return "Steam"

    def _find_steam_root(self) -> Optional[str]:
        """Finds Steam installation directory from Windows Registry or default paths."""
        if winreg is not None:
            # 1. Try HKCU
            try:
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
                    path, _ = winreg.QueryValueEx(key, "SteamPath")
                    if path and os.path.exists(path):
                        return os.path.normpath(path)
            except OSError:
                pass

            # 2. Try HKLM 64-bit / 32-bit
            for subkey in [r"SOFTWARE\WOW6432Node\Valve\Steam", r"SOFTWARE\Valve\Steam"]:
                try:
                    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, subkey) as key:
                        path, _ = winreg.QueryValueEx(key, "InstallPath")
                        if path and os.path.exists(path):
                            return os.path.normpath(path)
                except OSError:
                    pass

        # 3. Default fallback paths
        defaults = [
            r"C:\Program Files (x86)\Steam",
            r"C:\Program Files\Steam",
            r"D:\Steam",
            r"E:\Steam",
        ]
        for d in defaults:
            if os.path.exists(d):
                return os.path.normpath(d)

        return None

    def _discover_libraries(self, steam_root: str) -> List[str]:
        """Parses libraryfolders.vdf to find all Steam library paths."""
        normalized_root = os.path.normpath(steam_root)
        libs_map: Dict[str, str] = {os.path.normcase(normalized_root): normalized_root}

        vdf_path = os.path.join(normalized_root, "steamapps", "libraryfolders.vdf")
        if os.path.exists(vdf_path):
            try:
                with open(vdf_path, "r", encoding="utf-8", errors="replace") as f:
                    content = f.read()

                # Find all "path" "\path\to\library" entries
                path_matches = re.findall(r'"path"\s+"([^"\\]*(?:\\.[^"\\]*)*)"', content)
                for raw_path in path_matches:
                    clean_path = os.path.normpath(raw_path.replace("\\\\", "\\"))
                    case_key = os.path.normcase(clean_path)
                    if os.path.exists(clean_path) and case_key not in libs_map:
                        libs_map[case_key] = clean_path
            except Exception as e:
                logger.warning("Error reading libraryfolders.vdf at %s: %s", vdf_path, e)

        for custom in self.custom_libraries:
            c_norm = os.path.normpath(custom)
            case_key = os.path.normcase(c_norm)
            if os.path.exists(c_norm) and case_key not in libs_map:
                libs_map[case_key] = c_norm

        return list(libs_map.values())

    def refresh(self) -> None:
        """Discovers all Steam libraries and parses all appmanifest_*.acf files."""
        steam_root = self._find_steam_root()
        if not steam_root:
            logger.info("Steam installation root not found on this system.")
            self.libraries = [os.path.normpath(p) for p in self.custom_libraries if os.path.exists(p)]
        else:
            self.libraries = self._discover_libraries(steam_root)

        logger.info("Discovered %d Steam libraries: %s", len(self.libraries), self.libraries)

        new_games: Dict[str, SteamAppInfo] = {}
        for lib in self.libraries:
            steamapps_dir = os.path.join(lib, "steamapps")
            if not os.path.isdir(steamapps_dir):
                continue

            try:
                for entry in os.scandir(steamapps_dir):
                    if entry.is_file() and entry.name.startswith("appmanifest_") and entry.name.endswith(".acf"):
                        self._parse_acf(entry.path, lib, new_games)
            except Exception as e:
                logger.warning("Failed to scan steamapps dir %s: %s", steamapps_dir, e)

        self.installed_games = new_games
        logger.info("Parsed %d installed Steam games across all libraries", len(self.installed_games))

    def _parse_acf(self, acf_path: str, library_path: str, games_dict: Dict[str, SteamAppInfo]) -> None:
        """Parses a single appmanifest_*.acf file and adds to games dictionary."""
        try:
            with open(acf_path, "r", encoding="utf-8", errors="replace") as f:
                content = f.read()

            kv = parse_vdf_simple(content)
            appid = kv.get("appid")
            name = kv.get("name")
            installdir = kv.get("installdir")

            if appid and name and installdir:
                # Exclude Steamworks common redistributables (appid 228980)
                if appid == "228980" or "steamworks shared" in name.lower():
                    return
                games_dict[appid] = SteamAppInfo(
                    appid=appid,
                    name=name,
                    install_dir=installdir,
                    library_path=library_path,
                )
        except Exception as e:
            logger.debug("Failed parsing ACF file %s: %s", acf_path, e)

    def detect(self, pid: int, name: str, exe_path: str) -> Optional[GameIdentity]:
        """Matches a running process against known Steam games."""
        if not exe_path:
            return None

        norm_exe_path = os.path.normcase(os.path.normpath(exe_path))

        # Check if process is located inside any installed Steam game's directory
        for appid, game in self.installed_games.items():
            norm_game_dir = os.path.normcase(game.game_dir)
            # Ensure path starts with game_dir + separator to avoid prefix partial collisions
            if norm_exe_path.startswith(norm_game_dir + os.sep) or norm_exe_path == norm_game_dir:
                return GameIdentity(
                    platform="Steam",
                    platform_id=appid,
                    name=game.name,
                    executable=name,
                    executable_path=exe_path,
                )

        # Quick check: does the path contain \steamapps\common\ ?
        # If so, but not in installed_games yet (e.g. newly installed), try refreshing once!
        if "\\steamapps\\common\\" in norm_exe_path or "/steamapps/common/" in norm_exe_path:
            logger.debug("Detected steamapps/common in path for %s, re-scanning manifests...", exe_path)
            self.refresh()
            for appid, game in self.installed_games.items():
                norm_game_dir = os.path.normcase(game.game_dir)
                if norm_exe_path.startswith(norm_game_dir + os.sep) or norm_exe_path == norm_game_dir:
                    return GameIdentity(
                        platform="Steam",
                        platform_id=appid,
                        name=game.name,
                        executable=name,
                        executable_path=exe_path,
                    )

        return None
