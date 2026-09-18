"""Unit tests for the modern 380x720 GameTimeGUI and 4-state tray icon."""

import os
import sys
import tempfile
import time
from datetime import datetime, timedelta

if sys.platform == "win32":
    tcl_path = os.path.join(sys.prefix, "tcl", "tcl8.6")
    tk_path = os.path.join(sys.prefix, "tcl", "tk8.6")
    if os.path.exists(tcl_path):
        os.environ.setdefault("TCL_LIBRARY", tcl_path)
    if os.path.exists(tk_path):
        os.environ.setdefault("TK_LIBRARY", tk_path)

import pytest
import customtkinter as ctk

from tracker.core.rollup import RollupEngine
from tracker.core.session_manager import SessionManager
from tracker.db.database import Database
from tracker.models import GameIdentity
from tracker.ui.gui import GameTimeGUI, Heatmap, MarqueeLabel, StatPill, StatusBadge
from tracker.ui.tray import create_default_icon


@pytest.fixture
def temp_db():
    fd, path = tempfile.mkstemp(suffix=".db")
    os.close(fd)
    db = Database(path)
    yield db
    if os.path.exists(path):
        try:
            os.remove(path)
        except Exception:
            pass


def test_gui_full_features_and_heatmap_interaction(temp_db):
    """Verifies that GameTimeGUI initializes with theme tokens, renders components, handles clicks, tooltips, and settings modal."""
    # Seed games
    g1 = temp_db.get_or_create_game(
        GameIdentity(platform="Steam", platform_id="1001", name="黑神话：悟空", executable="b1.exe", executable_path="C:\\b1.exe")
    )
    g2 = temp_db.get_or_create_game(
        GameIdentity(platform="Xbox", platform_id="2002", name="地平线5", executable="forza.exe", executable_path="C:\\forza.exe")
    )
    g3 = temp_db.get_or_create_game(
        GameIdentity(platform="Steam", platform_id="3003", name="Heroes of Might and Magic: Olden Era (Super Long Game Title)", executable="homm.exe", executable_path="C:\\homm.exe")
    )
    temp_db.update_game_status(g1.id, "active")
    temp_db.update_game_status(g2.id, "active")
    temp_db.update_game_status(g3.id, "active")

    today_str = datetime.now().strftime("%Y-%m-%d")
    yesterday_str = (datetime.now() - timedelta(days=1)).strftime("%Y-%m-%d")

    temp_db.upsert_daily_summary(today_str, g1.id, 135 * 60, session_count=2)
    temp_db.upsert_daily_summary(today_str, g2.id, 60 * 60, session_count=1)
    temp_db.upsert_daily_summary(today_str, g3.id, 15 * 60, session_count=1)
    temp_db.upsert_daily_summary(yesterday_str, g1.id, 90 * 60, session_count=1)

    rollup = RollupEngine(temp_db)
    session_mgr = SessionManager(temp_db, rollup)

    gui = GameTimeGUI(
        db=temp_db,
        session_manager=session_mgr,
        sync_engine=None,
        config=None,
    )
    gui.update()

    try:
        # 1. Verify StatPill statistics
        assert "3h 30m" in gui.today_pill.cget("text")
        assert "5h 0m" in gui.week_pill.cget("text")

        # 2. Verify Heatmap data loaded
        assert len(gui.heatmap.data) >= 50
        assert today_str in gui.heatmap.data
        assert gui.heatmap.data[today_str]["minutes"] == 210

        # 3. Test clicking a day on the heatmap
        mock_day = {
            "display_date": "2026年09月17日",
            "minutes": 210,
            "games": [("黑神话：悟空", 135), ("地平线5", 60), ("Heroes of Might and Magic: Olden Era (Super Long Game Title)", 15)],
        }
        gui._on_heatmap_day_clicked(mock_day)
        assert "2026年09月17日" in gui.day_detail_date_lbl.cget("text")
        assert "黑神话：悟空" in gui.day_detail_games_lbl.cget("text")

        # 4. Verify top active card when idle
        gui._update_top_active_card()
        assert "空闲中" in gui.timer_lbl.cget("text")

        # 5. Verify MarqueeLabel in Today's Games
        marquees = []
        for box in gui.today_games_list.winfo_children():
            for child in box.winfo_children():
                if isinstance(child, ctk.CTkFrame):
                    for sub in child.winfo_children():
                        if isinstance(sub, MarqueeLabel):
                            marquees.append(sub)

        assert len(marquees) >= 2
        long_m = next(m for m in marquees if "Olden Era" in m.text)
        short_m = next(m for m in marquees if "地平线" in m.text or "悟空" in m.text)

        assert long_m.text_width > long_m.fixed_width
        assert long_m._after_id is not None
        assert short_m.text_width <= short_m.fixed_width
        assert short_m._after_id is None

        # 6. Test Today tile tooltip hover
        tiles = gui.heatmap.grid_container.winfo_children()
        today_tile = tiles[-1]
        today_tile._canvas.event_generate("<Enter>")
        gui.update()
        today_tile._canvas.event_generate("<Leave>")
        gui.update()

        # 7. Test Settings modal lifecycle & dark mode reopen
        gui.open_settings_modal()
        gui.update()
        assert gui._settings_window is not None
        assert gui._settings_window.winfo_exists() == 1

        gui._settings_window._on_theme_changed("深色模式")
        for _ in range(5):
            time.sleep(0.02)
            gui.update()

        assert gui._settings_window.winfo_exists() == 1
        assert gui._settings_window.state() == "normal"

        gui._settings_window._on_close()
        gui.update()
        assert gui._settings_window is None

        gui.open_settings_modal()
        gui.update()
        assert gui._settings_window is not None
        assert gui._settings_window.winfo_exists() == 1
        assert gui._settings_window.state() == "normal"

    finally:
        gui.quit_app()


def test_4_state_tray_icons():
    """Verifies that create_default_icon handles all 4 states without errors."""
    for state in ("idle", "gaming", "pending", "error"):
        img = create_default_icon(state=state)
        assert img is not None
        assert img.size == (64, 64)


def test_background_thread_quit_app_clean(temp_db):
    """Verifies that quit_app called from a background thread (e.g. system tray) exits cleanly without TclError."""
    import threading

    rollup = RollupEngine(temp_db)
    session_mgr = SessionManager(temp_db, rollup)
    gui = GameTimeGUI(db=temp_db, session_manager=session_mgr)
    gui.update()

    def bg_trigger():
        time.sleep(0.1)
        gui.quit_app()

    t = threading.Thread(target=bg_trigger)
    t.start()
    gui.mainloop()
    t.join()
