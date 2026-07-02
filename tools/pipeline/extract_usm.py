import glob
import os
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

ROOT = C.GAME
os.chdir(ROOT)
OUT = C.VIDEOS
os.makedirs(OUT, exist_ok=True)

usms = []
usms += glob.glob("assets/CriStreamingData/*.usm*")
usms += glob.glob("files/UnityCache/Shared/*/*/*.usm*")

print(f"Found {len(usms)} USM files", flush=True)
for src in usms:
    base = os.path.basename(src)
    i = base.lower().find(".usm")
    clean = base[: i + 4]  # e.g. abyss_pv_release.usm
    tmpdir = tempfile.mkdtemp()
    dst = os.path.join(tmpdir, clean)
    shutil.copy2(src, dst)
    print(f"=== {clean} ===", flush=True)
    try:
        subprocess.run(
            [sys.executable, "-m", "wannacri", "extractusm", dst, "-o", OUT],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    finally:
        os.chdir(ROOT)
        shutil.rmtree(tmpdir, ignore_errors=True)

print("USM demux complete.", flush=True)
