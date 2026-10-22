using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using XRCollab.Measurement.Cues;

namespace XRCollab.Measurement.Turns
{
    /// <summary>
    /// Lo que se ve del botón de turno, y sus animaciones. Sobrias, cortas y con curvas suaves (sin rebotes): es un
    /// instrumento de investigación, no un juego.
    ///  - Aparece: el anillo se dibuja en sentido horario (0,35 s) y el disco crece de 55 % a 100 % (ease-out).
    ///  - Mantener el dedo: un arco de progreso se llena en sentido horario desde las 12; al completarse, el color
    ///    cambia con un fundido (verde = esperando, naranjo = en turno) y una onda sale del borde.
    ///  - Moverlo (pinza): viaja por un arco hacia arriba y abajo mientras da una vuelta completa sobre sí mismo, con una
    ///    onda donde se va y otra donde llega.
    ///  - Pinza en curso: un anillo de progreso en la punta de los dedos.
    /// El disco queda siempre horizontal (mirando hacia arriba) y el texto acostado más allá del disco, legible desde
    /// donde está el participante.
    ///
    /// «orb» (por defecto desde 2026-10-22): una esfera de luz de puntos (Shaders/PadOrb) que se activa metiendo la punta
    /// del índice; el texto flota arriba y abajo de la esfera, de cara al participante, y el índice que se acerca se
    /// ilumina (Shaders/FingerGlow). Recién movida queda tenue (<see cref="Dim"/>) hasta que la mano sale.
    /// </summary>
    public sealed class TurnPadVisual
    {
        /// <summary>
        /// Diseño del disco (para que la mano no parezca quedar debajo del botón: sin profundidad, la oclusión llega
        /// tarde). Solid = metal opaco (antes); Ghost = semitransparente siempre; Fade = opaco lejos, se vuelve
        /// translúcido al acercar la mano; Ring = al acercar la mano el disco desaparece y queda solo el anillo;
        /// Field (por defecto desde 2026-10-15) = sin disco: un campo de puntos con forma de gaussiana 3D cuya cima sube
        /// hacia la punta del dedo (Shaders/PadField) y texto curvo en Geist. Nada sólido: no necesita oclusión por la mano.
        /// Orb (por defecto desde 2026-10-22) = esfera de puntos que se activa con la punta del índice.
        /// </summary>
        public enum PadStyle { Solid, Ghost, Fade, Ring, Field, Orb }

        public PadStyle Style = PadStyle.Orb;
        public bool IsOrb => Style == PadStyle.Orb && _orb != null;

        /// <summary>0..1: recién movido, tenue y desactivado hasta que la mano sale (se suaviza).</summary>
        public float Dim;
        /// <summary>Orb: dirección (espacio del botón) por donde entró el dedo: de ahí parte el llenado y la onda.</summary>
        public Vector3 HoldDir = Vector3.back;

        /// <summary>Punta del dedo más cercana en coordenadas del disco; w = 0..1 fuerza (0 = sin mano cerca).</summary>
        public Vector4 HandLocal;

        public const float PadRadius = 0.045f;
        public const float OrbRadius = 0.035f;   // esfera de 7 cm
        // Zona del texto en coordenadas del disco (para saber si la mano lo tapa).
        public const float LabelHalfWidth = 0.06f, LabelBottom = PadRadius + 0.012f, LabelTop = PadRadius + 0.05f;
        private const float CoveredAlpha = 0.12f;   // cuánto queda del botón con la mano encima
        private const float CoverInPerSec = 30f;    // se va en ~35 ms (que no se vea encima de la mano)
        private const float CoverOutPerSec = 6f;    // vuelve en ~170 ms
        private const float AppearSeconds = 0.6f;
        private const float MoveSeconds = 0.85f;
        private const float RippleSeconds = 0.55f;
        private const float ColorFadeSeconds = 0.3f;

        public static readonly Color IdleColor = new Color(0.11f, 0.69f, 0.48f);   // verde agua
        public static readonly Color TurnColor = new Color(0.92f, 0.41f, 0.20f);   // naranjo (el mismo del panel)
        private static readonly Color PinchColor = new Color(0.82f, 0.90f, 1f);
        private static readonly Color Base = new Color(0.09f, 0.1f, 0.12f);

        /// <summary>La pose del botón en la sala (la que usa el gesto).</summary>
        public readonly Transform Root;
        private readonly Transform _body, _face, _labels;
        private readonly MeshRenderer _faceRenderer, _baseRenderer;
        private readonly MaterialPropertyBlock _faceBlock = new MaterialPropertyBlock(), _baseBlock = new MaterialPropertyBlock();
        private readonly Material _metal, _ghostBase, _ghostFace;
        private float _near;   // 0 = mano lejos, 1 = mano sobre el disco (suavizado)
        private float _cover;  // 0..1: la mano tapa el botón visto desde el ojo (suavizado, rápido al tapar)
        private float Visibility => Mathf.Lerp(1f, CoveredAlpha, _cover);
        private readonly PadArc _track, _progress, _rippleA, _rippleB, _pinchTrack, _pinchArc;
        private readonly TextMeshPro _label, _sub;
        private readonly AudioSource _audio;
        private readonly MeshRenderer _field;
        private readonly MaterialPropertyBlock _fieldBlock = new MaterialPropertyBlock();
        private float _flash, _fieldT, _dim;
        private readonly MeshRenderer _orb;
        private readonly MaterialPropertyBlock _orbBlock = new MaterialPropertyBlock();
        private const int OrbDots = 1000, OrbCores = 3;
        private readonly MeshRenderer[] _fingers = new MeshRenderer[2];
        private readonly MaterialPropertyBlock[] _fingerBlocks = { new MaterialPropertyBlock(), new MaterialPropertyBlock() };
        private const int FingerDots = 220, TipGlows = 5, TetherDots = 80, Sheaths = 14;
        private const int FieldDots = 1100;
        public const float FieldRadius = 0.075f;   // radio del campo de puntos (m)
        private PadStyle _appliedStyle = (PadStyle)(-1);
        private readonly TMP_FontAsset _defaultFont, _fieldFont;
        private readonly Material _defaultText, _fieldText;

        private float _appearT = -1f, _moveT = -1f, _colorT = 1f, _rippleAT = -1f, _rippleBT = -1f;
        private Pose _from, _to;
        private float _arcHeight;
        private Color _colorFrom = IdleColor, _colorTo = IdleColor;
        private float _pinchProgress = -1f, _pinchCollapseT = -1f;

        public bool Moving => _moveT >= 0f;

        public TurnPadVisual(Transform owner, CueAssets assets, int layer, List<Material> owned)
        {
            Root = Child("Pad", owner, layer);
            _body = Child("Body", Root, layer);

            _metal = assets.Metal;
            _ghostBase = Ghost(assets.Alpha, 3004, owned);
            _ghostFace = Ghost(assets.Alpha, 3005, owned);
            _baseRenderer = Disc("Base", _body, assets, PadRadius, 0.012f, 0.004f, 0f, layer).GetComponent<MeshRenderer>();
            GameObject face = Disc("Face", _body, assets, PadRadius * 0.78f, 0.008f, 0.003f, -0.009f, layer);
            _face = face.transform;
            _faceRenderer = face.GetComponent<MeshRenderer>();

            _track = new PadArc("Track", _body, assets.Alpha, layer, owned);
            _track.Transform.localPosition = new Vector3(0f, 0f, -0.0135f);
            _progress = new PadArc("Progress", _body, assets.Alpha, layer, owned);
            _progress.Transform.localPosition = new Vector3(0f, 0f, -0.014f);
            if (assets.PadField != null) _field = Field("Field", _body, assets.PadField, layer, owned);
            if (assets.PadOrb != null) _orb = Orb("Orb", _body, assets.PadOrb, layer, owned);
            else Style = PadStyle.Field;
            if (assets.FingerGlow != null)
                for (int i = 0; i < 2; i++) _fingers[i] = Finger(i == 0 ? "FingerGlowLeft" : "FingerGlowRight", owner, assets.FingerGlow, layer, owned);

            // Ondas en el mundo (no giran con el disco).
            _rippleA = new PadArc("RippleFrom", owner, assets.Alpha, layer, owned);
            _rippleB = new PadArc("RippleTo", owner, assets.Alpha, layer, owned);
            _pinchTrack = new PadArc("PinchTrack", owner, assets.Alpha, layer, owned);
            _pinchArc = new PadArc("PinchProgress", _pinchTrack.Transform, assets.Alpha, layer, owned);
            _pinchArc.Transform.localPosition = new Vector3(0f, 0f, -0.0005f);
            _rippleA.Visible = _rippleB.Visible = _pinchTrack.Visible = false;

            // Texto acostado más allá del disco (+Y local = lejos del participante).
            _labels = Child("Labels", Root, layer);
            _labels.localPosition = new Vector3(0f, PadRadius + 0.03f, -0.006f);
            Material textMat = new Material(assets.Text);
            textMat.SetColor("_OutlineColor", new Color(0.05f, 0.05f, 0.06f));
            owned.Add(textMat);
            _label = Text("Label", _labels, assets.Font, textMat, 0.22f, new Vector3(0f, 0.013f, 0f), layer);
            _sub = Text("Sub", _labels, assets.Font, textMat, 0.145f, new Vector3(0f, -0.009f, 0f), layer);
            _sub.fontStyle = FontStyles.Bold;
            _defaultFont = assets.Font;
            _defaultText = textMat;
            _fieldFont = assets.PadFont;
            // Texto curvo (solo «field»): cada letra se pone sobre un arco alrededor del campo, después de que TMP arma la
            // malla y antes de subirla, así sobrevive a cada cambio de texto (el contador del turno cambia cada segundo).
            _label.OnPreRenderText += info => Curve(info, true);
            _sub.OnPreRenderText += info => Curve(info, false);
            if (assets.PadText != null) { _fieldText = new Material(assets.PadText); owned.Add(_fieldText); }

            _audio = owner.gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0.7f;
            _audio.minDistance = 0.5f;
            _audio.maxDistance = 6f;
            _audio.rolloffMode = AudioRolloffMode.Linear;
            ApplyColor(IdleColor);
        }

        // ---------------- órdenes ----------------

        /// <summary>Orientación horizontal: cara hacia arriba, texto legible desde <paramref name="eye"/>.</summary>
        public static Quaternion FlatRotation(Vector3 pos, Vector3 eye, Vector3 fallbackForward)
        {
            Vector3 away = Vector3.ProjectOnPlane(pos - eye, Vector3.up);
            if (away.sqrMagnitude < 1e-4f) away = Vector3.ProjectOnPlane(fallbackForward, Vector3.up);
            if (away.sqrMagnitude < 1e-6f) away = Vector3.forward;
            // -Z local (la cara) hacia arriba; +Y local (arriba del texto) alejándose del participante.
            return Quaternion.LookRotation(Vector3.down, away.normalized);
        }

        /// <summary>
        /// Orb: de cara a <paramref name="eye"/> (también inclinado hacia arriba: está bajo los ojos) y con el «arriba» de
        /// la cabeza (<paramref name="headUp"/>), así el texto se lee derecho y el aro se ve redondo. Disco: horizontal
        /// (<see cref="FlatRotation"/>).
        /// </summary>
        public Quaternion RotationFor(Vector3 pos, Vector3 eye, Vector3 fallbackForward, Vector3 headUp)
        {
            if (!IsOrb) return FlatRotation(pos, eye, fallbackForward);
            Vector3 away = pos - eye;
            if (away.sqrMagnitude < 1e-4f) away = fallbackForward;
            Vector3 up = Vector3.Cross(away, headUp).sqrMagnitude > 1e-6f ? headUp : Vector3.up;
            if (Vector3.Cross(away, up).sqrMagnitude < 1e-6f) away = Vector3.forward;
            return Quaternion.LookRotation(away.normalized, up);
        }

        /// <summary>
        /// Acompañar al participante: la posición la suaviza TurnFollow; aquí solo se gira el texto hacia los ojos con
        /// calma. No hace nada mientras viaja (pinza).
        /// </summary>
        public void Follow(Vector3 pos, Vector3 eye, Vector3 fallbackForward, Vector3 headUp, float dt)
        {
            if (Moving) return;
            Quaternion target = RotationFor(pos, eye, fallbackForward, headUp);
            Root.SetPositionAndRotation(pos, Quaternion.Slerp(Root.rotation, target, 1f - Mathf.Exp(-dt * 6f)));
        }

        /// <summary>
        /// Índice iluminado de una mano (0 izquierda, 1 derecha): articulaciones del nudillo a la punta (mundo), el punto
        /// de la esfera más cercano, brillo 0..1 y el hilo hacia la esfera 0..1. glow = 0 lo apaga.
        /// </summary>
        public void SetFinger(int hand, Vector3 knuckle, Vector3 mid, Vector3 distal, Vector3 tip, Vector3 orbPoint, float glow, float tether, float hold)
        {
            MeshRenderer r = _fingers[hand];
            if (r == null) return;
            bool on = IsOrb && (glow > 0.01f || tether > 0.01f);
            if (r.enabled != on) r.enabled = on;
            if (!on) return;
            MaterialPropertyBlock b = _fingerBlocks[hand];
            b.SetVector("_P0", knuckle);
            b.SetVector("_P1", mid);
            b.SetVector("_P2", distal);
            b.SetVector("_P3", tip);
            b.SetVector("_P4", orbPoint);
            b.SetFloat("_Glow", glow);
            b.SetFloat("_Tether", tether);
            b.SetFloat("_Hold", hold);
            b.SetFloat("_T", _fieldT);
            b.SetColor("_Color", CurrentColor);
            r.SetPropertyBlock(b);
        }

        public void HideFingers()
        {
            foreach (MeshRenderer r in _fingers) if (r != null && r.enabled) r.enabled = false;
        }

        public void Appear(Pose pose)
        {
            Root.SetPositionAndRotation(pose.position, pose.rotation);
            _moveT = -1f;
            _appearT = 0f;
            Ripple(_rippleB, ref _rippleBT, pose);
        }

        public void MoveTo(Pose pose)
        {
            _from = new Pose(Root.position, Root.rotation);
            _to = pose;
            float dist = Vector3.Distance(_from.position, _to.position);
            _arcHeight = Mathf.Clamp(dist * 0.35f, 0.04f, 0.15f);
            _moveT = 0f;
            _appearT = -1f;
            Ripple(_rippleA, ref _rippleAT, _from);
            if (_audio != null && Application.isPlaying) _audio.PlayOneShot(CueSynth.Whoosh(), 0.35f);
        }

        public void SetTurnState(bool inTurn, bool animate)
        {
            Color target = inTurn ? TurnColor : IdleColor;
            if (target == _colorTo) return;
            _colorFrom = CurrentColor;
            _colorTo = target;
            _colorT = animate ? 0f : 1f;
            if (animate)
            {
                Ripple(_rippleB, ref _rippleBT, new Pose(Root.position, Root.rotation));
                _flash = 1f;
                if (_audio != null && Application.isPlaying) _audio.PlayOneShot(CueSynth.For(inTurn ? CueTone.Good : CueTone.Info), 0.7f);
            }
        }

        public void SetText(string label, string sub)
        {
            if (_label.text != label) _label.text = label;
            if (_sub.text != sub) _sub.text = sub;
        }

        /// <summary>Pinza en curso: progreso 0..1 en <paramref name="at"/>, mirando a <paramref name="eye"/>. -1 = sin pinza.</summary>
        public void Pinch(float progress, Vector3 at, Vector3 eye)
        {
            if (_pinchCollapseT >= 0f) return;   // se está cerrando sobre los dedos: que termine
            if (progress < 0f)
            {
                _pinchTrack.Visible = false;
                _pinchProgress = -1f;
                return;
            }
            _pinchProgress = progress;
            _pinchCollapseT = -1f;
            _pinchTrack.Visible = true;
            _pinchTrack.Transform.SetPositionAndRotation(at, Quaternion.LookRotation(at - eye, Vector3.up));
            _pinchTrack.Transform.localScale = Vector3.one;
        }

        /// <summary>La pinza se completó: el anillo se cierra sobre los dedos (0,2 s).</summary>
        public void PinchDone()
        {
            _pinchProgress = 1f;
            _pinchCollapseT = 0f;
        }

        // ---------------- cuadro a cuadro ----------------

        /// <summary>Cambio de color pasando por un tono claro (verde→naranjo directo pasa por un oliva sucio).</summary>
        public Color CurrentColor
        {
            get
            {
                float k = Mathf.Clamp01(_colorT);
                if (k >= 1f) return _colorTo;
                Color light = Color.Lerp(Color.Lerp(_colorFrom, _colorTo, 0.5f), Color.white, 0.55f);
                return k < 0.4f ? Color.Lerp(_colorFrom, light, EaseInOut(k / 0.4f)) : Color.Lerp(light, _colorTo, EaseInOut((k - 0.4f) / 0.6f));
            }
        }

        /// <param name="hold">progreso de «mantener» 0..1</param>
        /// <param name="press">cuánto se hunde la cara (m)</param>
        /// <param name="near">cercanía de la mano al disco 0..1 (1 = encima)</param>
        /// <param name="cover">0..1 la mano tapa el botón visto desde el ojo: se desvanece rápido (todos los diseños)</param>
        public void Tick(float dt, float hold, float press, bool hover, float near = 0f, float cover = 0f)
        {
            _cover = Mathf.MoveTowards(_cover, Mathf.Clamp01(cover), dt * (cover > _cover ? CoverInPerSec : CoverOutPerSec));
            _colorT += dt / ColorFadeSeconds;
            _flash = Mathf.MoveTowards(_flash, 0f, dt * 3f);
            _near = Mathf.MoveTowards(_near, Mathf.Clamp01(near), dt * (near > _near ? 8f : 3f));
            _dim = Mathf.MoveTowards(_dim, Mathf.Clamp01(Dim), dt * (Dim > _dim ? 6f : 2.5f));   // se apaga rápido, vuelve con calma
            float lit = Mathf.Lerp(1f, 0.3f, _dim);
            Color c = CurrentColor;
            ApplyColor(c);

            // Mover: arco hacia arriba + una vuelta completa sobre la normal.
            float spin = 0f;
            if (_moveT >= 0f)
            {
                _moveT += dt / MoveSeconds;
                float u = EaseInOut(Mathf.Clamp01(_moveT));
                Vector3 p = Vector3.Lerp(_from.position, _to.position, u) + Vector3.up * (Mathf.Sin(Mathf.PI * u) * _arcHeight);
                Root.SetPositionAndRotation(p, Quaternion.Slerp(_from.rotation, _to.rotation, u));
                spin = 360f * u;
                _body.localScale = Vector3.one * (1f - 0.12f * Mathf.Sin(Mathf.PI * u));
                if (_moveT >= 1f)
                {
                    _moveT = -1f;
                    Root.SetPositionAndRotation(_to.position, _to.rotation);
                    Ripple(_rippleB, ref _rippleBT, _to);
                }
            }
            else if (_appearT >= 0f)
            {
                _appearT += dt / AppearSeconds;
                float s = EaseOut(Mathf.Clamp01((_appearT - 0.25f) / 0.75f));
                _body.localScale = Vector3.one * Mathf.Lerp(0.55f, 1f, s);
                if (_appearT >= 1f) { _appearT = -1f; _body.localScale = Vector3.one; }
            }
            _body.localRotation = Quaternion.Euler(0f, 0f, spin);
            _face.localPosition = new Vector3(0f, 0f, -0.009f + press);

            // Anillo base: se dibuja al aparecer; más visible con el dedo cerca.
            float trackSweep = _appearT >= 0f ? 360f * EaseOut(Mathf.Clamp01(_appearT / 0.58f)) : 360f;
            float trackAlpha = Moving ? 0.2f : hover ? 0.6f : 0.35f;
            if (Style == PadStyle.Ring || Style == PadStyle.Ghost) trackAlpha = Mathf.Max(trackAlpha, Mathf.Lerp(0.5f, 0.95f, _near));
            bool field = Style == PadStyle.Field && _field != null, orb = IsOrb;
            // Campo: el contorno es una línea finísima en el borde del campo de puntos. Orb: un aro alrededor de la
            // esfera, de cara al participante (el botón está de pie).
            float tIn = orb ? OrbRadius + 0.0060f : field ? FieldRadius + 0.0038f : 0.0505f;
            float tOut = orb ? OrbRadius + 0.0067f : field ? FieldRadius + 0.0046f : 0.0525f;
            float pIn = orb ? OrbRadius + 0.0050f : field ? FieldRadius + 0.0025f : 0.0495f;
            float pOut = orb ? OrbRadius + 0.0085f : field ? FieldRadius + 0.0060f : 0.0565f;
            if (field) trackAlpha = Mathf.Lerp(0.22f, 0.5f, Mathf.Max(_near, hold));
            if (orb) trackAlpha = Mathf.Lerp(0.14f, 0.42f, Mathf.Max(_near, hold));
            _track.Set(tIn, tOut, 0f, trackSweep, WithAlpha(Color.Lerp(c, Color.white, field || orb ? 0.5f : 0f), trackAlpha * Visibility * lit));
            _fieldT += dt;
            TickField(c, hold);
            TickOrb(c, hold);
            if (!orb) HideFingers();

            // Progreso de «mantener»: arco horario desde las 12. Mientras viaja: dos segmentos que orbitan el anillo
            // (el giro de un disco liso no se ve).
            bool showProgress = hold > 0.01f && !Moving;
            _progress.Visible = showProgress || Moving;
            if (Moving)
            {
                float orbit = 540f * EaseInOut(Mathf.Clamp01(_moveT)) + 30f;
                _progress.Set(pIn, pOut, orbit, 70f, WithAlpha(Color.Lerp(c, Color.white, 0.35f), 0.95f));
                _track.Set(pIn, pOut, orbit + 180f, 70f, WithAlpha(Color.Lerp(c, Color.white, 0.35f), 0.95f));
            }
            else if (showProgress) _progress.Set(pIn, pOut, 0f, 360f * Mathf.Clamp01(hold), WithAlpha(Color.Lerp(c, Color.white, 0.35f), 1f));   // el progreso se ve siempre (va por el borde)

            // Texto: oculto mientras se mueve o aparece.
            bool labels = !Moving && (_appearT < 0f || _appearT > 0.5f);
            if (_labels.gameObject.activeSelf != labels) _labels.gameObject.SetActive(labels);
            _sub.color = Color.Lerp(Color.white, c, 0.55f);
            _label.alpha = _sub.alpha = Visibility * Mathf.Lerp(1f, 0.45f, _dim);

            TickRipple(_rippleA, ref _rippleAT, dt);
            TickRipple(_rippleB, ref _rippleBT, dt);
            TickPinch(dt);
        }

        private void TickPinch(float dt)
        {
            if (!_pinchTrack.Visible) return;
            float r0 = 0.024f, r1 = 0.0262f;
            if (_pinchCollapseT >= 0f)
            {
                _pinchCollapseT += dt / 0.2f;
                float k = 1f - EaseInOut(Mathf.Clamp01(_pinchCollapseT));
                _pinchTrack.Transform.localScale = Vector3.one * Mathf.Max(0.01f, k);
                if (_pinchCollapseT >= 1f) { _pinchTrack.Visible = false; _pinchCollapseT = -1f; _pinchProgress = -1f; return; }
            }
            _pinchTrack.Set(r0, r1, 0f, 360f, WithAlpha(PinchColor, 0.3f));
            _pinchArc.Set(r0 - 0.0008f, r1 + 0.0018f, 0f, 360f * Mathf.Clamp01(_pinchProgress), WithAlpha(PinchColor, 1f));
        }

        // ---------------- ondas ----------------

        private static void Ripple(PadArc arc, ref float t, Pose at)
        {
            arc.Transform.SetPositionAndRotation(at.position + at.rotation * new Vector3(0f, 0f, -0.012f), at.rotation);
            arc.Visible = true;
            t = 0f;
        }

        private void TickRipple(PadArc arc, ref float t, float dt)
        {
            if (t < 0f) return;
            t += dt / RippleSeconds;
            float k = EaseOut(Mathf.Clamp01(t));
            float r = IsOrb ? Mathf.Lerp(OrbRadius + 0.006f, 0.085f, k) : Mathf.Lerp(0.052f, 0.11f, k);
            arc.Set(r, r + Mathf.Lerp(0.003f, 0.0008f, k), 0f, 360f, WithAlpha(CurrentColor, 0.65f * (1f - k)));
            if (t >= 1f) { t = -1f; arc.Visible = false; }
        }

        // ---------------- armado ----------------

        private void TickField(Color c, float hold)
        {
            bool on = Style == PadStyle.Field && _field != null;
            if (_field != null && _field.enabled != on) _field.enabled = on;
            if (!on) return;
            _fieldBlock.SetFloat("_T", _fieldT);
            _fieldBlock.SetColor("_Color", c);
            _fieldBlock.SetFloat("_Near", _near);
            _fieldBlock.SetFloat("_Hold", Mathf.Clamp01(hold));
            _fieldBlock.SetFloat("_Flash", _flash);
            _fieldBlock.SetVector("_Hand", HandLocal);
            _field.SetPropertyBlock(_fieldBlock);
        }

        private void TickOrb(Color c, float hold)
        {
            bool on = IsOrb;
            if (_orb != null && _orb.enabled != on) _orb.enabled = on;
            if (!on) return;
            _orbBlock.SetFloat("_T", _fieldT);
            _orbBlock.SetColor("_Color", c);
            _orbBlock.SetFloat("_Near", _near);
            _orbBlock.SetFloat("_Hold", Mathf.Clamp01(hold));
            _orbBlock.SetFloat("_Flash", _flash);
            _orbBlock.SetFloat("_Dim", _dim);
            _orbBlock.SetFloat("_R", OrbRadius);
            _orbBlock.SetVector("_Hand", HandLocal);
            _orbBlock.SetVector("_HoldDir", HoldDir);
            _orb.SetPropertyBlock(_orbBlock);
        }

        /// <summary>Esfera: <see cref="OrbDots"/> quads en direcciones de Fibonacci (parejas, sin polos marcados) + el núcleo.</summary>
        private static MeshRenderer Orb(string name, Transform parent, Material mat, int layer, List<Material> owned)
        {
            int n = OrbDots + OrbCores;
            var seeds = new List<Vector4>(n * 4);
            var rng = new System.Random(1022);
            const float golden = 2.39996323f;
            for (int i = 0; i < n; i++)
            {
                Vector4 seed;
                if (i < OrbDots)
                {
                    float y = 1f - 2f * (i + 0.5f) / OrbDots, r = Mathf.Sqrt(1f - y * y), phi = i * golden;
                    seed = new Vector4(Mathf.Cos(phi) * r, y, Mathf.Sin(phi) * r, (float)rng.NextDouble());
                }
                else seed = new Vector4(0f, 0f, 0f, (float)rng.NextDouble());   // núcleo
                for (int k = 0; k < 4; k++) seeds.Add(seed);
            }
            return Quads(name, parent, mat, layer, owned, seeds, 0.3f);
        }

        /// <summary>Índice iluminado: corrientes por el dedo, brillo de la punta y el hilo a la esfera (posiciones en el shader).</summary>
        private static MeshRenderer Finger(string name, Transform parent, Material mat, int layer, List<Material> owned)
        {
            var seeds = new List<Vector4>((FingerDots + TipGlows + TetherDots + Sheaths + 1) * 4);
            var rng = new System.Random(name.Length * 77);
            void Add(Vector4 seed) { for (int k = 0; k < 4; k++) seeds.Add(seed); }
            for (int i = 0; i < FingerDots; i++) Add(new Vector4((i + (float)rng.NextDouble()) / FingerDots, (float)rng.NextDouble(), (float)rng.NextDouble(), 0f));
            for (int i = 0; i < TipGlows; i++) Add(new Vector4(0f, 0f, (i + 0.5f) / TipGlows, 1f));
            for (int i = 0; i < TetherDots; i++) Add(new Vector4((i + 0.5f) / TetherDots, (float)rng.NextDouble(), (float)rng.NextDouble(), 2f));
            for (int i = 0; i < Sheaths; i++) Add(new Vector4((i + 0.5f) / Sheaths, 0f, (float)rng.NextDouble(), 3f));
            Add(new Vector4(1f, 0f, 0.5f, 4f));   // aro de la punta
            MeshRenderer r = Quads(name, parent, mat, layer, owned, seeds, 2000f);   // posiciones del mundo: que nunca se recorte
            r.enabled = false;
            return r;
        }

        private static MeshRenderer Quads(string name, Transform parent, Material mat, int layer, List<Material> owned, List<Vector4> seeds, float bounds)
        {
            int n = seeds.Count / 4;
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            var corners = new Vector2[n * 4];
            var tris = new int[n * 6];
            Vector2[] q = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < 4; k++) corners[i * 4 + k] = q[k];
                int v = i * 4, t = i * 6;
                tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2; tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
            }
            var mesh = new Mesh { name = name + "Dots", vertices = new Vector3[n * 4], uv = corners, triangles = tris };
            mesh.SetUVs(1, seeds);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * bounds);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var m = new Material(mat);
            owned.Add(m);
            r.sharedMaterial = m;
            Setup(r);
            return r;
        }

        /// <summary>Malla fija de <see cref="FieldDots"/> quads (esquina en uv0, semillas en uv1); el shader los mueve.</summary>
        private static MeshRenderer Field(string name, Transform parent, Material mat, int layer, List<Material> owned)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            var verts = new Vector3[FieldDots * 4];
            var corners = new Vector2[FieldDots * 4];
            var seeds = new List<Vector4>(FieldDots * 4);
            var tris = new int[FieldDots * 6];
            var rng = new System.Random(1015);
            Vector2[] q = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            const float golden = 2.39996323f;   // patrón de girasol (filotaxis): reparto parejo sin anillos visibles
            for (int i = 0; i < FieldDots; i++)
            {
                float rad = FieldRadius * Mathf.Sqrt((i + 0.5f) / FieldDots), ang = i * golden;
                var seed = new Vector4(rad * Mathf.Cos(ang), rad * Mathf.Sin(ang), (float)i / FieldDots, (float)rng.NextDouble());
                for (int k = 0; k < 4; k++) { corners[i * 4 + k] = q[k]; seeds.Add(seed); }
                int v = i * 4, t = i * 6;
                tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2; tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
            }
            var mesh = new Mesh { name = "PadFieldDots", vertices = verts, uv = corners, triangles = tris };
            mesh.SetUVs(1, seeds);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 0.3f);   // los puntos se mueven en el shader
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var m = new Material(mat);
            owned.Add(m);
            r.sharedMaterial = m;
            Setup(r);
            return r;
        }

        // Al cambiar de diseño: tipografía y lugar del texto (el campo es más ancho que el disco).
        private void ApplyStyle()
        {
            _appliedStyle = Style;
            if (IsOrb && _fieldFont != null && _fieldText != null)
            {
                // Orb: título arriba de la esfera y subtítulo abajo, derechos y de cara al participante.
                _labels.localPosition = Vector3.zero;
                foreach (TextMeshPro t in new[] { _label, _sub })
                {
                    t.font = _fieldFont;
                    t.fontSharedMaterial = _fieldText;
                    t.fontStyle = FontStyles.UpperCase;
                }
                _label.fontSize = 0.2f;
                _label.characterSpacing = 18f;
                _sub.fontSize = 0.13f;
                _sub.characterSpacing = 20f;
                _label.transform.localPosition = new Vector3(0f, OrbRadius + 0.024f, 0f);
                _sub.transform.localPosition = new Vector3(0f, -(OrbRadius + 0.02f), 0f);
                _label.ForceMeshUpdate();
                _sub.ForceMeshUpdate();
                return;
            }
            bool field = Style == PadStyle.Field && _fieldFont != null && _fieldText != null;
            // Campo: el texto se centra en el botón y Curve lo dobla en arcos alrededor de los puntos.
            _labels.localPosition = field ? new Vector3(0f, 0f, -0.004f) : new Vector3(0f, PadRadius + 0.03f, -0.006f);
            foreach (TextMeshPro t in new[] { _label, _sub })
            {
                t.font = field ? _fieldFont : _defaultFont;
                t.fontSharedMaterial = field ? _fieldText : _defaultText;
                t.fontStyle = field ? FontStyles.UpperCase : FontStyles.Bold | FontStyles.UpperCase;
            }
            _label.fontSize = field ? 0.2f : 0.22f;
            _label.characterSpacing = field ? 22f : 3f;
            _sub.fontSize = field ? 0.125f : 0.145f;
            _sub.characterSpacing = field ? 26f : 3f;
            _label.transform.localPosition = field ? Vector3.zero : new Vector3(0f, 0.013f, 0f);
            _sub.transform.localPosition = field ? Vector3.zero : new Vector3(0f, -0.009f, 0f);
            _label.ForceMeshUpdate();
            _sub.ForceMeshUpdate();
        }

        // Radios de los arcos del texto (m): el título por arriba, hacia afuera; el subtítulo por abajo, de cara al
        // participante (las letras crecen hacia el centro, así su base queda en el radio de afuera).
        private const float TitleArcRadius = FieldRadius + 0.011f;
        private const float SubArcRadius = FieldRadius + 0.019f;

        /// <summary>
        /// Dobla el texto ya armado por TMP sobre un arco. Arriba (<paramref name="top"/>): de izquierda a derecha en
        /// sentido horario, la parte de arriba de las letras hacia afuera. Abajo: sentido antihorario, las letras
        /// derechas para quien mira desde el borde cercano.
        /// </summary>
        private void Curve(TMP_TextInfo info, bool top)
        {
            if (_appliedStyle != PadStyle.Field || _fieldFont == null || info == null) return;
            float r = top ? TitleArcRadius : SubArcRadius;
            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo ci = info.characterInfo[i];
                if (!ci.isVisible) continue;
                Vector3[] v = info.meshInfo[ci.materialReferenceIndex].vertices;
                int vi = ci.vertexIndex;
                var pivot = new Vector3((v[vi].x + v[vi + 2].x) * 0.5f, ci.baseLine, 0f);
                float ang = top ? Mathf.PI * 0.5f - pivot.x / r : -Mathf.PI * 0.5f + pivot.x / r;
                var at = new Vector3(r * Mathf.Cos(ang), r * Mathf.Sin(ang), 0f);
                Quaternion q = Quaternion.Euler(0f, 0f, (top ? ang - Mathf.PI * 0.5f : ang + Mathf.PI * 0.5f) * Mathf.Rad2Deg);
                for (int k = 0; k < 4; k++) v[vi + k] = at + q * (v[vi + k] - pivot);
            }
        }

        private void ApplyColor(Color c)
        {
            if (_appliedStyle != Style) ApplyStyle();
            bool field = Style == PadStyle.Field || IsOrb;   // sin disco
            if (_faceRenderer.enabled == field) { _faceRenderer.enabled = !field; _baseRenderer.enabled = !field; }
            if (field) return;
            if (Style == PadStyle.Solid && _cover < 0.01f)
            {
                if (_faceRenderer.sharedMaterial != _metal) { _faceRenderer.sharedMaterial = _metal; _baseRenderer.sharedMaterial = _metal; }
                _faceBlock.SetColor("_Color", c);
                _faceBlock.SetFloat("_Metallic", 0.3f);
                _faceBlock.SetFloat("_Glossiness", 0.8f);
                _baseBlock.SetColor("_Color", Base);
                _baseBlock.SetFloat("_Metallic", 0.6f);
                _baseBlock.SetFloat("_Glossiness", 0.75f);
            }
            else
            {
                if (_faceRenderer.sharedMaterial != _ghostFace) { _faceRenderer.sharedMaterial = _ghostFace; _baseRenderer.sharedMaterial = _ghostBase; }
                float face, bas;
                switch (Style)
                {
                    case PadStyle.Ghost: face = 0.45f; bas = 0.3f; break;
                    case PadStyle.Fade: face = Mathf.Lerp(0.95f, 0.18f, _near); bas = Mathf.Lerp(0.9f, 0.08f, _near); break;
                    case PadStyle.Ring: face = Mathf.Lerp(0.95f, 0f, _near); bas = Mathf.Lerp(0.9f, 0f, _near); break;
                    default: face = 0.95f; bas = 0.9f; break;   // Solid tapado por la mano: translúcido mientras tanto
                }
                face *= Visibility;
                bas *= Visibility;
                _faceBlock.SetColor("_Color", WithAlpha(c, face));
                _baseBlock.SetColor("_Color", WithAlpha(Color.Lerp(Base, c, 0.25f), bas));
            }
            _faceRenderer.SetPropertyBlock(_faceBlock);
            _baseRenderer.SetPropertyBlock(_baseBlock);
        }

        /// <summary>Material translúcido (CueFx alpha premultiplicado) para el disco; color por _Color.</summary>
        private static Material Ghost(Material alpha, int queue, List<Material> owned)
        {
            var m = new Material(alpha) { mainTexture = Texture2D.whiteTexture, renderQueue = queue };
            m.SetFloat("_Premul", 1f);
            m.SetFloat("_Intensity", 1f);
            owned.Add(m);
            return m;
        }

        private static Transform Child(string name, Transform parent, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static GameObject Disc(string name, Transform parent, CueAssets assets, float radius, float depth, float bevel, float z, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            Mesh mesh = CueMeshes.Extrude(new[] { CueMeshes.Circle(radius, 64) }, depth, bevel, name);
            var white = new Color[mesh.vertexCount];
            for (int i = 0; i < white.Length; i++) white[i] = Color.white;
            mesh.colors = white;   // el shader translúcido multiplica por el color del vértice
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = assets.Metal;
            Setup(r);
            return go;
        }

        private static TextMeshPro Text(string name, Transform parent, TMP_FontAsset font, Material mat, float size, Vector3 pos, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = font;
            tmp.fontSharedMaterial = mat;
            tmp.fontSize = size;
            tmp.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            tmp.characterSpacing = 3f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.rectTransform.sizeDelta = new Vector2(0.5f, 0.05f);
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) Setup(r);
            return tmp;
        }

        private static void Setup(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);
        private static float EaseOut(float x) => 1f - Mathf.Pow(1f - x, 3f);
        private static float EaseInOut(float x) => x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f;
    }
}
