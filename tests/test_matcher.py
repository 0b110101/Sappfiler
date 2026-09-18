"""Unit tests for game identity matcher."""

import pytest
from tracker.core.game_matcher import GameMatcher, normalize_game_title
from tracker.models import GameRecord


def test_normalize_game_title():
    assert normalize_game_title("Baldur's Gate: 3") == "baldurs gate 3"
    assert normalize_game_title("Hades II") == "hades 2"
    assert normalize_game_title("Final Fantasy VII Rebirth") == "final fantasy 7 rebirth"
    assert normalize_game_title("S.T.A.L.K.E.R. 2: Heart of Chornobyl") == "s t a l k e r 2 heart of chornobyl"


def test_priority_1_cached_notion_id():
    matcher = GameMatcher()
    notion_games = {
        "page_bg3": "博德之门3",  # Renamed in Notion!
        "page_hades": "Hades II",
    }
    # Local game record already has notion_page_id saved
    game = GameRecord(
        id=1,
        platform="Steam",
        platform_id="1086940",
        name="Baldur's Gate 3",
        notion_page_id="page_bg3",
    )

    result = matcher.match(game, notion_games)
    assert result is not None
    assert result.page_id == "page_bg3"
    assert result.match_type == "local_cached_id"
    assert result.title == "博德之门3"


def test_priority_2_exact_match():
    matcher = GameMatcher()
    notion_games = {
        "page_1": "No Man's Sky",
        "page_2": "Cyberpunk 2077",
    }
    game = GameRecord(
        id=2,
        platform="Steam",
        platform_id="275850",
        name="No Man's Sky",
        notion_page_id=None,
    )

    result = matcher.match(game, notion_games)
    assert result is not None
    assert result.page_id == "page_1"
    assert result.match_type == "exact"


def test_priority_3_normalized_match_roman_numerals():
    matcher = GameMatcher()
    notion_games = {
        "page_hades": "Hades 2",  # Uses Arabic number in Notion
    }
    game = GameRecord(
        id=3,
        platform="Epic",
        platform_id="hades_2_id",
        name="Hades II",          # Uses Roman numeral in Game
        notion_page_id=None,
    )

    result = matcher.match(game, notion_games)
    assert result is not None
    assert result.page_id == "page_hades"
    assert result.match_type == "normalized"


def test_priority_4_fuzzy_candidates_requires_confirmation():
    matcher = GameMatcher(fuzzy_threshold=75.0, score_gap_threshold=15.0)
    notion_games = {
        "page_fm": "Football Manager 26",
        "page_other": "Farming Simulator 22",
    }
    # No exact or normalized match
    game = GameRecord(
        id=4,
        platform="Steam",
        platform_id="12345",
        name="Football Manager 2026",
        notion_page_id=None,
    )

    # Auto-match must be None (prevent unconfirmed auto-bind)
    assert matcher.match(game, notion_games) is None

    # But find_fuzzy_candidates provides candidates for user to confirm
    candidates = matcher.find_fuzzy_candidates(game.name, notion_games)
    assert len(candidates) >= 1
    assert candidates[0].page_id == "page_fm"
    assert candidates[0].title == "Football Manager 26"
    assert candidates[0].score >= 75.0


def test_alias_full_name_match():
    """English game name matches Chinese Notion page via '全名' alias."""
    from tracker.models import NotionMasterGame

    matcher = GameMatcher()
    notion_games = {
        "page_disco": NotionMasterGame(
            page_id="page_disco",
            title="极乐迪斯科最终剪辑版",  # Chinese Title
            aliases=["Disco Elysium"],      # English Full Name (全名)
            identifiers=[],
        ),
    }

    game = GameRecord(
        id=5,
        platform="Steam",
        platform_id="632470",
        name="Disco Elysium",  # Process/Steam name in English
        notion_page_id=None,
    )

    result = matcher.match(game, notion_games)
    assert result is not None
    assert result.page_id == "page_disco"
    assert result.title == "极乐迪斯科最终剪辑版"
    assert result.match_type == "exact"


def test_identifier_appid_match():
    """Steam/Heybox AppID matches Notion page with identifier property regardless of title."""
    from tracker.models import NotionMasterGame

    matcher = GameMatcher()
    notion_games = {
        "page_bg3_zh": NotionMasterGame(
            page_id="page_bg3_zh",
            title="博德之门3",  # Entirely different Chinese name
            aliases=[],
            identifiers=["1086940", "Steam:1086940"],  # Identified by AppID
        ),
    }

    game = GameRecord(
        id=6,
        platform="Steam",
        platform_id="1086940",
        name="Baldur's Gate 3",
        notion_page_id=None,
    )

    result = matcher.match(game, notion_games)
    assert result is not None
    assert result.page_id == "page_bg3_zh"
    assert result.title == "博德之门3"
    assert result.match_type == "identifier_match"


def test_steam_store_cn_exact_match(monkeypatch):
    """When Notion has Chinese title, Steam AppID fetches Chinese name from Store API and matches."""
    from tracker.models import NotionMasterGame

    matcher = GameMatcher()

    # Mock requests.get to simulate Steam Store API response
    class MockResponse:
        status_code = 200

        def json(self):
            return {
                "1086940": {
                    "success": True,
                    "data": {
                        "name": "博德之门3",
                    },
                }
            }

    monkeypatch.setattr("requests.get", lambda url, **kwargs: MockResponse())

    notion_games = {
        "page_bg3": NotionMasterGame(
            page_id="page_bg3",
            title="博德之门3",
            aliases=[],
            identifiers=[],
        ),
    }

    game = GameRecord(
        id=7,
        platform="Steam",
        platform_id="1086940",
        name="Baldurs Gate 3",  # English process name, no aliases, no identifier in Notion
        notion_page_id=None,
    )

    result = matcher.match(game, notion_games)
    assert result is not None
    assert result.page_id == "page_bg3"
    assert result.title == "博德之门3"
    assert result.match_type == "steam_store_cn_exact"


def test_steam_store_cn_normalized_match(monkeypatch):
    """Handles punctuation or spacing discrepancies between Steam Store API and Notion."""
    from tracker.models import NotionMasterGame

    matcher = GameMatcher()

    class MockResponse:
        status_code = 200

        def json(self):
            return {
                "582010": {
                    "success": True,
                    "data": {
                        "name": "怪物猎人：世界",  # Colon in Steam Store API
                    },
                }
            }

    monkeypatch.setattr("requests.get", lambda url, **kwargs: MockResponse())

    notion_games = {
        "page_mhw": NotionMasterGame(
            page_id="page_mhw",
            title="怪物猎人 世界",  # Space instead of colon in Notion
            aliases=[],
            identifiers=[],
        ),
    }

    game = GameRecord(
        id=8,
        platform="Steam",
        platform_id="582010",
        name="Monster Hunter: World",
        notion_page_id=None,
    )

    result = matcher.match(game, notion_games)
    assert result is not None
    assert result.page_id == "page_mhw"
    assert result.title == "怪物猎人 世界"
    assert result.match_type == "steam_store_cn_normalized"


def test_steam_store_api_failure_graceful(monkeypatch):
    """When Steam API is down or network fails, matcher gracefully falls back without error."""
    matcher = GameMatcher()

    def mock_failure(url, **kwargs):
        raise ConnectionError("Network unreachable")

    monkeypatch.setattr("requests.get", mock_failure)

    notion_games = {
        "page_1": "博德之门3",
    }

    game = GameRecord(
        id=9,
        platform="Steam",
        platform_id="1086940",
        name="Baldurs Gate 3",
        notion_page_id=None,
    )

    # Should safely return None without throwing an uncaught exception
    result = matcher.match(game, notion_games)
    assert result is None


