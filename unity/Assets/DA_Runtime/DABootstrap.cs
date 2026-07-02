using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Live2D.Cubism.Core;

namespace DA
{
    /// <summary>
    /// Single entry point: builds the camera (with URP post-processing), the cinematic ADV UI,
    /// and wires the engine. Game look = canvas-framed Live2D + bloom/grade/vignette + fade overlay
    /// + AUTO/SKIP/menu. The model/scene/motion pickers live behind the ☰ menu (debug panel).
    /// </summary>
    public class DABootstrap : MonoBehaviour
    {
        private Font _font;
        private Camera _cam;
        private Canvas _canvas;

        private ModelRegistry _registry;
        private AdvSceneList _scenes;
        private CueIndex _cues;
        private L2DStage _stage;
        private AudioRouter _audio;
        private AdvPlayer _player;

        private DAPicker _modelPick, _scenePick, _motionPick;
        private DAPicker _assetPick;                 // "Assets folder" selector (detected roots)
        private InputField _assetField;              // custom asset-root path entry
        private Text _assetRootLabel;                // shows the active asset root
        private readonly List<string> _assetPaths = new List<string>();   // picker row -> full path
        private Text _sayWho, _sayText, _status;
        private Toggle _mosaicTg, _aspectTg, _liteTg;
        private bool _autoOn = true;
        private Image _fade;
        private CanvasGroup _dialogGroup;
        private CanvasGroup _dialogUserGroup;  // user "hide text" toggle, composed over the scripted _dialogGroup
        private bool _dialogHidden;
        private Image _textBg;                  // TEXT button tint reflects shown/hidden
        private Bloom _bloom;                   // toggled off in Lite quality (the big mobile cost)
        private GameObject _debugPanel, _uiRoot;
        private Text _diagText, _hintChevron;
        private Camera _bgCam;                 // clears the letterbox bars black behind the scene camera
        private RectTransform _contentRoot;    // UI parent kept inside the letterboxed content rect
        private Rect _lastCamRect = new Rect(-1, -1, -1, -1);

        private List<string> _modelNames = new List<string>();
        private string _lastSceneModel;
        // picker row -> index into _scenes.items (scenes are shown grouped/sorted by character,
        // not in file order, so playback must map back through this).
        private int[] _sceneOrder = new int[0];

        private IEnumerator Start()
        {
            _font = Font.CreateDynamicFontFromOSFont(
                new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic", "Noto Sans CJK JP", "Arial" }, 22);

            BuildCamera();
            BuildEventSystem();
            BuildCanvas();
            BuildUI();

            // Quality profile: Lite on mobile, Full on desktop; a saved choice overrides the default.
            bool lite = PlayerPrefs.HasKey("da_lite") ? PlayerPrefs.GetInt("da_lite") == 1 : Application.isMobilePlatform;
            ApplyQuality(lite);

            _stage = new GameObject("Stage").AddComponent<L2DStage>();
            _audio = new GameObject("Audio").AddComponent<AudioRouter>();
            _player = gameObject.AddComponent<AdvPlayer>();

            _registry = ModelRegistry.Load();
            if (_registry == null) { SetStatus("ERROR: Resources/ModelRegistry missing"); yield break; }
            _stage.Init(_registry, _cam);

            // framing tuning overrides (for matching the game's default camera): DA_FILL, DA_VBIAS, DA_FOV
            ApplyEnvFloat("DA_FILL", v => _stage.FillScale = v);
            ApplyEnvFloat("DA_VBIAS", v => _stage.VerticalBias = v);
            ApplyEnvFloat("DA_FOV", v => _stage.BaseFov = v);

            _cues = CueIndex.Load();
            _audio.Init(_cues);
            SetStatus($"models={_registry.entries.Count} cues={_cues.Count} loading scenes...");

            yield return ScenesDb.Load(s => _scenes = s);

            _player.stage = _stage;
            _player.audio = _audio;
            _player.onSay = (who, text) => { _sayWho.text = who ?? ""; _sayText.text = text ?? ""; };
            _player.onModelChanged = SyncMotionsAndModelDd;
            // a scene can end on cleanall/hide (model released) and/or a fade-to-black — reload the model
            // and clear the fade so the view isn't blank/black forever
            _player.onSceneEnd = () =>
            {
                SetStatus("scene ended");
                if (_fade) _fade.color = new Color(0, 0, 0, 0);
                if (_dialogGroup) _dialogGroup.alpha = 1f;
                if (!string.IsNullOrEmpty(_lastSceneModel)) { _stage.Load(_lastSceneModel); SyncMotionsAndModelDd(); }
            };
            _player.onFade = (dir, color, sec) => StartCoroutine(FadeRoutine(dir, color, sec));
            _player.onWindow = (on, sec) => StartCoroutine(WindowRoutine(on, sec));
            _player.onUiVisible = on => { if (_uiRoot) _uiRoot.SetActive(on); };

            PopulateModels();
            PopulateScenes();

            var tm = Environment.GetEnvironmentVariable("DA_TEST_MODEL");   // load a specific model at default (closed-mouth) pose
            if (!string.IsNullOrEmpty(tm) && _modelNames.Contains(tm)) LoadModel(tm);
            else if (_modelNames.Count > 0) LoadModel(_modelNames[0]);
            RefreshAssetPicker();
            SetStatus($"ready · models={_registry.entries.Count} · scenes={_scenes.items.Count} · cues={_cues.Count}");

#if UNITY_ANDROID && !UNITY_EDITOR
            // Older Android (<11): ask for read access up front so the public folders are scannable.
            // On 11+ the user grants "All files access" via the ☰ → Assets folder → Grant button.
            if (AndroidSdk() < 30 && !UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.ExternalStorageRead))
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.ExternalStorageRead);
#endif

            var ts = Environment.GetEnvironmentVariable("DA_TEST_SCENE");
            if (!string.IsNullOrEmpty(ts) && int.TryParse(ts, out var si) && _scenes.items.Count > 0)
            {
                si = Mathf.Clamp(si, 0, _scenes.items.Count - 1);   // DA_TEST_SCENE is an items[] index
                int pos = System.Array.IndexOf(_sceneOrder, si);    // -> grouped/sorted picker row
                _scenePick.SetValueWithoutNotify(pos < 0 ? 0 : pos);
                PlaySelectedScene();
            }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DA_DEBUGPANEL")) && _debugPanel)
            {
                _debugPanel.SetActive(true);
                StartCoroutine(ShowDropdownSoon());
            }

            var cap = Environment.GetEnvironmentVariable("DA_CAP");
            if (!string.IsNullOrEmpty(cap)) StartCoroutine(CapAndExit(cap));
            var seq = Environment.GetEnvironmentVariable("DA_CAPSEQ");
            if (!string.IsNullOrEmpty(seq)) StartCoroutine(CapSeq(seq));
        }

        // ---------- cinematic overlays ----------
        private IEnumerator FadeRoutine(string dir, string color, float sec)
        {
            if (_fade == null) yield break;
            Color c = (color != null && color.Equals("White", StringComparison.OrdinalIgnoreCase)) ? Color.white : Color.black;
            float target = (dir != null && dir.Equals("Out", StringComparison.OrdinalIgnoreCase)) ? 1f : 0f;
            float start = _fade.color.a;
            if (sec <= 0f) { _fade.color = new Color(c.r, c.g, c.b, target); yield break; }
            float t = 0f;
            while (t < sec)
            {
                t += Time.deltaTime;
                float a = Mathf.Lerp(start, target, Mathf.Clamp01(t / sec));
                _fade.color = new Color(c.r, c.g, c.b, a);
                yield return null;
            }
            _fade.color = new Color(c.r, c.g, c.b, target);
        }

        private IEnumerator WindowRoutine(bool on, float sec)
        {
            if (_dialogGroup == null) yield break;
            float start = _dialogGroup.alpha, target = on ? 1f : 0f;
            if (sec <= 0f) { _dialogGroup.alpha = target; yield break; }
            float t = 0f;
            while (t < sec) { t += Time.deltaTime; _dialogGroup.alpha = Mathf.Lerp(start, target, t / sec); yield return null; }
            _dialogGroup.alpha = target;
        }

        // Manual "hide the dialogue box" toggle (H / TEXT button). Owns a separate CanvasGroup so it
        // never fights the scripted `window` alpha on _dialogGroup; the AUTO/SKIP/☰ bar stays visible.
        // Advancing is unaffected — AdvPlayer reads Input.* directly, not UI raycasts.
        private void ToggleDialogue()
        {
            _dialogHidden = !_dialogHidden;
            if (_dialogUserGroup != null)
            {
                _dialogUserGroup.alpha = _dialogHidden ? 0f : 1f;
                _dialogUserGroup.blocksRaycasts = !_dialogHidden;
            }
            if (_textBg != null) _textBg.color = _dialogHidden ? UiIdle : UiActive;
            SetStatus(_dialogHidden ? "dialogue hidden" : "dialogue shown");
        }

        // Runtime quality profile (no URP-asset regeneration): Lite drops bloom + render scale for
        // low-end devices; Full keeps the cinematic look. Persisted so the choice sticks per device.
        private void ApplyQuality(bool lite)
        {
            if (_bloom != null) _bloom.active = !lite;
            var urp = (QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline)
                      as UniversalRenderPipelineAsset;
            if (urp != null) urp.renderScale = lite ? 0.7f : 1f;
            Application.targetFrameRate = lite ? 30 : -1;   // Lite caps for battery/thermal; Full = platform default
            PlayerPrefs.SetInt("da_lite", lite ? 1 : 0);
            if (_liteTg != null) _liteTg.SetIsOnWithoutNotify(lite);
            SetStatus(lite ? "quality: Lite (renderScale 0.7, no bloom)" : "quality: Full");
        }

        // ---------- engine actions ----------
        private void LoadModel(string name)
        {
            _player.Stop();
            _stage.Load(name);
            SyncMotionsAndModelDd();
            SetStatus("model: " + name);
        }

        private void SyncMotionsAndModelDd()
        {
            int mi = _modelNames.IndexOf(_stage.CurrentModel);
            if (mi >= 0) _modelPick.SetValueWithoutNotify(mi);
            _motionPick.SetOptions(_stage.MotionNames());
        }

        private void PlaySelectedMotion()
        {
            var name = _motionPick.CurrentText;
            if (string.IsNullOrEmpty(name)) return;
            _stage.PlayTrigger(name, true);
            SetStatus("motion: " + name);
        }

        private void PlaySelectedScene()
        {
            if (_scenes == null || _scenes.items.Count == 0) return;
            int sel = _scenePick.Value;
            if (sel < 0 || sel >= _sceneOrder.Length) return;
            var sc = _scenes.items[_sceneOrder[sel]];
            _lastSceneModel = sc.model;
            _player.autoAdvance = _autoOn;
            _dialogGroup.alpha = 1f;
            _player.Play(sc);
            SetStatus($"scene: {SceneCharName(sc)}  ·  {sc.id} ({sc.model}) · {sc.steps.Count} steps");
        }

        // ---------- assets folder selection ----------
        // Rebuild the picker: current active root first, then the auto-detected candidates, each tagged
        // so it's obvious which actually contain data. Row -> full path is kept in _assetPaths.
        private void RefreshAssetPicker()
        {
            if (_assetPick == null) return;
            _assetPaths.Clear();
            var opts = new List<string>();
            void AddPath(string p)
            {
                if (string.IsNullOrEmpty(p) || _assetPaths.Contains(p)) return;
                _assetPaths.Add(p);
                string tag = !Directory.Exists(p) ? "  (missing)"
                           : DAConfig.LooksLikeAssetRoot(p) ? "  ✓" : "  (no data?)";
                opts.Add(ShortPath(p) + tag);
            }
            AddPath(DAConfig.AssetRoot);                                 // whatever is active now
            foreach (var c in DAConfig.AutoCandidates()) AddPath(c);     // likely/detected spots
            _assetPick.SetOptions(opts);
            int cur = _assetPaths.IndexOf(DAConfig.AssetRoot);
            if (cur >= 0) _assetPick.SetValueWithoutNotify(cur);
            if (_assetRootLabel != null)
                _assetRootLabel.text = DAConfig.AssetRoot +
                    (DAConfig.LooksLikeAssetRoot(DAConfig.AssetRoot) ? "  ✓" : "  (no data found here)");
            if (_assetField != null) _assetField.text = DAConfig.SavedOverride;
        }

        private void ApplyAssetChoice(int i)
        {
            if (i >= 0 && i < _assetPaths.Count) ApplyAssetPath(_assetPaths[i]);
        }

        private void ApplyAssetPath(string path)
        {
            path = (path ?? "").Trim();
            if (string.IsNullOrEmpty(path)) { SetStatus("enter a folder path first"); return; }
            if (!Directory.Exists(path)) { SetStatus("folder not found: " + path); RefreshAssetPicker(); return; }
            DAConfig.SetAssetRoot(path);
            ReloadAfterRootChange();
            SetStatus((DAConfig.LooksLikeAssetRoot(path) ? "assets folder: " : "set (no data found in) ") + DAConfig.AssetRoot);
        }

        private void ResetAssetRoot()
        {
            DAConfig.ClearOverride();
            ReloadAfterRootChange();
            SetStatus("assets folder reset → " + DAConfig.AssetRoot);
        }

        // Re-run everything that reads from the asset root, keeping the current model if it still exists.
        private void ReloadAfterRootChange()
        {
            var keep = _stage != null ? _stage.CurrentModel : null;
            if (_player != null) _player.Stop();
            if (_stage != null) _stage.Release();
            ModelBundles.Reset();                       // drop bundles loaded from the old folder
            _cues = CueIndex.Load();                    // re-read cue_index.json from the new folder
            if (_audio != null) _audio.SetCues(_cues);
            if (!string.IsNullOrEmpty(keep)) LoadModel(keep);
            else if (_modelNames.Count > 0) LoadModel(_modelNames[0]);
            RefreshAssetPicker();
        }

        // Last path segment (+ its parent) so long Android paths stay readable in the narrow picker.
        private static string ShortPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            p = p.Replace('\\', '/').TrimEnd('/');
            int a = p.LastIndexOf('/');
            if (a <= 0) return p;
            int b = p.LastIndexOf('/', a - 1);
            return "…/" + p.Substring(b + 1);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static int AndroidSdk()
        {
            try { using var v = new AndroidJavaClass("android.os.Build$VERSION"); return v.GetStatic<int>("SDK_INT"); }
            catch { return 0; }
        }

        // True once the app can read arbitrary user folders (Android 11+ = "All files access";
        // older = the runtime read-storage permission).
        private static bool HasStorageAccess()
        {
            try
            {
                if (AndroidSdk() >= 30)
                {
                    using var env = new AndroidJavaClass("android.os.Environment");
                    return env.CallStatic<bool>("isExternalStorageManager");
                }
                return UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.ExternalStorageRead);
            }
            catch { return false; }
        }

        // Android 11+: open this app's "All files access" settings page (the only way to read arbitrary
        // user folders). Older: show the runtime read-permission dialog.
        private void RequestStorageAccess()
        {
            if (HasStorageAccess()) { SetStatus("storage access already granted"); RefreshAssetPicker(); return; }
            try
            {
                if (AndroidSdk() >= 30)
                {
                    using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = up.GetStatic<AndroidJavaObject>("currentActivity");
                    using var uriClass = new AndroidJavaClass("android.net.Uri");
                    using var uri = uriClass.CallStatic<AndroidJavaObject>("parse", "package:" + Application.identifier);
                    using var intent = new AndroidJavaObject("android.content.Intent",
                        "android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION", uri);
                    activity.Call("startActivity", intent);
                    SetStatus("grant 'All files access', then pick your folder");
                }
                else
                {
                    UnityEngine.Android.Permission.RequestUserPermission(
                        UnityEngine.Android.Permission.ExternalStorageRead);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DA] storage access request failed: " + e.Message);
                SetStatus("couldn't open storage settings: " + e.Message);
            }
        }
#endif

        // ---------- UI population ----------
        private void PopulateModels()
        {
            _modelNames = _registry.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            _modelPick.SetOptions(_modelNames);
        }

        private void PopulateScenes()
        {
            int n = _scenes.items.Count;
            // A Live2D model is reused across different characters, so the per-scene character is the
            // dominant speaker, not the model id. Group scenes so each character's scenes sit together.
            var names = new string[n];
            var order = new List<int>(n);
            for (int i = 0; i < n; i++) { names[i] = SceneCharName(_scenes.items[i]); order.Add(i); }
            order.Sort((a, b) =>
            {
                int c = string.Compare(names[a], names[b], StringComparison.Ordinal);
                return c != 0 ? c : a.CompareTo(b);   // stable within a character (keeps file order)
            });
            _sceneOrder = order.ToArray();

            var opts = new List<string>(n);
            var seen = new Dictionary<string, int>();
            foreach (int idx in _sceneOrder)
            {
                string nm = names[idx];
                seen.TryGetValue(nm, out int k); seen[nm] = ++k;
                opts.Add($"{nm}  ·  {k}");
            }
            _scenePick.SetOptions(opts);
        }

        // The character a scene is about = its most-frequent named speaker (say.who); fall back to the
        // first loaded chara (charaload.who), then the model id. Validated against the scene scripts.
        private static string SceneCharName(AdvScene s)
        {
            if (s == null || s.steps == null) return "?";
            var counts = new Dictionary<string, int>();
            string top = null; int topN = 0;
            foreach (var st in s.steps)
            {
                if (st == null || st.op != "say" || string.IsNullOrEmpty(st.who)) continue;
                counts.TryGetValue(st.who, out int c); counts[st.who] = ++c;
                if (c > topN) { topN = c; top = st.who; }
            }
            if (top != null) return top;
            foreach (var st in s.steps)
                if (st != null && st.op == "charaload" && !string.IsNullOrEmpty(st.who)) return st.who;
            return string.IsNullOrEmpty(s.model) ? "?" : s.model;
        }

        // ---------- UI construction ----------
        private void BuildCamera()
        {
            var go = new GameObject("MainCamera");
            _cam = go.AddComponent<Camera>();
            // The game's cinematic "dot camera" (Novel.unity / NovelDotCameraView) is a perspective
            // rig with a gentle ~43.5mm telephoto: vertical FOV 26.268° (DotCameraFieldView). That
            // mild compression is what gives the H-scenes their filmic, flattened look. L2DStage owns
            // the distance/aim — it cover-fits each model's canvas so off-stage is never visible — and
            // drives fieldOfView for camzoom; the value set here is only the zoom-1 default.
            //
            // The game also applies a physical lensShift of -0.8 for the off-center crop, but that
            // constant is calibrated to the game's own content placement and would reveal off-stage on
            // our differently-centered ripped canvases. We reproduce the same high-subject composition
            // content-relatively in L2DStage.SolveFraming (biasing the aim within the canvas), which is
            // projection-identical for the flat Cubism plane yet can never push the frame off-canvas.
            _cam.orthographic = false;
            _cam.usePhysicalProperties = false;
            _cam.fieldOfView = 26.268f;                     // vertical FOV; L2DStage re-drives per zoom
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 200f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.05f, 0.03f, 0.05f, 1f);
            _cam.transform.position = new Vector3(0, 0, -10);
            go.tag = "MainCamera";
            go.AddComponent<AudioListener>();

            var camData = _cam.GetUniversalAdditionalCameraData();
            if (camData != null) camData.renderPostProcessing = true;

            // Background camera: clears the whole screen black so the letterbox/pillarbox bars are clean
            // when the scene camera renders into a sub-rect under aspect lock. Depth below the main camera.
            var bgGO = new GameObject("LetterboxCamera");
            _bgCam = bgGO.AddComponent<Camera>();
            _bgCam.depth = _cam.depth - 1f;
            _bgCam.clearFlags = CameraClearFlags.SolidColor;
            _bgCam.backgroundColor = Color.black;
            _bgCam.cullingMask = 0;
            _bgCam.orthographic = true;
            _bgCam.rect = new Rect(0f, 0f, 1f, 1f);
            var bgData = _bgCam.GetUniversalAdditionalCameraData();
            if (bgData != null) { bgData.renderType = CameraRenderType.Base; bgData.renderPostProcessing = false; }

            // global post-processing — SUBTLE: the warm/pink mood is mostly painted into the art,
            // so grade lightly. Heavy bloom/exposure blows out bright skin into white blobs.
            var volGO = new GameObject("PostVolume");
            var vol = volGO.AddComponent<Volume>();
            vol.isGlobal = true;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            vol.profile = profile;
            // Match the game: bright, pink, soft glow, light vignette. Bloom threshold stays high
            // (only true highlights glow → no blown-out skin blobs); brightness/pink come from the grade.
            // The pink/punchy mood comes from SATURATION + colorFilter (no washout there). Brightness
            // and bloom are what blow bright skin/face highlights to white, so keep exposure neutral and
            // bloom on a high threshold + modest intensity → only true speculars glow, faces stay readable.
            _bloom = profile.Add<Bloom>(true);
            _bloom.intensity.Override(0.42f); _bloom.threshold.Override(1.30f); _bloom.scatter.Override(0.80f);
            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.0f); color.contrast.Override(8f);
            color.colorFilter.Override(new Color(1.0f, 0.85f, 0.90f)); color.saturation.Override(22f);
            var vig = profile.Add<Vignette>(true);
            vig.intensity.Override(0.16f); vig.smoothness.Override(0.62f); vig.color.Override(new Color(0.1f, 0.03f, 0.06f));
        }

        private void BuildEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }

        private void BuildCanvas()
        {
            var go = new GameObject("Canvas");
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var sc = go.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1280, 720);
            sc.matchWidthOrHeight = 1f; // match height (the framing axis)
            go.AddComponent<GraphicRaycaster>();
        }

        private void BuildUI()
        {
            // Content root: all in-frame UI (dialogue + top buttons) lives inside the letterboxed content
            // rect so controls hug the framed image like the game (synced to _cam.rect in Update()).
            var crGO = new GameObject("ContentRoot");
            crGO.transform.SetParent(_canvas.transform, false);
            _contentRoot = crGO.AddComponent<RectTransform>();
            _contentRoot.anchorMin = Vector2.zero; _contentRoot.anchorMax = Vector2.one;
            _contentRoot.offsetMin = Vector2.zero; _contentRoot.offsetMax = Vector2.zero;

            // ----- user "hide dialogue" wrapper: a full-rect CanvasGroup the TEXT toggle owns, composed
            //       over the scripted _dialogGroup so a manual hide never fights the `window`/scene alpha. -----
            var dlgWrapGO = new GameObject("DialogueWrap");
            dlgWrapGO.transform.SetParent(_contentRoot, false);
            var dlgWrapRT = dlgWrapGO.AddComponent<RectTransform>();
            _dialogUserGroup = dlgWrapGO.AddComponent<CanvasGroup>();
            dlgWrapRT.anchorMin = Vector2.zero; dlgWrapRT.anchorMax = Vector2.one;
            dlgWrapRT.offsetMin = Vector2.zero; dlgWrapRT.offsetMax = Vector2.zero;

            // ----- dialogue (bottom): text floats over a soft bottom gradient with a glow outline,
            //       speaker above, left-positioned like the game. CanvasGroup = `window on/off`. -----
            var dlgGO = new GameObject("Dialogue");
            dlgGO.transform.SetParent(dlgWrapGO.transform, false);
            _dialogGroup = dlgGO.AddComponent<CanvasGroup>();
            var dlgRT = dlgGO.AddComponent<RectTransform>();
            dlgRT.anchorMin = new Vector2(0, 0); dlgRT.anchorMax = new Vector2(1, 0);
            dlgRT.offsetMin = new Vector2(0, 0); dlgRT.offsetMax = new Vector2(0, 250);
            var dlgImg = dlgGO.AddComponent<Image>();
            // soft bottom gradient (transparent at top → dark at the very bottom), not a hard box
            dlgImg.sprite = MakeVGradient(64, 0f, 0.62f, Color.black);
            dlgImg.type = Image.Type.Simple; dlgImg.color = Color.white;

            _sayWho = Label(dlgRT, "Who", new Vector2(0, 0), new Vector2(0, 0), new Vector2(232, 130), new Vector2(620, 34), 24, TextAnchor.LowerLeft);
            _sayWho.color = new Color(1f, 0.86f, 0.62f); _sayWho.fontStyle = FontStyle.Bold;
            AddOutline(_sayWho);
            _sayText = Label(dlgRT, "Text", new Vector2(0, 0), new Vector2(0, 0), new Vector2(232, 36), new Vector2(840, 90), 27, TextAnchor.UpperLeft);
            _sayText.color = Color.white;
            AddOutline(_sayText);
            // blinking down-chevron "waiting for click" indicator (bottom-right), like the game
            _hintChevron = Label(dlgRT, "Hint", new Vector2(1, 0), new Vector2(1, 0), new Vector2(-28, 18), new Vector2(40, 30), 24, TextAnchor.LowerRight);
            _hintChevron.text = "▽"; _hintChevron.color = new Color(1f, 1f, 1f, 0.7f);
            AddOutline(_hintChevron);

            // ----- top-right game controls (AUTO ▶ / SKIP » / ☰), toggled by `uivisible` -----
            _uiRoot = new GameObject("UIRoot");
            _uiRoot.transform.SetParent(_contentRoot, false);
            var uiRT = _uiRoot.AddComponent<RectTransform>();
            uiRT.anchorMin = new Vector2(0, 1); uiRT.anchorMax = new Vector2(1, 1);
            uiRT.offsetMin = new Vector2(0, -64); uiRT.offsetMax = new Vector2(0, -8);

            var idle = UiIdle;
            var active = UiActive;
            float rx = -16; // from right edge
            GameButton(uiRT, "☰", ref rx, 54, () =>
            {
                if (!_debugPanel) return;
                bool show = !_debugPanel.activeSelf;
                _debugPanel.SetActive(show);
                if (!show && _diagText) _diagText.text = "";   // never leave a diag label in the clean view
            }, out _);
            Image skipBg; var skipBtn = GameButton(uiRT, "SKIP »", ref rx, 116, null, out skipBg);
            skipBtn.onClick.AddListener(() => { _player.skip = !_player.skip; skipBg.color = _player.skip ? active : idle; });
            Image autoBg; var autoBtn = GameButton(uiRT, "AUTO ▶", ref rx, 116, null, out autoBg);
            autoBg.color = active; _autoOn = true;
            autoBtn.onClick.AddListener(() => { _autoOn = !_autoOn; if (_player != null) _player.autoAdvance = _autoOn; autoBg.color = _autoOn ? active : idle; });
            // TEXT: hide/show the dialogue box (stays visible when hidden, so it's the way back on touch)
            GameButton(uiRT, "TEXT", ref rx, 96, ToggleDialogue, out _textBg);
            _textBg.color = active;   // dialogue shown by default

            // ----- debug panel (pickers), hidden behind ☰ -----
            BuildDebugPanel();

            // ----- diagnostic label (top-center), for the [ ] drawable-isolation tool -----
            _diagText = Label((RectTransform)_canvas.transform, "Diag", new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -64), new Vector2(900, 40), 22, TextAnchor.UpperCenter);
            _diagText.color = new Color(1f, 0.95f, 0.4f); _diagText.fontStyle = FontStyle.Bold;
            AddOutline(_diagText); _diagText.text = "";

            // ----- full-screen fade overlay (top-most) -----
            var fadeGO = new GameObject("Fade");
            fadeGO.transform.SetParent(_canvas.transform, false);
            _fade = fadeGO.AddComponent<Image>();
            _fade.color = new Color(0, 0, 0, 0);
            _fade.raycastTarget = false;
            var fRT = (RectTransform)_fade.transform;
            fRT.anchorMin = Vector2.zero; fRT.anchorMax = Vector2.one;
            fRT.offsetMin = Vector2.zero; fRT.offsetMax = Vector2.zero;
        }

        private void BuildDebugPanel()
        {
            _debugPanel = new GameObject("DebugPanel");
            // Parent to the letterboxed content root (not the raw canvas) so the panel stays aligned
            // with the top-bar controls; otherwise a top letterbox bar slides the bar down onto it.
            _debugPanel.transform.SetParent(_contentRoot, false);
            var img = _debugPanel.AddComponent<Image>();
            img.color = new Color(0.1f, 0.1f, 0.13f, 0.94f);
            var rt = (RectTransform)_debugPanel.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(12, -64);
            rt.sizeDelta = new Vector2(380, 250);

            float y = -12;
            _modelPick = new DAPicker(rt, _font, y, 30); y -= 36;
            _modelPick.onChanged = i => LoadModel(_modelNames[i]);
            _scenePick = new DAPicker(rt, _font, y, 30); y -= 36;
            Row2(rt, ref y, "▶ Scene", PlaySelectedScene, "■ Stop", () =>
            {
                _player.Stop();
                if (_fade) _fade.color = new Color(0, 0, 0, 0);
                _dialogGroup.alpha = 1f;
                if (_stage.Current == null && !string.IsNullOrEmpty(_lastSceneModel)) { _stage.Load(_lastSceneModel); SyncMotionsAndModelDd(); }
                SetStatus("stopped");
            });
            _motionPick = new DAPicker(rt, _font, y, 30); y -= 36;
            Row2(rt, ref y, "▶ Motion", PlaySelectedMotion, "Mosaic", null, out _mosaicTg);
            StyleToggle(_mosaicTg, "Mosaic");
            _mosaicTg.onValueChanged.AddListener(on => { _stage.SetMosaic(on); SetStatus(on ? "mosaic on" : "uncensored"); });
            Row2(rt, ref y, "Fullscreen (F11)", ToggleFullscreen, "↻ Reload", () => { if (!string.IsNullOrEmpty(_stage.CurrentModel)) LoadModel(_stage.CurrentModel); });
            Row2(rt, ref y, "↺ Recenter", () => _stage.ReFrame(), "Lock Aspect", null, out _aspectTg);
            StyleToggle(_aspectTg, "Lock Aspect");
            _aspectTg.SetIsOnWithoutNotify(true);   // default = locked to the game aspect (20:9)
            _aspectTg.onValueChanged.AddListener(on => { _stage.SetLockAspect(on); SetStatus(on ? "aspect: game (20:9)" : "aspect: free"); });
            // performance profile toggle (full-width): Lite drops bloom + render scale for low-end devices.
            // ApplyQuality() syncs its on-state (default set by platform in Start), so no SetIsOnWithoutNotify here.
            {
                var tg = DefaultControls.CreateToggle(Res); tg.transform.SetParent(rt, false);
                var tgRT = (RectTransform)tg.transform;
                tgRT.anchorMin = new Vector2(0, 1); tgRT.anchorMax = new Vector2(1, 1); tgRT.pivot = new Vector2(0.5f, 1);
                tgRT.offsetMin = new Vector2(12, 0); tgRT.offsetMax = new Vector2(-12, 0);
                tgRT.sizeDelta = new Vector2(tgRT.sizeDelta.x, 30); tgRT.anchoredPosition = new Vector2(tgRT.anchoredPosition.x, y);
                _liteTg = tg.GetComponent<Toggle>();
                StyleToggle(_liteTg, "Lite (performance)");
                _liteTg.onValueChanged.AddListener(ApplyQuality);
                y -= 36;
            }

            // ----- assets folder selector: choose where the extracted data (audio/bundles) lives.
            //       On Android the app-specific folder is hard to reach, so let the user point the app
            //       at Download/ etc.; the choice is remembered (DAConfig → PlayerPrefs). -----
            {
                var hdr = Label(rt, "AssetsHdr", new Vector2(0, 1), new Vector2(1, 1),
                    new Vector2(12, y), new Vector2(-12, 20), 13, TextAnchor.LowerLeft);
                hdr.text = "Assets folder"; hdr.color = new Color(1f, 0.86f, 0.62f); hdr.fontStyle = FontStyle.Bold;
                y -= 22;

                _assetRootLabel = Label(rt, "AssetPath", new Vector2(0, 1), new Vector2(1, 1),
                    new Vector2(12, y), new Vector2(-12, 34), 12, TextAnchor.UpperLeft);
                _assetRootLabel.color = new Color(0.8f, 0.85f, 0.9f);
                y -= 38;

                _assetPick = new DAPicker(rt, _font, y, 30); y -= 36;
                _assetPick.onChanged = ApplyAssetChoice;

                _assetField = InputRow(rt, ref y, "custom folder path…");
                Row2(rt, ref y, "Apply path", () => ApplyAssetPath(_assetField != null ? _assetField.text : null),
                                "Reset", ResetAssetRoot);
#if UNITY_ANDROID && !UNITY_EDITOR
                Row1(rt, ref y, "Grant storage access", RequestStorageAccess);
#endif
            }

            _status = Label(rt, "Status", new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, y - 4), new Vector2(-12, 40), 13, TextAnchor.UpperLeft);

            rt.sizeDelta = new Vector2(380, -y + 44);
            _debugPanel.SetActive(false); // start in clean game-look; ☰ opens it
        }

        private static void ApplyEnvFloat(string key, Action<float> set)
        {
            var s = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(s) && float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) set(v);
        }

        private void SetStatus(string s) { if (_status) _status.text = s; Debug.Log("[DA] " + s); }
        private void Diag(string s) { if (_diagText) _diagText.text = s; if (_status) _status.text = s; Debug.Log("[DA] " + s); }

        private IEnumerator ShowDropdownSoon()
        {
            yield return new WaitForSeconds(1.5f);
            _modelPick?.Toggle();
        }

        private int _diagIdx = -1;
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F11) ||
                ((Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) && Input.GetKeyDown(KeyCode.Return)))
                ToggleFullscreen();

            // H hides/shows the dialogue box (the AUTO/SKIP/☰ bar stays); taps still advance.
            if (Input.GetKeyDown(KeyCode.H)) ToggleDialogue();

            SyncContentRect();   // keep the in-frame UI inside the (possibly letterboxed) content rect

            // gentle blink on the "click to continue" chevron
            if (_hintChevron != null)
            {
                var c = _hintChevron.color;
                c.a = 0.35f + 0.4f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 2.2f));
                _hintChevron.color = c;
            }

            // G toggles the game-aspect lock (letterbox) vs fill-window
            if (Input.GetKeyDown(KeyCode.G) && _stage != null)
            {
                bool on = !_stage.LockAspect;
                _stage.SetLockAspect(on);
                if (_aspectTg) _aspectTg.SetIsOnWithoutNotify(on);
                SetStatus(on ? "aspect: game (20:9)" : "aspect: free");
            }

            // drawable-isolation diagnostics are dev-only: gated behind the debug panel being open so their
            // on-screen labels ("playing"/"HIDE …") never leak into the clean game view.
            if (_stage != null && _stage.Current != null && _debugPanel != null && _debugPanel.activeSelf)
            {
                if (Input.GetKeyDown(KeyCode.P)) { _stage.TogglePause(); Diag(_stage.Paused ? "PAUSED (P)" : "playing"); }
                else if (Input.GetKeyDown(KeyCode.RightBracket)) { _stage.DiagActive = true; _diagIdx++; Diag("HIDE " + _stage.IsolateHide(_diagIdx)); }
                else if (Input.GetKeyDown(KeyCode.LeftBracket)) { _stage.DiagActive = true; _diagIdx--; Diag("HIDE " + _stage.IsolateHide(_diagIdx)); }
                else if (Input.GetKeyDown(KeyCode.Backslash)) { _diagIdx = -1; _stage.DiagActive = false; _stage.ShowAllDrawables(); Diag(""); }
            }
        }

        // Sync the content-UI root to the scene camera's (possibly letterboxed) viewport rect, so the
        // dialogue and top buttons hug the framed image instead of the raw window edges.
        private void SyncContentRect()
        {
            if (_cam == null || _contentRoot == null || _cam.rect == _lastCamRect) return;
            _lastCamRect = _cam.rect;
            _contentRoot.anchorMin = _cam.rect.min;
            _contentRoot.anchorMax = _cam.rect.max;
            _contentRoot.offsetMin = Vector2.zero; _contentRoot.offsetMax = Vector2.zero;
        }

        private void ToggleFullscreen()
        {
            if (Screen.fullScreen)
                Screen.SetResolution(1440, 648, FullScreenMode.Windowed);   // 20:9 game aspect
            else
            {
                var r = Screen.currentResolution;
                Screen.SetResolution(r.width, r.height, FullScreenMode.FullScreenWindow);
            }
        }

        // ---------- uGUI helpers ----------
        private static readonly DefaultControls.Resources Res = new DefaultControls.Resources();
        // game-control button tints: idle (translucent white) vs active/on (warm pink)
        private static readonly Color UiIdle = new Color(1f, 1f, 1f, 0.72f);
        private static readonly Color UiActive = new Color(1f, 0.74f, 0.84f, 0.95f);

        private void Row2(RectTransform parent, ref float y, string a, UnityEngine.Events.UnityAction ca, string b, UnityEngine.Events.UnityAction cb)
        { Row2(parent, ref y, a, ca, b, cb, out _); }

        private void Row2(RectTransform parent, ref float y, string a, UnityEngine.Events.UnityAction ca, string b, UnityEngine.Events.UnityAction cb, out Toggle toggleB)
        {
            var ba = DefaultControls.CreateButton(Res); ba.transform.SetParent(parent, false);
            var raRT = (RectTransform)ba.transform;
            raRT.anchorMin = new Vector2(0, 1); raRT.anchorMax = new Vector2(0.5f, 1); raRT.pivot = new Vector2(0.5f, 1);
            raRT.offsetMin = new Vector2(12, 0); raRT.offsetMax = new Vector2(-6, 0);
            raRT.sizeDelta = new Vector2(raRT.sizeDelta.x, 30); raRT.anchoredPosition = new Vector2(raRT.anchoredPosition.x, y);
            ba.GetComponentInChildren<Text>().text = a; SetFonts(ba); ba.GetComponent<Button>().onClick.AddListener(ca);

            toggleB = null;
            if (cb != null)
            {
                var bb = DefaultControls.CreateButton(Res); bb.transform.SetParent(parent, false);
                var rbRT = (RectTransform)bb.transform;
                rbRT.anchorMin = new Vector2(0.5f, 1); rbRT.anchorMax = new Vector2(1, 1); rbRT.pivot = new Vector2(0.5f, 1);
                rbRT.offsetMin = new Vector2(6, 0); rbRT.offsetMax = new Vector2(-12, 0);
                rbRT.sizeDelta = new Vector2(rbRT.sizeDelta.x, 30); rbRT.anchoredPosition = new Vector2(rbRT.anchoredPosition.x, y);
                bb.GetComponentInChildren<Text>().text = b; SetFonts(bb); bb.GetComponent<Button>().onClick.AddListener(cb);
            }
            else
            {
                var tg = DefaultControls.CreateToggle(Res); tg.transform.SetParent(parent, false);
                var rbRT = (RectTransform)tg.transform;
                rbRT.anchorMin = new Vector2(0.5f, 1); rbRT.anchorMax = new Vector2(1, 1); rbRT.pivot = new Vector2(0.5f, 1);
                rbRT.offsetMin = new Vector2(6, 0); rbRT.offsetMax = new Vector2(-12, 0);
                rbRT.sizeDelta = new Vector2(rbRT.sizeDelta.x, 30); rbRT.anchoredPosition = new Vector2(rbRT.anchoredPosition.x, y);
                var lbl = tg.GetComponentInChildren<Text>(); if (lbl) lbl.text = b;
                SetFonts(tg); toggleB = tg.GetComponent<Toggle>(); toggleB.isOn = false;
            }
            y -= 36;
        }

        // single full-width panel button
        private void Row1(RectTransform parent, ref float y, string label, UnityEngine.Events.UnityAction onClick)
        {
            var b = DefaultControls.CreateButton(Res); b.transform.SetParent(parent, false);
            var r = (RectTransform)b.transform;
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(1, 1); r.pivot = new Vector2(0.5f, 1);
            r.offsetMin = new Vector2(12, 0); r.offsetMax = new Vector2(-12, 0);
            r.sizeDelta = new Vector2(r.sizeDelta.x, 30); r.anchoredPosition = new Vector2(r.anchoredPosition.x, y);
            b.GetComponentInChildren<Text>().text = label; SetFonts(b);
            b.GetComponent<Button>().onClick.AddListener(onClick);
            y -= 36;
        }

        // full-width text entry (opens the native keyboard on mobile); dark text on the default light field
        private InputField InputRow(RectTransform parent, ref float y, string placeholder)
        {
            var go = DefaultControls.CreateInputField(Res); go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(1, 1); r.pivot = new Vector2(0.5f, 1);
            r.offsetMin = new Vector2(12, 0); r.offsetMax = new Vector2(-12, 0);
            r.sizeDelta = new Vector2(r.sizeDelta.x, 30); r.anchoredPosition = new Vector2(r.anchoredPosition.x, y);
            SetFonts(go);
            var f = go.GetComponent<InputField>();
            if (f.textComponent != null) f.textComponent.color = new Color(0.1f, 0.1f, 0.13f);
            if (f.placeholder is Text ph) { ph.text = placeholder; ph.color = new Color(0.4f, 0.4f, 0.45f); ph.fontStyle = FontStyle.Italic; }
            y -= 36;
            return f;
        }

        // game-style rounded translucent control button (white bg, dark bold text), laid out right→left
        private Button GameButton(RectTransform parent, string text, ref float x, float w, UnityEngine.Events.UnityAction onClick, out Image bg)
        {
            var go = new GameObject("Btn", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            bg = go.AddComponent<Image>();
            bg.sprite = Res.standard; bg.type = Image.Type.Sliced;
            bg.color = new Color(1f, 1f, 1f, 0.55f);
            var btn = go.AddComponent<Button>();
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(1, 0.5f); rt.anchorMax = new Vector2(1, 0.5f); rt.pivot = new Vector2(1, 0.5f);
            rt.sizeDelta = new Vector2(w, 40); rt.anchoredPosition = new Vector2(x, 0);
            x -= w + 8;
            var t = new GameObject("Text", typeof(RectTransform)).AddComponent<Text>();
            t.transform.SetParent(go.transform, false);
            t.font = _font; t.fontSize = 20; t.fontStyle = FontStyle.Bold;
            t.alignment = TextAnchor.MiddleCenter; t.color = new Color(0.13f, 0.09f, 0.11f); t.text = text;
            var trt = (RectTransform)t.transform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            if (onClick != null) btn.onClick.AddListener(onClick);
            return btn;
        }

        private Text Label(RectTransform parent, string name, Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 size, int fs, TextAnchor anchor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = _font; t.fontSize = fs; t.alignment = anchor; t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = new Vector2(aMin.x, aMax.y);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            return t;
        }

        private static void AddShadow(Text t)
        {
            var sh = t.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0, 0, 0, 0.9f);
            sh.effectDistance = new Vector2(2, -2);
        }

        // 4-direction outline = soft glow/readability for dialogue text over bright art
        private static void AddOutline(Text t)
        {
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0.15f, 0.02f, 0.08f, 0.95f);
            o.effectDistance = new Vector2(1.6f, -1.6f);
        }

        // 1×N vertical gradient sprite (texture row 0 = bottom). Used for the soft dialogue backdrop.
        private static Sprite MakeVGradient(int h, float topA, float botA, Color col)
        {
            var tex = new Texture2D(1, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++)
            {
                float t = h > 1 ? y / (float)(h - 1) : 0f;     // 0 at bottom row, 1 at top
                float a = Mathf.Lerp(botA, topA, t);
                tex.SetPixel(0, y, new Color(col.r, col.g, col.b, a));
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }

        private void SetFonts(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Text>(true))
            {
                t.font = _font;
                if (t.fontSize < 16) t.fontSize = 18;
            }
        }

        // DefaultControls toggles ship with a dark label + dark checkmark → invisible on the dark panel.
        // Make the label readable (white/bold) and the checkmark a bright fill so on/off is obvious.
        private void StyleToggle(Toggle tg, string label)
        {
            if (tg == null) return;
            SetFonts(tg.gameObject);
            var lbl = tg.GetComponentInChildren<Text>(true);
            if (lbl != null) { lbl.text = label; lbl.color = Color.white; lbl.fontStyle = FontStyle.Bold; lbl.fontSize = 15; }
            if (tg.targetGraphic is Image box) box.color = new Color(0.82f, 0.82f, 0.88f, 1f);   // checkbox background
            if (tg.graphic is Image check) check.color = new Color(0.20f, 0.78f, 0.36f, 1f);      // bright green tick when ON
        }

        // ---------- headless capture ----------
        private IEnumerator CapAndExit(string path)
        {
            yield return new WaitForSeconds(4f);
#if UNITY_EDITOR
            int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24);
            _cam.targetTexture = rt; _cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = null; _cam.targetTexture = null;
            try { File.WriteAllBytes(path, tex.EncodeToPNG()); } catch (Exception e) { Debug.LogError(e.Message); }
            Debug.Log("[DA] PROBE captured=" + path);
            UnityEditor.EditorApplication.Exit(0);
#else
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log("[DA] PROBE screenshot=" + path);
            yield return new WaitForSeconds(2f);
            Application.Quit();
#endif
        }

        private IEnumerator CapSeq(string spec)
        {
            var parts = spec.Split(':');
            int n = parts.Length > 0 && int.TryParse(parts[0], out var pn) ? pn : 8;
            float interval = parts.Length > 1 && float.TryParse(parts[1], out var pi) ? pi : 1f;
            string dir = parts.Length > 2 ? string.Join(":", parts, 2, parts.Length - 2) : ".";
            try { Directory.CreateDirectory(dir); } catch { }
            yield return new WaitForSeconds(0.5f);
            for (int i = 0; i < n; i++)
            {
                yield return new WaitForSeconds(interval);
                string p = Path.Combine(dir, $"seq_{i:00}.png");
                ScreenCapture.CaptureScreenshot(p);
                Debug.Log($"[DA] SEQ frame={i} t={Time.time:0.0} -> {p}");
            }
            yield return new WaitForSeconds(0.6f);
            Application.Quit();
        }
    }
}
