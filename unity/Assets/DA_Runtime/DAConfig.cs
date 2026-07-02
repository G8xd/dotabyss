using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DA
{
    /// <summary>
    /// Resolves where the loose runtime assets (audio, bundles, cue_index.json) live.
    ///
    /// Resolution order (first hit wins):
    ///   1. a user-chosen folder saved in PlayerPrefs (set from the in-app "Assets folder" picker)
    ///   2. a `da_assets.txt` file next to the executable or in persistentDataPath (one line = the path)
    ///   3. auto-detection of the common locations for this platform (see <see cref="AutoCandidates"/>)
    ///   4. a platform default
    ///
    /// On Android the app-specific folder (persistentDataPath = Android/data/&lt;pkg&gt;/files) is hard for
    /// users to reach on modern devices, so the picker lets them point the app at an easier location
    /// (Download/, Documents/, or the sdcard root) and that choice is remembered here.
    /// </summary>
    public static class DAConfig
    {
        /// <summary>PlayerPrefs key holding the user-chosen asset root (empty = not set).</summary>
        public const string PrefKey = "da_asset_root";
        /// <summary>Folder name we look for when auto-detecting on device / next to the exe.</summary>
        public const string FolderName = "dotabyss_extracted";

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

        /// <summary>The user's saved override, or "" when none is set.</summary>
        public static string SavedOverride => PlayerPrefs.GetString(PrefKey, "");

        /// <summary>
        /// Persist a user-chosen asset root and drop the cached resolution so the next
        /// <see cref="AssetRoot"/> read picks it up. Passing null/empty clears the override.
        /// </summary>
        public static void SetAssetRoot(string path)
        {
            path = path?.Trim();
            if (string.IsNullOrEmpty(path)) PlayerPrefs.DeleteKey(PrefKey);
            else PlayerPrefs.SetString(PrefKey, path);
            PlayerPrefs.Save();
            _root = null;   // force re-resolve on next access
        }

        /// <summary>Forget the user override and fall back to auto-detection / default.</summary>
        public static void ClearOverride() => SetAssetRoot(null);

        /// <summary>Drop the cached resolution so the next <see cref="AssetRoot"/> read re-scans.</summary>
        public static void Refresh() => _root = null;

        private static string Resolve()
        {
            // 1) explicit user override saved from the in-app picker (highest priority)
            var saved = SavedOverride;
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved)) return saved;

            // 2) legacy override file next to the exe or in the app folder
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

            // 3) auto-detect the usual spots for this platform
            foreach (var c in AutoCandidates())
                if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;

            // 4) platform default (may not exist yet; the picker/status will say "not found")
#if UNITY_ANDROID && !UNITY_EDITOR
            return Path.Combine(Application.persistentDataPath, FolderName);
#else
            return AppRootDir();
#endif
        }

        /// <summary>
        /// Candidate asset roots to auto-detect and to offer in the in-app picker, in priority order.
        /// Android lists the user-accessible public folders (Download/, Documents/, sdcard root) plus
        /// the app-specific folder; desktop/editor lists the repo's data/extracted.
        /// </summary>
        public static List<string> AutoCandidates()
        {
            var list = new List<string>();
#if UNITY_ANDROID && !UNITY_EDITOR
            string ext = ExternalStorageRoot();     // usually /storage/emulated/0
            if (!string.IsNullOrEmpty(ext))
            {
                Add(list, Path.Combine(ext, "Download", FolderName));
                Add(list, Path.Combine(ext, "Documents", FolderName));
                Add(list, Path.Combine(ext, FolderName));
            }
            Add(list, Path.Combine(Application.persistentDataPath, FolderName));   // app folder (no perms needed)
#else
    #if UNITY_EDITOR
            // <repo>/unity/Assets -> <repo>/data/extracted
            try
            {
                var repo = Directory.GetParent(Application.dataPath)?.Parent?.FullName;
                if (repo != null) Add(list, Path.Combine(repo, "data", "extracted"));
            }
            catch { }
    #endif
            var app = AppRootDir();
            Add(list, Path.Combine(app, "data", "extracted"));   // exe sitting next to data/
            try
            {
                var up = Directory.GetParent(app)?.FullName;      // build/ next to data/ (both under repo)
                if (up != null) Add(list, Path.Combine(up, "data", "extracted"));
            }
            catch { }
#endif
            return list;
        }

        private static void Add(List<string> list, string p)
        {
            if (!string.IsNullOrEmpty(p) && !list.Contains(p)) list.Add(p);
        }

        /// <summary>
        /// True if <paramref name="path"/> looks like an extracted asset root (has the audio or bundles
        /// subfolders). Used to warn in the picker before the user commits to a bad path.
        /// </summary>
        public static bool LooksLikeAssetRoot(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
            return Directory.Exists(Path.Combine(path, "audio")) ||
                   Directory.Exists(Path.Combine(path, "bundles")) ||
                   Directory.Exists(Path.Combine(path, "bundles_win"));
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Public external storage root (Environment.getExternalStorageDirectory), e.g. /storage/emulated/0.
        private static string ExternalStorageRoot()
        {
            try
            {
                using var env = new AndroidJavaClass("android.os.Environment");
                using var dir = env.CallStatic<AndroidJavaObject>("getExternalStorageDirectory");
                return dir.Call<string>("getAbsolutePath");
            }
            catch { return "/storage/emulated/0"; }
        }
#endif

        private static string AppRootDir()
        {
            try { return Path.GetDirectoryName(Application.dataPath); } catch { return Application.dataPath; }
        }

        public static string AudioRoot => Path.Combine(AssetRoot, "audio");
        public static string CueIndexPath => Path.Combine(AudioRoot, "cue_index.json");

        // Per-model AssetBundles. Android reads the bundles under the chosen asset root (…/bundles);
        // Desktop/editor uses a separate platform-built set (…/bundles_win) so the two can coexist.
#if UNITY_ANDROID && !UNITY_EDITOR
        public static string BundlesDir => Path.Combine(AssetRoot, "bundles");
#else
        public static string BundlesDir => Path.Combine(AssetRoot, "bundles_win");
#endif
        // scenes.json ships inside the build (StreamingAssets) so the player works without the folder.
        public static string ScenesPath => Path.Combine(Application.streamingAssetsPath, "scenes.json");
    }
}
