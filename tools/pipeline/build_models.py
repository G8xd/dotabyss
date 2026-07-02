"""Scale the proven single-model pipeline to all 132 Live2D models.

Per model:
  - copy moc3 + textures/ from Live2DOutput
  - copy model3.json with Motions/Expressions emptied (avoids the Cubism importer
    crash on the lossy motion3.json; we use the genuine ripped .anim clips instead)
  - copy the genuine AnimatorController from the rip (verbatim; keeps clip GUID refs)
  - copy every clip the controller references, remapping AssetRipper stub Cubism
    script GUIDs -> real SDK GUIDs in the clip body (clip .meta kept verbatim so the
    controller's m_Motion guids still resolve)
"""

import glob
import json
import os
import re
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

L2D = C.LIVE2D
RIP = C.RIP_ASSETS
RIPC = os.path.join(RIP, "AnimatorController")
RIPA = os.path.join(RIP, "AnimationClip")
SDK = C.SDK
DSTROOT = C.MODELS_DST


def guid_of(meta):
    m = re.search(r"guid: ([0-9a-f]{32})", open(meta, encoding="utf-8", errors="replace").read())
    return m.group(1) if m else None


# --- 1) full Cubism stub -> real script-GUID remap, matched by .cs filename ---
def build_remap():
    rip, sdk = {}, {}
    for meta in glob.glob(os.path.join(RIP, "Scripts", "**", "*.cs.meta"), recursive=True):
        b = os.path.basename(meta)[:-8]
        if b.startswith("Cubism"):
            rip.setdefault(b, guid_of(meta))
    for meta in glob.glob(os.path.join(SDK, "**", "*.cs.meta"), recursive=True):
        b = os.path.basename(meta)[:-8]
        if b.startswith("Cubism"):
            sdk.setdefault(b, guid_of(meta))
    remap = {rip[n]: sdk[n] for n in rip if n in sdk and rip[n] and sdk[n] and rip[n] != sdk[n]}
    return remap


REMAP = build_remap()
print("Cubism remap entries:", len(REMAP))


def remap_text(s):
    for a, b in REMAP.items():
        s = s.replace(a, b)
    return s


# --- 1b) fix unresolved Cubism animation bindings ---------------------------------
# A bundles-only rip can't resolve the Cubism MonoBehaviour types, so AssetRipper emits
# ONE placeholder type guid in every clip binding and a CRC-mangled attribute name. But
# the binding path ("Parameters/<id>") + attribute CRC 0xDCB67730 (= CRC32("Value"))
# already identify it as CubismParameter.Value, so we rewrite the placeholder to the real
# SDK type. (~99% of curves are parameters; the few EffectObject/Drawables curves become
# CubismParameter at non-parameter paths and simply bind to nothing — harmless.)
def _sdk_guid(name):
    for meta in glob.glob(os.path.join(SDK, "**", name + ".cs.meta"), recursive=True):
        return guid_of(meta)
    return None


CUBISM_PARAM_GUID = _sdk_guid("CubismParameter")
PLACEHOLDER_REF = "{fileID: 0, guid: 0000000deadbeef15deadf00d0000000, type: 2}"
PARAM_REF = f"{{fileID: 11500000, guid: {CUBISM_PARAM_GUID}, type: 3}}"
print("CubismParameter SDK guid:", CUBISM_PARAM_GUID)


def fix_cubism_bindings(s):
    if not CUBISM_PARAM_GUID:
        return s
    # runtime binding table (m_ClipBindingConstant.genericBindings)
    s = s.replace(PLACEHOLDER_REF, PARAM_REF)
    # editor float-curve bindings: name the field "Value" and point its script at the type
    s = re.sub(r"attribute: script_0xDCB67730_\w+", "attribute: Value", s)
    s = re.sub(
        r"(attribute: Value\n    path: [^\n]+\n    classID: 114\n    script: )\{fileID: 0\}",
        lambda mm: mm.group(1) + PARAM_REF,
        s,
    )
    return s


# --- 2) rip clip GUID -> .anim path index (scan once) ---
print("indexing rip clips ...")
guid2anim = {}
for meta in glob.glob(os.path.join(RIPA, "*.anim.meta")):
    g = guid_of(meta)
    if g:
        guid2anim[g] = meta[:-5]  # strip .meta
print("indexed", len(guid2anim), "clips")

# --- 3) per-model build ---
models = sorted(os.path.basename(d) for d in glob.glob(os.path.join(L2D, "*")) if os.path.isdir(d))
print("models:", len(models))

tot_clips = 0
tot_missing = 0
no_ctrl = []
summary = []
for i, m in enumerate(models, 1):
    src = os.path.join(L2D, m)
    dst = os.path.join(DSTROOT, m)
    clips_dir = os.path.join(dst, "clips")
    # fresh clips dir; keep generated assets (importer refreshes them)
    if os.path.isdir(clips_dir):
        shutil.rmtree(clips_dir)
    os.makedirs(clips_dir, exist_ok=True)

    # moc3
    shutil.copy2(os.path.join(src, m + ".moc3"), os.path.join(dst, m + ".moc3"))
    # textures
    tsrc = os.path.join(src, "textures")
    tdst = os.path.join(dst, "textures")
    if os.path.isdir(tdst):
        shutil.rmtree(tdst)
    shutil.copytree(tsrc, tdst)
    # model3.json with Motions/Expressions emptied
    d = json.load(open(os.path.join(src, m + ".model3.json"), encoding="utf-8"))
    d.setdefault("FileReferences", {})["Motions"] = {}
    d["FileReferences"]["Expressions"] = []
    json.dump(
        d,
        open(os.path.join(dst, m + ".model3.json"), "w", encoding="utf-8"),
        ensure_ascii=False,
        indent=2,
    )

    # controller (verbatim copy keeps m_Motion clip-guid references intact)
    csrc = os.path.join(RIPC, m + ".controller")
    if not os.path.exists(csrc):
        no_ctrl.append(m)
        summary.append((m, 0, 0, "NO_CONTROLLER"))
        continue
    ctrl = open(csrc, encoding="utf-8", errors="replace").read()
    shutil.copy2(csrc, os.path.join(dst, m + ".controller"))
    shutil.copy2(csrc + ".meta", os.path.join(dst, m + ".controller.meta"))

    # clips referenced by the controller
    want = set(re.findall(r"m_Motion: \{fileID: \d+, guid: ([0-9a-f]{32})", ctrl))
    got = 0
    miss = 0
    for g in want:
        anim = guid2anim.get(g)
        if not anim:
            miss += 1
            continue
        base = os.path.basename(anim)
        body = open(anim, encoding="utf-8", errors="replace").read()
        open(os.path.join(clips_dir, base), "w", encoding="utf-8").write(
            fix_cubism_bindings(remap_text(body))
        )
        shutil.copy2(anim + ".meta", os.path.join(clips_dir, base + ".meta"))
        got += 1
    tot_clips += got
    tot_missing += miss
    summary.append((m, len(want), got, "" if miss == 0 else f"MISSING={miss}"))
    if i % 20 == 0 or i == len(models):
        print(f"  [{i}/{len(models)}] {m}: refs={len(want)} copied={got} miss={miss}")

print("\n=== SUMMARY ===")
print("models processed:", len(models))
print("models without controller:", len(no_ctrl), no_ctrl[:5])
print("total clips copied:", tot_clips, "| total missing clip refs:", tot_missing)
bad = [s for s in summary if s[3]]
if bad:
    print("models with issues:", len(bad))
    for s in bad[:20]:
        print("  ", s)
else:
    print("all models: every referenced clip resolved OK")
