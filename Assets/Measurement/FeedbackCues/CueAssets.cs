using TMPro;
using UnityEngine;

namespace XRCollab.Measurement.Cues
{
    /// <summary>
    /// Materiales y texturas de las señales. Los materiales viven en Resources/FeedbackCues (los crea
    /// Assets/Editor/FeedbackCueAssetsBuilder.cs, también al compilar con MeasurementBuild): así el build incluye
    /// los shaders y sus variantes. Las texturas de brillo se generan en código.
    /// </summary>
    public sealed class CueAssets
    {
        public const string ResourceFolder = "FeedbackCues";
        public const string TextMaterialName = "CueText";
        public const string MetalMaterialName = "CueMetal";
        public const string AdditiveMaterialName = "CueAdditive";
        public const string AlphaMaterialName = "CueAlpha";
        public const string PadFieldMaterialName = "PadField";   // botón de turno «field» (TurnPad/Shaders/PadField)
        public const string PadFontName = "PadFont SDF";          // Geist (OFL) para el texto del botón «field»
        public const string PadTextMaterialName = "PadText";
        public const string PadOrbMaterialName = "PadOrb";          // botón de turno «orb» (TurnPad/Shaders/PadOrb)

        public TMP_FontAsset Font, PadFont;
        public Material PadText;
        public Material Text, Metal, Additive, Alpha, PadField, PadOrb;
        public Texture2D Glow, Rays, Ring, Spark, Shadow;

        public bool Ok => Font != null && Text != null && Metal != null && Additive != null && Alpha != null;

        public string Missing =>
            (Font == null ? " fuente" : "") + (Text == null ? " " + TextMaterialName : "") + (Metal == null ? " " + MetalMaterialName : "") +
            (Additive == null ? " " + AdditiveMaterialName : "") + (Alpha == null ? " " + AlphaMaterialName : "");

        public static CueAssets Load()
        {
            var a = new CueAssets
            {
                Text = Resources.Load<Material>(ResourceFolder + "/" + TextMaterialName),
                Metal = Resources.Load<Material>(ResourceFolder + "/" + MetalMaterialName),
                Additive = Resources.Load<Material>(ResourceFolder + "/" + AdditiveMaterialName),
                Alpha = Resources.Load<Material>(ResourceFolder + "/" + AlphaMaterialName),
                PadField = Resources.Load<Material>(ResourceFolder + "/" + PadFieldMaterialName),
                PadOrb = Resources.Load<Material>(ResourceFolder + "/" + PadOrbMaterialName),
                PadFont = Resources.Load<TMP_FontAsset>(ResourceFolder + "/" + PadFontName),
                PadText = Resources.Load<Material>(ResourceFolder + "/" + PadTextMaterialName),
                Font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF") ?? TMP_Settings.defaultFontAsset,
            };
            a.Glow = Radial("CueGlow", 128, r => Mathf.Pow(Mathf.Clamp01(1f - r), 2.2f));
            a.Shadow = Radial("CueShadow", 128, r => Mathf.SmoothStep(1f, 0f, r));
            a.Ring = Radial("CueRing", 128, r => Mathf.Exp(-Mathf.Pow((r - 0.82f) / 0.07f, 2f)));
            a.Spark = Radial("CueSpark", 64, r => Mathf.Pow(Mathf.Clamp01(1f - r), 3f) + 0.6f * Mathf.Exp(-r * 12f));
            a.Rays = RaysTexture(256);
            return a;
        }

        private static Texture2D Radial(string name, int size, System.Func<float, float> alphaOfRadius)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float a = Mathf.Clamp01(alphaOfRadius(Mathf.Sqrt(dx * dx + dy * dy)));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }

        // Rayos de luz radiales (para «great»): 18 haces de ancho y largo variables que se apagan hacia afuera.
        private static Texture2D RaysTexture(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "CueRays", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var px = new Color32[size * size];
            const int beams = 18;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ang = Mathf.Atan2(dy, dx);
                    float beam = 0f;
                    for (int b = 0; b < beams; b++)
                    {
                        float center = b * Mathf.PI * 2f / beams + 0.17f * Mathf.Sin(b * 2.3f);
                        float d = Mathf.DeltaAngle(ang * Mathf.Rad2Deg, center * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                        float width = 0.05f + 0.035f * Mathf.Abs(Mathf.Sin(b * 1.7f));
                        float len = 0.7f + 0.3f * Mathf.Abs(Mathf.Sin(b * 3.1f));
                        beam = Mathf.Max(beam, Mathf.Exp(-(d * d) / (width * width)) * Mathf.Clamp01(1f - r / len));
                    }
                    float a = beam * Mathf.SmoothStep(0f, 0.25f, r);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
                }
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }
    }
}
