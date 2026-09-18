"""Configuration loader and validator for GameTimeTracker."""

import json
import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional

from tracker.logger import logger


@dataclass
class NotionConfig:
    token: str = ""
    game_database_id: str = ""
    daily_database_id: str = ""
    game_title_property: str = "Name"
    daily_date_property: str = "日期"
    daily_game_relation_property: str = "游戏"
    daily_playtime_property: str = "时长"
    daily_title_property: str = "游戏名称"
    daily_status_property: str = "绑定状态"
    daily_game_identifier_property: str = "游戏标识"
    auto_create_games: bool = False


@dataclass
class TrackerConfig:
    process_scan_interval_seconds: int = 5
    session_heartbeat_interval_seconds: int = 15
    sync_interval_minutes: int = 15
    overlap_mode: str = "parallel"
    fuzzy_candidate_threshold: float = 80.0
    fuzzy_score_gap_threshold: float = 15.0


@dataclass
class DatabaseConfig:
    db_path: str = "gametime.db"


@dataclass
class CustomPathsConfig:
    steam_libraries: List[str] = field(default_factory=list)
    epic_manifests: List[str] = field(default_factory=list)
    custom_game_directories: List[str] = field(default_factory=list)


@dataclass
class AppConfig:
    notion: NotionConfig = field(default_factory=NotionConfig)
    tracker: TrackerConfig = field(default_factory=TrackerConfig)
    database: DatabaseConfig = field(default_factory=DatabaseConfig)
    custom_paths: CustomPathsConfig = field(default_factory=CustomPathsConfig)
    config_file_path: Optional[Path] = None

    def is_notion_configured(self) -> bool:
        """Checks whether Notion API credentials are fully provided."""
        return bool(
            self.notion.token
            and not self.notion.token.startswith("secret_your_")
            and self.notion.game_database_id
            and not self.notion.game_database_id.startswith("your_")
            and self.notion.daily_database_id
            and not self.notion.daily_database_id.startswith("your_")
        )


def get_base_dir() -> Path:
    """Returns the base project directory."""
    return Path(__file__).resolve().parent.parent


def load_config(config_path: Optional[str] = None) -> AppConfig:
    """Loads configuration from JSON file or environment variables."""
    base_dir = get_base_dir()
    path = Path(config_path) if config_path else base_dir / "config.json"

    data: Dict[str, Any] = {}
    if path.exists():
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
            logger.info("Loaded configuration from %s", path)
        except Exception as e:
            logger.error("Failed to parse config file at %s: %s", path, e)
    else:
        example_path = base_dir / "config.example.json"
        logger.warning("Config file %s not found. Using defaults/env vars.", path)

    notion_data = data.get("notion", {})
    tracker_data = data.get("tracker", {})
    db_data = data.get("database", {})
    paths_data = data.get("custom_paths", {})

    # Environment variable overrides
    token = os.environ.get("NOTION_TOKEN", notion_data.get("token", ""))
    game_db = os.environ.get("GAME_DATABASE_ID", notion_data.get("game_database_id", ""))
    daily_db = os.environ.get("DAILY_DATABASE_ID", notion_data.get("daily_database_id", ""))

    # Normalize database path
    raw_db_path = db_data.get("db_path", "gametime.db")
    if not os.path.isabs(raw_db_path):
        resolved_db_path = str(base_dir / raw_db_path)
    else:
        resolved_db_path = raw_db_path

    config = AppConfig(
        notion=NotionConfig(
            token=token,
            game_database_id=game_db,
            daily_database_id=daily_db,
            game_title_property=notion_data.get("game_title_property", "Name"),
            daily_date_property=notion_data.get("daily_date_property", "日期"),
            daily_game_relation_property=notion_data.get("daily_game_relation_property", "游戏"),
            daily_playtime_property=notion_data.get("daily_playtime_property", "时长"),
            daily_title_property=notion_data.get("daily_title_property", "游戏名称"),
            daily_status_property=notion_data.get("daily_status_property", "绑定状态"),
            daily_game_identifier_property=notion_data.get("daily_game_identifier_property", "游戏标识"),
            auto_create_games=notion_data.get("auto_create_games", False),
        ),
        tracker=TrackerConfig(
            process_scan_interval_seconds=tracker_data.get("process_scan_interval_seconds", 5),
            session_heartbeat_interval_seconds=tracker_data.get("session_heartbeat_interval_seconds", 15),
            sync_interval_minutes=tracker_data.get("sync_interval_minutes", 15),
            overlap_mode=tracker_data.get("overlap_mode", "parallel"),
            fuzzy_candidate_threshold=tracker_data.get("fuzzy_candidate_threshold", 80.0),
            fuzzy_score_gap_threshold=tracker_data.get("fuzzy_score_gap_threshold", 15.0),
        ),
        database=DatabaseConfig(
            db_path=resolved_db_path,
        ),
        custom_paths=CustomPathsConfig(
            steam_libraries=paths_data.get("steam_libraries", []),
            epic_manifests=paths_data.get("epic_manifests", []),
            custom_game_directories=paths_data.get("custom_game_directories", []),
        ),
        config_file_path=path,
    )
    return config
