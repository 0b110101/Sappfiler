"""Data models for GameTimeTracker."""

from dataclasses import dataclass, field
from datetime import datetime
from typing import List, Optional


@dataclass
class GameIdentity:
    """Represents a game identity detected from a running process."""
    platform: str                # 'Steam', 'Epic', 'Xbox', 'Standalone'
    platform_id: str             # AppID, CatalogItemId, or path hash
    name: str                    # Game display name
    executable: str              # Process name (e.g. bg3.exe)
    executable_path: str         # Full path to executable


@dataclass
class GameRecord:
    """Database representation of a registered game."""
    id: Optional[int] = None
    platform: str = "Standalone"
    platform_id: str = ""
    name: str = ""
    executable: str = ""
    executable_path: str = ""
    notion_page_id: Optional[str] = None
    status: str = "active"       # 'active', 'ignored', 'unconfirmed'
    created_at: Optional[str] = None
    updated_at: Optional[str] = None


@dataclass
class SessionRecord:
    """Database representation of a game play session."""
    id: Optional[int] = None
    game_id: int = 0
    pid: int = 0
    process_name: str = ""
    start_time: str = ""         # YYYY-MM-DD HH:MM:SS
    end_time: Optional[str] = None
    last_heartbeat: str = ""     # YYYY-MM-DD HH:MM:SS
    duration_seconds: int = 0
    is_active: int = 1
    created_at: Optional[str] = None


@dataclass
class DailySummaryRecord:
    """Database representation of daily playtime summary per game."""
    id: Optional[int] = None
    date: str = ""               # YYYY-MM-DD
    game_id: int = 0
    duration_seconds: int = 0
    duration_minutes: int = 0
    session_count: int = 0
    sync_status: str = "pending" # 'pending', 'synced', 'failed', 'unmapped'
    notion_page_id: Optional[str] = None  # Daily Playtime Notion Page ID
    last_sync_at: Optional[str] = None
    error_message: Optional[str] = None


@dataclass
class DailySlice:
    """A sliced portion of a session belonging to a specific calendar date."""
    date: str                    # YYYY-MM-DD
    game_id: int
    duration_seconds: int


@dataclass
class NotionMasterGame:
    """Represents a game entry in Notion 游戏总表 with title, aliases (e.g. 全名/英文名), and identifiers."""
    page_id: str
    title: str
    aliases: List[str] = field(default_factory=list)
    identifiers: List[str] = field(default_factory=list)

    def __str__(self) -> str:
        return self.title
