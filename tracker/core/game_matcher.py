"""Game identity matcher with multi-priority matching and safe fuzzy candidate scoring."""

import re
import unicodedata
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Tuple

from rapidfuzz import fuzz
import requests

from tracker.logger import logger
from tracker.models import GameRecord

# Roman numeral translation mapping
ROMAN_TO_ARABIC = [
    (re.compile(r"\bVIII\b", re.IGNORECASE), "8"),
    (re.compile(r"\bVII\b", re.IGNORECASE), "7"),
    (re.compile(r"\bVI\b", re.IGNORECASE), "6"),
    (re.compile(r"\bIV\b", re.IGNORECASE), "4"),
    (re.compile(r"\bV\b", re.IGNORECASE), "5"),
    (re.compile(r"\bIX\b", re.IGNORECASE), "9"),
    (re.compile(r"\bX\b", re.IGNORECASE), "10"),
    (re.compile(r"\bIII\b", re.IGNORECASE), "3"),
    (re.compile(r"\bII\b", re.IGNORECASE), "2"),
    (re.compile(r"\bI\b", re.IGNORECASE), "1"),
]


def normalize_game_title(title: str) -> str:
    """Normalizes a game title for robust matching:
    - Unicode normalization (NFKC)
    - Lowercase
    - Strip punctuation and symbols (colon, hyphen, apostrophes)
    - Convert Roman numerals to Arabic numbers
    - Collapse whitespaces
    """
    if not title:
        return ""

    text = unicodedata.normalize("NFKC", title).lower()

    # Strip apostrophes directly so "baldur's" -> "baldurs"
    text = re.sub(r"['’`]", "", text)

    # Replace roman numerals with arabic numerals
    for pattern, replacement in ROMAN_TO_ARABIC:
        text = pattern.sub(replacement, text)

    # Remove non-alphanumeric characters (keep unicode letters/digits for Chinese/Japanese)
    text = re.sub(r"[^\w\s]", " ", text)

    # Collapse multiple whitespaces
    text = re.sub(r"\s+", " ", text).strip()
    return text


def _extract_game_meta(val: Any) -> Tuple[str, List[str], List[str]]:
    if isinstance(val, str):
        return val, [], []
    if hasattr(val, "title") and not callable(val.title):
        return str(val.title), getattr(val, "aliases", []), getattr(val, "identifiers", [])
    return str(val), [], []


@dataclass
class NotionGameCandidate:
    page_id: str
    title: str
    match_type: str  # 'exact', 'normalized', 'identifier_match', 'fuzzy_candidate'
    score: float = 100.0


class GameMatcher:
    """Matches a local game name or identity against Notion 游戏总表 entries."""

    def __init__(
        self,
        fuzzy_threshold: float = 80.0,
        score_gap_threshold: float = 15.0,
    ):
        self.fuzzy_threshold = fuzzy_threshold
        self.score_gap_threshold = score_gap_threshold
        self._steam_cn_cache: Dict[str, Optional[str]] = {}

    def resolve_steam_chinese_name(self, appid: str) -> Optional[str]:
        """Queries Steam Store API for the official Simplified Chinese title.
        
        Results are cached in-memory so each AppID is fetched at most once.
        Uses a short timeout and SSL fallback for proxy environments.
        """
        if not appid or not str(appid).strip().isdigit():
            return None

        clean_appid = str(appid).strip()
        if clean_appid in self._steam_cn_cache:
            return self._steam_cn_cache[clean_appid]

        url = f"https://store.steampowered.com/api/appdetails?appids={clean_appid}&l=schinese"
        try:
            try:
                resp = requests.get(url, timeout=3.0)
            except requests.exceptions.SSLError:
                import urllib3
                urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
                resp = requests.get(url, timeout=3.0, verify=False)

            if resp.status_code == 200:
                data = resp.json()
                app_info = data.get(clean_appid, {})
                if app_info.get("success"):
                    app_data = app_info.get("data", {})
                    # If name_localized dict is present
                    if isinstance(app_data.get("name_localized"), dict):
                        cn_name = app_data["name_localized"].get("schinese") or app_data["name_localized"].get("tchinese")
                        if cn_name:
                            self._steam_cn_cache[clean_appid] = str(cn_name).strip()
                            return self._steam_cn_cache[clean_appid]

                    # Standard Steam Store API returns localized title in 'name'
                    name = app_data.get("name")
                    if name:
                        self._steam_cn_cache[clean_appid] = str(name).strip()
                        return self._steam_cn_cache[clean_appid]
        except Exception as e:
            logger.debug("Failed to query Steam Store API for AppID %s: %s", clean_appid, e)

        self._steam_cn_cache[clean_appid] = None
        return None

    def match(
        self,
        game_record: GameRecord,
        notion_games: Dict[str, Any],  # page_id -> NotionMasterGame or title string
    ) -> Optional[NotionGameCandidate]:
        """Runs the multi-priority matching pipeline.
        
        Priority 1: Cached local Notion Page ID
        Priority 2: Identifier / AppID match (e.g. Steam:2871440, AppID in Notion)
        Priority 3: Exact title or alias match (case-insensitive, e.g. against 全名/英文名)
        Priority 4: Normalized title or alias match (roman numerals, punctuation, spacing)
        Priority 5: Steam Store API Simplified Chinese name match
        """
        # Priority 1: Already mapped locally via Notion Page ID
        if game_record.notion_page_id and game_record.notion_page_id in notion_games:
            title, _, _ = _extract_game_meta(notion_games[game_record.notion_page_id])
            return NotionGameCandidate(
                page_id=game_record.notion_page_id,
                title=title,
                match_type="local_cached_id",
                score=100.0,
            )

        # Priority 2: Identifier / AppID match
        target_platform_id = (game_record.platform_id or "").strip().lower()
        target_full_id = f"{game_record.platform}:{game_record.platform_id}".lower()

        if target_platform_id:
            for page_id, item in notion_games.items():
                title, _, identifiers = _extract_game_meta(item)
                for ident in identifiers:
                    clean_ident = ident.strip().lower()
                    if clean_ident in (target_platform_id, target_full_id):
                        return NotionGameCandidate(
                            page_id=page_id,
                            title=title,
                            match_type="identifier_match",
                            score=100.0,
                        )
                    if target_platform_id.isdigit():
                        digits = re.findall(r"\d+", clean_ident)
                        if target_platform_id in digits:
                            return NotionGameCandidate(
                                page_id=page_id,
                                title=title,
                                match_type="identifier_match",
                                score=100.0,
                            )

        target_name = game_record.name.strip()
        target_lower = target_name.lower()
        target_norm = normalize_game_title(target_name)
        target_stripped = target_norm.replace(" ", "")

        # Priority 3: Exact match against Title OR any Alias (e.g. 全名)
        for page_id, item in notion_games.items():
            title, aliases, _ = _extract_game_meta(item)
            names_to_check = [title] + aliases
            for n in names_to_check:
                if n.strip().lower() == target_lower:
                    return NotionGameCandidate(
                        page_id=page_id,
                        title=title,
                        match_type="exact",
                        score=100.0,
                    )

        # Priority 4: Normalized match against Title OR any Alias (e.g. 全名)
        for page_id, item in notion_games.items():
            title, aliases, _ = _extract_game_meta(item)
            names_to_check = [title] + aliases
            for n in names_to_check:
                n_norm = normalize_game_title(n)
                if n_norm == target_norm or (target_stripped and n_norm.replace(" ", "") == target_stripped):
                    return NotionGameCandidate(
                        page_id=page_id,
                        title=title,
                        match_type="normalized",
                        score=98.0,
                    )

        # Priority 5: Steam Store API Simplified Chinese name match
        if game_record.platform == "Steam" and game_record.platform_id:
            steam_cn_name = self.resolve_steam_chinese_name(game_record.platform_id)
            if steam_cn_name:
                cn_lower = steam_cn_name.strip().lower()
                cn_norm = normalize_game_title(steam_cn_name)
                cn_stripped = cn_norm.replace(" ", "")

                for page_id, item in notion_games.items():
                    title, aliases, _ = _extract_game_meta(item)
                    names_to_check = [title] + aliases
                    # Exact check
                    for n in names_to_check:
                        if n.strip().lower() == cn_lower:
                            return NotionGameCandidate(
                                page_id=page_id,
                                title=title,
                                match_type="steam_store_cn_exact",
                                score=100.0,
                            )
                    # Normalized check
                    for n in names_to_check:
                        n_norm = normalize_game_title(n)
                        if n_norm == cn_norm or (cn_stripped and n_norm.replace(" ", "") == cn_stripped):
                            return NotionGameCandidate(
                                page_id=page_id,
                                title=title,
                                match_type="steam_store_cn_normalized",
                                score=98.0,
                            )

        return None

    def find_fuzzy_candidates(
        self,
        game_name: str,
        notion_games: Dict[str, Any],
        platform: Optional[str] = None,
        platform_id: Optional[str] = None,
    ) -> List[NotionGameCandidate]:
        """Finds candidate matches using token sort fuzzy matching against Title and Aliases."""
        target_norm = normalize_game_title(game_name)

        cn_norm: Optional[str] = None
        if platform == "Steam" and platform_id:
            cn_name = self.resolve_steam_chinese_name(platform_id)
            if cn_name:
                cn_norm = normalize_game_title(cn_name)

        scored: List[Tuple[str, str, float]] = []

        for page_id, item in notion_games.items():
            title, aliases, _ = _extract_game_meta(item)
            names_to_check = [title] + aliases

            best_item_score = 0.0
            for n in names_to_check:
                norm_n = normalize_game_title(n)
                score1 = fuzz.token_sort_ratio(target_norm, norm_n)
                score2 = fuzz.WRatio(target_norm, norm_n)
                curr_score = max(score1, score2)
                if cn_norm:
                    s_cn1 = fuzz.token_sort_ratio(cn_norm, norm_n)
                    s_cn2 = fuzz.WRatio(cn_norm, norm_n)
                    curr_score = max(curr_score, s_cn1, s_cn2)

                best_item_score = max(best_item_score, curr_score)

            if best_item_score >= self.fuzzy_threshold:
                scored.append((page_id, title, float(best_item_score)))

        if not scored:
            return []

        # Sort by score descending
        scored.sort(key=lambda x: x[2], reverse=True)

        # Check score gap between top candidate and runner-up
        top_page_id, top_title, top_score = scored[0]
        candidates = [
            NotionGameCandidate(
                page_id=top_page_id,
                title=top_title,
                match_type="fuzzy_candidate",
                score=top_score,
            )
        ]

        if len(scored) > 1:
            second_score = scored[1][2]
            score_gap = top_score - second_score
            # If the score gap is too narrow, include second candidate for user to pick
            if score_gap < self.score_gap_threshold:
                candidates.append(
                    NotionGameCandidate(
                        page_id=scored[1][0],
                        title=scored[1][1],
                        match_type="fuzzy_candidate",
                        score=second_score,
                    )
                )

        return candidates
