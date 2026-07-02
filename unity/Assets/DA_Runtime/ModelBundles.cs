using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DA
{
    /// <summary>
    /// Loads model prefabs from per-model AssetBundles at runtime (so the APK stays small and the
    /// 3 GB of models live on device storage). Bundles are in <see cref="DAConfig.BundlesDir"/>,
    /// one bundle per model named after the model (lowercase). Desktop builds with an embedded
    /// registry never hit this; Android (names-only registry) loads everything here.
    /// </summary>
    public static class ModelBundles
    {
        private static readonly Dictionary<string, AssetBundle> _cache = new Dictionary<string, AssetBundle>();
        private static AssetBundleManifest _manifest;
        private static bool _manifestTried;

        private static string Dir => DAConfig.BundlesDir;
        public static bool Available => Directory.Exists(Dir);

        /// <summary>
        /// Drop every loaded bundle + the cached manifest so the next load re-reads from the current
        /// <see cref="DAConfig.BundlesDir"/>. Call after the asset root changes (the caller must release
        /// any instantiated prefab first — Unload(true) frees the assets those instances reference).
        /// </summary>
        public static void Reset()
        {
            foreach (var b in _cache.Values) if (b != null) b.Unload(true);
            _cache.Clear();
            _manifest = null;
            _manifestTried = false;
        }

        private static AssetBundleManifest Manifest()
        {
            if (_manifestTried) return _manifest;
            _manifestTried = true;
            try
            {
                var mfPath = Path.Combine(Dir, Path.GetFileName(Dir)); // the "all" manifest bundle is named after the folder
                if (File.Exists(mfPath))
                {
                    var mb = AssetBundle.LoadFromFile(mfPath);
                    if (mb != null) _manifest = mb.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[DA] bundle manifest load failed: " + e.Message); }
            return _manifest;
        }

        private static AssetBundle LoadBundle(string bundleName)
        {
            if (_cache.TryGetValue(bundleName, out var b)) return b;
            var path = Path.Combine(Dir, bundleName);
            b = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
            if (b == null) Debug.LogWarning("[DA] bundle not found: " + path);
            _cache[bundleName] = b;
            return b;
        }

        public static GameObject LoadPrefab(string modelName)
        {
            if (string.IsNullOrEmpty(modelName) || !Available) return null;
            var bn = modelName.ToLowerInvariant();
            var man = Manifest();
            if (man != null)
                foreach (var dep in man.GetAllDependencies(bn))
                    LoadBundle(dep);
            var bundle = LoadBundle(bn);
            if (bundle == null) return null;
            var go = bundle.LoadAsset<GameObject>(modelName);
            if (go == null)
            {
                var all = bundle.LoadAllAssets<GameObject>();
                if (all != null && all.Length > 0) go = all[0];
            }
            return go;
        }
    }
}
