"""Rebuild Live2DOutput/<model>/{moc3, textures/, model3.json} from the game cache.

Replaces the old external AssetStudio "Live2D export" step. For each Cubism model
the game ships two cache bundles: a standalone CubismMoc bundle (the .moc3 binary)
and the baked-prefab bundle (textures + CubismParameter GameObjects + the
AnimatorController). This walks the UnityCache once, pairs them by model id, and
writes the exact Live2DOutput layout build_models.py consumes.

  - moc3:     raw bytes of the CubismMoc MonoBehaviour byte-array (verified
              byte-identical to the AssetStudio export).
  - textures: Texture2D -> texture_NN.png (mask/grab helpers skipped).
  - model3.json: synthesized with Moc + Textures + EyeBlink/LipSync Groups.
              Motions/Expressions are left empty on purpose (build_models empties
              them anyway; animation comes from the ripped .anim clips).

Usage:
  python extract_live2d.py                 # only models missing from Live2DOutput
  python extract_live2d.py --all           # (re)extract every model found
  python extract_live2d.py --models a,b    # specific model ids
  python extract_live2d.py --rescan        # ignore cached map, rescan the cache
"""

import argparse
import glob
import json
import os
import re
import struct
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import UnityPy

CACHE = os.path.join(C.GAME, "files", "UnityCache", "Shared")
MAP = os.path.join(C.BUILD, "live2d_map.json")
EYE_IDS = ("ParamEyeLOpen", "ParamEyeROpen")
MOUTH_IDS = ("ParamMouthOpenY",)


def _moc_name(env):
    """Model id of a standalone CubismMoc bundle (read only the MonoBehaviour name)."""
    for o in env.objects:
        if o.type.name == "MonoBehaviour":
            try:
                nm = o.read_typetree().get("m_Name", "")
            except Exception:
                nm = ""
            if nm.startswith("l2d_"):
                return nm
    return None


def scan_cache():
    """Build {model: {"moc": path, "prefab": path}} the SAFE way.

    NB: a single UnityPy pass that reads every object over all ~15.8k bundles
    segfaults natively. So we build the map from two crash-free passes instead:
      - prefab bundles (model -> path) from scan_l2d_bundles.py (container/type only)
      - moc bundles via a MOC3-magic scan over get_raw_data (no full object reads),
        then read just the CubismMoc name from those ~144 bundles.
    """
    manifest_path = os.path.join(C.BUILD, "l2d_bundle_manifest.json")
    if not os.path.isfile(manifest_path):
        print("scan_l2d_bundles (prefab/controller bundles) ...", flush=True)
        subprocess.run(
            [
                sys.executable,
                os.path.join(os.path.dirname(__file__), "scan_l2d_bundles.py"),
                "--out",
                manifest_path,
            ],
            check=True,
        )
    man = json.load(open(manifest_path, encoding="utf-8"))
    prefab = {c: b["path"] for b in man["bundles"] for c in b["controllers"]}

    bundles = sorted(glob.glob(os.path.join(CACHE, "*", "*", "__data")))
    print(f"scanning {len(bundles)} bundles for MOC3 (get_raw_data only)", flush=True)
    moc = {}
    t0 = time.time()
    for i, p in enumerate(bundles):
        try:
            env = UnityPy.load(p)
        except Exception:
            continue
        if not any(
            (o.type.name == "MonoBehaviour" and (lambda r: r and b"MOC3" in r)(_safe_raw(o)))
            for o in env.objects
        ):
            continue
        nm = _moc_name(env)
        if nm:
            moc[nm] = p.replace("\\", "/")
        if (i + 1) % 2000 == 0:
            print(
                f"  {i + 1}/{len(bundles)} | moc={len(moc)} | {time.time() - t0:.0f}s", flush=True
            )

    mp = {}
    for m in set(prefab) | set(moc):
        rec = {}
        if m in moc:
            rec["moc"] = moc[m]
        if m in prefab:
            rec["prefab"] = prefab[m]
        mp[m] = rec
    paired = {k: v for k, v in mp.items() if "moc" in v and "prefab" in v}
    print(f"map built in {time.time() - t0:.0f}s | paired(moc+prefab)={len(paired)}/{len(mp)}")
    os.makedirs(C.BUILD, exist_ok=True)
    json.dump(mp, open(MAP, "w", encoding="utf-8"), indent=2)
    return mp


def _safe_raw(o):
    try:
        return o.get_raw_data()
    except Exception:
        return b""


def extract_moc(env):
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        try:
            raw = o.get_raw_data()
        except Exception:
            continue
        i = raw.find(b"MOC3")
        if i < 4:
            continue
        length = struct.unpack_from("<i", raw, i - 4)[0]
        if 0 < length <= len(raw) - i:
            return raw[i : i + length]
        return raw[i:]
    return None


ATLAS_RE = re.compile(r"^texture_(\d+)$")


def extract_textures(env, tdir):
    texs = []
    for o in env.objects:
        if o.type.name != "Texture2D":
            continue
        try:
            d = o.read()
        except Exception:
            continue
        nm = getattr(d, "m_Name", "") or ""
        low = nm.lower()
        if "mask" in low or "grab" in low:
            continue
        texs.append((nm, d))

    # Save every texture to disk (lossless; the effect maps are kept for reference)...
    os.makedirs(tdir, exist_ok=True)
    for nm, d in texs:
        d.image.save(os.path.join(tdir, nm + ".png"), format="PNG")

    # ...but the model3.json Textures list must contain ONLY the Cubism atlas textures
    # (texture_NN), in NUMERIC index order. The moc3 references textures by INDEX, so
    # including the effect maps (Mist/Voronoi/Aura/Sphere/Emission/Light/Bg/...) or sorting
    # alphabetically scrambles index 0/1 and the whole model samples the wrong map -> the
    # grey/white "broken face" render. Effect maps are material-only (custom shaders we
    # don't reconstruct) and never carry a moc3 texture index.
    atlases = sorted(
        (nm for nm, _ in texs if ATLAS_RE.match(nm)),
        key=lambda nm: int(ATLAS_RE.match(nm).group(1)),
    )
    if not atlases:  # no texture_NN found: fall back to all (alphabetical) rather than emit none
        atlases = sorted(nm for nm, _ in texs)
    return ["textures/" + nm + ".png" for nm in atlases]


def param_ids(env):
    ids = set()
    for o in env.objects:
        if o.type.name == "GameObject":
            try:
                nm = getattr(o.read(), "m_Name", "")
            except Exception:
                nm = ""
            if nm.startswith("Param"):
                ids.add(nm)
    return ids


def build_model3(model, textures, ids):
    eye = [p for p in EYE_IDS if p in ids]
    mouth = [p for p in MOUTH_IDS if p in ids]
    groups = []
    if eye:
        groups.append({"Target": "Parameter", "Name": "EyeBlink", "Ids": eye})
    if mouth:
        groups.append({"Target": "Parameter", "Name": "LipSync", "Ids": mouth})
    return {
        "Version": 3,
        "Name": model,
        "FileReferences": {
            "Moc": model + ".moc3",
            "Textures": textures,
            "Physics": None,
            "Pose": None,
            "DisplayInfo": None,
            "Motions": {},
            "Expressions": [],
        },
        "Groups": groups,
    }


def extract_one(model, rec):
    dst = os.path.join(C.LIVE2D, model)
    os.makedirs(dst, exist_ok=True)
    # moc3
    menv = UnityPy.load(rec["moc"])
    moc = extract_moc(menv)
    if not moc:
        return False, "no moc3 bytes"
    open(os.path.join(dst, model + ".moc3"), "wb").write(moc)
    # textures + groups from prefab bundle
    penv = UnityPy.load(rec["prefab"])
    textures = extract_textures(penv, os.path.join(dst, "textures"))
    ids = param_ids(penv)
    m3 = build_model3(model, textures, ids)
    json.dump(
        m3,
        open(os.path.join(dst, model + ".model3.json"), "w", encoding="utf-8"),
        ensure_ascii=False,
        indent=2,
    )
    return True, f"moc={len(moc)}B tex={len(textures)} eye/mouth={len(m3['Groups'])}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--all", action="store_true", help="(re)extract every paired model")
    ap.add_argument("--models", default="", help="comma-separated model ids")
    ap.add_argument("--rescan", action="store_true", help="ignore cached map; rescan cache")
    a = ap.parse_args()

    mp = None
    if not a.rescan and os.path.isfile(MAP):
        mp = json.load(open(MAP, encoding="utf-8"))
        print(f"using cached map {MAP} ({len(mp)} models) — pass --rescan to refresh")
    if mp is None:
        mp = scan_cache()
    paired = {k: v for k, v in mp.items() if "moc" in v and "prefab" in v}

    if a.models:
        want = [m.strip() for m in a.models.split(",") if m.strip()]
    elif a.all:
        want = sorted(paired)
    else:
        have = {
            os.path.basename(d.rstrip("/\\"))
            for d in glob.glob(os.path.join(C.LIVE2D, "*"))
            if os.path.isdir(d)
        }
        want = sorted(m for m in paired if m not in have)
        print(f"missing-from-Live2DOutput models to extract: {len(want)}")

    ok = 0
    for m in want:
        if m not in paired:
            print(f"  SKIP {m}: not paired in cache (moc or prefab missing)")
            continue
        good, msg = extract_one(m, paired[m])
        print(f"  {'OK ' if good else 'FAIL'} {m}: {msg}")
        ok += good
    print(f"\nDONE: extracted {ok}/{len(want)} models -> {C.LIVE2D}")


if __name__ == "__main__":
    main()
