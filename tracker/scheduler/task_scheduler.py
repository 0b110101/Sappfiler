"""Windows Task Scheduler integration for automatic logon background startup."""

import os
import subprocess
import sys
from pathlib import Path
from typing import Tuple

from tracker.logger import logger

TASK_NAME = "GameTimeTracker"


def get_pythonw_executable() -> str:
    """Finds the pythonw.exe corresponding to current Python environment."""
    current_python = sys.executable
    parent = Path(current_python).parent
    pythonw = parent / "pythonw.exe"
    if pythonw.exists():
        return str(pythonw)
    return current_python


def install_startup_task() -> Tuple[bool, str]:
    """Registers GameTimeTracker as a Windows Scheduled Task running on user logon."""
    pythonw = get_pythonw_executable()
    main_py = str(Path(__file__).resolve().parent.parent / "main.py")
    working_dir = str(Path(__file__).resolve().parent.parent.parent)

    cmd = f'"{pythonw}" "{main_py}" --tray'

    # Build schtasks command
    # /SC ONLOGON : runs when the user logs on
    # /TN : task name
    # /TR : command to run
    # /F : force create/overwrite
    # /RL LIMITED : standard user rights, avoids annoying UAC popups
    args = [
        "schtasks",
        "/Create",
        "/TN", TASK_NAME,
        "/TR", cmd,
        "/SC", "ONLOGON",
        "/RL", "LIMITED",
        "/F",
    ]

    try:
        res = subprocess.run(args, capture_output=True, text=True, check=False)
        if res.returncode == 0:
            logger.info("Successfully created Windows startup task '%s': %s", TASK_NAME, cmd)
            return True, f"已成功注册 Windows 开机自动启动任务 [{TASK_NAME}]！\n命令: {cmd}"
        else:
            err = res.stderr.strip() or res.stdout.strip()
            logger.error("Failed to create Windows task: %s", err)
            return False, f"创建任务失败: {err}"
    except Exception as e:
        logger.error("Exception creating task: %s", e)
        return False, str(e)


def uninstall_startup_task() -> Tuple[bool, str]:
    """Removes the GameTimeTracker task from Windows Task Scheduler."""
    args = ["schtasks", "/Delete", "/TN", TASK_NAME, "/F"]
    try:
        res = subprocess.run(args, capture_output=True, text=True, check=False)
        if res.returncode == 0:
            logger.info("Successfully removed Windows startup task '%s'", TASK_NAME)
            return True, f"已成功删除任务计划 [{TASK_NAME}]"
        else:
            err = res.stderr.strip() or res.stdout.strip()
            return False, f"删除任务失败: {err}"
    except Exception as e:
        return False, str(e)


def query_startup_task() -> str:
    """Queries current status of the GameTimeTracker task."""
    args = ["schtasks", "/Query", "/TN", TASK_NAME, "/FO", "LIST"]
    try:
        res = subprocess.run(args, capture_output=True, text=True, check=False)
        if res.returncode == 0:
            return res.stdout.strip()
        return "未发现已安装的开机自启动任务"
    except Exception as e:
        return f"查询失败: {e}"
