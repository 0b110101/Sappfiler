"""Epic Games detector with manifest parsing."""

import json
import os
from typing import Dict, List, Optional

from tracker.detectors.base import PlatformDetector
from tracker.logger import logger
from tracker.models import GameIdentity


class EpicAppInfo:
    def __init__(self, catalog_id: str, app_name: str, display_name: str, install_location: str, launch_exe: str):
        self.catalog_id = catalog_id or app_name
        self.app_name = app_name
        self.display_name = display_name
        self.install_location = os.path.normpath(install_location)
        self.launch_exe = launch_exe


class EpicDetector(PlatformDetector):
    """Detects games installed and launched via Epic Games Store."""

    def __init__(self, custom_manifest_dirs: Optional[List[str]] = None):
        self.custom_dirs = custom_manifest_dirs or []
        self.installed_games: Dict[str, EpicAppInfo] = {}
        self.refresh()

    @property
    def platform_name(self) -> str:
        return "Epic"

    def _get_manifest_dirs(self) -> List[str]:
        dirs = []
        program_data = os.environ.get("PROGRAMDATA", r"C:\ProgramData")
        default_dir = os.path.join(program_data, "Epic", "EpicGamesLauncher", "Data", "Manifests")
        if os.path.isdir(default_dir):
            dirs.append(default_dir)

        for c in self.custom_dirs:
            if os.path.isdir(c) and c not in dirs:
                dirs.append(c)
        return dirs

    def refresh(self) -> None:
        """Parses all *.item manifests from Epic manifest directories."""
        new_games: Dict[str, EpicAppInfo] = {}
        for m_dir in self._get_manifest_dirs():
            try:
                for entry in os.scandir(m_dir):
                    if entry.is_file() and entry.name.endswith(".item"):
                        self._parse_item(entry.path, new_games)
            except Exception as e:
                logger.warning("Error reading Epic manifest dir %s: %s", m_dir, e)

        self.installed_games = new_games
        if self.installed_games:
            logger.info("Parsed %d installed Epic games", len(self.installed_games))

    def _parse_item(self, item_path: str, games_dict: Dict[str, EpicAppInfo]) -> None:
        try:
            with open(item_path, "r", encoding="utf-8", errors="replace") as f:
                data = json.load(f)

            display_name = data.get("DisplayName")
            install_loc = data.get("InstallLocation")
            catalog_id = data.get("CatalogItemId") or data.get("AppName")
            app_name = data.get("AppName", "")
            launch_exe = data.get("LaunchExecutable", "")

            if display_name and install_loc and catalog_id:
                games_dict[catalog_id] = EpicAppInfo(
                    catalog_id=catalog_id,
                    app_name=app_name,
                    display_name=display_name,
                    install_location=install_loc,
                    launch_exe=launch_exe,
                )
        except Exception as e:
            logger.debug("Failed parsing Epic item manifest %s: %s", item_path, e)

    def detect(self, pid: int, name: str, exe_path: str) -> Optional[GameIdentity]:
        if not exe_path:
            return None

        norm_exe_path = os.path.normcase(os.path.normpath(exe_path))

        for catalog_id, game in self.installed_games.items():
            norm_loc = os.path.normcase(game.install_location)
            if norm_exe_path.startswith(norm_loc + os.sep) or norm_exe_path == norm_loc:
                return GameIdentity(
                    platform="Epic",
                    platform_id=catalog_id,
                    name=game.display_name,
                    executable=name,
                    executable_path=exe_path,
                )

        return None
