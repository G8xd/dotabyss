using System;
using System.Collections.Generic;
using UnityEngine;

namespace DA
{
    /// <summary>
    /// One ADV command (a "step"). All fields are optional; which ones are set
    /// depends on <see cref="op"/>. Mirrors the parsed scenes.json steps.
    /// Ops: model, motion, say, wait, bgm, bgmstop, bgv, bgvstop, se, bg, hide, click.
    /// </summary>
    [Serializable]
    public class AdvStep
    {
        public string op;
        public string id;     // model id / bgm id / se id / bg id
        public string name;   // motion trigger name
        public bool @async;   // motion: asyncl2dmotion (escaped keyword; JSON key stays "async")
        public float delay;   // motion: async schedule delay (seconds)
        public string who;    // say: speaker / charaload: display name
        public string text;   // say: dialogue
        public string voice;  // say: voice cue (nullable)
        public string cue;    // bgv: voice cue
        public float sec;     // wait/fade/window/move: seconds

        // ---- cinematic direction ----
        public string dir;    // fade: In | Out
        public string color;  // fade: Black | White
        public bool on;       // window / uivisible: on/off
        public string slot;   // charaload/charamove/charascale/motion: chara_N
        public string mode;   // charamove: Set | Add
        public string axis;   // charamove: X | Y
        public float x;       // charamove/cammove: x (design pixels)
        public float y;       // charamove/cammove: y (design pixels)
        public float scale;   // charascale: scale factor
        public float zoom;    // camzoom: zoom factor
        public string path;   // subimage: image path
    }

    [Serializable]
    public class AdvScene
    {
        public string id;
        public string model;
        public int says;
        public int voiced;
        public List<AdvStep> steps = new List<AdvStep>();
        public string src;
    }

    [Serializable]
    public class AdvSceneList
    {
        public List<AdvScene> items = new List<AdvScene>();
    }
}
