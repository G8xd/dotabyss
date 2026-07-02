using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace DA
{
    /// <summary>Loads + parses StreamingAssets/scenes.json (a top-level JSON array of 133 scenes).</summary>
    public static class ScenesDb
    {
        public static IEnumerator Load(Action<AdvSceneList> done)
        {
            string path = DAConfig.ScenesPath;
            string url = path.Contains("://") ? path : "file://" + path.Replace('\\', '/');
            string json = null;
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) json = req.downloadHandler.text;
                else Debug.LogError("[DA] scenes.json load failed: " + req.error + " (" + url + ")");
            }

            var list = new AdvSceneList();
            if (!string.IsNullOrEmpty(json))
            {
                try { list = JsonUtility.FromJson<AdvSceneList>("{\"items\":" + json + "}"); }
                catch (Exception e) { Debug.LogError("[DA] scenes.json parse failed: " + e.Message); }
            }
            done?.Invoke(list ?? new AdvSceneList());
        }
    }
}
