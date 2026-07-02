# Architecture

This project is two cooperating parts, not one monolith:

1. **A data pipeline** (Python, `tools/`) — a linear DAG that turns your unpacked game
   files into engine-ready assets.
2. **A Unity player** (`unity/`) — consumes those assets and reproduces the game's Live2D
   scenario playback.

Paths are never hardcoded: every stage resolves locations through
[`tools/daconfig.py`](../tools/daconfig.py), which reads `config.json` (+ optional
`config.local.json`). That is the single source of truth — treat it as such when adding code.

> This project is **not** structured as formal "Clean Architecture." That layered style suits
> business apps; here the natural shape is an **ETL pipeline + a player**. The design goals are
> simpler and stricter: clear stage boundaries, one config source, no committed game assets, and
> no vendored third-party code.

## The pipeline (DAG)

```
your unpacked APK  (data/game)
        │
        ▼
   da extract ──► images + text + live2d  (data/extracted/…)   [UnityPy]
        │
   da audio   ──► ACB/AWB → OGG → cue_index.json               [vgmstream/ffmpeg]
        │
   da scenes  ──► scenario scripts → _viewer/scenes_v2.json
        │
   da rip     ──► l2d_* bundles → prefabs/controllers/clips    [AssetRipper, headless]
        │              (data/ripped)
   da live2d  ──► moc3 + textures + model3.json                (data/extracted/live2d/Live2DOutput)
        │
   da models  ──► assemble unity/Assets/Models
        │
   da unity setup ──► import + wire in the Unity project
        │
   da unity windows | android ──► player builds (build/)

   da dump    ──► Cpp2IL → data/dump   (only when the game CODE changes; off the main path)
```

`da update` runs the main path end to end (`extract → audio → scenes → rip → live2d →
models → unity setup`). Every stage is **skip-existing / re-runnable** so a game update only
reprocesses what changed.

### Where each stage lives

| Stage | Script(s) | Inputs | Outputs |
|---|---|---|---|
| extract | `pipeline/extract_assets.py`, `organize_images.py` | `data/game` | `data/extracted/{images,text,live2d}` |
| audio | `pipeline/extract_audio.py`, `rename_cues.py`, `encode_audio.py`, `acb_lib.py` | game ACB/AWB | `data/extracted/audio/*.ogg`, `cue_index.json` |
| scenes | `pipeline/parse_scenes.py` | scenario scripts | `_viewer/scenes_v2.json` |
| rip | `pipeline/rip_headless.py`, `scan_l2d_bundles.py` | UnityCache bundles | `data/ripped/ExportedProject` |
| live2d | `pipeline/extract_live2d.py` | UnityCache bundles | `Live2DOutput/<model>/…` |
| models | `pipeline/build_models.py`, `wire_model.py`, `fill_variant_motions.py` | `ripped` + `Live2DOutput` | `unity/Assets/Models` |
| dump | `da dump` (Cpp2IL) | `libil2cpp.so` + `global-metadata.dat` | `data/dump` |

The `da` CLI ([`tools/da.py`](../tools/da.py)) is a thin dispatcher: each subcommand shells out
to a pipeline script or a Unity batchmode method. Adding a stage = new `pipeline/<x>.py` + a
`cmd_<x>` in `da.py` + a row in the README table.

## The Unity player

Runtime and editor code are deliberately separated:

- **`unity/Assets/DA_Runtime/`** — runs in the built player:
  - `DABootstrap` — builds the uGUI in code and boots the scene.
  - `L2DStage` — the Live2D stage: loads a model, drives the Mecanim `Animator`, frames the
    Cubism canvas, applies the mosaic toggle. (Ports the game's `NovelLive2DAnimator`.)
  - `AdvPlayer` + `AdvData` — the ADV scenario interpreter (`model/motion/say/wait/bgm/…`).
  - `AudioRouter`, `CueIndex` — resolve and play cues (OGG/WAV by extension).
  - `ScenesDb`, `ModelRegistry`, `ModelBundles` — data lookups + runtime bundle loading.
  - `DAConfig` — resolves the asset root on device/desktop: a user-chosen folder (saved in
    `PlayerPrefs` via the in-app ☰ → "Assets folder" picker) wins, then a `da_assets.txt`
    override, then auto-detection of the usual spots, then a platform default.
- **`unity/Assets/Editor/`** — editor-only build tooling, never in a build:
  - `DA_Build` — build entry points (`FullSetup`, `BuildWindows`, `BuildAndroid`,
    `WinBundleTest`) invoked by `da unity <step>`.
  - `DA_Setup` — URP setup, model wiring, and validation.

The engine detail that drives everything (motions are played through a Mecanim
`AnimatorController`, **not** `CubismMotionController.PlayAnimation`) is documented in
[`behavior_spec.md`](behavior_spec.md).

## Invariants (please keep these true)

- **No game assets in git.** Everything under `data/` and `build/` is derived and git-ignored.
- **No vendored third-party code in git.** The Cubism SDK (`unity/Assets/Live2D/`) and the
  tools (`tools/bin/`) are git-ignored and fetched per the README.
- **No hardcoded absolute paths.** Resolve through `daconfig.py` / `config.json`.
- **No secrets.** The Android keystore is generated locally, never committed.
