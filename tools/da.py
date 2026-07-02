#!/usr/bin/env python3
"""da - the Dot Abyss data/build CLI.

One command set to re-extract and rebuild everything when the game updates. All
paths come from config.json via daconfig.py, so this works on any machine after a
checkout (point config.local.json at your data/game if it lives elsewhere).

  da paths                 print resolved paths (daconfig self-test)
  da extract               assets: images + text + live2d (UnityPy) + organize images
  da audio                 ACB/AWB -> OGG -> cue_index.json
  da encode [args...]      re-encode leftover WAV -> OGG (passes args to encode_audio.py)
  da scenes                scenario scripts -> _viewer/scenes_v2.json
  da images                re-bucket images/_misc by size
  da rip                   headless+targeted AssetRipper: only the l2d_* model
                             bundles (controllers/clips) -> data/ripped (~5 min)
  da live2d                rebuild Live2DOutput moc3+textures+model3 from the cache
                             (replaces AssetStudio; default = only new models)
  da models                build_models -> unity/Assets/Models
  da unity <step> [target] run a Unity Editor method in batchmode:
                             setup | windows | android | bundles | validate
                             or a fully-qualified Class.Method
  da dump                  Cpp2IL -> data/dump (only when the game CODE changes)
  da update [--game DIR] [--no-rip]
                           extract -> audio -> scenes -> rip -> live2d -> models
                             -> unity setup  (dump stays manual; see notes)

Run `da <command> -h` is not supported per-subcommand; see this header.
"""

import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import daconfig as C

PIPE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pipeline")
PY = sys.executable


def _hdr(msg):
    print("\n" + "=" * 70 + f"\n== {msg}\n" + "=" * 70, flush=True)


def run_py(script, *args):
    """Run a pipeline script with the current interpreter; raise on failure."""
    path = os.path.join(PIPE, script)
    if not os.path.isfile(path):
        sys.exit(f"[da] missing pipeline script: {path}")
    r = subprocess.run([PY, path, *args])
    if r.returncode != 0:
        sys.exit(f"[da] {script} failed (exit {r.returncode})")


# ---- Unity batchmode ----------------------------------------------------------

UNITY_ALIASES = {
    "setup": "DA_Build.FullSetup",
    "windows": "DA_Build.BuildWindows",
    "win": "DA_Build.BuildWindows",
    "android": "DA_Build.BuildAndroid",
    "apk": "DA_Build.BuildAndroid",
    "bundles": "DA_Build.WinBundleTest",
    "validate": "DA_Setup.ValidateAll",
    "urp": "DA_Setup.SetupURP",
    "wire": "DA_Setup.WireAll",
}


def run_unity(method):
    if not os.path.isfile(C.UNITY_EXE):
        sys.exit(f"[da] Unity not found at {C.UNITY_EXE} (set 'unityExe' in config.json)")
    os.makedirs(C.BUILD, exist_ok=True)
    log = os.path.join(C.BUILD, "da_unity_" + method.replace(".", "_") + ".log")
    if os.path.exists(log):
        try:
            os.remove(log)
        except OSError:
            pass
    cmd = [
        C.UNITY_EXE,
        "-batchmode",
        "-quit",
        "-projectPath",
        C.UNITY,
        "-executeMethod",
        method,
        "-logFile",
        log,
    ]
    _hdr(f"Unity: {method}")
    print("[da] " + " ".join(cmd), flush=True)
    t0 = time.time()
    proc = subprocess.Popen(cmd)
    # Unity.exe (direct editor) runs synchronously with -batchmode -quit. Wait, then
    # confirm completion via the log in case it relaunched itself.
    rc = proc.wait()
    deadline = time.time() + 1800
    while not _unity_log_done(log) and time.time() < deadline:
        time.sleep(3)
    el = time.time() - t0
    results = _grep_log(log, "DA_RESULT")
    problems = _grep_log(log, "DA_PROBLEM")
    for r in results:
        print("  " + r)
    if problems:
        print(f"  ({len(problems)} DA_PROBLEM lines, first 5:)")
        for p in problems[:5]:
            print("   " + p)
    print(f"[da] Unity {method} exit={rc} in {el:.0f}s | log={log}")
    if rc != 0 and not results:
        sys.exit(f"[da] Unity {method} failed; see {log}")


def _unity_log_done(log):
    if not os.path.isfile(log):
        return False
    try:
        tail = open(log, encoding="utf-8", errors="replace").read()[-4000:]
    except OSError:
        return False
    return (
        "Exiting batchmode" in tail
        or "DA_RESULT" in tail
        or "Aborting batchmode" in tail
        or "Crash!!!" in tail
    )


def _grep_log(log, token):
    if not os.path.isfile(log):
        return []
    out = []
    for line in open(log, encoding="utf-8", errors="replace"):
        i = line.find(token)
        if i >= 0:
            out.append(line[i:].rstrip())
    return out


# ---- subcommands --------------------------------------------------------------


def cmd_paths(_a):
    subprocess.run([PY, os.path.join(C.REPO, "tools", "daconfig.py")])


def cmd_extract(_a):
    _hdr("extract assets (images + text + live2d)")
    run_py("extract_assets.py")
    cmd_images(_a)


def cmd_images(_a):
    _hdr("organize images")
    run_py("organize_images.py")


def cmd_audio(_a):
    _hdr("audio: ACB/AWB -> OGG")
    run_py("extract_audio.py")
    _hdr("audio: rename to cue names -> cue_index.json")
    run_py("rename_cues.py")


def cmd_encode(a):
    _hdr("encode WAV -> OGG")
    run_py("encode_audio.py", *a.rest)


def cmd_scenes(_a):
    _hdr("parse scenario scripts -> scenes_v2.json")
    run_py("parse_scenes.py")


def cmd_models(_a):
    _hdr("build models -> unity/Assets/Models")
    run_py("build_models.py")


def cmd_unity(a):
    method = UNITY_ALIASES.get(a.step, a.step)
    if "." not in method:
        sys.exit(
            f"[da] unknown unity step '{a.step}'. Use one of "
            f"{', '.join(sorted(UNITY_ALIASES))} or a Class.Method."
        )
    run_unity(method)


def cmd_rip(_a):
    # Headless + targeted: scan the cache for the ~148 l2d_* model bundles, stage
    # them as junctions, drive AssetRipper's localhost API with no browser, and
    # export only those -> ~5 min instead of an hour on the full cache.
    _hdr("AssetRipper (headless, targeted)")
    run_py("rip_headless.py")


def cmd_live2d(_a):
    # rebuild Live2DOutput/<model>/{moc3,textures,model3.json} from the cache
    # (replaces the old external AssetStudio export). Default = only new models.
    _hdr("extract Live2DOutput from cache (moc3 + textures + model3.json)")
    run_py("extract_live2d.py")


def cmd_dump(_a):
    exe = os.path.join(C.REPO, "tools", "bin", "Cpp2IL.exe")
    _hdr("Cpp2IL dump (only when the game code changes)")
    if not os.path.isfile(exe):
        sys.exit(f"[da] Cpp2IL not found: {exe}")
    so = os.path.join(C.GAME, "lib", "arm64-v8a", "libil2cpp.so")
    meta = os.path.join(
        C.GAME, "assets", "bin", "Data", "Managed", "Metadata", "global-metadata.dat"
    )
    out = os.path.join(C.DUMP, "cpp2il_out")
    if not (os.path.isfile(so) and os.path.isfile(meta)):
        print("[da] expected IL2CPP inputs not found:")
        print("   " + so + ("  OK" if os.path.isfile(so) else "  MISSING"))
        print("   " + meta + ("  OK" if os.path.isfile(meta) else "  MISSING"))
        sys.exit("[da] cannot dump; check the unpacked APK layout.")
    os.makedirs(out, exist_ok=True)
    cmd = [
        exe,
        "--game-path",
        C.GAME,
        "--force-binary-path",
        so,
        "--force-metadata-path",
        meta,
        "--output-root",
        out,
        "--output-as",
        "dummydll",
    ]
    print("[da] " + " ".join(cmd))
    r = subprocess.run(cmd)
    print(f"[da] Cpp2IL exit={r.returncode} out={out}")
    if r.returncode != 0:
        print(
            "[da] NOTE: Cpp2IL args are version-specific; adjust if it fails "
            "(see tools/bin/Cpp2IL.exe --help)."
        )


def cmd_update(a):
    if a.game:
        # let config.local.json point at a different unpacked APK without editing config.json
        local = os.path.join(C.REPO, "config.local.json")
        import json

        cfg = {}
        if os.path.isfile(local):
            cfg = json.load(open(local, encoding="utf-8"))
        cfg["game"] = a.game
        json.dump(cfg, open(local, "w", encoding="utf-8"), indent=2)
        print(f"[da] config.local.json game = {a.game} (re-run reads it)")
        sys.exit("[da] game path updated; run `da update` again to use it.")

    _hdr("UPDATE: extract -> audio -> scenes -> rip -> live2d -> models -> unity setup")
    print("[da] NOTE: `dump` (Cpp2IL) is skipped — only needed when the game CODE changes.")
    cmd_extract(a)
    cmd_audio(a)
    cmd_scenes(a)
    if a.no_rip:
        print("[da] --no-rip: skipping AssetRipper (reusing existing data/ripped)")
    else:
        cmd_rip(a)  # headless + targeted (~5 min)
    cmd_live2d(a)  # rebuild Live2DOutput for any new models (from cache)
    if not os.path.isdir(C.RIP_ASSETS):
        print(
            f"[da] WARNING: {C.RIP_ASSETS} missing — run `da rip` then `da models`. Skipping models."
        )
    else:
        cmd_models(a)
    run_unity("DA_Build.FullSetup")
    _hdr("UPDATE done")
    print("Next: `da unity android` (APK) and/or `da unity windows` (desktop) to rebuild players.")


def main():
    ap = argparse.ArgumentParser(
        prog="da",
        description="Dot Abyss data/build CLI",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    sub = ap.add_subparsers(dest="cmd")

    sub.add_parser("paths").set_defaults(func=cmd_paths)
    sub.add_parser("extract").set_defaults(func=cmd_extract)
    sub.add_parser("images").set_defaults(func=cmd_images)
    sub.add_parser("audio").set_defaults(func=cmd_audio)
    sub.add_parser("scenes").set_defaults(func=cmd_scenes)
    sub.add_parser("models").set_defaults(func=cmd_models)
    sub.add_parser("live2d").set_defaults(func=cmd_live2d)
    sub.add_parser("rip").set_defaults(func=cmd_rip)
    sub.add_parser("dump").set_defaults(func=cmd_dump)

    pe = sub.add_parser("encode")
    pe.add_argument("rest", nargs=argparse.REMAINDER)
    pe.set_defaults(func=cmd_encode)

    pu = sub.add_parser("unity")
    pu.add_argument("step")
    pu.set_defaults(func=cmd_unity)

    pup = sub.add_parser("update")
    pup.add_argument("--game", default=None, help="path to the unpacked APK folder")
    pup.add_argument("--no-rip", action="store_true", help="skip AssetRipper; reuse data/ripped")
    pup.set_defaults(func=cmd_update)

    a = ap.parse_args()
    if not getattr(a, "func", None):
        ap.print_help()
        return
    a.func(a)


if __name__ == "__main__":
    main()
