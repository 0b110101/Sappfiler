"""User prompts and interactive dialogs for unknown games, binding confirmation, and rebinding."""

import os
import sys
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk
from typing import Any, Dict, List, Optional

from tracker.core.game_matcher import NotionGameCandidate
from tracker.db.database import Database
from tracker.detectors.standalone import compute_path_hash
from tracker.logger import logger
from tracker.models import GameIdentity, GameRecord


class UserPromptManager:
    """Manages user confirmation prompts via GUI dialog or CLI."""

    def __init__(self, db: Database, sync_engine: Optional[Any] = None):
        self.db = db
        self.sync_engine = sync_engine

    def prompt_fuzzy_candidate_gui(
        self,
        game_name: str,
        candidates: List[NotionGameCandidate],
    ) -> Optional[str]:
        """Pops up a dialog to confirm fuzzy candidates."""
        if not candidates:
            return None

        top = candidates[0]
        root = tk.Tk()
        root.withdraw()
        root.attributes("-topmost", True)

        msg = (
            f"检测到游戏: {game_name}\n\n"
            f"在 Notion 游戏总表中找到高度相似的游戏:\n"
            f"→ {top.title} (相似度: {top.score:.1f}%)\n\n"
            f"是否绑定为此游戏？"
        )
        confirmed = messagebox.askyesno("确认游戏绑定 - GameTimeTracker", msg, parent=root)
        root.destroy()

        if confirmed:
            return top.page_id
        return None

    def open_rebind_dialog(self, notion_games: Dict[str, str]) -> None:
        """Opens a comprehensive GUI management window for viewing/rebinding Notion games and confirming new candidates."""
        root = tk.Tk()
        root.title("游戏映射与管理 - GameTimeTracker")
        root.geometry("820x540")
        root.attributes("-topmost", True)

        notebook = ttk.Notebook(root)
        notebook.pack(fill=tk.BOTH, expand=True, padx=8, pady=8)

        # Tab 1: 已登记游戏与 Notion 映射
        tab_active = ttk.Frame(notebook, padding=10)
        notebook.add(tab_active, text="  已登记游戏与 Notion 映射  ")

        # Tab 2: 待确认的独立游戏候选
        tab_unconfirmed = ttk.Frame(notebook, padding=10)
        notebook.add(tab_unconfirmed, text="  待确认应用候选  ")

        # ------------------ Tab 1: 已登记游戏 ------------------
        columns_active = ("id", "name", "platform", "platform_id", "notion_title", "notion_page_id")
        tree_active = ttk.Treeview(tab_active, columns=columns_active, show="headings", selectmode="browse")

        tree_active.heading("id", text="ID")
        tree_active.heading("name", text="游戏名称")
        tree_active.heading("platform", text="平台")
        tree_active.heading("platform_id", text="平台识别码")
        tree_active.heading("notion_title", text="Notion 对应名称")
        tree_active.heading("notion_page_id", text="Notion Page ID")

        tree_active.column("id", width=35, anchor=tk.CENTER)
        tree_active.column("name", width=180)
        tree_active.column("platform", width=75, anchor=tk.CENTER)
        tree_active.column("platform_id", width=110)
        tree_active.column("notion_title", width=180)
        tree_active.column("notion_page_id", width=190)

        scroll_active = ttk.Scrollbar(tab_active, orient=tk.VERTICAL, command=tree_active.yview)
        tree_active.configure(yscroll=scroll_active.set)
        scroll_active.pack(side=tk.RIGHT, fill=tk.Y)
        tree_active.pack(fill=tk.BOTH, expand=True)

        btn_box1 = ttk.Frame(tab_active, padding=5)
        btn_box1.pack(fill=tk.X, pady=5)

        def refresh_active_table():
            for item in tree_active.get_children():
                tree_active.delete(item)
            games = self.db.list_all_games()
            for g in games:
                if g.status == "active":
                    val = notion_games.get(g.notion_page_id)
                    if hasattr(val, "title"):
                        n_title = val.title
                    elif val:
                        n_title = str(val)
                    else:
                        n_title = "(未绑定)" if not g.notion_page_id else g.notion_page_id
                    tree_active.insert("", tk.END, values=(g.id, g.name, g.platform, g.platform_id, n_title, g.notion_page_id or ""))

        def on_rebind_click():
            selected = tree_active.selection()
            if not selected:
                messagebox.showwarning("提示", "请先在列表中选中一款游戏", parent=root)
                return
            item = tree_active.item(selected[0])
            game_id = int(item["values"][0])
            game_name = item["values"][1]
            curr_pid = item["values"][5]

            new_pid = simpledialog.askstring(
                "重新绑定 Notion Page ID",
                f"为游戏 [{game_name}] 输入新的 Notion Page ID:\n(可在 Notion 页面链接中复制 32 位 ID，留空则解除绑定)",
                initialvalue=str(curr_pid) if curr_pid else "",
                parent=root,
            )
            if new_pid is not None:
                clean_pid = new_pid.strip().replace("-", "") or None
                self.db.update_game_notion_id(game_id, clean_pid)
                if self.sync_engine and clean_pid:
                    self.sync_engine.run_task_b_backfill_relations()
                messagebox.showinfo("成功", f"游戏 [{game_name}] 的 Notion 映射已更新，并已自动回溯补全历史每日记录的关联！", parent=root)
                refresh_active_table()

        def on_create_master_page():
            selected = tree_active.selection()
            if not selected:
                messagebox.showwarning("提示", "请先在列表中选中一款游戏", parent=root)
                return
            item = tree_active.item(selected[0])
            game_id = int(item["values"][0])
            game_name = item["values"][1]
            curr_pid = item["values"][5]
            if curr_pid:
                messagebox.showinfo("提示", f"游戏 [{game_name}] 已经绑定了 Notion Page ID:\n{curr_pid}", parent=root)
                return
            if not self.sync_engine or not self.sync_engine.client or not self.sync_engine.config.is_notion_configured():
                messagebox.showerror("错误", "未配置 Notion 同步引擎或 Token 无效", parent=root)
                return

            confirm = messagebox.askyesno(
                "在总表建档",
                f"是否在 Notion「游戏总表」中为 [{game_name}] 新建条目，并立即将之前所有每日记录关联到此页面？",
                parent=root,
            )
            if confirm:
                new_id = self.sync_engine.create_master_game_and_bind(game_id)
                if new_id:
                    notion_games[new_id] = game_name
                    messagebox.showinfo("建档成功", f"已在 Notion 总表创建 [{game_name}] 页面！\nPage ID: {new_id}\n历史每日记录关联已全部补齐！", parent=root)
                    refresh_active_table()
                else:
                    messagebox.showerror("建档失败", "创建失败，请检查网络或 Notion Token 配置", parent=root)

        def on_sync_now():
            if not self.sync_engine or not self.sync_engine.client or not self.sync_engine.config.is_notion_configured():
                messagebox.showwarning("提示", "Notion 尚未配置或 Token 无效", parent=root)
                return
            s, f = self.sync_engine.sync_pending()
            if self.sync_engine._master_games_cache:
                notion_games.update(self.sync_engine._master_games_cache)
            messagebox.showinfo("同步完成", f"Notion 双任务同步完成！\n成功: {s} 条，失败/重试: {f} 条", parent=root)
            refresh_active_table()

        def on_manual_add_exe():
            file_path = filedialog.askopenfilename(
                title="选择独立游戏主程序 (.exe)",
                filetypes=[("可执行程序", "*.exe"), ("所有文件", "*.*")],
                parent=root,
            )
            if not file_path:
                return
            norm_path = os.path.normpath(file_path)
            default_name = os.path.splitext(os.path.basename(norm_path))[0]
            game_name = simpledialog.askstring(
                "游戏显示名称",
                "请输入该游戏的显示名称:",
                initialvalue=default_name,
                parent=root,
            )
            if game_name and game_name.strip():
                p_hash = compute_path_hash(norm_path)
                identity = GameIdentity(
                    platform="Standalone",
                    platform_id=p_hash,
                    name=game_name.strip(),
                    executable=os.path.basename(norm_path),
                    executable_path=norm_path,
                )
                game = self.db.get_or_create_game(identity)
                self.db.update_game_status(game.id, "active")
                messagebox.showinfo("成功", f"独立游戏 [{game_name.strip()}] 已成功添加！", parent=root)
                refresh_active_table()

        def on_exclude_game_click():
            selected = tree_active.selection()
            if not selected:
                messagebox.showwarning("提示", "请先在列表中选中一款游戏", parent=root)
                return
            item = tree_active.item(selected[0])
            game_id = item["values"][0]
            game_name = item["values"][1]

            confirm = messagebox.askyesno(
                "排除游戏确认",
                f"是否将 [{game_name}] 从游戏列表中排除并加入黑名单？\n\n排除后该应用将不再被作为游戏追踪，其未同步记录也会被清理。",
                parent=root,
            )
            if confirm:
                self.db.update_game_status(game_id, "ignored")
                messagebox.showinfo("排除成功", f"[{game_name}] 已移入排除黑名单！", parent=root)
        def on_clear_playtime_click():
            selected = tree_active.selection()
            if not selected:
                messagebox.showwarning("提示", "请先在列表中选中一款游戏", parent=root)
                return
            item = tree_active.item(selected[0])
            game_id = int(item["values"][0])
            game_name = item["values"][1]

            confirm = messagebox.askyesno(
                "清除游玩记录",
                f"是否清除游戏 [{game_name}] 的所有本地游玩历史？\n\n注意：\n1. 本地数据库中该游戏的所有游玩会话与每日统计将被彻底清空。\n2. 若已配置 Notion，对应每日打卡卡片也会自动移入废纸篓。\n3. 该游戏在总表的关联保持不变。",
                parent=root,
            )
            if confirm:
                archived = 0
                if self.sync_engine:
                    archived = self.sync_engine.clear_game_playtime(game_id, archive_notion=True)
                else:
                    self.db.delete_game_playtime(game_id)
                messagebox.showinfo("清除完成", f"已成功清空 [{game_name}] 的本地游玩时长！\n(已同步清理 Notion 云端打卡卡片: {archived} 张)", parent=root)
                refresh_active_table()

        def on_delete_game_click():
            selected = tree_active.selection()
            if not selected:
                messagebox.showwarning("提示", "请先在列表中选中一款游戏", parent=root)
                return
            item = tree_active.item(selected[0])
            game_id = int(item["values"][0])
            game_name = item["values"][1]

            confirm = messagebox.askyesnocancel(
                "彻底删除游戏确认",
                f"是否彻底删除游戏 [{game_name}]？\n\n"
                f"【是 (Yes)】：同时删除本地数据 + Notion 每日时长表卡片 + Notion「游戏总表」页面！\n"
                f"【否 (No)】：仅删除本地数据 + Notion 每日时长表卡片（保留 Notion 游戏总表页面）\n"
                f"【取消 (Cancel)】：放弃本次删除操作",
                parent=root,
            )
            if confirm is True:
                if self.sync_engine:
                    self.sync_engine.delete_game_completely(game_id, archive_notion_daily=True, archive_notion_master=True)
                else:
                    self.db.delete_game(game_id)
                messagebox.showinfo("删除成功", f"游戏 [{game_name}] 已从本地数据库、Notion 每日时长表及游戏总表中彻底删除！", parent=root)
                refresh_active_table()
            elif confirm is False:
                if self.sync_engine:
                    self.sync_engine.delete_game_completely(game_id, archive_notion_daily=True, archive_notion_master=False)
                else:
                    self.db.delete_game(game_id)
                messagebox.showinfo("删除成功", f"游戏 [{game_name}] 已从本地及每日时长表中删除（Notion 游戏总表已保留）。", parent=root)
                refresh_active_table()

        ttk.Button(btn_box1, text="重新绑定 Page ID", command=on_rebind_click).pack(side=tk.LEFT, padx=2)
        ttk.Button(btn_box1, text="在总表一键建档", command=on_create_master_page).pack(side=tk.LEFT, padx=2)
        ttk.Button(btn_box1, text="清除游玩记录", command=on_clear_playtime_click).pack(side=tk.LEFT, padx=2)
        ttk.Button(btn_box1, text="彻底删除游戏", command=on_delete_game_click).pack(side=tk.LEFT, padx=2)
        ttk.Button(btn_box1, text="排除此应用", command=on_exclude_game_click).pack(side=tk.LEFT, padx=2)
        ttk.Button(btn_box1, text="手动添加...", command=on_manual_add_exe).pack(side=tk.LEFT, padx=2)
        ttk.Button(btn_box1, text="立即同步", command=on_sync_now).pack(side=tk.LEFT, padx=2)
        ttk.Button(btn_box1, text="刷新", command=refresh_active_table).pack(side=tk.LEFT, padx=2)

        # ------------------ Tab 2: 待确认候选 ------------------
        columns_unconf = ("id", "name", "executable", "path")
        tree_unconf = ttk.Treeview(tab_unconfirmed, columns=columns_unconf, show="headings", selectmode="browse")

        tree_unconf.heading("id", text="ID")
        tree_unconf.heading("name", text="推荐名称")
        tree_unconf.heading("executable", text="主程序")
        tree_unconf.heading("path", text="可执行文件完整路径")

        tree_unconf.column("id", width=35, anchor=tk.CENTER)
        tree_unconf.column("name", width=160)
        tree_unconf.column("executable", width=140)
        tree_unconf.column("path", width=420)

        scroll_unconf = ttk.Scrollbar(tab_unconfirmed, orient=tk.VERTICAL, command=tree_unconf.yview)
        tree_unconf.configure(yscroll=scroll_unconf.set)
        scroll_unconf.pack(side=tk.RIGHT, fill=tk.Y)
        tree_unconf.pack(fill=tk.BOTH, expand=True)

        btn_box2 = ttk.Frame(tab_unconfirmed, padding=5)
        btn_box2.pack(fill=tk.X, pady=5)

        def refresh_unconfirmed_table():
            for item in tree_unconf.get_children():
                tree_unconf.delete(item)
            games = self.db.list_all_games()
            count = 0
            for g in games:
                if g.status == "unconfirmed":
                    tree_unconf.insert("", tk.END, values=(g.id, g.name, g.executable, g.executable_path))
                    count += 1
            notebook.tab(tab_unconfirmed, text=f"  待确认应用候选 ({count})  ")

        def on_confirm_candidate():
            selected = tree_unconf.selection()
            if not selected:
                messagebox.showwarning("提示", "请在待确认列表中选中一项", parent=root)
                return
            item = tree_unconf.item(selected[0])
            game_id = item["values"][0]
            curr_name = item["values"][1]

            game_name = simpledialog.askstring("确认游戏名称", "指定该游戏的显示名称:", initialvalue=curr_name, parent=root)
            if game_name and game_name.strip():
                with self.db.transaction() as cur:
                    cur.execute("UPDATE games SET name = ?, status = 'active' WHERE id = ?;", (game_name.strip(), game_id))
                messagebox.showinfo("成功", f"[{game_name.strip()}] 已成功添加为游戏！", parent=root)
                refresh_unconfirmed_table()
                refresh_active_table()

        def on_ignore_candidate():
            selected = tree_unconf.selection()
            if not selected:
                messagebox.showwarning("提示", "请在待确认列表中选中一项", parent=root)
                return
            item = tree_unconf.item(selected[0])
            game_id = item["values"][0]
            self.db.update_game_status(game_id, "ignored")
            refresh_unconfirmed_table()

        ttk.Button(btn_box2, text="确认添加为游戏", command=on_confirm_candidate).pack(side=tk.LEFT, padx=4)
        ttk.Button(btn_box2, text="忽略此程序", command=on_ignore_candidate).pack(side=tk.LEFT, padx=4)
        ttk.Button(btn_box2, text="刷新", command=refresh_unconfirmed_table).pack(side=tk.LEFT, padx=4)

        # Initial loads
        refresh_active_table()
        refresh_unconfirmed_table()

        ttk.Button(root, text="关闭", command=root.destroy).pack(side=tk.RIGHT, padx=15, pady=6)
        root.mainloop()
