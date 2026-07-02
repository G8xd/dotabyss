"""Single source of truth for every path the pipeline and CLI use.

Reads <repo>/config.json (and an optional <repo>/config.local.json override) and
exposes absolute paths derived from the repo root, so no script ever hardcodes an
absolute Windows path again. Import this from any tools/pipeline/* script:

    import daconfig as C
    src = C.GAME            # unpacked-APK folder (inputs: files/, assets/)
    out = C.EXTRACTED       # derived data root

When the game updates, only `config.json` (or config.local.json) needs touching.
"""

import json
import os
import sys

# tools/daconfig.py  ->  repo root is the parent of tools/
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)


def _load_config():
    cfg = {}
    main = os.path.join(REPO, "config.json")
    if os.path.isfile(main):
        with open(main, encoding="utf-8") as f:
            cfg.update({k: v for k, v in json.load(f).items() if not k.startswith("_")})
    local = os.path.join(REPO, "config.local.json")
    if os.path.isfile(local):
        with open(local, encoding="utf-8") as f:
            cfg.update({k: v for k, v in json.load(f).items() if not k.startswith("_")})
    return cfg


CONFIG = _load_config()


def _abs(value, default):
    """Resolve a config value (relative to REPO) to an absolute path."""
    v = value if value else default
    return v if os.path.isabs(v) else os.path.normpath(os.path.join(REPO, v))


# ---- top-level roots (config-driven, relative to repo) ----
GAME = _abs(CONFIG.get("game"), "data/game")
EXTRACTED = _abs(CONFIG.get("extracted"), "data/extracted")
RIPPED = _abs(CONFIG.get("ripped"), "data/ripped")
BUNDLES = _abs(CONFIG.get("bundles"), "data/bundles")
BUNDLES_WIN = _abs(CONFIG.get("bundlesWin"), "data/bundles_win")
DUMP = _abs(CONFIG.get("dump"), "data/dump")
UNITY = _abs(CONFIG.get("unity"), "unity")
BUILD = _abs(CONFIG.get("build"), "build")
UNITY_EXE = (
    CONFIG.get("unityExe") or r"C:/Program Files/Unity/Hub/Editor/6000.3.8f1/Editor/Unity.exe"
)

# ---- derived locations under EXTRACTED ----
AUDIO_DIR = os.path.join(EXTRACTED, "audio")
TEXT_DIR = os.path.join(EXTRACTED, "text")
IMAGES = os.path.join(EXTRACTED, "images")
VIDEOS = os.path.join(EXTRACTED, "videos")
VIEWER = os.path.join(EXTRACTED, "_viewer")
LIVE2D = os.path.join(EXTRACTED, "live2d", "Live2DOutput")
CUE_INDEX = os.path.join(AUDIO_DIR, "cue_index.json")
SCENES = os.path.join(VIEWER, "scenes_v2.json")

# ---- ripped assets (AssetRipper export) ----
RIP_ASSETS = os.path.join(RIPPED, "ExportedProject", "Assets")

# ---- Unity project locations ----
MODELS_DST = os.path.join(UNITY, "Assets", "Models")
SDK = os.path.join(UNITY, "Assets", "Live2D", "Cubism")

# ---- audio encode settings ----
_AUDIO = CONFIG.get("audio", {})
VOICE_QUALITY = int(_AUDIO.get("voiceQuality", 3))
VOICE_MONO = bool(_AUDIO.get("voiceMono", True))
BGM_SE_QUALITY = int(_AUDIO.get("bgmSeQuality", 5))


def category(name):
    """ACB/cue name -> audio sub-bucket (shared by extract/rename/encode)."""
    if name.startswith("bgm"):
        return "bgm"
    if name.startswith("se"):
        return "se"
    if name.startswith(("cha", "bgv")):
        return "voice_character"
    if name.startswith(("hmr", "hmn")):
        return "voice_hscene"
    return "voice_other"


def is_voice(category_name):
    return category_name.startswith("voice")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print("REPO       =", REPO)
    for k in (
        "GAME",
        "EXTRACTED",
        "AUDIO_DIR",
        "TEXT_DIR",
        "IMAGES",
        "VIDEOS",
        "VIEWER",
        "LIVE2D",
        "CUE_INDEX",
        "SCENES",
        "RIPPED",
        "RIP_ASSETS",
        "BUNDLES",
        "BUNDLES_WIN",
        "DUMP",
        "UNITY",
        "BUILD",
        "MODELS_DST",
        "SDK",
        "UNITY_EXE",
    ):
        v = globals()[k]
        exists = "OK " if os.path.exists(v) else "-- "
        print(f"  {exists}{k:12} = {v}")
    print(f"audio: voice q{VOICE_QUALITY} mono={VOICE_MONO} | bgm/se q{BGM_SE_QUALITY}")
