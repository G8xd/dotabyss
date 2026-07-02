"""Headless, targeted AssetRipper rip — no browser, no GUI, no full-cache hour.

AssetRipper.GUI.Free is just a localhost HTTP server; `--headless` stops it
opening a browser (and the "directory not empty" override alert that came with
it). We rip ONLY the ~148 cache bundles that hold l2d_* AnimatorControllers (the
controllers + their clips are co-located per model), staged as directory
junctions, so processing+export drop from ~196k assets/~1h to ~9.5k/~5min.

Flow: scan_l2d_bundles -> manifest -> junction-stage -> launch --headless ->
POST /LoadFolder -> POST /Export/UnityProject -> kill server -> remove stage.

Usage: python rip_headless.py [--keep-stage]
"""

import glob
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import time
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

PIPE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(C.REPO, "tools", "bin", "AssetRipper", "AssetRipper.GUI.Free.exe")
STAGE = os.path.join(C.BUILD, "l2d_stage")
MANIFEST = os.path.join(C.BUILD, "l2d_bundle_manifest.json")


def _run_scan():
    print("[rip] scanning cache for l2d_* model bundles ...", flush=True)
    r = subprocess.run(
        [sys.executable, os.path.join(PIPE, "scan_l2d_bundles.py"), "--out", MANIFEST]
    )
    if r.returncode != 0 or not os.path.isfile(MANIFEST):
        sys.exit("[rip] bundle scan failed")
    return json.load(open(MANIFEST, encoding="utf-8"))


def _is_junction(p):
    try:
        return bool(os.lstat(p).st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT)
    except (OSError, AttributeError):
        return False


def _teardown_stage():
    """Remove the stage WITHOUT following junctions (shutil.rmtree would recurse into a
    junction and delete the REAL base/assets, base/lib, and cache bundles)."""
    if not os.path.isdir(STAGE):
        return
    # leaf model-bundle junctions, then the assemblies junctions: os.rmdir removes the link only
    for p in glob.glob(os.path.join(STAGE, "files", "UnityCache", "Shared", "*", "*")) + [
        os.path.join(STAGE, "assets"),
        os.path.join(STAGE, "lib"),
    ]:
        if _is_junction(p):
            try:
                os.rmdir(p)
            except OSError:
                pass
    # only real (empty) skeleton dirs remain now -> safe to recurse
    shutil.rmtree(STAGE, ignore_errors=True)


def _junction(link, target):
    # cmd mklink needs native (backslash) paths; manifest paths use forward slashes
    link = os.path.normpath(link)
    target = os.path.normpath(target)
    os.makedirs(os.path.dirname(link), exist_ok=True)
    return (
        subprocess.run(
            ["cmd", "/c", "mklink", "/J", link, target],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        ).returncode
        == 0
    )


def _stage(manifest):
    # Bundles-ONLY stage (flat junctions). We intentionally do NOT include the game's
    # assets/ here: when AssetRipper sees an "Android game structure" it switches to
    # game-mode and ignores the loose UnityCache __data bundles entirely (verified). So
    # AssetRipper can't resolve the Cubism MonoBehaviour types and emits a placeholder
    # type guid in every clip binding — build_models.py fixes that guid afterwards
    # (the binding's path + attribute-CRC already identify CubismParameter.Value).
    _teardown_stage()
    os.makedirs(STAGE, exist_ok=True)
    n = 0
    for b in manifest["bundles"]:
        inner = os.path.dirname(b["path"])  # .../<h1>/<h2>
        h2 = os.path.basename(inner)
        h1 = os.path.basename(os.path.dirname(inner))
        if _junction(os.path.join(STAGE, f"{h1[:8]}_{h2[:8]}"), inner):
            n += 1
    print(f"[rip] staged {n}/{len(manifest['bundles'])} model bundles as junctions")
    if n == 0:
        sys.exit("[rip] staging produced 0 model-bundle junctions")
    return STAGE


def _launch(logpath):
    if os.path.exists(logpath):
        try:
            os.remove(logpath)
        except OSError:
            pass
    log = open(logpath, "w", encoding="utf-8", errors="replace")
    proc = subprocess.Popen(
        [EXE, "--headless"], stdout=log, stderr=subprocess.STDOUT, cwd=os.path.dirname(EXE)
    )
    # parse the dynamic port from the log
    port = None
    deadline = time.time() + 60
    while time.time() < deadline:
        if os.path.isfile(logpath):
            txt = open(logpath, encoding="utf-8", errors="replace").read()
            m = re.search(r"Now listening on: http://127\.0\.0\.1:(\d+)", txt)
            if m:
                port = int(m.group(1))
                break
        if proc.poll() is not None:
            sys.exit("[rip] AssetRipper exited before listening; see " + logpath)
        time.sleep(0.5)
    if not port:
        proc.kill()
        sys.exit("[rip] AssetRipper never reported a port")
    print(f"[rip] AssetRipper headless on 127.0.0.1:{port}")
    return proc, port


def _post(port, route, path, timeout):
    data = urllib.parse.urlencode({"Path": path}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}{route}", data=data, method="POST")
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            code = r.status
    except urllib.error.HTTPError as e:
        code = e.code  # 302 etc. count as success — the action ran server-side
    print(f"[rip] POST {route} -> {code} in {time.time() - t0:.0f}s")
    return code


def main():
    keep = "--keep-stage" in sys.argv
    if not os.path.isfile(EXE):
        sys.exit(f"[rip] AssetRipper not found: {EXE}")
    manifest = _run_scan()
    stage = _stage(manifest)
    log = os.path.join(C.BUILD, "ar_headless.log")
    proc, port = _launch(log)
    try:
        _post(port, "/LoadFolder", stage, timeout=900)
        os.makedirs(C.RIPPED, exist_ok=True)
        _post(port, "/Export/UnityProject", C.RIPPED, timeout=1800)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except Exception:
            proc.kill()
        print("[rip] AssetRipper stopped")
    if not keep:
        _teardown_stage()
    # sanity
    ctrl_dir = os.path.join(C.RIP_ASSETS, "AnimatorController")
    n = (
        len([f for f in os.listdir(ctrl_dir) if f.startswith("l2d_") and f.endswith(".controller")])
        if os.path.isdir(ctrl_dir)
        else 0
    )
    print(f"[rip] DONE — l2d_* controllers in data/ripped: {n}")
    if n == 0:
        sys.exit("[rip] no l2d_ controllers exported; rip likely failed")


if __name__ == "__main__":
    main()
