# Dot Abyss X — Live2D playback behavior (recovered from IL2CPP)

Source: Cpp2IL dump of `libil2cpp.so` + `global-metadata.dat` (Unity 6000.3.8f1,
metadata v39), decompiled to C# with ilspycmd. Game code is in `Project.dll`,
namespace `Project.Novel`. RVAs below are into `libil2cpp.so` (arm64-v8a).

## Headline finding (this changes the asset strategy)
The game does **NOT** call `CubismMotionController.PlayAnimation` to play motions.
Live2D is driven by a **Unity Mecanim `Animator`** whose `AnimatorController` state
machine encodes every transition (the crossfades), loop state, and intro→loop chain.
The C# only sets named Animator parameters:

- `NovelLive2DAnimator` (RVA region 0xB055xxx) has `enum Layer { Scene=0, Face=1, Body=2, Emotion=3 }`
  → the AnimatorController has **4 layers**.
- `Playing(string trigger, bool boolValue)` → `StartCoroutine(PlayingAsync(trigger, boolValue))`.
- `PlayingAsync.MoveNext` (RVA 0xB055894):
  - `_parameters = animator.parameters` (cached in `Initialize`).
  - Finds the `AnimatorControllerParameter` whose `name == trigger`
    (`Enumerable.Where(... x.name == trigger).FirstOrDefault()`).
  - If that parameter's `type` is **Bool** → `animator.SetBool(trigger, boolValue)`.
  - Else (Trigger) → `animator.SetTrigger(trigger)`.
  - Also `string.StartsWith(...)` gate + `GetCurrentAnimatorStateInfo(layer).shortNameHash`
    polling (waits for the state to actually start / for scene transitions).
- `Initialize` calls `AnimatorExtensions.ResetAllTriggers(animator)`.
- `CanSceneTransition()` checks the Scene-layer current `shortNameHash` (so a scene
  intro finishes before the next scene trigger is accepted). Static `SceneTrigger`.

**Therefore the fade times / loop flags / intro→loop / return-to-neutral are authored
inside the AnimatorController asset, not in code and not in the motion3.json** (whose
`FadeInTime=0, Loop=true` are meaningless — confirmed). Reproducing them faithfully
requires the actual AnimatorController, not a re-import of motion3.json.

## Command → code flow (all in Project.Novel)
ADV script (UTF-8 `.bytes`, comma-separated) → `NovelCmd*` → logic → model → controller → object → animator:

- `l2dshow,<model>` → `NovelCmdL2dShow` → `NovelModelLive2D.Load(objName, modelName, isDraw)`
  → `NovelLive2DController.LoadPrefabAsync` → `NovelLive2DObject.CreateL2dObjecct(objName, parent)`
  (instantiates the model **prefab**, Addressables).
- `l2dmotion,<trigger>` and `asyncl2dmotion,<trigger>,on,STOP,<delay>`
  → `NovelCmdL2dMotion` / `NovelCmdL2dMotionAsync` → `NovelCmdL2dMotionLogic`
  (`OnDelayCommandStart` / `OnEndCommandAction`, RVA 0xAFC27E8 / 0xAFC28A0)
  → `NovelModelLive2D.PlayMotion(objName, trigger, boolValue)` (RVA 0xB0798F0)
  → `NovelLive2DObject.PlayMotion(trigger, bool)` → `NovelLive2DAnimator.Playing(trigger, bool)`.
  - Args read via `NovelArguments.GetString(idx,def)` (trigger) and `GetOn(idx,def)` (boolValue).
  - Real field layout (from scripts): `[0]=cmd [1]=trigger [2]="on" [3]="STOP" [4]=delaySeconds`.
    `boolValue` = the `on` flag; `delay` = field[4] (async schedule delay); `STOP` = literal token.
- `l2dmessage,<who>,<text>,,<voiceCue>` → `NovelCmdL2dMessage : NovelCmdMessage`
  → dialogue + `NovelModelLive2D.PlayLipSync(objName, voiceId)` (lip-sync from the voice).
- `l2dhide` → `NovelCmdL2dHide`/`Async` → `NovelCmdL2dHideLogic.ReleaseLive2D` → `NovelModelLive2D.Release(objName)`.
- LipSync mode: `NovelCmdL2dLipSyncMode` → `NovelModelLive2D.SetLipSyncMode(bool useMultiCharacter)`.

## Triggers and layers
Trigger names from scripts map 1:1 to AnimatorController parameters, spread over the 4 layers:
- **Scene** layer: `Scene02`..`Scene12` (capital S). Intro state `sceneNN` → (controller
  transition) → loop state `sceneNN_loop`. Script fires only `SceneNN`; the loop is automatic.
- **Face** layer: `FaceAngle*`, `EyeOpen*`, `EyeEmotion*`, `MouthEmotion*`, plus their
  **`*Reset`** parameters (`FaceAngleReset`, `EyeOpenReset`, `MouthEmotionReset`,
  `EyebrowEmotionReset`, `EyeEmotionReset`) — these are real Animator **triggers/states**
  that transition the layer back to neutral. *They are not files; the old player skipping
  them was the "expressions pile up / overlap" bug.*
- **Emotion** layer: `ExEmotion*`, `EyebrowEmotion*`, `EyeEmotion*`.
- **Body** layer: body params.
(Exact per-parameter layer/type comes free once the AnimatorController is ripped.)

## Per-model prefab contents (`NovelLive2DObject` fields)
`GameObject _l2dObject`, `NovelLive2DAnimator _l2dAnimator` (wraps `Animator`),
`CubismModel _cubismModel`, `CubismRenderController _cubismRenderController`,
`NovelLive2DLipSync` / `NovelLive2DLipSyncByVoiceSuffix`,
`Live2DMosaicMaterialSetter _live2DMosaicMaterialSetter`. Static `L2DLayer` (sorting layer),
`L2DSortingOrderOffset`.
- **Mosaic** is a **material swap** via `Live2DMosaicMaterialSetter` (NOT drawable hiding) —
  the uncensor toggle = pick the non-mosaic material.

## Where the data actually is
- Live2D content is **remote Addressables** — NOT in the APK (local bundle = 68 KB;
  `assets/bin/Data` has no Cubism/Animator assets).
- The user already downloaded it: **`files/UnityCache/Shared/` = 14,681 bundles, 3.8 GB**
  (LZ4-compressed; that's why plaintext grep finds nothing).
- The existing `dotabyss_extracted/live2d` (132 models, model3/motion3 only) was produced by
  an AssetStudio-style **Live2D-only** export ("Working Mode: Live2D, Motion Export Method:
  MonoBehaviour") which **discards the AnimatorController + prefab + fade assets**. That is
  the missing data — and it is recoverable from the same bundle cache, no new download needed.

## Implications for the Unity rebuild
1. **Asset strategy = rip, don't re-import.** Run **AssetRipper** on `files/UnityCache/Shared/`
   to export the model **prefabs + AnimatorControllers + AnimationClips + CubismFadeController
   setups + materials (incl. mosaic)** into the Unity 6.3 project. This carries the real
   fades/loops/intro→loop/Reset for free.
2. **Code port is thin** (all decompiled here): replicate `NovelLive2DController` (load model
   prefab by name), `NovelLive2DObject`, and `NovelLive2DAnimator.Playing` =
   *find Animator parameter by name == trigger; SetBool(trigger, boolValue) if Bool else
   SetTrigger(trigger)*; plus the `NovelCmd*` ADV interpreter and `NovelArguments` field parsing.
3. **No fade/layer/priority constants to chase in arm64** — they live in the ripped
   AnimatorController. (This is why direct PlayAnimation constants were never the answer.)

## Sizes (for bundling decisions, Phase 2/6)
- live2d models 1.1 GB (132) · audio 16 GB (23,976 wav — re-encode to OGG/Opus ⇒ ~1.5 GB) ·
  images 1.8 GB. Ripped prefabs/controllers must ship as **Unity AssetBundles/Addressables**
  (runtime can't load `.controller`/`.prefab` from loose files); audio/images stay loose,
  loaded from device storage.

## Open items to pin during the port (cheap, from this same dump)
- Exact `NovelArguments` field indices for each `NovelCmd*` (read the `+<OnCommandStartASync>`
  state machines) — confirm trigger idx, boolValue idx, delay idx, and what `STOP` selects.
- `boolValue`/`on`/`STOP` precise meaning for Bool-type params (hold-on vs hold-off).
- `objName` default for single-character scenes (`l2dshow` had only the model field).
- The Scene-layer transition gating (`CanSceneTransition` / `SceneTrigger`) timing.
