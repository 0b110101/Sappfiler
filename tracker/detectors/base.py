"""Base interface for platform detectors."""

from abc import ABC, abstractmethod
from typing import Optional

from tracker.models import GameIdentity


class PlatformDetector(ABC):
    """Abstract base class for game platform detectors."""

    @property
    @abstractmethod
    def platform_name(self) -> str:
        """Returns the platform name (e.g., 'Steam', 'Epic', 'Xbox', 'Standalone')."""
        pass

    @abstractmethod
    def detect(self, pid: int, name: str, exe_path: str) -> Optional[GameIdentity]:
        """Inspects the running process and returns a GameIdentity if matched."""
        pass

    def refresh(self) -> None:
        """Optional hook to re-scan libraries or manifests from disk."""
        pass
