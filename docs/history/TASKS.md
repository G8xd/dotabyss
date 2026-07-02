# Dot Abyss X — Unity rebuild: task checklist

Live status of the phased plan. Historical note: paths in entries below predate the
consolidation into this repo (`dotabyss/`); see `docs/README.md` for the current layout
and the `da` CLI. Old `dotabyss_extracted/` → `data/extracted/`, `dotabyss_unity/` → `unity/`,
`dotabyss_il2cpp/` → `data/dump` + `data/ripped`.

| # | Phase | Status |
|---|-------|--------|
| 0 | Install Unity 6000.3.8f1 (URP) + Cubism SDK for Unity | ✅ done |
| 1 | IL2CPP dump → `behavior_spec.md` | ✅ done |
| 2 | Rip Unity assets (AssetRipper) → prefabs + AnimatorControllers + clips | ✅ done |
| 3 | Port the thin ADV scene engine in C# | ✅ done |
| 4 | Player UI (model/motion/scene pickers) + audio + mosaic toggle | ✅ done |
| 5 | Desktop build + verify the reported defects | ✅ done — defects verified gone |
| 6 | Android build (assets from device storage) | ✅ APK built (34 MB) — AssetBundles + app-folder; device-test pending |
| 7 | Uninstall Godot toolchain + delete old player | ✅ done — 11.1 GB freed; audio tools + keystore kept |
| 8 | Game-look + exact camera (cinematic camera, post-fx, fade/window, clean UI) | ✅ done — verified `gamelook.png` |

## Where things stand (Phase 2 detail)
- ✅ AssetRipper rip of the bundle cache → 133 `l2d_*.controller`, prefabs, clips, materials.
- ✅ SDK model prefab built for `l2d_10070100032` (205 params, 289 meshes).
- ✅ Genuine ripped clips BIND to the SDK model (73/205 params move on scene02 sample).
- ✅ Real AnimatorController wired (16 layers, 97 params).
- ✅ URP pipeline + `CubismRenderPassFeature` created and assigned (`Assets/Settings/CubismURP.asset`).
- ✅ **Visual render CONFIRMED** — `l2d_10070100032` renders fully (textures, masking, blend modes)
      through the SDK's own URP render pass. Proof image: `dotabyss_il2cpp/render_play.png`.

## Phase 2 scale-out — DONE (all 132 models)
- ✅ `scale_models.py` reconstructed all 132: moc3 + textures + model3.json(motions stripped) +
      genuine controller + its clips (22-entry Cubism GUID remap). 6281 clips, 0 missing refs.
- ✅ `DA_Setup.WireAll` → 132/132 prefabs generated (0 importer crashes), controllers wired (avg 10.4 layers).
- ✅ `DA_Setup.ValidateAll` → 132/132 parameter binding OK.
- ✅ Visual render confirmed on 2 distinct models (`render_play.png`, `render_spot.png`).
- ⏭ Packaging as AssetBundles/Addressables is deferred to the build phase (Phase 5/6), where runtime loading matters.

## Phase 3/4 — player built and WORKING (desktop)
- ✅ Runtime engine ported (`Assets/DA_Runtime/`): L2DStage (NovelLive2DAnimator.Playing port),
      AdvPlayer (NovelCmd* interpreter: model/motion/say/wait/bgm/bgv/se/bg/hide/click), AudioRouter,
      CueIndex (21,447 cues), ScenesDb (133 scenes), ModelRegistry (132), DABootstrap (uGUI built in code).
- ✅ Confirmed in a real Windows build: model/scene/motion pickers, scene playback, Japanese dialogue
      (「あ……司令官くんっ！？」 / speaker フィルム), Live2D rendering through URP, camera framing.
- ▶ **RUN IT:** launch `build/DotAbyssPlayer.exe` (1280×720 window). Pick a model + ▶ Motion,
      or pick a Scene + ▶ Scene. Space/Click = advance. Assets are read from `data/extracted`
      (audio/cue_index/scenes).

## Phase 4 — DONE (mosaic toggle + audio verified)
- ✅ **Mosaic toggle** wired (motion row, default OFF = uncensored). Mosaic drawables = ArtMesh names
      starting with `Mosaic` (`Mosaic_*`, `MosaicInsted_*`). Probe proved hiding them gives the clean
      uncensored view and removes the red marker. Fix: `L2DStage.ApplyMosaic` uses
      `gameObject.SetActive(MosaicShown)` (NOT `mr.enabled` — Cubism's CubismRenderController re-applies
      `MeshRenderer.enabled` from each drawable's IsVisible flag every update, so enabled-toggle doesn't stick).
      The game's TRUE grab-pixelation (base-RT + Abyss/Live2DMosaicV2 shader, `_grabEffectValue`/`_ReUV`)
      needs the CustomPPMosaic render-feature pipeline — deferred; the visibility toggle is the 90% solution.
- ✅ **Audio verified** end-to-end: scene-0 cues all resolve to real files (se4300003, bgm0029,
      vc_10070100032_*) and the player log loads clean (no exceptions, no missing-script warnings at runtime).
- ⏭ Strip editor-only "missing script" (CubismParametersInspector) — harmless, no runtime warning. Deferred.

## Phase 5 — DONE (reported Godot defects verified GONE on l2d_10070100032 scene 0)
Verified via DA_CAPSEQ frame sequence + per-frame head-param logging (`[DA] SEQPARAM`):
- ✅ Crossfade not snap — ParamAngleX interpolates through intermediates (-0.93→0.50→0.90→-0.04→-0.95).
- ✅ No leftover/overlap parts — clean frames (seq_00..09 in `dotabyss_il2cpp/seq/`).
- ✅ Intro→loop / continuous — 12s playback through multiple poses, cycles correctly.
- ✅ No stuck head rotation — returns to ≈neutral (-0.04) mid-scene, never freezes.
- ✅ Mosaic hidden by default (no red in any frame).
- ⏭ Idle HarmonicMotion sway: SDK-built prefabs lack CubismHarmonicMotionController (minor fidelity gap).
- Capture mode: env `DA_CAPSEQ="N:interval:dir"` on the player; `DA_TEST_SCENE=idx`; `DA_CAP=path` (single).

## Phase 8 — Game-look + exact camera — DONE (verified gamelook.png)
The current player looks like the actual game now, not a debug viewer:
- **Exact camera = the Cubism CANVAS** (NOT drawable bounds). `L2DStage.TryFrameCanvas` uses
  `CubismModel.CanvasInformation`: ortho = (CanvasHeight/PixelsPerUnit)/2, centered on canvas origin,
  width fills the screen. The canvas is the artist's intended crop (drawable bounds include off-stage
  padding — that was the "whole set" look). l2d_10070100032 canvas = 2176×1696 px, PPU 1696 → world
  1.28×1.00, ortho 0.5. `Cinematic=false` restores the old fit-to-bounds debug view.
- **URP post-fx** (DABootstrap.BuildCamera): Bloom + ColorAdjustments (warm pink filter) + Vignette via
  a runtime global Volume; camera `renderPostProcessing=true`.
- **Scenario re-parse** (`dotabyss_extracted/parse_scenes_v2.py` → `_viewer/scenes_v2.json`): recovers
  the cinematic ops the web-viewer parser dropped. 133 scenes, 48,142 steps, 422 fade, 300 window,
  35 camera/scale. Build copies scenes_v2.json (CopyStreamingAssets prefers it; BuildWindows calls it).
- **New ops wired** (AdvData + AdvPlayer): fade (In/Out, Black/White, sec — sync blocks), window (on/off
  fade of the dialogue CanvasGroup), uivisible, charaload (speaker name; l2dmessage already carries it),
  charamove/charascale (model transform), camzoom/cammove (camera; design-px ÷ PPU), cleanall, sestop.
  Note: the 35 camera/scale ops are wired but not yet visually verified (scene 0 uses none).
- **Clean UI** (DABootstrap rewrite): AUTO + SKIP toggles + ☰ top-right; pickers/mosaic/stop live in a
  hidden debug panel opened by ☰; full-screen fade Image overlay; dialogue box with speaker+text.
  SKIP = AdvPlayer.skip (ConsumeAdvance returns true). Cubism re-enable note still applies to mosaic.
- ⏭ True grab-mosaic pixelation + per-cut screen effects (MONOTONE/SEPIA/BLUR/WARP) still deferred;
  base mood = the warm grade. Mosaic default = fully uncensored (hidden), per user.

### Phase 8 polish (user feedback round 1) — fixed
- **White blobs on characters** = NOT missing textures (LookProbe: nullTexDrawables=0). It was post-fx
  blowing out bright skin (bloom thr 0.88 + exposure +0.22 + contrast 10). FIXED by softening: bloom
  0.28/thr 1.15, exposure 0, contrast 4, gentle warm filter, vignette 0.26. Verified on l2d_10010100022.
- **Invisible dropdown text** (white-on-white) FIXED: `DABootstrap.StyleDropdown` (dark list + white
  caption/item text). Dialogue text now white + `Shadow` for readability over bright art.
- **No resize / fullscreen** FIXED: `PlayerSettings.resizableWindow=true`; `⛶` button + F11 + Alt+Enter
  (Screen.SetResolution windowed↔FullScreenWindow). Canvas framing adapts (ortho locked, width fills).
- Capture hook `DA_DEBUGPANEL=1` opens the picker panel + model dropdown.
- ⏭ Atmosphere warmth subjective/tunable; exact game button styling (rounded translucent AUTO▶/SKIP⏭)
  not yet pixel-matched — pending user direction.

## Phase 6 — Android APK — BUILT (device-test pending)
- **AssetBundle pipeline** (`DA_Build`): `AssignBundles` (one bundle per model), `BuildBundles`,
  `BuildRegistryNamesOnly` (no embedded prefabs → tiny APK), `BuildAndroid` (switch to Android target,
  build Android bundles, names-only registry, IL2CPP arm64, debug.keystore, APK not AAB), restores the
  embedded registry in `finally`. Runtime: `ModelBundles.LoadPrefab` loads `<AssetRoot>/bundles/<name>`;
  `L2DStage.Load` falls back to it when the registry prefab is null.
- **Sizes:** APK **34 MB**; Android bundles **870 MB** (`dotabyss_extracted/bundles/`); Windows bundles
  870 MB (`bundles_win/`). Embedded desktop build was 4.9 GB (uncompressed clips+textures) — bundles
  compress models ~3.6×. **Loader verified on desktop** (`dotabyss_build_test`, `bundle_test.png`).
- **Device layout** (app-specific folder, no permission): copy to
  `/storage/emulated/0/Android/data/com.da.dotabyssplayer/files/dotabyss_extracted/` →
  `bundles/` (870 MB) + `audio/` (+cue_index.json) + `images/`. (`DAConfig` checks persistentDataPath.)
- **Audio is the bloat:** 15.5 GB decoded WAV. Re-encode to OGG/Opus → ~1.5 GB to match the original ~5 GB.
- ⏭ **Untested on device** — main risk is URP shader-variant stripping (Cubism shaders in bundles may
  render pink). If so: add Cubism shaders to Always-Included / a ShaderVariantCollection and rebuild.

## (old) Phase 6 notes — the APK-size blocker
- Android module READY: AndroidPlayer + NDK + SDK + OpenJDK installed for 6000.3.8f1. Debug keystore
  generated locally (see README setup). `DAConfig` already resolves Android paths + `da_assets.txt` override.
- **Blocker:** desktop build is 4.9 GB because all 132 models embed via ModelRegistry prefab refs
  (`Assets/Models` = 3.13 GB: 1.9 GB .anim, 597 MB png, 339 MB .asset, 169 MB moc3, 152 MB prefab).
  Device-side already-external: audio 15.5 GB, images 1.7 GB.
- **Plan:** per-model **AssetBundles** (Android target) → tiny APK (engine+UI+scenes.json); models load
  at runtime from `<AssetRoot>/bundles/`; ModelRegistry becomes name-only (no embedded prefab refs).
- **Awaiting user:** asset-access method on device (app-folder vs all-files-access) + install via adb vs APK file.

_This file is a historical build log of the reverse-engineering effort, kept for context.
For the current layout and workflow see the root `README.md` and `docs/ARCHITECTURE.md`._
