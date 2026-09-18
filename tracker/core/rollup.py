"""Midnight splitting and daily rollup calculations."""

from datetime import datetime, timedelta
from typing import Dict, List, Optional, Tuple

from tracker.db.database import Database
from tracker.logger import logger
from tracker.models import DailySlice, DailySummaryRecord, SessionRecord

DATETIME_FORMAT = "%Y-%m-%d %H:%M:%S"
DATE_FORMAT = "%Y-%m-%d"


def parse_datetime(dt_str: str) -> datetime:
    return datetime.strptime(dt_str, DATETIME_FORMAT)


def format_datetime(dt: datetime) -> str:
    return dt.strftime(DATETIME_FORMAT)


def format_date(dt: datetime) -> str:
    return dt.strftime(DATE_FORMAT)


def split_session_by_day(start_dt: datetime, end_dt: datetime) -> List[Tuple[str, int]]:
    """Splits a time interval across midnight boundaries into (date_str, duration_seconds) tuples.
    
    Example:
        2026-09-16 23:40:00 to 2026-09-17 00:35:00
        Yields:
            ('2026-09-16', 1200)  # 20 mins
            ('2026-09-17', 2100)  # 35 mins
    """
    if end_dt <= start_dt:
        return []

    slices: List[Tuple[str, int]] = []
    current_start = start_dt

    while current_start < end_dt:
        current_date = current_start.date()
        # Next midnight is start of the next day: current_date + 1 day at 00:00:00
        next_midnight = datetime.combine(current_date + timedelta(days=1), datetime.min.time())

        chunk_end = min(end_dt, next_midnight)
        duration_seconds = int((chunk_end - current_start).total_seconds())

        if duration_seconds > 0:
            slices.append((current_date.strftime(DATE_FORMAT), duration_seconds))

        current_start = chunk_end

    return slices


class RollupEngine:
    """Computes daily playtime summaries by aggregating session day-slices."""

    def __init__(self, db: Database):
        self.db = db

    def rollup_date(self, target_date_str: str) -> List[DailySummaryRecord]:
        """Calculates and updates the daily summary for a specific calendar date (YYYY-MM-DD)."""
        target_date = datetime.strptime(target_date_str, DATE_FORMAT).date()
        day_start = datetime.combine(target_date, datetime.min.time())
        day_end = datetime.combine(target_date, datetime.max.time().replace(microsecond=0))

        start_iso = format_datetime(day_start)
        end_iso = format_datetime(day_end)

        # Retrieve all sessions overlapping target date
        sessions = self.db.get_sessions_for_date_range(start_iso, end_iso)

        # Aggregate seconds per game_id
        game_durations: Dict[int, int] = {}
        game_sessions: Dict[int, int] = {}

        for s in sessions:
            start_dt = parse_datetime(s.start_time)
            # If still active, use last_heartbeat as conservative end time
            end_time_str = s.end_time or s.last_heartbeat
            end_dt = parse_datetime(end_time_str)

            if end_dt <= start_dt:
                continue

            day_slices = split_session_by_day(start_dt, end_dt)
            for date_str, dur in day_slices:
                if date_str == target_date_str:
                    game_durations[s.game_id] = game_durations.get(s.game_id, 0) + dur
                    game_sessions[s.game_id] = game_sessions.get(s.game_id, 0) + 1

        results: List[DailySummaryRecord] = []
        for game_id, total_seconds in game_durations.items():
            record = self.db.upsert_daily_summary(
                date=target_date_str,
                game_id=game_id,
                duration_seconds=total_seconds,
                session_count=game_sessions[game_id],
            )
            results.append(record)

        return results

    def rollup_recent_days(self, days_back: int = 3) -> List[DailySummaryRecord]:
        """Rolls up summaries for the past N days plus today to ensure no pending data is missed."""
        today = datetime.now().date()
        all_updated: List[DailySummaryRecord] = []
        for i in range(days_back, -1, -1):
            d = today - timedelta(days=i)
            updated = self.rollup_date(d.strftime(DATE_FORMAT))
            all_updated.extend(updated)
        return all_updated
