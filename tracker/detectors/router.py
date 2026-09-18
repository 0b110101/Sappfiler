"""Platform detector router combining all individual platform detectors."""

from typing import List, Optional

from tracker.db.database import Database
from tracker.detectors.base import PlatformDetector
from tracker.detectors.epic import EpicDetector
from tracker.detectors.gog import GOGDetector
from tracker.detectors.heybox import HeyboxDetector
from tracker.detectors.standalone import StandaloneDetector, compute_path_hash
from tracker.detectors.steam import SteamDetector
from tracker.detectors.xbox import XboxDetector
from tracker.logger import logger
from tracker.models import GameIdentity


class DetectorRouter:
    """Dispatches process detection to specialized platform detectors."""

    def __init__(self, db: Database, custom_steam_libs: Optional[List[str]] = None, custom_epic_dirs: Optional[List[str]] = None, custom_heybox_dirs: Optional[List[str]] = None):
        self.db = db
        self.steam = SteamDetector(custom_steam_libs)
        self.epic = EpicDetector(custom_epic_dirs)
        self.gog = GOGDetector()
        self.heybox = HeyboxDetector(custom_heybox_dirs)
        self.xbox = XboxDetector()
        self.standalone = StandaloneDetector(db)

        # Priority order:
        # 1. Platform-specific detectors (Steam, Epic, GOG, Heybox, Xbox)
        # 2. Standalone / already registered database games
        self.detectors: List[PlatformDetector] = [
            self.steam,
            self.epic,
            self.gog,
            self.heybox,
            self.xbox,
            self.standalone,
        ]

    def refresh(self) -> None:
        """Refreshes all platform detectors."""
        for d in self.detectors:
            d.refresh()

    def detect_game(self, pid: int, name: str, exe_path: str) -> Optional[GameIdentity]:
        """Runs all detectors in order. Returns first matching GameIdentity or None."""
        if not exe_path:
            return None

        # Check if explicitly ignored in DB
        game = self.db.get_game_by_path_or_exe(exe_path, name)
        if game and game.status == "ignored":
            return None

        for detector in self.detectors:
            try:
                identity = detector.detect(pid, name, exe_path)
                if identity:
                    return identity
            except Exception as e:
                logger.error("Detector %s encountered error while checking %s: %s", detector.platform_name, exe_path, e)

        return None
