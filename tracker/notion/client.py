"""Notion API client with exponential backoff and rate-limit handling."""

import time
from typing import Any, Dict, List, Optional

import requests

from tracker.logger import logger

NOTION_API_BASE = "https://api.notion.com/v1"
NOTION_VERSION = "2022-06-28"


class NotionClient:
    """Client for interacting with Notion REST API with rate-limit and error retry handling."""

    def __init__(self, token: str, max_retries: int = 4, backoff_factor: float = 1.5):
        self.token = token
        self.max_retries = max_retries
        self.backoff_factor = backoff_factor
        self.session = requests.Session()
        self.session.headers.update({
            "Authorization": f"Bearer {token}",
            "Notion-Version": NOTION_VERSION,
            "Content-Type": "application/json",
        })

    def _request(self, method: str, endpoint: str, json_data: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        """Performs an HTTP request with exponential backoff for 429 and 5xx errors."""
        url = f"{NOTION_API_BASE}/{endpoint.lstrip('/')}"
        delay = 1.0

        for attempt in range(1, self.max_retries + 1):
            try:
                response = self.session.request(
                    method=method,
                    url=url,
                    json=json_data,
                    timeout=20.0,
                )

                if response.status_code == 429:
                    retry_after = response.headers.get("Retry-After")
                    wait_time = float(retry_after) if retry_after else delay
                    logger.warning("Notion 429 Rate Limit. Backing off for %.2f seconds (attempt %d/%d)", wait_time, attempt, self.max_retries)
                    time.sleep(wait_time)
                    delay *= self.backoff_factor
                    continue

                if 500 <= response.status_code < 600:
                    logger.warning("Notion Server Error %d. Retrying in %.2f seconds (attempt %d/%d)", response.status_code, delay, attempt, self.max_retries)
                    time.sleep(delay)
                    delay *= self.backoff_factor
                    continue

                response.raise_for_status()
                return response.json()

            except requests.exceptions.RequestException as e:
                logger.warning("Network request to Notion %s failed (attempt %d/%d): %s", endpoint, attempt, self.max_retries, e)
                if attempt == self.max_retries:
                    raise
                time.sleep(delay)
                delay *= self.backoff_factor

        raise RuntimeError(f"Failed Notion request {method} {endpoint} after {self.max_retries} attempts")

    def test_connection(self) -> Dict[str, Any]:
        """Validates API token by calling /v1/users/me."""
        return self._request("GET", "/users/me")

    def get_database(self, database_id: str) -> Dict[str, Any]:
        """Retrieves database metadata and property schema."""
        clean_id = database_id.replace("-", "")
        return self._request("GET", f"/databases/{clean_id}")

    def get_page(self, page_id: str) -> Dict[str, Any]:
        """Retrieves a page object from Notion."""
        clean_id = page_id.replace("-", "")
        return self._request("GET", f"/pages/{clean_id}")

    def archive_page(self, page_id: str) -> bool:
        """Archives (deletes) a page in Notion, moving it to Trash."""
        if not page_id:
            return False
        clean_id = page_id.replace("-", "")
        try:
            self._request("PATCH", f"/pages/{clean_id}", json_data={"archived": True})
            logger.info("Archived Notion page %s", page_id)
            return True
        except Exception as e:
            logger.warning("Failed to archive Notion page %s: %s", page_id, e)
            return False

    def is_page_archived_or_deleted(self, page_id: str) -> bool:
        """Checks whether a Notion page has been moved to Trash (archived) or deleted."""
        if not page_id:
            return True
        clean_id = page_id.replace("-", "")
        try:
            res = self._request("GET", f"/pages/{clean_id}")
            return bool(res.get("archived", False))
        except Exception as e:
            err_str = str(e).lower()
            if "404" in err_str or "could not find page" in err_str or "object_not_found" in err_str:
                return True
            return False

    def fetch_all_games(self, database_id: str, title_property: str = "Name") -> Dict[str, Any]:
        """Fetches all games from 游戏总表 with Title, 全名/英文名/Aliases, and 游戏标识/AppID.
        
        Returns:
            Dict[page_id, NotionMasterGame]
        """
        from tracker.models import NotionMasterGame

        clean_id = database_id.replace("-", "")
        games: Dict[str, Any] = {}
        has_more = True
        next_cursor = None

        while has_more:
            body: Dict[str, Any] = {"page_size": 100}
            if next_cursor:
                body["start_cursor"] = next_cursor

            res = self._request("POST", f"/databases/{clean_id}/query", json_data=body)
            results = res.get("results", [])

            for page in results:
                page_id = page["id"]
                props = page.get("properties", {})

                # 1. Resolve Title
                title_str = ""
                title_obj = props.get(title_property, {})
                title_list = title_obj.get("title", [])
                if title_list:
                    title_str = "".join([t.get("plain_text", "") for t in title_list]).strip()
                if not title_str:
                    for k, v in props.items():
                        if isinstance(v, dict) and v.get("type") == "title":
                            t_list = v.get("title", [])
                            title_str = "".join([t.get("plain_text", "") for t in t_list]).strip()
                            if title_str:
                                break

                if not title_str:
                    continue

                # 2. Extract aliases (全名, 英文名, 别名, etc.) and identifiers (游戏标识, AppID, etc.)
                aliases: List[str] = []
                identifiers: List[str] = []

                for prop_name, prop_val in props.items():
                    if not isinstance(prop_val, dict):
                        continue
                    p_type = prop_val.get("type")
                    p_lower = prop_name.lower()

                    # Check for alias / full name
                    if prop_name in ("全名", "英文名", "别名", "English Name", "Alias", "原名") or "全名" in prop_name or "别名" in prop_name:
                        if p_type == "rich_text":
                            rt_list = prop_val.get("rich_text", [])
                            val = "".join([t.get("plain_text", "") for t in rt_list]).strip()
                            if val and val not in aliases:
                                aliases.append(val)
                        elif p_type == "multi_select":
                            for m in prop_val.get("multi_select", []):
                                val = m.get("name", "").strip()
                                if val and val not in aliases:
                                    aliases.append(val)

                    # Check for identifiers (游戏标识, Steam AppID, AppID, etc.)
                    if prop_name in ("游戏标识", "Steam AppID", "AppID", "appid", "Identifier", "Steam ID") or "appid" in p_lower or "游戏标识" in prop_name:
                        if p_type == "rich_text":
                            rt_list = prop_val.get("rich_text", [])
                            val = "".join([t.get("plain_text", "") for t in rt_list]).strip()
                            if val and val not in identifiers:
                                identifiers.append(val)
                        elif p_type == "number":
                            num_val = prop_val.get("number")
                            if num_val is not None:
                                identifiers.append(str(int(num_val)))

                games[page_id] = NotionMasterGame(
                    page_id=page_id,
                    title=title_str,
                    aliases=aliases,
                    identifiers=identifiers,
                )

            has_more = res.get("has_more", False)
            next_cursor = res.get("next_cursor")

        logger.info("Fetched %d games from Notion master database %s (with aliases & identifiers)", len(games), database_id)
        return games

    def create_game_page(self, database_id: str, title_property: str, game_name: str) -> str:
        """Creates a new page in 游戏总表 with only the title property, leaving all other fields blank."""
        clean_id = database_id.replace("-", "")
        actual_title_prop = title_property
        try:
            body = {
                "parent": {"database_id": clean_id},
                "properties": {
                    actual_title_prop: {
                        "title": [{"type": "text", "text": {"content": game_name}}]
                    }
                }
            }
            res = self._request("POST", "/pages", json_data=body)
            new_page_id = res["id"]
        except Exception as e:
            logger.warning("Failed creating game page with title prop '%s': %s. Retrying with discovered schema title property...", actual_title_prop, e)
            db_meta = self.get_database(clean_id)
            for k, v in db_meta.get("properties", {}).items():
                if isinstance(v, dict) and v.get("type") == "title":
                    actual_title_prop = k
                    break
            body = {
                "parent": {"database_id": clean_id},
                "properties": {
                    actual_title_prop: {
                        "title": [{"type": "text", "text": {"content": game_name}}]
                    }
                }
            }
            res = self._request("POST", "/pages", json_data=body)
            new_page_id = res["id"]

        logger.info("Created new game page '%s' in master database: %s", game_name, new_page_id)
        return new_page_id

    def query_daily_playtime_page(
        self,
        database_id: str,
        date_property: str,
        date_str: str,
        relation_property: Optional[str] = None,
        game_page_id: Optional[str] = None,
        title_property: Optional[str] = None,
        game_name: Optional[str] = None,
        identifier_property: Optional[str] = None,
        game_identifier: Optional[str] = None,
    ) -> Optional[str]:
        """Queries 每日游戏时长 Database for an existing page matching Date AND (Relation OR Identifier OR Title)."""
        clean_id = database_id.replace("-", "")
        filters: List[Dict[str, Any]] = [
            {
                "property": date_property,
                "date": {"equals": date_str},
            }
        ]

        if relation_property and game_page_id:
            filters.append({
                "property": relation_property,
                "relation": {"contains": game_page_id},
            })
        elif identifier_property and game_identifier:
            filters.append({
                "property": identifier_property,
                "rich_text": {"contains": game_identifier},
            })
        elif title_property and game_name:
            filters.append({
                "property": title_property,
                "title": {"equals": game_name},
            })

        body = {
            "filter": {"and": filters} if len(filters) > 1 else filters[0],
            "page_size": 1,
        }
        res = self._request("POST", f"/databases/{clean_id}/query", json_data=body)
        results = res.get("results", [])
        if results:
            return results[0]["id"]
        return None

    def create_daily_playtime_page(
        self,
        database_id: str,
        date_property: str,
        date_str: str,
        playtime_property: str,
        playtime_minutes: int,
        title_property: Optional[str] = None,
        game_name: Optional[str] = None,
        relation_property: Optional[str] = None,
        game_page_id: Optional[str] = None,
        status_property: Optional[str] = None,
        binding_status: Optional[str] = None,
        identifier_property: Optional[str] = None,
        game_identifier: Optional[str] = None,
    ) -> str:
        """Creates a new page in 每日游戏时长 Database with Date, Playtime, Title, Status, and optional Relation."""
        clean_id = database_id.replace("-", "")
        props: Dict[str, Any] = {
            date_property: {
                "date": {"start": date_str}
            },
            playtime_property: {
                "number": playtime_minutes
            },
        }

        if title_property and game_name:
            props[title_property] = {
                "title": [{"type": "text", "text": {"content": game_name}}]
            }
        if relation_property and game_page_id:
            props[relation_property] = {
                "relation": [{"id": game_page_id}]
            }
        if status_property and binding_status:
            props[status_property] = {
                "select": {"name": binding_status}
            }
        if identifier_property and game_identifier:
            props[identifier_property] = {
                "rich_text": [{"type": "text", "text": {"content": game_identifier}}]
            }

        body = {
            "parent": {"database_id": clean_id},
            "properties": props,
        }
        res = self._request("POST", "/pages", json_data=body)
        new_page_id = res["id"]
        logger.info(
            "Created daily playtime entry for %s (%s, page_id: %s, %d mins, status: %s)",
            date_str,
            game_name or game_page_id,
            new_page_id,
            playtime_minutes,
            binding_status or "N/A",
        )
        return new_page_id

    def update_daily_playtime_page(
        self,
        page_id: str,
        playtime_property: Optional[str] = None,
        playtime_minutes: Optional[int] = None,
        relation_property: Optional[str] = None,
        game_page_id: Optional[str] = None,
        status_property: Optional[str] = None,
        binding_status: Optional[str] = None,
        title_property: Optional[str] = None,
        game_name: Optional[str] = None,
    ) -> None:
        """Updates specific properties of an existing daily playtime page, leaving other fields untouched."""
        clean_id = page_id.replace("-", "")
        props: Dict[str, Any] = {}
        if playtime_property is not None and playtime_minutes is not None:
            props[playtime_property] = {"number": playtime_minutes}
        if relation_property and game_page_id:
            props[relation_property] = {"relation": [{"id": game_page_id}]}
        if status_property and binding_status:
            props[status_property] = {"select": {"name": binding_status}}
        if title_property and game_name:
            props[title_property] = {"title": [{"type": "text", "text": {"content": game_name}}]}

        if not props:
            return

        body = {"properties": props}
        self._request("PATCH", f"/pages/{clean_id}", json_data=body)
        logger.info("Updated daily playtime page %s (properties: %s)", page_id, list(props.keys()))
