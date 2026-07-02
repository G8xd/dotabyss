#!/usr/bin/env python3
"""Download the external datamining tools this project uses into tools/bin/.

These tools are NOT redistributed in this repo (see THIRD_PARTY.md); this helper just
fetches them from their official releases so the `da` pipeline has what it needs. It uses
only the Python standard library, so you can run it before `uv sync`:

    python tools/fetch_tools.py            # download everything missing
    python tools/fetch_tools.py --list     # show tools, versions, and status
    python tools/fetch_tools.py --force     # re-download even if present

The **Live2D Cubism SDK is intentionally not downloaded here** — you must accept Live2D's
EULA and download it yourself from https://www.live2d.com/en/sdk/download/unity/, then unzip
it into unity/Assets/Live2D/Cubism/. See the README.

NOTE: pinned versions/URLs below are what this project was last verified against. Bump them
together with the tables in README.md and THIRD_PARTY.md. If a download 404s, the release
asset was renamed for a new version — update PINS.
"""

from __future__ import annotations

import argparse
import io
import os
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "bin")

# Each tool: the pinned version, a marker path that must exist once installed (relative to
# tools/bin/), the download URL (a .zip), and the homepage for manual fallback.
PINS = {
    "AssetRipper": {
        # Verified against the GUI Free build 1.3.14 (compiled 2026-04-25). Confirm the
        # Windows asset name on the release page if the download 404s.
        "version": "1.3.14",
        "marker": os.path.join("AssetRipper", "AssetRipper.GUI.Free.exe"),
        "url": "https://github.com/AssetRipper/AssetRipper/releases/download/1.3.14/AssetRipper_win_x64.zip",
        "extract_to": "AssetRipper",
        "home": "https://github.com/AssetRipper/AssetRipper/releases/tag/1.3.14",
    },
    "Cpp2IL": {
        # Verified against 2022.1.0-pre-release.21. This release ships a bare Cpp2IL.exe
        # (not a zip), so download it manually into tools/bin/ from the release page.
        "version": "2022.1.0-pre-release.21",
        "marker": "Cpp2IL.exe",
        "url": "",  # bare .exe asset — fetched manually (see home)
        "extract_to": ".",
        "home": "https://github.com/SamboyCoding/Cpp2IL/releases/tag/2022.1.0-pre-release.21",
    },
    "vgmstream": {
        # Verified against r2117 (2026-05-19). `latest` tracks the newest numbered/nightly
        # win64 build; pin a specific tag here if you need byte-for-byte reproducibility.
        "version": "r2117",
        "marker": os.path.join("vgmstream", "vgmstream-cli.exe"),
        "url": "https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win64.zip",
        "extract_to": "vgmstream",
        "home": "https://github.com/vgmstream/vgmstream/releases",
    },
    # Optional: AssetStudioMod is a legacy/alternative export path (the pipeline now uses
    # `da live2d` + `da rip`). Requires the .NET 8 runtime. Enable by moving into use if needed.
    "AssetStudioMod": {
        "version": "0.19.0",
        "marker": os.path.join("cli", "AssetStudioModCLI_net8_portable", "AssetStudioModCLI.dll"),
        "url": "https://github.com/aelurum/AssetStudio/releases/download/v0.19.0/AssetStudioModCLI_net8_portable.zip",
        "extract_to": "cli",
        "home": "https://github.com/aelurum/AssetStudio/releases/tag/v0.19.0",
    },
}


def installed(tool: dict) -> bool:
    return os.path.exists(os.path.join(BIN, tool["marker"]))


def fetch(name: str, tool: dict, force: bool) -> bool:
    dest_marker = os.path.join(BIN, tool["marker"])
    if installed(tool) and not force:
        print(f"  [skip] {name} already present ({dest_marker})")
        return True
    url = tool["url"]
    if not url:
        print(
            f"  [manual] {name}: no pinned URL set. Download from {tool['home']}\n"
            f"           and place it so that '{tool['marker']}' exists under tools/bin/."
        )
        return False
    out_dir = os.path.join(BIN, tool["extract_to"])
    os.makedirs(out_dir, exist_ok=True)
    print(f"  [get ] {name} {tool['version']} <- {url}")
    try:
        with urllib.request.urlopen(url) as r:  # noqa: S310 (trusted release hosts)
            data = r.read()
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            z.extractall(out_dir)
    except Exception as e:
        print(
            f"  [FAIL] {name}: {e}\n"
            f"         Download manually from {tool['home']} and unzip so that\n"
            f"         '{tool['marker']}' exists under tools/bin/."
        )
        return False
    ok = installed(tool)
    print(
        f"  [{'ok  ' if ok else 'WARN'}] {name} -> {out_dir}"
        + ("" if ok else f"  (expected '{tool['marker']}' not found; check the archive layout)")
    )
    return ok


def main() -> int:
    ap = argparse.ArgumentParser(description="Fetch external tools into tools/bin/")
    ap.add_argument("--list", action="store_true", help="list tools and their status")
    ap.add_argument("--force", action="store_true", help="re-download even if present")
    a = ap.parse_args()

    os.makedirs(BIN, exist_ok=True)
    if a.list:
        for name, tool in PINS.items():
            status = "present" if installed(tool) else "missing"
            print(f"  {name:12} {tool['version']:14} [{status}]  {tool['home']}")
        return 0

    print(f"Fetching tools into {BIN}")
    results = {name: fetch(name, tool, a.force) for name, tool in PINS.items()}
    print(
        "\nReminder: install the Live2D Cubism SDK for Unity yourself into "
        "unity/Assets/Live2D/Cubism/\n(https://www.live2d.com/en/sdk/download/unity/ — EULA "
        "required; not auto-downloaded)."
    )
    missing = [n for n, ok in results.items() if not ok]
    if missing:
        print(f"\nNot installed automatically: {', '.join(missing)} — see the notes above.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
