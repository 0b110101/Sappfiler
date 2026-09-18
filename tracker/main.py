"""Main entry point for GameTimeTracker."""

import argparse
import os
import signal
import sys
import threading
import time
from datetime import datetime
from pathlib import Path
from typing import Optional

# Ensure project root is in sys.path
BASE_DIR = Path(__file__).resolve().parent.parent
if str(BASE_DIR) not in sys.path:
    sys.path.insert(0, str(BASE_DIR))

# Ensure Windows terminal outputs UTF-8 properly
if sys.platform == "win32":
    try:
        if sys.stdout and hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8")
        if sys.stderr and hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

from tracker.config import AppConfig, load_config
from tracker.core.game_matcher import GameMatcher
from tracker.core.rollup import RollupEngine
from tracker.core.session_manager import SessionManager
from tracker.db.database import Database
from tracker.detectors.router import DetectorRouter
from tracker.detectors.standalone import compute_path_hash
from tracker.logger import logger
from tracker.models import GameIdentity
from tracker.notion.client import NotionClient
from tracker.notion.sync import NotionSyncEngine
from tracker.process.monitor import ProcessMonitor
from tracker.scheduler.task_scheduler import (
    install_startup_task,
    query_startup_task,
    uninstall_startup_task,
)
from tracker.ui.prompt import UserPromptManager
from tracker.ui.tray import SystemTrayApp


class TrackerApplication:
    """Coordinates all subsystems of GameTimeTracker."""

    def __init__(self, config_path: Optional[str] = None):
        self.config: AppConfig = load_config(config_path)
        self.db = Database(self.config.database.db_path)
        self.rollup = RollupEngine(self.db)
        self.session_manager = SessionManager(self.db, self.rollup)
        self.router = DetectorRouter(
            db=self.db,
            custom_steam_libs=self.config.custom_paths.steam_libraries,
            custom_epic_dirs=self.config.custom_paths.epic_manifests,
        )
        # Notion integration
        self.notion_client = (
            NotionClient(self.config.notion.token)
            if self.config.is_notion_configured()
            else None
        )
        self.matcher = GameMatcher(
            fuzzy_threshold=self.config.tracker.fuzzy_candidate_threshold,
            score_gap_threshold=self.config.tracker.fuzzy_score_gap_threshold,
        )
        self.sync_engine = NotionSyncEngine(
            config=self.config,
            db=self.db,
            client=self.notion_client,
            matcher=self.matcher,
        )
        self.prompt_mgr = UserPromptManager(self.db, self.sync_engine)

        self.monitor = ProcessMonitor(
            router=self.router,
            session_manager=self.session_manager,
            scan_interval_seconds=self.config.tracker.process_scan_interval_seconds,
            heartbeat_interval_seconds=self.config.tracker.session_heartbeat_interval_seconds,
            on_unconfirmed_candidate=self._on_unconfirmed_candidate,
            custom_game_dirs=self.config.custom_paths.custom_game_directories,
        )

        self._running = False
        self._sync_thread: Optional[threading.Thread] = None
        self.tray_app: Optional[SystemTrayApp] = None

    def _on_unconfirmed_candidate(self, process_name: str, exe_path: str) -> None:
        """Silently queues a potential new indie game candidate for user review in tray/UI."""
        try:
            p_hash = compute_path_hash(exe_path)
            existing = self.db.get_game_by_platform_id("Standalone", p_hash)
            if not existing:
                default_name = os.path.splitext(process_name)[0]
                identity = GameIdentity(
                    platform="Standalone",
                    platform_id=p_hash,
                    name=default_name,
                    executable=process_name,
                    executable_path=exe_path,
                )
                game = self.db.get_or_create_game(identity)
                self.db.update_game_status(game.id, "unconfirmed")
                logger.info("Queued potential game candidate: '%s' (%s)", default_name, exe_path)

                if self.tray_app and self.tray_app.icon:
                    try:
                        self.tray_app.icon.notify(
                            f"发现新独立游戏候选: {default_name}\n可在托盘菜单【游戏映射管理】中确认",
                            "GameTimeTracker",
                        )
                    except Exception:
                        pass
                    self.tray_app.update_menu()
        except Exception as e:
            logger.debug("Error recording unconfirmed candidate: %s", e)

    def start_background_workers(self) -> None:
        """Starts background worker threads for process monitoring and periodic Notion syncing."""
        self._running = True

        # Process monitor loop
        def monitor_loop():
            logger.info("Starting process monitor loop (interval: %ds)", self.config.tracker.process_scan_interval_seconds)
            while self._running:
                try:
                    self.monitor.scan_once()
                    if self.tray_app:
                        self.tray_app.update_menu()
                except Exception as e:
                    logger.error("Error in process monitor cycle: %s", e)
                time.sleep(self.config.tracker.process_scan_interval_seconds)

        t_monitor = threading.Thread(target=monitor_loop, daemon=True)
        t_monitor.start()

        # Periodic Notion sync loop
        def sync_loop():
            sync_interval_sec = max(60, self.config.tracker.sync_interval_minutes * 60)
            logger.info("Starting periodic Notion sync loop (interval: %d mins)", self.config.tracker.sync_interval_minutes)
            while self._running:
                try:
                    self.sync_engine.sync_pending()
                except Exception as e:
                    logger.error("Error in periodic Notion sync: %s", e)
                time.sleep(sync_interval_sec)

        if self.config.is_notion_configured():
            t_sync = threading.Thread(target=sync_loop, daemon=True)
            t_sync.start()

    def stop(self) -> None:
        """Shuts down tracker safely."""
        logger.info("Stopping GameTimeTracker...")
        self._running = False
        # Discard or close any active sessions conservatively
        for pid in list(self.session_manager.active_sessions.keys()):
            self.session_manager.handle_game_stopped(pid)
        logger.info("GameTimeTracker shutdown cleanly.")

    def run_gui(self, start_minimized: bool = False) -> None:
        """Runs the modern GUI application with system tray integration."""
        self.start_background_workers()

        from tracker.ui.gui import GameTimeGUI

        gui = GameTimeGUI(
            db=self.db,
            session_manager=self.session_manager,
            sync_engine=self.sync_engine,
            config=self.config,
            on_exit_callback=self.stop,
        )

        def show_gui_from_tray():
            gui.after(0, lambda: (gui.deiconify(), gui.lift(), gui.focus_force(), gui.refresh_all_data()))

        self.tray_app = SystemTrayApp(
            db=self.db,
            session_manager=self.session_manager,
            sync_engine=self.sync_engine,
            prompt_mgr=self.prompt_mgr,
            on_exit_callback=gui.quit_app,
            on_show_gui_callback=show_gui_from_tray,
        )
        self.tray_app.run_detached()

        if start_minimized:
            gui.withdraw()

        logger.info("GameTimeTracker GUI running.")
        try:
            gui.mainloop()
        except KeyboardInterrupt:
            gui.quit_app()

    def run_tray(self) -> None:
        """Runs the application with system tray in taskbar."""
        self.run_gui(start_minimized=True)

    def run_daemon(self) -> None:
        """Runs in headless daemon mode without GUI."""
        self.start_background_workers()
        logger.info("GameTimeTracker headless daemon running. Press Ctrl+C to stop.")

        def handle_signal(sig, frame):
            self.stop()
            sys.exit(0)

        signal.signal(signal.SIGINT, handle_signal)
        signal.signal(signal.SIGTERM, handle_signal)

        while self._running:
            time.sleep(1)


# ------------------ CLI Helper Commands ------------------

def cmd_sync(app: TrackerApplication) -> None:
    print("正在执行 Notion 同步...")
    if not app.config.is_notion_configured():
        print("错误: config.json 中未完整配置 Notion API Token 及 Database ID。")
        return
    succ, fail = app.sync_engine.sync_pending()
    print(f"同步完成: 成功 {succ} 项, 失败/未映射 {fail} 项。详细请查看 logs/tracker.log。")


def cmd_summary(app: TrackerApplication) -> None:
    today_str = datetime.now().strftime("%Y-%m-%d")
    app.rollup.rollup_recent_days(days_back=7)
    records = app.db.get_sessions_for_date_range("1970-01-01 00:00:00", "2099-01-01 00:00:00")

    print("\n======================= 最近 7 天游玩汇总 =======================")
    print(f"{'日期':<12} {'游戏名称':<25} {'时长(分钟)':<12} {'同步状态':<10} {'Notion Page ID'}")
    print("-" * 75)

    from datetime import timedelta
    today = datetime.now().date()
    for i in range(7, -1, -1):
        d_str = (today - timedelta(days=i)).strftime("%Y-%m-%d")
        summaries = app.db.get_daily_summaries_by_date(d_str)
        for s in summaries:
            game = app.db.get_game_by_id(s.game_id)
            g_name = game.name if game else f"Game #{s.game_id}"
            p_id = s.notion_page_id or "(未同步)"
            print(f"{s.date:<12} {g_name:<25} {s.duration_minutes:<12} {s.sync_status:<10} {p_id}")
    print("=================================================================\n")


def cmd_status(app: TrackerApplication) -> None:
    running = app.session_manager.get_running_games()
    active_games = app.db.list_all_games(status="active")
    pending = app.db.get_pending_sync_summaries()

    print("\n===================== GameTimeTracker 运行状态 =====================")
    print(f"正在运行的游戏数: {len(running)}")
    for g, dur in running:
        m = dur // 60
        s = dur % 60
        print(f"  ● {g.name} ({g.platform}) - 已运行 {m}分{s}秒")

    print(f"\n已登记活跃游戏库: {len(active_games)} 款")
    for g in active_games[:10]:
        n_status = f"已绑定 -> {g.notion_page_id}" if g.notion_page_id else "(未绑定 Notion，每日时长独立同步)"
        print(f"  [{g.platform}] {g.name:<25} {n_status}")
    if len(active_games) > 10:
        print(f"  ... 以及其他 {len(active_games) - 10} 款")

    print(f"\n待同步 Notion 条目数: {len(pending)}")
    print(f"开机自启动计划状态:\n{query_startup_task()}")
    print("====================================================================\n")


def cmd_test_notion(app: TrackerApplication) -> None:
    print("正在连接并测试 Notion 配置...")
    if not app.config.is_notion_configured():
        print("❌ Notion 配置不完整，请检查 config.json 中的 token 与 database id。")
        return

    client = app.notion_client
    try:
        user_info = client.test_connection()
        bot_name = user_info.get("name", "Unknown Bot")
        print(f"✅ Notion Token 验证成功! 机器人名称: {bot_name}")
    except Exception as e:
        print(f"❌ Notion Token 验证失败: {e}")
        return

    # Check Game Master DB
    try:
        g_db = client.get_database(app.config.notion.game_database_id)
        g_title = "".join([t.get("plain_text", "") for t in g_db.get("title", [])])
        print(f"✅ 成功连接【游戏总表】: '{g_title}' (ID: {app.config.notion.game_database_id})")
    except Exception as e:
        print(f"❌ 无法访问【游戏总表】Database: {e}\n提示: 请确保在 Notion 中已将该 Database 授权给本 Integration。")

    # Check Daily DB
    try:
        d_db = client.get_database(app.config.notion.daily_database_id)
        d_title = "".join([t.get("plain_text", "") for t in d_db.get("title", [])])
        props = d_db.get("properties", {})
        print(f"✅ 成功连接【每日游戏时长】: '{d_title}' (ID: {app.config.notion.daily_database_id})")

        # Validate properties
        date_p = app.config.notion.daily_date_property
        rel_p = app.config.notion.daily_game_relation_property
        dur_p = app.config.notion.daily_playtime_property

        d_ok = date_p in props and props[date_p].get("type") == "date"
        r_ok = rel_p in props and props[rel_p].get("type") == "relation"
        n_ok = dur_p in props and props[dur_p].get("type") == "number"

        print(f"   属性检查:")
        print(f"   - 日期属性 '{date_p}': {'✅ 正确 (date)' if d_ok else '❌ 未找到或类型不是 date'}")
        print(f"   - 游戏关联 '{rel_p}': {'✅ 正确 (relation)' if r_ok else '❌ 未找到或类型不是 relation'}")
        print(f"   - 时长属性 '{dur_p}': {'✅ 正确 (number)' if n_ok else '❌ 未找到或类型不是 number'}")

        if d_ok and r_ok and n_ok:
            print("🎉 所有 Notion Database 属性验证通过！随时可以自动同步。")
    except Exception as e:
        print(f"❌ 无法访问【每日游戏时长】Database: {e}\n提示: 请确保在 Notion 中已将该 Database 授权给本 Integration。")


def cmd_clear_test_data(app: TrackerApplication) -> None:
    print("正在清空所有本地游玩测试数据，并同步清理 Notion 每日卡片...")
    archived = app.sync_engine.clear_all_playtime(archive_notion=True)
    print(f"清空完成！本地所有测试游玩时长记录已清空，游戏库定义完好保留。同步移入 Notion 废纸篓卡片: {archived} 张。")


def cmd_clear_game(app: TrackerApplication, target: str) -> None:
    game = None
    if target.isdigit():
        game = app.db.get_game_by_id(int(target))
    if not game:
        for g in app.db.list_all_games():
            if g.name.lower() == target.lower():
                game = g
                break
    if not game:
        print(f"未找到游戏: {target}")
        return
    archived = app.sync_engine.clear_game_playtime(game.id, archive_notion=True)
    print(f"已清空游戏 [{game.name}] 的所有本地游玩时长记录！(同步移入 Notion 废纸篓卡片: {archived} 张)")


def main() -> None:
    parser = argparse.ArgumentParser(description="Windows 游戏时长自动监听 + Notion 自动同步")
    parser.add_argument("--gui", action="store_true", help="启动并显示现代 GUI 游戏时长看板 (默认模式)")
    parser.add_argument("--tray", action="store_true", help="启动并仅在 Windows 系统托盘后台静默运行")
    parser.add_argument("--daemon", action="store_true", help="以无头终端后台模式运行 (无GUI)")
    parser.add_argument("--sync", action="store_true", help="立即执行一次 Notion 同步")
    parser.add_argument("--summary", action="store_true", help="查看最近每日游玩时长与同步汇总")
    parser.add_argument("--status", action="store_true", help="查看当前运行状态与正在玩的游戏")
    parser.add_argument("--rebind", action="store_true", help="打开游戏映射管理界面")
    parser.add_argument("--test-notion", action="store_true", help="测试 Notion API 连接与数据库属性权限")
    parser.add_argument("--clear-test-data", action="store_true", help="一键清空所有本地测试时长，并同步清理 Notion 每日时长卡片")
    parser.add_argument("--clear-game", type=str, default=None, help="清空指定游戏的游玩时长记录 (指定游戏名称或ID)")
    parser.add_argument("--install-startup", action="store_true", help="注册 Windows 开机自动启动任务计划")
    parser.add_argument("--uninstall-startup", action="store_true", help="注销 Windows 开机自动启动任务计划")
    parser.add_argument("--config", type=str, default=None, help="指定配置文件路径")

    args = parser.parse_args()

    # Priority 1: Startup Task Management
    if args.install_startup:
        ok, msg = install_startup_task()
        print(msg)
        return
    if args.uninstall_startup:
        ok, msg = uninstall_startup_task()
        print(msg)
        return

    # Initialize app
    app = TrackerApplication(args.config)

    if args.sync:
        cmd_sync(app)
        return
    elif args.summary:
        cmd_summary(app)
        return
    elif args.status:
        cmd_status(app)
        return
    elif args.clear_test_data:
        cmd_clear_test_data(app)
        return
    elif args.clear_game:
        cmd_clear_game(app, args.clear_game)
        return
    elif args.test_notion:
        cmd_test_notion(app)
        return
    elif args.rebind:
        notion_games = app.sync_engine.refresh_master_games() if app.config.is_notion_configured() else {}
        app.prompt_mgr.open_rebind_dialog(notion_games)
        return
    elif args.daemon:
        app.run_daemon()
        return
    elif args.gui:
        # Explicitly show window
        app.run_gui(start_minimized=False)
        return
    else:
        # Default: Start minimized directly to system tray
        app.run_gui(start_minimized=True)


if __name__ == "__main__":
    main()
