"""GOG Galaxy and GOG offline installer game detector."""

import glob
import json
import os
import sqlite3
import sys
from typing import Dict, List, Optional

from tracker.detectors.base import PlatformDetector
from tracker.logger import logger
from tracker.models import GameIdentity

if sys.platform == "win32":
    import winreg
else:
    winreg = None


class GOGAppInfo:
    def __init__(self, game_id: str, name: str, install_path: str, executable: str = ""):
        self.game_id = game_id
        self.name = name
        self.install_path = os.path.normpath(install_path)
        self.executable = executable


class GOGDetector(PlatformDetector):
    """Detects games installed via GOG Galaxy or GOG standalone offline installers."""

    def __init__(self, custom_dirs: Optional[List[str]] = None):
        self.custom_dirs = custom_dirs or []
        self.installed_games: Dict[str, GOGAppInfo] = {}  # game_id -> GOGAppInfo
        self.refresh()

    @property
    def platform_name(self) -> str:
        return "GOG"

    def _scan_galaxy_db(self, games: Dict[str, GOGAppInfo]) -> None:
        """Reads %ProgramData%\\GOG.com\\Galaxy\\storage\\galaxy-2.0.db if GOG Galaxy 2.0 is installed."""
        program_data = os.environ.get("PROGRAMDATA", r"C:\ProgramData")
        db_path = os.path.join(program_data, "GOG.com", "Galaxy", "storage", "galaxy-2.0.db")

        if not os.path.exists(db_path):
            return

        try:
            # Open read-only SQLite connection with URI
            uri = f"file:{db_path}?mode=ro"
            conn = sqlite3.connect(uri, uri=True, timeout=5.0)
            conn.row_factory = sqlite3.Row
            cur = conn.cursor()

            # Check if InstalledExternalProducts or InstalledBaseProducts table exists
            cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name IN ('InstalledExternalProducts', 'InstalledBaseProducts');")
            tables = [row[0] for row in cur.fetchall()]

            for tbl in tables:
                try:
                    cur.execute(f"SELECT productId, title, installationPath, executable FROM {tbl};")
                    for row in cur.fetchall():
                        p_id = str(row["productId"])
                        title = row["title"] or f"GOG Game {p_id}"
                        path = row["installationPath"]
                        exe = row.get("executable", "")
                        if path and os.path.exists(path):
                            games[p_id] = GOGAppInfo(
                                game_id=p_id,
                                name=title,
                                install_path=path,
                                executable=exe,
                            )
                except Exception as e:
                    logger.debug("Querying GOG table %s failed: %s", tbl, e)

            conn.close()
        except Exception as e:
            logger.debug("Failed reading GOG Galaxy database at %s: %s", db_path, e)

    def _scan_registry(self, games: Dict[str, GOGAppInfo]) -> None:
        """Scans Windows Registry HKLM\\SOFTWARE\\WOW6432Node\\GOG.com\\Games for offline GOG installations."""
        if winreg is None:
            return

        registry_keys = [
            r"SOFTWARE\WOW6432Node\GOG.com\Games",
            r"SOFTWARE\GOG.com\Games",
        ]

        for reg_path in registry_keys:
            try:
                with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, reg_path) as root_key:
                    num_subkeys, _, _ = winreg.QueryInfoKey(root_key)
                    for i in range(num_subkeys):
                        subkey_name = winreg.EnumKey(root_key, i)
                        try:
                            with winreg.OpenKey(root_key, subkey_name) as subkey:
                                def get_val(name: str) -> str:
                                    try:
                                        val, _ = winreg.QueryValueEx(subkey, name)
                                        return str(val)
                                    except OSError:
                                        return ""

                                game_id = get_val("gameID") or subkey_name
                                game_name = get_val("gameName")
                                install_path = get_val("path")
                                exe = get_val("exe")

                                if game_id and game_name and install_path and os.path.exists(install_path):
                                    games[game_id] = GOGAppInfo(
                                        game_id=game_id,
                                        name=game_name,
                                        install_path=install_path,
                                        executable=exe,
                                    )
                        except OSError:
                            continue
            except OSError:
                continue

    def refresh(self) -> None:
        """Discovers GOG games via Galaxy database and Windows Registry."""
        new_games: Dict[str, GOGAppInfo] = {}
        self._scan_galaxy_db(new_games)
        self._scan_registry(new_games)
        self.installed_games = new_games
        if self.installed_games:
            logger.info("Parsed %d installed GOG games", len(self.installed_games))

    def _check_goggame_info(self, exe_path: str) -> Optional[GameIdentity]:
        """Checks for goggame-*.info manifest in the executable's directory or up to 3 parent directories."""
        current_dir = os.path.dirname(exe_path)
        candidates = []
        for _ in range(4):
            if current_dir and current_dir not in candidates:
                candidates.append(current_dir)
            parent = os.path.dirname(current_dir)
            if parent == current_dir:
                break
            current_dir = parent

        for directory in candidates:
            if not os.path.isdir(directory):
                continue
            info_files = glob.glob(os.path.join(directory, "goggame-*.info"))
            for info_file in info_files:
                try:
                    with open(info_file, "r", encoding="utf-8", errors="replace") as f:
                        data = json.load(f)
                    game_id = str(data.get("gameId") or data.get("rootGameId", ""))
                    name = data.get("name")
                    if game_id and name:
                        return GameIdentity(
                            platform="GOG",
                            platform_id=game_id,
                            name=name,
                            executable=os.path.basename(exe_path),
                            executable_path=exe_path,
                        )
                except Exception as e:
                    logger.debug("Failed reading GOG info file %s: %s", info_file, e)
        return None

    def detect(self, pid: int, name: str, exe_path: str) -> Optional[GameIdentity]:
        if not exe_path:
            return None

        norm_exe_path = os.path.normcase(os.path.normpath(exe_path))

        # Check in pre-indexed games (Galaxy DB or Registry)
        for game_id, game in self.installed_games.items():
            norm_install = os.path.normcase(game.install_path)
            if norm_exe_path.startswith(norm_install + os.sep) or norm_exe_path == norm_install:
                return GameIdentity(
                    platform="GOG",
                    platform_id=game_id,
                    name=game.name,
                    executable=name,
                    executable_path=exe_path,
                )

        # Fallback: check for goggame-*.info file in current or parent folder
        gog_info_match = self._check_goggame_info(exe_path)
        if gog_info_match:
            return gog_info_match

        return None
