"""Unit tests for session manager and crash recovery."""

import os
import tempfile
from datetime import datetime
import pytest

from tracker.core.rollup import RollupEngine
from tracker.core.session_manager import SessionManager
from tracker.db.database import Database
from tracker.models import GameIdentity


def test_crash_recovery_conservative():
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

    # Insert an unclosed session simulating a crash (e.g. power loss)
    # Start: 18:00:00, Last Heartbeat: 18:25:00
    # Process PID 99999999 (non-existent)
    with db.transaction() as cur:
        cur.execute(
            """
            INSERT INTO sessions (game_id, pid, process_name, start_time, last_heartbeat, duration_seconds, is_active)
            VALUES (?, 99999999, 'bg3.exe', '2026-09-16 18:00:00', '2026-09-16 18:25:00', 1500, 1);
            """,
            (game.id,),
        )

    # Initialize SessionManager (which runs recover_crashed_sessions automatically)
    rollup = RollupEngine(db)
    sm = SessionManager(db, rollup)

    # Verify session is no longer active
    active = db.get_active_sessions()
    assert len(active) == 0

    # Verify session end_time equals last_heartbeat
    all_sessions = db.get_all_sessions()
    assert len(all_sessions) == 1
    recovered = all_sessions[0]
    assert recovered.is_active == 0
    assert recovered.end_time == "2026-09-16 18:25:00"
    assert recovered.duration_seconds == 1500  # 25 minutes exactly

    # Verify daily summary was updated
    summary = db.get_daily_summary("2026-09-16", game.id)
    assert summary is not None
    assert summary.duration_minutes == 25

    try:
        os.remove(db_path)
    except Exception:
        pass
