"""Heybox (小黑盒) game platform detector with appstate JSON manifest parsing."""

import glob
import json
import os
from typing import Dict, List, Optional

from tracker.detectors.base import PlatformDetector
from tracker.logger import logger
from tracker.models import GameIdentity


class HeyboxAppInfo:
    def __init__(self, appid: str, name: str, install_dir: str, library_path: str, process_names: List[str]):
        self.appid = appid
        self.name = name
        self.install_dir = install_dir
        self.library_path = library_path
        self.process_names = [p.lower() for p in process_names]
        self.game_dir = os.path.normpath(os.path.join(library_path, "common", install_dir))


class HeyboxDetector(PlatformDetector):
    """Detects games launched via Heybox (小黑盒) App Store."""

    def __init__(self, custom_libraries: Optional[List[str]] = None):
        self.custom_libraries = custom_libraries or []
        self.libraries: List[str] = []
        self.installed_games: Dict[str, HeyboxAppInfo] = {}  # appid -> HeyboxAppInfo
        self.refresh()

    @property
    def platform_name(self) -> str:
        return "Heybox"

    def _discover_libraries(self) -> List[str]:
        """Discovers HeyboxApps directories across system drives."""
        libs = []
        # Common locations for Heybox
        candidate_paths = [
            r"E:\xhh\HeyboxApps",
            r"D:\xhh\HeyboxApps",
            r"C:\xhh\HeyboxApps",
            r"D:\HeyboxApps",
            r"E:\HeyboxApps",
            r"C:\HeyboxApps",
        ]
        # Also check all drive roots
        for letter in "CDEFGHIJ":
            drive = f"{letter}:\\"
            if os.path.exists(drive):
                candidate_paths.append(os.path.join(drive, "HeyboxApps"))
                candidate_paths.append(os.path.join(drive, "xhh", "HeyboxApps"))

        for c in candidate_paths + self.custom_libraries:
            if os.path.isdir(c):
                c_norm = os.path.normpath(c)
                if c_norm not in libs:
                    libs.append(c_norm)

        return libs

    def refresh(self) -> None:
        """Parses all appstate_*.json manifests across all Heybox libraries."""
        self.libraries = self._discover_libraries()
        new_games: Dict[str, HeyboxAppInfo] = {}

        for lib in self.libraries:
            try:
                for entry in os.scandir(lib):
                    if entry.is_file() and entry.name.startswith("appstate_") and entry.name.endswith(".json"):
                        self._parse_appstate(entry.path, lib, new_games)
            except Exception as e:
                logger.debug("Failed scanning Heybox directory %s: %s", lib, e)

        self.installed_games = new_games
        if self.installed_games:
            logger.info("Parsed %d installed Heybox games", len(self.installed_games))

    def _parse_appstate(self, json_path: str, library_path: str, games_dict: Dict[str, HeyboxAppInfo]) -> None:
        try:
            with open(json_path, "r", encoding="utf-8", errors="replace") as f:
                data = json.load(f)

            appid = str(data.get("appid", ""))
            name = data.get("name", "")
            installdir = data.get("installdir", "")

            process_names = []
            build_cfg = data.get("build", {}).get("config", {})
            if isinstance(build_cfg, dict):
                process_names = build_cfg.get("process_names", [])

            if appid and name and installdir:
                games_dict[appid] = HeyboxAppInfo(
                    appid=appid,
                    name=name,
                    install_dir=installdir,
                    library_path=library_path,
                    process_names=process_names,
                )
        except Exception as e:
            logger.debug("Failed parsing Heybox appstate %s: %s", json_path, e)

    def detect(self, pid: int, name: str, exe_path: str) -> Optional[GameIdentity]:
        if not exe_path:
            return None

        norm_exe_path = os.path.normcase(os.path.normpath(exe_path))
        name_lower = name.lower()

        # Dynamic check: if path contains HeyboxApps/common, dynamically discover its parent library
        if "\\heyboxapps\\common\\" in norm_exe_path or "/heyboxapps/common/" in norm_exe_path:
            # Locate HeyboxApps root from exe_path
            parts = os.path.normpath(exe_path).split(os.sep)
            for i, part in enumerate(parts):
                if part.lower() == "heyboxapps":
                    lib_root = os.sep.join(parts[:i + 1])
                    if lib_root not in self.libraries:
                        self.libraries.append(lib_root)
                        self.refresh()
                    break

        # Check in installed games
        for appid, game in self.installed_games.items():
            norm_game_dir = os.path.normcase(game.game_dir)
            if norm_exe_path.startswith(norm_game_dir + os.sep) or norm_exe_path == norm_game_dir:
                return GameIdentity(
                    platform="Heybox",
                    platform_id=appid,
                    name=game.name,
                    executable=name,
                    executable_path=exe_path,
                )
            if name_lower in game.process_names and norm_exe_path.startswith(os.path.normcase(game.library_path)):
                return GameIdentity(
                    platform="Heybox",
                    platform_id=appid,
                    name=game.name,
                    executable=name,
                    executable_path=exe_path,
                )

        return None
