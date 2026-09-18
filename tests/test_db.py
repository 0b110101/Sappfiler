"""Tests for SQLite database layer."""

import os
import tempfile
import pytest
from tracker.db.database import Database
from tracker.models import GameIdentity


@pytest.fixture
def temp_db():
    with tempfile.NamedTemporaryFile(suffix=".db", delete=False) as f:
        db_path = f.name
    db = Database(db_path)
    yield db
    try:
        os.remove(db_path)
        for ext in ["-wal", "-shm"]:
            if os.path.exists(db_path + ext):
                os.remove(db_path + ext)
    except Exception:
        pass


def test_game_crud(temp_db):
    identity = GameIdentity(
        platform="Steam",
        platform_id="1086940",
        name="Baldur's Gate 3",
        executable="bg3.exe",
        executable_path="D:\\steam\\steamapps\\common\\Baldurs Gate 3\\bin\\bg3.exe",
    )
    game = temp_db.get_or_create_game(identity)
    assert game.id is not None
    assert game.name == "Baldur's Gate 3"
    assert game.platform == "Steam"
    assert game.platform_id == "1086940"
    assert game.notion_page_id is None

    # Retrieve again - should return same game ID
    game2 = temp_db.get_or_create_game(identity)
    assert game2.id == game.id

    # Update Notion ID
    temp_db.update_game_notion_id(game.id, "page-notion-12345")
    updated_game = temp_db.get_game_by_id(game.id)
    assert updated_game.notion_page_id == "page-notion-12345"


def test_session_lifecycle(temp_db):
    identity = GameIdentity(
        platform="Steam",
        platform_id="1086940",
        name="Baldur's Gate 3",
        executable="bg3.exe",
        executable_path="D:\\steam\\steamapps\\common\\Baldurs Gate 3\\bin\\bg3.exe",
    )
    game = temp_db.get_or_create_game(identity)

    # 1. Create session
    session = temp_db.create_session(
        game_id=game.id,
        pid=1234,
        process_name="bg3.exe",
        start_time="2026-09-16 18:00:00",
    )
    assert session.is_active == 1
    assert session.start_time == "2026-09-16 18:00:00"

    # 2. Heartbeat
    temp_db.heartbeat_session(session.id, "2026-09-16 18:15:00")
    active = temp_db.get_active_sessions()
    assert len(active) == 1
    assert active[0].last_heartbeat == "2026-09-16 18:15:00"

    # 3. Close
    temp_db.close_session(session.id, "2026-09-16 19:30:00", 5400)
    assert len(temp_db.get_active_sessions()) == 0

    all_s = temp_db.get_all_sessions()
    assert len(all_s) == 1
    assert all_s[0].duration_seconds == 5400
    assert all_s[0].is_active == 0


def test_daily_summary_idempotency(temp_db):
    identity = GameIdentity(
        platform="Steam",
        platform_id="1086940",
        name="Baldur's Gate 3",
        executable="bg3.exe",
        executable_path="D:\\steam\\steamapps\\common\\Baldurs Gate 3\\bin\\bg3.exe",
    )
    game = temp_db.get_or_create_game(identity)

    # Insert initial summary
    summary1 = temp_db.upsert_daily_summary(
        date="2026-09-16",
        game_id=game.id,
        duration_seconds=3600,
        session_count=1,
    )
    assert summary1.duration_minutes == 60
    assert summary1.sync_status == "pending"

    # Upsert with more playtime
    summary2 = temp_db.upsert_daily_summary(
        date="2026-09-16",
        game_id=game.id,
        duration_seconds=5640,
        session_count=2,
    )
    assert summary2.id == summary1.id  # Same record
    assert summary2.duration_minutes == 94
    assert summary2.session_count == 2

    # Mark synced
    temp_db.mark_daily_summary_synced("2026-09-16", game.id, "notion-daily-page-999")
    synced = temp_db.get_daily_summary("2026-09-16", game.id)
    assert synced.sync_status == "synced"
    assert synced.notion_page_id == "notion-daily-page-999"
