using System.Collections;
using UnityEngine;

namespace DA
{
    /// <summary>
    /// Runs an AdvScene's step list, mirroring the game's NovelCmd* flow:
    ///   model -> stage.Load; motion -> stage.PlayTrigger (async => delayed, non-blocking);
    ///   say -> dialogue + voice (+ auto-advance); wait/click -> pause; bgm/bgv/se -> audio;
    ///   hide -> stage.Release.
    /// </summary>
    public class AdvPlayer : MonoBehaviour
    {
        public L2DStage stage;
        public AudioRouter audio;

        public bool autoAdvance = true;
        public bool skip;                       // SKIP: fast-forward through waits/voices
        public float readingDelayPerChar = 0.055f;
        public float readingDelayMin = 1.2f;

        public System.Action<string, string> onSay;   // (who, text)
        public System.Action onModelChanged;
        public System.Action<string> onBg;
        public System.Action onSceneEnd;
        public System.Action<string, string, float> onFade;   // (dir In/Out, color Black/White, seconds)
        public System.Action<bool, float> onWindow;           // (on, seconds)
        public System.Action<bool> onUiVisible;               // (on)

        public bool IsPlaying { get; private set; }
        private Coroutine _co;
        private bool _advance;

        public void RequestAdvance() => _advance = true;

        public void Play(AdvScene scene)
        {
            Stop();
            if (scene == null) return;
            _co = StartCoroutine(Run(scene));
        }

        public void Stop()
        {
            if (_co != null) { StopCoroutine(_co); _co = null; }
            StopAllCoroutines();
            IsPlaying = false;
            if (audio != null) audio.StopAll();
        }

        private IEnumerator Run(AdvScene scene)
        {
            IsPlaying = true;
            foreach (var st in scene.steps)
            {
                if (st == null || string.IsNullOrEmpty(st.op)) continue;
                switch (st.op)
                {
                    case "model":
                        if (stage.Load(st.id)) onModelChanged?.Invoke();
                        yield return null;
                        break;

                    case "motion":
                        if (st.@async)
                            StartCoroutine(AsyncMotion(st.name, Mathf.Max(0f, st.delay)));
                        else
                            stage.PlayTrigger(st.name, true);
                        break;

                    case "say":
                        onSay?.Invoke(st.who, st.text);
                        if (audio != null && !string.IsNullOrEmpty(st.voice)) audio.PlayVoice(st.voice);
                        yield return SayWait(st);
                        break;

                    case "wait":
                        yield return WaitOrAdvance(Mathf.Max(0f, st.sec));
                        break;

                    case "click":
                        yield return WaitOrAdvance(-1f);
                        break;

                    case "bgm": audio?.PlayBgm(st.id); break;
                    case "bgmstop": audio?.StopBgm(); break;
                    case "bgv": audio?.PlayBgv(st.cue); break;
                    case "bgvstop": audio?.StopBgv(); break;
                    case "se": audio?.PlaySe(st.id); break;
                    case "sestop": audio?.StopAll(); break;
                    case "bg": onBg?.Invoke(st.id); break;
                    case "hide": stage.Release(); break;
                    case "cleanall": stage.Release(); onSay?.Invoke("", ""); break;

                    case "fade":
                        onFade?.Invoke(st.dir, st.color, Mathf.Max(0f, st.sec));
                        if (!st.@async && st.sec > 0f) yield return new WaitForSeconds(st.sec);
                        break;

                    case "window":
                        onWindow?.Invoke(st.on, Mathf.Max(0f, st.sec));
                        break;

                    case "uivisible":
                        onUiVisible?.Invoke(st.on);
                        break;

                    case "charamove":
                        stage.ModelMove(st.x, st.y, st.mode != null && st.mode.Equals("Add", System.StringComparison.OrdinalIgnoreCase));
                        break;
                    case "charascale": stage.ModelScale(st.scale); break;
                    case "camzoom": stage.CamZoom(st.zoom); break;
                    case "cammove": stage.CamMove(st.x, st.y, false); break;

                    // charaload registers a slot's display name (l2dmessage already carries the
                    // speaker, so nothing to show here); subimage/shake are cosmetic — skipped.
                    case "charaload": break;
                    case "subimage": break;
                    case "shake": break;
                }
            }
            IsPlaying = false;
            onSceneEnd?.Invoke();
        }

        private IEnumerator AsyncMotion(string trigger, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            stage.PlayTrigger(trigger, true);
        }

        private IEnumerator SayWait(AdvStep st)
        {
            bool voiced = audio != null && !string.IsNullOrEmpty(st.voice);
            if (!autoAdvance)
            {
                yield return WaitOrAdvance(-1f);
                yield break;
            }
            if (voiced)
            {
                // let the voice line start, then wait until it finishes (advance skips it)
                float t = 0f;
                while (t < 0.5f && !(audio.VoicePlaying)) { t += Time.deltaTime; yield return null; }
                while (audio.VoicePlaying)
                {
                    if (ConsumeAdvance()) { audio.StopVoice(); break; }
                    yield return null;
                }
                yield return WaitOrAdvance(0.15f);
            }
            else
            {
                float secs = Mathf.Max(readingDelayMin, (st.text != null ? st.text.Length : 0) * readingDelayPerChar);
                yield return WaitOrAdvance(secs);
            }
        }

        // seconds < 0 => wait indefinitely for an advance (click / Space / Next button).
        private IEnumerator WaitOrAdvance(float seconds)
        {
            _advance = false;
            float t = 0f;
            while (true)
            {
                if (ConsumeAdvance()) yield break;
                if (seconds >= 0f) { t += Time.deltaTime; if (t >= seconds) yield break; }
                yield return null;
            }
        }

        private bool ConsumeAdvance()
        {
            if (skip) return true;
            bool key = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return)
                       || Input.GetMouseButtonDown(0);
            if (_advance || key) { _advance = false; return true; }
            return false;
        }
    }
}
