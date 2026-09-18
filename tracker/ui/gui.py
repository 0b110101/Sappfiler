"""Modern 380x720 Windows 11 Mica GUI for GameTime Tracker.

Design & Architectural Features:
- Unified theme token architecture (Colors, Fonts, Spacing) in tracker.ui.theme.
- Native Windows 11 Mica backdrop (DWMSBT_MAINWINDOW = 2) with dark immersive titlebar.
- Semi-translucent rounded Card containers (#1a1d2e, border #2a2d45).
- StatusBadge pill (monitoring / idle / pending / error) with colored dot and capsule shape.
- StatPill capsule indicators (今日 / 本周) with elevated background (#252842).
- 5-Level GitHub-style activity Heatmap (#1a1d2e -> #1e3a5f -> #2d5aa0 -> #4f8fff -> #7c5cfc)
  with cursor-safe tooltip (never flickers) and day breakdown click drawer.
- GameItem list with hover color shift, smooth MarqueeLabel scrolling for long titles,
  and blue-to-purple GradientProgressBar.
- Rock-solid lifecycle (no cross-thread TclError exceptions on exit, immune to titlebar redraw withdraws).
"""

import ctypes
import os
import subprocess
import sys
import threading
import time
from datetime import datetime, timedelta
from typing import Any, Callable, Dict, List, Optional, Tuple

import customtkinter as ctk
import tkinter as tk
from tkinter import messagebox, simpledialog

from tracker.config import AppConfig
from tracker.core.rollup import RollupEngine
from tracker.core.session_manager import SessionManager
from tracker.db.database import Database
from tracker.logger import logger
from tracker.models import GameIdentity, GameRecord
from tracker.notion.sync import NotionSyncEngine
from tracker.scheduler.task_scheduler import (
    install_startup_task,
    query_startup_task,
    uninstall_startup_task,
)
from tracker.ui.theme import Colors, Fonts, Spacing


class HeatmapTooltip:
    """Lightweight frameless tooltip for heatmap tiles with cursor-safe positioning."""

    def __init__(self, widget: tk.Widget, text_func: Callable[[], str]):
        self.widget = widget
        self.text_func = text_func
        self.tip_window: Optional[tk.Toplevel] = None
        self._bind_events(widget)

    def _bind_events(self, w):
        try:
            w.bind("<Enter>", self.show_tip, add="+")
            w.bind("<Leave>", self.hide_tip, add="+")
        except Exception:
            pass
        if hasattr(w, "_canvas") and w._canvas:
            try:
                w._canvas.bind("<Enter>", self.show_tip, add="+")
                w._canvas.bind("<Leave>", self.hide_tip, add="+")
            except Exception:
                pass

    def show_tip(self, event=None):
        if self.tip_window or not self.text_func:
            return
        text = self.text_func()
        if not text:
            return

        self.tip_window = tw = tk.Toplevel(self.widget)
        tw.wm_overrideredirect(True)
        tw.attributes("-topmost", True)

        bg = "#0f172a"
        fg = Colors.TEXT_PRIMARY
        border = Colors.BORDER

        frame = tk.Frame(tw, background=bg, highlightbackground=border, highlightthickness=1)
        frame.pack()

        label = tk.Label(
            frame,
            text=text,
            justify=tk.LEFT,
            background=bg,
            foreground=fg,
            font=(Fonts.FAMILY, 11),
            padx=10,
            pady=5,
        )
        label.pack()

        # Update geometry to get actual rendered size
        tw.update_idletasks()
        tw_w = tw.winfo_width()
        tw_h = tw.winfo_height()

        try:
            tile_x = self.widget.winfo_rootx()
            tile_y = self.widget.winfo_rooty()
            tile_w = self.widget.winfo_width()
            tile_h = self.widget.winfo_height()
            screen_w = self.widget.winfo_screenwidth()

            # Center horizontally above tile
            x = tile_x + (tile_w // 2) - (tw_w // 2)
            # Position strictly 8px ABOVE tile so cursor never overlaps and causes flicker
            y = tile_y - tw_h - 8

            # Screen bounds clamping
            if x < 8:
                x = 8
            elif x + tw_w > screen_w - 8:
                x = screen_w - tw_w - 8

            # If clipped off top of screen, show below tile
            if y < 8:
                y = tile_y + tile_h + 8

            tw.wm_geometry(f"+{x}+{y}")
        except Exception:
            tw.wm_geometry("+100+100")

    def hide_tip(self, event=None):
        if self.tip_window:
            try:
                self.tip_window.destroy()
            except Exception:
                pass
            self.tip_window = None


class MarqueeLabel(tk.Canvas):
    """Canvas-based marquee label with fixed width that scrolls long text smoothly."""

    def __init__(
        self,
        master,
        text: str,
        width: int = 200,
        height: int = 24,
        font=None,
        text_color: str = Colors.TEXT_PRIMARY,
        bg_color: str = Colors.BG_CARD,
        **kwargs,
    ):
        font = font or Fonts.body()
        super().__init__(
            master,
            width=width,
            height=height,
            bg=bg_color,
            highlightthickness=0,
            **kwargs,
        )
        self.text = text
        self.font = font
        self.text_color = text_color
        self.bg_color = bg_color
        self.fixed_width = width
        self.fixed_height = height

        self._text_id = self.create_text(
            0, height // 2, text=text, font=font, fill=text_color, anchor="w"
        )
        bbox = self.bbox(self._text_id)
        self.text_width = bbox[2] - bbox[0] if bbox else 0

        self.offset = 0.0
        self.speed = 1.0
        self.pause_counter = 35  # Pause for ~1.2s at beginning
        self._after_id = None

        if self.text_width > self.fixed_width:
            self._schedule_scroll()

    def update_colors(self, text_color: str, bg_color: str):
        self.text_color = text_color
        self.bg_color = bg_color
        try:
            self.configure(bg=bg_color)
            self.itemconfig(self._text_id, fill=text_color)
        except Exception:
            pass

    def _schedule_scroll(self):
        self._after_id = self.after(35, self._scroll_step)

    def _scroll_step(self):
        if not self.winfo_exists():
            return
        if self.text_width <= self.fixed_width:
            return

        if self.pause_counter > 0:
            self.pause_counter -= 1
            self._schedule_scroll()
            return

        max_scroll = self.text_width - self.fixed_width + 16
        self.offset += self.speed

        if self.offset >= max_scroll:
            self.offset = 0.0
            self.pause_counter = 45  # Pause for ~1.5s at end before reset
            self.coords(self._text_id, 0, self.fixed_height // 2)
        else:
            self.coords(self._text_id, -self.offset, self.fixed_height // 2)

        self._schedule_scroll()

    def destroy(self):
        if self._after_id:
            try:
                self.after_cancel(self._after_id)
            except Exception:
                pass
            self._after_id = None
        super().destroy()


class GradientProgressBar(tk.Canvas):
    """Blue-to-purple horizontal gradient progress bar with rounded ends."""

    def __init__(self, master, progress: float = 0.5, height: int = 5, **kwargs):
        super().__init__(
            master, height=height, bg=Colors.BG_CARD, highlightthickness=0, bd=0, **kwargs
        )
        self.height = height
        self.progress = max(0.0, min(1.0, progress))
        self.bind("<Configure>", self._redraw)

    def set_progress(self, ratio: float):
        self.progress = max(0.0, min(1.0, ratio))
        self._redraw()

    def _redraw(self, event=None):
        self.delete("all")
        w = self.winfo_width()
        h = self.height
        if w <= 1:
            return

        # Background track
        self._rounded_rect(0, 0, w, h, radius=h // 2, fill=Colors.BORDER_SUBTLE)

        fill_w = int(w * self.progress)
        if fill_w <= 1:
            return

        steps = max(fill_w, 1)
        for i in range(steps):
            t = i / max(w, 1)
            color = self._interpolate_color(Colors.ACCENT_BLUE, Colors.ACCENT_PURPLE, t)
            self.create_line(i, 0, i, h, fill=color, width=1)

    @staticmethod
    def _interpolate_color(c1: str, c2: str, t: float) -> str:
        r1, g1, b1 = int(c1[1:3], 16), int(c1[3:5], 16), int(c1[5:7], 16)
        r2, g2, b2 = int(c2[1:3], 16), int(c2[3:5], 16), int(c2[5:7], 16)
        r = int(r1 + (r2 - r1) * t)
        g = int(g1 + (g2 - g1) * t)
        b = int(b1 + (b2 - b1) * t)
        return f"#{r:02x}{g:02x}{b:02x}"

    def _rounded_rect(self, x1, y1, x2, y2, radius=2, **kwargs):
        points = [
            x1 + radius, y1, x2 - radius, y1,
            x2, y1, x2, y1 + radius,
            x2, y2 - radius, x2, y2,
            x2 - radius, y2, x1 + radius, y2,
            x1, y2, x1, y2 - radius,
            x1, y1 + radius, x1, y1,
        ]
        self.create_polygon(points, smooth=True, **kwargs)


class Card(ctk.CTkFrame):
    """Semi-translucent rounded card container establishing visual hierarchy."""

    def __init__(self, master, **kwargs):
        super().__init__(
            master,
            fg_color=Colors.BG_CARD,
            corner_radius=16,
            border_width=1,
            border_color=Colors.BORDER,
            **kwargs,
        )


class StatusBadge(ctk.CTkFrame):
    """Status pill badge: ⚪空闲 / 🟢监控中 / 🟠待绑定 / 🔴异常."""

    STATUS_COLORS = {
        "monitoring": (Colors.STATUS_GREEN, "#0d3326"),
        "idle": (Colors.TEXT_SECONDARY, "#1a1d2e"),
        "pending": (Colors.STATUS_ORANGE, "#3d2a0d"),
        "error": (Colors.STATUS_RED, "#3d1515"),
    }

    def __init__(self, master, text="监控中", status="monitoring", **kwargs):
        fg, bg = self.STATUS_COLORS.get(status, self.STATUS_COLORS["idle"])
        super().__init__(master, fg_color=bg, corner_radius=20, **kwargs)

        self.dot = ctk.CTkLabel(
            self, text="●", text_color=fg, font=ctk.CTkFont(size=10), width=10
        )
        self.dot.pack(side="left", padx=(12, 4), pady=5)

        self.label = ctk.CTkLabel(
            self,
            text=text,
            text_color=fg,
            font=ctk.CTkFont(family=Fonts.FAMILY, size=13, weight="bold"),
        )
        self.label.pack(side="left", padx=(0, 14), pady=5)

    def set_status(self, text: str, status: str):
        fg, bg = self.STATUS_COLORS.get(status, self.STATUS_COLORS["idle"])
        self.configure(fg_color=bg)
        self.dot.configure(text_color=fg)
        self.label.configure(text=text, text_color=fg)


class StatPill(ctk.CTkFrame):
    """Elevated capsule pill for statistics: '今日 0h 5m', '本周 0h 5m'."""

    def __init__(self, master, label: str, value: str, **kwargs):
        super().__init__(
            master,
            fg_color=Colors.BG_ELEVATED,
            corner_radius=25,
            border_width=1,
            border_color=Colors.BORDER,
            **kwargs,
        )
        self.tag = ctk.CTkLabel(
            self,
            text=label,
            fg_color=Colors.ACCENT_BLUE,
            corner_radius=12,
            text_color="#ffffff",
            font=ctk.CTkFont(family=Fonts.FAMILY, size=12, weight="bold"),
            width=44,
            height=28,
        )
        self.tag.pack(side="left", padx=(6, 8), pady=6)

        self.val = ctk.CTkLabel(
            self,
            text=value,
            text_color=Colors.TEXT_PRIMARY,
            font=ctk.CTkFont(family=Fonts.FAMILY, size=14, weight="bold"),
        )
        self.val.pack(side="left", padx=(0, 16), pady=6)

    def set_value(self, value: str):
        self.val.configure(text=value)

    def cget(self, param):
        if param == "text":
            return f"{self.tag.cget('text')} {self.val.cget('text')}"
        return super().cget(param)


class Heatmap(Card):
    """5-level activity heatmap with dynamic thresholds and cursor-safe tooltip."""

    LEVELS = [
        Colors.HEAT_0,  # Level 0: None
        Colors.HEAT_1,  # Level 1: Low
        Colors.HEAT_2,  # Level 2: Medium-low
        Colors.HEAT_3,  # Level 3: Medium-high
        Colors.HEAT_4,  # Level 4: High
    ]

    def __init__(
        self,
        master,
        on_day_clicked: Optional[Callable[[Dict[str, Any]], None]] = None,
        **kwargs,
    ):
        super().__init__(master, **kwargs)
        self.on_day_clicked = on_day_clicked
        self.data: Dict[str, Dict[str, Any]] = {}

        # Header row
        top_row = ctk.CTkFrame(self, fg_color="transparent")
        top_row.pack(fill="x", padx=Spacing.CARD_PAD, pady=(12, 4))

        ctk.CTkLabel(
            top_row,
            text="活动热力图",
            font=Fonts.section(),
            text_color=Colors.TEXT_PRIMARY,
        ).pack(side="left")

        self.subtitle_lbl = ctk.CTkLabel(
            top_row,
            text="最近 8 周",
            font=Fonts.caption(),
            text_color=Colors.TEXT_SECONDARY,
        )
        self.subtitle_lbl.pack(side="right")

        # 7 rows x 8 cols grid container (56 days)
        self.grid_container = ctk.CTkFrame(self, fg_color="transparent")
        self.grid_container.pack(padx=10, pady=(4, 12))

    def set_data(self, history_data: Dict[str, Dict[str, Any]]):
        self.data = history_data
        for child in self.grid_container.winfo_children():
            child.destroy()

        today = datetime.now().date()
        days_list: List[Dict[str, Any]] = []
        all_mins = []

        for i in range(55, -1, -1):
            d = today - timedelta(days=i)
            d_str = d.strftime("%Y-%m-%d")
            entry = history_data.get(d_str, {"minutes": 0, "games": []})
            mins = entry.get("minutes", 0)
            games = entry.get("games", [])
            all_mins.append(mins)

            days_list.append({
                "date": d,
                "date_str": d_str,
                "display_date": d.strftime("%Y年%m月%d日"),
                "short_date": d.strftime("%m.%d"),
                "minutes": mins,
                "games": games,
                "is_today": (i == 0),
            })

        t_max = max(all_mins) if all_mins else 0
        if t_max < 60:
            t_max = 60

        rows = 7

        for idx, item in enumerate(days_list):
            r = idx % rows
            c = idx // rows
            m = item["minutes"]

            if m == 0:
                lvl = 0
            elif m < t_max * 0.25:
                lvl = 1
            elif m < t_max * 0.50:
                lvl = 2
            elif m < t_max * 0.75:
                lvl = 3
            else:
                lvl = 4

            tile_color = self.LEVELS[lvl]
            is_today = item["is_today"]

            tile = ctk.CTkFrame(
                self.grid_container,
                width=24,
                height=18,
                corner_radius=4,
                fg_color=tile_color,
                border_width=1 if (is_today or lvl == 0) else 0,
                border_color=Colors.ACCENT_BLUE if is_today else Colors.BORDER_SUBTLE,
            )
            tile.grid(row=r, column=c, padx=2, pady=2)
            tile.pack_propagate(False)

            def make_tip(it=item):
                mins_val = it["minutes"]
                h_str = f"{mins_val//60}h " if mins_val >= 60 else ""
                m_str = f"{mins_val%60}m"
                dur = f"{h_str}{m_str}" if mins_val > 0 else "未游玩"
                g_count = len(it["games"])
                g_text = f"\n{g_count} 款游戏" if g_count > 0 else ""
                return f"{it['display_date']}\n游玩 {dur}{g_text}"

            HeatmapTooltip(tile, make_tip)

            def on_click(e, it=item):
                if self.on_day_clicked:
                    self.on_day_clicked(it)

            tile.bind("<Button-1>", on_click)
            if hasattr(tile, "_canvas") and tile._canvas:
                try:
                    tile._canvas.bind("<Button-1>", on_click, add="+")
                except Exception:
                    pass


# Backwards compatibility alias
HeatmapWidget = Heatmap


class GameItem(ctk.CTkFrame):
    """Game item card with title marquee, playtime, and gradient progress bar."""

    def __init__(self, master, name: str, minutes: int, max_minutes: int, **kwargs):
        super().__init__(
            master,
            fg_color=Colors.BG_CARD,
            corner_radius=12,
            border_width=1,
            border_color=Colors.BORDER_SUBTLE,
            **kwargs,
        )

        # Top line: Name + Playtime
        top = ctk.CTkFrame(self, fg_color="transparent")
        top.pack(fill="x", padx=14, pady=(10, 4))

        self.name_label = MarqueeLabel(
            top,
            text=name,
            width=200,
            height=22,
            font=Fonts.body(),
            text_color=Colors.TEXT_PRIMARY,
            bg_color=Colors.BG_CARD,
        )
        self.name_label.pack(side="left")

        dur_str = f"{minutes//60}h {minutes%60}m" if minutes >= 60 else f"{minutes}m"
        time_label = ctk.CTkLabel(
            top,
            text=dur_str,
            font=ctk.CTkFont(family=Fonts.FAMILY, size=13, weight="bold"),
            text_color=Colors.ACCENT_BLUE,
        )
        time_label.pack(side="right")

        # Bottom line: Horizontal Gradient Progress Bar
        progress = GradientProgressBar(
            self,
            progress=minutes / max(max_minutes, 1),
            height=4,
        )
        progress.pack(fill="x", padx=14, pady=(0, 10))

        # Hover state
        self._bind_hover()

    def _bind_hover(self):
        def on_enter(e):
            self.configure(fg_color=Colors.BG_CARD_HOVER)
            self.name_label.update_colors(Colors.TEXT_PRIMARY, Colors.BG_CARD_HOVER)

        def on_leave(e):
            self.configure(fg_color=Colors.BG_CARD)
            self.name_label.update_colors(Colors.TEXT_PRIMARY, Colors.BG_CARD)

        self.bind("<Enter>", on_enter)
        self.bind("<Leave>", on_leave)
        if hasattr(self, "_canvas") and self._canvas:
            try:
                self._canvas.bind("<Enter>", on_enter, add="+")
                self._canvas.bind("<Leave>", on_leave, add="+")
            except Exception:
                pass


class GameTimeGUI(ctk.CTk):
    """Modern 380x720 Windows 11 Mica card dashboard for GameTime Tracker."""

    def __init__(
        self,
        db: Database,
        session_manager: SessionManager,
        sync_engine: Optional[NotionSyncEngine] = None,
        config: Optional[AppConfig] = None,
        on_exit_callback: Optional[Callable[[], None]] = None,
    ):
        super().__init__()

        self.db = db
        self.session_manager = session_manager
        self.sync_engine = sync_engine
        self.config = config
        self.on_exit_callback = on_exit_callback
        self.rollup = RollupEngine(db)

        # 380x720 standard window setup
        self.title("GameTime Tracker")
        self.geometry("380x720")
        self.minsize(360, 600)

        ctk.set_appearance_mode("dark")
        self._current_theme_setting = "深色模式"
        self.configure(fg_color=Colors.BG_BASE)
        self.attributes("-alpha", 0.96)

        # Apply native Windows 11 Mica and DWM effects
        self.after(100, self._apply_dwm_effects)

        self.minimize_to_tray_on_close = True
        self.is_syncing = False
        self._refresh_timer_running = True
        self._settings_window: Optional[ctk.CTkToplevel] = None

        self.protocol("WM_DELETE_WINDOW", self.on_window_close)

        # Build UI layout
        self._build_header_bar()
        self._build_content()

        # Initial refresh
        self.refresh_all_data()
        self._schedule_live_refresh()

    def report_callback_exception(self, exc, val, tb):
        """Suppresses harmless teardown TclErrors when widgets are destroyed during shutdown."""
        if issubclass(exc, tk.TclError) and (
            "invalid command name" in str(val) or "application has been destroyed" in str(val)
        ):
            return
        super().report_callback_exception(exc, val, tb)

    def refresh_dashboard_data(self):
        """Backwards-compatible alias for refresh_all_data."""
        self.refresh_all_data()

    def _apply_theme_colors(self):
        self.configure(fg_color=Colors.BG_BASE)

    def _apply_dwm_effects(self):
        """Applies Windows 11 Native Mica (value 2), round corners, and dark immersive titlebar."""
        try:
            hwnd = ctypes.windll.user32.GetParent(self.winfo_id()) or self.winfo_id()
            dwmapi = ctypes.windll.dwmapi

            # 1. Dark Mode (DWMWA_USE_IMMERSIVE_DARK_MODE = 20)
            dark_mode = ctypes.c_int(1)
            dwmapi.DwmSetWindowAttribute(
                hwnd, 20, ctypes.byref(dark_mode), ctypes.sizeof(dark_mode)
            )

            # 2. Mica Backdrop (DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_MAINWINDOW = 2)
            backdrop_type = ctypes.c_int(2)
            dwmapi.DwmSetWindowAttribute(
                hwnd, 38, ctypes.byref(backdrop_type), ctypes.sizeof(backdrop_type)
            )

            # 3. Round Corners (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2)
            corner_pref = ctypes.c_int(2)
            dwmapi.DwmSetWindowAttribute(
                hwnd, 33, ctypes.byref(corner_pref), ctypes.sizeof(corner_pref)
            )

            # 4. Border Color (DWMWA_BORDER_COLOR = 34, 0x00452d2a)
            border_color = ctypes.c_int(0x00452D2A)
            dwmapi.DwmSetWindowAttribute(
                hwnd, 34, ctypes.byref(border_color), ctypes.sizeof(border_color)
            )
        except Exception as e:
            logger.debug("DWM Mica setup note: %s", e)

    # ================= Header Bar =================

    def _build_header_bar(self):
        """Top action bar with brand title, Notion status pill, and settings button."""
        self.header_frame = ctk.CTkFrame(self, fg_color="transparent")
        self.header_frame.pack(
            fill="x",
            padx=Spacing.WINDOW_PAD,
            pady=(Spacing.CARD_GAP + 2, Spacing.CARD_GAP // 2),
        )

        self.brand_title = ctk.CTkLabel(
            self.header_frame,
            text="🎮 GameTime Tracker",
            font=Fonts.title(),
            text_color=Colors.TEXT_PRIMARY,
        )
        self.brand_title.pack(side="left")

        actions_box = ctk.CTkFrame(self.header_frame, fg_color="transparent")
        actions_box.pack(side="right")

        # Notion Status Pill (click to sync)
        self.notion_pill = ctk.CTkFrame(
            actions_box,
            fg_color=Colors.BG_CARD,
            corner_radius=16,
            border_width=1,
            border_color=Colors.BORDER_SUBTLE,
            cursor="hand2",
        )
        self.notion_pill.pack(side="left", padx=(0, 8))
        self.notion_pill.bind("<Button-1>", lambda e: self.trigger_manual_sync())

        self.notion_dot = ctk.CTkLabel(
            self.notion_pill,
            text="●",
            font=ctk.CTkFont(size=10),
            text_color=Colors.STATUS_GREEN,
            width=10,
        )
        self.notion_dot.pack(side="left", padx=(10, 4), pady=4)
        self.notion_dot.bind("<Button-1>", lambda e: self.trigger_manual_sync())

        self.notion_text_lbl = ctk.CTkLabel(
            self.notion_pill,
            text="已同步",
            font=Fonts.small(),
            text_color=Colors.TEXT_SECONDARY,
        )
        self.notion_text_lbl.pack(side="left", padx=(0, 10), pady=4)
        self.notion_text_lbl.bind("<Button-1>", lambda e: self.trigger_manual_sync())

        # Gear (Settings)
        self.gear_btn = ctk.CTkButton(
            actions_box,
            text="⚙",
            width=32,
            height=32,
            corner_radius=10,
            fg_color=Colors.BG_CARD,
            hover_color=Colors.BG_CARD_HOVER,
            border_width=1,
            border_color=Colors.BORDER_SUBTLE,
            text_color=Colors.TEXT_SECONDARY,
            font=ctk.CTkFont(size=15),
            command=self.open_settings_modal,
        )
        self.gear_btn.pack(side="left")

    # ================= Content Area =================

    def _build_content(self):
        self.container = ctk.CTkScrollableFrame(self, fg_color="transparent")
        self.container.pack(
            fill="both",
            expand=True,
            padx=Spacing.WINDOW_PAD - 4,
            pady=(0, Spacing.CARD_GAP),
        )

        # 1. Top Status Card
        self.status_card = Card(self.container)
        self.status_card.pack(fill="x", pady=(0, Spacing.CARD_GAP))

        self.status_header_lbl = ctk.CTkLabel(
            self.status_card,
            text="状态监控",
            font=ctk.CTkFont(family=Fonts.FAMILY, size=13, weight="bold"),
            text_color=Colors.TEXT_SECONDARY,
        )
        self.status_header_lbl.pack(pady=(14, 2))

        self.game_name_lbl = ctk.CTkLabel(
            self.status_card,
            text="",
            font=Fonts.title(),
            text_color=Colors.TEXT_PRIMARY,
        )

        self.timer_lbl = ctk.CTkLabel(
            self.status_card,
            text="● 空闲中",
            font=Fonts.headline(),
            text_color=Colors.TEXT_PRIMARY,
        )
        self.timer_lbl.pack(pady=2)

        self.status_badge = StatusBadge(self.status_card, text="空闲中", status="idle")
        self.status_badge.pack(pady=(4, 6))

        self.recording_hint_lbl = ctk.CTkLabel(
            self.status_card,
            text="后台静默监听游戏进程中",
            font=Fonts.caption(),
            text_color=Colors.TEXT_SECONDARY,
        )
        self.recording_hint_lbl.pack(pady=(0, 14))

        # 2. Stat Pills (Today & Week)
        pills_frame = ctk.CTkFrame(self.container, fg_color="transparent")
        pills_frame.pack(fill="x", pady=(0, Spacing.CARD_GAP))

        self.today_pill = StatPill(pills_frame, "今日", "0h 0m")
        self.today_pill.pack(side="left", expand=True, fill="x", padx=(0, 4))

        self.week_pill = StatPill(pills_frame, "本周", "0h 0m")
        self.week_pill.pack(side="left", expand=True, fill="x", padx=(4, 0))

        # 3. Activity Heatmap Card
        self.heatmap = Heatmap(
            self.container, on_day_clicked=self._on_heatmap_day_clicked
        )
        self.heatmap.pack(fill="x", pady=(0, Spacing.CARD_GAP))

        # Day breakdown panel (shown on day click)
        self.day_breakdown_card = ctk.CTkFrame(
            self.container,
            corner_radius=14,
            fg_color=Colors.BG_ELEVATED,
            border_width=1,
            border_color=Colors.ACCENT_PURPLE,
        )
        self.day_detail_date_lbl = ctk.CTkLabel(
            self.day_breakdown_card,
            text="",
            font=ctk.CTkFont(family=Fonts.FAMILY, size=13, weight="bold"),
            text_color=Colors.TEXT_PRIMARY,
        )
        self.day_detail_date_lbl.pack(anchor="w", padx=14, pady=(10, 2))

        self.day_detail_games_lbl = ctk.CTkLabel(
            self.day_breakdown_card,
            text="",
            font=Fonts.caption(),
            text_color=Colors.TEXT_SECONDARY,
            justify="left",
        )
        self.day_detail_games_lbl.pack(anchor="w", padx=14, pady=(0, 10))

        # 4. Today's Games Card
        self.today_card = Card(self.container)
        self.today_card.pack(fill="x", pady=(0, Spacing.CARD_GAP))

        today_header = ctk.CTkFrame(self.today_card, fg_color="transparent")
        today_header.pack(fill="x", padx=Spacing.CARD_PAD, pady=(14, 6))

        ctk.CTkLabel(
            today_header,
            text="今日游戏",
            font=Fonts.section(),
            text_color=Colors.TEXT_PRIMARY,
        ).pack(side="left")

        self.today_games_list = ctk.CTkFrame(self.today_card, fg_color="transparent")
        self.today_games_list.pack(fill="x", padx=10, pady=(2, 12))

    # ================= Live Refresh =================

    def _schedule_live_refresh(self):
        if not self._refresh_timer_running:
            return
        try:
            self._update_top_active_card()
            self._update_notion_status_indicator()
        except Exception as e:
            logger.debug("Live refresh error: %s", e)

        self.after(2000, self._schedule_live_refresh)

    def _update_top_active_card(self):
        running = self.session_manager.get_running_games()

        if running:
            game, dur_sec = running[0]
            h = dur_sec // 3600
            m = (dur_sec % 3600) // 60
            s = dur_sec % 60
            time_str = f"{h:02d}:{m:02d}:{s:02d}"

            self.status_header_lbl.configure(text="当前正在运行")
            self.game_name_lbl.configure(text=f"🎮 {game.name}")
            self.game_name_lbl.pack(after=self.status_header_lbl, pady=2)

            self.timer_lbl.configure(
                text=time_str,
                font=ctk.CTkFont(family=Fonts.FAMILY, size=32, weight="bold"),
                text_color=Colors.ACCENT_BLUE,
            )
            self.status_badge.set_status("游戏中", "monitoring")
            self.recording_hint_lbl.configure(text="正在记录游戏时长")
        else:
            self.status_header_lbl.configure(text="状态监控")
            self.game_name_lbl.pack_forget()

            self.timer_lbl.configure(
                text="● 空闲中",
                font=Fonts.headline(),
                text_color=Colors.TEXT_PRIMARY,
            )
            self.status_badge.set_status("空闲中", "idle")
            self.recording_hint_lbl.configure(text="后台静默监听游戏进程中")

    def _update_notion_status_indicator(self):
        if not self.config or not self.config.is_notion_configured():
            self.notion_dot.configure(text_color="gray50")
            self.notion_text_lbl.configure(text="未配置")
            return

        if self.is_syncing:
            self.notion_dot.configure(text_color=Colors.ACCENT_BLUE)
            self.notion_text_lbl.configure(text="同步中...")
            return

        pending = len(self.db.get_pending_sync_summaries())
        if pending > 0:
            self.notion_dot.configure(text_color=Colors.STATUS_ORANGE)
            self.notion_text_lbl.configure(text=f"待同步({pending})")
        else:
            self.notion_dot.configure(text_color=Colors.STATUS_GREEN)
            self.notion_text_lbl.configure(text="已同步")

    def refresh_all_data(self):
        try:
            self.rollup.rollup_recent_days(days_back=7)
        except Exception as e:
            logger.debug("Rollup error: %s", e)

        today_date = datetime.now().date()
        today_str = today_date.strftime("%Y-%m-%d")

        # 1. Fetch past 60 days for Heatmap
        history_map: Dict[str, Dict[str, Any]] = {}
        for i in range(59, -1, -1):
            d = today_date - timedelta(days=i)
            d_str = d.strftime("%Y-%m-%d")
            summaries = self.db.get_daily_summaries_by_date(d_str)
            tot_mins = sum(s.duration_minutes for s in summaries)
            g_list = []
            for s in summaries:
                g = self.db.get_game_by_id(s.game_id)
                g_list.append(
                    (g.name if g else f"Game #{s.game_id}", s.duration_minutes)
                )

            if d_str == today_str:
                for g, dur_sec in self.session_manager.get_running_games():
                    add_m = int(dur_sec / 60)
                    tot_mins += add_m
                    found = False
                    for idx, (g_name, g_m) in enumerate(g_list):
                        if g_name == g.name:
                            g_list[idx] = (g_name, g_m + add_m)
                            found = True
                            break
                    if not found and add_m > 0:
                        g_list.append((g.name, add_m))

            history_map[d_str] = {
                "minutes": tot_mins,
                "games": g_list,
            }

        self.heatmap.set_data(history_map)

        # 2. Stat Pills
        t_info = history_map.get(today_str, {"minutes": 0})
        t_mins = t_info["minutes"]
        self.today_pill.set_value(f"{t_mins//60}h {t_mins%60}m")

        w_mins = sum(
            history_map.get(
                (today_date - timedelta(days=i)).strftime("%Y-%m-%d"), {}
            ).get("minutes", 0)
            for i in range(7)
        )
        self.week_pill.set_value(f"{w_mins//60}h {w_mins%60}m")

        # 3. Today's Games Breakdown
        self._render_today_games(today_str)

    def _render_today_games(self, today_str: str):
        for child in self.today_games_list.winfo_children():
            child.destroy()

        summaries = self.db.get_daily_summaries_by_date(today_str)
        games_dict: Dict[str, int] = {}
        for s in summaries:
            g = self.db.get_game_by_id(s.game_id)
            if g:
                games_dict[g.name] = s.duration_minutes

        for g, dur_sec in self.session_manager.get_running_games():
            games_dict[g.name] = games_dict.get(g.name, 0) + int(dur_sec / 60)

        if not games_dict:
            placeholder = ctk.CTkLabel(
                self.today_games_list,
                text="今日暂无游玩记录",
                font=Fonts.caption(),
                text_color=Colors.TEXT_SECONDARY,
                pady=16,
            )
            placeholder.pack()
            return

        sorted_games = sorted(games_dict.items(), key=lambda x: x[1], reverse=True)
        max_duration = max(sorted_games[0][1], 1)

        for name, mins in sorted_games:
            item = GameItem(
                self.today_games_list,
                name=name,
                minutes=mins,
                max_minutes=max_duration,
            )
            item.pack(fill="x", pady=4)

    def _on_heatmap_day_clicked(self, day_item: Dict[str, Any]):
        date_str = day_item["display_date"]
        mins = day_item["minutes"]
        h_str = f"{mins//60}h " if mins >= 60 else ""
        m_str = f"{mins%60}m"
        dur_str = f"{h_str}{m_str}" if mins > 0 else "0m"

        self.day_breakdown_card.pack(fill="x", pady=(0, Spacing.CARD_GAP))
        self.day_detail_date_lbl.configure(text=f"{date_str}  ·  总计 {dur_str}")

        games = day_item.get("games", [])
        if not games:
            self.day_detail_games_lbl.configure(text="当天无游戏记录。")
        else:
            lines = []
            for g_name, gm in games:
                gh = f"{gm//60}h " if gm >= 60 else ""
                lines.append(f"• {g_name}  ({gh}{gm%60}m)")
            self.day_detail_games_lbl.configure(text="\n".join(lines))

    # ================= Settings Modal =================

    def open_settings_modal(self):
        """Safely opens settings dialog without freezing or broken references."""
        if self._settings_window is not None:
            try:
                if self._settings_window.winfo_exists():
                    self._settings_window.deiconify()
                    self._settings_window.lift()
                    self._settings_window.focus_force()
                    self._settings_window.attributes("-topmost", True)
                    self._settings_window.after(
                        150,
                        lambda: self._settings_window.attributes("-topmost", False)
                        if self._settings_window and self._settings_window.winfo_exists()
                        else None,
                    )
                    return
            except Exception:
                pass
            self._settings_window = None

        self._settings_window = SettingsDialog(self)

    # ================= Notion Sync =================

    def trigger_manual_sync(self):
        if self.is_syncing:
            return
        if not self.sync_engine or not self.sync_engine.config.is_notion_configured():
            messagebox.showwarning("提示", "Notion 尚未配置或 Token 无效", parent=self)
            return

        self.is_syncing = True
        self._update_notion_status_indicator()

        def worker():
            try:
                succ, fail = self.sync_engine.sync_pending()
                self.after(0, lambda: self._on_sync_done(succ, fail))
            except Exception as e:
                self.after(0, lambda: self._on_sync_error(str(e)))

        threading.Thread(target=worker, daemon=True).start()

    def _on_sync_done(self, succ: int, fail: int):
        self.is_syncing = False
        self._update_notion_status_indicator()
        self.refresh_all_data()
        messagebox.showinfo(
            "同步完成",
            f"Notion 同步完成！\n成功: {succ} 条\n未绑定/跳过: {fail} 条",
            parent=self,
        )

    def _on_sync_error(self, err: str):
        self.is_syncing = False
        self._update_notion_status_indicator()
        messagebox.showerror("同步失败", f"Notion 同步异常:\n{err}", parent=self)

    # ================= Window Close & Exit =================

    def on_window_close(self):
        if self.minimize_to_tray_on_close:
            self.withdraw()
        else:
            self.quit_app()

    def quit_app(self):
        """Clean application shutdown preventing cross-thread TclError exceptions."""
        if threading.current_thread() is not threading.main_thread():
            self.after(0, self.quit_app)
            return

        self._refresh_timer_running = False

        if self._settings_window is not None:
            try:
                self._settings_window.destroy()
            except Exception:
                pass
            self._settings_window = None

        # Hide window immediately so no further Windows dimension/configure events trigger redraws
        try:
            self.withdraw()
        except Exception:
            pass

        if self.on_exit_callback:
            try:
                self.on_exit_callback()
            except Exception as e:
                logger.debug("on_exit_callback error: %s", e)

        try:
            self.quit()
        except Exception:
            pass
        try:
            self.destroy()
        except Exception:
            pass


class SettingsDialog(ctk.CTkToplevel):
    """Clean settings and game library modal styled with unified Colors tokens."""

    def __init__(self, parent: GameTimeGUI):
        super().__init__(parent)
        self.parent_gui = parent
        self.db = parent.db
        self.sync_engine = parent.sync_engine

        self.title("偏好设置与游戏映射")
        self.geometry("460x570")
        self.minsize(420, 500)
        self.configure(fg_color=Colors.BG_BASE)
        self.transient(parent)

        self.protocol("WM_DELETE_WINDOW", self._on_close)

        # Position dialog neatly next to the tracker window
        try:
            parent.update_idletasks()
            px = parent.winfo_x()
            py = parent.winfo_y()
            sw = self.winfo_screenwidth()
            sh = self.winfo_screenheight()
            tx = px + 390
            if tx + 460 > sw:
                tx = max(10, px - 470)
            ty = max(10, min(py, sh - 600))
            self.geometry(f"460x570+{tx}+{ty}")
        except Exception:
            pass

        tabs = ctk.CTkTabview(
            self,
            corner_radius=14,
            fg_color=Colors.BG_CARD,
            segmented_button_selected_color=Colors.ACCENT_BLUE,
            segmented_button_selected_hover_color=Colors.ACCENT_PURPLE,
        )
        tabs.pack(fill="both", expand=True, padx=Spacing.CARD_PAD, pady=Spacing.CARD_PAD)

        tab_games = tabs.add("🎮 游戏库与总表")
        tab_general = tabs.add("⚙️ 偏好设置")

        self._build_games_tab(tab_games)
        self._build_general_tab(tab_general)

        # Ensure window is visible and on top, countering CustomTkinter withdraw glitches
        self.after(20, self._ensure_visible)
        self.after(100, self._ensure_visible)
        self.after(300, self._ensure_visible)

    def _ensure_visible(self):
        try:
            if self.winfo_exists():
                self.deiconify()
                self.lift()
                self.focus_force()
        except Exception:
            pass

    def _on_close(self):
        self.parent_gui._settings_window = None
        self.destroy()

    def _build_games_tab(self, tab):
        scroll = ctk.CTkScrollableFrame(tab, fg_color="transparent")
        scroll.pack(fill="both", expand=True, padx=4, pady=4)

        all_games = self.db.list_all_games()
        unconfirmed = [g for g in all_games if g.status == "unconfirmed"]

        if unconfirmed:
            cand_box = ctk.CTkFrame(
                scroll, corner_radius=10, fg_color=("#fef3c7", "#78350f")
            )
            cand_box.pack(fill="x", pady=(0, 8))
            ctk.CTkLabel(
                cand_box,
                text=f"⚠️ 发现 {len(unconfirmed)} 个新独立游戏候选:",
                font=ctk.CTkFont(family=Fonts.FAMILY, size=13, weight="bold"),
                text_color=("#92400e", "#fef3c7"),
            ).pack(anchor="w", padx=8, pady=(6, 2))

            for cand in unconfirmed:
                r = ctk.CTkFrame(cand_box, fg_color="transparent")
                r.pack(fill="x", padx=8, pady=2)
                ctk.CTkLabel(
                    r, text=f"• {cand.name}", font=ctk.CTkFont(family=Fonts.FAMILY, size=13)
                ).pack(side="left")
                ctk.CTkButton(
                    r,
                    text="确认",
                    width=50,
                    height=24,
                    command=lambda c=cand: self._confirm_candidate(c),
                ).pack(side="right", padx=(4, 0))
                ctk.CTkButton(
                    r,
                    text="忽略",
                    width=50,
                    height=24,
                    fg_color="gray50",
                    command=lambda c=cand: self._ignore_candidate(c),
                ).pack(side="right")

        active_games = [g for g in all_games if g.status == "active"]
        for g in active_games:
            card = ctk.CTkFrame(
                scroll,
                corner_radius=12,
                fg_color=Colors.BG_CARD,
                border_width=1,
                border_color=Colors.BORDER,
            )
            card.pack(fill="x", pady=4)

            row = ctk.CTkFrame(card, fg_color="transparent")
            row.pack(fill="x", padx=10, pady=8)

            info_box = ctk.CTkFrame(row, fg_color="transparent")
            info_box.pack(side="left", fill="x", expand=True)

            ctk.CTkLabel(
                info_box,
                text=f"{g.name} [{g.platform}]",
                font=ctk.CTkFont(family=Fonts.FAMILY, size=13, weight="bold"),
                text_color=Colors.TEXT_PRIMARY,
            ).pack(anchor="w")

            status_text = "🟢 已绑定总表" if g.notion_page_id else "🟡 未绑定 (独立同步中)"
            ctk.CTkLabel(
                info_box,
                text=status_text,
                font=ctk.CTkFont(family=Fonts.FAMILY, size=11),
                text_color=Colors.STATUS_GREEN if g.notion_page_id else Colors.STATUS_ORANGE,
            ).pack(anchor="w")

            btn_box = ctk.CTkFrame(row, fg_color="transparent")
            btn_box.pack(side="right")

            if not g.notion_page_id:
                ctk.CTkButton(
                    btn_box,
                    text="总表建档",
                    width=68,
                    height=26,
                    font=ctk.CTkFont(family=Fonts.FAMILY, size=11, weight="bold"),
                    fg_color=("#10b981", "#059669"),
                    command=lambda gm=g: self._create_master(gm),
                ).pack(side="left", padx=2)

            ctk.CTkButton(
                btn_box,
                text="绑定",
                width=48,
                height=26,
                font=ctk.CTkFont(family=Fonts.FAMILY, size=11),
                command=lambda gm=g: self._rebind_game(gm),
            ).pack(side="left", padx=2)

            ctk.CTkButton(
                btn_box,
                text="清空",
                width=48,
                height=26,
                font=ctk.CTkFont(family=Fonts.FAMILY, size=11),
                fg_color=("gray70", "gray35"),
                command=lambda gm=g: self._clear_game(gm),
            ).pack(side="left", padx=2)

        ctk.CTkButton(
            tab,
            text="清空全部测试游玩时长 (保留游戏库)",
            fg_color=("#ef4444", "#dc2626"),
            font=ctk.CTkFont(family=Fonts.FAMILY, size=13, weight="bold"),
            command=self._clear_all_test_data,
        ).pack(fill="x", padx=8, pady=8)

    def _build_general_tab(self, tab):
        scroll = ctk.CTkScrollableFrame(tab, fg_color="transparent")
        scroll.pack(fill="both", expand=True, padx=4, pady=4)

        # Theme selector
        theme_row = ctk.CTkFrame(
            scroll,
            corner_radius=12,
            fg_color=Colors.BG_CARD,
            border_width=1,
            border_color=Colors.BORDER,
        )
        theme_row.pack(fill="x", pady=4, padx=4)
        ctk.CTkLabel(
            theme_row,
            text="界面主题模式:",
            font=ctk.CTkFont(family=Fonts.FAMILY, size=13, weight="bold"),
            text_color=Colors.TEXT_PRIMARY,
        ).pack(side="left", padx=12, pady=10)

        theme_seg = ctk.CTkSegmentedButton(
            theme_row,
            values=["跟随系统", "深色模式", "浅色模式"],
            command=self._on_theme_changed,
        )
        current_setting = getattr(self.parent_gui, "_current_theme_setting", None)
        if not current_setting:
            current_mode = ctk.get_appearance_mode()
            current_setting = "深色模式" if current_mode == "Dark" else "浅色模式"
        theme_seg.set(current_setting)
        theme_seg.pack(side="right", padx=12, pady=10)

        # Startup task
        startup_row = ctk.CTkFrame(
            scroll,
            corner_radius=12,
            fg_color=Colors.BG_CARD,
            border_width=1,
            border_color=Colors.BORDER,
        )
        startup_row.pack(fill="x", pady=4, padx=4)
        startup_sw = ctk.CTkSwitch(
            startup_row,
            text="开机自动启动并在系统托盘后台静默运行",
            font=ctk.CTkFont(family=Fonts.FAMILY, size=12),
            command=lambda: self._toggle_startup(startup_sw),
        )
        if "已启用" in query_startup_task():
            startup_sw.select()
        startup_sw.pack(padx=12, pady=10)

        # Diagnostics
        diag_row = ctk.CTkFrame(
            scroll,
            corner_radius=12,
            fg_color=Colors.BG_CARD,
            border_width=1,
            border_color=Colors.BORDER,
        )
        diag_row.pack(fill="x", pady=4, padx=4)
        ctk.CTkButton(
            diag_row,
            text="测试 Notion API 与数据库连接",
            font=ctk.CTkFont(family=Fonts.FAMILY, size=12),
            command=self._test_notion,
        ).pack(fill="x", padx=12, pady=10)

        # Directories
        dir_row = ctk.CTkFrame(
            scroll,
            corner_radius=12,
            fg_color=Colors.BG_CARD,
            border_width=1,
            border_color=Colors.BORDER,
        )
        dir_row.pack(fill="x", pady=4, padx=4)
        ctk.CTkButton(
            dir_row,
            text="打开数据库存储目录",
            font=ctk.CTkFont(family=Fonts.FAMILY, size=12),
            command=self._open_data_dir,
        ).pack(side="left", expand=True, fill="x", padx=(12, 4), pady=10)
        ctk.CTkButton(
            dir_row,
            text="查看运行日志目录",
            font=ctk.CTkFont(family=Fonts.FAMILY, size=12),
            command=self._open_log_dir,
        ).pack(side="right", expand=True, fill="x", padx=(4, 12), pady=10)

    def _confirm_candidate(self, cand):
        name = simpledialog.askstring(
            "确认游戏", "输入游戏显示名称:", initialvalue=cand.name, parent=self
        )
        if name and name.strip():
            self.db.update_game_name(cand.id, name.strip())
            self.db.update_game_status(cand.id, "active")
            self._on_close()
            self.parent_gui.open_settings_modal()
            self.parent_gui.refresh_all_data()

    def _ignore_candidate(self, cand):
        self.db.update_game_status(cand.id, "ignored")
        self._on_close()
        self.parent_gui.open_settings_modal()

    def _create_master(self, g):
        if not self.sync_engine or not self.sync_engine.config.is_notion_configured():
            messagebox.showerror("未配置", "Notion 尚未配置", parent=self)
            return
        if messagebox.askyesno(
            "总表建档", f"在 Notion 总表为 [{g.name}] 创建页面并绑定？", parent=self
        ):
            new_id = self.sync_engine.create_master_game_and_bind(g.id)
            if new_id:
                messagebox.showinfo("成功", f"总表已建档！Page ID: {new_id}", parent=self)
                self._on_close()
                self.parent_gui.open_settings_modal()
                self.parent_gui.refresh_all_data()
            else:
                messagebox.showerror("失败", "总表建档失败，请检查网络", parent=self)

    def _rebind_game(self, g):
        new_pid = simpledialog.askstring(
            "绑定 Notion",
            f"输入 [{g.name}] 的 Notion Page ID:",
            initialvalue=g.notion_page_id or "",
            parent=self,
        )
        if new_pid is not None:
            clean_pid = new_pid.strip().replace("-", "") or None
            self.db.update_game_notion_id(g.id, clean_pid)
            if self.sync_engine and clean_pid:
                threading.Thread(
                    target=self.sync_engine.run_task_b_backfill_relations, daemon=True
                ).start()
            self._on_close()
            self.parent_gui.open_settings_modal()
            self.parent_gui.refresh_all_data()

    def _clear_game(self, g):
        ans = messagebox.askyesnocancel(
            "清空时长",
            f"清空 [{g.name}] 的游玩时长？\n【是】: 同步清理 Notion 每日卡片\n【否】: 仅清空本地",
            parent=self,
        )
        if ans is not None:
            if self.sync_engine:
                self.sync_engine.clear_game_playtime(g.id, archive_notion=(ans is True))
            else:
                self.db.delete_game_playtime(g.id)
            self.parent_gui.refresh_all_data()
            messagebox.showinfo("完成", "已重置该游戏时长", parent=self)

    def _clear_all_test_data(self):
        if messagebox.askyesno(
            "警告", "确定清空所有本地测试时长记录吗？(游戏库将完好保留)", parent=self
        ):
            archive = messagebox.askyesno("同步清理", "是否同步清理 Notion 每日卡片？", parent=self)
            if self.sync_engine:
                cnt = self.sync_engine.clear_all_playtime(archive_notion=archive)
                messagebox.showinfo(
                    "完成", f"已清空游玩记录 (清理 Notion: {cnt} 张)", parent=self
                )
            else:
                self.db.clear_all_playtime_data()
                messagebox.showinfo("完成", "本地时长已清空", parent=self)
            self.parent_gui.refresh_all_data()

    def _on_theme_changed(self, mode):
        self.parent_gui._current_theme_setting = mode
        if mode == "跟随系统":
            ctk.set_appearance_mode("system")
        elif mode == "深色模式":
            ctk.set_appearance_mode("dark")
        elif mode == "浅色模式":
            ctk.set_appearance_mode("light")

        self.parent_gui._apply_theme_colors()
        self.parent_gui.refresh_all_data()

        # Counter CustomTkinter withdrawing toplevel on Windows titlebar color change
        self.after(20, self._ensure_visible)
        self.after(100, self._ensure_visible)
        self.after(300, self._ensure_visible)

    def _toggle_startup(self, sw):
        if sw.get() == 1:
            ok, msg = install_startup_task()
            messagebox.showinfo("自启动", msg, parent=self)
        else:
            ok, msg = uninstall_startup_task()
            messagebox.showinfo("自启动", msg, parent=self)

    def _test_notion(self):
        if not self.sync_engine or not self.sync_engine.client:
            messagebox.showerror("未配置", "Notion 客户端未配置", parent=self)
            return
        try:
            info = self.sync_engine.client.test_connection()
            bot = info.get("name", "Unknown")
            messagebox.showinfo("连接成功", f"✅ Notion 连接成功！\n机器人: {bot}", parent=self)
        except Exception as e:
            messagebox.showerror("连接失败", f"❌ 连接异常: {e}", parent=self)

    def _open_data_dir(self):
        d = os.path.dirname(self.db.db_path)
        if sys.platform == "win32":
            os.startfile(d)
        else:
            subprocess.Popen(["explorer", d])

    def _open_log_dir(self):
        d = os.path.abspath(os.path.join(os.path.dirname(self.db.db_path), "logs"))
        os.makedirs(d, exist_ok=True)
        if sys.platform == "win32":
            os.startfile(d)
        else:
            subprocess.Popen(["explorer", d])
