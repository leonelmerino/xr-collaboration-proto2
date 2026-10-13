using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using XRCollab.Measurement.Cues;

/// <summary>
/// Vista previa de las señales 3D sin visor: arma una escena vacía, ilumina con <see cref="PassthroughLighting"/> a
/// partir de una foto de la sala (un cuadro del espejo del visor), anima cada señal y compone cada cuadro sobre la
/// foto como lo hace el visor (alpha premultiplicado: app + sala × (1 − alpha)). Escribe PNGs y una hoja de contacto.
///   Unity.exe -batchmode -quit -projectPath . -executeMethod FeedbackCuePreview.Render -cue-bg sala.png -cue-out carpeta
///       [-cue-frame espejo.png: cuadro completo del espejo (ambos ojos) para la luz] [-cue-spec "bad|cross|TEXTO|sub"]
/// </summary>
public static class FeedbackCuePreview
{
    private static readonly string[] DefaultSpecs =
    {
        "great|star|¡PERFECTO!|Bloque impecable",
        "good|check|BIEN|Sigue así",
        "bad|cross|MAL COLOCADO|El bloque quedó torcido",
        "slow|clock|DEMASIADO LENTO|Más de 30 segundos",
        "warn|warning|CUIDADO|La torre está inestable",
        "great|bolt|¡RÁPIDO!|",
    };

    private static readonly float[] Times = { 0.15f, 0.35f, 0.6f, 1.0f, 1.8f, 3.45f, 3.6f };
    private const int Width = 1280, Height = 720;

    [MenuItem("XR Collab/Señales 3D: vista previa (PNG)")]
    public static void Render()
    {
        FeedbackCueAssetsBuilder.Ensure();
        OcclusionAssetsBuilder.Ensure();
        var occluderMat = Resources.Load<Material>(XRCollab.Measurement.Occlusion.Occluders.MaterialResource);
        // Escena nueva primero: cambiar de escena descarga las texturas creadas antes.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        string bgPath = Arg("-cue-bg");
        string outDir = Arg("-cue-out") ?? Path.Combine(Path.GetTempPath(), "cue-preview");
        Directory.CreateDirectory(outDir);
        List<string> specs = new List<string>(DefaultSpecs);
        string one = Arg("-cue-spec");
        if (one != null) { specs.Clear(); specs.Add(one); }

        var bg = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (bgPath == null || !bg.LoadImage(File.ReadAllBytes(bgPath))) { bg = new Texture2D(Width, Height); Fill(bg, new Color(0.45f, 0.44f, 0.42f)); }
        Texture2D bgScaled = Scale(bg, Width, Height);

        int layer = FeedbackCueDirector.CueLayer;
        var camGo = new GameObject("PreviewCamera");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = 1 << layer;
        cam.allowHDR = false;
        cam.fieldOfView = 58f;
        cam.nearClipPlane = 0.05f;

        // Iluminación desde la foto: como si la persona mirara de frente (un ojo = la foto).
        var lighting = new PassthroughLighting();
        Light key = FeedbackCueDirector.CueLight("CueKeyLight", null);
        Light fill = FeedbackCueDirector.CueLight("CueFillLight", null);
        // Luz desde un cuadro completo del espejo (-cue-frame, ambos ojos) si hay; si no, desde la foto de fondo.
        string framePath = Arg("-cue-frame");
        var full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Texture2D frame = framePath != null && full.LoadImage(File.ReadAllBytes(framePath)) ? ToRgba32(full) : BothEyes(bgScaled);
        for (int i = 0; i < 3; i++) lighting.Sample(frame, Quaternion.identity);
        lighting.Apply(key, fill, Vector3.forward);
        Debug.Log($"[CuePreview] luz ambiente {PassthroughLighting.Hex(lighting.AmbientLinear)}, principal {PassthroughLighting.Hex(lighting.KeyColor)} ×{lighting.KeyIntensity:0.00} dir {lighting.KeyForward}, relleno ×{lighting.FillIntensity:0.00}, exposición {lighting.Exposure:0.00}");

        CueAssets assets = CueAssets.Load();
        if (!assets.Ok) { Debug.LogError("[CuePreview] faltan recursos:" + assets.Missing); return; }
        var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
        cam.targetTexture = rt;
        var read = new Texture2D(Width, Height, TextureFormat.RGBA32, false);

        var sheet = new List<Texture2D>();
        for (int s = 0; s < specs.Count; s++)
        {
            FeedbackCueSpec spec = FeedbackCueSpec.Parse(specs[s], out string error);
            if (spec == null) { Debug.LogError($"[CuePreview] {specs[s]}: {error}"); continue; }
            Vector3 dir = Quaternion.AngleAxis(-6f, Vector3.right) * Vector3.forward;
            Vector3 pos = dir * 1.4f;
            FeedbackCueVisual v = FeedbackCueVisual.Create(spec, assets, pos, Quaternion.LookRotation(pos, Vector3.up), layer, lighting.KeyIntensity);
            v.ManualParticles = true;
            // Prueba de oclusión: un «dedo» (cápsula que solo escribe profundidad) delante del título en la 3.ª señal.
            GameObject finger = null;
            if (s == 2 && occluderMat != null)
            {
                finger = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                finger.layer = XRCollab.Measurement.Occlusion.Occluders.Layer;
                finger.GetComponent<Renderer>().sharedMaterial = occluderMat;
                finger.transform.position = pos + new Vector3(-0.12f, -0.02f, -0.45f);
                finger.transform.rotation = Quaternion.Euler(0f, 0f, 20f);
                finger.transform.localScale = new Vector3(0.022f, 0.09f, 0.022f);
                cam.cullingMask |= 1 << XRCollab.Measurement.Occlusion.Occluders.Layer;
            }
            float t = 0f;
            foreach (float target in Times)
            {
                while (t + 1e-4f < target) { float dt = Mathf.Min(1f / 60f, target - t); v.Tick(dt); t += dt; }
                cam.Render();
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                read.Apply();
                RenderTexture.active = null;
                Texture2D composed = Compose(read, bgScaled);
                File.WriteAllBytes(Path.Combine(outDir, $"cue{s}_{spec.Tone.ToString().ToLowerInvariant()}_t{target:0.00}.png"), composed.EncodeToPNG());
                if (Mathf.Approximately(target, 1.8f)) sheet.Add(composed);
                else UnityEngine.Object.DestroyImmediate(composed);
            }
            UnityEngine.Object.DestroyImmediate(v.gameObject);
            if (finger != null) UnityEngine.Object.DestroyImmediate(finger);
        }
        if (sheet.Count > 0) File.WriteAllBytes(Path.Combine(outDir, "sheet.png"), Sheet(sheet, 3).EncodeToPNG());
        Debug.Log($"[CuePreview] listo: {outDir}");
    }

    /// <summary>
    /// Vista previa del botón de turno (Assets/Measurement/TurnPad): aparecer, mantener el dedo, cambio de color, viaje
    /// con la pinza y anillo de la pinza, sobre una foto de la sala, mirando hacia abajo como un participante parado.
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod FeedbackCuePreview.RenderPad -cue-bg sala.png -cue-out carpeta
    /// </summary>
    public static void RenderPad()
    {
        FeedbackCueAssetsBuilder.Ensure();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        string bgPath = Arg("-cue-bg");
        string outDir = Arg("-cue-out") ?? Path.Combine(Path.GetTempPath(), "pad-preview");
        Directory.CreateDirectory(outDir);
        var bg = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (bgPath == null || !bg.LoadImage(File.ReadAllBytes(bgPath))) { bg = new Texture2D(Width, Height); Fill(bg, new Color(0.45f, 0.44f, 0.42f)); }
        Texture2D bgScaled = Scale(bg, Width, Height);

        int layer = FeedbackCueDirector.CueLayer;
        var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = 1 << layer;
        cam.allowHDR = false;
        cam.fieldOfView = 58f;
        cam.nearClipPlane = 0.05f;
        cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(38f, 0f, 0f));
        var lighting = new PassthroughLighting();
        Light key = FeedbackCueDirector.CueLight("CueKeyLight", null);
        Light fill = FeedbackCueDirector.CueLight("CueFillLight", null);
        Texture2D frame = BothEyes(bgScaled);
        for (int i = 0; i < 3; i++) lighting.Sample(frame, cam.transform.rotation);
        lighting.Apply(key, fill, cam.transform.forward);

        CueAssets assets = CueAssets.Load();
        if (!assets.Ok) { Debug.LogError("[PadPreview] faltan recursos:" + assets.Missing); return; }
        var owner = new GameObject("[TurnPadPreview]");
        var owned = new List<Material>();
        var v = new XRCollab.Measurement.Turns.TurnPadVisual(owner.transform, assets, layer, owned);
        v.SetText("INICIAR TURNO", "mantén el índice");
        Vector3 eye = Vector3.zero;
        Vector3 a = new Vector3(0.08f, -0.42f, 0.42f), b = new Vector3(-0.16f, -0.40f, 0.50f);
        var poseA = new Pose(a, XRCollab.Measurement.Turns.TurnPadVisual.FlatRotation(a, eye, Vector3.forward));
        var poseB = new Pose(b, XRCollab.Measurement.Turns.TurnPadVisual.FlatRotation(b, eye, Vector3.forward));

        var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
        cam.targetTexture = rt;
        var read = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
        var sheet = new List<Texture2D>();
        int n = 0;
        void Shot(string name)
        {
            cam.Render();
            RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            read.Apply();
            RenderTexture.active = null;
            Texture2D composed = Compose(read, bgScaled);
            File.WriteAllBytes(Path.Combine(outDir, $"pad{n++:00}_{name}.png"), composed.EncodeToPNG());
            sheet.Add(composed);
        }
        void Run(float seconds, float hold = 0f, bool hover = false)
        {
            for (float t = 0f; t < seconds; t += 1f / 60f) v.Tick(1f / 60f, hold, 0f, hover);
        }

        v.Appear(poseA);
        Run(0.15f); Shot("aparece_0.15");
        Run(0.2f); Shot("aparece_0.35");
        Run(0.5f); Shot("quieto");
        Run(0.05f, 0.55f, true); Shot("mantener_55");
        v.SetTurnState(true, true);
        v.SetText("TERMINAR TURNO", "turno 1 · 0:00");
        Run(0.12f); Shot("cambio_0.12");
        Run(0.5f); Shot("en_turno");
        v.Pinch(0.6f, b + Vector3.up * 0.02f, eye);
        Run(0.02f); Shot("pinza_60");
        v.PinchDone();
        v.Pinch(-1f, b, eye);
        v.MoveTo(poseB);
        Run(0.25f); Shot("viaje_0.25");
        Run(0.25f); Shot("viaje_0.50");
        Run(0.45f); Shot("llega");
        File.WriteAllBytes(Path.Combine(outDir, "pad-sheet.png"), Sheet(sheet, 3).EncodeToPNG());
        foreach (Material m in owned) UnityEngine.Object.DestroyImmediate(m);
        Debug.Log($"[PadPreview] listo: {outDir}");
    }

    // App (premultiplicada, sRGB) sobre la sala, en espacio lineal.
    private static Texture2D Compose(Texture2D app, Texture2D bg)
    {
        Color[] a = app.GetPixels(), b = bg.GetPixels();
        var o = new Color[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            Color ca = a[i].linear, cb = b[i].linear;
            float alpha = a[i].a;
            o[i] = new Color(ca.r + cb.r * (1f - alpha), ca.g + cb.g * (1f - alpha), ca.b + cb.b * (1f - alpha), 1f).gamma;
        }
        var t = new Texture2D(app.width, app.height, TextureFormat.RGBA32, false);
        t.SetPixels(o);
        t.Apply();
        return t;
    }

    // LoadImage deja un PNG sin alpha en RGB24; el espejo real es RGBA32.
    private static Texture2D ToRgba32(Texture2D src)
    {
        var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
        t.SetPixels32(src.GetPixels32());
        t.Apply();
        return t;
    }

    private static Texture2D BothEyes(Texture2D eye)
    {
        var t = new Texture2D(eye.width * 2, eye.height, TextureFormat.RGBA32, false);
        Color32[] src = eye.GetPixels32();
        var dst = new Color32[t.width * t.height];
        for (int y = 0; y < eye.height; y++)
            for (int x = 0; x < eye.width; x++)
            {
                Color32 c = src[y * eye.width + x];
                dst[y * t.width + x] = c;
                dst[y * t.width + x + eye.width] = c;
            }
        t.SetPixels32(dst);
        t.Apply();
        return t;
    }

    private static Texture2D Scale(Texture2D src, int w, int h)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = src.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
        t.SetPixels(px);
        t.Apply();
        return t;
    }

    private static Texture2D Sheet(List<Texture2D> frames, int cols)
    {
        int w = Width / 2, h = Height / 2, rows = (frames.Count + cols - 1) / cols;
        var t = new Texture2D(w * cols, h * rows, TextureFormat.RGBA32, false);
        Fill(t, Color.black);
        for (int i = 0; i < frames.Count; i++)
        {
            Texture2D small = Scale(frames[i], w, h);
            int cx = (i % cols) * w, cy = (rows - 1 - i / cols) * h;
            t.SetPixels(cx, cy, w, h, small.GetPixels());
        }
        t.Apply();
        return t;
    }

    private static void Fill(Texture2D t, Color c)
    {
        var px = new Color[t.width * t.height];
        for (int i = 0; i < px.Length; i++) px[i] = c;
        t.SetPixels(px);
        t.Apply();
    }

    private static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
