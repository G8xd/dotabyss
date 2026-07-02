"""Find the cache bundles that hold Live2D model animation assets.

A full-cache AssetRipper rip processes/exports ~196k assets to recover the ~132
`l2d_*` AnimatorControllers + their AnimationClips we actually use. Instead, scan
the UnityCache bundles with UnityPy, identify only the bundles that contain l2d_*
controllers (and the clips they reference), and write a manifest. `da rip` then
stages just those bundles for AssetRipper -> minutes instead of an hour.

Usage:
  python scan_l2d_bundles.py [--limit N] [--out PATH]
Writes a JSON manifest: {bundles:[{path, controllers:[...], clips:N, ...}], ...}
"""

import argparse
import glob
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import UnityPy

CACHE = os.path.join(C.GAME, "files", "UnityCache", "Shared")
DEFAULT_OUT = os.path.join(C.BUILD, "l2d_bundle_manifest.json")


def name_of(obj, cpath):
    """Cheap name: prefer container path stem, else m_Name (reads body)."""
    if cpath:
        base = os.path.basename(cpath.replace("\\", "/"))
        return os.path.splitext(base)[0]
    try:
        return getattr(obj.read(), "m_Name", "") or ""
    except Exception:
        return ""


def scan(limit=0, out=DEFAULT_OUT):
    bundles = sorted(glob.glob(os.path.join(CACHE, "*", "*", "__data")))
    if limit:
        bundles = bundles[:limit]
    print(f"scanning {len(bundles)} cache bundles for l2d_* controllers", flush=True)

    selected = []
    n_ctrl_total = 0
    t0 = time.time()
    for i, p in enumerate(bundles):
        try:
            env = UnityPy.load(p)
        except Exception:
            continue
        # path_id -> container path (cheap; no body read)
        cmap = {}
        try:
            for cpath, obj in env.container.items():
                cmap[obj.path_id] = cpath
        except Exception:
            pass

        ctrls, clips, has_l2d_other = [], 0, False
        for obj in env.objects:
            t = obj.type.name
            if t == "AnimatorController":
                nm = name_of(obj, cmap.get(obj.path_id))
                if nm.startswith("l2d_"):
                    ctrls.append(nm)
            elif t == "AnimationClip":
                clips += 1
            elif t in ("GameObject", "MonoBehaviour"):
                nm = cmap.get(obj.path_id, "")
                if "l2d_" in (nm or ""):
                    has_l2d_other = True

        if ctrls:
            n_ctrl_total += len(ctrls)
            selected.append(
                {
                    "path": p.replace("\\", "/"),
                    "controllers": sorted(set(ctrls)),
                    "clips": clips,
                    "has_l2d_other": has_l2d_other,
                }
            )

        if (i + 1) % 500 == 0:
            el = time.time() - t0
            print(
                f"  {i + 1}/{len(bundles)} | bundles_with_l2d_ctrl={len(selected)} "
                f"ctrls={n_ctrl_total} | {el:.0f}s",
                flush=True,
            )

    os.makedirs(os.path.dirname(out), exist_ok=True)
    manifest = {
        "cache": CACHE.replace("\\", "/"),
        "scanned": len(bundles),
        "bundles": selected,
        "controller_count": n_ctrl_total,
        "elapsed_s": round(time.time() - t0, 1),
    }
    json.dump(manifest, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(
        f"\nDONE in {manifest['elapsed_s']}s | bundles_with_l2d_ctrl={len(selected)} "
        f"| total l2d controllers={n_ctrl_total}"
    )
    print(f"manifest -> {out}")
    # quick co-location signal: how many selected bundles also carry clips?
    with_clips = sum(1 for b in selected if b["clips"] > 0)
    print(f"selected bundles that ALSO contain AnimationClips: {with_clips}/{len(selected)}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--out", default=DEFAULT_OUT)
    a = ap.parse_args()
    scan(a.limit, a.out)
