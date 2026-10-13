using UnityEngine;
using XRCollab.Measurement.Mirroring;
using XRCollab.Measurement.Recording;

namespace XRCollab.Measurement.Cues
{
    /// <summary>
    /// Señales 3D que el equipo de la sesión le muestra al participante en el visor (panel de sesión,
    /// tools\lab-dashboard): «¡PERFECTO!», «MAL COLOCADO», «DEMASIADO LENTO»… Llegan por el canal de control de la
    /// app (127.0.0.1:47811, orden «cue tono|ícono|título|subtítulo», ver <see cref="FeedbackCueSpec"/>).
    ///
    /// La señal aparece fija en la sala (no pegada a la cabeza), 1,4 m delante de donde mira la persona y un poco
    /// arriba de la línea de la vista (no tapa la torre), de frente a ella. Una nueva reemplaza a la anterior.
    /// Iluminación: <see cref="PassthroughLighting"/> (solo en modo medición; en VR la escena tiene su propia luz).
    /// Cada señal queda en el CSV de eventos (FEEDBACK_CUE) y como marca del video si se está grabando.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FeedbackCueDirector : MonoBehaviour
    {
        public const int CueLayer = 29;   // capa sin nombre (30 = punto de mirada, 31 = RecordingIndicator)
        private const string LogPrefix = "[FeedbackCues] ";
        private const float Distance = 1.4f;
        private const float RaiseDegrees = 6f;
        private const float SampleSeconds = 0.25f;
        private const float ApplySeconds = 1f;

        public static FeedbackCueDirector Instance { get; private set; }

        /// <summary>El botón de turno (Assets/Measurement/TurnPad) usa la misma capa y luces: la capa queda visible mientras esté.</summary>
        public static bool PadVisible;

        public PassthroughLighting Lighting { get; } = new PassthroughLighting();
        public string CurrentTitle => _current != null && !_current.Leaving ? _current.Spec.Title : "";
        public int Shown { get; private set; }

        private CueAssets _assets;
        private FeedbackCueVisual _current;
        private Light _key, _fill;
        private HeadsetMirror _mirror;
        private Camera _maskedCamera;
        private float _nextSample, _nextApply, _nextLookup;
        private bool _estimate;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (Instance != null) return;
            var go = new GameObject("[FeedbackCues]");
            DontDestroyOnLoad(go);
            go.AddComponent<FeedbackCueDirector>();
#endif
        }

        private void Awake()
        {
            Instance = this;
            _assets = CueAssets.Load();
            if (!_assets.Ok) Debug.LogWarning($"{LogPrefix}faltan recursos:{_assets.Missing} (Assets/Editor/FeedbackCueAssetsBuilder.cs): sin señales 3D");
            _estimate = MeasurementMode.IsActive;

            _key = CueLight("CueKeyLight", transform);
            _fill = CueLight("CueFillLight", transform);
            if (_estimate) Lighting.Apply(_key, _fill, Vector3.forward);
            else
            {
                _key.intensity = 0.8f; _key.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                _fill.intensity = 0.35f; _fill.transform.rotation = Quaternion.Euler(20f, 0f, 0f);
            }

            AppControlHooks.Cue = HandleCue;
            Debug.Log($"{LogPrefix}listas (capa {CueLayer}, iluminación {(_estimate ? "de la sala por el espejo del visor" : "de la escena VR")})");
        }

        /// <summary>Luz direccional que solo ilumina la capa de las señales.</summary>
        public static Light CueLight(string name, Transform parent)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = 1 << CueLayer;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            return light;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (AppControlHooks.Cue == HandleCue) AppControlHooks.Cue = null;
            if (_maskedCamera != null) _maskedCamera.cullingMask &= ~(1 << CueLayer);
        }

        /// <summary>Orden «cue …» del canal de control (hilo principal). null = ok, si no el error.</summary>
        public string HandleCue(string text)
        {
            if (text.Trim().Equals("clear", System.StringComparison.OrdinalIgnoreCase))
            {
                if (_current != null) _current.Dismiss();
                return null;
            }
            if (!_assets.Ok) return "faltan recursos de las señales:" + _assets.Missing;
            FeedbackCueSpec spec = FeedbackCueSpec.Parse(text, out string error);
            if (spec == null) return error;
            Camera cam = Camera.main;
            if (cam == null) return "no hay cámara principal";
            return Show(spec, cam) ? null : "no se pudo mostrar la señal";
        }

        public bool Show(FeedbackCueSpec spec, Camera cam)
        {
            if (_current != null) _current.Dismiss();
            if (_estimate) Lighting.Apply(_key, _fill, cam.transform.forward);   // la luz más reciente, justo antes de aparecer

            // 1,4 m adelante en la dirección de la vista (sin inclinación), un poco arriba de los ojos, mirando a la persona.
            Transform head = cam.transform;
            Vector3 flat = new Vector3(head.forward.x, 0f, head.forward.z);
            if (flat.sqrMagnitude < 1e-4f) flat = new Vector3(head.up.x, 0f, head.up.z);
            flat.Normalize();
            Vector3 dir = Quaternion.AngleAxis(-RaiseDegrees, Vector3.Cross(Vector3.up, flat)) * flat;
            Vector3 pos = head.position + dir * Distance;
            Quaternion rot = Quaternion.LookRotation(pos - head.position, Vector3.up);

            _current = FeedbackCueVisual.Create(spec, _assets, pos, rot, CueLayer, _estimate ? Lighting.KeyIntensity : 0.8f);
            Shown++;
            string detail = spec.ToString();
            Debug.Log($"{LogPrefix}señal #{Shown}: {detail} (luz ambiente {PassthroughLighting.Hex(Lighting.AmbientLinear)}, principal {PassthroughLighting.Hex(Lighting.KeyColor)} ×{Lighting.KeyIntensity:0.00}, sala vista {Lighting.Coverage:P0})");
            if (AcquisitionEventManager.Instance != null) AcquisitionEventManager.Instance.LogLocalExternal("FEEDBACK_CUE", detail.Replace(',', ' '));
            return true;
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            Camera cam = Camera.main;
            if (_current != null && _current.Done) _current = null;

            if (_estimate && cam != null)
            {
                if (now >= _nextLookup && _mirror == null) { _nextLookup = now + 2f; _mirror = FindObjectOfType<HeadsetMirror>(); }
                // Con una señal en pantalla no se mide: el espejo la incluye y se iluminaría a sí misma.
                if (_current == null && _mirror != null && now >= _nextSample)
                {
                    _nextSample = now + SampleSeconds;
                    Lighting.Sample(_mirror.Frame, cam.transform.rotation);
                }
                if (_current == null && now >= _nextApply)
                {
                    _nextApply = now + ApplySeconds;
                    Lighting.Apply(_key, _fill, cam.transform.forward);
                }
            }
            UpdateCullingMask(cam);
        }

        // MeasurementMode deja la máscara de la cámara en 0 (nada virtual): la capa de las señales se agrega solo
        // mientras hay una en pantalla (o el botón de turno).
        private void UpdateCullingMask(Camera cam)
        {
            int bit = 1 << CueLayer;
            bool show = _current != null || PadVisible;
            if (_maskedCamera != null && (_maskedCamera != cam || !show))
            {
                _maskedCamera.cullingMask &= ~bit;
                _maskedCamera = null;
            }
            if (show && cam != null && (cam.cullingMask & bit) == 0)
            {
                cam.cullingMask |= bit;
                _maskedCamera = cam;
            }
        }
    }
}
