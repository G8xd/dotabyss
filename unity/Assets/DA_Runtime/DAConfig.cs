using System.IO;
using UnityEngine;

namespace DA
{
    /// <summary>
    /// Resolves where the loose runtime assets (audio, images, scenes.json, cue_index.json) live.
    /// Desktop/editor default = the repo's data/extracted (resolved relative to the project or the
    /// exe); overridable with a `da_assets.txt` file next to the executable or in persistentDataPath
    /// containing one line: the asset root path. On Android, points to external storage.
    /// </summary>
    public static class DAConfig
    {
        private static string _root;

        public static string AssetRoot
        {
            get
            {
                if (_root != null) return _root;
                _root = Resolve();
                Debug.Log("[DA] AssetRoot = " + _root);
                return _root;
            }
        }

        private static string Resolve()
        {
            // 1) override file
            foreach (var dir in new[] { AppRootDir(), Application.persistentDataPath })
            {
                try
                {
                    var f = Path.Combine(dir, "da_assets.txt");
                    if (File.Exists(f))
                    {
                        var line = File.ReadAllText(f).Trim();
                        if (!string.IsNullOrEmpty(line) && Directory.Exists(line)) return line;
                    }
                }
                catch { }
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            // common device locations
            foreach (var p in new[] {
                "/storage/emulated/0/dotabyss_extracted",
                Path.Combine(Application.persistentDataPath, "dotabyss_extracted") })
                if (Directory.Exists(p)) return p;
            return "/storage/emulated/0/dotabyss_extracted";
#else
            // Desktop / editor: resolve the repo's data/extracted (relative to the
            // project in-editor, or to the exe in a build). No machine-specific path.
            foreach (var cand in DesktopCandidates())
                if (!string.IsNullOrEmpty(cand) && Directory.Exists(cand)) return cand;
            return AppRootDir();
#endif
        }

        // Candidate extracted-data roots for desktop/editor, in priority order.
        private static string[] DesktopCandidates()
        {
            var list = new System.Collections.Generic.List<string>();
#if UNITY_EDITOR
            // <repo>/unity/Assets -> <repo>/data/extracted
            try
            {
                var repo = Directory.GetParent(Application.dataPath)?.Parent?.FullName;
                if (repo != null) list.Add(Path.Combine(repo, "data", "extracted"));
            }
            catch { }
#endif
            var app = AppRootDir();
            list.Add(Path.Combine(app, "data", "extracted"));   // exe sitting next to data/
            try
            {
                var up = Directory.GetParent(app)?.FullName;     // build/ next to data/ (both under repo)
                if (up != null) list.Add(Path.Combine(up, "data", "extracted"));
            }
            catch { }
            return list.ToArray();
        }

        private static string AppRootDir()
        {
            try { return Path.GetDirectoryName(Application.dataPath); } catch { return Application.dataPath; }
        }

        public static string AudioRoot => Path.Combine(AssetRoot, "audio");
        public static string CueIndexPath => Path.Combine(AudioRoot, "cue_index.json");

        // Per-model AssetBundles. Android reads the bundles copied into the app-specific folder
        // (= persistentDataPath/dotabyss_extracted/bundles). Desktop/editor uses a separate
        // platform-built set so the two can coexist in the same extracted folder.
#if UNITY_ANDROID && !UNITY_EDITOR
        public static string BundlesDir => Path.Combine(AssetRoot, "bundles");
#else
        public static string BundlesDir => Path.Combine(AssetRoot, "bundles_win");
#endif
        // scenes.json ships inside the build (StreamingAssets) so the player works without the folder.
        public static string ScenesPath => Path.Combine(Application.streamingAssetsPath, "scenes.json");
    }
}
