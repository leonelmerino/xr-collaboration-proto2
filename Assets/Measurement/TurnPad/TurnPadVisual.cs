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
    /// </summary>
    public sealed class TurnPadVisual
    {
        /// <summary>
        /// Diseño del disco (para que la mano no parezca quedar debajo del botón: sin profundidad, la oclusión llega
        /// tarde). Solid = metal opaco (antes); Ghost = semitransparente siempre; Fade = opaco lejos, se vuelve
        /// translúcido al acercar la mano; Ring = al acercar la mano el disco desaparece y queda solo el anillo.
        /// </summary>
        public enum PadStyle { Solid, Ghost, Fade, Ring }

        public PadStyle Style = PadStyle.Solid;

        public const float PadRadius = 0.045f;
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
        private readonly PadArc _track, _progress, _rippleA, _rippleB, _pinchTrack, _pinchArc;
        private readonly TextMeshPro _label, _sub;
        private readonly AudioSource _audio;

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
        public void Tick(float dt, float hold, float press, bool hover, float near = 0f)
        {
            _colorT += dt / ColorFadeSeconds;
            _near = Mathf.MoveTowards(_near, Mathf.Clamp01(near), dt * (near > _near ? 8f : 3f));
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
            _track.Set(0.0505f, 0.0525f, 0f, trackSweep, WithAlpha(c, trackAlpha));

            // Progreso de «mantener»: arco horario desde las 12. Mientras viaja: dos segmentos que orbitan el anillo
            // (el giro de un disco liso no se ve).
            bool showProgress = hold > 0.01f && !Moving;
            _progress.Visible = showProgress || Moving;
            if (Moving)
            {
                float orbit = 540f * EaseInOut(Mathf.Clamp01(_moveT)) + 30f;
                _progress.Set(0.0495f, 0.0565f, orbit, 70f, WithAlpha(Color.Lerp(c, Color.white, 0.35f), 0.95f));
                _track.Set(0.0495f, 0.0565f, orbit + 180f, 70f, WithAlpha(Color.Lerp(c, Color.white, 0.35f), 0.95f));
            }
            else if (showProgress) _progress.Set(0.0495f, 0.0565f, 0f, 360f * Mathf.Clamp01(hold), WithAlpha(Color.Lerp(c, Color.white, 0.35f), 1f));

            // Texto: oculto mientras se mueve o aparece.
            bool labels = !Moving && (_appearT < 0f || _appearT > 0.5f);
            if (_labels.gameObject.activeSelf != labels) _labels.gameObject.SetActive(labels);
            _sub.color = Color.Lerp(Color.white, c, 0.55f);

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
            float r = Mathf.Lerp(0.052f, 0.11f, k);
            arc.Set(r, r + Mathf.Lerp(0.003f, 0.0008f, k), 0f, 360f, WithAlpha(CurrentColor, 0.65f * (1f - k)));
            if (t >= 1f) { t = -1f; arc.Visible = false; }
        }

        // ---------------- armado ----------------

        private void ApplyColor(Color c)
        {
            if (Style == PadStyle.Solid)
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
                    default: face = Mathf.Lerp(0.95f, 0f, _near); bas = Mathf.Lerp(0.9f, 0f, _near); break;   // Ring
                }
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
