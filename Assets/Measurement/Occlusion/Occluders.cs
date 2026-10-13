using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using XRCollab.Measurement.Recording;

namespace XRCollab.Measurement.Occlusion
{
    /// <summary>
    /// Oclusión de lo virtual por cosas reales (passthrough). Dos modos:
    ///  - <b>suave</b> (por defecto, <c>XRCollab/HandCut</c>): las formas se dibujan después de todo lo virtual y lo
    ///    «borran» multiplicando color y alpha, con el borde difuminado (N·V) e inflado unos mm. Un error chico de
    ///    forma o de tiempo entre la mano real y la estimada no se nota como un corte duro.
    ///  - <b>duro</b> (<c>XRCollab/DepthOnly</c>): formas que solo escriben profundidad antes de lo virtual.
    ///
    /// Cualquier módulo puede aportar formas: implementa <see cref="IOccluderSource"/> y se registra con
    /// <see cref="Register"/>. Cada cuadro, antes de renderizar (Application.onBeforeRender), se le pide a cada fuente que
    /// dibuje con <see cref="DrawCapsule"/> / <see cref="DrawBox"/> / <see cref="DrawMesh"/> (mundo), o que actualice
    /// sus propios renderers con <see cref="CurrentMaterials"/>. Fuente incluida: <see cref="HandOccluderSource"/>
    /// (malla de mano con piel, conducida por las 26 articulaciones, más el antebrazo). Por venir: mesa y torre.
    ///
    /// Por qué así: VIVE Streaming en PC no entrega profundidad ni malla de la sala (ver README), así que la oclusión
    /// sale de lo que sí se sigue: las manos, y en el futuro objetos calibrados.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Occluders : MonoBehaviour
    {
        /// <summary>Capa sin nombre de los oclusores (29 señales, 30 punto de mirada, 31 RecordingIndicator).</summary>
        public const int Layer = 28;
        public const string MaterialResource = "Occlusion/DepthOnly";
        public const string SoftMaterialResource = "Occlusion/HandCut";
        public const string DebugMaterialResource = "Occlusion/OccluderDebug";

        public static Occluders Instance { get; private set; }

        /// <summary>Apagar para comparar (o si molesta): lo virtual vuelve a dibujarse encima de todo.</summary>
        public static bool Enabled = true;

        /// <summary>Dibuja además los oclusores en color translúcido, para ver en un «snapshot» si calzan con la mano real.</summary>
        public static bool ShowDebug;

        /// <summary>Recorte suave (true) o solo profundidad (false).</summary>
        public static bool Soft = true;

        public const float DefaultFeather = 0.15f;
        public const float DefaultInflateMm = 5f;

        /// <summary>Cambia cada vez que cambian modo o materiales: las fuentes con renderers propios los reasignan.</summary>
        public static int Version { get; private set; }

        private readonly List<IOccluderSource> _sources = new List<IOccluderSource>();
        private Material _material, _softMaterial, _debugMaterial;
        private Mesh _capsule, _cube;
        private Camera _maskedCamera;
        private Material[] _current = Array.Empty<Material>();
        private string _handsKind = "";

        public Material Material => _material;

        /// <summary>Materiales con que se dibuja una forma ahora (vacío si la oclusión está apagada).</summary>
        public Material[] CurrentMaterials => _current;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            if (Instance != null) return;
            var go = new GameObject("[Occluders]");
            DontDestroyOnLoad(go);
            go.AddComponent<Occluders>();
        }

        public static void Register(IOccluderSource source)
        {
            if (Instance != null && source != null && !Instance._sources.Contains(source)) Instance._sources.Add(source);
        }

        public static void Unregister(IOccluderSource source)
        {
            if (Instance != null) Instance._sources.Remove(source);
        }

        private void Awake()
        {
            Instance = this;
            _material = Resources.Load<Material>(MaterialResource);
            _softMaterial = Resources.Load<Material>(SoftMaterialResource);
            _debugMaterial = Resources.Load<Material>(DebugMaterialResource);
            if (_material == null && _softMaterial == null)
            {
                Debug.LogWarning($"[Occluders] faltan Resources/{MaterialResource} y {SoftMaterialResource} (Assets/Editor/OcclusionAssetsBuilder.cs): sin oclusión");
                enabled = false;
                return;
            }
            if (_softMaterial == null) Soft = false;
            else
            {
                // Copia propia (no se toca el asset) con los valores por defecto: borde suave angosto (un halo ancho
                // deja ver lo virtual «a medias» dentro del contorno del dedo) y un poco de inflado.
                _softMaterial = new Material(_softMaterial);
                _softMaterial.SetFloat("_Feather", DefaultFeather);
                SetInflate(DefaultInflateMm);
            }
            ApplyCommandLine();
            if (_material != null) _material.enableInstancing = true;
            _capsule = PrimitiveMesh(PrimitiveType.Capsule);
            _cube = PrimitiveMesh(PrimitiveType.Cube);
            RefreshMaterials();
            var hands = new HandOccluderSource();
            _handsKind = hands.Kind;
            Register(hands);
            Application.onBeforeRender += Draw;
            AppControlHooks.Occlusion = HandleCommand;
            Debug.Log($"[Occluders] listos (capa {Layer}, {(Soft ? "recorte suave" : "solo profundidad")}): manos y antebrazos tapan lo virtual");
        }

        private void OnDestroy()
        {
            Application.onBeforeRender -= Draw;
            if (AppControlHooks.Occlusion == HandleCommand) AppControlHooks.Occlusion = null;
            foreach (IOccluderSource s in _sources) (s as IDisposable)?.Dispose();
            if (_maskedCamera != null) _maskedCamera.cullingMask &= ~(1 << Layer);
            if (Instance == this) Instance = null;
        }

        // -occlusion-offset-ms N, -occlusion-inflate-mm N, -occlusion-hard
        private void ApplyCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : "";
                switch (args[i].ToLowerInvariant())
                {
                    case "-occlusion-hard": Soft = false; break;
                    case "-occlusion-offset-ms": if (TryFloat(next, out float o)) HandOccluderSource.OffsetMs = Mathf.Clamp(o, -200f, 200f); break;
                    case "-occlusion-inflate-mm": if (TryFloat(next, out float f)) SetInflate(f); break;
                }
            }
        }

        private static bool TryFloat(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private void RefreshMaterials()
        {
            Material main = Soft && _softMaterial != null ? _softMaterial : _material;
            if (!Enabled || main == null) _current = Array.Empty<Material>();
            else if (ShowDebug && _debugMaterial != null) _current = new[] { main, _debugMaterial };
            else _current = new[] { main };
            Version++;
        }

        private void SetInflate(float mm)
        {
            if (_softMaterial != null) _softMaterial.SetFloat("_Inflate", Mathf.Clamp(mm, 0f, 30f) / 1000f);
        }

        /// <summary>
        /// Orden «occlusion …» del canal de control. null = ok, si no el error.
        /// on | off | show | hide | soft | hard | offset=MS (+ predice, - retrasa) | inflate=MM | feather=0..1
        /// </summary>
        public string HandleCommand(string arg)
        {
            string a = (arg ?? "").Trim().ToLowerInvariant();
            int eq = a.IndexOf('=');
            if (eq > 0)
            {
                string key = a.Substring(0, eq);
                if (!TryFloat(a.Substring(eq + 1), out float v)) return $"valor inválido en «{a}»";
                switch (key)
                {
                    case "offset": HandOccluderSource.OffsetMs = Mathf.Clamp(v, -200f, 200f); break;
                    case "inflate":
                        if (_softMaterial == null) return "sin material de recorte suave";
                        SetInflate(v); break;
                    case "feather":
                        if (_softMaterial == null) return "sin material de recorte suave";
                        _softMaterial.SetFloat("_Feather", Mathf.Clamp(v, 0.02f, 1f)); break;
                    default: return "occlusion offset=MS | inflate=MM | feather=0..1";
                }
            }
            else
            {
                switch (a)
                {
                    case "on": Enabled = true; break;
                    case "off": Enabled = false; break;
                    case "show":
                        if (_debugMaterial == null) return $"falta Resources/{DebugMaterialResource} (Assets/Editor/OcclusionAssetsBuilder.cs)";
                        ShowDebug = true; break;
                    case "hide": ShowDebug = false; break;
                    case "soft":
                        if (_softMaterial == null) return $"falta Resources/{SoftMaterialResource}";
                        Soft = true; break;
                    case "hard":
                        if (_material == null) return $"falta Resources/{MaterialResource}";
                        Soft = false; break;
                    default: return "occlusion on|off|show|hide|soft|hard|offset=MS|inflate=MM|feather=0..1";
                }
            }
            RefreshMaterials();
            Debug.Log($"[Occluders] {Describe()}");
            return null;
        }

        public string Describe() =>
            $"{(Enabled ? "activos" : "apagados")}, {(Soft ? "suave" : "duro")}{(ShowDebug ? ", visibles" : "")}, " +
            $"desfase {HandOccluderSource.OffsetMs:+0;-0;0} ms, inflado {InflateMm:0.#} mm, borde {Feather:0.00}";

        public float InflateMm => _softMaterial != null ? _softMaterial.GetFloat("_Inflate") * 1000f : 0f;
        public float Feather => _softMaterial != null ? _softMaterial.GetFloat("_Feather") : 0f;

        /// <summary>Objeto JSON para "app" del canal de control.</summary>
        public string HealthJson()
        {
            string handsKind = _handsKind;
            var c = CultureInfo.InvariantCulture;
            return "{\"mode\":\"" + (!Enabled ? "off" : Soft ? "soft" : "hard") + "\"" +
                   ",\"show\":" + (ShowDebug ? "true" : "false") +
                   ",\"offsetMs\":" + HandOccluderSource.OffsetMs.ToString("0", c) +
                   ",\"inflateMm\":" + InflateMm.ToString("0.#", c) +
                   ",\"feather\":" + Feather.ToString("0.00", c) +
                   ",\"hands\":\"" + handsKind + "\"}";
        }

        private void Draw()
        {
            Camera cam = Camera.main;
            UpdateCullingMask(cam);
            if (cam == null) return;
            foreach (IOccluderSource s in _sources) s.DrawOccluders(this, cam);
        }

        // MeasurementMode deja la máscara de la cámara en 0: la capa de los oclusores se agrega siempre (no dibujan color).
        private void UpdateCullingMask(Camera cam)
        {
            int bit = 1 << Layer;
            if (_maskedCamera != null && _maskedCamera != cam) { _maskedCamera.cullingMask &= ~bit; _maskedCamera = null; }
            if (cam != null && Enabled && (cam.cullingMask & bit) == 0) { cam.cullingMask |= bit; _maskedCamera = cam; }
            if (cam != null && !Enabled && (cam.cullingMask & bit) != 0 && _maskedCamera == cam) { cam.cullingMask &= ~bit; _maskedCamera = null; }
        }

        // ---------------- primitivas (mundo, un cuadro) ----------------

        /// <summary>Cápsula de <paramref name="a"/> a <paramref name="b"/> con radio <paramref name="radius"/> (metros).</summary>
        public void DrawCapsule(Vector3 a, Vector3 b, float radius) => DrawMesh(_capsule, CapsuleMatrix(a, b, radius));

        public void DrawBox(Vector3 center, Quaternion rotation, Vector3 size) =>
            DrawMesh(_cube, Matrix4x4.TRS(center, rotation, size));

        public void DrawMesh(Mesh mesh, Matrix4x4 localToWorld)
        {
            foreach (Material m in _current) Graphics.DrawMesh(mesh, localToWorld, m, Layer);
        }

        public Mesh CapsuleMesh => _capsule;

        /// <summary>Matriz de la cápsula de Unity (2 de alto con tapas, 1 de diámetro) que va de a a b con ese radio.</summary>
        public static Matrix4x4 CapsuleMatrix(Vector3 a, Vector3 b, float radius)
        {
            Vector3 axis = b - a;
            float len = axis.magnitude;
            Quaternion rot = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, axis / len) : Quaternion.identity;
            var scale = new Vector3(radius * 2f, (len + radius * 2f) * 0.5f, radius * 2f);
            return Matrix4x4.TRS((a + b) * 0.5f, rot, scale);
        }

        private static Mesh PrimitiveMesh(PrimitiveType type)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Destroy(go);
            return mesh;
        }
    }

    /// <summary>Algo que aporta formas de oclusión cada cuadro (ver <see cref="Occluders"/>).</summary>
    public interface IOccluderSource
    {
        void DrawOccluders(Occluders occluders, Camera camera);
    }
}
