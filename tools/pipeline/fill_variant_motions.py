"""Copy each card's richest motion set into its motion-less pose variants,
and update their model3.json so the motions are registered."""

import glob
import json
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

ROOT = C.LIVE2D
os.chdir(ROOT)
APPLY = "--apply" in sys.argv


def nm(d):
    return os.path.basename(os.path.normpath(d))


def mcount(name):
    return len(glob.glob(os.path.join(name, "motions", "*.motion3.json")))


def cardkey(name):
    d = name.replace("l2d_", "")
    return d[:9] if (len(d) == 11 and d.isdigit()) else None


groups = {}
for d in sorted(glob.glob("*/")):
    name = nm(d)
    k = cardkey(name)
    if k:
        groups.setdefault(k, []).append(name)

plan = []
for k, members in sorted(groups.items()):
    counts = {m: mcount(m) for m in members}
    src = max(members, key=lambda m: counts[m])
    targets = [m for m in members if counts[m] == 0]
    if counts[src] > 0 and targets:
        plan.append((k, src, counts[src], targets))

print("=== plan (card: source -> targets) ===")
tot = 0
for k, src, sc, targets in plan:
    print(f"  {k}: {src} ({sc} mo) -> {', '.join(targets)}")
    tot += len(targets)
print(f"\nGroups: {len(plan)} | variants to fill: {tot}")

if not APPLY:
    print("\n(dry-run; pass --apply to execute)")
    sys.exit(0)

copied_files = 0
fixed = 0
for _k, src, _sc, targets in plan:
    src_motions = glob.glob(os.path.join(src, "motions", "*.motion3.json"))
    src_m3 = json.load(open(glob.glob(os.path.join(src, "*.model3.json"))[0], encoding="utf-8"))
    src_motion_block = src_m3.get("FileReferences", {}).get("Motions", {})
    for t in targets:
        os.makedirs(os.path.join(t, "motions"), exist_ok=True)
        for f in src_motions:
            shutil.copy2(f, os.path.join(t, "motions", os.path.basename(f)))
            copied_files += 1
        # update target model3.json (backup first), replace only the Motions block
        tpath = glob.glob(os.path.join(t, "*.model3.json"))[0]
        if not os.path.exists(tpath + ".bak"):
            shutil.copy2(tpath, tpath + ".bak")
        tj = json.load(open(tpath, encoding="utf-8"))
        tj.setdefault("FileReferences", {})["Motions"] = json.loads(json.dumps(src_motion_block))
        json.dump(tj, open(tpath, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        fixed += 1

print(f"\nDONE: filled {fixed} variant models with {copied_files} motion files copied.")
