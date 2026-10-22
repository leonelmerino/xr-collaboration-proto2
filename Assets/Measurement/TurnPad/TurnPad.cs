using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using XRCollab.Measurement.Cues;
using XRCollab.Measurement.Recording;

namespace XRCollab.Measurement.Turns
{
    /// <summary>
    /// Botón de turno del participante (hand tracking, sin controles) para marcar el INICIO y el FIN de su turno de
    /// Jenga. Desde 2026-10-22 es una esfera de luz («orb») que se activa metiendo la punta del índice; los diseños de
    /// disco (field, solid…) siguen con «turn style». README en esta carpeta.
    ///
    /// Acompaña al participante (<see cref="TurnFollow"/>): vive en un círculo alrededor de su eje vertical, a un ángulo
    /// respecto del frente de su cuerpo, a una distancia y a una altura desde el piso. Movimientos chicos no lo mueven;
    /// al caminar o girarse de verdad vuelve suave a su lugar cómodo; con una mano yendo hacia él no se mueve.
    ///
    /// Para que no se active sin querer (las manos están todo el rato cerca de la torre):
    ///  - cuenta solo la punta del índice, con el índice estirado (apuntando), entrando desde afuera;
    ///  - esfera: si otra punta de dedo o la palma también está adentro (puño, pinza, mano abierta) no cuenta, y avisa
    ///    «SOLO CON EL ÍNDICE»; disco: el dedo tiene que llegar desde arriba y no puede haber 2+ puntas encima;
    ///  - hay que mantenerlo <see cref="HoldSeconds"/>: un arco de progreso se llena mientras tanto;
    ///  - después de cada cambio hay que sacar el dedo antes de poder volver a presionar.
    /// Al acercar la mano, el índice se ilumina (corrientes de luz hacia la punta y un hilo hacia la esfera).
    ///
    /// Moverlo: pinza «OK» (pulgar con índice, los otros tres dedos estirados) quieta 0,8 s → elige la altura, el ángulo y
    /// la distancia (dentro del alcance cómodo); el botón viaja ahí y queda tenue y desactivado hasta que la mano sale.
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

        // Ubicación por defecto respecto del participante: 31° hacia el lado de la mano, a 45 cm del eje, 40 cm bajo los ojos.
        private const float DefaultAngle = 31f;
        private const float DefaultRadius = 0.45f;
        private const float DefaultDrop = 0.40f;
        private const float MinRadius = 0.22f, MaxRadius = 0.65f;   // alcance cómodo al elegir con la pinza

        // Esfera («orb»).
        private const float OrbTouchMargin = 0.008f;   // la punta cuenta hasta 8 mm fuera de la superficie
        private const float OrbHoverRange = 0.12f;
        private const float OrbArmOutside = 0.012f;    // para armar, la punta estuvo al menos esto fuera del área de toque
        private const float FreezeRange = 0.25f;       // una mano a menos de esto del botón: no se mueve (va a tocarlo)
        private const float ReleaseRange = 0.10f;      // recién movido: se activa cuando la mano entera está más lejos que esto…
        private const float ReleaseHoldSeconds = 0.3f; // …durante esto
        private const float GlowRange = 0.30f;         // el índice se ilumina desde 30 cm de la superficie
        private const float TetherRange = 0.18f;       // y el hilo hacia la esfera desde 18 cm
        private const float BadShapeSeconds = 0.25f, BadShapeHintSeconds = 1.6f;
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
        /// <summary>Hora UTC (ticks) del inicio del turno en curso o del último: TurnCoordinator compara con esto.</summary>
        public long TurnStartUtcTicks { get; private set; }

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
        private TurnCoordinator _coordinator;

        // Dónde vive el botón respecto del participante: grados desde el frente del cuerpo (+ = derecha), m del eje, y
        // altura (m, coordenadas del seguimiento: desde el piso si el origen es Floor).
        private readonly TurnFollow _follow = new TurnFollow();
        private float _angle, _radius = DefaultRadius, _height;
        private TurnSnapshot _savedPad;        // ubicación guardada (se usa al colocarlo la primera vez)
        private bool _disabled;                // recién movido: tenue y sin activar hasta que la mano salga
        private float _clearSince = -1f, _badShapeSince = -1f, _badShapeUntil = -10f;
        private string _hint = "";
        private Vector3 _pressTip;

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
            _coordinator = gameObject.AddComponent<TurnCoordinator>();
            LoadState();
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
                if (!Enum.TryParse(a.Substring(6).Trim(), true, out TurnPadVisual.PadStyle st)) return "turn style orb|field|solid|ghost|fade|ring";
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
            if (a.StartsWith("latest "))
            {
                // Respaldo del panel: el turno más nuevo que vio en otro PC (sirve aunque el host esté caído).
                string[] p = a.Split(' ');
                if (p.Length != 3 || !long.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out long key)) return "turn latest PC CLAVE";
                _coordinator.Sync.OnPanel(p[1].ToUpperInvariant(), key);
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
                    return "turn start|end|place|show|hide|style orb|field|solid|ghost|fade|ring|mode pad|gesture|both|latest PC CLAVE";
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
            bool orb = _visual.IsOrb;

            // Acompañar al participante (quieto si una mano va hacia el botón, se está presionando, pinzando o viajando).
            if (cam != null)
            {
                Vector3 head = cam.transform.position;
                bool reaching = show && handsOn && HandWithin(_root.position, FreezeRange, space);
                bool frozen = !Placed || reaching || pinch >= 0f || _hold > 0f || _visual.Moving;
                _follow.Update(head.x, head.z, HeadYaw(cam.transform), dt, frozen);
                if (Placed) _visual.Follow(PadPosition(), head, cam.transform.forward, cam.transform.up, dt);
            }

            float depth = float.MaxValue;
            string who = null;
            bool hover = false, badShape = false;
            float near = 0f, cover = 0f;
            if (show && cam != null && handsOn && !_visual.Moving)
            {
                if (orb)
                {
                    Vector3 c = _root.position;
                    ProbeOrb(_hands.leftHand, space, c, ref _armedL, ref depth, ref who, ref hover, ref badShape, "hand_left");
                    ProbeOrb(_hands.rightHand, space, c, ref _armedR, ref depth, ref who, ref hover, ref badShape, "hand_right");
                    _visual.HandLocal = OrbFingertip(_hands.leftHand, _hands.rightHand, space, c, out near);
                    if (who != null) _visual.HoldDir = _root.InverseTransformDirection((_pressTip - c).normalized);
                }
                else
                {
                    Vector3 eye = _root.InverseTransformPoint(cam.transform.position);
                    cover = Mathf.Max(Cover(_hands.leftHand, space, eye), Cover(_hands.rightHand, space, eye));
                    _visual.HandLocal = Fingertip(_hands.leftHand, _hands.rightHand, space);
                    Probe(_hands.leftHand, space, ref _armedL, ref depth, ref who, ref hover, "hand_left");
                    Probe(_hands.rightHand, space, ref _armedR, ref depth, ref who, ref hover, "hand_right");
                    if (_visual.Style != TurnPadVisual.PadStyle.Solid && _visual.Style != TurnPadVisual.PadStyle.Ghost)
                        near = Mathf.Max(Nearness(_hands.leftHand, space), Nearness(_hands.rightHand, space));
                }
            }

            // Recién movido: tenue y sin activar hasta que la mano entera salga del botón.
            if (_disabled)
            {
                float range = (orb ? TurnPadVisual.OrbRadius : TurnPadVisual.PadRadius) + ReleaseRange;
                bool clear = !handsOn || !HandWithin(_root.position, range, space);
                _clearSince = clear ? (_clearSince < 0f ? now : _clearSince) : -1f;
                if (clear && now - _clearSince >= ReleaseHoldSeconds)
                {
                    _disabled = false;
                    _latched = false;
                    Debug.Log($"{LogPrefix}la mano salió: botón activo de nuevo");
                }
                who = null;
                badShape = false;
            }
            _visual.Dim = _disabled ? 1f : 0f;

            // Mano rara adentro de la esfera (puño, pinza, palma): no cuenta y lo dice.
            _badShapeSince = badShape ? (_badShapeSince < 0f ? now : _badShapeSince) : -1f;
            if (badShape && now - _badShapeSince >= BadShapeSeconds) _badShapeUntil = now + BadShapeHintSeconds;
            string hint = _disabled ? "retira la mano" : now < _badShapeUntil ? "solo con el índice" : "";
            if (hint != _hint) { _hint = hint; RefreshText(); }

            if (orb && show && cam != null && handsOn && !_visual.Moving) UpdateFingers(space, _root.position, who);
            else _visual.HideFingers();
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

        // ---------------- esfera («orb») ----------------

        /// <summary>
        /// Punta del índice de una mano contra la esfera de centro <paramref name="c"/>: hover, y si la está activando
        /// válidamente (índice estirado, entrando desde afuera, sin otra punta ni la palma adentro). Si la punta está
        /// adentro con otra forma de mano, <paramref name="badShape"/>.
        /// </summary>
        private void ProbeOrb(XRHand hand, Transform space, Vector3 c, ref bool armed, ref float bestDepth, ref string who,
            ref bool hover, ref bool badShape, string name)
        {
            if (!hand.isTracked || !World(hand, XRHandJointID.IndexTip, space, out Vector3 tip)) { armed = false; return; }
            float touch = TurnPadVisual.OrbRadius + OrbTouchMargin;
            float d = (tip - c).magnitude;
            bool others = OthersInOrb(hand, space, c);
            if (d > touch)
            {
                if (others) badShape = true;   // palma, puño o pulgar adentro sin el índice
                if (d > TurnPadVisual.OrbRadius + OrbHoverRange) { armed = false; return; }
                hover = true;
                if (d > touch + OrbArmOutside) armed = true;   // entra desde afuera
                return;
            }
            hover = true;
            if (!armed) return;
            if (others || !IndexExtended(hand, space)) { badShape = true; return; }
            if (d < bestDepth) { bestDepth = d; who = name; _pressTip = tip; }
        }

        private static readonly XRHandJointID[] OrbOtherJoints =
            { XRHandJointID.ThumbTip, XRHandJointID.MiddleTip, XRHandJointID.RingTip, XRHandJointID.LittleTip, XRHandJointID.Palm };

        /// <summary>Otra punta de dedo o la palma dentro de la esfera (+ margen): no es un índice apuntando.</summary>
        private static bool OthersInOrb(XRHand hand, Transform space, Vector3 c)
        {
            foreach (XRHandJointID id in OrbOtherJoints)
            {
                if (!World(hand, id, space, out Vector3 p)) continue;
                float lim = TurnPadVisual.OrbRadius + (id == XRHandJointID.Palm ? 0.02f : id == XRHandJointID.ThumbTip ? 0f : OrbTouchMargin);
                if ((p - c).magnitude < lim) return true;
            }
            return false;
        }

        /// <summary>Punta del índice más cercana (coordenadas de la esfera), w = 1 a ≤ 2 cm de la superficie, 0 a ≥ 15 cm.</summary>
        private Vector4 OrbFingertip(XRHand left, XRHand right, Transform space, Vector3 c, out float near)
        {
            Vector4 best = new Vector4(0f, 0f, -1f, 0f);
            float bestD = float.MaxValue;
            near = 0f;
            for (int i = 0; i < 2; i++)
            {
                XRHand h = i == 0 ? left : right;
                if (!h.isTracked || !World(h, XRHandJointID.IndexTip, space, out Vector3 tip)) continue;
                float d = (tip - c).magnitude - TurnPadVisual.OrbRadius;
                if (d >= bestD) continue;
                bestD = d;
                float w = 1f - Mathf.Clamp01((d - 0.02f) / 0.13f);
                Vector3 local = _root.InverseTransformPoint(tip);
                best = new Vector4(local.x, local.y, local.z, w);
                near = w;
            }
            return best;
        }

        /// <summary>El índice de cada mano se ilumina al acercarse (más fuerte apuntando; con el hilo hacia la esfera).</summary>
        private void UpdateFingers(Transform space, Vector3 c, string who)
        {
            for (int i = 0; i < 2; i++)
            {
                XRHand h = i == 0 ? _hands.leftHand : _hands.rightHand;
                if (_disabled || !h.isTracked ||
                    !World(h, XRHandJointID.IndexProximal, space, out Vector3 k) || !World(h, XRHandJointID.IndexIntermediate, space, out Vector3 m) ||
                    !World(h, XRHandJointID.IndexDistal, space, out Vector3 d) || !World(h, XRHandJointID.IndexTip, space, out Vector3 t))
                {
                    _visual.SetFinger(i, default, default, default, default, default, 0f, 0f, 0f);
                    continue;
                }
                float dist = (t - c).magnitude - TurnPadVisual.OrbRadius;
                float glow = Mathf.SmoothStep(0f, 1f, 1f - Mathf.Clamp01((dist - 0.02f) / GlowRange));
                bool pointing = IndexExtended(h, space);
                if (!pointing) glow *= 0.35f;   // con el índice estirado brilla de verdad: así se entiende qué dedo usar
                float tether = pointing && dist > OrbTouchMargin + 0.004f ? Mathf.SmoothStep(0f, 1f, 1f - Mathf.Clamp01((dist - 0.015f) / TetherRange)) : 0f;
                bool pressing = who == (i == 0 ? "hand_left" : "hand_right");
                Vector3 surface = c + (t - c).normalized * TurnPadVisual.OrbRadius;
                _visual.SetFinger(i, k, m, d, t, surface, pressing ? 1f : glow, pressing ? 0f : tether, pressing ? Mathf.Clamp01(Progress) : 0f);
            }
        }

        private static readonly XRHandJointID[] AnyJoint =
        {
            XRHandJointID.Wrist, XRHandJointID.Palm, XRHandJointID.ThumbTip, XRHandJointID.IndexTip, XRHandJointID.MiddleTip,
            XRHandJointID.RingTip, XRHandJointID.LittleTip, XRHandJointID.IndexIntermediate, XRHandJointID.MiddleIntermediate,
            XRHandJointID.ThumbDistal,
        };

        /// <summary>Alguna parte de alguna mano a menos de <paramref name="range"/> de <paramref name="c"/>.</summary>
        private bool HandWithin(Vector3 c, float range, Transform space)
        {
            if (_hands == null || !_hands.running) return false;
            for (int i = 0; i < 2; i++)
            {
                XRHand h = i == 0 ? _hands.leftHand : _hands.rightHand;
                if (!h.isTracked) continue;
                foreach (XRHandJointID id in AnyJoint)
                    if (World(h, id, space, out Vector3 p) && (p - c).sqrMagnitude < range * range) return true;
            }
            return false;
        }

        /// <summary>Hacia dónde mira la cabeza en el plano del piso (grados, 0 = +z). Mirando muy abajo o arriba, usa la coronilla.</summary>
        private static float HeadYaw(Transform head)
        {
            Vector3 f = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (f.sqrMagnitude < 0.04f) f = Vector3.ProjectOnPlane(head.forward.y < 0f ? head.up : -head.up, Vector3.up);
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        private Vector3 PadPosition()
        {
            _follow.PointAt(_angle, _radius, out float x, out float z);
            return new Vector3(x, _height, z);
        }

        /// <summary>Punta del índice más cercana al disco (coordenadas del disco), w = 1 a ≤ 4 cm, 0 a ≥ 12 cm.</summary>
        private Vector4 Fingertip(XRHand left, XRHand right, Transform space)
        {
            Vector4 best = Vector4.zero;
            float bestD = float.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                XRHand h = i == 0 ? left : right;
                if (!h.isTracked || !Local(h, XRHandJointID.IndexTip, space, out Vector3 p)) continue;
                float d = p.magnitude;
                if (d < bestD) { bestD = d; best = new Vector4(p.x, p.y, p.z, 1f - Mathf.Clamp01((d - 0.04f) / 0.08f)); }
            }
            return best;
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

        /// <summary>
        /// Otro PC inició un turno más nuevo (TurnCoordinator): termina el de este PC, con source=handover. Si el aviso
        /// llegó tarde (red cortada, app reiniciada), next_start_utc dice cuándo empezó de verdad el turno del otro.
        /// </summary>
        public void EndForHandover(string nextPc, long nextKey)
        {
            if (!InTurn) return;
            _hold = 0f;
            Toggle("handover", $"|next={nextPc}" + (nextKey > 0 ? $"|next_start_utc={FormatTicks(nextKey)}" : ""));
        }

        private static string FormatTicks(long utcTicks) =>
            utcTicks > 0 && utcTicks < DateTime.MaxValue.Ticks ? RecordingCommand.FormatUtc(new DateTime(utcTicks, DateTimeKind.Utc)) : "?";

        private void Toggle(string source, string extraDetail = "")
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
                TurnStartUtcTicks = PreciseClock.UtcNow.Ticks;
                evt = EventStart;
                detail = $"{TurnNumber}|source={source}";
            }
            else
            {
                InTurn = false;
                _lastDuration = t - _turnStart;
                evt = EventEnd;
                detail = $"{TurnNumber}|source={source}|duration_s={_lastDuration.ToString("F3", CultureInfo.InvariantCulture)}{extraDetail}";
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
            // Un solo turno a la vez: los demás PCs se enteran (host o panel) y el turno en curso de otro termina.
            if (InTurn) _coordinator.Sync.StartTurn(TurnStartUtcTicks);
            SaveState();
        }

        // ---------------- estado en disco (reinicios de la app) ----------------

        private static string StatePath => Path.Combine(Application.persistentDataPath, "turn-state.txt");
        private static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>
        /// Guarda turno en curso, número de turno y turno más nuevo conocido, para que un reinicio de la app (cierre,
        /// Watch-LabApps, Restart-LabStation) no los pierda. Se escribe a un .tmp y se reemplaza (no queda a medias).
        /// </summary>
        public void SaveState()
        {
            TurnLedger latest = _coordinator.Sync.Ledger;
            var s = new TurnSnapshot
            {
                Day = Today, Number = TurnNumber, InTurn = InTurn, StartUtcTicks = InTurn ? TurnStartUtcTicks : 0,
                LastDuration = _lastDuration, LastUtc = _lastUtc, Source = LastSource, LatestPc = latest.Pc, LatestKey = latest.Key,
            };
            if (Placed) { s.PadAngle = _angle; s.PadRadius = _radius; s.PadHeight = _height; s.PadOrigin = OriginMode(); }
            else if (_savedPad != null) { s.PadAngle = _savedPad.PadAngle; s.PadRadius = _savedPad.PadRadius; s.PadHeight = _savedPad.PadHeight; s.PadOrigin = _savedPad.PadOrigin; }
            string tmp = StatePath + ".tmp";
            try
            {
                File.WriteAllText(tmp, s.Serialize());
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
                else File.Move(tmp, StatePath);
            }
            catch (Exception e)
            {
                try { File.Copy(tmp, StatePath, true); }
                catch { Debug.LogWarning($"{LogPrefix}no se pudo guardar {StatePath}: {e.Message}"); }
            }
        }

        /// <summary>
        /// Al abrir la app: el número de turno sigue si es del mismo día, y un turno en curso se retoma si empezó hace
        /// menos de <see cref="TurnSnapshot.MaxResumeAge"/> (si no, queda anotado en events.csv y no se retoma). Si
        /// mientras la app estaba cerrada otro PC tomó el turno, termina en cuanto se entera (handover).
        /// </summary>
        private void LoadState()
        {
            TurnSnapshot s;
            try
            {
                if (!File.Exists(StatePath)) return;
                s = TurnSnapshot.Parse(File.ReadAllText(StatePath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogPrefix}no se pudo leer {StatePath}: {e.Message}");
                return;
            }
            long now = PreciseClock.UtcNow.Ticks;
            if (s.PadRadius > 0f) _savedPad = s;   // ubicación elegida antes (vale aunque sea de otro día)
            if (s.SameDay(Today))
            {
                TurnNumber = s.Number;
                _lastDuration = s.LastDuration;
                _lastUtc = s.LastUtc;
                LastSource = s.Source;
            }
            if (s.CanResume(Today, now))
            {
                InTurn = true;
                TurnStartUtcTicks = s.StartUtcTicks;
                _turnStart = Time.realtimeSinceStartupAsDouble - (now - s.StartUtcTicks) / (double)TimeSpan.TicksPerSecond;
                Debug.Log($"{LogPrefix}app reiniciada en pleno turno {TurnNumber} (empezó {FormatTicks(s.StartUtcTicks)}): se retoma");
                AcquisitionEventManager.Instance?.LogLocalExternal("TURN_PAD", $"RESUMED turn={TurnNumber} start_utc={FormatTicks(s.StartUtcTicks)}");
            }
            else if (s.InTurn)
            {
                Debug.LogWarning($"{LogPrefix}turno {s.Number} guardado en curso pero viejo (empezó {FormatTicks(s.StartUtcTicks)}): no se retoma");
                AcquisitionEventManager.Instance?.LogLocalExternal("TURN_PAD", $"STALE_NOT_RESUMED turn={s.Number} start_utc={FormatTicks(s.StartUtcTicks)}");
            }
            _visual.SetTurnState(InTurn, false);
            RefreshText();
            _coordinator.Sync.Restore(s.LatestPc, s.LatestKey);
            SaveState();
        }

        private void RefreshText()
        {
            int s = (int)TurnSeconds;
            _shownSecond = s;
            string sub = InTurn ? $"turno {TurnNumber} · {s / 60}:{s % 60:00}"
                : TurnNumber == 0 ? (_visual.IsOrb ? "toca con el índice" : "mantén el índice") : $"turno {TurnNumber} duró {s / 60}:{s % 60:00}";
            _visual.SetText(InTurn ? "TERMINAR TURNO" : "INICIAR TURNO", _hint.Length > 0 ? _hint : sub);
        }

        // ---------------- ubicación ----------------

        /// <summary>
        /// Coloca el botón. Con <paramref name="atFinger"/> y una mano a la vista, donde está la punta del índice (el
        /// participante muestra con el dedo dónde lo quiere). Si no: la ubicación guardada (ángulo y distancia; la altura
        /// solo si el origen del seguimiento es el piso, como cuando se guardó) o la de siempre, al lado de la mano.
        /// </summary>
        private bool Place(bool atFinger)
        {
            Camera cam = Camera.main;
            if (cam == null) return false;
            Transform head = cam.transform;
            if (!_follow.Initialized || !Placed) _follow.Snap(head.position.x, head.position.z, HeadYaw(head));
            else _follow.SnapPosition(head.position.x, head.position.z);
            float angle = _rightSide ? DefaultAngle : -DefaultAngle, radius = DefaultRadius, height = head.position.y - DefaultDrop;
            string how = "por defecto";
            if (!Placed && _savedPad != null && _savedPad.PadRadius > 0f)
            {
                angle = _savedPad.PadAngle;
                radius = _savedPad.PadRadius;
                if (_savedPad.PadOrigin == "Floor" && OriginMode() == "Floor") height = _savedPad.PadHeight;
                how = "guardado";
            }
            _follow.PointAt(angle, radius, out float px, out float pz);
            Vector3 pos = new Vector3(px, height, pz);

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

        /// <summary>
        /// Lleva el botón a <paramref name="pos"/> (mundo), pero guardado como ubicación respecto del participante: el
        /// eje pasa a ser su cabeza ahora (el frente del cuerpo no cambia), y quedan el ángulo, la distancia (dentro del
        /// alcance cómodo) y la altura. Desde ahí lo acompaña. Queda tenue y desactivado hasta que la mano sale.
        /// </summary>
        private void PlaceAt(Vector3 pos, string how)
        {
            Camera cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : pos - Vector3.forward;
            if (cam != null)
            {
                if (_follow.Initialized) _follow.SnapPosition(eye.x, eye.z);
                else _follow.Snap(eye.x, eye.z, HeadYaw(cam.transform));
            }
            _follow.ToPolar(pos.x, pos.z, out _angle, out float r);
            _radius = Mathf.Clamp(r, MinRadius, MaxRadius);
            _height = pos.y;
            pos = PadPosition();
            var pose = new Pose(pos, _visual.RotationFor(pos, eye, cam != null ? cam.transform.forward : Vector3.forward, cam != null ? cam.transform.up : Vector3.up));
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
            _disabled = true;   // hasta que la mano salga del botón
            _clearSince = -1f;
            var ci = CultureInfo.InvariantCulture;
            string where = $"angle={_angle.ToString("F0", ci)} radius={_radius.ToString("F2", ci)} height={_height.ToString("F2", ci)} origin={OriginMode()}";
            Debug.Log($"{LogPrefix}{(moved ? "movido" : "colocado")} ({how}): {where} (a {(pos - eye).magnitude:0.00} m de los ojos)");
            AcquisitionEventManager.Instance?.LogLocalExternal("TURN_PAD", $"{(moved ? "MOVED" : "PLACED")} {how} {where} pos={pos.x.ToString("F3", ci)};{pos.y.ToString("F3", ci)};{pos.z.ToString("F3", ci)}");
            SaveState();
        }

        /// <summary>Origen del seguimiento XR («Floor» = la altura es desde el piso; «Device» = desde donde partió la cabeza).</summary>
        private static string OriginMode()
        {
            var list = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(list);
            foreach (XRInputSubsystem s in list)
                if (s.running) return s.GetTrackingOriginMode().ToString();
            return "";
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
                   ",\"me\":\"" + (_coordinator != null ? _coordinator.Sync.Me : "") + "\"" +
                   ",\"latestPc\":\"" + (_coordinator != null ? _coordinator.Sync.Ledger.Pc : "") + "\"" +
                   ",\"latestKey\":\"" + (_coordinator != null ? _coordinator.Sync.Ledger.Key.ToString(c) : "0") + "\"" +
                   ",\"pad\":{\"angle\":" + Math.Round(_angle).ToString(c) + ",\"radius\":" + Math.Round(_radius, 2).ToString(c) +
                   ",\"height\":" + Math.Round(_height, 2).ToString(c) + ",\"disabled\":" + (_disabled ? "true" : "false") + "}" +
                   ",\"lastUtc\":\"" + _lastUtc + "\"}";
        }
    }
}
