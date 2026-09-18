"""Tests for midnight splitting algorithm and daily summary rollups."""

import os
import tempfile
from datetime import datetime
import pytest

from tracker.core.rollup import (
    RollupEngine,
    parse_datetime,
    split_session_by_day,
)
from tracker.db.database import Database
from tracker.models import GameIdentity


def test_split_session_single_day():
    start = datetime(2026, 9, 16, 14, 0, 0)
    end = datetime(2026, 9, 16, 15, 30, 0)
    slices = split_session_by_day(start, end)

    assert len(slices) == 1
    assert slices[0] == ("2026-09-16", 5400)


def test_split_session_cross_midnight():
    # 23:40:00 to 00:35:00 next day
    # Day 1: 20 minutes = 1200 seconds
    # Day 2: 35 minutes = 2100 seconds
    start = datetime(2026, 9, 16, 23, 40, 0)
    end = datetime(2026, 9, 17, 0, 35, 0)
    slices = split_session_by_day(start, end)

    assert len(slices) == 2
    assert slices[0] == ("2026-09-16", 1200)
    assert slices[1] == ("2026-09-17", 2100)


def test_split_session_multi_day():
    # Across 2 midnights
    start = datetime(2026, 9, 15, 23, 0, 0)
    end = datetime(2026, 9, 17, 1, 0, 0)
    slices = split_session_by_day(start, end)

    assert len(slices) == 3
    assert slices[0] == ("2026-09-15", 3600)   # 1 hour
    assert slices[1] == ("2026-09-16", 86400)  # full 24 hours
    assert slices[2] == ("2026-09-17", 3600)   # 1 hour


def test_rollup_engine_with_database():
    with tempfile.NamedTemporaryFile(suffix=".db", delete=False) as f:
        db_path = f.name
    db = Database(db_path)

    game = db.get_or_create_game(
        GameIdentity(
            platform="Steam",
            platform_id="1086940",
            name="Baldur's Gate 3",
            executable="bg3.exe",
            executable_path=r"D:\Steam\steamapps\common\Baldurs Gate 3\bg3.exe",
        )
    )

    # Add a session spanning across midnight
    s = db.create_session(
        game_id=game.id,
        pid=1001,
        process_name="bg3.exe",
        start_time="2026-09-16 23:40:00",
    )
    db.close_session(s.id, "2026-09-17 00:35:00", 3300)

    rollup = RollupEngine(db)

    # Rollup for 2026-09-16
    res16 = rollup.rollup_date("2026-09-16")
    assert len(res16) == 1
    assert res16[0].duration_seconds == 1200
    assert res16[0].duration_minutes == 20

    # Rollup for 2026-09-17
    res17 = rollup.rollup_date("2026-09-17")
    assert len(res17) == 1
    assert res17[0].duration_seconds == 2100
    assert res17[0].duration_minutes == 35

    try:
        os.remove(db_path)
    except Exception:
        pass
