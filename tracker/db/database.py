"""SQLite database layer for GameTimeTracker with WAL mode and transaction safety."""

import sqlite3
import threading
from contextlib import contextmanager
from datetime import datetime
from pathlib import Path
from typing import Generator, List, Optional, Tuple

from tracker.logger import logger
from tracker.models import DailySummaryRecord, GameIdentity, GameRecord, SessionRecord


class Database:
    """Thread-safe SQLite database manager."""

    def __init__(self, db_path: str = "gametime.db"):
        self.db_path = str(Path(db_path).resolve())
        self._local = threading.local()
        self._lock = threading.Lock()
        self._init_db()
        self.cleanup_stale_records()

    def _get_connection(self) -> sqlite3.Connection:
        """Returns a thread-local SQLite connection configured with WAL mode."""
        if not hasattr(self._local, "conn") or self._local.conn is None:
            Path(self.db_path).parent.mkdir(parents=True, exist_ok=True)
            conn = sqlite3.connect(
                self.db_path,
                timeout=30.0,
                check_same_thread=False,
                isolation_level=None,  # Autocommit mode; transactions handled explicitly
            )
            conn.row_factory = sqlite3.Row
            # Enable WAL mode and foreign keys
            conn.execute("PRAGMA journal_mode=WAL;")
            conn.execute("PRAGMA synchronous=NORMAL;")
            conn.execute("PRAGMA foreign_keys=ON;")
            self._local.conn = conn
        return self._local.conn

    @contextmanager
    def transaction(self) -> Generator[sqlite3.Cursor, None, None]:
        """Provides a transactional cursor block with automatic commit/rollback."""
        conn = self._get_connection()
        with self._lock:
            conn.execute("BEGIN IMMEDIATE;")
            cur = conn.cursor()
            try:
                yield cur
                conn.execute("COMMIT;")
            except Exception as e:
                conn.execute("ROLLBACK;")
                logger.error("Transaction rolled back due to error: %s", e)
                raise

    def _init_db(self) -> None:
        """Creates tables and indexes if they do not exist."""
        with self.transaction() as cur:
            # 1. Games table
            cur.execute(
                """
                CREATE TABLE IF NOT EXISTS games (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    platform TEXT NOT NULL,
                    platform_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    executable TEXT NOT NULL,
                    executable_path TEXT NOT NULL,
                    notion_page_id TEXT,
                    status TEXT DEFAULT 'active',
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(platform, platform_id)
                );
                """
            )
            cur.execute(
                "CREATE INDEX IF NOT EXISTS idx_games_exe ON games(executable, status);"
            )
            cur.execute(
                "CREATE INDEX IF NOT EXISTS idx_games_notion ON games(notion_page_id);"
            )

            # 2. Sessions table
            cur.execute(
                """
                CREATE TABLE IF NOT EXISTS sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                    pid INTEGER NOT NULL,
                    process_name TEXT NOT NULL,
                    start_time DATETIME NOT NULL,
                    end_time DATETIME,
                    last_heartbeat DATETIME NOT NULL,
                    duration_seconds INTEGER DEFAULT 0,
                    is_active INTEGER DEFAULT 1,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                """
            )
            cur.execute(
                "CREATE INDEX IF NOT EXISTS idx_sessions_active ON sessions(is_active);"
            )
            cur.execute(
                "CREATE INDEX IF NOT EXISTS idx_sessions_game_time ON sessions(game_id, start_time);"
            )

            # 3. Daily summary table
            cur.execute(
                """
                CREATE TABLE IF NOT EXISTS daily_summary (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    date TEXT NOT NULL,
                    game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                    duration_seconds INTEGER DEFAULT 0,
                    duration_minutes INTEGER DEFAULT 0,
                    session_count INTEGER DEFAULT 1,
                    sync_status TEXT DEFAULT 'pending',
                    notion_page_id TEXT,
                    last_sync_at DATETIME,
                    error_message TEXT,
                    UNIQUE(date, game_id)
                );
                """
            )
            cur.execute(
                "CREATE INDEX IF NOT EXISTS idx_daily_sync ON daily_summary(sync_status);"
            )

            # 4. Settings table
            cur.execute(
                """
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                """
            )
        logger.info("Initialized database at %s with WAL mode", self.db_path)

    # ------------------ Game Operations ------------------

    def get_or_create_game(self, identity: GameIdentity) -> GameRecord:
        """Retrieves an existing game or registers a new one based on platform & platform_id."""
        with self.transaction() as cur:
            cur.execute(
                "SELECT * FROM games WHERE platform = ? AND platform_id = ?;",
                (identity.platform, identity.platform_id),
            )
            row = cur.fetchone()
            if row:
                # Update executable & path & name if changed
                cur.execute(
                    """
                    UPDATE games
                    SET name = ?, executable = ?, executable_path = ?, updated_at = CURRENT_TIMESTAMP
                    WHERE id = ?;
                    """,
                    (identity.name, identity.executable, identity.executable_path, row["id"]),
                )
                return GameRecord(**dict(row))

            cur.execute(
                """
                INSERT INTO games (platform, platform_id, name, executable, executable_path, status)
                VALUES (?, ?, ?, ?, ?, 'active');
                """,
                (
                    identity.platform,
                    identity.platform_id,
                    identity.name,
                    identity.executable,
                    identity.executable_path,
                ),
            )
            game_id = cur.lastrowid
            cur.execute("SELECT * FROM games WHERE id = ?;", (game_id,))
            new_row = cur.fetchone()
            return GameRecord(**dict(new_row))

    def get_game_by_id(self, game_id: int) -> Optional[GameRecord]:
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute("SELECT * FROM games WHERE id = ?;", (game_id,))
        row = cur.fetchone()
        return GameRecord(**dict(row)) if row else None

    def get_game_by_platform_id(self, platform: str, platform_id: str) -> Optional[GameRecord]:
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute(
            "SELECT * FROM games WHERE platform = ? AND platform_id = ?;",
            (platform, platform_id),
        )
        row = cur.fetchone()
        return GameRecord(**dict(row)) if row else None

    def get_game_by_path_or_exe(self, executable_path: str, executable: str) -> Optional[GameRecord]:
        conn = self._get_connection()
        cur = conn.cursor()
        # Prefer exact executable_path match
        cur.execute("SELECT * FROM games WHERE executable_path = ?;", (executable_path,))
        row = cur.fetchone()
        if not row:
            # Fallback to executable name
            cur.execute("SELECT * FROM games WHERE executable = ?;", (executable,))
            row = cur.fetchone()
        return GameRecord(**dict(row)) if row else None

    def list_all_games(self, status: Optional[str] = None) -> List[GameRecord]:
        conn = self._get_connection()
        cur = conn.cursor()
        if status:
            cur.execute("SELECT * FROM games WHERE status = ? ORDER BY name ASC;", (status,))
        else:
            cur.execute("SELECT * FROM games ORDER BY name ASC;")
        return [GameRecord(**dict(row)) for row in cur.fetchall()]

    def update_game_notion_id(self, game_id: int, notion_page_id: Optional[str]) -> None:
        with self.transaction() as cur:
            cur.execute(
                """
                UPDATE games
                SET notion_page_id = ?, updated_at = CURRENT_TIMESTAMP
                WHERE id = ?;
                """,
                (notion_page_id, game_id),
            )
            logger.info("Updated game %d notion_page_id to %s", game_id, notion_page_id)

    def update_game_status(self, game_id: int, status: str) -> None:
        with self.transaction() as cur:
            cur.execute(
                """
                UPDATE games
                SET status = ?, updated_at = CURRENT_TIMESTAMP
                WHERE id = ?;
                """,
                (status, game_id),
            )
            if status == "ignored":
                cur.execute("DELETE FROM daily_summary WHERE game_id = ?;", (game_id,))
                cur.execute("DELETE FROM sessions WHERE game_id = ? AND is_active = 0;", (game_id,))

    # ------------------ Session Operations ------------------

    def create_session(self, game_id: int, pid: int, process_name: str, start_time: str) -> SessionRecord:
        """Creates a new active session."""
        with self.transaction() as cur:
            cur.execute(
                """
                INSERT INTO sessions (game_id, pid, process_name, start_time, last_heartbeat, duration_seconds, is_active)
                VALUES (?, ?, ?, ?, ?, 0, 1);
                """,
                (game_id, pid, process_name, start_time, start_time),
            )
            session_id = cur.lastrowid
            cur.execute("SELECT * FROM sessions WHERE id = ?;", (session_id,))
            row = cur.fetchone()
            return SessionRecord(**dict(row))

    def heartbeat_session(self, session_id: int, heartbeat_time: str) -> None:
        """Updates the session's last heartbeat and in-progress duration."""
        with self.transaction() as cur:
            cur.execute(
                """
                UPDATE sessions
                SET last_heartbeat = ?,
                    duration_seconds = max(0, CAST((strftime('%s', ?) - strftime('%s', start_time)) AS INTEGER))
                WHERE id = ? AND is_active = 1;
                """,
                (heartbeat_time, heartbeat_time, session_id),
            )

    def close_session(self, session_id: int, end_time: str, duration_seconds: int) -> None:
        """Closes an active session with end_time and final duration."""
        with self.transaction() as cur:
            cur.execute(
                """
                UPDATE sessions
                SET end_time = ?,
                    last_heartbeat = ?,
                    duration_seconds = ?,
                    is_active = 0
                WHERE id = ?;
                """,
                (end_time, end_time, duration_seconds, session_id),
            )
            logger.info("Closed session %d (duration: %d seconds)", session_id, duration_seconds)

    def get_active_sessions(self) -> List[SessionRecord]:
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute("SELECT * FROM sessions WHERE is_active = 1;")
        return [SessionRecord(**dict(row)) for row in cur.fetchall()]

    def get_unclosed_sessions(self) -> List[SessionRecord]:
        """Returns sessions marked is_active=1 for crash recovery."""
        return self.get_active_sessions()

    def get_sessions_for_date_range(self, start_iso: str, end_iso: str) -> List[SessionRecord]:
        """Returns all completed or active sessions overlapping the given ISO range."""
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute(
            """
            SELECT * FROM sessions
            WHERE start_time <= ? AND (end_time >= ? OR (is_active = 1 AND last_heartbeat >= ?))
            ORDER BY start_time ASC;
            """,
            (end_iso, start_iso, start_iso),
        )
        return [SessionRecord(**dict(row)) for row in cur.fetchall()]

    def get_all_sessions(self) -> List[SessionRecord]:
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute("SELECT * FROM sessions ORDER BY start_time DESC;")
        return [SessionRecord(**dict(row)) for row in cur.fetchall()]

    # ------------------ Daily Summary Operations ------------------

    def upsert_daily_summary(
        self,
        date: str,
        game_id: int,
        duration_seconds: int,
        session_count: int,
        sync_status: Optional[str] = None,
        notion_page_id: Optional[str] = None,
    ) -> DailySummaryRecord:
        """Upserts a daily summary record for a specific date and game."""
        duration_minutes = int(round(duration_seconds / 60.0))
        with self.transaction() as cur:
            cur.execute(
                "SELECT * FROM daily_summary WHERE date = ? AND game_id = ?;",
                (date, game_id),
            )
            row = cur.fetchone()
            if row:
                existing_page_id = notion_page_id or row["notion_page_id"]
                # If duration increased or status needs update
                status = sync_status if sync_status else (
                    "pending" if duration_seconds != row["duration_seconds"] else row["sync_status"]
                )
                cur.execute(
                    """
                    UPDATE daily_summary
                    SET duration_seconds = ?,
                        duration_minutes = ?,
                        session_count = ?,
                        sync_status = ?,
                        notion_page_id = ?
                    WHERE id = ?;
                    """,
                    (
                        duration_seconds,
                        duration_minutes,
                        session_count,
                        status,
                        existing_page_id,
                        row["id"],
                    ),
                )
                record_id = row["id"]
            else:
                status = sync_status or "pending"
                cur.execute(
                    """
                    INSERT INTO daily_summary (
                        date, game_id, duration_seconds, duration_minutes,
                        session_count, sync_status, notion_page_id
                    ) VALUES (?, ?, ?, ?, ?, ?, ?);
                    """,
                    (
                        date,
                        game_id,
                        duration_seconds,
                        duration_minutes,
                        session_count,
                        status,
                        notion_page_id,
                    ),
                )
                record_id = cur.lastrowid

            cur.execute("SELECT * FROM daily_summary WHERE id = ?;", (record_id,))
            new_row = cur.fetchone()
            return DailySummaryRecord(**dict(new_row))

    def get_daily_summary(self, date: str, game_id: int) -> Optional[DailySummaryRecord]:
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute(
            "SELECT * FROM daily_summary WHERE date = ? AND game_id = ?;",
            (date, game_id),
        )
        row = cur.fetchone()
        return DailySummaryRecord(**dict(row)) if row else None

    def get_daily_summaries_by_date(self, date: str) -> List[DailySummaryRecord]:
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute(
            "SELECT * FROM daily_summary WHERE date = ? ORDER BY duration_seconds DESC;",
            (date,),
        )
        return [DailySummaryRecord(**dict(row)) for row in cur.fetchall()]

    def cleanup_stale_records(self) -> int:
        """Cleans up orphan sessions, daily summaries, and summaries for non-active/ignored games."""
        with self.transaction() as cur:
            cur.execute("DELETE FROM daily_summary WHERE game_id NOT IN (SELECT id FROM games WHERE status = 'active');")
            d_count = cur.rowcount
            cur.execute("DELETE FROM sessions WHERE game_id NOT IN (SELECT id FROM games WHERE status = 'active') AND is_active = 0;")
            s_count = cur.rowcount
            if d_count or s_count:
                logger.info("Cleaned up %d stale daily summary and %d stale session records", d_count, s_count)
            return d_count + s_count

    def get_pending_sync_summaries(self) -> List[DailySummaryRecord]:
        """Returns summaries that need syncing (status 'pending' or 'failed') for ACTIVE games only."""
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute(
            """
            SELECT ds.* FROM daily_summary ds
            JOIN games g ON ds.game_id = g.id
            WHERE g.status = 'active'
              AND ds.sync_status IN ('pending', 'failed')
            ORDER BY ds.date ASC;
            """
        )
        return [DailySummaryRecord(**dict(row)) for row in cur.fetchall()]

    def get_unmapped_synced_summaries(self, game_id: Optional[int] = None) -> List[DailySummaryRecord]:
        """Returns daily summaries that were synced without a Notion Relation (sync_status='unmapped')
        belonging to active games."""
        conn = self._get_connection()
        cur = conn.cursor()
        if game_id is not None:
            cur.execute(
                """
                SELECT ds.* FROM daily_summary ds
                JOIN games g ON ds.game_id = g.id
                WHERE g.status = 'active'
                  AND ds.sync_status = 'unmapped'
                  AND ds.game_id = ?
                ORDER BY ds.date ASC;
                """,
                (game_id,),
            )
        else:
            cur.execute(
                """
                SELECT ds.* FROM daily_summary ds
                JOIN games g ON ds.game_id = g.id
                WHERE g.status = 'active'
                  AND ds.sync_status = 'unmapped'
                ORDER BY ds.date ASC;
                """
            )
        return [DailySummaryRecord(**dict(row)) for row in cur.fetchall()]

    def mark_daily_summary_synced(self, date: str, game_id: int, notion_page_id: str) -> None:
        with self.transaction() as cur:
            cur.execute(
                """
                UPDATE daily_summary
                SET sync_status = 'synced',
                    notion_page_id = ?,
                    last_sync_at = CURRENT_TIMESTAMP,
                    error_message = NULL
                WHERE date = ? AND game_id = ?;
                """,
                (notion_page_id, date, game_id),
            )

    def mark_daily_summary_unmapped(self, date: str, game_id: int, notion_daily_page_id: str) -> None:
        """Marks daily summary as synced to Notion daily table, but relation is pending (unmapped)."""
        with self.transaction() as cur:
            cur.execute(
                """
                UPDATE daily_summary
                SET sync_status = 'unmapped',
                    notion_page_id = ?,
                    last_sync_at = CURRENT_TIMESTAMP,
                    error_message = NULL
                WHERE date = ? AND game_id = ?;
                """,
                (notion_daily_page_id, date, game_id),
            )

    def mark_daily_summary_failed(self, date: str, game_id: int, error_msg: str) -> None:
        with self.transaction() as cur:
            cur.execute(
                """
                UPDATE daily_summary
                SET sync_status = 'failed',
                    error_message = ?
                WHERE date = ? AND game_id = ?;
                """,
                (error_msg, date, game_id),
            )

    # ------------------ Settings Operations ------------------

    def get_setting(self, key: str, default: Optional[str] = None) -> Optional[str]:
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute("SELECT value FROM settings WHERE key = ?;", (key,))
        row = cur.fetchone()
        return row["value"] if row else default

    def set_setting(self, key: str, value: str) -> None:
        with self.transaction() as cur:
            cur.execute(
                """
                INSERT INTO settings (key, value, updated_at)
                VALUES (?, ?, CURRENT_TIMESTAMP)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = CURRENT_TIMESTAMP;
                """,
                (key, value),
            )

    # ------------------ Deletion & Cleanup Operations ------------------

    def delete_daily_summary(self, date: str, game_id: int) -> None:
        """Deletes a daily summary and associated sessions on that date for a specific game."""
        with self.transaction() as cur:
            cur.execute(
                "DELETE FROM sessions WHERE game_id = ? AND date(start_time) = ?;",
                (game_id, date),
            )
            cur.execute(
                "DELETE FROM daily_summary WHERE date = ? AND game_id = ?;",
                (date, game_id),
            )
            logger.info("Deleted daily summary and sessions for game %d on %s", game_id, date)

    def get_synced_daily_summaries(self, limit: int = 100) -> List[DailySummaryRecord]:
        """Returns daily summaries that have a non-null notion_page_id for reconciliation."""
        conn = self._get_connection()
        cur = conn.cursor()
        cur.execute(
            """
            SELECT id, date, game_id, duration_seconds, duration_minutes, session_count, sync_status, notion_page_id, last_sync_at, error_message
            FROM daily_summary
            WHERE notion_page_id IS NOT NULL AND sync_status IN ('synced', 'unmapped')
            ORDER BY date DESC
            LIMIT ?;
            """,
            (limit,),
        )
        return [
            DailySummaryRecord(
                id=row["id"],
                date=row["date"],
                game_id=row["game_id"],
                duration_seconds=row["duration_seconds"],
                duration_minutes=row["duration_minutes"],
                session_count=row["session_count"],
                sync_status=row["sync_status"],
                notion_page_id=row["notion_page_id"],
                last_sync_at=row["last_sync_at"],
                error_message=row["error_message"],
            )
            for row in cur.fetchall()
        ]

    def delete_game_playtime(self, game_id: int) -> List[str]:
        """Physically deletes all sessions and daily_summary records for a specific game.
        Returns a list of associated Notion Page IDs that were deleted.
        """
        daily_pids: List[str] = []
        with self.transaction() as cur:
            cur.execute(
                "SELECT notion_page_id FROM daily_summary WHERE game_id = ? AND notion_page_id IS NOT NULL;",
                (game_id,),
            )
            daily_pids = [row["notion_page_id"] for row in cur.fetchall() if row["notion_page_id"]]
            cur.execute("DELETE FROM sessions WHERE game_id = ?;", (game_id,))
            cur.execute("DELETE FROM daily_summary WHERE game_id = ?;", (game_id,))
            logger.info("Deleted all playtime records for game %d (found %d Notion daily pages)", game_id, len(daily_pids))
        return daily_pids

    def delete_game(self, game_id: int) -> Tuple[Optional[str], List[str]]:
        """Physically deletes a game record along with all its sessions and daily summaries.
        Returns (master_notion_page_id, list_of_daily_notion_page_ids).
        """
        master_pid: Optional[str] = None
        daily_pids: List[str] = []
        with self.transaction() as cur:
            cur.execute("SELECT notion_page_id FROM games WHERE id = ?;", (game_id,))
            g_row = cur.fetchone()
            if g_row and g_row["notion_page_id"]:
                master_pid = g_row["notion_page_id"]

            cur.execute(
                "SELECT notion_page_id FROM daily_summary WHERE game_id = ? AND notion_page_id IS NOT NULL;",
                (game_id,),
            )
            daily_pids = [row["notion_page_id"] for row in cur.fetchall() if row["notion_page_id"]]

            cur.execute("DELETE FROM sessions WHERE game_id = ?;", (game_id,))
            cur.execute("DELETE FROM daily_summary WHERE game_id = ?;", (game_id,))
            cur.execute("DELETE FROM games WHERE id = ?;", (game_id,))
            logger.info("Deleted game %d and all its records (master: %s, %d daily pages)", game_id, master_pid, len(daily_pids))
        return master_pid, daily_pids

    def clear_all_playtime_data(self) -> List[str]:
        """Clears all sessions and daily_summary records across all games (for fresh restart).
        Preserves games configuration, paths, and master mappings.
        Returns all Notion daily page IDs that were removed.
        """
        daily_pids: List[str] = []
        with self.transaction() as cur:
            cur.execute("SELECT notion_page_id FROM daily_summary WHERE notion_page_id IS NOT NULL;")
            daily_pids = [row["notion_page_id"] for row in cur.fetchall() if row["notion_page_id"]]
            cur.execute("DELETE FROM sessions;")
            cur.execute("DELETE FROM daily_summary;")
            logger.info("Cleared all playtime data (sessions & daily_summary), returning %d daily Notion pages", len(daily_pids))
        return daily_pids
