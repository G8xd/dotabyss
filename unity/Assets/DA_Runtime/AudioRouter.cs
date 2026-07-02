using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace DA
{
    /// <summary>
    /// Four audio channels matching the game: BGM (loop), Voice (one-shot, drives lip-sync +
    /// auto-advance), BGV (background voice loop), SE (one-shot). Clips are loaded from disk on
    /// demand (OGG or WAV, by file extension) and cached.
    /// </summary>
    public class AudioRouter : MonoBehaviour
    {
        public AudioSource bgm, voice, bgv, se;
        private CueIndex _cues;
        private readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>();

        public void Init(CueIndex cues)
        {
            _cues = cues;
            bgm = MakeSource("BGM", true, 0.6f);
            voice = MakeSource("Voice", false, 1.0f);
            bgv = MakeSource("BGV", true, 0.9f);
            se = MakeSource("SE", false, 0.9f);
        }

        private AudioSource MakeSource(string name, bool loop, float vol)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false; s.loop = loop; s.volume = vol; s.spatialBlend = 0f;
            return s;
        }

        public bool VoicePlaying => voice != null && voice.isPlaying;

        public void PlayBgm(string id) => PlayCue(bgm, id, true);
        public void StopBgm() { if (bgm) bgm.Stop(); }
        public void PlayBgv(string cue) => PlayCue(bgv, cue, true);
        public void StopBgv() { if (bgv) bgv.Stop(); }
        public void PlaySe(string id) => PlayCue(se, id, false);
        public void PlayVoice(string cue) => PlayCue(voice, cue, false);
        public void StopVoice() { if (voice) voice.Stop(); }

        public void StopAll() { StopBgm(); StopBgv(); StopVoice(); if (se) se.Stop(); }

        private void PlayCue(AudioSource src, string cue, bool loop)
        {
            if (src == null || string.IsNullOrEmpty(cue)) return;
            StartCoroutine(PlayCueCo(src, cue, loop));
        }

        private IEnumerator PlayCueCo(AudioSource src, string cue, bool loop)
        {
            AudioClip clip;
            if (!_cache.TryGetValue(cue, out clip))
            {
                var path = _cues != null ? _cues.ResolveAbsolute(cue) : null;
                if (path == null || !System.IO.File.Exists(path)) { Debug.LogWarning("[DA] cue missing: " + cue); yield break; }
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                var atype = ext == ".ogg" ? AudioType.OGGVORBIS : AudioType.WAV;
                using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path.Replace('\\', '/'), atype))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success) { Debug.LogWarning("[DA] audio load failed: " + cue + " " + req.error); yield break; }
                    clip = DownloadHandlerAudioClip.GetContent(req);
                    clip.name = cue;
                    _cache[cue] = clip;
                }
            }
            src.loop = loop;
            src.clip = clip;
            src.Play();
        }
    }
}
