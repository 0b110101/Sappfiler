"""Session manager with conservative crash recovery and heartbeat separation."""

from datetime import datetime
from typing import Dict, List, Optional, Tuple

import psutil

from tracker.core.rollup import DATETIME_FORMAT, RollupEngine, format_datetime, parse_datetime
from tracker.db.database import Database
from tracker.logger import logger
from tracker.models import GameIdentity, GameRecord, SessionRecord


class ActiveSessionTracker:
    """In-memory state of an active session."""
    def __init__(self, session_id: int, game_id: int, pid: int, process_name: str, start_time: datetime):
        self.session_id = session_id
        self.game_id = game_id
        self.pid = pid
        self.process_name = process_name
        self.start_time = start_time
        self.last_heartbeat = start_time


class SessionManager:
    """Manages the full lifecycle of game sessions."""

    def __init__(self, db: Database, rollup_engine: Optional[RollupEngine] = None):
        self.db = db
        self.rollup_engine = rollup_engine or RollupEngine(db)
        self.active_sessions: Dict[int, ActiveSessionTracker] = {}  # pid -> ActiveSessionTracker
        self.recover_crashed_sessions()

    def recover_crashed_sessions(self) -> int:
        """Recovers any unclosed sessions from previous crashes or sudden shutdowns.
        
        Conservative rule: If process is dead, use last_heartbeat as end_time.
        Never invent playtime.
        """
        unclosed = self.db.get_unclosed_sessions()
        recovered_count = 0

        for s in unclosed:
            is_still_running = False
            try:
                if psutil.pid_exists(s.pid):
                    proc = psutil.Process(s.pid)
                    if proc.name().lower() == s.process_name.lower():
                        is_still_running = True
            except Exception:
                is_still_running = False

            if is_still_running:
                # Process survived (e.g. tracker was restarted while game kept running)
                logger.info("Session %d (PID %d, %s) is still running. Resuming tracking.", s.id, s.pid, s.process_name)
                self.active_sessions[s.pid] = ActiveSessionTracker(
                    session_id=s.id,
                    game_id=s.game_id,
                    pid=s.pid,
                    process_name=s.process_name,
                    start_time=parse_datetime(s.start_time),
                )
            else:
                # Process died during shutdown/crash. Use last_heartbeat as conservative end_time.
                start_dt = parse_datetime(s.start_time)
                hb_dt = parse_datetime(s.last_heartbeat)
                duration = max(0, int((hb_dt - start_dt).total_seconds()))
                self.db.close_session(s.id, s.last_heartbeat, duration)
                logger.warning(
                    "Recovered crashed session %d (%s, PID %d): closed at last heartbeat %s (duration %d s)",
                    s.id,
                    s.process_name,
                    s.pid,
                    s.last_heartbeat,
                    duration,
                )
                recovered_count += 1

        if recovered_count > 0:
            self.rollup_engine.rollup_recent_days(days_back=3)

        return recovered_count

    def handle_game_detected(self, identity: GameIdentity, pid: int, proc_start_time: Optional[datetime] = None) -> SessionRecord:
        """Called when a game process is detected running."""
        # 1. Ensure game exists in database
        game = self.db.get_or_create_game(identity)

        # 2. Check if already tracked in memory
        if pid in self.active_sessions:
            return self.db.get_sessions_for_date_range("1970-01-01 00:00:00", "2099-01-01 00:00:00")[0]

        start_dt = proc_start_time or datetime.now()
        start_iso = format_datetime(start_dt)

        session = self.db.create_session(
            game_id=game.id,
            pid=pid,
            process_name=identity.executable,
            start_time=start_iso,
        )

        self.active_sessions[pid] = ActiveSessionTracker(
            session_id=session.id,
            game_id=game.id,
            pid=pid,
            process_name=identity.executable,
            start_time=start_dt,
        )
        logger.info("Started session %d for game '%s' (PID %d)", session.id, game.name, pid)
        return session

    def handle_heartbeat(self, now: Optional[datetime] = None) -> None:
        """Dispatches heartbeat every 15s to update SQLite last_heartbeat."""
        current_time = now or datetime.now()
        current_iso = format_datetime(current_time)

        for pid, tracker in list(self.active_sessions.items()):
            try:
                self.db.heartbeat_session(tracker.session_id, current_iso)
                tracker.last_heartbeat = current_time
            except Exception as e:
                logger.error("Failed to update heartbeat for session %d: %s", tracker.session_id, e)

    def handle_game_stopped(self, pid: int, stop_time: Optional[datetime] = None) -> Optional[int]:
        """Called when a game process exits."""
        if pid not in self.active_sessions:
            return None

        tracker = self.active_sessions.pop(pid)
        end_dt = stop_time or datetime.now()
        duration_seconds = max(0, int((end_dt - tracker.start_time).total_seconds()))

        self.db.close_session(
            session_id=tracker.session_id,
            end_time=format_datetime(end_dt),
            duration_seconds=duration_seconds,
        )
        logger.info(
            "Game session %d (PID %d, %s) stopped. Duration: %d seconds (%d mins)",
            tracker.session_id,
            pid,
            tracker.process_name,
            duration_seconds,
            int(round(duration_seconds / 60.0)),
        )

        # Update daily summaries
        try:
            self.rollup_engine.rollup_recent_days(days_back=1)
        except Exception as e:
            logger.error("Error updating daily rollup after session stopped: %s", e)

        return tracker.session_id

    def get_running_games(self) -> List[Tuple[GameRecord, int]]:
        """Returns list of (GameRecord, running_duration_seconds) for UI/Tray display."""
        now = datetime.now()
        running: List[Tuple[GameRecord, int]] = []
        for pid, tracker in self.active_sessions.items():
            game = self.db.get_game_by_id(tracker.game_id)
            if game:
                running_sec = max(0, int((now - tracker.start_time).total_seconds()))
                running.append((game, running_sec))
        return running
