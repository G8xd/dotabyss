using System.Collections;
using System.IO;
using UnityEngine;

// Runtime play-mode screenshot helper (used only for headless validation).
public class DA_Capture : MonoBehaviour
{
    public Camera cam;
    public Animator animator;
    public string trigger = "Scene02";
    public string outPath;
    public float wait = 2.5f;

    IEnumerator Start()
    {
        yield return null;
        yield return null;
        if (animator != null && !string.IsNullOrEmpty(trigger))
        {
            foreach (var p in animator.parameters)
            {
                if (p.name != trigger) continue;
                if (p.type == AnimatorControllerParameterType.Trigger) animator.SetTrigger(trigger);
                else if (p.type == AnimatorControllerParameterType.Bool) animator.SetBool(trigger, true);
            }
        }
        yield return new WaitForSeconds(wait);

        // Ensure each Cubism renderer uses its real blend material, not the editor picking material.
        var crs = Object.FindObjectsByType<Live2D.Cubism.Rendering.CubismRenderer>(FindObjectsSortMode.None);
        int swapped = 0;
        foreach (var cr in crs)
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
        var anyMR = Object.FindFirstObjectByType<MeshRenderer>();
        Debug.Log($"DA_RESULT cubismRenderers={crs.Length} swapped={swapped} firstMat={(anyMR && anyMR.sharedMaterial ? anyMR.sharedMaterial.shader.name : "n/a")}");
        yield return null;
        yield return null;

        int W = 720, H = 1280;
        var rt = new RenderTexture(W, H, 24);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        cam.targetTexture = null;
        File.WriteAllBytes(outPath, tex.EncodeToPNG());
        Debug.Log("DA_RESULT playcapture=" + outPath);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.Exit(0);
#endif
    }
}
