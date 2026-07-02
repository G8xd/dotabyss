using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DA
{
    /// <summary>
    /// Loads audio/cue_index.json — a flat { "cueId": "relative/path.wav", ... } map
    /// (21k entries, ASCII paths). Parsed with a regex since Unity's JsonUtility can't
    /// do arbitrary dictionaries.
    /// </summary>
    public class CueIndex
    {
        private readonly Dictionary<string, string> _map = new Dictionary<string, string>();
        public int Count => _map.Count;

        public static CueIndex Load()
        {
            var ci = new CueIndex();
            try
            {
                var path = DAConfig.CueIndexPath;
                if (!File.Exists(path)) { Debug.LogWarning("[DA] cue_index.json not found: " + path); return ci; }
                var text = File.ReadAllText(path);
                foreach (Match m in Regex.Matches(text, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\""))
                    ci._map[m.Groups[1].Value] = m.Groups[2].Value;
            }
            catch (System.Exception e) { Debug.LogWarning("[DA] cue_index load failed: " + e.Message); }
            return ci;
        }

        /// <summary>Absolute path to the wav for a cue, or null if unknown.</summary>
        public string ResolveAbsolute(string cue)
        {
            if (string.IsNullOrEmpty(cue)) return null;
            if (!_map.TryGetValue(cue, out var rel)) return null;
            return Path.Combine(DAConfig.AudioRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
