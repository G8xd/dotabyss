"""Re-parse the ADV scenario scripts KEEPING the cinematic direction the original
parse_scenes.py dropped: fade, window, charaload (speaker names), uivisible,
charamove/charascale/charaface, dotcamera move/zoom, subimage, shake.
Output -> _viewer/scenes_v2.json (consumed by the Unity player)."""

import glob
import hashlib
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

TEXT = C.TEXT_DIR
OUT = C.SCENES
os.makedirs(os.path.dirname(OUT), exist_ok=True)

VOICE_RE = re.compile(r"^(vc|bgv|mcv|cv|adv)_[0-9]")


def fnum(s, d=0.0):
    try:
        return float(s)
    except Exception:
        return d


def parse(path):
    txt = open(path, encoding="utf-8", errors="replace").read().lstrip("﻿")
    steps = []
    model = None
    names = {}
    for raw in txt.splitlines():
        line = raw.strip().lstrip("﻿")
        if not line or line.startswith("//") or line.startswith(":"):
            continue
        p = [x.strip() for x in line.split(",")]
        c = p[0]

        def vf(p=p):
            for x in p[1:]:
                if VOICE_RE.match(x):
                    return x
            return None

        if c == "l2dshow" and len(p) > 1:
            model = p[1]
            steps.append({"op": "model", "id": p[1]})
        elif c == "charaload" and len(p) > 1:
            slot = p[1]
            who = p[3] if len(p) > 3 else ""
            names[slot] = who
            steps.append(
                {"op": "charaload", "slot": slot, "id": p[2] if len(p) > 2 else "", "who": who}
            )
        elif c == "l2dhide":
            steps.append({"op": "hide"})
        elif c == "cleanall":
            steps.append({"op": "cleanall"})
        elif c in ("l2dmotion", "asyncl2dmotion") and len(p) > 1:
            dly = fnum(p[4]) if (c.startswith("async") and len(p) > 4) else 0.0
            steps.append(
                {"op": "motion", "name": p[1], "async": c.startswith("async"), "delay": dly}
            )
        elif c == "charaface" and len(p) > 2:
            steps.append({"op": "motion", "name": p[2], "async": False, "delay": 0.0, "slot": p[1]})
        elif c in ("l2dmessage", "message") and len(p) > 2:
            who = p[1] if c == "l2dmessage" else ""
            steps.append(
                {"op": "say", "who": who, "text": p[2].replace("<br>", "\n"), "voice": vf()}
            )
        elif c == "bgvplay":
            v = vf() or (p[2] if len(p) > 2 else None)
            if v:
                steps.append({"op": "bgv", "cue": v})
        elif c == "bgvstop":
            steps.append({"op": "bgvstop"})
        elif c == "bgmplay" and len(p) > 2:
            steps.append({"op": "bgm", "id": p[2]})
        elif c == "bgmstop":
            steps.append({"op": "bgmstop"})
        elif c == "seplay" and len(p) > 2:
            steps.append({"op": "se", "id": p[2]})
        elif c == "sestop":
            steps.append({"op": "sestop"})
        elif c == "wait" and len(p) > 1:
            steps.append({"op": "wait", "sec": fnum(p[1])})
        elif c in ("waitorclick", "click"):
            steps.append({"op": "click"})
        elif c == "bg" and len(p) > 1:
            steps.append({"op": "bg", "id": p[1]})
        # ---- cinematic direction (previously dropped) ----
        elif c in ("fade", "asyncfade") and len(p) > 1:
            steps.append(
                {
                    "op": "fade",
                    "dir": p[1],
                    "color": (p[2] if len(p) > 2 else "Black"),
                    "sec": fnum(p[3]) if len(p) > 3 else 0.0,
                    "async": c.startswith("async"),
                }
            )
        elif c == "window" and len(p) > 1:
            steps.append(
                {
                    "op": "window",
                    "on": (p[1].lower() == "on"),
                    "sec": fnum(p[2]) if len(p) > 2 else 0.0,
                }
            )
        elif c == "uivisible" and len(p) > 1:
            steps.append({"op": "uivisible", "on": (p[1].lower() == "on")})
        elif c in ("charamove", "asynccharamove") and len(p) > 4:
            axis = p[3].upper()
            val = fnum(p[4])
            steps.append(
                {
                    "op": "charamove",
                    "slot": p[1],
                    "mode": p[2],
                    "axis": axis,
                    "x": val if axis == "X" else 0.0,
                    "y": val if axis == "Y" else 0.0,
                    "sec": fnum(p[5]) if len(p) > 5 else 0.0,
                    "async": c.startswith("async"),
                }
            )
        elif c == "charascale" and len(p) > 2:
            steps.append(
                {
                    "op": "charascale",
                    "slot": p[1],
                    "scale": fnum(p[2], 1.0),
                    "sec": fnum(p[3]) if len(p) > 3 else 0.0,
                }
            )
        elif c in ("dotcamerazoom",) and len(p) > 1:
            steps.append(
                {"op": "camzoom", "zoom": fnum(p[1], 1.0), "sec": fnum(p[2]) if len(p) > 2 else 0.0}
            )
        elif c in ("dotcameramove", "asyncdotcameramove") and len(p) > 2:
            steps.append(
                {
                    "op": "cammove",
                    "x": fnum(p[1]),
                    "y": fnum(p[2]),
                    "sec": fnum(p[4]) if len(p) > 4 else 0.0,
                    "async": c.startswith("async"),
                }
            )
        elif c in ("shake", "asyncshake"):
            steps.append({"op": "shake", "async": c.startswith("async")})
        elif c == "subimage" and len(p) > 2:
            steps.append({"op": "subimage", "path": p[1], "slot": p[2]})
    return model, steps, names


scenes = []
for f in glob.glob(os.path.join(TEXT, "**", "*"), recursive=True):
    if not os.path.isfile(f):
        continue
    try:
        if open(f, "rb").read(3) != b"\xef\xbb\xbf":
            continue
    except Exception:
        continue
    if "l2dshow" not in open(f, encoding="utf-8", errors="replace").read():
        continue
    model, steps, names = parse(f)
    if not model:
        continue
    says = sum(1 for s in steps if s["op"] == "say")
    voiced = sum(1 for s in steps if s["op"] == "say" and s.get("voice"))
    sid = model + "_" + hashlib.md5(f.encode()).hexdigest()[:6]
    scenes.append(
        {
            "id": sid,
            "model": model,
            "says": says,
            "voiced": voiced,
            "steps": steps,
            "src": os.path.basename(f),
        }
    )

scenes.sort(key=lambda s: (-s["voiced"], s["model"]))
json.dump(scenes, open(OUT, "w", encoding="utf-8"), ensure_ascii=False)
fades = sum(sum(1 for st in s["steps"] if st["op"] == "fade") for s in scenes)
wins = sum(sum(1 for st in s["steps"] if st["op"] == "window") for s in scenes)
cam = sum(
    sum(1 for st in s["steps"] if st["op"] in ("camzoom", "cammove", "charamove", "charascale"))
    for s in scenes
)
print(f"scenes: {len(scenes)} | steps: {sum(len(s['steps']) for s in scenes)}")
print(f"fade ops: {fades} | window ops: {wins} | camera/scale ops: {cam}")
print("scene[0]:", scenes[0]["id"], "steps:", len(scenes[0]["steps"]))
