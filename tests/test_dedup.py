"""Unit tests for Notion sync deduplication and error handling."""

import os
import tempfile
from unittest.mock import MagicMock
import pytest

from tracker.config import AppConfig, NotionConfig
from tracker.core.game_matcher import GameMatcher
from tracker.db.database import Database
from tracker.models import GameIdentity
from tracker.notion.sync import NotionSyncEngine


@pytest.fixture
def sync_env():
    with tempfile.NamedTemporaryFile(suffix=".db", delete=False) as f:
        db_path = f.name
    db = Database(db_path)

    config = AppConfig(
        notion=NotionConfig(
            token="secret_test_token_123",
            game_database_id="test_game_db",
            daily_database_id="test_daily_db",
            game_title_property="Name",
            daily_date_property="日期",
            daily_game_relation_property="游戏",
            daily_playtime_property="时长",
        ),
    )

    mock_client = MagicMock()
    mock_client.fetch_all_games.return_value = {
        "notion-game-bg3-page": "Baldur's Gate 3"
    }

    # Pre-populate game
    game = db.get_or_create_game(
        GameIdentity(
            platform="Steam",
            platform_id="1086940",
            name="Baldur's Gate 3",
            executable="bg3.exe",
            executable_path=r"D:\Steam\steamapps\common\Baldurs Gate 3\bg3.exe",
        )
    )
    db.update_game_notion_id(game.id, "notion-game-bg3-page")

    engine = NotionSyncEngine(config, db, client=mock_client)

    yield {"db": db, "engine": engine, "client": mock_client, "game": game, "db_path": db_path}

    try:
        os.remove(db_path)
    except Exception:
        pass


def test_first_time_sync_creates_record(sync_env):
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]
    game = sync_env["game"]

    # Insert daily summary
    db.upsert_daily_summary("2026-09-16", game.id, duration_seconds=5640, session_count=1)

    # Remote returns None on query
    client.query_daily_playtime_page.return_value = None
    client.create_daily_playtime_page.return_value = "new_remote_page_123"

    success, fail = engine.sync_pending()
    assert success == 1
    assert fail == 0

    client.create_daily_playtime_page.assert_called_once_with(
        database_id="test_daily_db",
        date_property="日期",
        date_str="2026-09-16",
        playtime_property="时长",
        playtime_minutes=94,
        title_property="游戏名称",
        game_name="Baldur's Gate 3",
        relation_property="游戏",
        game_page_id="notion-game-bg3-page",
        status_property="绑定状态",
        binding_status="已绑定",
        identifier_property="游戏标识",
        game_identifier="Steam:1086940",
    )

    summary = db.get_daily_summary("2026-09-16", game.id)
    assert summary.sync_status == "synced"
    assert summary.notion_page_id == "new_remote_page_123"


def test_second_time_sync_updates_record(sync_env):
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]
    game = sync_env["game"]

    # Local record already has notion_page_id from previous sync
    db.upsert_daily_summary(
        "2026-09-16",
        game.id,
        duration_seconds=7200,
        session_count=2,
        sync_status="pending",
        notion_page_id="existing_page_456",
    )

    success, fail = engine.sync_pending()
    assert success == 1
    assert fail == 0

    # Must call update, NEVER create
    client.update_daily_playtime_page.assert_called_once_with(
        page_id="existing_page_456",
        playtime_property="时长",
        playtime_minutes=120,
        title_property="游戏名称",
        game_name="Baldur's Gate 3",
        relation_property="游戏",
        game_page_id="notion-game-bg3-page",
        status_property="绑定状态",
        binding_status="已绑定",
    )
    client.create_daily_playtime_page.assert_not_called()


def test_remote_exists_prevents_duplicate_creation(sync_env):
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]
    game = sync_env["game"]

    # Local record has NO notion_page_id
    db.upsert_daily_summary(
        "2026-09-16",
        game.id,
        duration_seconds=3600,
        session_count=1,
        sync_status="pending",
        notion_page_id=None,
    )

    # But remote query finds an existing page!
    client.query_daily_playtime_page.return_value = "found_remote_page_789"

    success, fail = engine.sync_pending()
    assert success == 1
    assert fail == 0

    # Must update existing, NEVER create duplicate!
    client.update_daily_playtime_page.assert_called_once_with(
        page_id="found_remote_page_789",
        playtime_property="时长",
        playtime_minutes=60,
        title_property="游戏名称",
        game_name="Baldur's Gate 3",
        relation_property="游戏",
        game_page_id="notion-game-bg3-page",
        status_property="绑定状态",
        binding_status="已绑定",
    )
    client.create_daily_playtime_page.assert_not_called()

    # Local record is now updated with remote ID
    summary = db.get_daily_summary("2026-09-16", game.id)
    assert summary.notion_page_id == "found_remote_page_789"


def test_offline_failure_retains_local_data(sync_env):
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]
    game = sync_env["game"]

    db.upsert_daily_summary(
        "2026-09-16",
        game.id,
        duration_seconds=3600,
        session_count=1,
        sync_status="pending",
    )

    # Simulate network failure
    client.query_daily_playtime_page.side_effect = RuntimeError("Connection timed out")

    success, fail = engine.sync_pending()
    assert success == 0
    assert fail == 1

    summary = db.get_daily_summary("2026-09-16", game.id)
    assert summary.sync_status == "failed"
    assert "Connection timed out" in summary.error_message
    assert summary.duration_seconds == 3600  # Local data preserved!


def test_unmapped_game_syncs_to_daily_table_without_relation(sync_env):
    """Task A: New game not in Notion master database must still sync daily playtime."""
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]

    # Register an unmapped game
    new_game = db.get_or_create_game(
        GameIdentity(
            platform="Steam",
            platform_id="2246340",
            name="Monster Hunter Wilds",
            executable="MonsterHunterWilds.exe",
            executable_path=r"D:\Steam\steamapps\common\Monster Hunter Wilds\MonsterHunterWilds.exe",
        )
    )
    assert new_game.notion_page_id is None

    # Insert daily playtime
    db.upsert_daily_summary("2026-09-17", new_game.id, duration_seconds=4500, session_count=1)

    client.query_daily_playtime_page.return_value = None
    client.create_daily_playtime_page.return_value = "mhw_daily_page_999"

    # Master table returns empty (game not yet in master table)
    client.fetch_all_games.return_value = {}

    success, fail = engine.sync_pending()
    assert success == 1
    assert fail == 0

    # Must create daily page with '未绑定' and NO relation, duration 75 min
    client.create_daily_playtime_page.assert_called_once_with(
        database_id="test_daily_db",
        date_property="日期",
        date_str="2026-09-17",
        playtime_property="时长",
        playtime_minutes=75,
        title_property="游戏名称",
        game_name="Monster Hunter Wilds",
        relation_property=None,
        game_page_id=None,
        status_property="绑定状态",
        binding_status="未绑定",
        identifier_property="游戏标识",
        game_identifier="Steam:2246340",
    )

    summary = db.get_daily_summary("2026-09-17", new_game.id)
    assert summary.sync_status == "unmapped"
    assert summary.notion_page_id == "mhw_daily_page_999"


def test_task_b_backfills_unmapped_records_when_game_bound(sync_env):
    """Task B: When master table gains the game, backfills relation onto daily record."""
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]

    new_game = db.get_or_create_game(
        GameIdentity(
            platform="Steam",
            platform_id="2246340",
            name="Monster Hunter Wilds",
            executable="MonsterHunterWilds.exe",
            executable_path=r"D:\Steam\steamapps\common\Monster Hunter Wilds\MonsterHunterWilds.exe",
        )
    )

    # Already synced unmapped
    db.upsert_daily_summary(
        "2026-09-17",
        new_game.id,
        duration_seconds=4500,
        session_count=1,
        sync_status="unmapped",
        notion_page_id="mhw_daily_page_999",
    )

    # Master database now contains the game!
    client.fetch_all_games.return_value = {
        "mhw_master_page_111": "Monster Hunter Wilds"
    }

    # Run Task B / sync_pending
    backfilled = engine.run_task_b_backfill_relations()
    assert backfilled == 1

    # Verified game was bound locally
    updated_game = db.get_game_by_id(new_game.id)
    assert updated_game.notion_page_id == "mhw_master_page_111"

    # Verified Notion daily page was patched with relation and '已绑定'
    client.update_daily_playtime_page.assert_called_once_with(
        page_id="mhw_daily_page_999",
        relation_property="游戏",
        game_page_id="mhw_master_page_111",
        status_property="绑定状态",
        binding_status="已绑定",
    )

    # Local summary updated to synced
    summary = db.get_daily_summary("2026-09-17", new_game.id)
    assert summary.sync_status == "synced"


def test_stale_and_ignored_records_are_cleaned_and_not_synced(sync_env):
    """Ignored games must have summaries removed and NOT have pending summaries synced."""
    db = sync_env["db"]

    # 1. Active game with summary
    active_game = db.get_or_create_game(
        GameIdentity(
            platform="Steam",
            platform_id="111111",
            name="ValidActiveGame",
            executable="valid.exe",
            executable_path=r"C:\valid.exe",
        )
    )
    db.upsert_daily_summary("2026-09-17", active_game.id, duration_seconds=300, session_count=1)

    # 2. Ignored game with summary
    ignored_game = db.get_or_create_game(
        GameIdentity(
            platform="Xbox",
            platform_id="fake_ignored_app",
            name="FakeIgnoredApp",
            executable="fake.exe",
            executable_path=r"C:\fake.exe",
        )
    )
    db.upsert_daily_summary("2026-09-17", ignored_game.id, duration_seconds=600, session_count=1)

    # Marking as ignored purges its daily summaries and sessions
    db.update_game_status(ignored_game.id, "ignored")

    # Verify ignored game's daily summary was cleaned up
    assert db.get_daily_summary("2026-09-17", ignored_game.id) is None

    # Verify pending summaries only contain active games
    pending = db.get_pending_sync_summaries()
    assert any(p.game_id == active_game.id for p in pending)
    assert not any(p.game_id == ignored_game.id for p in pending)


def test_create_master_game_and_bind(sync_env):
    """create_master_game_and_bind creates master page and backfills unmapped daily records."""
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]

    # 1. New game without Notion page
    new_game = db.get_or_create_game(
        GameIdentity(
            platform="Steam",
            platform_id="999888",
            name="BrandNewGame",
            executable="bng.exe",
            executable_path=r"C:\bng.exe",
        )
    )
    db.upsert_daily_summary("2026-09-17", new_game.id, duration_seconds=1200, session_count=1)
    db.mark_daily_summary_unmapped("2026-09-17", new_game.id, "daily_page_bng")

    client.create_game_page.return_value = "new_master_page_id_123"

    new_page_id = engine.create_master_game_and_bind(new_game.id)
    assert new_page_id == "new_master_page_id_123"

    # Verify game record updated
    updated_game = db.get_game_by_id(new_game.id)
    assert updated_game.notion_page_id == "new_master_page_id_123"

    # Verify Task B backfill was triggered and updated daily page
    client.update_daily_playtime_page.assert_called_with(
        page_id="daily_page_bng",
        relation_property="游戏",
        game_page_id="new_master_page_id_123",
        status_property="绑定状态",
        binding_status="已绑定",
    )


def test_system_tray_sync_engine_wiring(sync_env):
    """SystemTrayApp properly forwards sync_engine to UserPromptManager."""
    from tracker.ui.tray import SystemTrayApp
    from tracker.ui.prompt import UserPromptManager

    db = sync_env["db"]
    engine = sync_env["engine"]
    mock_session_mgr = MagicMock()

    tray = SystemTrayApp(
        db=db,
        session_manager=mock_session_mgr,
        sync_engine=engine,
    )

    assert tray.sync_engine is engine
    assert tray.prompt_mgr is not None
    assert tray.prompt_mgr.sync_engine is engine


def test_remote_master_page_deleted_unbinds_local_game(sync_env):
    """If user deletes a game in Notion master DB, local game unbinds notion_page_id without losing local data."""
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]

    # Game currently bound to 'old_deleted_page'
    game = sync_env["game"]
    db.update_game_notion_id(game.id, "old_deleted_page")

    # Notion remote returns empty or other games (old_deleted_page is deleted!)
    client.fetch_all_games.return_value = {
        "some_other_page": "OtherGame"
    }

    engine.auto_bind_unmapped_games()

    # Verify local SQLite game is still active, but notion_page_id is unbound (None)
    updated = db.get_game_by_id(game.id)
    assert updated.notion_page_id is None
    assert updated.status == "active"
    assert updated.name == "Baldur's Gate 3"


def test_remote_daily_page_deleted_404_recreates_page(sync_env):
    """If user deletes a daily row in Notion, 404 is caught and the row is automatically re-created."""
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]
    game = sync_env["game"]

    # Local record has daily_page_old recorded and is pending sync
    db.upsert_daily_summary(
        "2026-09-17",
        game.id,
        duration_seconds=1800,
        session_count=1,
        sync_status="pending",
        notion_page_id="daily_page_old",
    )

    # Simulate Notion update returning 404 (Could not find page)
    client.update_daily_playtime_page.side_effect = RuntimeError("Could not find page with ID: daily_page_old (404)")
    client.query_daily_playtime_page.return_value = None
    client.create_daily_playtime_page.return_value = "new_recreated_daily_page"

    succ, fail = engine.run_task_a_sync_daily()
    assert succ == 1
    assert fail == 0

    # Verify new page was created and local summary updated
    client.create_daily_playtime_page.assert_called_once()
    summary = db.get_daily_summary("2026-09-17", game.id)
    assert summary.notion_page_id == "new_recreated_daily_page"
    assert summary.sync_status == "synced"


def test_clear_game_playtime_archives_notion(sync_env):
    """clear_game_playtime deletes local playtime and archives daily pages in Notion."""
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]
    game = sync_env["game"]

    db.upsert_daily_summary("2026-09-17", game.id, duration_seconds=1200, session_count=1)
    db.mark_daily_summary_synced("2026-09-17", game.id, "page_to_archive_123")
    client.archive_page.return_value = True

    archived_count = engine.clear_game_playtime(game.id, archive_notion=True)
    assert archived_count == 1
    client.archive_page.assert_called_with("page_to_archive_123")

    # Local summary and sessions must be gone
    assert db.get_daily_summary("2026-09-17", game.id) is None
    # Game itself must still exist!
    assert db.get_game_by_id(game.id) is not None


def test_clear_all_playtime_data(sync_env):
    """clear_all_playtime clears all playtime locally and archives all daily pages in Notion."""
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]
    game = sync_env["game"]

    db.upsert_daily_summary("2026-09-16", game.id, duration_seconds=600, session_count=1)
    db.mark_daily_summary_synced("2026-09-16", game.id, "pid_1")
    db.upsert_daily_summary("2026-09-17", game.id, duration_seconds=1200, session_count=1)
    db.mark_daily_summary_synced("2026-09-17", game.id, "pid_2")

    client.archive_page.return_value = True

    archived_count = engine.clear_all_playtime(archive_notion=True)
    assert archived_count == 2
    assert client.archive_page.call_count >= 2

    # All daily summaries are empty
    assert len(db.get_daily_summaries_by_date("2026-09-16")) == 0
    assert len(db.get_daily_summaries_by_date("2026-09-17")) == 0


def test_sync_remote_deletions_cleans_local_daily_record(sync_env):
    """When a user deletes a daily row in Notion, sync_remote_deletions deletes it locally."""
    db = sync_env["db"]
    engine = sync_env["engine"]
    client = sync_env["client"]
    game = sync_env["game"]

    # Local record marked synced to pid_test_deleted
    db.upsert_daily_summary("2026-09-17", game.id, duration_seconds=1800, session_count=1)
    db.mark_daily_summary_synced("2026-09-17", game.id, "pid_test_deleted")

    # Notion reports this page was archived / deleted by user
    client.is_page_archived_or_deleted.return_value = True

    deleted_count = engine.sync_remote_deletions()
    assert deleted_count == 1

    # Local summary must be completely deleted!
    assert db.get_daily_summary("2026-09-17", game.id) is None





