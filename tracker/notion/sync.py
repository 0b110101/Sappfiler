"""Notion synchronization engine with two-tier deduplication and safe retry."""

from typing import Dict, List, Optional, Tuple

from tracker.config import AppConfig
from tracker.core.game_matcher import GameMatcher
from tracker.db.database import Database
from tracker.logger import logger
from tracker.models import DailySummaryRecord, GameRecord
from tracker.notion.client import NotionClient


class NotionSyncEngine:
    """Coordinates local SQLite daily summaries and Notion databases."""

    def __init__(
        self,
        config: AppConfig,
        db: Database,
        client: Optional[NotionClient] = None,
        matcher: Optional[GameMatcher] = None,
    ):
        self.config = config
        self.db = db
        self.client = client or (NotionClient(config.notion.token) if config.is_notion_configured() else None)
        self.matcher = matcher or GameMatcher(
            fuzzy_threshold=config.tracker.fuzzy_candidate_threshold,
            score_gap_threshold=config.tracker.fuzzy_score_gap_threshold,
        )
        self._master_games_cache: Dict[str, str] = {}  # page_id -> title

    def refresh_master_games(self) -> Dict[str, str]:
        """Fetches all games from Notion 游戏总表 into in-memory cache."""
        if not self.client or not self.config.is_notion_configured():
            return {}
        try:
            self._master_games_cache = self.client.fetch_all_games(
                database_id=self.config.notion.game_database_id,
                title_property=self.config.notion.game_title_property,
            )
            return self._master_games_cache
        except Exception as e:
            logger.error("Failed to fetch master games from Notion: %s", e)
            return {}

    def auto_bind_unmapped_games(self) -> int:
        """Attempts to match and bind unmapped local active games to Notion 游戏总表."""
        if not self._master_games_cache:
            self.refresh_master_games()

        if not self._master_games_cache:
            return 0

        all_games = self.db.list_all_games()
        clean_master_ids = {k.replace("-", "") for k in self._master_games_cache.keys()}

        # 1. Detect remote deletions in Notion 游戏总表: if page was deleted, unbind locally
        for game in all_games:
            if game.status == "active" and game.notion_page_id:
                clean_bound_id = game.notion_page_id.replace("-", "")
                if clean_bound_id not in clean_master_ids:
                    logger.info("Notion master page '%s' for game '%s' was deleted/archived remotely. Unbinding locally.", game.notion_page_id, game.name)
                    self.db.update_game_notion_id(game.id, None)
                    game.notion_page_id = None

        bound_count = 0

        for game in all_games:
            if game.status == "active" and not game.notion_page_id:
                candidate = self.matcher.match(game, self._master_games_cache)
                if candidate:
                    self.db.update_game_notion_id(game.id, candidate.page_id)
                    logger.info(
                        "Auto-bound game '%s' (%s %s) to Notion master page '%s' (%s, match: %s)",
                        game.name,
                        game.platform,
                        game.platform_id,
                        candidate.title,
                        candidate.page_id,
                        candidate.match_type,
                    )
                    bound_count += 1

        return bound_count

    def run_task_b_backfill_relations(self) -> int:
        """Task B: Compensates missing relations on Notion daily pages when games become bound.
        
        1. Auto-binds unmapped local games against Notion 游戏总表.
        2. Scans all daily summaries with sync_status='unmapped' for games that now have a Notion Page ID.
        3. Updates the Notion daily page to add the relation and switch status to '已绑定'.
        """
        if not self.client or not self.config.is_notion_configured():
            return 0

        # 1. Attempt to auto-bind any unmapped active games
        self.auto_bind_unmapped_games()

        # 2. Find daily summaries that were synced unmapped
        unmapped_summaries = self.db.get_unmapped_synced_summaries()
        if not unmapped_summaries:
            return 0

        backfilled_count = 0
        notion_cfg = self.config.notion

        for item in unmapped_summaries:
            game = self.db.get_game_by_id(item.game_id)
            if not game or game.status != "active":
                continue

            # If game is not bound locally yet, check if user manually picked a Relation in Notion UI
            if not game.notion_page_id and item.notion_page_id:
                try:
                    daily_page = self.client.get_page(item.notion_page_id)
                    rel = daily_page.get("properties", {}).get(notion_cfg.daily_game_relation_property, {}).get("relation", [])
                    if rel:
                        manual_master_id = rel[0]["id"]
                        self.db.update_game_notion_id(game.id, manual_master_id)
                        game = self.db.get_game_by_id(game.id)
                        logger.info("Auto-learned manual relation for game '%s' from Notion daily page -> %s", game.name, manual_master_id)
                except Exception as e:
                    logger.debug("Could not check manual relation on page %s: %s", item.notion_page_id, e)

            if not game.notion_page_id:
                continue

            # This game is now bound! Backfill relation onto Notion daily page
            try:
                daily_page_id = item.notion_page_id
                if not daily_page_id:
                    # Query Notion daily database by date + identifier
                    game_identifier = f"{game.platform}:{game.platform_id}"
                    daily_page_id = self.client.query_daily_playtime_page(
                        database_id=notion_cfg.daily_database_id,
                        date_property=notion_cfg.daily_date_property,
                        date_str=item.date,
                        identifier_property=notion_cfg.daily_game_identifier_property,
                        game_identifier=game_identifier,
                        title_property=notion_cfg.daily_title_property,
                        game_name=game.name,
                    )

                if daily_page_id:
                    try:
                        self.client.update_daily_playtime_page(
                            page_id=daily_page_id,
                            relation_property=notion_cfg.daily_game_relation_property,
                            game_page_id=game.notion_page_id,
                            status_property=notion_cfg.daily_status_property,
                            binding_status="已绑定",
                        )
                        self.db.mark_daily_summary_synced(item.date, item.game_id, daily_page_id)
                        logger.info(
                            "Task B: Successfully backfilled relation for %s (%s) -> Notion master page %s",
                            game.name,
                            item.date,
                            game.notion_page_id,
                        )
                        backfilled_count += 1
                    except Exception as e:
                        err_str = str(e).lower()
                        is_404 = (
                            (hasattr(e, "response") and getattr(e.response, "status_code", None) == 404)
                            or "404" in err_str
                            or "could not find page" in err_str
                            or "object_not_found" in err_str
                        )
                        if is_404:
                            logger.info("Daily page %s no longer exists remotely in Notion. Re-queuing for Task A...", daily_page_id)
                            self.db.update_daily_summary_sync_status(item.date, item.game_id, "pending", None)
                        else:
                            raise
                else:
                    # Daily page doesn't exist yet on remote, re-queue for Task A
                    self.db.mark_daily_summary_failed(item.date, item.game_id, "Pending daily page creation")
            except Exception as e:
                logger.warning(
                    "Task B: Failed to backfill relation for %s on %s: %s",
                    game.name,
                    item.date,
                    e,
                )

        if backfilled_count > 0:
            logger.info("Task B: Backfilled relations for %d daily records in Notion", backfilled_count)
        return backfilled_count

    def run_task_a_sync_daily(self) -> Tuple[int, int]:
        """Task A: Synchronizes daily playtime records to Notion '每日游戏时长' database.
        
        Zero data loss principle:
        - If the game is already bound to Notion 游戏总表, writes Relation and sets status '已绑定'.
        - If the game is NOT bound yet, writes Game Name and Playtime, leaves Relation empty,
          sets status '未绑定', and saves local sync_status as 'unmapped'.
        """
        if not self.client or not self.config.is_notion_configured():
            logger.debug("Notion sync skipped: credentials not configured.")
            return (0, 0)

        pending = self.db.get_pending_sync_summaries()
        if not pending:
            logger.debug("Task A: No pending daily summaries to sync.")
            return (0, 0)

        logger.info("Task A: Found %d pending daily summary records to sync to Notion", len(pending))
        success_count = 0
        fail_count = 0
        notion_cfg = self.config.notion

        for item in pending:
            game = self.db.get_game_by_id(item.game_id)
            if not game or game.status != "active":
                continue

            game_identifier = f"{game.platform}:{game.platform_id}"
            is_bound = bool(game.notion_page_id)
            binding_status = "已绑定" if is_bound else "未绑定"

            try:
                target_page_id = item.notion_page_id

                # Tier 1 Idempotency: Local Daily Summary Notion Page ID
                if target_page_id:
                    try:
                        self.client.update_daily_playtime_page(
                            page_id=target_page_id,
                            playtime_property=notion_cfg.daily_playtime_property,
                            playtime_minutes=item.duration_minutes,
                            title_property=notion_cfg.daily_title_property,
                            game_name=game.name,
                            relation_property=notion_cfg.daily_game_relation_property if is_bound else None,
                            game_page_id=game.notion_page_id if is_bound else None,
                            status_property=notion_cfg.daily_status_property,
                            binding_status=binding_status,
                        )
                    except Exception as e:
                        err_str = str(e).lower()
                        is_404 = (
                            (hasattr(e, "response") and getattr(e.response, "status_code", None) == 404)
                            or "404" in err_str
                            or "could not find page" in err_str
                            or "object_not_found" in err_str
                        )
                        if is_404:
                            logger.info("Daily page %s for '%s' was deleted remotely in Notion. Re-creating page...", target_page_id, game.name)
                            target_page_id = None
                        else:
                            raise

                if not target_page_id:
                    # Tier 2 Idempotency: Query Notion by Date + (Relation or Identifier/Title)
                    existing_id = self.client.query_daily_playtime_page(
                        database_id=notion_cfg.daily_database_id,
                        date_property=notion_cfg.daily_date_property,
                        date_str=item.date,
                        relation_property=notion_cfg.daily_game_relation_property if is_bound else None,
                        game_page_id=game.notion_page_id if is_bound else None,
                        identifier_property=notion_cfg.daily_game_identifier_property if not is_bound else None,
                        game_identifier=game_identifier if not is_bound else None,
                        title_property=notion_cfg.daily_title_property if not is_bound else None,
                        game_name=game.name if not is_bound else None,
                    )

                    if existing_id:
                        # Update existing page
                        self.client.update_daily_playtime_page(
                            page_id=existing_id,
                            playtime_property=notion_cfg.daily_playtime_property,
                            playtime_minutes=item.duration_minutes,
                            title_property=notion_cfg.daily_title_property,
                            game_name=game.name,
                            relation_property=notion_cfg.daily_game_relation_property if is_bound else None,
                            game_page_id=game.notion_page_id if is_bound else None,
                            status_property=notion_cfg.daily_status_property,
                            binding_status=binding_status,
                        )
                        target_page_id = existing_id
                    else:
                        # Create new page in Notion Daily DB
                        new_id = self.client.create_daily_playtime_page(
                            database_id=notion_cfg.daily_database_id,
                            date_property=notion_cfg.daily_date_property,
                            date_str=item.date,
                            playtime_property=notion_cfg.daily_playtime_property,
                            playtime_minutes=item.duration_minutes,
                            title_property=notion_cfg.daily_title_property,
                            game_name=game.name,
                            relation_property=notion_cfg.daily_game_relation_property if is_bound else None,
                            game_page_id=game.notion_page_id if is_bound else None,
                            status_property=notion_cfg.daily_status_property,
                            binding_status=binding_status,
                            identifier_property=notion_cfg.daily_game_identifier_property,
                            game_identifier=game_identifier,
                        )
                        target_page_id = new_id

                # Update local sync status
                if is_bound:
                    self.db.mark_daily_summary_synced(item.date, item.game_id, target_page_id)
                else:
                    self.db.mark_daily_summary_unmapped(item.date, item.game_id, target_page_id)

                success_count += 1

            except Exception as e:
                logger.error("Failed to sync daily summary for date %s, game %s: %s", item.date, game.name, e)
                self.db.mark_daily_summary_failed(item.date, item.game_id, str(e))
                fail_count += 1

        logger.info("Task A: Notion sync completed: %d synced successfully, %d failed", success_count, fail_count)
        return (success_count, fail_count)

    def sync_pending(self) -> Tuple[int, int]:
        """Runs the complete two-task sync pipeline:
        1. Cleans up any stale/ignored records.
        2. Task B: Backfills relations for newly mapped games.
        3. Task A: Syncs pending daily summaries to Notion.
        """
        if not self.client or not self.config.is_notion_configured():
            logger.debug("Notion sync skipped: credentials not configured.")
            return (0, 0)

        # 0. Clean stale records
        self.db.cleanup_stale_records()

        # 1. Sync remote deletions from Notion to local SQLite
        self.sync_remote_deletions()

        # 2. Task B: Backfill relations on existing daily records
        self.run_task_b_backfill_relations()

        # 3. Task A: Sync pending daily summaries
        return self.run_task_a_sync_daily()

    def sync_remote_deletions(self, limit: int = 50) -> int:
        """Scans local synced daily records; if the corresponding page in Notion
        was deleted/archived by the user, deletes the local record to mirror Notion!
        """
        if not self.client or not self.config.is_notion_configured():
            return 0

        synced_records = self.db.get_synced_daily_summaries(limit=limit)
        deleted_count = 0

        for item in synced_records:
            if not item.notion_page_id:
                continue
            if self.client.is_page_archived_or_deleted(item.notion_page_id):
                game = self.db.get_game_by_id(item.game_id)
                g_name = game.name if game else f"Game #{item.game_id}"
                logger.info(
                    "Detected that Notion page %s for '%s' (%s) was deleted/archived in Notion. Syncing deletion to local database...",
                    item.notion_page_id,
                    g_name,
                    item.date,
                )
                self.db.delete_daily_summary(item.date, item.game_id)
                deleted_count += 1

        if deleted_count > 0:
            logger.info("Synced %d remote deletions from Notion to local SQLite.", deleted_count)
        return deleted_count

    def create_master_game_and_bind(self, game_id: int) -> Optional[str]:
        """Creates a new game page in Notion 游戏总表 with title only, and immediately backfills relations."""
        if not self.client or not self.config.is_notion_configured():
            return None
        game = self.db.get_game_by_id(game_id)
        if not game:
            return None
        try:
            new_page_id = self.client.create_game_page(
                database_id=self.config.notion.game_database_id,
                title_property=self.config.notion.game_title_property,
                game_name=game.name,
            )
            self.db.update_game_notion_id(game.id, new_page_id)
            self._master_games_cache[new_page_id] = game.name
            logger.info("Created game page in Notion 游戏总表 for '%s': %s", game.name, new_page_id)

            # Immediately backfill all unmapped daily records for this game
            self.run_task_b_backfill_relations()
            return new_page_id
        except Exception as e:
            logger.error("Failed to create master game page for '%s': %s", game.name, e)
            return None

    def clear_game_playtime(self, game_id: int, archive_notion: bool = True) -> int:
        """Clears local playtime records (sessions & daily_summary) for a game,
        and optionally archives the corresponding daily records in Notion.
        """
        daily_pids = self.db.delete_game_playtime(game_id)
        archived_count = 0
        if archive_notion and self.client and self.config.is_notion_configured():
            for pid in daily_pids:
                if self.client.archive_page(pid):
                    archived_count += 1
        logger.info("Cleared playtime for game %d (archived %d Notion daily pages)", game_id, archived_count)
        return archived_count

    def delete_game_completely(self, game_id: int, archive_notion_daily: bool = True, archive_notion_master: bool = False) -> None:
        """Completely deletes a game and its history locally, and optionally archives pages in Notion."""
        master_pid, daily_pids = self.db.delete_game(game_id)
        if self.client and self.config.is_notion_configured():
            if archive_notion_daily:
                for pid in daily_pids:
                    self.client.archive_page(pid)
            if archive_notion_master and master_pid:
                self.client.archive_page(master_pid)
                if master_pid in self._master_games_cache:
                    del self._master_games_cache[master_pid]

    def clear_all_playtime(self, archive_notion: bool = True) -> int:
        """Clears all playtime data locally, keeping game definitions intact,
        and optionally archives all corresponding daily playtime pages in Notion.
        """
        daily_pids = self.db.clear_all_playtime_data()
        archived_count = 0
        if archive_notion and self.client and self.config.is_notion_configured():
            for pid in daily_pids:
                if self.client.archive_page(pid):
                    archived_count += 1
        logger.info("Cleared all playtime data (archived %d Notion daily pages)", archived_count)
        return archived_count

