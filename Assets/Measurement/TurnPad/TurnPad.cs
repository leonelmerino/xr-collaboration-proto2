using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR.Hands;
using XRCollab.Measurement.Cues;
using XRCollab.Measurement.Recording;

namespace XRCollab.Measurement.Turns
{
    /// <summary>
    /// Botón de turno del participante: un disco horizontal fijo en la sala que se presiona con la punta del índice
    /// (hand tracking, sin controles) para marcar el INICIO y el FIN de su turno de Jenga. README en esta carpeta.
    ///
    /// Para que no se active sin querer (las manos están todo el rato cerca de la torre):
    ///  - cuenta solo la punta del índice, y con el índice estirado (apuntando);
    ///  - el dedo tiene que llegar desde arriba del disco (no de lado ni por debajo);
    ///  - si dos o más puntas de dedos están en el disco (palma, puño, un bloque en la mano) no cuenta;
    ///  - hay que mantenerlo <see cref="HoldSeconds"/>: un arco de progreso se llena mientras tanto;
    ///  - después de cada cambio hay que sacar el dedo antes de poder volver a presionar.
    ///
    /// Moverlo: pinza «OK» (pulgar con índice, los otros tres dedos estirados) quieta 0,8 s → el disco viaja hasta ahí.
    ///
    /// Cada cambio sale por el camino oficial de eventos (Documentation/architecture.md):
    /// <c>AcquisitionEventManager.EmitInteractionEvent("TURN_START"|"TURN_END", "turn", ...)</c> → events.csv local +
    /// BioLab por UDP (<c>E:VR_&lt;PC&gt;,TURN_START|participant=…|node=…|t_local=…|t_host=…|sync=…|turn=N|source=…,0</c>),
    /// con la hora del host (NetworkClockSync). Además queda como marca en marks.csv de la grabación de video y en
    /// el estado de la app para el panel de sesión. El panel también puede marcar el turno («turn start|end»), por si
    /// el hand tracking falla; la fuente queda registrada (hand_left, hand_right, panel).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TurnPad : MonoBehaviour
    {
        public const string EventStart = "TURN_START";
        public const string EventEnd = "TURN_END";
        private const string LogPrefix = "[TurnPad] ";

        // Geometría del gesto (metros, en coordenadas del disco: z < 0 = arriba de la cara).
        private const float TouchRadius = 0.055f;        // 1 cm de tolerancia alrededor del disco de 9 cm
        private const float HoverRadius = 0.10f;
        private const float TouchFront = 0.015f;         // el dedo cuenta desde 1,5 cm sobre la cara...
        private const float TouchBehind = 0.045f;        // ...hasta 4,5 cm atravesándola
        private const float HoverFront = 0.14f;
        private const float PressTravel = 0.007f;        // cuánto se hunde la cara visualmente

        // Gesto.
        public const float HoldSeconds = 0.45f;
        private const float ReleaseSeconds = 0.25f;      // sin dedo este tiempo = soltado
        private const float CooldownSeconds = 1.0f;
        private const float MaxIndexBendDegrees = 50f;   // índice estirado

        // Mover el botón con una pinza («OK»: pulgar con índice, los otros 3 dedos estirados), quieta un rato.
        // Umbrales como el detector de pinza de Ultraleap (0,025 activa / 0,030 suelta) y desde la mano abierta, para no
        // dispararse al agarrar un bloque.
        private const float PinchOn = 0.022f;
        private const float PinchOff = 0.035f;
        private const float PinchOpen = 0.045f;           // antes de cerrar, pulgar e índice separados al menos esto…
        private const float PinchFromOpenSeconds = 0.6f;  // …hace no más de esto
        private const float PinchHoldSeconds = 0.8f;
        private const float PinchStillMeters = 0.03f;
        private const float MaxOtherFingerBend = 50f;

        // Ubicación por defecto respecto de la cabeza al colocarlo (adelante, al lado de la mano, abajo).
        private const float PlaceForward = 0.40f;
        private const float PlaceSide = 0.24f;
        private const float PlaceDown = 0.40f;
        private const float AutoPlaceFocusedSeconds = 3f;
        private const float AutoPlaceMaxPitch = 30f;

        // Gesto de turno (alternativa al disco): pulgar arriba con el puño cerrado, quieto un rato.
        private const float GestureHoldSeconds = 0.6f;
        private const float GestureCooldownSeconds = 1.5f;
        private const float ThumbUpCos = 0.75f;           // pulgar a ≤ ~41° de la vertical
        private const float ThumbStraightDegrees = 40f;
        private const float CurlDegrees = 80f;             // los otros 4 dedos doblados al menos esto
        private const float TipToPalmMax = 0.075f;
        private const float GestureStillMeters = 0.03f;

        // Cercanía de la mano para los diseños Fade/Ring: 1 a ≤ 3 cm del disco, 0 a ≥ 15 cm.
        private const float NearFull = 0.03f, NearNone = 0.15f;

        public enum TurnMode { Pad, Gesture, Both }

        public static TurnPad Instance { get; private set; }

        public TurnMode Mode { get; private set; } = TurnMode.Pad;
        public TurnPadVisual.PadStyle Style => _visual != null ? _visual.Style : TurnPadVisual.PadStyle.Solid;

        /// <summary>Activa o no el mover con pinza (-noturnpad-pinch lo apaga).</summary>
        public static bool PinchToMove = true;

        public bool Visible { get; private set; } = true;
        public bool Placed { get; private set; }
        public bool InTurn { get; private set; }
        public int TurnNumber { get; private set; }
        public bool Hover { get; private set; }
        public float Progress => _hold / HoldSeconds;
        public double TurnSeconds => InTurn ? Time.realtimeSinceStartupAsDouble - _turnStart : _lastDuration;
        public string LastSource { get; private set; } = "";

        private bool _rightSide = true;
        private TurnPadVisual _visual;
        private Transform _root;   // = _visual.Root: la pose del disco en la sala
        private readonly List<Material> _owned = new List<Material>();
        private readonly List<XRHandSubsystem> _handList = new List<XRHandSubsystem>();
        private XRHandSubsystem _hands;
        private float _nextLookup, _hold, _lastTouch = -10f, _cooldownUntil, _focusedSince = -1f;
        private bool _latched;
        private bool _armedL, _armedR;
        private double _turnStart, _lastDuration;
        private string _lastUtc = "";   // hora UTC del último cambio (panel: línea de tiempo exacta)
        private int _shownSecond = -1;
        private string _toggleSource = "";

        private sealed class Pinch { public bool Active; public float Since, LastOpen = -10f; public Vector3 Anchor; }
        private readonly Pinch _pinchL = new Pinch(), _pinchR = new Pinch();
        private sealed class Thumb { public bool Active, Armed = true; public float Since; public Vector3 Anchor; }
        private readonly Thumb _thumbL = new Thumb(), _thumbR = new Thumb();
        private float _gestureCooldownUntil;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN || UNITY_ANDROID   // Android: build nativo en el visor (HeadsetBuild)
            if (Instance != null || !MeasurementMode.IsActive) return;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-noturnpad") >= 0) return;
            var go = new GameObject("[TurnPad]");
            DontDestroyOnLoad(go);
            go.AddComponent<TurnPad>();
#endif
        }

        private void Awake()
        {
            Instance = this;
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-turnpad-side");
            if (i >= 0 && i + 1 < args.Length) _rightSide = !args[i + 1].Trim().Equals("left", StringComparison.OrdinalIgnoreCase);
            if (Array.IndexOf(args, "-noturnpad-pinch") >= 0) PinchToMove = false;
            i = Array.IndexOf(args, "-turn-mode");
            string mode = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            i = Array.IndexOf(args, "-turnpad-style");
            string style = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            CueAssets assets = CueAssets.Load();
            if (!assets.Ok) { Debug.LogWarning($"{LogPrefix}faltan recursos de las señales:{assets.Missing}: sin botón de turno"); enabled = false; return; }
            _visual = new TurnPadVisual(transform, assets, FeedbackCueDirector.CueLayer, _owned);
            _root = _visual.Root;
            _root.gameObject.SetActive(false);
            RefreshText();
            AppControlHooks.Turn = HandleCommand;
            if (style != null) HandleCommand("style " + style);
            if (mode != null) HandleCommand("mode " + mode);
            Debug.Log($"{LogPrefix}listo (lado {(_rightSide ? "derecho" : "izquierdo")}); se coloca solo al ponerse el visor, con «turn place» o con la pinza");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (AppControlHooks.Turn == HandleCommand) AppControlHooks.Turn = null;
            FeedbackCueDirector.PadVisible = false;
            foreach (Material m in _owned) if (m != null) Destroy(m);
        }

        // ---------------- órdenes del panel ----------------

        /// <summary>«turn start|end|place|show|hide» (hilo principal). null = ok, si no el error.</summary>
        public string HandleCommand(string arg)
        {
            string a = (arg ?? "").Trim().ToLowerInvariant();
            if (a.StartsWith("style "))
            {
                if (!Enum.TryParse(a.Substring(6).Trim(), true, out TurnPadVisual.PadStyle st)) return "turn style solid|ghost|fade|ring";
                _visual.Style = st;
                Debug.Log($"{LogPrefix}diseño {st}");
                AcquisitionEventManager.Instance?.LogLocalExternal("TURN_PAD", "STYLE " + st.ToString().ToUpperInvariant());
                return null;
            }
            if (a.StartsWith("mode "))
            {
                if (!Enum.TryParse(a.Substring(5).Trim(), true, out TurnMode m)) return "turn mode pad|gesture|both";
                Mode = m;
                _hold = 0f;
                Debug.Log($"{LogPrefix}modo {m}");
                AcquisitionEventManager.Instance?.LogLocalExternal("TURN_PAD", "MODE " + m.ToString().ToUpperInvariant());
                return null;
            }
            switch (a)
            {
                case "start":
                    if (InTurn) return $"el turno {TurnNumber} ya está en curso";
                    Toggle("panel");
                    return null;
                case "end":
                    if (!InTurn) return "no hay turno en curso";
                    Toggle("panel");
                    return null;
                case "place":
                    return Place(true) ? null : "no hay cámara XR todavía";
                case "show":
                    Visible = true;
                    if (!Placed) Place(false);
                    return null;
                case "hide":
                    Visible = false;
                    return null;
                default:
                    return "turn start|end|place|show|hide|style solid|ghost|fade|ring|mode pad|gesture|both";
            }
        }

        // ---------------- ciclo ----------------

        private void Update()
        {
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            Camera cam = Camera.main;
            bool focused = PassthroughUnderlayFeature.SessionState == 5;
            _focusedSince = focused ? (_focusedSince < 0f ? now : _focusedSince) : -1f;
            if (!Placed && Visible && cam != null && focused && now - _focusedSince >= AutoPlaceFocusedSeconds)
            {
                float pitch = Vector3.Angle(Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up), cam.transform.forward);
                if (pitch <= AutoPlaceMaxPitch) Place(false);
            }

            if ((_hands == null || !_hands.running) && now >= _nextLookup)
            {
                _nextLookup = now + 2f;
                _handList.Clear();
                SubsystemManager.GetSubsystems(_handList);
                _hands = null;
                foreach (XRHandSubsystem h in _handList) { if (_hands == null || h.running) _hands = h; if (h.running) break; }
            }
            Transform space = cam != null ? cam.transform.parent : null;
            bool handsOn = _hands != null && _hands.running;

            // Pinza para mover el botón (también sirve para ponerlo la primera vez).
            float pinch = -1f;
            Vector3 pinchAt = default;
            if (Visible && PinchToMove && cam != null && handsOn && !_visual.Moving)
            {
                UpdatePinch(_hands.leftHand, _pinchL, space, now, "pinza izquierda", ref pinch, ref pinchAt);
                UpdatePinch(_hands.rightHand, _pinchR, space, now, "pinza derecha", ref pinch, ref pinchAt);
            }

            // Gesto de turno: pulgar arriba (alternativa más casual al disco). Usa el mismo anillo de progreso.
            if (Mode != TurnMode.Pad && cam != null && handsOn && !_visual.Moving)
            {
                UpdateThumb(_hands.leftHand, _thumbL, space, now, "gesture_left", ref pinch, ref pinchAt);
                UpdateThumb(_hands.rightHand, _thumbR, space, now, "gesture_right", ref pinch, ref pinchAt);
            }
            _visual.Pinch(pinch, pinchAt, cam != null ? cam.transform.position : pinchAt - Vector3.forward);

            bool show = Visible && Placed && Mode != TurnMode.Gesture;
            if (_root.gameObject.activeSelf != show) _root.gameObject.SetActive(show);
            FeedbackCueDirector.PadVisible = show || pinch >= 0f;

            float depth = float.MaxValue;
            string who = null;
            bool hover = false;
            float near = 0f, cover = 0f;
            if (show && cam != null && handsOn && !_visual.Moving)
            {
                Vector3 eye = _root.InverseTransformPoint(cam.transform.position);
                cover = Mathf.Max(Cover(_hands.leftHand, space, eye), Cover(_hands.rightHand, space, eye));
                Probe(_hands.leftHand, space, ref _armedL, ref depth, ref who, ref hover, "hand_left");
                Probe(_hands.rightHand, space, ref _armedR, ref depth, ref who, ref hover, "hand_right");
                if (_visual.Style == TurnPadVisual.PadStyle.Fade || _visual.Style == TurnPadVisual.PadStyle.Ring)
                    near = Mathf.Max(Nearness(_hands.leftHand, space), Nearness(_hands.rightHand, space));
            }
            Hover = hover || who != null;

            if (who != null) _lastTouch = now;
            if (_latched && now - _lastTouch > ReleaseSeconds) _latched = false;
            bool pressing = who != null && !_latched && now >= _cooldownUntil;
            _hold = pressing ? _hold + dt : Mathf.Max(0f, _hold - dt * 2.5f);
            if (pressing) _toggleSource = who;
            if (_hold >= HoldSeconds)
            {
                _hold = 0f;
                _latched = true;
                Toggle(_toggleSource);
            }

            if (InTurn && (int)TurnSeconds != _shownSecond) RefreshText();
            _visual.Tick(dt, Mathf.Clamp01(Progress), who != null ? Mathf.Clamp(depth, 0f, PressTravel) : 0f, Hover, near, cover);
        }

        // Puntos de la mano que pueden tapar el disco visto desde el ojo (palma, nudillos, dedos).
        private static readonly XRHandJointID[] CoverJoints =
        {
            XRHandJointID.Wrist, XRHandJointID.Palm, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip,
            XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexTip,
            XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleTip,
            XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingTip,
            XRHandJointID.LittleProximal, XRHandJointID.LittleTip,
        };
        private const float CoverMargin = 0.012f;   // medio dedo/palma (m, en el plano del disco)
        private const float CoverSoft = 0.015f;     // borde suave más allá del margen

        /// <summary>
        /// 0..1: cuánto tapa la mano el botón visto desde el ojo (sin oclusión real, el botón quedaría dibujado encima
        /// de la mano). Cada articulación delante del disco se proyecta desde el ojo al plano del disco; si cae sobre el
        /// disco o el texto, 1. Barato: ~16 proyecciones por mano y cuadro.
        /// </summary>
        private float Cover(XRHand hand, Transform space, Vector3 eye)
        {
            if (!hand.isTracked || eye.z >= -0.01f) return 0f;   // ojo detrás del disco: nada que tapar
            float best = 0f;
            foreach (XRHandJointID id in CoverJoints)
            {
                if (!Local(hand, id, space, out Vector3 p)) continue;
                if (p.z >= 0f || p.z <= eye.z) continue;             // detrás del disco o detrás del ojo
                float s = eye.z / (eye.z - p.z);                       // ojo→p hasta el plano z=0 (s > 1)
                Vector3 hit = eye + (p - eye) * s;
                float margin = CoverMargin * s;
                float outside = Mathf.Max(0f, new Vector2(hit.x, hit.y).magnitude - TurnPadVisual.PadRadius);
                // El texto (arriba del disco) también.
                float lx = Mathf.Max(0f, Mathf.Abs(hit.x) - TurnPadVisual.LabelHalfWidth);
                float ly = Mathf.Max(0f, Mathf.Max(TurnPadVisual.LabelBottom - hit.y, hit.y - TurnPadVisual.LabelTop));
                float d = Mathf.Min(outside, new Vector2(lx, ly).magnitude) - margin;
                float k = 1f - Mathf.Clamp01(d / CoverSoft);
                if (k > best) { best = k; if (best >= 1f) break; }
            }
            return best;
        }

        private static readonly XRHandJointID[] NearJoints =
        {
            XRHandJointID.Palm, XRHandJointID.ThumbTip, XRHandJointID.IndexTip, XRHandJointID.MiddleTip,
            XRHandJointID.RingTip, XRHandJointID.LittleTip, XRHandJointID.IndexProximal, XRHandJointID.LittleProximal,
        };

        /// <summary>0..1: qué tan cerca está la mano (cualquier punta o la palma) del disco, para Fade/Ring.</summary>
        private float Nearness(XRHand hand, Transform space)
        {
            if (!hand.isTracked) return 0f;
            float best = float.MaxValue;
            foreach (XRHandJointID id in NearJoints)
            {
                if (!Local(hand, id, space, out Vector3 p)) continue;
                // Distancia al disco (no a su centro): radial fuera del borde + altura sobre la cara.
                float radial = Mathf.Max(0f, new Vector2(p.x, p.y).magnitude - TurnPadVisual.PadRadius);
                float d = new Vector2(radial, Mathf.Max(0f, -p.z)).magnitude;
                if (d < best) best = d;
            }
            return best == float.MaxValue ? 0f : 1f - Mathf.Clamp01((best - NearFull) / (NearNone - NearFull));
        }

        /// <summary>
        /// Gesto de turno: pulgar estirado hacia arriba, los otros cuatro dedos doblados contra la palma, la mano quieta
        /// <see cref="GestureHoldSeconds"/>. Al manipular un bloque de Jenga el pulgar empuja o pinza (no apunta arriba
        /// con el puño cerrado), así que no se dispara. Después hay que deshacer el gesto antes del siguiente.
        /// </summary>
        private void UpdateThumb(XRHand hand, Thumb ts, Transform space, float now, string source, ref float progress, ref Vector3 at)
        {
            if (!hand.isTracked || !ThumbsUp(hand, space, out Vector3 tip, out Vector3 palm))
            {
                ts.Active = false;
                ts.Armed = true;
                return;
            }
            if (!ts.Armed || now < _gestureCooldownUntil) return;
            if (!ts.Active || (palm - ts.Anchor).magnitude > GestureStillMeters)
            {
                ts.Active = true;
                ts.Since = now;
                ts.Anchor = palm;
                return;
            }
            float k = (now - ts.Since) / GestureHoldSeconds;
            if (k < 1f)
            {
                if (k > progress) { progress = k; at = tip + Vector3.up * 0.02f; }
                return;
            }
            ts.Active = false;
            ts.Armed = false;
            _gestureCooldownUntil = now + GestureCooldownSeconds;
            _visual.PinchDone();
            Toggle(source);
        }

        private static readonly XRHandJointID[][] CurledFingers =
        {
            new[] { XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexDistal, XRHandJointID.IndexTip },
            new[] { XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip },
            new[] { XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingDistal, XRHandJointID.RingTip },
            new[] { XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleDistal, XRHandJointID.LittleTip },
        };

        private static bool ThumbsUp(XRHand hand, Transform space, out Vector3 tip, out Vector3 palm)
        {
            palm = default;
            if (!World(hand, XRHandJointID.ThumbTip, space, out tip) || !World(hand, XRHandJointID.ThumbProximal, space, out Vector3 tp) ||
                !World(hand, XRHandJointID.ThumbDistal, space, out Vector3 td) || !World(hand, XRHandJointID.ThumbMetacarpal, space, out Vector3 tm) ||
                !World(hand, XRHandJointID.Palm, space, out palm)) return false;
            if (Vector3.Dot((tip - tp).normalized, Vector3.up) < ThumbUpCos) return false;
            if (Vector3.Angle(tp - tm, tip - td) > ThumbStraightDegrees) return false;
            foreach (XRHandJointID[] f in CurledFingers)
            {
                if (!World(hand, f[0], space, out Vector3 m) || !World(hand, f[1], space, out Vector3 p) ||
                    !World(hand, f[2], space, out Vector3 d) || !World(hand, f[3], space, out Vector3 t)) return false;
                if (Vector3.Angle(p - m, t - d) < CurlDegrees) return false;
                if ((t - palm).magnitude > TipToPalmMax) return false;
            }
            // El pulgar sobresale del puño (no envuelto alrededor de algo).
            return tip.y - palm.y > 0.04f;
        }

        /// <summary>Mira la punta del índice de una mano: hover, y si está presionando válidamente el disco.</summary>
        private void Probe(XRHand hand, Transform space, ref bool armed, ref float bestDepth, ref string who, ref bool hover, string name)
        {
            if (!hand.isTracked || !Local(hand, XRHandJointID.IndexTip, space, out Vector3 tip)) { armed = false; return; }
            float r = new Vector2(tip.x, tip.y).magnitude;
            bool inHover = r < HoverRadius && tip.z > -HoverFront && tip.z < TouchBehind;
            if (!inHover) { armed = false; return; }
            hover = true;
            // Armado: la punta estuvo sobre la cara del disco (llega de arriba, no por el costado ni por debajo).
            if (tip.z < -TouchFront * 0.5f && r < HoverRadius) armed = true;
            bool touching = r < TouchRadius && tip.z > -TouchFront && tip.z < TouchBehind;
            if (!touching || !armed) return;
            if (!IndexExtended(hand, space)) return;
            if (OtherTipsOnPad(hand, space) >= 2) return;
            float d = Mathf.Max(0f, tip.z);
            if (d < bestDepth) { bestDepth = d; who = name; }
        }

        /// <summary>
        /// Pinza «OK» quieta <see cref="PinchHoldSeconds"/> → un arco de progreso se llena en los dedos y luego el disco
        /// viaja hasta ese punto. Deja en <paramref name="progress"/> el avance (0..1) si esta mano está pinzando.
        /// </summary>
        private void UpdatePinch(XRHand hand, Pinch ps, Transform space, float now, string how, ref float progress, ref Vector3 at)
        {
            if (!hand.isTracked || !World(hand, XRHandJointID.ThumbTip, space, out Vector3 thumb) || !World(hand, XRHandJointID.IndexTip, space, out Vector3 index))
            {
                ps.Active = false;
                return;
            }
            float d = Vector3.Distance(thumb, index);
            if (d > PinchOpen) ps.LastOpen = now;
            Vector3 p = (thumb + index) * 0.5f;
            if (!ps.Active)
            {
                if (d < PinchOn && now - ps.LastOpen < PinchFromOpenSeconds && OtherFingersExtended(hand, space))
                {
                    ps.Active = true;
                    ps.Since = now;
                    ps.Anchor = p;
                }
                return;
            }
            if (d > PinchOff || (p - ps.Anchor).magnitude > PinchStillMeters || !OtherFingersExtended(hand, space))
            {
                ps.Active = false;
                return;
            }
            float k = (now - ps.Since) / PinchHoldSeconds;
            if (k < 1f)
            {
                if (k > progress) { progress = k; at = p; }
                return;
            }
            ps.Active = false;
            ps.LastOpen = -10f;          // para otra, hay que abrir la mano de nuevo
            _visual.PinchDone();
            PlaceAt(p, how);
        }

        private static readonly XRHandJointID[][] OtherFingers =
        {
            new[] { XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleTip },
            new[] { XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingTip },
            new[] { XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleTip },
        };

        /// <summary>Medio, anular y meñique estirados: la pinza es un gesto «OK», no la mano agarrando un bloque.</summary>
        private bool OtherFingersExtended(XRHand hand, Transform space)
        {
            foreach (XRHandJointID[] f in OtherFingers)
            {
                if (!World(hand, f[0], space, out Vector3 a) || !World(hand, f[1], space, out Vector3 b) || !World(hand, f[2], space, out Vector3 c)) return false;
                if (Vector3.Angle(b - a, c - b) > MaxOtherFingerBend) return false;
            }
            return true;
        }

        private bool IndexExtended(XRHand hand, Transform space)
        {
            if (!World(hand, XRHandJointID.IndexProximal, space, out Vector3 a) ||
                !World(hand, XRHandJointID.IndexIntermediate, space, out Vector3 b) ||
                !World(hand, XRHandJointID.IndexTip, space, out Vector3 c)) return false;
            return Vector3.Angle(b - a, c - b) <= MaxIndexBendDegrees;
        }

        private static readonly XRHandJointID[] OtherTips =
            { XRHandJointID.ThumbTip, XRHandJointID.MiddleTip, XRHandJointID.RingTip, XRHandJointID.LittleTip };

        private int OtherTipsOnPad(XRHand hand, Transform space)
        {
            int n = 0;
            foreach (XRHandJointID id in OtherTips)
            {
                if (!Local(hand, id, space, out Vector3 p)) continue;
                if (new Vector2(p.x, p.y).magnitude < TouchRadius && p.z > -TouchFront && p.z < TouchBehind) n++;
            }
            return n;
        }

        private static bool World(XRHand hand, XRHandJointID id, Transform space, out Vector3 world)
        {
            world = default;
            if (!hand.GetJoint(id).TryGetPose(out Pose pose)) return false;
            world = space != null ? space.TransformPoint(pose.position) : pose.position;
            return true;
        }

        /// <summary>Articulación en coordenadas del disco: z &lt; 0 sobre la cara (lado del participante).</summary>
        private bool Local(XRHand hand, XRHandJointID id, Transform space, out Vector3 local)
        {
            local = default;
            if (!World(hand, id, space, out Vector3 w)) return false;
            local = _root.InverseTransformPoint(w);
            return true;
        }

        // ---------------- turno ----------------

        private void Toggle(string source)
        {
            double t = Time.realtimeSinceStartupAsDouble;
            _lastUtc = RecordingCommand.FormatUtc(PreciseClock.UtcNow);
            _cooldownUntil = Time.unscaledTime + CooldownSeconds;
            LastSource = source;
            string detail;
            string evt;
            if (!InTurn)
            {
                InTurn = true;
                TurnNumber++;
                _turnStart = t;
                evt = EventStart;
                detail = $"{TurnNumber}|source={source}";
            }
            else
            {
                InTurn = false;
                _lastDuration = t - _turnStart;
                evt = EventEnd;
                detail = $"{TurnNumber}|source={source}|duration_s={_lastDuration.ToString("F3", CultureInfo.InvariantCulture)}";
            }

            // Camino oficial: events.csv + BioLab (UDP) con hora del host. Si la sesión de adquisición aún no partió,
            // AcquisitionEventManager lo deja como INTERACTION_REJECTED en events.csv (queda el registro igual).
            AcquisitionEventManager acq = AcquisitionEventManager.Instance;
            if (acq != null) acq.EmitInteractionEvent(evt, "turn", detail);
            else Debug.LogWarning($"{LogPrefix}sin AcquisitionEventManager: {evt} no llega a BioLab ni a events.csv");
            // Marca en el video (marks.csv), como «mark» del panel.
            SessionRecorder.Instance?.Mark($"{evt} turno={detail.Replace('|', ' ')}");
            Debug.Log($"{LogPrefix}{evt} turno {TurnNumber} ({source}){(InTurn ? "" : $", duró {_lastDuration:0.0} s")}");

            _visual.SetTurnState(InTurn, Application.isPlaying);
            RefreshText();
        }

        private void RefreshText()
        {
            int s = (int)TurnSeconds;
            _shownSecond = s;
            _visual.SetText(InTurn ? "TERMINAR TURNO" : "INICIAR TURNO",
                InTurn ? $"turno {TurnNumber} · {s / 60}:{s % 60:00}" : TurnNumber == 0 ? "mantén el índice" : $"turno {TurnNumber} duró {s / 60}:{s % 60:00}");
        }

        // ---------------- ubicación ----------------

        /// <summary>
        /// Coloca el disco fijo en la sala. Con <paramref name="atFinger"/> y una mano a la vista, donde está la punta
        /// del índice (el participante muestra con el dedo dónde lo quiere); si no, adelante, abajo y al lado de la mano.
        /// </summary>
        private bool Place(bool atFinger)
        {
            Camera cam = Camera.main;
            if (cam == null) return false;
            Transform head = cam.transform;
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(-head.up, Vector3.up);
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 pos = head.position + fwd * PlaceForward + (_rightSide ? right : -right) * PlaceSide - Vector3.up * PlaceDown;
            string how = "por defecto";

            if (atFinger && _hands != null && _hands.running)
            {
                XRHand first = _rightSide ? _hands.rightHand : _hands.leftHand, second = _rightSide ? _hands.leftHand : _hands.rightHand;
                Transform space = head.parent;
                if ((first.isTracked && World(first, XRHandJointID.IndexTip, space, out Vector3 tip)) ||
                    (second.isTracked && World(second, XRHandJointID.IndexTip, space, out tip)))
                {
                    // Un poco bajo el dedo, para que no quede presionado al aparecer.
                    pos = tip - Vector3.up * 0.02f;
                    how = "en la punta del índice";
                }
            }
            PlaceAt(pos, how);
            return true;
        }

        /// <summary>Deja el disco horizontal en <paramref name="pos"/> (mundo), con el texto legible desde los ojos.</summary>
        private void PlaceAt(Vector3 pos, string how)
        {
            Camera cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : pos - Vector3.forward;
            var pose = new Pose(pos, TurnPadVisual.FlatRotation(pos, eye, cam != null ? cam.transform.forward : Vector3.forward));
            bool moved = Placed && Visible;
            if (moved) _visual.MoveTo(pose);
            else
            {
                _root.gameObject.SetActive(Visible);
                _visual.Appear(pose);
            }
            Placed = true;
            _hold = 0f;
            _latched = true;   // si el dedo quedó encima, primero hay que sacarlo
            _lastTouch = Time.unscaledTime;
            Debug.Log($"{LogPrefix}{(moved ? "movido" : "colocado")} ({how}): {pos.ToString("F2")} (a {(pos - eye).magnitude:0.00} m de los ojos)");
            AcquisitionEventManager.Instance?.LogLocalExternal("TURN_PAD", $"{(moved ? "MOVED" : "PLACED")} {how} pos={pos.x:F3};{pos.y:F3};{pos.z:F3}".Replace(',', '.'));
        }

        // ---------------- estado para el panel ----------------

        /// <summary>Objeto JSON para "app" del canal de control.</summary>
        public string HealthJson()
        {
            var c = CultureInfo.InvariantCulture;
            return "{\"visible\":" + (Visible ? "true" : "false") +
                   ",\"placed\":" + (Placed ? "true" : "false") +
                   ",\"state\":\"" + (InTurn ? "turn" : "idle") + "\"" +
                   ",\"n\":" + TurnNumber.ToString(c) +
                   ",\"secs\":" + Math.Round(TurnSeconds, 1).ToString(c) +
                   ",\"hover\":" + (Hover ? "true" : "false") +
                   ",\"source\":\"" + LastSource + "\"" +
                   ",\"style\":\"" + Style.ToString().ToLowerInvariant() + "\"" +
                   ",\"mode\":\"" + Mode.ToString().ToLowerInvariant() + "\"" +
                   ",\"lastUtc\":\"" + _lastUtc + "\"}";
        }
    }
}
