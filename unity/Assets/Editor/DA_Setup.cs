using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Live2D.Cubism.Core;

public static class DA_Setup
{
    const string Dir = "Assets/Models/l2d_10070100032";

    // Diagnostic captures go under <repo>/build/diag (gitignored, regenerable) instead of
    // a machine-specific folder. <repo> = parent of unity/ = parent.parent of dataPath.
    static string DiagDir
    {
        get
        {
            var d = Path.Combine(Directory.GetParent(Application.dataPath).Parent.FullName, "build", "diag");
            Directory.CreateDirectory(d);
            return d;
        }
    }
    static string Diag(string name) => Path.Combine(DiagDir, name);

    public static void WireAndValidate()
    {
        AssetDatabase.Refresh();
        string prefabPath = Dir + "/l2d_10070100032.prefab";
        string ctrlPath = Dir + "/l2d_10070100032.controller";

        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
        Debug.Log("DA_RESULT controller=" + (ctrl != null) + " layers=" + (ctrl != null ? ctrl.layers.Length : -1)
                  + " params=" + (ctrl != null ? ctrl.parameters.Length : -1));

        // attach Animator + controller to the prefab
        var root = PrefabUtility.LoadPrefabContents(prefabPath);
        var anim = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
        anim.runtimeAnimatorController = ctrl;
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        PrefabUtility.UnloadPrefabContents(root);
        Debug.Log("DA_RESULT animator_wired=true");

        // binding test: instantiate, sample scene02 clip, count moved parameters
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        var model = inst.GetComponent<CubismModel>();
        Debug.Log("DA_RESULT cubismModel=" + (model != null) + " paramCount=" + (model != null ? model.Parameters.Length : -1));

        var clipPath = AssetDatabase.FindAssets("t:AnimationClip", new[] { Dir + "/clips" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).StartsWith("scene02"));
        var clip = clipPath != null ? AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath) : null;
        Debug.Log("DA_RESULT scene02clip=" + (clip != null ? Path.GetFileName(clipPath) : "NULL"));

        if (model != null && clip != null)
        {
            // capture defaults
            var before = model.Parameters.ToDictionary(p => p.Id, p => p.Value);
            clip.SampleAnimation(inst, clip.length * 0.5f);
            int moved = model.Parameters.Count(p => Mathf.Abs(p.Value - before[p.Id]) > 1e-4f);
            var sample = model.Parameters.FirstOrDefault(p => p.Id == "ParamAngleX");
            Debug.Log($"DA_RESULT BIND moved={moved}/{model.Parameters.Length} ParamAngleX={(sample!=null?sample.Value.ToString():"n/a")}");
        }
        Object.DestroyImmediate(inst);
        Debug.Log("DA_RESULT done");
    }

    public static void PlayCapture()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10070100032";
        string trig = System.Environment.GetEnvironmentVariable("DA_TRIGGER");
        if (string.IsNullOrEmpty(trig)) trig = "Scene02";
        string outp = System.Environment.GetEnvironmentVariable("DA_OUT");
        if (string.IsNullOrEmpty(outp)) outp = Diag("render_play.png");
        string mdir = "Assets/Models/" + model;
        string prefabPath = mdir + "/" + model + ".prefab";
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);

        var camGO = new GameObject("Cam");
        var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.16f, 0.20f, 1f);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        inst.transform.position = Vector3.zero;
        var cubModel = inst.GetComponent<CubismModel>();
        if (cubModel != null) cubModel.ForceUpdateNow();

        var rends = inst.GetComponentsInChildren<Renderer>();
        var b = new Bounds(Vector3.zero, Vector3.one);
        bool first = true;
        foreach (var r in rends) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }
        int W = 720, H = 1280; float aspect = (float)W / H;
        cam.transform.position = new Vector3(b.center.x, b.center.y, -10f);
        cam.orthographicSize = Mathf.Max(b.extents.y, b.extents.x / aspect) * 1.12f;

        var capGO = new GameObject("Cap");
        var cap = capGO.AddComponent<DA_Capture>();
        cap.cam = cam;
        cap.animator = inst.GetComponent<Animator>();
        cap.trigger = trig;
        cap.wait = 2.5f;
        cap.outPath = outp;

        Debug.Log($"DA_RESULT entering_playmode model={model} trigger={trig} bounds={b.size}");
        EditorApplication.EnterPlaymode();
    }

    public static void ValidateAll()
    {
        var dirs = AssetDatabase.GetSubFolders("Assets/Models");
        int ok = 0, noModel = 0, noClip = 0, zeroMoved = 0;
        var problems = new System.Collections.Generic.List<string>();
        foreach (var dir in dirs)
        {
            string m = Path.GetFileName(dir);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(dir + "/" + m + ".prefab");
            if (prefab == null) { problems.Add(m + ":NOPREFAB"); continue; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var model = inst.GetComponent<CubismModel>();
            if (model == null) { noModel++; problems.Add(m + ":NOMODEL"); Object.DestroyImmediate(inst); continue; }

            var clipPath = AssetDatabase.FindAssets("t:AnimationClip", new[] { dir + "/clips" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => Path.GetFileNameWithoutExtension(p).StartsWith("scene") ? 0 : 1)
                .FirstOrDefault();
            var clip = clipPath != null ? AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath) : null;
            if (clip == null) { noClip++; problems.Add(m + ":NOCLIP"); Object.DestroyImmediate(inst); continue; }

            var before = model.Parameters.ToDictionary(p => p.Id, p => p.Value);
            clip.SampleAnimation(inst, clip.length * 0.5f);
            int moved = model.Parameters.Count(p => before.ContainsKey(p.Id) && Mathf.Abs(p.Value - before[p.Id]) > 1e-4f);
            if (moved == 0) { zeroMoved++; problems.Add($"{m}:ZEROMOVED params={model.Parameters.Length}"); }
            else ok++;
            Object.DestroyImmediate(inst);
        }
        Debug.Log($"DA_RESULT ValidateAll models={dirs.Length} bindOK={ok} noModel={noModel} noClip={noClip} zeroMoved={zeroMoved}");
        foreach (var p in problems) Debug.Log("DA_PROBLEM " + p);
    }

    public static void WireAll()
    {
        AssetDatabase.Refresh();
        var dirs = AssetDatabase.GetSubFolders("Assets/Models");
        int ok = 0, noPrefab = 0, noCtrl = 0, totLayers = 0;
        var problems = new System.Collections.Generic.List<string>();
        foreach (var dir in dirs)
        {
            string m = Path.GetFileName(dir);
            string prefabPath = dir + "/" + m + ".prefab";
            string ctrlPath = dir + "/" + m + ".controller";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) { noPrefab++; problems.Add(m + ":NOPREFAB"); continue; }
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
            if (ctrl == null) { noCtrl++; problems.Add(m + ":NOCTRL"); continue; }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            var anim = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            totLayers += ctrl.layers.Length;
            ok++;
        }
        Debug.Log($"DA_RESULT WireAll models={dirs.Length} ok={ok} noPrefab={noPrefab} noCtrl={noCtrl} avgLayers={(ok>0?(float)totLayers/ok:0):0.0}");
        foreach (var p in problems) Debug.Log("DA_PROBLEM " + p);
        AssetDatabase.SaveAssets();
    }

    public static void SetupURP()
    {
        const string dir = "Assets/Settings";
        if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder("Assets", "Settings");

        // Universal Renderer data + the Cubism URP render feature.
        var rendererData = ScriptableObject.CreateInstance<UnityEngine.Rendering.Universal.UniversalRendererData>();
        AssetDatabase.CreateAsset(rendererData, dir + "/CubismUniversalRenderer.asset");

        var feature = ScriptableObject.CreateInstance<Live2D.Cubism.Rendering.URP.CubismRenderPassFeature>();
        feature.name = "CubismRenderPassFeature";
        rendererData.rendererFeatures.Add(feature);
        AssetDatabase.AddObjectToAsset(feature, rendererData);
        EditorUtility.SetDirty(rendererData);

        // Pipeline asset referencing that renderer.
        var urp = UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset.Create(rendererData);
        AssetDatabase.CreateAsset(urp, dir + "/CubismURP.asset");

        UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = urp;
        QualitySettings.renderPipeline = urp;
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = urp;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        var active = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
        Debug.Log($"DA_RESULT URP_setup pipeline={(active ? active.name : "null")} features={rendererData.rendererFeatures.Count}");
    }

    public static void Diag()
    {
        string prefabPath = Dir + "/l2d_10070100032.prefab";
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        var model = inst.GetComponent<CubismModel>();
        if (model != null) model.ForceUpdateNow();
        var mr = inst.GetComponentsInChildren<MeshRenderer>();
        var mf = inst.GetComponentsInChildren<MeshFilter>();
        int withMesh = mf.Count(f => f.sharedMesh != null && f.sharedMesh.vertexCount > 0);
        Debug.Log($"DA_DIAG meshRenderers={mr.Length} meshFilters={mf.Length} filtersWithMesh={withMesh}");
        if (mr.Length > 0)
        {
            var m = mr[0].sharedMaterial;
            Debug.Log($"DA_DIAG mat={(m ? m.name : "null")} shader={(m && m.shader ? m.shader.name : "null")} enabled={mr[0].enabled}");
        }
        var rcType = System.Type.GetType("Live2D.Cubism.Rendering.CubismRenderController, Live2D.Cubism");
        Debug.Log($"DA_DIAG renderControllerType={(rcType != null)} hasComp={(rcType != null && inst.GetComponentInChildren(rcType) != null)}");
        Debug.Log($"DA_DIAG renderPipeline={(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline.name : "Built-in")}");
        Object.DestroyImmediate(inst);
    }

    // Empirically determine the mosaic semantics: render the model with the "Mosaic*" drawables
    // shown vs hidden, so we can see which state is the clean/uncensored view and which carries
    // the red marker. Writes mosaic_on.png (drawables shown) and mosaic_off.png (drawables hidden).
    public static void MosaicProbe()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10070100032";
        string mdir = "Assets/Models/" + model;
        string prefabPath = mdir + "/" + model + ".prefab";

        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);

        var camGO = new GameObject("Cam");
        var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.16f, 0.20f, 1f);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        inst.transform.position = Vector3.zero;
        var cubModel = inst.GetComponent<CubismModel>();

        // sample a scene clip so the body is in the H pose where the mosaic region is exposed
        var clipPath = AssetDatabase.FindAssets("t:AnimationClip", new[] { mdir + "/clips" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).StartsWith("scene02"));
        if (clipPath != null)
            AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath).SampleAnimation(inst, 1.0f);
        if (cubModel != null) cubModel.ForceUpdateNow();

        int swapped = 0;
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var mr = cr.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            var sh = mr.sharedMaterial != null && mr.sharedMaterial.shader != null ? mr.sharedMaterial.shader.name : null;
            if (mr.sharedMaterial == null || sh == "Live2D Cubism/TransparentPicking")
            { mr.sharedMaterial = cr.SetMaterialFromPicker(); swapped++; }
        }

        // collect the mosaic drawables
        var mosaicRends = new System.Collections.Generic.List<MeshRenderer>();
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            if (!cr.gameObject.name.StartsWith("Mosaic")) continue;
            var mr = cr.GetComponent<MeshRenderer>();
            if (mr != null) mosaicRends.Add(mr);
        }
        Debug.Log($"DA_RESULT MosaicProbe model={model} swapped={swapped} mosaicDrawables={mosaicRends.Count} names=[{string.Join(",", mosaicRends.Select(r => r.name))}]");

        // frame camera
        var rends = inst.GetComponentsInChildren<Renderer>();
        var b = new Bounds(inst.transform.position, Vector3.one);
        bool first = true;
        foreach (var r in rends) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }
        int W = 720, H = 1280; float aspect = (float)W / H;
        cam.transform.position = new Vector3(b.center.x, b.center.y, -10f);
        cam.orthographicSize = Mathf.Max(b.extents.y, b.extents.x / aspect) * 1.1f;

        // shown
        foreach (var mr in mosaicRends) mr.enabled = true;
        CaptureCam(cam, W, H, Diag("mosaic_on.png"));
        // hidden
        foreach (var mr in mosaicRends) mr.enabled = false;
        CaptureCam(cam, W, H, Diag("mosaic_off.png"));
        Debug.Log("DA_RESULT MosaicProbe done on=mosaic_on.png off=mosaic_off.png");
    }

    // Demonstrate the GAME look vs the debug viewer look: a fixed cinematic crop (you do NOT see
    // the whole set / off-stage), widescreen, with URP bloom + warm color grade + vignette.
    // Writes cinematic.png (game-style) and cinematic_full.png (debug fit-to-bounds, for contrast).
    public static void CinematicProbe()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10070100032";
        string mdir = "Assets/Models/" + model;
        string prefabPath = mdir + "/" + model + ".prefab";

        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);

        var camGO = new GameObject("Cam");
        var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.06f, 0.03f, 0.05f, 1f);
        var camData = cam.GetUniversalAdditionalCameraData();
        if (camData != null) camData.renderPostProcessing = true;

        // post-processing volume: bloom + warm grade + vignette (the H-scene mood)
        var volGO = new GameObject("Volume");
        var vol = volGO.AddComponent<Volume>();
        vol.isGlobal = true;
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        vol.profile = profile;
        var bloom = profile.Add<Bloom>(true);
        bloom.intensity.Override(1.1f); bloom.threshold.Override(0.85f); bloom.scatter.Override(0.7f);
        var color = profile.Add<ColorAdjustments>(true);
        color.postExposure.Override(0.3f); color.contrast.Override(12f);
        color.colorFilter.Override(new Color(1.0f, 0.82f, 0.86f)); color.saturation.Override(10f);
        var vig = profile.Add<Vignette>(true);
        vig.intensity.Override(0.42f); vig.smoothness.Override(0.5f); vig.color.Override(new Color(0.25f, 0.05f, 0.12f));

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        inst.transform.position = Vector3.zero;
        var cubModel = inst.GetComponent<CubismModel>();
        var clipPath = AssetDatabase.FindAssets("t:AnimationClip", new[] { mdir + "/clips" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).StartsWith("scene02"));
        if (clipPath != null) AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath).SampleAnimation(inst, 0.4f);
        if (cubModel != null) cubModel.ForceUpdateNow();

        int swapped = 0;
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var mr = cr.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            var sh = mr.sharedMaterial != null && mr.sharedMaterial.shader != null ? mr.sharedMaterial.shader.name : null;
            if (mr.sharedMaterial == null || sh == "Live2D Cubism/TransparentPicking")
            { mr.sharedMaterial = cr.SetMaterialFromPicker(); swapped++; }
            if (cr.gameObject.name.StartsWith("Mosaic")) cr.gameObject.SetActive(false); // uncensored for the demo
        }

        var rends = inst.GetComponentsInChildren<Renderer>();
        var b = new Bounds(inst.transform.position, Vector3.one);
        bool first = true;
        foreach (var r in rends) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }

        // widescreen, like the game (≈2.2:1)
        int W = 1280, H = 582; float aspect = (float)W / H;

        // (1) GAME LOOK: fixed cinematic crop — zoom in on the upper body, let the rest run off-stage.
        cam.transform.position = new Vector3(b.center.x + b.extents.x * 0.12f, b.center.y + b.extents.y * 0.34f, -10f);
        cam.orthographicSize = b.extents.y * 0.58f;
        CaptureCam(cam, W, H, Diag("cinematic.png"));

        // (2) DEBUG LOOK: fit whole model (what the current player shows).
        cam.transform.position = new Vector3(b.center.x, b.center.y, -10f);
        cam.orthographicSize = Mathf.Max(b.extents.y, b.extents.x / aspect) * 1.12f;
        CaptureCam(cam, W, H, Diag("cinematic_full.png"));

        Debug.Log($"DA_RESULT CinematicProbe model={model} swapped={swapped} bounds={b.size} done");
    }

    // Test the hypothesis that the Cubism CANVAS = the intended cinematic frame (not drawable bounds).
    public static void CanvasProbe()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10070100032";
        string mdir = "Assets/Models/" + model;
        string prefabPath = mdir + "/" + model + ".prefab";

        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);

        var camGO = new GameObject("Cam");
        var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.06f, 0.03f, 0.05f, 1f);
        var camData = cam.GetUniversalAdditionalCameraData();
        if (camData != null) camData.renderPostProcessing = true;

        var volGO = new GameObject("Volume");
        var vol = volGO.AddComponent<Volume>(); vol.isGlobal = true;
        var profile = ScriptableObject.CreateInstance<VolumeProfile>(); vol.profile = profile;
        var bloom = profile.Add<Bloom>(true); bloom.intensity.Override(0.9f); bloom.threshold.Override(0.9f);
        var color = profile.Add<ColorAdjustments>(true);
        color.postExposure.Override(0.2f); color.contrast.Override(8f);
        color.colorFilter.Override(new Color(1.0f, 0.88f, 0.9f));
        var vig = profile.Add<Vignette>(true); vig.intensity.Override(0.34f); vig.smoothness.Override(0.5f);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        inst.transform.position = Vector3.zero;
        var cm = inst.GetComponent<CubismModel>();
        var clipPath = AssetDatabase.FindAssets("t:AnimationClip", new[] { mdir + "/clips" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).StartsWith("scene02"));
        if (clipPath != null) AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath).SampleAnimation(inst, 0.4f);
        if (cm != null) cm.ForceUpdateNow();

        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var mr = cr.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            var sh = mr.sharedMaterial != null && mr.sharedMaterial.shader != null ? mr.sharedMaterial.shader.name : null;
            if (mr.sharedMaterial == null || sh == "Live2D Cubism/TransparentPicking") mr.sharedMaterial = cr.SetMaterialFromPicker();
            if (cr.gameObject.name.StartsWith("Mosaic")) cr.gameObject.SetActive(false);
        }

        var ci = cm.CanvasInformation;
        float ppu = ci.PixelsPerUnit;
        float cw = ci.CanvasWidth / ppu, ch = ci.CanvasHeight / ppu;
        float cx = (ci.CanvasWidth * 0.5f - ci.CanvasOriginX) / ppu;
        float cy = (ci.CanvasHeight * 0.5f - ci.CanvasOriginY) / ppu;
        Debug.Log($"DA_RESULT Canvas px=({ci.CanvasWidth}x{ci.CanvasHeight}) origin=({ci.CanvasOriginX},{ci.CanvasOriginY}) ppu={ppu} -> worldSize=({cw:0.00}x{ch:0.00}) worldCenter=({cx:0.00},{cy:0.00})");

        // (1) canvas-aspect render: frame exactly the canvas
        int chH = 1280; int chW = Mathf.RoundToInt(chH * (cw / ch));
        cam.transform.position = new Vector3(cx, cy, -10f);
        cam.orthographicSize = ch * 0.5f;
        CaptureCam(cam, chW, chH, Diag("canvas_native.png"));

        // (2) widescreen (device-like 19.5:9 portrait? game H-scene was landscape ~2.2:1) fit height to canvas
        int W = 1280, H = 582;
        cam.transform.position = new Vector3(cx, cy, -10f);
        cam.orthographicSize = ch * 0.5f;
        CaptureCam(cam, W, H, Diag("canvas_wide.png"));
        Debug.Log("DA_RESULT CanvasProbe done");
    }

    // Isolate the "white blobs" cause: render a model canvas-framed with post OFF then ON.
    public static void LookProbe()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10010100022";
        string mdir = "Assets/Models/" + model;
        string prefabPath = mdir + "/" + model + ".prefab";

        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);

        var camGO = new GameObject("Cam");
        var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.06f, 0.03f, 0.05f, 1f);
        var camData = cam.GetUniversalAdditionalCameraData();
        if (camData != null) camData.renderPostProcessing = false;

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        inst.transform.position = Vector3.zero;
        var cm = inst.GetComponent<CubismModel>();
        var clipPath = AssetDatabase.FindAssets("t:AnimationClip", new[] { mdir + "/clips" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).StartsWith("scene02"))
            ?? AssetDatabase.FindAssets("t:AnimationClip", new[] { mdir + "/clips" }).Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();
        if (clipPath != null) AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath).SampleAnimation(inst, 0.4f);
        if (cm != null) cm.ForceUpdateNow();

        int swapped = 0, whiteish = 0;
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var mr = cr.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            var sh = mr.sharedMaterial != null && mr.sharedMaterial.shader != null ? mr.sharedMaterial.shader.name : null;
            if (mr.sharedMaterial == null || sh == "Live2D Cubism/TransparentPicking")
            { mr.sharedMaterial = cr.SetMaterialFromPicker(); swapped++; }
            if (cr.gameObject.name.StartsWith("Mosaic")) cr.gameObject.SetActive(false);
            var mt = cr.MainTexture;
            if (mt == null) whiteish++;
        }
        Debug.Log($"DA_RESULT LookProbe model={model} swapped={swapped} nullTexDrawables={whiteish}");

        var ci = cm.CanvasInformation; float ppu = ci.PixelsPerUnit;
        float ch = ci.CanvasHeight / ppu;
        float cx = (ci.CanvasWidth * 0.5f - ci.CanvasOriginX) / ppu;
        float cy = (ci.CanvasHeight * 0.5f - ci.CanvasOriginY) / ppu;
        cam.transform.position = new Vector3(cx, cy, -10f);
        cam.orthographicSize = ch * 0.5f;
        int W = 1080, H = 1080;

        // (1) NO post
        CaptureCam(cam, W, H, Diag("look_nopost.png"));

        // (2) WITH post (current settings)
        if (camData != null) camData.renderPostProcessing = true;
        var volGO = new GameObject("Volume"); var vol = volGO.AddComponent<Volume>(); vol.isGlobal = true;
        var profile = ScriptableObject.CreateInstance<VolumeProfile>(); vol.profile = profile;
        var bloom = profile.Add<Bloom>(true); bloom.intensity.Override(0.28f); bloom.threshold.Override(1.15f); bloom.scatter.Override(0.55f);
        var color = profile.Add<ColorAdjustments>(true); color.postExposure.Override(0f); color.contrast.Override(4f); color.colorFilter.Override(new Color(1f, 0.95f, 0.96f)); color.saturation.Override(2f);
        var vig = profile.Add<Vignette>(true); vig.intensity.Override(0.26f); vig.smoothness.Override(0.55f);
        CaptureCam(cam, W, H, Diag("look_post.png"));
        Debug.Log("DA_RESULT LookProbe done");
    }

    // Hunt the animation-driven "white streak": sample a clip across time and flag any drawable whose
    // mesh bounds blow up (a vertex shooting off = a stretched triangle spike). Logs name+size+time.
    public static void ArtifactProbe()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10070100032";
        string mdir = "Assets/Models/" + model;
        string prefabPath = mdir + "/" + model + ".prefab";

        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
        var camGO = new GameObject("Cam"); var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.1f, 0.1f, 0.12f, 1f);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        inst.transform.position = Vector3.zero;
        var cm = inst.GetComponent<CubismModel>();
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var mr = cr.GetComponent<MeshRenderer>(); if (mr == null) continue;
            var sh = mr.sharedMaterial && mr.sharedMaterial.shader ? mr.sharedMaterial.shader.name : null;
            if (mr.sharedMaterial == null || sh == "Live2D Cubism/TransparentPicking") mr.sharedMaterial = cr.SetMaterialFromPicker();
            if (cr.gameObject.name.StartsWith("Mosaic")) cr.gameObject.SetActive(false);
        }

        var clips = AssetDatabase.FindAssets("t:AnimationClip", new[] { mdir + "/clips" }).Select(AssetDatabase.GUIDToAssetPath).ToList();
        var clipPath = clips.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).StartsWith("scene02")) ?? clips.FirstOrDefault();
        var clip = clipPath != null ? AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath) : null;

        var ci = cm.CanvasInformation; float ppu = ci.PixelsPerUnit;
        float canvasHalf = (ci.CanvasHeight / ppu) * 0.5f;
        var seen = new System.Collections.Generic.HashSet<string>();
        // zoom on the faces (upper-center of the canvas)
        cam.transform.position = new Vector3(-0.05f, 0.18f, -10f);
        cam.orthographicSize = 0.34f;

        float[] times = { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f, 1.3f, 1.6f, 2.0f };
        int fi = 0;
        foreach (var t in times)
        {
            if (clip != null) clip.SampleAnimation(inst, t * clip.length / 2.2f);
            cm.ForceUpdateNow();
            foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
            {
                var mesh = cr.Mesh; if (mesh == null) continue;
                var ext = mesh.bounds.extents;
                if (ext.x > canvasHalf * 1.6f || ext.y > canvasHalf * 1.6f)
                {
                    string key = cr.gameObject.name;
                    if (seen.Add(key))
                        Debug.Log($"DA_SPIKE drawable={key} ext=({ext.x:0.0},{ext.y:0.0}) firstAt_t={t:0.0}");
                }
            }
            CaptureCam(cam, 720, 720, Diag($"art_{fi:00}.png"));
            fi++;
        }
        Debug.Log($"DA_RESULT ArtifactProbe model={model} canvasHalf={canvasHalf:0.00} clip={(clip!=null?Path.GetFileName(clipPath):"none")} frames={fi} done");
    }

    // Find the REAL cause of stray fluids: print fluid-related parameters' Min/Max/Default/Value
    // and any parameter whose default is not at its minimum (candidates that show something at rest).
    public static void FluidProbe()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10070100032";
        string mdir = "Assets/Models/" + model;
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mdir + "/" + model + ".prefab"));
        var cm = inst.GetComponent<CubismModel>();
        cm.ForceUpdateNow();
        var fluidWords = new[] { "semen", "drool", "sweat", "ase", "namida", "tear", "yodare", "shiru", "siru", "juice" };
        Debug.Log($"DA_RESULT FluidProbe model={model} params={cm.Parameters.Length}");
        foreach (var p in cm.Parameters)
        {
            string idl = p.Id.ToLowerInvariant();
            bool fluid = false; foreach (var w in fluidWords) if (idl.Contains(w)) { fluid = true; break; }
            if (fluid)
                Debug.Log($"DA_FPARAM {p.Id} min={p.MinimumValue:0.##} max={p.MaximumValue:0.##} def={p.DefaultValue:0.##} val={p.Value:0.##}");
        }
        // also: any parameter whose default sits above its minimum (potential "on at rest")
        int nonMin = 0;
        foreach (var p in cm.Parameters)
            if (p.DefaultValue > p.MinimumValue + 0.001f && p.DefaultValue >= p.MaximumValue - 0.001f)
            { Debug.Log($"DA_DEFMAX {p.Id} def={p.DefaultValue:0.##} (==max) min={p.MinimumValue:0.##}"); nonMin++; }
        Debug.Log($"DA_RESULT FluidProbe defAtMax={nonMin} done");

        // render default vs fluid-params-zeroed (canvas-framed, no post) to confirm the lever
        var camGO = new GameObject("Cam"); var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.1f,0.1f,0.12f,1f);
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var mr = cr.GetComponent<MeshRenderer>(); if (mr == null) continue;
            var sh = mr.sharedMaterial && mr.sharedMaterial.shader ? mr.sharedMaterial.shader.name : null;
            if (mr.sharedMaterial == null || sh == "Live2D Cubism/TransparentPicking") mr.sharedMaterial = cr.SetMaterialFromPicker();
            if (cr.gameObject.name.StartsWith("Mosaic")) cr.gameObject.SetActive(false);
        }
        var ci = cm.CanvasInformation; float ppu = ci.PixelsPerUnit;
        cam.transform.position = new Vector3((ci.CanvasWidth*0.5f-ci.CanvasOriginX)/ppu, (ci.CanvasHeight*0.5f-ci.CanvasOriginY)/ppu, -10f);
        cam.orthographicSize = (ci.CanvasHeight/ppu)*0.5f;
        cm.ForceUpdateNow();
        CaptureCam(cam, 720, 720, Diag("fluid_default.png"));
        // zero all fluid-named params
        int zeroed = 0;
        foreach (var p in cm.Parameters)
        {
            string idl = p.Id.ToLowerInvariant(); bool fl = false;
            foreach (var w in fluidWords) if (idl.Contains(w)) { fl = true; break; }
            if (fl) { p.Value = p.MinimumValue; zeroed++; }
        }
        cm.ForceUpdateNow();
        CaptureCam(cam, 720, 720, Diag("fluid_zeroed.png"));

        // list fluid-named PARTS + their opacity, then zero them and render
        int partsZeroed = 0;
        if (cm.Parts != null)
            foreach (var part in cm.Parts)
            {
                string idl = part.Id.ToLowerInvariant(); bool fl = false;
                foreach (var w in fluidWords) if (idl.Contains(w)) { fl = true; break; }
                if (fl) { Debug.Log($"DA_FPART {part.Id} opacity={part.Opacity:0.##}"); part.Opacity = 0f; partsZeroed++; }
            }
        cm.ForceUpdateNow();
        CaptureCam(cam, 720, 720, Diag("fluid_parts0.png"));
        Debug.Log($"DA_RESULT FluidProbe zeroedParams={zeroed} partsZeroed={partsZeroed} parts={(cm.Parts!=null?cm.Parts.Length:-1)} done2");
        Object.DestroyImmediate(inst);
    }

    // Definitively find which parameter(s) control the DROOL drawables' opacity: sweep each param to
    // min and max, measure drool mesh vertex-alpha, report params that change it. Also report drool's
    // opacity at the model's DEFAULT pose (is it visible at rest?).
    public static void SweepDrool()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10070100032";
        string mdir = "Assets/Models/" + model;
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mdir + "/" + model + ".prefab"));
        var cm = inst.GetComponent<CubismModel>();

        var droolRenderers = new System.Collections.Generic.List<Live2D.Cubism.Rendering.CubismRenderer>();
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
            if (cr.gameObject.name.StartsWith("Drool", System.StringComparison.OrdinalIgnoreCase)) droolRenderers.Add(cr);
        Debug.Log($"DA_RESULT SweepDrool model={model} droolDrawables={droolRenderers.Count} names=[{string.Join(",", droolRenderers.Select(r => r.name))}]");

        System.Func<float> measure = () =>
        {
            cm.ForceUpdateNow();
            float sum = 0; int n = 0;
            foreach (var cr in droolRenderers)
            {
                var m = cr.Mesh; if (m == null) continue;
                var cols = m.colors; if (cols == null || cols.Length == 0) continue;
                float a = 0; for (int i = 0; i < cols.Length; i++) a += cols[i].a; a /= cols.Length;
                sum += a; n++;
            }
            return n > 0 ? sum / n : -1f;
        };

        float baseOp = measure();
        Debug.Log($"DA_RESULT droolOpacityAtDefault={baseOp:0.000}");

        foreach (var p in cm.Parameters)
        {
            float def = p.Value;
            p.Value = p.MinimumValue; float aMin = measure();
            p.Value = p.MaximumValue; float aMax = measure();
            p.Value = def; cm.ForceUpdateNow();
            if (Mathf.Abs(aMax - aMin) > 0.25f)
                Debug.Log($"DA_DROOLCTRL {p.Id} min={p.MinimumValue:0.#}->op{aMin:0.00}  max={p.MaximumValue:0.#}->op{aMax:0.00}  def={def:0.#}");
        }
        Debug.Log("DA_RESULT SweepDrool done");
        Object.DestroyImmediate(inst);
    }

    // Identify the stray white face mark: render the face with all drawables, then with highlight
    // drawables (Nose_Hi*, *Hi*) hidden. Also log each highlight drawable's blend material (additive?).
    public static void HiProbe()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10070100032";
        string mdir = "Assets/Models/" + model;
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
        var cam = new GameObject("Cam").AddComponent<Camera>();
        cam.orthographic = true; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.1f,0.1f,0.12f,1f);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mdir + "/" + model + ".prefab"));
        var cm = inst.GetComponent<CubismModel>();
        var clip = AssetDatabase.FindAssets("t:AnimationClip", new[] { mdir + "/clips" }).Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => Path.GetFileNameWithoutExtension(p).StartsWith("scene02")).Select(p => AssetDatabase.LoadAssetAtPath<AnimationClip>(p)).FirstOrDefault();
        if (clip != null) clip.SampleAnimation(inst, clip.length * 0.25f);
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var mr = cr.GetComponent<MeshRenderer>(); if (mr == null) continue;
            var sh = mr.sharedMaterial && mr.sharedMaterial.shader ? mr.sharedMaterial.shader.name : null;
            if (mr.sharedMaterial == null || sh == "Live2D Cubism/TransparentPicking") mr.sharedMaterial = cr.SetMaterialFromPicker();
            if (cr.gameObject.name.StartsWith("Mosaic") || cr.gameObject.name.StartsWith("Semen") || cr.gameObject.name.StartsWith("Drool")) cr.gameObject.SetActive(false);
        }
        cm.ForceUpdateNow();

        // log highlight drawables + their material (blend)
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var n = cr.gameObject.name;
            if (n.IndexOf("Hi", System.StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Nose", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var mr = cr.GetComponent<MeshRenderer>();
                Debug.Log($"DA_HILITE {n} mat={(mr && mr.sharedMaterial ? mr.sharedMaterial.name : "null")}");
            }
        }

        var ci = cm.CanvasInformation; float ppu = ci.PixelsPerUnit;
        cam.transform.position = new Vector3((ci.CanvasWidth*0.5f-ci.CanvasOriginX)/ppu, (ci.CanvasHeight*0.5f-ci.CanvasOriginY)/ppu + 0.18f, -10f);
        cam.orthographicSize = (ci.CanvasHeight/ppu)*0.32f;
        cm.ForceUpdateNow();
        CaptureCam(cam, 720, 720, Diag("hi_all.png"));

        int hid = 0;
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var n = cr.gameObject.name;
            if (n.StartsWith("Nose_Hi", System.StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("EyeHi", System.StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("Eye_Hi", System.StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("Nosei", System.StringComparison.OrdinalIgnoreCase))
            { cr.gameObject.SetActive(false); hid++; }
        }
        cm.ForceUpdateNow();
        CaptureCam(cam, 720, 720, Diag("hi_hidden.png"));
        Debug.Log($"DA_RESULT HiProbe model={model} highlightsHidden={hid} done");
        Object.DestroyImmediate(inst);
    }

    // Find which parameter controls the mouth-interior drawables (Oral*, Tongue*) opacity, and at what
    // value they hide. Confirms the fix (set that param to its hidden value at load).
    public static void SweepMark()
    {
        string model = System.Environment.GetEnvironmentVariable("DA_MODEL");
        if (string.IsNullOrEmpty(model)) model = "l2d_10070100032";
        string mdir = "Assets/Models/" + model;
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mdir + "/" + model + ".prefab"));
        var cm = inst.GetComponent<CubismModel>();

        var targets = new System.Collections.Generic.List<Live2D.Cubism.Rendering.CubismRenderer>();
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var n = cr.gameObject.name;
            if (n.StartsWith("Oral", System.StringComparison.OrdinalIgnoreCase) || n.StartsWith("Tongue", System.StringComparison.OrdinalIgnoreCase))
                targets.Add(cr);
        }
        Debug.Log($"DA_RESULT SweepMark targets={targets.Count} names=[{string.Join(",", targets.Select(r => r.name))}]");

        System.Func<float> op = () =>
        {
            cm.ForceUpdateNow();
            float s = 0; int n = 0;
            foreach (var cr in targets) { var m = cr.Mesh; if (m == null) continue; var c = m.colors; if (c == null || c.Length == 0) continue; float a = 0; foreach (var x in c) a += x.a; s += a / c.Length; n++; }
            return n > 0 ? s / n : -1f;
        };
        Debug.Log($"DA_RESULT markOpacityAtDefault={op():0.000}");
        foreach (var p in cm.Parameters)
        {
            float d = p.Value;
            p.Value = p.MinimumValue; float aMin = op();
            p.Value = p.MaximumValue; float aMax = op();
            p.Value = d; cm.ForceUpdateNow();
            if (Mathf.Abs(aMax - aMin) > 0.2f)
                Debug.Log($"DA_MARKCTRL {p.Id} min={p.MinimumValue:0.#}->op{aMin:0.00} max={p.MaximumValue:0.#}->op{aMax:0.00} def={d:0.#}");
        }
        Debug.Log("DA_RESULT SweepMark done");
        Object.DestroyImmediate(inst);
    }

    static void CaptureCam(Camera cam, int W, int H, string path)
    {
        var rt = new RenderTexture(W, H, 24);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        tex.Apply();
        RenderTexture.active = null; cam.targetTexture = null;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(rt);
    }

    public static void Render()
    {
        string prefabPath = Dir + "/l2d_10070100032.prefab";
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);

        var camGO = new GameObject("Cam");
        var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.16f, 0.20f, 1f);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        inst.transform.position = Vector3.zero;
        var model = inst.GetComponent<CubismModel>();

        var clipPath = AssetDatabase.FindAssets("t:AnimationClip", new[] { Dir + "/clips" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).StartsWith("scene02"));
        if (clipPath != null)
            AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath).SampleAnimation(inst, 1.0f);
        if (model != null) model.ForceUpdateNow();

        // Swap each Cubism renderer from the editor picking material to its real blend material.
        int swapped = 0;
        foreach (var cr in inst.GetComponentsInChildren<Live2D.Cubism.Rendering.CubismRenderer>())
        {
            var mr = cr.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            var sh = mr.sharedMaterial != null && mr.sharedMaterial.shader != null ? mr.sharedMaterial.shader.name : null;
            if (mr.sharedMaterial == null || sh == "Live2D Cubism/TransparentPicking")
            {
                mr.sharedMaterial = cr.SetMaterialFromPicker();
                swapped++;
            }
        }
        Debug.Log($"DA_RESULT mat_swapped={swapped}");

        // frame camera to model bounds
        var rends = inst.GetComponentsInChildren<Renderer>();
        var b = new Bounds(inst.transform.position, Vector3.one);
        bool first = true;
        foreach (var r in rends) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }
        int W = 720, H = 1280;
        float aspect = (float)W / H;
        cam.transform.position = new Vector3(b.center.x, b.center.y, -10f);
        cam.orthographicSize = Mathf.Max(b.extents.y, b.extents.x / aspect) * 1.1f;
        Debug.Log($"DA_RESULT bounds center={b.center} size={b.size} renderers={rends.Length}");

        var rt = new RenderTexture(W, H, 24);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        var outPath = Diag("render_test.png");
        File.WriteAllBytes(outPath, tex.EncodeToPNG());
        Debug.Log("DA_RESULT rendered=" + outPath.Replace('\\', '/'));
    }
}
