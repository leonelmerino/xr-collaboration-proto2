using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace XRCollab.Measurement.Cues
{
    /// <summary>
    /// Iluminación real de la sala para las señales 3D, sacada de lo que ve el participante (el espejo del visor:
    /// la imagen de las cámaras de passthrough). No hay acceso directo a las cámaras del visor por VIVE Streaming,
    /// pero el espejo (scrcpy) trae esa misma imagen.
    ///
    /// Cada ~0,25 s se toman ~1.600 puntos del ojo izquierdo, se pasan a direcciones del mundo con la orientación de la
    /// cabeza y se acumulan en un mapa de entorno de cubo de baja resolución (6×8×8 celdas, media móvil): a medida que
    /// la persona mira alrededor, el mapa «aprende» la sala. De ese mapa salen:
    ///   - la luz ambiente (armónicos esféricos, como una sonda de luz real);
    ///   - una luz principal (dirección y color de las zonas más brillantes: tubos del techo, ventana);
    ///   - un cubemap de reflejos para el metal de los íconos (reflejan la sala de verdad).
    /// Mientras no hay imagen, una luz de oficina neutra. Todo en espacio lineal (el proyecto es Linear).
    /// </summary>
    public sealed class PassthroughLighting
    {
        private const int Cells = 8;                 // celdas por lado de cada cara del mapa acumulado
        private const int CubeSize = 32;             // resolución del cubemap de reflejos
        private const int Grid = 40;                 // puntos por lado al muestrear el ojo
        private const float TanHalfFov = 1.15f;      // ~49° de medio campo horizontal del visor (aprox.)
        private const float Rate = 0.35f;            // peso de cada muestra nueva en la media móvil

        private readonly Color[] _cells = new Color[6 * Cells * Cells];
        private readonly float[] _weight = new float[6 * Cells * Cells];
        private readonly Vector3[] _cellDir = new Vector3[6 * Cells * Cells];
        private Cubemap _cube;
        private int _filled;

        public bool HasData => _filled > 0;
        /// <summary>Fracción del mapa (0–1) ya vista.</summary>
        public float Coverage => _filled / (float)_cells.Length;
        public Color AmbientLinear { get; private set; } = new Color(0.32f, 0.33f, 0.35f);
        public Color KeyColor { get; private set; } = new Color(1f, 0.96f, 0.9f);
        public float KeyIntensity { get; private set; } = 0.9f;
        /// <summary>Hacia dónde viaja la luz principal (Light.forward), en el mundo.</summary>
        public Vector3 KeyForward { get; private set; } = new Vector3(0.2f, -0.85f, 0.45f).normalized;

        public PassthroughLighting()
        {
            for (int f = 0; f < 6; f++)
                for (int j = 0; j < Cells; j++)
                    for (int i = 0; i < Cells; i++)
                        _cellDir[Index(f, i, j)] = FaceDirection(f, (i + 0.5f) / Cells * 2f - 1f, (j + 0.5f) / Cells * 2f - 1f);
        }

        /// <summary>Acumula una imagen del espejo (ambos ojos lado a lado) vista con <paramref name="head"/> (rotación en el mundo).</summary>
        public void Sample(Texture2D frame, Quaternion head)
        {
            if (frame == null || frame.format != TextureFormat.RGBA32) return;
            NativeArray<Color32> px = frame.GetPixelData<Color32>(0);
            int w = frame.width, h = frame.height;
            float eyeW = w * 0.5f;
            float cx = eyeW * 0.5f, cy = h * 0.5f;
            float half = Mathf.Min(eyeW, h) * 0.5f;
            float radius = half * 0.94f;              // fuera de este círculo está el borde negro del lente
            for (int gy = 0; gy < Grid; gy++)
            {
                for (int gx = 0; gx < Grid; gx++)
                {
                    float u = ((gx + 0.5f) / Grid) * 2f - 1f, v = ((gy + 0.5f) / Grid) * 2f - 1f;
                    if (u * u + v * v > 1f) continue;
                    int x = Mathf.Clamp((int)(cx + u * radius), 0, w - 1), y = Mathf.Clamp((int)(cy + v * radius), 0, h - 1);
                    Color32 c32 = px[y * w + x];
                    if (c32.r + c32.g + c32.b < 9) continue;   // negro del borde o de la capa vacía
                    Vector3 dirView = new Vector3(u * radius / half * TanHalfFov, v * radius / half * TanHalfFov, 1f).normalized;
                    Vector3 dir = head * dirView;
                    int k = CellOf(dir);
                    Color lin = ((Color)c32).linear;
                    if (_weight[k] <= 0f) { _cells[k] = lin; _filled++; _weight[k] = 1f; }
                    else _cells[k] = Color.Lerp(_cells[k], lin, Rate);
                }
            }
        }

        /// <summary>
        /// Recalcula la iluminación y la aplica: ambiente (armónicos esféricos), <paramref name="key"/> (luz principal
        /// real de la sala), <paramref name="fill"/> (relleno suave desde quien mira) y reflejos.
        /// </summary>
        public void Apply(Light key, Light fill, Vector3 headForward)
        {
            int n = _cells.Length;
            var env = new Color[n];
            Color mean = Color.black, meanUp = Color.black;
            int cUp = 0;
            for (int k = 0; k < n; k++)
            {
                if (_weight[k] <= 0f) continue;
                mean += _cells[k];
                if (_cellDir[k].y > 0.3f) { meanUp += _cells[k]; cUp++; }
            }
            bool data = _filled > 0;
            if (data)
            {
                mean /= _filled;
                meanUp = cUp > 0 ? meanUp / cUp : mean;
            }
            else
            {
                mean = new Color(0.3f, 0.31f, 0.33f);
                meanUp = new Color(0.42f, 0.43f, 0.45f);
            }
            for (int k = 0; k < n; k++)
            {
                if (_weight[k] > 0f) { env[k] = _cells[k]; continue; }
                // Sin ver: arriba como el promedio de arriba, abajo más oscuro (piso), el resto el promedio.
                float y = _cellDir[k].y;
                env[k] = y > 0.3f ? meanUp : y < -0.3f ? mean * 0.55f : mean;
            }

            // Exposición: nivel de luz de la sala desde las superficies claras (percentil 85 de luminancia ≈ pared o
            // mesa clara = luz × albedo ~0,7), no desde el promedio, que un monitor negro o una ventana arrastran.
            // Así un objeto claro de la señal queda tan iluminado como una pared clara de esa sala.
            float level = 0.75f;
            if (data)
            {
                var lums = new List<float>(_filled);
                for (int k = 0; k < n; k++) if (_weight[k] > 0f) lums.Add(Lum(_cells[k]));
                lums.Sort();
                level = Mathf.Clamp(lums[Mathf.Min(lums.Count - 1, (int)(lums.Count * 0.85f))] / 0.7f, 0.42f, 1.6f);   // piso: siempre legible
            }
            Exposure = level;
            float meanLum = Mathf.Max(Lum(mean), 1e-3f);
            float scale = level * AmbientShare / meanLum;
            var scaled = new Color[n];
            for (int k = 0; k < n; k++) scaled[k] = Desaturate(env[k], 0.35f) * scale;

            // Ambiente: cada celda como una luz direccional chica (peso 4/N: un entorno uniforme de color c da c).
            var sh = new SphericalHarmonicsL2();
            float wCell = 4f / n;
            for (int k = 0; k < n; k++) sh.AddDirectionalLight(_cellDir[k], scaled[k], wCell);
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = sh;
            AmbientLinear = Desaturate(mean, 0.35f) * scale;

            // Luz principal real: las celdas más brillantes respecto del promedio (tubos del techo, ventana).
            Vector3 dirSum = Vector3.zero;
            Color keySum = Color.black;
            float keyW = 0f;
            for (int k = 0; k < n; k++)
            {
                // Las luces de una sala están arriba: una pantalla o una ventana baja brillan pero casi no iluminan.
                float excess = (Lum(env[k]) / meanLum - 1.25f) * Mathf.Clamp01(0.2f + _cellDir[k].y);
                if (excess <= 0f) continue;
                dirSum += _cellDir[k] * excess;
                keySum += env[k] * excess;
                keyW += excess;
            }
            Vector3 flat = new Vector3(headForward.x, 0f, headForward.z);
            flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
            if (data && keyW > 0f && dirSum.sqrMagnitude > 1e-6f)
            {
                KeyForward = -dirSum.normalized;
                KeyColor = Normalized(Color.Lerp(keySum / keyW, Color.white, 0.25f));
                KeyIntensity = level * 0.85f;
            }
            else
            {
                // Oficina neutra: desde arriba y un poco detrás de la persona.
                KeyForward = -(Vector3.up * 0.85f - flat * 0.4f).normalized;
                KeyColor = new Color(1f, 0.96f, 0.9f);
                KeyIntensity = level * 0.8f;
            }
            Orient(key, KeyForward, KeyColor, KeyIntensity);

            // Relleno: desde quien mira y un poco arriba, del color del ambiente (el rebote de la sala). Es el truco de
            // composición de siempre: la luz real de la sala suele venir de adelante-arriba y dejaría la cara del
            // texto a contraluz.
            FillColor = Normalized(Color.Lerp(AmbientLinear, Color.white, 0.5f));
            FillIntensity = level * 0.55f;
            Orient(fill, Quaternion.AngleAxis(22f, Vector3.Cross(Vector3.up, flat)) * flat, FillColor, FillIntensity);

            UpdateReflections(scaled, 1.6f);   // reflejos un poco más vivos que el ambiente: el metal se lee como metal
        }

        private const float AmbientShare = 0.5f;

        /// <summary>Nivel de luz de la sala estimado (lineal, ~luminancia de una superficie blanca).</summary>
        public float Exposure { get; private set; } = 0.75f;
        public Color FillColor { get; private set; } = Color.white;
        public float FillIntensity { get; private set; } = 0.4f;

        private static void Orient(Light light, Vector3 forward, Color color, float intensity)
        {
            if (light == null) return;
            light.transform.rotation = Quaternion.LookRotation(forward, Mathf.Abs(forward.y) > 0.95f ? Vector3.forward : Vector3.up);
            light.color = color;
            light.intensity = intensity;
        }

        private static Color Normalized(Color c)
        {
            float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b), 1e-4f);
            return new Color(c.r / m, c.g / m, c.b / m);
        }

        private static Color Desaturate(Color c, float amount)
        {
            float l = Lum(c);
            return Color.Lerp(c, new Color(l, l, l), amount);
        }

        private void UpdateReflections(Color[] env, float gain)
        {
            if (_cube == null)
            {
                _cube = new Cubemap(CubeSize, TextureFormat.RGBAHalf, true) { name = "PassthroughReflections", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            }
            var face = new Color[CubeSize * CubeSize];
            for (int f = 0; f < 6; f++)
            {
                for (int y = 0; y < CubeSize; y++)
                {
                    for (int x = 0; x < CubeSize; x++)
                    {
                        // Interpolación bilineal entre celdas de la misma cara.
                        float fx = (x + 0.5f) / CubeSize * Cells - 0.5f, fy = (y + 0.5f) / CubeSize * Cells - 0.5f;
                        int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, Cells - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, Cells - 1);
                        int x1 = Mathf.Min(x0 + 1, Cells - 1), y1 = Mathf.Min(y0 + 1, Cells - 1);
                        float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
                        Color a = Color.Lerp(env[Index(f, x0, y0)], env[Index(f, x1, y0)], tx);
                        Color b = Color.Lerp(env[Index(f, x0, y1)], env[Index(f, x1, y1)], tx);
                        // Las caras del cubemap van de arriba hacia abajo en Y.
                        face[(CubeSize - 1 - y) * CubeSize + x] = Color.Lerp(a, b, ty) * gain;
                    }
                }
                _cube.SetPixels(face, (CubemapFace)f);
            }
            _cube.Apply(true, false);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = _cube;
            RenderSettings.reflectionIntensity = 1f;
        }

        // ---------------- cubo: dirección ↔ celda ----------------

        private static int Index(int face, int i, int j) => face * Cells * Cells + j * Cells + i;

        // Convención de caras de Unity (CubemapFace: +X, -X, +Y, -Y, +Z, -Z) con u a la derecha y v hacia arriba.
        private static Vector3 FaceDirection(int face, float u, float v)
        {
            switch (face)
            {
                case 0: return new Vector3(1f, v, -u).normalized;
                case 1: return new Vector3(-1f, v, u).normalized;
                case 2: return new Vector3(u, 1f, -v).normalized;
                case 3: return new Vector3(u, -1f, v).normalized;
                case 4: return new Vector3(u, v, 1f).normalized;
                default: return new Vector3(-u, v, -1f).normalized;
            }
        }

        private static int CellOf(Vector3 d)
        {
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            int face;
            float u, v;
            if (ax >= ay && ax >= az) { face = d.x > 0 ? 0 : 1; u = d.x > 0 ? -d.z / ax : d.z / ax; v = d.y / ax; }
            else if (ay >= az) { face = d.y > 0 ? 2 : 3; u = d.x / ay; v = d.y > 0 ? -d.z / ay : d.z / ay; }
            else { face = d.z > 0 ? 4 : 5; u = d.z > 0 ? d.x / az : -d.x / az; v = d.y / az; }
            int i = Mathf.Clamp((int)((u * 0.5f + 0.5f) * Cells), 0, Cells - 1);
            int j = Mathf.Clamp((int)((v * 0.5f + 0.5f) * Cells), 0, Cells - 1);
            return Index(face, i, j);
        }

        private static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        public static string Hex(Color linear) => "#" + ColorUtility.ToHtmlStringRGB(linear.gamma);
    }
}
