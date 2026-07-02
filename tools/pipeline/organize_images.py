"""Sort the flat images/_misc folder into size buckets by PNG dimensions.
Reads only the 24-byte PNG header (cheap), then moves files."""

import glob
import os
import shutil
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

BASE = C.IMAGES
SRC = os.path.join(BASE, "_misc")


def png_size(path):
    with open(path, "rb") as f:
        head = f.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n" or head[12:16] != b"IHDR":
        return None
    w, h = struct.unpack(">II", head[16:24])
    return w, h


def bucket(w, h):
    m = max(w, h)
    if m >= 1024:
        return "01_large_1024up"  # CGs, backgrounds, illustrations
    if m >= 512:
        return "02_medium_512"
    if m >= 256:
        return "03_small_256"
    if m >= 64:
        return "04_icons_64"
    return "05_tiny_under64"


moved = {}
files = glob.glob(os.path.join(SRC, "*.png"))
for p in files:
    sz = png_size(p)
    b = bucket(*sz) if sz else "00_unknown"
    d = os.path.join(BASE, b)
    os.makedirs(d, exist_ok=True)
    dst = os.path.join(d, os.path.basename(p))
    if os.path.exists(dst):
        stem, ext = os.path.splitext(os.path.basename(p))
        i = 1
        while os.path.exists(dst):
            dst = os.path.join(d, f"{stem}__{i}{ext}")
            i += 1
    shutil.move(p, dst)
    moved[b] = moved.get(b, 0) + 1

try:
    os.rmdir(SRC)
except OSError:
    pass

print(f"Organized {len(files)} images:")
for b in sorted(moved):
    print(f"  {b}: {moved[b]}")
