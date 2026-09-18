"""Theme and design token system for GameTime Tracker."""

import customtkinter as ctk


class Colors:
    """Design token color system providing depth and visual hierarchy."""

    # Background depth layers (deep to elevated)
    BG_BASE = "#0d0e1a"       # Deepest layer (translucent under Mica)
    BG_CARD = "#1a1d2e"       # Card surface
    BG_CARD_HOVER = "#222540" # Card hover state
    BG_ELEVATED = "#252842"   # Elevated / popover layers

    # Borders
    BORDER = "#2a2d45"        # Default card border
    BORDER_SUBTLE = "#1f2236" # Subtle divider / line border

    # Text hierarchy
    TEXT_PRIMARY = "#f0f0f5"   # High emphasis / headings
    TEXT_SECONDARY = "#8b8fa3" # Medium emphasis / descriptions
    TEXT_TERTIARY = "#555873"  # Low emphasis / captions

    # Accents
    ACCENT_BLUE = "#4f8fff"   # Primary accent
    ACCENT_PURPLE = "#7c5cfc" # Secondary accent / gradient target

    # Status badges
    STATUS_GREEN = "#34d399"  # Active / monitoring
    STATUS_ORANGE = "#fb923c" # Pending binding
    STATUS_RED = "#f87171"    # Error / disconnected

    # Heatmap activity levels (5-level GitHub-style ramp)
    HEAT_0 = "#1a1d2e"        # Level 0: No activity (matches card base)
    HEAT_1 = "#1e3a5f"        # Level 1: Low activity (deep teal-blue)
    HEAT_2 = "#2d5aa0"        # Level 2: Medium-low (royal blue)
    HEAT_3 = "#4f8fff"        # Level 3: Medium-high (vibrant cyan-blue)
    HEAT_4 = "#7c5cfc"        # Level 4: High activity (electric violet)


class Fonts:
    """Unified typography tokens for Microsoft YaHei UI."""

    FAMILY = "Microsoft YaHei UI"

    @staticmethod
    def title():
        """Page title: '🎮 GameTime Tracker' (18pt bold)."""
        return ctk.CTkFont(family=Fonts.FAMILY, size=18, weight="bold")

    @staticmethod
    def headline():
        """Large numbers and ticking stopwatch (26pt bold)."""
        return ctk.CTkFont(family=Fonts.FAMILY, size=26, weight="bold")

    @staticmethod
    def section():
        """Section headers: '活动热力图' / '今日游戏' (15pt bold)."""
        return ctk.CTkFont(family=Fonts.FAMILY, size=15, weight="bold")

    @staticmethod
    def body():
        """Standard text: Game titles (14pt bold)."""
        return ctk.CTkFont(family=Fonts.FAMILY, size=14, weight="bold")

    @staticmethod
    def caption():
        """Supporting descriptions: '后台静默监听游戏进程中' (12pt)."""
        return ctk.CTkFont(family=Fonts.FAMILY, size=12)

    @staticmethod
    def small():
        """Compact badges and status tags: 'Notion 已同步' (11pt)."""
        return ctk.CTkFont(family=Fonts.FAMILY, size=11)


class Spacing:
    """Layout spacing tokens for consistent breathing room."""

    WINDOW_PAD = 16   # Outer window horizontal padding
    CARD_GAP = 10     # Vertical gap between cards
    CARD_PAD = 14     # Inner padding of cards
    SECTION_GAP = 6   # Gap between elements in a card
    ITEM_GAP = 8      # Gap between list items
