using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Hands;
using XRCollab.Measurement.Mirroring;
using XRCollab.Measurement.Recording;

namespace XRCollab.Measurement.Cues
{
    /// <summary>
    /// Estado en vivo de la app para el panel de sesión (tools\lab-dashboard), en cada respuesta del canal de control
    /// (campo "app"): sesión XR, passthrough, eye tracking (% de mirada válida en 5 s, apertura), manos, movimiento de
    /// la cabeza, fps, espejo del visor, red (host/cliente), señal en pantalla e iluminación estimada.
    /// También responde «snapshot»: un JPG chico de lo que ve el visor (del espejo), para la miniatura del panel.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AppHealth : MonoBehaviour
    {
        private const float WindowSeconds = 5f;
        private const float LookupSeconds = 2f;
        private const float SnapshotCacheSeconds = 0.4f;

        private struct GazeMark { public float T; public bool C, L, R; public float Open; }

        private readonly Queue<GazeMark> _gaze = new Queue<GazeMark>();
        private readonly Queue<Vector2> _frames = new Queue<Vector2>();   // (hora, duración del cuadro) de los últimos 5 s
        private readonly List<float> _frameSort = new List<float>();
        private int _c, _l, _r, _openN;
        private float _openSum;
        private ViveEyeTrackingProvider _eye;
        private HeadsetMirror _mirror;
        private XRHandSubsystem _hands;
        private readonly List<XRHandSubsystem> _handList = new List<XRHandSubsystem>();
        private float _nextLookup, _fps = 60f;
        private Quaternion _headRef;
        private float _headRefT, _headDeg, _headDegLast;
        private Texture2D _snap;
        private string _snapB64;
        private float _snapTime = -10f;
        private int _snapWidth;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN || UNITY_ANDROID   // Android: build nativo en el visor (HeadsetBuild)
            if (FindObjectOfType<AppHealth>() != null) return;
            var go = new GameObject("[AppHealth]");
            DontDestroyOnLoad(go);
            go.AddComponent<AppHealth>();
#endif
        }

        private void Awake()
        {
            AppControlHooks.Health = Json;
            AppControlHooks.Snapshot = Snapshot;
        }

        private void OnDestroy()
        {
            if (AppControlHooks.Health == Json) AppControlHooks.Health = null;
            if (AppControlHooks.Snapshot == Snapshot) AppControlHooks.Snapshot = null;
            if (_snap != null) Destroy(_snap);
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            float dt = Mathf.Max(1e-4f, Time.unscaledDeltaTime);
            _fps = Mathf.Lerp(_fps, 1f / dt, 0.05f);
            _frames.Enqueue(new Vector2(now, dt));
            while (_frames.Count > 0 && now - _frames.Peek().x > WindowSeconds) _frames.Dequeue();
            if (now >= _nextLookup) Lookup(now);

            if (_eye != null)
            {
                var m = new GazeMark { T = now, C = _eye.CombinedGazeValid, L = _eye.LeftGazeValid, R = _eye.RightGazeValid, Open = -1f };
                if (_eye.LeftEyeOpenness.HasValue && _eye.RightEyeOpenness.HasValue) m.Open = (_eye.LeftEyeOpenness.Value + _eye.RightEyeOpenness.Value) * 0.5f;
                Add(m);
            }
            while (_gaze.Count > 0 && now - _gaze.Peek().T > WindowSeconds) Remove(_gaze.Dequeue());

            Camera cam = Camera.main;
            if (cam != null)
            {
                Quaternion q = cam.transform.rotation;
                if (now - _headRefT > WindowSeconds) { _headDegLast = _headDeg; _headDeg = 0f; _headRef = q; _headRefT = now; }
                _headDeg = Mathf.Max(_headDeg, Quaternion.Angle(_headRef, q));
            }
        }

        private void Add(GazeMark m)
        {
            _gaze.Enqueue(m);
            if (m.C) _c++;
            if (m.L) _l++;
            if (m.R) _r++;
            if (m.Open >= 0f) { _openSum += m.Open; _openN++; }
        }

        private void Remove(GazeMark m)
        {
            if (m.C) _c--;
            if (m.L) _l--;
            if (m.R) _r--;
            if (m.Open >= 0f) { _openSum -= m.Open; _openN--; }
        }

        private void Lookup(float now)
        {
            _nextLookup = now + LookupSeconds;
            if (_eye == null) _eye = FindObjectOfType<ViveEyeTrackingProvider>();
            if (_mirror == null) _mirror = FindObjectOfType<HeadsetMirror>();
            if (_hands == null || !_hands.running)
            {
                _handList.Clear();
                SubsystemManager.GetSubsystems(_handList);
                _hands = null;
                foreach (XRHandSubsystem h in _handList) { if (_hands == null || h.running) _hands = h; if (h.running) break; }
            }
        }

        private static string SessionStateName(int s)
        {
            switch (s)
            {
                case 1: return "IDLE";
                case 2: return "READY";
                case 3: return "SYNCHRONIZED";
                case 4: return "VISIBLE";
                case 5: return "FOCUSED";
                case 6: return "STOPPING";
                case 7: return "LOSS_PENDING";
                case 8: return "EXITING";
                default: return "NONE";
            }
        }

        // ---------------- JSON ----------------

        private string Json()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(512);
            sb.Append('{');
            Prop(sb, "fps", Math.Round(_fps, 1).ToString(c));
            // Cuadros lentos: con streaming a 90 Hz cada cuadro que pasa de ~11 ms se ve como un tirón al moverse.
            _frameSort.Clear();
            foreach (Vector2 f in _frames) _frameSort.Add(f.y * 1000f);
            if (_frameSort.Count > 10)
            {
                _frameSort.Sort();
                float median = _frameSort[_frameSort.Count / 2];
                int slow = 0;
                foreach (float ms in _frameSort) if (ms > median * 1.5f) slow++;
                Prop(sb, "frameMs", Math.Round(median, 2).ToString(c));
                Prop(sb, "frameP99Ms", Math.Round(_frameSort[Mathf.Min(_frameSort.Count - 1, (int)(_frameSort.Count * 0.99f))], 2).ToString(c));
                Prop(sb, "frameMaxMs", Math.Round(_frameSort[_frameSort.Count - 1], 2).ToString(c));
                Prop(sb, "slowFrames", slow.ToString(c));
            }
            Prop(sb, "xr", Q(SessionStateName(PassthroughUnderlayFeature.SessionState)));
            Prop(sb, "worn", PassthroughUnderlayFeature.SessionState == 5 ? "true" : "false");
            Prop(sb, "passthrough", Q(PassthroughUnderlayFeature.Status));
            Prop(sb, "passthroughOn", PassthroughUnderlayFeature.IsShowing ? "true" : "false");
            Prop(sb, "measurement", MeasurementMode.IsActive ? "true" : "false");

            int n = _gaze.Count;
            Prop(sb, "eyeTracker", _eye == null ? Q("ausente") : _eye.TrackerReady ? Q("ok") : Q("sin tracker"));
            Prop(sb, "gazePct", n > 0 ? Mathf.RoundToInt(100f * _c / n).ToString(c) : "null");
            Prop(sb, "gazeLPct", n > 0 ? Mathf.RoundToInt(100f * _l / n).ToString(c) : "null");
            Prop(sb, "gazeRPct", n > 0 ? Mathf.RoundToInt(100f * _r / n).ToString(c) : "null");
            Prop(sb, "openness", _openN > 0 ? Math.Round(_openSum / _openN, 2).ToString(c) : "null");

            bool handsOn = _hands != null && _hands.running;
            Prop(sb, "handL", handsOn && _hands.leftHand.isTracked ? "true" : "false");
            Prop(sb, "handR", handsOn && _hands.rightHand.isTracked ? "true" : "false");
            Prop(sb, "headDeg", Math.Round(Mathf.Max(_headDeg, _headDegLast), 1).ToString(c));

            if (_mirror != null)
            {
                Prop(sb, "mirror", Q(_mirror.Status.State.ToString()));
                Prop(sb, "mirrorDetail", Q(_mirror.Status.Detail));
                Prop(sb, "mirrorFps", Math.Round(_mirror.DisplayFps, 1).ToString(c));
            }
            else Prop(sb, "mirror", Q("off"));

            NetworkManager nm = NetworkManager.Singleton;
            string role = nm == null ? "none" : nm.IsHost ? "host" : nm.IsServer ? "server" : nm.IsClient ? (nm.IsConnectedClient ? "client" : "connecting") : "none";
            Prop(sb, "net", Q(role));
            Prop(sb, "clients", nm != null && nm.IsServer ? (nm.ConnectedClients.Count - (nm.IsHost ? 1 : 0)).ToString(c) : "0");

            FeedbackCueDirector cues = FeedbackCueDirector.Instance;
            Prop(sb, "cue", Q(cues != null ? cues.CurrentTitle : ""));
            Prop(sb, "cuesShown", (cues != null ? cues.Shown : 0).ToString(c));
            if (cues != null)
            {
                PassthroughLighting l = cues.Lighting;
                Prop(sb, "light", "{" + "\"ambient\":" + Q(PassthroughLighting.Hex(l.AmbientLinear)) + ",\"key\":" + Q(PassthroughLighting.Hex(l.KeyColor)) +
                    ",\"keyIntensity\":" + Math.Round(l.KeyIntensity, 2).ToString(c) + ",\"coverage\":" + Math.Round(l.Coverage, 2).ToString(c) + "}");
            }
            XRCollab.Measurement.Turns.TurnPad pad = XRCollab.Measurement.Turns.TurnPad.Instance;
            Prop(sb, "turn", pad != null ? pad.HealthJson() : "null");
            Prop(sb, "biolab", AcquisitionEventManager.Bridge != null ? AcquisitionEventManager.Bridge.HealthJson() : "null");
            if (sb[sb.Length - 1] == ',') sb.Length--;
            sb.Append('}');
            return sb.ToString();
        }

        private static void Prop(StringBuilder sb, string name, string rawValue) => sb.Append('"').Append(name).Append("\":").Append(rawValue).Append(',');

        private static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ---------------- snapshot ----------------

        /// <summary>JPG (base64) de lo que ve el visor, ambos ojos, de a lo más <paramref name="maxWidth"/> px de ancho.</summary>
        private string Snapshot(int maxWidth)
        {
            float now = Time.unscaledTime;
            if (_snapB64 != null && _snapWidth == maxWidth && now - _snapTime < SnapshotCacheSeconds) return _snapB64;
            Texture2D frame = _mirror != null ? _mirror.Frame : null;
            if (frame == null || frame.format != TextureFormat.RGBA32) return null;
            int sw = frame.width, sh = frame.height;
            int w = Mathf.Min(maxWidth, sw), h = Mathf.Max(1, Mathf.RoundToInt(sh * (w / (float)sw)));
            if (_snap == null || _snap.width != w || _snap.height != h)
            {
                if (_snap != null) Destroy(_snap);
                _snap = new Texture2D(w, h, TextureFormat.RGB24, false) { name = "Snapshot" };
            }
            NativeArray<Color32> src = frame.GetPixelData<Color32>(0);
            var dst = new Color32[w * h];
            float sx = sw / (float)w, sy = sh / (float)h;
            for (int y = 0; y < h; y++)
            {
                int y0 = Mathf.Min(sh - 1, (int)(y * sy)), y1 = Mathf.Min(sh - 1, (int)((y + 0.5f) * sy));
                for (int x = 0; x < w; x++)
                {
                    int x0 = Mathf.Min(sw - 1, (int)(x * sx)), x1 = Mathf.Min(sw - 1, (int)((x + 0.5f) * sx));
                    Color32 a = src[y0 * sw + x0], b = src[y0 * sw + x1], c = src[y1 * sw + x0], d = src[y1 * sw + x1];
                    dst[y * w + x] = new Color32((byte)((a.r + b.r + c.r + d.r) >> 2), (byte)((a.g + b.g + c.g + d.g) >> 2), (byte)((a.b + b.b + c.b + d.b) >> 2), 255);
                }
            }
            _snap.SetPixels32(dst);
            _snap.Apply(false, false);
            _snapB64 = Convert.ToBase64String(_snap.EncodeToJPG(72));
            _snapTime = now;
            _snapWidth = maxWidth;
            return _snapB64;
        }
    }
}
