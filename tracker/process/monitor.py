"""Process monitor running periodic 5s scans and 15s session heartbeats."""

import time
from datetime import datetime
from typing import Callable, Dict, List, Optional, Set

import psutil

from tracker.core.session_manager import SessionManager
from tracker.detectors.router import DetectorRouter
from tracker.logger import logger
from tracker.models import GameIdentity
from tracker.process.filter import is_blacklisted_process, is_potential_game_candidate


class ProcessMonitor:
    """Monitors running processes on Windows, tracks game lifecycles, and triggers heartbeats."""

    def __init__(
        self,
        router: DetectorRouter,
        session_manager: SessionManager,
        scan_interval_seconds: int = 5,
        heartbeat_interval_seconds: int = 15,
        on_unconfirmed_candidate: Optional[Callable[[str, str], None]] = None,
        custom_game_dirs: Optional[List[str]] = None,
    ):
        self.router = router
        self.session_manager = session_manager
        self.scan_interval = scan_interval_seconds
        self.heartbeat_interval = heartbeat_interval_seconds
        self.on_unconfirmed_candidate = on_unconfirmed_candidate
        self.custom_game_dirs = custom_game_dirs or []

        self.last_heartbeat_time = time.time()
        self.tracked_pids: Set[int] = set()
        self.unconfirmed_seen: Set[str] = set()  # prevent spamming prompts for the same exe path

    def scan_once(self) -> List[GameIdentity]:
        """Performs a single process scan cycle.
        
        5-second scan: in-memory check, zero database write if no state changed.
        15-second heartbeat: updates SQLite last_heartbeat for active sessions.
        """
        current_running_pids: Set[int] = set()
        detected_games: List[GameIdentity] = []

        # 1. Inspect running processes
        for proc in psutil.process_iter(["pid", "name", "exe", "create_time"]):
            try:
                info = proc.info
                pid = info["pid"]
                name = info.get("name") or ""
                exe = info.get("exe") or ""

                if not exe or is_blacklisted_process(name, exe):
                    continue

                # Run through platform detectors
                identity = self.router.detect_game(pid, name, exe)

                if identity:
                    current_running_pids.add(pid)
                    detected_games.append(identity)

                    # If not already tracked as an active session, start tracking
                    if pid not in self.tracked_pids:
                        create_dt = None
                        if info.get("create_time"):
                            try:
                                create_dt = datetime.fromtimestamp(info["create_time"])
                            except Exception:
                                create_dt = datetime.now()

                        self.session_manager.handle_game_detected(identity, pid, create_dt)
                        self.tracked_pids.add(pid)
                else:
                    # Unrecognized process: only consider if it passes potential game heuristics
                    norm_exe = exe.lower()
                    if norm_exe not in self.unconfirmed_seen:
                        self.unconfirmed_seen.add(norm_exe)
                        is_game, details = is_potential_game_candidate(pid, name, exe, self.custom_game_dirs)
                        if is_game:
                            logger.info("Heuristic game candidate detected: %s (%s) - %s", name, exe, details)
                            if self.on_unconfirmed_candidate:
                                self.on_unconfirmed_candidate(name, exe)

            except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
                continue
            except Exception as e:
                logger.debug("Error inspecting process PID %d: %s", getattr(proc, "pid", -1), e)

        # 2. Detect stopped games
        stopped_pids = self.tracked_pids - current_running_pids
        for pid in stopped_pids:
            self.session_manager.handle_game_stopped(pid)
            self.tracked_pids.remove(pid)

        # 3. Check 15s heartbeat interval
        now_time = time.time()
        if now_time - self.last_heartbeat_time >= self.heartbeat_interval:
            self.session_manager.handle_heartbeat()
            self.last_heartbeat_time = now_time

        return detected_games
