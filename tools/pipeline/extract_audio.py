"""ACB/AWB -> OGG (Vorbis). Two-phase + parallel + skip-existing.

Phase 1 walks every bundle (default group + downloaded UnityCache + standalone
AWB banks), parses the AFS2 sub-files, and writes each unique HCA/ADX blob to a
temp file — queuing a decode task. Cues whose .ogg already exists are skipped
(pass --force to re-decode). Phase 2 decodes the queue with a thread pool (ffmpeg
is an external process, so threads give near-linear speedup). This turns the old
~27-min single-threaded decode into a few minutes, and a re-run where nothing
changed into seconds.

  python extract_audio.py [--force] [--workers N]
"""

import argparse
import glob
import hashlib
import os
import re
import struct
import subprocess
import sys
import tempfile
import time
from concurrent.futures import ThreadPoolExecutor, as_completed

import UnityPy

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import imageio_ffmpeg

import daconfig as C

FF = imageio_ffmpeg.get_ffmpeg_exe()
# vgmstream decodes CRI HCA correctly; ffmpeg's HCA decoder GARBLES this game's HCA
# (verified ~0.28 waveform correlation vs vgmstream) -> robotic audio. So: vgmstream
# HCA->WAV, then ffmpeg WAV->OGG (Vorbis). ffmpeg is only the encoder, never the decoder.
VGM = os.path.join(C.REPO, "tools", "bin", "vgmstream", "vgmstream-cli.exe")

os.chdir(C.GAME)
OUT = C.AUDIO_DIR
LOG = os.path.join(OUT, "audio_extract.log")
os.makedirs(OUT, exist_ok=True)
TMP = tempfile.mkdtemp()


def log(m):
    line = f"[{time.strftime('%H:%M:%S')}] {m}"
    print(line, flush=True)
    open(LOG, "a", encoding="utf-8").write(line + "\n")


def parse_afs2(data, base):
    off_size = data[base + 5]
    id_size = data[base + 6]
    count = struct.unpack_from("<I", data, base + 8)[0]
    align = struct.unpack_from("<H", data, base + 0x0C)[0] or 1
    p = base + 0x10 + count * id_size
    rd = lambda o: struct.unpack_from("<I" if off_size == 4 else "<H", data, o)[0]
    offs = [base + rd(p + i * off_size) for i in range(count + 1)]
    return [((offs[i] + align - 1) // align * align, offs[i + 1]) for i in range(count)]


def acb_name(acb):
    if acb[:4] != b"@UTF":
        return None
    try:
        str_off = struct.unpack_from(">I", acb, 12)[0]
        data_off = struct.unpack_from(">I", acb, 16)[0]
        pool = acb[8 + str_off : 8 + data_off]
    except Exception:
        return None
    toks = [t.decode("utf-8", "replace") for t in pool.split(b"\x00") if 1 < len(t) < 48]
    for t in toks:
        if re.match(r"^[a-z]{2,6}_?\d{4,}$", t):
            return t
    return None


category = C.category


def qmono(cat):
    if C.is_voice(cat):
        return C.VOICE_QUALITY, C.VOICE_MONO
    return C.BGM_SE_QUALITY, False


# ---- decode worker (thread pool; vgmstream + ffmpeg are external processes) ----
DN = subprocess.DEVNULL


def _decode(task):
    hca, out, q, mono = task
    wav = hca[:-4] + ".wav"
    ok = False
    try:
        # 1) HCA -> PCM WAV with vgmstream (correct decoder for CRI HCA)
        r1 = subprocess.run([VGM, "-o", wav, hca], stdout=DN, stderr=DN)
        if r1.returncode == 0 and os.path.exists(wav) and os.path.getsize(wav) > 0:
            # 2) WAV -> OGG (Vorbis) with ffmpeg (encoder only)
            cmd = [FF, "-y", "-loglevel", "error", "-i", wav, "-c:a", "libvorbis", "-q:a", str(q)]
            if mono:
                cmd += ["-ac", "1"]
            cmd += [out]
            r2 = subprocess.run(cmd, stdout=DN, stderr=DN)
            ok = r2.returncode == 0 and os.path.exists(out) and os.path.getsize(out) > 0
    except Exception:
        ok = False
    for f in (wav, hca):
        try:
            os.remove(f)
        except OSError:
            pass
    if not ok and os.path.exists(out):
        try:
            os.remove(out)
        except OSError:
            pass
    return ok


TASKS = []  # (hca_tmp, out_ogg, q, mono)
_tmp_n = 0


def _queue(blob, out, q, mono, force):
    """Write blob to a temp .hca and queue a decode, unless the .ogg already exists."""
    global _tmp_n
    if not force and os.path.exists(out) and os.path.getsize(out) > 0:
        return False  # skipped
    _tmp_n += 1
    hca = os.path.join(TMP, f"{_tmp_n:07d}.hca")
    open(hca, "wb").write(blob)
    TASKS.append((hca, out, q, mono))
    return True


def process_acb(acb, name, force):
    cat = category(name)
    d = os.path.join(OUT, cat, name)
    os.makedirs(d, exist_ok=True)
    q, mono = qmono(cat)
    b = acb.find(b"AFS2")
    if b < 0:
        return 0, 0
    try:
        files = parse_afs2(acb, b)
    except Exception:
        return 0, 0
    queued = skipped = 0
    for i, (s, e) in enumerate(files):
        out = os.path.join(d, f"{name}_{i:03d}.ogg")
        if _queue(acb[s:e], out, q, mono, force):
            queued += 1
        else:
            skipped += 1
    return queued, skipped


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--force", action="store_true", help="re-decode even if the .ogg exists")
    ap.add_argument("--workers", type=int, default=0)
    a = ap.parse_args()

    # ---- Phase 1: gather decode tasks ----
    bundles = [f for f in glob.glob("assets/bin/Data/*") if os.path.isfile(f)]
    local = "assets/aa/Android/defaultlocalgroup_assets_all.bundle"
    if os.path.isfile(local):
        bundles.append(local)
    bundles += sorted(glob.glob("files/UnityCache/Shared/*/*/__data"))
    log(f"scanning {len(bundles)} bundles for embedded ACBs (force={a.force})")

    seen = set()
    n_acb = 0
    queued = 0
    skipped = 0
    namecount = {}
    t0 = time.time()
    for bi, p in enumerate(bundles):
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
            acb = raw[i : i + length] if (0 < length <= len(raw) - i) else raw[i:]
            h = hashlib.md5(acb).digest()
            if h in seen:
                continue
            seen.add(h)
            nm = acb_name(acb) or ("acb_" + hashlib.md5(acb).hexdigest()[:8])
            if nm in namecount:
                namecount[nm] += 1
                nm = f"{nm}__{namecount[nm]}"
            else:
                namecount[nm] = 0
            qd, sk = process_acb(acb, nm, a.force)
            queued += qd
            skipped += sk
            n_acb += 1
        if (bi + 1) % 2000 == 0:
            log(
                f"  {bi + 1}/{len(bundles)} | ACBs={n_acb} queued={queued} skipped={skipped} ({time.time() - t0:.0f}s)"
            )

    # standalone BGM .awb banks
    awbs = glob.glob("files/UnityCache/Shared/*/*/*.awb*")
    log(f"scanning {len(awbs)} standalone AWB banks")
    bq, bmono = qmono("bgm")
    for p in awbs:
        base = os.path.basename(p)
        nm = base[: base.lower().find(".awb")]
        data = open(p, "rb").read()
        b = data.find(b"AFS2")
        if b < 0:
            continue
        try:
            files = parse_afs2(data, b)
        except Exception:
            continue
        d = os.path.join(OUT, "bgm", nm)
        os.makedirs(d, exist_ok=True)
        for i, (s, e) in enumerate(files):
            out = os.path.join(d, f"{nm}_{i:03d}.ogg" if len(files) > 1 else f"{nm}.ogg")
            if _queue(data[s:e], out, bq, bmono, a.force):
                queued += 1
            else:
                skipped += 1

    # ---- Phase 2: parallel decode ----
    workers = a.workers or (os.cpu_count() or 4)
    log(f"decoding {len(TASKS)} cues | workers={workers} | skipped(existing)={skipped}")
    ok = 0
    with ThreadPoolExecutor(max_workers=workers) as ex:
        futs = [ex.submit(_decode, t) for t in TASKS]
        done = 0
        for fu in as_completed(futs):
            ok += 1 if fu.result() else 0
            done += 1
            if done % 2000 == 0 or done == len(futs):
                el = time.time() - t0
                log(f"  decoded {done}/{len(futs)} | ok={ok} | {el:.0f}s")
    fail = len(TASKS) - ok
    log(
        f"DONE: {n_acb} unique ACBs | decoded ok={ok} fail={fail} skipped={skipped} | {time.time() - t0:.0f}s"
    )


if __name__ == "__main__":
    main()
