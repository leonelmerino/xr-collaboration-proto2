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

    /// <summary>
    /// Vista previa del botón «orb» (esfera de puntos) con la punta de un índice sintético que se acerca desde abajo a la
    /// derecha: quieto, índice a 20 y a 5 cm, adentro manteniendo, el cambio de turno, en turno, mano rara adentro («solo
    /// con el índice») y recién movido (tenue hasta que la mano sale).
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod FeedbackCuePreview.RenderOrb -cue-bg mesa.png -cue-out carpeta
    /// </summary>
    public static void RenderOrb()
    {
        FeedbackCueAssetsBuilder.Ensure();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        string bgPath = Arg("-cue-bg");
        string outDir = Arg("-cue-out") ?? Path.Combine(Path.GetTempPath(), "orb-preview");
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
        cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(30f, 0f, 0f));
        var lighting = new PassthroughLighting();
        Light key = FeedbackCueDirector.CueLight("CueKeyLight", null);
        Light fill = FeedbackCueDirector.CueLight("CueFillLight", null);
        Texture2D frame = BothEyes(bgScaled);
        for (int i = 0; i < 3; i++) lighting.Sample(frame, cam.transform.rotation);
        lighting.Apply(key, fill, cam.transform.forward);

        CueAssets assets = CueAssets.Load();
        if (!assets.Ok || assets.PadOrb == null) { Debug.LogError("[OrbPreview] faltan recursos:" + assets.Missing + (assets.PadOrb == null ? " PadOrb" : "")); return; }
        var owner = new GameObject("[TurnPadPreview]");
        var owned = new List<Material>();
        var v = new XRCollab.Measurement.Turns.TurnPadVisual(owner.transform, assets, layer, owned);
        float R = XRCollab.Measurement.Turns.TurnPadVisual.OrbRadius;
        Vector3 eye = Vector3.zero, c = new Vector3(0.10f, -0.24f, 0.40f);
        v.Appear(new Pose(c, v.RotationFor(c, eye, Vector3.forward, cam.transform.up)));
        // Índice sintético: llega desde abajo a la derecha (desde la mano del participante) hacia el centro.
        Vector3 axis = (c - new Vector3(0.24f, -0.46f, 0.18f)).normalized;

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
            File.WriteAllBytes(Path.Combine(outDir, $"orb{n++:00}_{name}.png"), composed.EncodeToPNG());
            sheet.Add(composed);
        }
        // gap = distancia de la punta a la superficie (m; negativo = adentro); glow = 0: sin mano cerca.
        void Finger(float gap, float glow, float tether, float hold)
        {
            Vector3 tip = c - axis * (R + gap);
            Vector3 local = v.Root.InverseTransformPoint(tip);
            float w = Mathf.Clamp01(1f - (gap - 0.02f) / 0.13f);
            v.HandLocal = new Vector4(local.x, local.y, local.z, glow > 0f ? w : 0f);
            v.HoldDir = v.Root.InverseTransformDirection(-axis);
        }
        void Run(float seconds, float hold = 0f, float near = 0f)
        {
            for (float t = 0f; t < seconds; t += 1f / 60f) v.Tick(1f / 60f, hold, 0f, near > 0f, near);
        }

        v.SetText("INICIAR TURNO", "toca con el índice");
        Finger(0.5f, 0f, 0f, 0f);
        Run(1.2f); Shot("quieta");
        Finger(0.20f, 0.45f, 0.25f, 0f);
        Run(0.4f, 0f, 0.1f); Shot("indice_20cm");
        Finger(0.05f, 0.95f, 0.85f, 0f);
        Run(0.4f, 0f, 0.9f); Shot("indice_5cm");
        Finger(-0.012f, 1f, 0f, 0.55f);
        Run(0.05f, 0.55f, 1f); Shot("adentro_mantener_55");
        v.SetTurnState(true, true);
        v.SetText("TERMINAR TURNO", "turno 1 · 0:00");
        Run(0.12f, 0f, 1f); Shot("cambio_0.12");
        Finger(0.5f, 0f, 0f, 0f);
        v.SetText("TERMINAR TURNO", "turno 1 · 0:04");
        Run(0.8f); Shot("en_turno");
        v.SetText("TERMINAR TURNO", "solo con el índice");
        Finger(-0.01f, 0.35f, 0f, 0f);
        Run(0.4f, 0f, 1f); Shot("mano_rara_adentro");
        v.Dim = 1f;
        v.SetText("TERMINAR TURNO", "retira la mano");
        Finger(-0.005f, 0f, 0f, 0f);
        Run(0.6f, 0f, 1f); Shot("recien_movida");
        File.WriteAllBytes(Path.Combine(outDir, "orb-sheet.png"), Sheet(sheet, 4).EncodeToPNG());
        foreach (Material m in owned) UnityEngine.Object.DestroyImmediate(m);
        Debug.Log($"[OrbPreview] listo: {outDir}");
    }

    /// <summary>
    /// Vista previa del botón «field» (campo de puntos gaussiano): quieto, dedo bajando (12 → 1 cm), mantener y la onda
    /// del cambio de turno, con una esfera blanca donde está la punta del dedo.
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod FeedbackCuePreview.RenderField -cue-bg sala.jpg -cue-out carpeta
    /// </summary>
    public static void RenderField()
    {
        FeedbackCueAssetsBuilder.Ensure();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        string bgPath = Arg("-cue-bg");
        string outDir = Arg("-cue-out") ?? Path.Combine(Path.GetTempPath(), "field-preview");
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
        cam.fieldOfView = 50f;
        cam.nearClipPlane = 0.02f;
        cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(45f, 0f, 0f));
        Light key = FeedbackCueDirector.CueLight("CueKeyLight", null);
        key.transform.rotation = Quaternion.Euler(60f, -30f, 0f);

        CueAssets assets = CueAssets.Load();
        if (!assets.Ok) { Debug.LogError("[FieldPreview] faltan recursos:" + assets.Missing); return; }
        var owner = new GameObject("[TurnPadPreview]");
        var owned = new List<Material>();
        var v = new XRCollab.Measurement.Turns.TurnPadVisual(owner.transform, assets, layer, owned);
        v.Style = XRCollab.Measurement.Turns.TurnPadVisual.PadStyle.Field;
        v.SetText("INICIAR TURNO", "mantén el índice");
        Vector3 at = new Vector3(0f, -0.42f, 0.40f);
        v.Appear(new Pose(at, XRCollab.Measurement.Turns.TurnPadVisual.FlatRotation(at, Vector3.zero, Vector3.forward)));

        GameObject tip = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        tip.layer = layer;
        tip.transform.localScale = Vector3.one * 0.016f;
        tip.GetComponent<Renderer>().sharedMaterial = assets.Metal;
        tip.SetActive(false);

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
            File.WriteAllBytes(Path.Combine(outDir, $"field{n++:00}_{name}.png"), composed.EncodeToPNG());
            sheet.Add(composed);
        }
        void Finger(Vector3 local)
        {
            float d = local.magnitude;
            v.HandLocal = new Vector4(local.x, local.y, local.z, 1f - Mathf.Clamp01((d - 0.04f) / 0.08f));
            tip.SetActive(true);
            tip.transform.position = v.Root.TransformPoint(local);
        }
        void Run(float seconds, float hold = 0f, bool hover = false, float near = 0f)
        {
            for (float t = 0f; t < seconds; t += 1f / 60f) v.Tick(1f / 60f, hold, 0f, hover, near);
        }

        Run(1.2f); Shot("quieto");
        Run(0.9f); Shot("quieto_respira");
        Finger(new Vector3(0.015f, -0.01f, -0.11f)); Run(0.3f, 0f, false, 0.15f); Shot("dedo_11cm");
        Finger(new Vector3(0.012f, -0.008f, -0.07f)); Run(0.3f, 0f, false, 0.5f); Shot("dedo_7cm");
        Finger(new Vector3(0.008f, -0.004f, -0.04f)); Run(0.3f, 0f, true, 0.8f); Shot("dedo_4cm");
        Finger(new Vector3(0.004f, 0f, -0.015f)); Run(0.3f, 0f, true, 1f); Shot("dedo_1.5cm");
        Run(0.05f, 0.45f, true, 1f); Shot("mantener_45");
        Run(0.05f, 0.9f, true, 1f); Shot("mantener_90");
        v.SetTurnState(true, true);
        v.SetText("TERMINAR TURNO", "turno 1 · 0:00");
        tip.SetActive(false);
        v.HandLocal = Vector4.zero;
        Run(0.12f); Shot("onda_0.12");
        Run(0.2f); Shot("onda_0.32");
        Run(1.2f); Shot("en_turno");
        File.WriteAllBytes(Path.Combine(outDir, "field-sheet.png"), Sheet(sheet, 3).EncodeToPNG());
        foreach (Material m in owned) UnityEngine.Object.DestroyImmediate(m);
        Debug.Log($"[FieldPreview] listo: {outDir}");
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
