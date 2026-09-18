"""Logging module for GameTimeTracker with automatic secret scrubbing."""

import logging
import os
import re
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Optional

# Regex patterns for sensitive tokens
TOKEN_PATTERN = re.compile(r"(secret_[a-zA-Z0-9_-]+|ntn_[a-zA-Z0-9_-]+|Bearer\s+[a-zA-Z0-9_.-]+)")


class SecretMaskingFormatter(logging.Formatter):
    """Logging formatter that automatically sanitizes sensitive tokens from log messages."""

    def format(self, record: logging.LogRecord) -> str:
        orig = super().format(record)
        return TOKEN_PATTERN.sub("[REDACTED_TOKEN]", orig)


def setup_logger(
    log_dir: Optional[str] = None,
    log_level: int = logging.INFO,
    max_bytes: int = 5 * 1024 * 1024,
    backup_count: int = 5,
) -> logging.Logger:
    """Configures and returns the central logger."""
    logger = logging.getLogger("GameTimeTracker")
    if logger.handlers:
        return logger

    logger.setLevel(log_level)

    if not log_dir:
        base_dir = Path(__file__).resolve().parent.parent
        log_dir_path = base_dir / "logs"
    else:
        log_dir_path = Path(log_dir)

    log_dir_path.mkdir(parents=True, exist_ok=True)
    log_file = log_dir_path / "tracker.log"

    formatter = SecretMaskingFormatter(
        "[%(asctime)s] [%(levelname)s] [%(module)s] %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    # Rotating File Handler
    file_handler = RotatingFileHandler(
        log_file,
        maxBytes=max_bytes,
        backupCount=backup_count,
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)
    file_handler.setLevel(log_level)
    logger.addHandler(file_handler)

    # Console Handler
    console_handler = logging.StreamHandler()
    console_handler.setFormatter(formatter)
    console_handler.setLevel(log_level)
    logger.addHandler(console_handler)

    return logger


logger = setup_logger()
