import glob
import hashlib
import json
import os
import re
import struct
import sys

import UnityPy

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from acb_lib import cue_names

import daconfig as C

os.chdir(C.GAME)
OUT = C.AUDIO_DIR


def acb_top_name(acb):
    try:
        so = struct.unpack_from(">I", acb, 12)[0]
        do = struct.unpack_from(">I", acb, 16)[0]
        for t in acb[8 + so : 8 + do].split(b"\x00"):
            s = t.decode("utf-8", "replace")
            if re.match(r"^[a-z]{2,6}_?\d{4,}$", s):
                return s
    except Exception:
        pass
    return None


category = C.category

bundles = [f for f in glob.glob("assets/bin/Data/*") if os.path.isfile(f)]
local = "assets/aa/Android/defaultlocalgroup_assets_all.bundle"
if os.path.isfile(local):
    bundles.append(local)
bundles += sorted(glob.glob("files/UnityCache/Shared/*/*/__data"))

seen = set()
namecount = {}
index = {}
renamed = 0
banks = 0
for p in bundles:
    try:
        env = UnityPy.load(p)
    except Exception:
        continue
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            raw = obj.get_raw_data()
        except Exception:
            continue
        i = raw.find(b"@UTF")
        if i < 0:
            continue
        length = struct.unpack_from("<I", raw, i - 4)[0] if i >= 4 else 0
        acb = raw[i : i + length] if 0 < length <= len(raw) - i else raw[i:]
        h = hashlib.md5(acb).digest()
        if h in seen:
            continue
        seen.add(h)
        nm = acb_top_name(acb) or ("acb_" + hashlib.md5(acb).hexdigest()[:8])
        if nm in namecount:
            namecount[nm] += 1
            nm = f"{nm}__{namecount[nm]}"
        else:
            namecount[nm] = 0
        cat = category(nm)
        folder = os.path.join(OUT, cat, nm)
        if not os.path.isdir(folder):
            continue
        try:
            cn = cue_names(acb)
        except Exception:
            cn = {}
        banks += 1
        for idx, cue in cn.items():
            safe = "".join(c for c in cue if c.isalnum() or c in "_-")[:80]
            # extract_audio now writes .ogg; tolerate a leftover .wav from an older run
            for ext in (".ogg", ".wav"):
                src = os.path.join(folder, f"{nm}_{idx:03d}{ext}")
                dst = os.path.join(folder, f"{safe}{ext}")
                if os.path.exists(src) and not os.path.exists(dst):
                    os.rename(src, dst)
                    renamed += 1
                if os.path.exists(dst):
                    index[cue] = f"{cat}/{nm}/{safe}{ext}".replace("\\", "/")
                    break

# bgm banks: index by bank name
for d in glob.glob(os.path.join(OUT, "bgm", "*")):
    nm = os.path.basename(d)
    hit = None
    for ext in (".ogg", ".wav"):
        w = os.path.join(d, nm + ext)
        if os.path.exists(w):
            hit = f"bgm/{nm}/{nm}{ext}"
            break
        ws = sorted(glob.glob(os.path.join(d, "*" + ext)))
        if ws:
            hit = "bgm/" + nm + "/" + os.path.basename(ws[0])
            break
    if hit:
        index[nm] = hit

json.dump(
    index, open(os.path.join(OUT, "cue_index.json"), "w", encoding="utf-8"), ensure_ascii=False
)
print(f"banks processed: {banks} | cues renamed: {renamed} | index entries: {len(index)}")
# sample
import itertools

for k, v in itertools.islice(index.items(), 6):
    print("  ", k, "->", v)
