"""System tray icon and menu for GameTimeTracker using pystray."""

import os
import subprocess
import sys
import threading
from datetime import datetime
from typing import Callable, Optional

import pystray
from PIL import Image, ImageDraw

from tracker.core.session_manager import SessionManager
from tracker.db.database import Database
from tracker.logger import logger
from tracker.notion.sync import NotionSyncEngine
from tracker.ui.prompt import UserPromptManager


def create_default_icon(state: str = "idle") -> Image.Image:
    """Draws a clean tray icon programmatically (width 64, height 64).
    
    States:
      - 'idle': White/Light Slate (空闲)
      - 'gaming': Emerald Green (游戏中)
      - 'pending': Amber Orange (等待绑定)
      - 'error': Crimson Red (同步异常)
    """
    image = Image.new("RGBA", (64, 64), color=(0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    if state == "gaming":
        bg_color = (34, 197, 94)    # Emerald green
        accent_color = (255, 255, 255)
    elif state == "pending":
        bg_color = (245, 158, 11)   # Amber orange
        accent_color = (255, 255, 255)
    elif state == "error":
        bg_color = (239, 68, 68)    # Crimson red
        accent_color = (255, 255, 255)
    else:  # 'idle'
        bg_color = (203, 213, 225)  # Crisp white/slate
        accent_color = (30, 41, 59)

    draw.rounded_rectangle([4, 12, 60, 52], radius=16, fill=bg_color)

    # D-pad on left
    draw.rectangle([14, 28, 26, 36], fill=accent_color)
    draw.rectangle([18, 24, 22, 40], fill=accent_color)

    # Action buttons on right
    if state in ("gaming", "idle"):
        draw.ellipse([40, 24, 46, 30], fill=(241, 196, 15))  # Yellow button
        draw.ellipse([48, 32, 54, 38], fill=(231, 76, 60))   # Red button
    else:
        draw.ellipse([40, 24, 46, 30], fill=accent_color)
        draw.ellipse([48, 32, 54, 38], fill=accent_color)

    return image


class SystemTrayApp:
    """Manages the Windows taskbar tray icon and context menu."""

    def __init__(
        self,
        db: Database,
        session_manager: SessionManager,
        sync_engine: Optional[NotionSyncEngine] = None,
        prompt_mgr: Optional[UserPromptManager] = None,
        on_exit_callback: Optional[Callable[[], None]] = None,
        on_show_gui_callback: Optional[Callable[[], None]] = None,
    ):
        self.db = db
        self.session_manager = session_manager
        self.sync_engine = sync_engine
        self.on_exit_callback = on_exit_callback
        self.on_show_gui_callback = on_show_gui_callback
        self.prompt_mgr = prompt_mgr or UserPromptManager(db, sync_engine=self.sync_engine)
        if self.sync_engine and self.prompt_mgr.sync_engine is None:
            self.prompt_mgr.sync_engine = self.sync_engine
        self.icon: Optional[pystray.Icon] = None

    def _format_seconds(self, seconds: int) -> str:
        h = seconds // 3600
        m = (seconds % 3600) // 60
        s = seconds % 60
        return f"{h:02d}:{m:02d}:{s:02d}"

    def _build_menu_items(self):
        """Dynamically generates the tray menu items with live status and today's playtime."""
        running = self.session_manager.get_running_games()

        items = []

        if self.on_show_gui_callback:
            items.append(pystray.MenuItem("📊 打开时长看板...", action=self._on_show_gui, default=True))
            items.append(pystray.Menu.SEPARATOR)

        # 1. Current Running Status
        if running:
            for game, dur in running:
                items.append(pystray.MenuItem(f"● 当前: {game.name}", action=None, enabled=False))
                items.append(pystray.MenuItem(f"  已运行: {self._format_seconds(dur)}", action=None, enabled=False))
        else:
            items.append(pystray.MenuItem("○ 当前无游戏运行", action=None, enabled=False))

        items.append(pystray.Menu.SEPARATOR)

        # 2. Today's Playtime
        today_str = datetime.now().strftime("%Y-%m-%d")
        today_summaries = self.db.get_daily_summaries_by_date(today_str)

        items.append(pystray.MenuItem(f"今日游玩记录 ({today_str}):", action=None, enabled=False))
        if today_summaries:
            for s in today_summaries:
                game = self.db.get_game_by_id(s.game_id)
                g_name = game.name if game else f"Game #{s.game_id}"
                sync_mark = "✓" if s.sync_status == "synced" else "⏳"
                items.append(
                    pystray.MenuItem(
                        f"  {g_name:<18} {s.duration_minutes}分钟 [{sync_mark}]",
                        action=None,
                        enabled=False,
                    )
                )
        else:
            items.append(pystray.MenuItem("  (今日暂无游玩记录)", action=None, enabled=False))

        items.append(pystray.Menu.SEPARATOR)

        # Check for unconfirmed game candidates
        all_games = self.db.list_all_games()
        unconfirmed = [g for g in all_games if g.status == "unconfirmed"]
        if unconfirmed:
            items.append(pystray.MenuItem(f"❓ 待确认应用候选 ({len(unconfirmed)}款)...", action=self._on_manage_mappings))
            items.append(pystray.Menu.SEPARATOR)

        # 3. Actions
        items.append(pystray.MenuItem("立即同步到 Notion", action=self._on_manual_sync))
        items.append(pystray.MenuItem("游戏映射与管理...", action=self._on_manage_mappings))
        items.append(pystray.MenuItem("打开数据与日志目录", action=self._on_open_datadir))
        items.append(pystray.Menu.SEPARATOR)
        items.append(pystray.MenuItem("退出 GameTimeTracker", action=self._on_exit))

        return items

    def _on_show_gui(self, icon=None, item=None) -> None:
        if self.on_show_gui_callback:
            self.on_show_gui_callback()

    def _on_manual_sync(self, icon, item) -> None:
        if not self.sync_engine:
            return
        threading.Thread(target=self._run_sync, daemon=True).start()

    def _run_sync(self) -> None:
        try:
            logger.info("Manual Notion sync triggered from system tray.")
            if self.sync_engine:
                succ, fail = self.sync_engine.sync_pending()
                if self.icon:
                    self.icon.notify(f"Notion 同步完成: 成功 {succ} 项, 失败 {fail} 项", "GameTimeTracker")
        except Exception as e:
            logger.error("Manual sync failed: %s", e)
            if self.icon:
                self.icon.notify(f"Notion 同步失败: {e}", "GameTimeTracker")

    def _on_manage_mappings(self, icon, item) -> None:
        def open_dialog():
            try:
                notion_games = {}
                if self.sync_engine:
                    self.prompt_mgr.sync_engine = self.sync_engine
                    if not self.sync_engine._master_games_cache:
                        notion_games = self.sync_engine.refresh_master_games()
                    else:
                        notion_games = self.sync_engine._master_games_cache
                self.prompt_mgr.open_rebind_dialog(notion_games)
            except Exception as e:
                logger.error("Error opening rebind dialog: %s", e)

        threading.Thread(target=open_dialog, daemon=True).start()

    def _on_open_datadir(self, icon, item) -> None:
        try:
            base_dir = os.path.dirname(self.db.db_path)
            if sys.platform == "win32":
                os.startfile(base_dir)
            else:
                subprocess.Popen(["explorer", base_dir])
        except Exception as e:
            logger.error("Failed to open data directory: %s", e)

    def _on_exit(self, icon, item) -> None:
        logger.info("Exit requested from system tray.")
        if self.icon:
            self.icon.stop()
        if self.on_exit_callback:
            self.on_exit_callback()

    def get_current_state(self) -> str:
        """Determines current tray state: 'gaming', 'error', 'pending', or 'idle'."""
        running = self.session_manager.get_running_games()
        if running:
            return "gaming"
        if self.sync_engine and getattr(self.sync_engine, "_last_sync_error", False):
            return "error"
        all_games = self.db.list_all_games()
        has_unconfirmed = any(g.status == "unconfirmed" for g in all_games)
        has_unmapped = any(g.status == "active" and not g.notion_page_id for g in all_games)
        if has_unconfirmed or has_unmapped:
            return "pending"
        return "idle"

    def run(self) -> None:
        """Starts the system tray loop (blocking call)."""
        image = create_default_icon(state=self.get_current_state())
        self.icon = pystray.Icon(
            name="GameTimeTracker",
            icon=image,
            title="GameTimeTracker - 游戏时长追踪",
            menu=pystray.Menu(self._build_menu_items),
        )
        self.icon.run()

    def run_detached(self) -> None:
        """Starts the system tray icon in a separate thread without blocking."""
        image = create_default_icon(state=self.get_current_state())
        self.icon = pystray.Icon(
            name="GameTimeTracker",
            icon=image,
            title="GameTimeTracker - 游戏时长追踪",
            menu=pystray.Menu(self._build_menu_items),
        )
        self.icon.run_detached()

    def update_menu(self) -> None:
        """Refreshes the tray icon appearance and menu items."""
        if self.icon:
            state = self.get_current_state()
            self.icon.icon = create_default_icon(state=state)
            self.icon.update_menu()
