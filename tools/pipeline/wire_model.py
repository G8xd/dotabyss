import glob
import os
import re
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

RIP = C.RIP_ASSETS
MODEL = "l2d_10070100032"
CTRL_SRC = os.path.join(RIP, "AnimatorController", MODEL + ".controller")
ANIM_DIR = os.path.join(RIP, "AnimationClip")
DST = os.path.join(C.MODELS_DST, MODEL)
CLIPS = os.path.join(DST, "clips")

REMAP = {
    "a736c2c0c467da53963d73c4ebdc9d73": "dafbb7ec1700e8147a48dbed2e4e543c",  # CubismParameter
    "7d5d8765f4236c8904045d99b3c30b57": "f718ba9eaf9cd9a48923922e5df94070",  # CubismRenderController
}

os.makedirs(CLIPS, exist_ok=True)

# 1) clip guids referenced by the controller
ctrl = open(CTRL_SRC, encoding="utf-8", errors="replace").read()
want = set(re.findall(r"m_Motion: \{fileID: \d+, guid: ([0-9a-f]{32})", ctrl))
print("controller references", len(want), "clips")

# 2) map guid -> .anim by scanning metas
guid2anim = {}
for meta in glob.glob(os.path.join(ANIM_DIR, "*.anim.meta")):
    t = open(meta, encoding="utf-8", errors="replace").read()
    m = re.search(r"guid: ([0-9a-f]{32})", t)
    if m and m.group(1) in want:
        guid2anim[m.group(1)] = meta[:-5]  # strip .meta

print("matched", len(guid2anim), "of", len(want))
missing = want - set(guid2anim)
if missing:
    print("MISSING", len(missing), list(missing)[:3])


def remap_text(s):
    for a, b in REMAP.items():
        s = s.replace(a, b)
    return s


# 3) copy + remap clips
copied = 0
for _guid, anim in guid2anim.items():
    base = os.path.basename(anim)
    data = open(anim, encoding="utf-8", errors="replace").read()
    open(os.path.join(CLIPS, base), "w", encoding="utf-8").write(remap_text(data))
    shutil.copy2(anim + ".meta", os.path.join(CLIPS, base + ".meta"))
    copied += 1
print("copied+remapped", copied, "clips")

# 4) copy controller + meta
shutil.copy2(CTRL_SRC, os.path.join(DST, MODEL + ".controller"))
shutil.copy2(CTRL_SRC + ".meta", os.path.join(DST, MODEL + ".controller.meta"))
print("controller copied")
