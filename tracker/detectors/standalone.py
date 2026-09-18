"""Standalone and local database detector for non-platform or manually mapped games."""

import hashlib
import os
from typing import Optional

from tracker.db.database import Database
from tracker.detectors.base import PlatformDetector
from tracker.models import GameIdentity, GameRecord


def compute_path_hash(exe_path: str) -> str:
    """Computes a stable hash for a standalone game executable path."""
    norm = os.path.normcase(os.path.normpath(exe_path))
    return hashlib.sha256(norm.encode("utf-8")).hexdigest()[:16]


class StandaloneDetector(PlatformDetector):
    """Detects games already registered in the local SQLite database."""

    def __init__(self, db: Database):
        self.db = db

    @property
    def platform_name(self) -> str:
        return "Standalone"

    def detect(self, pid: int, name: str, exe_path: str) -> Optional[GameIdentity]:
        if not exe_path:
            return None

        # Look up in database
        game = self.db.get_game_by_path_or_exe(exe_path, name)
        if game and game.status == "active":
            return GameIdentity(
                platform=game.platform,
                platform_id=game.platform_id,
                name=game.name,
                executable=game.executable,
                executable_path=game.executable_path or exe_path,
            )

        return None
