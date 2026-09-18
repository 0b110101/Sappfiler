"""Unit tests for GOG detection, categorized blacklist filtering, and game heuristics."""

import os
import tempfile
import pytest

from tracker.detectors.gog import GOGDetector
from tracker.process.filter import (
    BROWSER_PROCESSES,
    CREATIVE_AND_3D_PROCESSES,
    ELECTRON_AND_WEBAPP_PROCESSES,
    WALLPAPER_CAPTURE_OVERLAY_PROCESSES,
    is_blacklisted_process,
    is_potential_game_candidate,
)
from tracker.process.heuristics import evaluate_game_heuristics


def test_categorized_blacklist():
    # 1. Browsers & GPU sub-processes
    assert is_blacklisted_process("chrome.exe") is True
    assert is_blacklisted_process("msedge.exe") is True
    assert is_blacklisted_process("firefox.exe") is True
    assert is_blacklisted_process("msedgewebview2.exe") is True

    # 2. Electron & WebApps
    assert is_blacklisted_process("discord.exe") is True
    assert is_blacklisted_process("code.exe") is True
    assert is_blacklisted_process("slack.exe") is True
    assert is_blacklisted_process("workbuddy.exe") is True

    # 3. Wallpaper, capture, and overlays
    assert is_blacklisted_process("wallpaper64.exe") is True
    assert is_blacklisted_process("obs64.exe") is True
    assert is_blacklisted_process("rtss.exe") is True
    assert is_blacklisted_process("msiafterburner.exe") is True

    # 4. 3D Creative & Video Editing
    assert is_blacklisted_process("blender.exe") is True
    assert is_blacklisted_process("3dsmax.exe") is True
    assert is_blacklisted_process("unity.exe") is True
    assert is_blacklisted_process("resolve.exe") is True

    # 5. Whole Windows directory
    assert is_blacklisted_process("someapp.exe", r"C:\Windows\SystemApps\test.exe") is True
    assert is_blacklisted_process("tool.exe", r"C:\Windows\ImmersiveControlPanel\test.exe") is True

    # Genuine game exe outside blacklist
    assert is_blacklisted_process("bg3.exe", r"D:\Steam\steamapps\common\Baldurs Gate 3\bg3.exe") is False
    assert is_blacklisted_process("Lunacid.exe", r"D:\Games\Lunacid\Lunacid.exe") is False


def test_gog_detector_with_info_manifest():
    with tempfile.TemporaryDirectory() as temp_dir:
        # Create mock GOG game folder
        game_dir = os.path.join(temp_dir, "The Witcher 3 Wild Hunt")
        bin_dir = os.path.join(game_dir, "bin", "x64")
        os.makedirs(bin_dir, exist_ok=True)

        info_file = os.path.join(game_dir, "goggame-1207664643.info")
        with open(info_file, "w", encoding="utf-8") as f:
            f.write('''
            {
                "gameId": "1207664643",
                "rootGameId": "1207664643",
                "name": "The Witcher 3: Wild Hunt - Complete Edition",
                "playTasks": [
                    {
                        "path": "bin\\\\x64\\\\witcher3.exe",
                        "type": "FileTask",
                        "isPrimary": true
                    }
                ]
            }
            ''')

        mock_exe = os.path.join(bin_dir, "witcher3.exe")
        detector = GOGDetector()
        identity = detector.detect(pid=1234, name="witcher3.exe", exe_path=mock_exe)

        assert identity is not None
        assert identity.platform == "GOG"
        assert identity.platform_id == "1207664643"
        assert identity.name == "The Witcher 3: Wild Hunt - Complete Edition"
        assert identity.executable == "witcher3.exe"


def test_game_heuristics_evaluation():
    with tempfile.TemporaryDirectory() as temp_dir:
        # Setup mock indie game in D:\Games\MyIndieGame
        game_folder = os.path.join(temp_dir, "Games", "MyIndieGame")
        os.makedirs(game_folder, exist_ok=True)
        exe_file = os.path.join(game_folder, "MyGame.exe")
        with open(exe_file, "w") as f:
            f.write("")

        # Create Unity Data folder
        data_dir = os.path.join(game_folder, "MyGame_Data")
        os.makedirs(data_dir, exist_ok=True)

        # Evaluate heuristics
        result = evaluate_game_heuristics(
            pid=os.getpid(),
            proc_name="MyGame.exe",
            exe_path=exe_file,
            custom_game_dirs=[temp_dir],
        )

        assert result.is_in_game_path is True
        assert "MyGame_Data" in result.detected_engine
        assert result.confidence_score >= 65.0
        assert result.is_likely_game is True
