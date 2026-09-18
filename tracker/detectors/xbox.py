"""Xbox PC / Microsoft Store / Game Pass detector."""

import os
import re
from typing import Optional

from tracker.detectors.base import PlatformDetector
from tracker.models import GameIdentity

# Known Xbox first-party and Game Pass game indicators in WindowsApps
KNOWN_XBOX_STORE_GAMES = (
    "halo", "forza", "ageofempires", "seaofthieves", "minecraft", "gears",
    "psychonauts", "flightsimulator", "stateofdecay", "grounded", "starfield",
    "fallout", "elder_scrolls", "doom", "avowed", "clockworkrevolution",
)


class XboxDetector(PlatformDetector):
    """Detects games launched via Xbox PC / Microsoft Store / Game Pass."""

    @property
    def platform_name(self) -> str:
        return "Xbox"

    def detect(self, pid: int, name: str, exe_path: str) -> Optional[GameIdentity]:
        if not exe_path:
            return None

        norm_path = os.path.normpath(exe_path)
        parts = norm_path.split(os.sep)

        # Case 1: Under XboxGames directory (Official directory for Xbox App / PC Game Pass games)
        for i, part in enumerate(parts):
            if part.lower() == "xboxgames" and i + 1 < len(parts):
                game_folder = parts[i + 1]
                return GameIdentity(
                    platform="Xbox",
                    platform_id=f"xbox_{game_folder.lower()}",
                    name=game_folder,
                    executable=name,
                    executable_path=exe_path,
                )

        # Case 2: WindowsApps directory
        # CRITICAL: WindowsApps contains ALL Microsoft Store apps (Apple Music, Spotify, WeChat, Bilibili, etc.).
        # We MUST NOT treat arbitrary Store apps as Xbox games unless they are explicitly verified Xbox games.
        for i, part in enumerate(parts):
            if part.lower() == "windowsapps" and i + 1 < len(parts):
                package_folder = parts[i + 1]
                pkg_lower = package_folder.lower()

                # Strictly require the package to match known Xbox Game Pass / Xbox Studios titles
                is_xbox_game = any(g in pkg_lower for g in KNOWN_XBOX_STORE_GAMES)
                if not is_xbox_game:
                    return None

                # Extract display title
                m = re.match(r"^([A-Za-z0-9]+)\.([A-Za-z0-9_-]+)_", package_folder)
                if m:
                    display = m.group(2)
                else:
                    display = package_folder.split("_")[0]

                return GameIdentity(
                    platform="Xbox",
                    platform_id=package_folder,
                    name=display,
                    executable=name,
                    executable_path=exe_path,
                )

        return None
