using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using DA;

public static class DA_Build
{
    // All build paths derive from the repo root (<repo>/unity/Assets -> <repo>), so the
    // project is portable. Mirrors tools/daconfig.py / config.json.
    static string RepoRoot => Directory.GetParent(Application.dataPath).Parent.FullName;
    static string ExtractRoot => Path.Combine(RepoRoot, "data", "extracted");
    static string BundlesWin => Path.Combine(RepoRoot, "data", "bundles_win");
    static string BundlesAndroid => Path.Combine(RepoRoot, "data", "bundles");
    static string BuildDir => Path.Combine(RepoRoot, "build");
    static string BuildTestDir => Path.Combine(RepoRoot, "build", "test");
    static string KeystorePath => Path.Combine(RepoRoot, "keystore", "debug.keystore");

    [MenuItem("DA/Setup All")]
    public static void SetupAll()
    {
        BuildRegistry();
        CopyStreamingAssets();
        BuildScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("DA_RESULT SetupAll done");
    }

    // Single-launch setup for `da unity setup` after models are (re)copied into Assets/Models:
    // import new models -> prefabs, ensure URP + Cubism feature, wire Animators, rebuild registry,
    // refresh StreamingAssets scenes, (re)create the player scene. Idempotent.
    public static void FullSetup()
    {
        AssetDatabase.Refresh();        // import any new .model3.json -> Cubism prefabs
        DA_Setup.SetupURP();            // URP pipeline + CubismRenderPassFeature
        DA_Setup.WireAll();             // attach Animator + controller to each model prefab
        BuildRegistry();
        CopyStreamingAssets();
        BuildScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("DA_RESULT FullSetup done");
    }

    public static void BuildRegistry()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        var reg = ScriptableObject.CreateInstance<ModelRegistry>();
        var dirs = AssetDatabase.GetSubFolders("Assets/Models");
        foreach (var dir in dirs.OrderBy(d => d))
        {
            string m = Path.GetFileName(dir);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(dir + "/" + m + ".prefab");
            if (prefab != null) reg.entries.Add(new ModelRegistry.Entry { name = m, prefab = prefab });
        }
        AssetDatabase.CreateAsset(reg, "Assets/Resources/ModelRegistry.asset");
        Debug.Log("DA_RESULT BuildRegistry entries=" + reg.entries.Count);
    }

    public static void CopyStreamingAssets()
    {
        if (!AssetDatabase.IsValidFolder("Assets/StreamingAssets")) AssetDatabase.CreateFolder("Assets", "StreamingAssets");
        // prefer the enriched v2 scenes (fade/window/charaload/camera); fall back to the old one
        var v2 = Path.Combine(ExtractRoot, "_viewer", "scenes_v2.json");
        var src = File.Exists(v2) ? v2 : Path.Combine(ExtractRoot, "_viewer", "scenes.json");
        var dst = "Assets/StreamingAssets/scenes.json";
        if (File.Exists(src)) { File.Copy(src, dst, true); Debug.Log("DA_RESULT scenes copied " + Path.GetFileName(src) + " " + new FileInfo(src).Length + " bytes"); }
        else Debug.LogError("scenes.json not found at " + src);
    }

    public static void BuildWindows()
    {
        CopyStreamingAssets();   // refresh scenes.json (enriched) before every build
        PlayerSettings.companyName = "DA";
        PlayerSettings.productName = "DotAbyssPlayer";
        PlayerSettings.defaultScreenWidth = 1440;          // 20:9 = the game's mobile aspect (no bars by default)
        PlayerSettings.defaultScreenHeight = 648;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

        var dir = BuildDir;
        Directory.CreateDirectory(dir);
        var opts = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Player.unity" },
            locationPathName = Path.Combine(dir, "DotAbyssPlayer.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        var report = BuildPipeline.BuildPlayer(opts);
        var s = report.summary;
        Debug.Log($"DA_RESULT BuildWindows result={s.result} size={s.totalSize} time={s.totalTime} out={opts.locationPathName}");
    }

    public static void PlayTest()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Player.unity");
        EditorApplication.EnterPlaymode();
    }

    public static void BuildScene()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("DA");
        go.AddComponent<DABootstrap>();
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Player.unity");
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Scenes/Player.unity", true) };
        Debug.Log("DA_RESULT BuildScene Player.unity created + added to build settings");
    }

    // ---------- AssetBundles (models load from device storage; keeps the APK small) ----------

    // one bundle per model, named after the model; deps (moc3/textures/controller/clips/materials) follow
    public static void AssignBundles()
    {
        var dirs = AssetDatabase.GetSubFolders("Assets/Models");
        int n = 0;
        foreach (var dir in dirs)
        {
            string m = Path.GetFileName(dir);
            var prefabPath = dir + "/" + m + ".prefab";
            var imp = AssetImporter.GetAtPath(prefabPath);
            if (imp == null) continue;
            if (imp.assetBundleName != m) imp.assetBundleName = m;
            n++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"DA_RESULT AssignBundles assigned={n}");
    }

    public static void BuildBundles(BuildTarget target, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var manifest = BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression, target);
        long sz = 0; foreach (var f in Directory.GetFiles(outDir)) sz += new FileInfo(f).Length;
        Debug.Log($"DA_RESULT BuildBundles target={target} out={outDir} bundles={(manifest != null ? manifest.GetAllAssetBundles().Length : -1)} sizeMB={sz / 1048576}");
    }

    // registry with NAMES ONLY (no prefab refs) so a build does not embed the 3 GB of models
    public static void BuildRegistryNamesOnly()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        var reg = ScriptableObject.CreateInstance<ModelRegistry>();
        foreach (var dir in AssetDatabase.GetSubFolders("Assets/Models").OrderBy(d => d))
            reg.entries.Add(new ModelRegistry.Entry { name = Path.GetFileName(dir), prefab = null });
        AssetDatabase.CreateAsset(reg, "Assets/Resources/ModelRegistry.asset");
        AssetDatabase.SaveAssets();
        Debug.Log("DA_RESULT BuildRegistryNamesOnly entries=" + reg.entries.Count);
    }

    // De-risk the bundle loader on desktop (no device needed): build Windows bundles + a names-only
    // exe to <repo>/build/test, then restore the embedded registry. Run that exe to verify.
    public static void WinBundleTest()
    {
        try
        {
            AssignBundles();
            BuildBundles(BuildTarget.StandaloneWindows64, BundlesWin);
            CopyStreamingAssets();
            BuildRegistryNamesOnly();
            PlayerSettings.companyName = "DA"; PlayerSettings.productName = "DotAbyssPlayer";
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed; PlayerSettings.resizableWindow = true;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            var dir = BuildTestDir; Directory.CreateDirectory(dir);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Player.unity" },
                locationPathName = Path.Combine(dir, "DotAbyssPlayer.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            });
            Debug.Log($"DA_RESULT WinBundleTest result={report.summary.result} size={report.summary.totalSize} out={dir}");
        }
        finally { BuildRegistry(); }   // restore embedded registry so the normal desktop build still works
    }

    public static void BuildAndroid()
    {
        // switch to Android (reimports textures to ASTC) — required for correct bundles + player
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        try
        {
            AssignBundles();
            BuildBundles(BuildTarget.Android, BundlesAndroid);
            CopyStreamingAssets();
            BuildRegistryNamesOnly();

            PlayerSettings.companyName = "DA";
            PlayerSettings.productName = "DotAbyss Player";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.da.dotabyssplayer");
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            EditorUserBuildSettings.buildAppBundle = false;   // APK, not AAB

            // sign with the reusable debug keystore (standard android debug creds)
            var ks = KeystorePath;
            if (File.Exists(ks))
            {
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = ks;
                PlayerSettings.Android.keystorePass = "android";
                PlayerSettings.Android.keyaliasName = "androiddebugkey";
                PlayerSettings.Android.keyaliasPass = "android";
            }

            var dir = BuildDir; Directory.CreateDirectory(dir);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Player.unity" },
                locationPathName = Path.Combine(dir, "DotAbyssPlayer.apk"),
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            });
            var s = report.summary;
            Debug.Log($"DA_RESULT BuildAndroid result={s.result} size={s.totalSize} time={s.totalTime} out={Path.Combine(dir, "DotAbyssPlayer.apk")}");
        }
        finally { BuildRegistry(); }   // restore embedded registry for desktop builds
    }
}
