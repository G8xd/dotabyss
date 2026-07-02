using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Live2D.Cubism.Core;
using Live2D.Cubism.Rendering;

namespace DA
{
    /// <summary>
    /// Owns the currently shown Live2D model instance and drives its Animator.
    /// Port of NovelLive2DController/Object + NovelLive2DAnimator.Playing:
    ///   "find the AnimatorController parameter whose name == trigger;
    ///    SetBool(trigger, on) if it's a Bool, else SetTrigger(trigger)."
    /// </summary>
    public class L2DStage : MonoBehaviour
    {
        private ModelRegistry _registry;
        private Camera _cam;

        public GameObject Current { get; private set; }
        public string CurrentModel { get; private set; }
        private Animator _animator;
        private AnimatorControllerParameter[] _params;
        private CubismModel _model;

        // Mosaic drawables (ArtMesh names start with "Mosaic": Mosaic_*, MosaicInsted_*).
        // The real game grab-pixelates these via a base-RT render feature; here we toggle their
        // visibility (hidden = uncensored + removes the red marker; shown = the censorship markers).
        private readonly List<MeshRenderer> _mosaicRenderers = new List<MeshRenderer>();
        public bool MosaicShown { get; private set; }   // default false = uncensored
        public bool HasMosaic => _mosaicRenderers.Count > 0;

        // Fluid/effect drawables (semen, drool, sweat, tears). These stretch with animation and
        // appear at rest in the reconstruction (param defaults differ from the game's base state),
        // showing as stray white streaks. Toggleable; default hidden for a clean look.
        private static readonly string[] FxPrefixes = { "Semen", "Drool", "Sweat", "Tear", "Ase", "Namida", "Yodare", "Shiru", "Juice", "Tear" };
        private readonly List<MeshRenderer> _fxRenderers = new List<MeshRenderer>();
        public bool FxShown { get; private set; } = true;   // fluids are hidden at rest via opacity; show per animation
        public bool HasFx => _fxRenderers.Count > 0;

        // Mouth-interior drawables (Oral*, Tongue*) and the mouth-open parameters that should gate them.
        // The moc3 leaves these ~visible at rest and the clips only animate parameters (no opacity
        // curves), so a closed mouth still shows a white patch. We recreate the missing link in code:
        // show the mouth interior only while a mouth-open parameter is non-trivial.
        private readonly List<MeshRenderer> _mouthRenderers = new List<MeshRenderer>();
        private readonly List<CubismParameter> _mouthParams = new List<CubismParameter>();
        public bool MouthGate = true;          // auto-hide mouth interior when mouths are closed
        public bool DiagActive;                // when the isolate-diagnostic is driving visibility, pause the gate

        // Cinematic camera state. Base = the model's CANVAS frame (the artist's intended composition);
        // per-cut camzoom/cammove ride on top of it. Set cinematic=false for the debug fit-to-bounds view.
        public bool Cinematic = true;
        private Vector3 _baseCenter;     // canvas-center world position (model plane)
        private float _ppu = 1000f;      // model pixels-per-unit (for design-pixel ops)
        private float _camZoom = 1f;
        private Vector2 _camOffset = Vector2.zero;

        // Perspective dot-camera framing (mirrors NovelDotCameraView: vertical FOV 26.268°).
        public float BaseFov = 26.268f;            // vertical FOV at zoom 1
        // Composition: where the subject sits within the spare vertical room left by the cover-fit
        // (0 = centered, 1 = frame top flush with the canvas top). 0.5 = centered on the CHARACTER.
        // (An earlier 0.85 "bias up" pushed the frame into the empty headroom above the figure, and
        // FillScale amplified it — the camera ended up pointing at the background, not the character.)
        public float VerticalBias = 0.5f;
        // The game's default dot-camera crops INTO the canvas (the figure fills the frame); a plain
        // canvas cover-fit (1.0) looks zoomed-out by comparison. ~1.3 matches the reference figure size
        // across characters without cropping heads. Override per-run with DA_FILL while tuning.
        public float FillScale = 1.28f;
        private float _dist = 10f;                 // solved camera back-distance
        private float _aimY;                       // world Y the frame is centered on
        private float _cw, _ch;                    // canvas world size (cached for re-framing on resize)
        private float _lastAspect = -1f;

        // Aspect lock: the real game renders at a fixed mobile aspect (reference capture = 2400x1080 =
        // 20:9). When locked we letterbox the camera viewport to that aspect so the crop is deterministic
        // and matches the game on any window; unlocked = fill the window (cover-fit varies with shape).
        // Locking works by shrinking _cam.rect to a centered sub-rect of TargetAspect — Unity then makes
        // _cam.aspect == TargetAspect automatically, so SolveFraming (which reads _cam.aspect) is correct.
        public bool LockAspect = true;
        public float TargetAspect = 2400f / 1080f;
        private int _lastScreenW = -1, _lastScreenH = -1;

        public void Init(ModelRegistry registry, Camera cam)
        {
            _registry = registry;
            _cam = cam;
            ApplyAspectRect();
        }

        /// <summary>Letterbox/pillarbox the camera viewport to TargetAspect (locked) or fill (unlocked).</summary>
        public void ApplyAspectRect()
        {
            if (_cam == null) return;
            _lastScreenW = Screen.width; _lastScreenH = Screen.height;
            if (!LockAspect) { _cam.rect = new Rect(0f, 0f, 1f, 1f); return; }
            float sa = (float)Screen.width / Mathf.Max(1, Screen.height);
            if (sa > TargetAspect)        // window wider than target -> pillarbox (bars left/right)
            {
                float w = TargetAspect / sa;
                _cam.rect = new Rect((1f - w) * 0.5f, 0f, w, 1f);
            }
            else                          // window taller -> letterbox (bars top/bottom)
            {
                float h = sa / TargetAspect;
                _cam.rect = new Rect(0f, (1f - h) * 0.5f, 1f, h);
            }
        }

        public void SetLockAspect(bool on)
        {
            LockAspect = on;
            ApplyAspectRect();
            if (Cinematic && _model != null) { SolveFraming(); ApplyCamera(); }
        }

        /// <summary>Re-apply the letterbox + cover-fit (e.g. after a manual recenter).</summary>
        public void ReFrame()
        {
            ApplyAspectRect();
            if (Cinematic && _model != null) { SolveFraming(); ApplyCamera(); }
        }

        public bool Load(string modelName)
        {
            if (modelName == CurrentModel && Current != null) return true;
            Release();
            var prefab = _registry != null ? _registry.Get(modelName) : null;
            if (prefab == null) prefab = ModelBundles.LoadPrefab(modelName);   // Android: load from device bundles
            if (prefab == null) { Debug.LogWarning("[DA] model not available: " + modelName); return false; }

            Current = Instantiate(prefab, transform);
            Current.transform.localPosition = Vector3.zero;
            CurrentModel = modelName;
            _model = Current.GetComponent<CubismModel>();
            _animator = Current.GetComponent<Animator>();
            _params = _animator != null ? _animator.parameters : new AnimatorControllerParameter[0];

            ResetAllTriggers();
            FixMaterials();
            CollectMosaic();
            CollectFx();
            CollectAll();
            CollectMouth();
            _camZoom = 1f; _camOffset = Vector2.zero;
            Current.transform.localScale = Vector3.one;
            StartCoroutine(FrameWhenReady());
            return true;
        }

        // ---- per-cut camera / character transforms (design-pixel units, like the scripts) ----
        public void CamZoom(float zoom) { _camZoom = zoom <= 0f ? 1f : zoom; ApplyCamera(); }
        public void CamMove(float xPx, float yPx, bool add)
        {
            var o = new Vector2(xPx, yPx) / _ppu;
            _camOffset = add ? _camOffset + o : o;
            ApplyCamera();
        }
        public void ModelMove(float xPx, float yPx, bool add)
        {
            if (Current == null) return;
            var p = new Vector3(xPx / _ppu, yPx / _ppu, 0f);
            Current.transform.localPosition = add ? Current.transform.localPosition + p : p;
        }
        public void ModelScale(float s)
        {
            if (Current != null && s > 0f) Current.transform.localScale = new Vector3(s, s, 1f);
        }

        private void ApplyCamera()
        {
            if (_cam == null) return;
            _cam.orthographic = false;
            _cam.fieldOfView = BaseFov / _camZoom;     // camzoom → FOV (dolly-in feel), like the game
            _cam.transform.position = new Vector3(
                _baseCenter.x + _camOffset.x,
                _aimY + _camOffset.y,
                _baseCenter.z - _dist);
        }

        private void CollectMosaic()
        {
            _mosaicRenderers.Clear();
            if (Current == null) return;
            foreach (var cr in Current.GetComponentsInChildren<CubismRenderer>(true))
            {
                // Match censorship drawables case-INSENSITIVELY anywhere in the name. The moc3s name
                // these inconsistently: Mosaic_NN, MosaicInsted_NN, MosaicInvert_NN, the lowercase
                // mosaic03, and Glue__Mosaic_NN__ArtMesh*. A case-sensitive StartsWith("Mosaic")
                // missed the lowercase/Glue variants, leaving their flat red marker visible.
                if (cr.gameObject.name.IndexOf("mosaic", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                var mr = cr.GetComponent<MeshRenderer>();
                if (mr != null) _mosaicRenderers.Add(mr);
            }
            ApplyMosaic();
        }

        private void ApplyMosaic()
        {
            // Deactivate the whole drawable GameObject: Cubism's CubismRenderController re-applies
            // MeshRenderer.enabled from each drawable's IsVisible flag every update, so toggling
            // mr.enabled alone does not stick. SetActive is order-independent and Cubism won't
            // reactivate it.
            foreach (var mr in _mosaicRenderers)
                if (mr != null) mr.gameObject.SetActive(MosaicShown);
        }

        /// <summary>Show/hide the mosaic drawables. show=false → uncensored (default).</summary>
        public void SetMosaic(bool show)
        {
            MosaicShown = show;
            ApplyMosaic();
        }

        // ---- diagnostic: isolate-hide one drawable at a time to identify a stray mark ----
        private readonly List<MeshRenderer> _allDrawables = new List<MeshRenderer>();
        private void CollectAll()
        {
            _allDrawables.Clear();
            if (Current == null) return;
            foreach (var cr in Current.GetComponentsInChildren<CubismRenderer>(true))
            {
                var mr = cr.GetComponent<MeshRenderer>();
                if (mr != null) _allDrawables.Add(mr);
            }
            _allDrawables.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        }

        public int DrawableCount => _allDrawables.Count;

        /// <summary>Re-show everything (respecting mosaic/fx), then hide only drawable[idx]. Returns its name.</summary>
        public string IsolateHide(int idx)
        {
            foreach (var mr in _allDrawables) if (mr != null) mr.gameObject.SetActive(true);
            ApplyMosaic(); ApplyFx();
            if (_allDrawables.Count == 0) return "(none)";
            idx = ((idx % _allDrawables.Count) + _allDrawables.Count) % _allDrawables.Count;
            var t = _allDrawables[idx];
            if (t != null) t.gameObject.SetActive(false);
            return $"{idx}/{_allDrawables.Count}: {(t != null ? t.name : "?")}";
        }

        public void ShowAllDrawables()
        {
            foreach (var mr in _allDrawables) if (mr != null) mr.gameObject.SetActive(true);
            ApplyMosaic(); ApplyFx();
        }

        public bool Paused { get; private set; }
        public void TogglePause()
        {
            Paused = !Paused;
            if (_animator != null) _animator.speed = Paused ? 0f : 1f;
        }

        // Mouth-interior drawables that must hide when the mouth is closed: the reconstruction has no
        // opacity link from a parameter to these, so a closed mouth would still show teeth/inside/tongue
        // (the "stray patch around the mouth"). Conservative — only clear interior names, NEVER the lips/
        // outline/mask (hiding a lip would be a worse artifact). Covers the Oral*/Tongue* models and the
        // ~21 that name the interior Mouth_Inside / Mouth_IN / Mouth_Back / Mouth_Dent / Tooth* / Teeth*.
        private static bool IsMouthInterior(string n)
        {
            var oic = System.StringComparison.OrdinalIgnoreCase;
            if (n.StartsWith("Oral", oic) || n.StartsWith("Tongue", oic) ||
                n.StartsWith("Tooth", oic) || n.StartsWith("Teeth", oic)) return true;
            if (n.StartsWith("Mouth", oic))
            {
                var r = n.Substring(5).TrimStart('_', ' ').ToLowerInvariant();
                if (r.StartsWith("in") || r.StartsWith("back") || r.StartsWith("dent") ||
                    r.StartsWith("tongue")) return true;   // inside/inner/in/inseide(typo)/back/dent/tongue
            }
            return false;
        }

        private void CollectMouth()
        {
            _mouthRenderers.Clear(); _mouthParams.Clear();
            if (Current == null) return;
            foreach (var cr in Current.GetComponentsInChildren<CubismRenderer>(true))
            {
                if (!IsMouthInterior(cr.gameObject.name)) continue;
                var mr = cr.GetComponent<MeshRenderer>();
                if (mr != null) _mouthRenderers.Add(mr);
            }
            if (_model != null)
                foreach (var p in _model.Parameters)
                    if (p.Id.IndexOf("MouthOpen", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _mouthParams.Add(p);
                        p.Value = p.MinimumValue;   // moc3 ships these "open"; neutral should be closed
                    }
            if (_model != null) _model.ForceUpdateNow();
        }

        // Recreate the missing "mouth interior visible only when the mouth is open" link.
        private void LateUpdate()
        {
            // On window resize, re-letterbox (the bar proportions change even though the locked
            // _cam.aspect does not), then re-cover-fit if the effective aspect actually changed.
            if (_cam != null && (Screen.width != _lastScreenW || Screen.height != _lastScreenH))
                ApplyAspectRect();
            if (Cinematic && _model != null && _cam != null && _lastAspect > 0f &&
                Mathf.Abs(_cam.aspect - _lastAspect) > 0.001f)
            {
                SolveFraming();
                ApplyCamera();
            }

            if (!MouthGate || DiagActive || _mouthRenderers.Count == 0 || _mouthParams.Count == 0) return;
            float m = 0f;
            for (int i = 0; i < _mouthParams.Count; i++) if (_mouthParams[i] != null && _mouthParams[i].Value > m) m = _mouthParams[i].Value;
            bool show = m > 0.12f;
            for (int i = 0; i < _mouthRenderers.Count; i++)
            {
                var mr = _mouthRenderers[i];
                if (mr != null && mr.gameObject.activeSelf != show) mr.gameObject.SetActive(show);
            }
        }

        private void CollectFx()
        {
            _fxRenderers.Clear();
            if (Current == null) return;
            foreach (var cr in Current.GetComponentsInChildren<CubismRenderer>(true))
            {
                var n = cr.gameObject.name;
                bool isFx = false;
                for (int i = 0; i < FxPrefixes.Length; i++)
                    if (n.StartsWith(FxPrefixes[i], System.StringComparison.OrdinalIgnoreCase)) { isFx = true; break; }
                if (!isFx) continue;
                var mr = cr.GetComponent<MeshRenderer>();
                if (mr != null) _fxRenderers.Add(mr);
            }
            ApplyFx();
        }

        private void ApplyFx()
        {
            foreach (var mr in _fxRenderers) if (mr != null) mr.gameObject.SetActive(FxShown);
        }

        /// <summary>Show/hide fluid/effect drawables (semen/drool/sweat/tears). Default hidden.</summary>
        public void SetFx(bool show)
        {
            FxShown = show;
            ApplyFx();
        }

        // Cubism builds its meshes at end of frame, so renderer bounds are zero (or transiently
        // huge) on the first frames. Let the model settle, then frame.
        private IEnumerator FrameWhenReady()
        {
            var target = Current;
            for (int i = 0; i < 4 && target != null; i++)
            {
                if (_model != null) _model.ForceUpdateNow();
                if (Cinematic && TryFrameCanvas()) yield break;   // canvas info is ready early
                yield return null;
            }
            for (int i = 0; i < 30 && target != null; i++)
            {
                if (_model != null) _model.ForceUpdateNow();
                if (Cinematic && TryFrameCanvas()) yield break;
                if (!Cinematic && TryFrameCamera()) yield break;
                yield return null;
            }
        }

        // The Cubism CANVAS is the artist's intended composition (smaller than the full drawable
        // bounds, which include off-stage padding). Cover-fit a perspective camera to it so the canvas
        // always fills the screen at any aspect — the artwork beyond the canvas absorbs the overscan,
        // so off-stage is never visible (the game's trick) — then bias the frame up so the subject
        // sits high, matching the reference crop.
        private bool TryFrameCanvas()
        {
            if (_cam == null || _model == null) return false;
            var ci = _model.CanvasInformation;
            if (ci == null || ci.PixelsPerUnit <= 0f || ci.CanvasHeight <= 0f) return false;
            _ppu = ci.PixelsPerUnit;
            _cw = ci.CanvasWidth / _ppu;
            _ch = ci.CanvasHeight / _ppu;
            float cx = (ci.CanvasWidth * 0.5f - ci.CanvasOriginX) / _ppu;
            float cy = (ci.CanvasHeight * 0.5f - ci.CanvasOriginY) / _ppu;
            // canvas center is in the model's local space — offset by the model's world transform
            _baseCenter = Current != null
                ? Current.transform.TransformPoint(new Vector3(cx, cy, 0f))
                : new Vector3(cx, cy, 0f);
            SolveFraming();
            ApplyCamera();
            return true;
        }

        // Solve the camera back-distance so the canvas covers the screen, plus the upward aim bias.
        private void SolveFraming()
        {
            float aspect = _cam.aspect > 0.01f ? _cam.aspect : 9f / 16f;
            _lastAspect = aspect;
            // Non-physical perspective: vertical FOV is fixed, horizontal grows with the aspect.
            float tanV = Mathf.Tan(BaseFov * 0.5f * Mathf.Deg2Rad);
            float tanH = tanV * aspect;
            // Distance at which each canvas axis exactly fills the frame; the nearer one binds (nearer
            // would crop the canvas, farther would reveal off-stage). Take the min → cover-fit.
            float dV = (_ch * 0.5f) / tanV;
            float dW = (_cw * 0.5f) / tanH;
            _dist = Mathf.Min(dV, dW) / Mathf.Max(0.01f, FillScale);
            // Spare vertical room (present when width is the binding axis) → push the frame up.
            float visH = 2f * _dist * tanV;
            float slack = Mathf.Max(0f, _ch - visH);
            _aimY = _baseCenter.y + (VerticalBias - 0.5f) * slack;
        }

        public void Release()
        {
            if (Current != null) Destroy(Current);
            Current = null; CurrentModel = null; _animator = null; _params = null; _model = null;
        }

        /// <summary>NovelLive2DAnimator.Playing — set the named Animator parameter.</summary>
        public void PlayTrigger(string trigger, bool on = true)
        {
            if (_animator == null || _params == null || string.IsNullOrEmpty(trigger)) return;
            for (int i = 0; i < _params.Length; i++)
            {
                if (_params[i].name != trigger) continue;
                if (_params[i].type == AnimatorControllerParameterType.Bool) _animator.SetBool(trigger, on);
                else if (_params[i].type == AnimatorControllerParameterType.Trigger) _animator.SetTrigger(trigger);
                return;
            }
        }

        private void ResetAllTriggers()
        {
            if (_animator == null || _params == null) return;
            foreach (var p in _params)
                if (p.type == AnimatorControllerParameterType.Trigger) _animator.ResetTrigger(p.name);
        }

        /// <summary>Trigger + Bool parameter names — the per-model motion list for the UI.</summary>
        public List<string> MotionNames()
        {
            var list = new List<string>();
            if (_params == null) return list;
            foreach (var p in _params)
                if (p.type == AnimatorControllerParameterType.Trigger || p.type == AnimatorControllerParameterType.Bool)
                    list.Add(p.name);
            list.Sort(System.StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // In editor play mode the saved prefab carries the invisible picking material; swap to the
        // real blend material so it renders. (In a player build runtime assigns it automatically.)
        private void FixMaterials()
        {
            foreach (var cr in Current.GetComponentsInChildren<CubismRenderer>())
            {
                var mr = cr.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                var sh = mr.sharedMaterial != null && mr.sharedMaterial.shader != null ? mr.sharedMaterial.shader.name : null;
                if (mr.sharedMaterial == null || sh == "Live2D Cubism/TransparentPicking")
                    mr.sharedMaterial = cr.SetMaterialFromPicker();
            }
            if (_model != null) _model.ForceUpdateNow();
        }

        private bool TryFrameCamera()
        {
            if (_cam == null || Current == null) return true;
            // At runtime the drawable geometry is in CubismRenderer.Mesh (MeshFilter bounds are
            // editor-only in this SDK), so derive bounds from each renderer's mesh.
            var crs = Current.GetComponentsInChildren<CubismRenderer>();
            bool any = false;
            var b = new Bounds();
            foreach (var cr in crs)
            {
                var mesh = cr.Mesh;
                if (mesh == null) continue;
                var lb = mesh.bounds;
                if (lb.size.sqrMagnitude <= 0f) continue;
                var wb = TransformBounds(cr.transform.localToWorldMatrix, lb);
                if (wb.extents.x > 50f || wb.extents.y > 50f) continue;
                if (!any) { b = wb; any = true; } else b.Encapsulate(wb);
            }
            if (!any || (b.extents.x < 1e-3f && b.extents.y < 1e-3f)) return false; // not built yet
            float aspect = _cam.aspect > 0.01f ? _cam.aspect : 9f / 16f;
            _cam.orthographic = true;
            _cam.transform.position = new Vector3(b.center.x, b.center.y, -10f);
            _cam.orthographicSize = Mathf.Max(b.extents.y, b.extents.x / aspect) * 1.12f;
            Debug.Log($"[DA] frame size={b.size} center={b.center} ortho={_cam.orthographicSize} aspect={aspect}");
            return true;
        }

        private static Bounds TransformBounds(Matrix4x4 m, Bounds b)
        {
            var c = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            var ax = m.MultiplyVector(new Vector3(e.x, 0, 0));
            var ay = m.MultiplyVector(new Vector3(0, e.y, 0));
            var az = m.MultiplyVector(new Vector3(0, 0, e.z));
            var ne = new Vector3(
                Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(c, ne * 2f);
        }
    }
}
