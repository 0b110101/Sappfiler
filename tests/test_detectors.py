"""Unit tests for platform detectors and process filters."""

import os
import tempfile
import pytest
from tracker.db.database import Database
from tracker.detectors.router import DetectorRouter
from tracker.detectors.standalone import StandaloneDetector, compute_path_hash
from tracker.detectors.steam import SteamDetector, parse_vdf_simple
from tracker.models import GameIdentity
from tracker.process.filter import is_blacklisted_process


def test_blacklist_filter():
    assert is_blacklisted_process("steam.exe") is True
    assert is_blacklisted_process("EpicGamesLauncher.exe") is True
    assert is_blacklisted_process("CrashReportClient.exe") is True
    assert is_blacklisted_process("EasyAntiCheat.exe") is True
    assert is_blacklisted_process("svchost.exe") is True
    assert is_blacklisted_process("explorer.exe") is True
    assert is_blacklisted_process("bg3.exe") is False
    assert is_blacklisted_process("Hades2.exe") is False


def test_vdf_parser():
    vdf_sample = '''
    "AppState"
    {
        "appid"     "1086940"
        "name"      "Baldur's Gate 3"
        "installdir"    "Baldurs Gate 3"
        "SizeOnDisk"    "143627192"
    }
    '''
    kv = parse_vdf_simple(vdf_sample)
    assert kv["appid"] == "1086940"
    assert kv["name"] == "Baldur's Gate 3"
    assert kv["installdir"] == "Baldurs Gate 3"


def test_steam_detector_matching():
    with tempfile.TemporaryDirectory() as temp_dir:
        # Create mock Steam library structure
        steamapps = os.path.join(temp_dir, "steamapps")
        common = os.path.join(steamapps, "common", "Baldurs Gate 3", "bin")
        os.makedirs(common, exist_ok=True)

        acf_file = os.path.join(steamapps, "appmanifest_1086940.acf")
        with open(acf_file, "w", encoding="utf-8") as f:
            f.write('''
            "AppState"
            {
                "appid" "1086940"
                "name" "Baldur's Gate 3"
                "installdir" "Baldurs Gate 3"
            }
            ''')

        detector = SteamDetector(custom_libraries=[temp_dir])
        assert "1086940" in detector.installed_games

        mock_exe = os.path.join(common, "bg3.exe")
        identity = detector.detect(pid=5000, name="bg3.exe", exe_path=mock_exe)

        assert identity is not None
        assert identity.platform == "Steam"
        assert identity.platform_id == "1086940"
        assert identity.name == "Baldur's Gate 3"
        assert identity.executable == "bg3.exe"


def test_standalone_detector():
    with tempfile.NamedTemporaryFile(suffix=".db", delete=False) as f:
        db_path = f.name
    db = Database(db_path)

    # Register an indie game
    path = r"D:\Indie Games\Lunacid\Lunacid.exe"
    p_hash = compute_path_hash(path)
    identity = GameIdentity(
        platform="Standalone",
        platform_id=p_hash,
        name="Lunacid",
        executable="Lunacid.exe",
        executable_path=path,
    )
    db.get_or_create_game(identity)

    standalone_det = StandaloneDetector(db)
    detected = standalone_det.detect(pid=777, name="Lunacid.exe", exe_path=path)

    assert detected is not None
    assert detected.platform == "Standalone"
    assert detected.platform_id == p_hash
    assert detected.name == "Lunacid"

    try:
        os.remove(db_path)
    except Exception:
        pass


def test_heybox_detector():
    with tempfile.TemporaryDirectory() as temp_dir:
        # Create mock HeyboxApps library
        common_dir = os.path.join(temp_dir, "common", "1200000067 伊莫")
        os.makedirs(common_dir, exist_ok=True)
        appstate_file = os.path.join(temp_dir, "appstate_1200000067.json")
        with open(appstate_file, "w", encoding="utf-8") as f:
            f.write('''{
                "appid": 1200000067,
                "name": "伊莫",
                "installdir": "1200000067 伊莫",
                "build": {
                    "config": {
                        "process_names": ["Aniimo.exe"]
                    }
                }
            }''')

        from tracker.detectors.heybox import HeyboxDetector
        detector = HeyboxDetector(custom_libraries=[temp_dir])
        assert "1200000067" in detector.installed_games

        mock_exe = os.path.join(common_dir, "Aniimo.exe")
        identity = detector.detect(pid=6123, name="Aniimo.exe", exe_path=mock_exe)

        assert identity is not None
        assert identity.platform == "Heybox"
        assert identity.platform_id == "1200000067"
        assert identity.name == "伊莫"
        assert identity.executable == "Aniimo.exe"


def test_xbox_detector_no_false_positives():
    from tracker.detectors.xbox import XboxDetector
    detector = XboxDetector()

    # Generic Microsoft Store app in WindowsApps should NOT be detected as Xbox game
    store_app = r"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1032.22270.0_x64__8wekyb3d8bbwe\AppleMusic.exe"
    assert detector.detect(pid=100, name="AppleMusic.exe", exe_path=store_app) is None

    terminal_app = r"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24.11911.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe"
    assert detector.detect(pid=101, name="WindowsTerminal.exe", exe_path=terminal_app) is None

    # App in XboxGames SHOULD be detected
    xbox_game = r"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe"
    identity = detector.detect(pid=200, name="ForzaHorizon5.exe", exe_path=xbox_game)
    assert identity is not None
    assert identity.platform == "Xbox"
    assert identity.name == "Forza Horizon 5"

