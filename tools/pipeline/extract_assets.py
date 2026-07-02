import glob
import hashlib
import os
import shutil
import sys
import time

import UnityPy

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import daconfig as C

os.chdir(C.GAME)
OUT = C.EXTRACTED
LOG = os.path.join(OUT, "extract.log")

os.makedirs(OUT, exist_ok=True)


def log(msg):
    line = f"[{time.strftime('%H:%M:%S')}] {msg}"
    print(line, flush=True)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(line + "\n")


# ---- gather sources ----
bundles = []
bundles += sorted(glob.glob("files/UnityCache/Shared/*/*/__data"))
local = "assets/aa/Android/defaultlocalgroup_assets_all.bundle"
if os.path.isfile(local):
    bundles.append(local)
bundles += sorted(f for f in glob.glob("assets/bin/Data/*") if os.path.isfile(f))

log(f"Sources: {len(bundles)} files to scan")

# ---- output dirs ----
# Clean images/ and text/ first so re-runs don't ACCUMULATE stale assets (unique_path only
# avoids collisions, it never removes leftovers from a previous game version — which would
# otherwise inflate parse_scenes, etc.). Only these two dirs are owned by this script; audio/,
# live2d/, videos/, _viewer/ are produced by other steps and left untouched.
IMG = os.path.join(OUT, "images")
TXT = os.path.join(OUT, "text")
for d in (IMG, TXT):
    if os.path.isdir(d):
        shutil.rmtree(d, ignore_errors=True)
os.makedirs(IMG, exist_ok=True)
os.makedirs(TXT, exist_ok=True)


def sanitize(name):
    bad = '<>:"/\\|?*\n\r\t'
    for c in bad:
        name = name.replace(c, "_")
    return name.strip().strip(".")[:120] or "unnamed"


seen_img = set()  # content hash -> dedupe
used_paths = set()  # avoid filename collisions
stats = {"img": 0, "img_dup": 0, "txt": 0, "err": 0}


def unique_path(base_dir, name, ext):
    name = sanitize(name)
    p = os.path.join(base_dir, name + ext)
    if p not in used_paths and not os.path.exists(p):
        used_paths.add(p)
        return p
    i = 1
    while True:
        p = os.path.join(base_dir, f"{name}_{i}{ext}")
        if p not in used_paths and not os.path.exists(p):
            used_paths.add(p)
            return p
        i += 1


def container_subdir(cpath):
    # turn "Assets/Foo/Bar/baz.png" -> ("Foo/Bar", "baz")
    if not cpath:
        return "_misc", None
    cp = cpath.replace("\\", "/")
    if cp.lower().startswith("assets/"):
        cp = cp[7:]
    d = os.path.dirname(cp)
    base = os.path.splitext(os.path.basename(cp))[0]
    return sanitize_dir(d), base


def sanitize_dir(d):
    parts = [sanitize(p) for p in d.split("/") if p]
    return "/".join(parts) if parts else "_misc"


start = time.time()
for i, p in enumerate(bundles):
    try:
        env = UnityPy.load(p)
    except Exception:
        stats["err"] += 1
        continue

    # path_id -> container path
    cmap = {}
    try:
        for cpath, obj in env.container.items():
            cmap[obj.path_id] = cpath
    except Exception:
        pass

    for obj in env.objects:
        t = obj.type.name
        try:
            if t in ("Texture2D", "Sprite"):
                data = obj.read()
                img = data.image
                if img is None:
                    continue
                import io

                buf = io.BytesIO()
                img.save(buf, format="PNG")
                raw = buf.getvalue()
                h = hashlib.md5(raw).digest()
                if h in seen_img:
                    stats["img_dup"] += 1
                    continue
                seen_img.add(h)
                cpath = cmap.get(obj.path_id)
                sub, base = container_subdir(cpath)
                if base is None:
                    base = getattr(data, "m_Name", None) or "unnamed"
                d = os.path.join(IMG, sub)
                os.makedirs(d, exist_ok=True)
                outp = unique_path(d, base, ".png")
                with open(outp, "wb") as f:
                    f.write(raw)
                stats["img"] += 1
            elif t == "TextAsset":
                data = obj.read()
                script = getattr(data, "m_Script", None)
                if script is None:
                    continue
                b = (
                    script.encode("utf-8", "surrogateescape")
                    if isinstance(script, str)
                    else bytes(script)
                )
                cpath = cmap.get(obj.path_id)
                sub, base = container_subdir(cpath)
                if base is None:
                    base = getattr(data, "m_Name", None) or "unnamed"
                # guess extension
                ext = ".bytes"
                if cpath and "." in os.path.basename(cpath):
                    ext = os.path.splitext(cpath)[1]
                elif b[:4] == b"MOC3":
                    ext = ".moc3"
                elif b[:1] in (b"{", b"["):
                    ext = ".json"
                d = os.path.join(TXT, sub)
                os.makedirs(d, exist_ok=True)
                outp = unique_path(d, base, ext)
                with open(outp, "wb") as f:
                    f.write(b)
                stats["txt"] += 1
        except Exception:
            stats["err"] += 1

    if (i + 1) % 500 == 0:
        el = time.time() - start
        log(
            f"{i + 1}/{len(bundles)} bundles | img={stats['img']} dup={stats['img_dup']} txt={stats['txt']} err={stats['err']} | {el:.0f}s"
        )

log(
    f"DONE {len(bundles)} bundles in {time.time() - start:.0f}s | "
    f"images={stats['img']} (deduped {stats['img_dup']}) text={stats['txt']} errors={stats['err']}"
)
