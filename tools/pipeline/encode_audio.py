"""One-time re-encode of the extracted WAVs to OGG (Vorbis), then delete the WAVs
and rewrite cue_index.json paths .wav -> .ogg.

Quality (from config.json / daconfig):
  voice_*  ->  -q:a {VOICE_QUALITY}  (+ -ac 1 if VOICE_MONO)   ~ small, speech
  bgm/se   ->  -q:a {BGM_SE_QUALITY}                            ~ music/effects

Usage:
  python encode_audio.py                 # encode everything, delete WAVs, fix cue_index
  python encode_audio.py --limit 20      # only first 20 (smoke test, keeps WAVs? no: still deletes)
  python encode_audio.py --keep-wav      # encode but DO NOT delete the WAVs
  python encode_audio.py --workers 8     # parallelism (default = cpu count)
  python encode_audio.py --reindex-only  # skip encoding, just rewrite cue_index .wav->.ogg
"""

import argparse
import glob
import json
import os
import subprocess
import sys
import time
from concurrent.futures import ProcessPoolExecutor, as_completed

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import imageio_ffmpeg

FF = imageio_ffmpeg.get_ffmpeg_exe()

DEVNULL = subprocess.DEVNULL


def quality_for(wav_path):
    """(quality, mono) from the top-level category folder under audio/."""
    rel = os.path.relpath(wav_path, C.AUDIO_DIR).replace("\\", "/")
    top = rel.split("/", 1)[0]
    if C.is_voice(top):
        return C.VOICE_QUALITY, C.VOICE_MONO
    return C.BGM_SE_QUALITY, False


def encode_one(args):
    ff, wav, q, mono, keep = args
    ogg = wav[:-4] + ".ogg"
    cmd = [ff, "-y", "-loglevel", "error", "-i", wav, "-c:a", "libvorbis", "-q:a", str(q)]
    if mono:
        cmd += ["-ac", "1"]
    cmd += [ogg]
    try:
        r = subprocess.run(cmd, stdout=DEVNULL, stderr=DEVNULL)
    except Exception:
        return (wav, False)
    ok = r.returncode == 0 and os.path.exists(ogg) and os.path.getsize(ogg) > 0
    if ok:
        if not keep:
            try:
                os.remove(wav)
            except OSError:
                pass
    else:
        if os.path.exists(ogg):
            try:
                os.remove(ogg)
            except OSError:
                pass
    return (wav, ok)


def reindex():
    """Rewrite cue_index.json values .wav -> .ogg (only when the .ogg exists)."""
    if not os.path.isfile(C.CUE_INDEX):
        print("no cue_index.json to reindex")
        return
    idx = json.load(open(C.CUE_INDEX, encoding="utf-8"))
    changed = 0
    for cue, rel in list(idx.items()):
        if isinstance(rel, str) and rel.lower().endswith(".wav"):
            ogg = rel[:-4] + ".ogg"
            if os.path.exists(os.path.join(C.AUDIO_DIR, ogg)):
                idx[cue] = ogg
                changed += 1
    json.dump(idx, open(C.CUE_INDEX, "w", encoding="utf-8"), ensure_ascii=False)
    print(f"cue_index.json: {changed}/{len(idx)} entries -> .ogg")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--workers", type=int, default=0)
    ap.add_argument("--keep-wav", action="store_true")
    ap.add_argument("--reindex-only", action="store_true")
    a = ap.parse_args()

    if a.reindex_only:
        reindex()
        return

    wavs = sorted(glob.glob(os.path.join(C.AUDIO_DIR, "**", "*.wav"), recursive=True))
    if a.limit:
        wavs = wavs[: a.limit]
    if not wavs:
        print("no .wav files found under", C.AUDIO_DIR)
        reindex()
        return

    tasks = []
    for w in wavs:
        q, mono = quality_for(w)
        tasks.append((FF, w, q, mono, a.keep_wav))

    workers = a.workers or (os.cpu_count() or 4)
    print(
        f"encoding {len(tasks)} WAV -> OGG | workers={workers} | "
        f"voice q{C.VOICE_QUALITY}{' mono' if C.VOICE_MONO else ''} | bgm/se q{C.BGM_SE_QUALITY}"
        f"{' | KEEP wav' if a.keep_wav else ''}",
        flush=True,
    )

    ok = fail = 0
    t0 = time.time()
    with ProcessPoolExecutor(max_workers=workers) as ex:
        futs = [ex.submit(encode_one, t) for t in tasks]
        done = 0
        for fu in as_completed(futs):
            _, good = fu.result()
            ok += good
            fail += 0 if good else 1
            done += 1
            if done % 1000 == 0 or done == len(futs):
                el = time.time() - t0
                rate = done / el if el else 0
                print(
                    f"  {done}/{len(futs)} | ok={ok} fail={fail} | {rate:.0f}/s {el:.0f}s",
                    flush=True,
                )

    print(f"DONE encode: ok={ok} fail={fail} in {time.time() - t0:.0f}s")
    if fail:
        print("WARNING: some files failed; their .wav were kept.")
    reindex()


if __name__ == "__main__":
    main()
