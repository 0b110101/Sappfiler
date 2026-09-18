"""Task scheduler integration package."""

from tracker.scheduler.task_scheduler import (
    install_startup_task,
    query_startup_task,
    uninstall_startup_task,
)

__all__ = ["install_startup_task", "uninstall_startup_task", "query_startup_task"]
