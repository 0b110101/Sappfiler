"""Platform detectors package."""

from tracker.detectors.base import PlatformDetector
from tracker.detectors.epic import EpicDetector
from tracker.detectors.gog import GOGDetector
from tracker.detectors.heybox import HeyboxDetector
from tracker.detectors.router import DetectorRouter
from tracker.detectors.standalone import StandaloneDetector, compute_path_hash
from tracker.detectors.steam import SteamDetector
from tracker.detectors.xbox import XboxDetector

__all__ = [
    "PlatformDetector",
    "DetectorRouter",
    "SteamDetector",
    "EpicDetector",
    "GOGDetector",
    "HeyboxDetector",
    "XboxDetector",
    "StandaloneDetector",
    "compute_path_hash",
]
