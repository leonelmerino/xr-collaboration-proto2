using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace XRCollab.Measurement.Cues
{
    /// <summary>
    /// Una señal 3D en el visor: ícono metálico biselado + título con volumen (capas de TMP con relieve y degradé) +
    /// subtítulo, sobre un brillo aditivo y una sombra suave para leerse sobre cualquier fondo. Entra con las letras
    /// en cascada, el ícono girando, una onda, chispas (y confeti o rayos según el tono), un destello de luz que
    /// recorre el texto y sonido; flota unos segundos y sale.
    ///
    /// Todo usa la iluminación de <see cref="PassthroughLighting"/> (ambiente, luz principal y reflejos sacados de la
    /// sala), así el material responde a la luz real. Se mueve con <see cref="Tick"/> (Update en la app; la vista
    /// previa del Editor lo llama a mano).
    /// </summary>
    public sealed class FeedbackCueVisual : MonoBehaviour
    {
        public const float IntroSeconds = 0.8f;
        public const float OutroSeconds = 0.5f;

        private const float TitleSize = 1.15f;       // ~11 cm de alto de línea
        private const float SubtitleSize = 0.5f;
        private const int TitleLayers = 12;
        private const int SubtitleLayers = 3;
        private const float LayerStep = 0.0024f;     // 12 capas ≈ 2,9 cm de profundidad
        private const float ObliqueX = 0.0006f;      // extrusión oblicua: la última capa queda ~7 mm a la derecha
        private const float ObliqueY = 0.0009f;      // y ~1 cm abajo
        private const float IconSize = 0.2f;
        private const float IconDepth = 0.16f;       // relativo al tamaño del ícono
        private const float LetterStagger = 0.035f;
        private const float LetterSeconds = 0.38f;

        public FeedbackCueSpec Spec { get; private set; }
        public bool Leaving => _outroStart >= 0f;
        public bool Done { get; private set; }
        /// <summary>Vista previa del Editor: las partículas avanzan con Simulate en <see cref="Tick"/>.</summary>
        public bool ManualParticles;

        private sealed class TextLayer
        {
            public TextMeshPro Tmp;
            public TMP_MeshInfo[] Source;
            public float Delay;
        }

        private CuePalette _pal;
        private Transform _body, _icon, _iconSpin, _rays, _ring, _glowT, _backdrop;
        private Transform _clockMinute, _clockHour;
        private Light _glint;
        private float _titleWidth;
        private readonly List<TextLayer> _texts = new List<TextLayer>();
        private readonly List<Material> _ownedMaterials = new List<Material>();
        private readonly List<ParticleSystem> _particles = new List<ParticleSystem>();
        private ParticleSystem _sparks, _confetti;
        private Material _glowMat, _raysMat, _ringMat, _backdropMat;
        private AudioSource _audio;
        private float _t, _outroStart = -1f, _holdEnd;
        private bool _whooshPlayed, _tonePlayed, _burstFired, _lettersSettled;
        private int _layer;
        private float _keyIntensity = 1f;

        private static Mesh s_quad;

        // ---------------- armado ----------------

        public static FeedbackCueVisual Create(FeedbackCueSpec spec, CueAssets assets, Vector3 position, Quaternion rotation, int layer, float keyIntensity)
        {
            var go = new GameObject($"[Cue] {spec.Title}");
            go.layer = layer;
            go.transform.SetPositionAndRotation(position, rotation);
            var v = go.AddComponent<FeedbackCueVisual>();
            v.Build(spec, assets, layer, keyIntensity);
            return v;
        }

        private void Build(FeedbackCueSpec spec, CueAssets assets, int layer, float keyIntensity)
        {
            Spec = spec;
            _layer = layer;
            _pal = CuePalette.For(spec.Tone);
            _keyIntensity = keyIntensity;
            _holdEnd = IntroSeconds + spec.HoldSeconds;

            _body = Child("Body", transform);
            CueIcon icon = spec.ResolvedIcon;
            bool hasTitle = spec.Title.Length > 0, hasSub = spec.Subtitle.Length > 0, hasIcon = icon != CueIcon.None;
            float iconY = hasTitle ? 0.185f : 0f;
            float iconScale = hasTitle ? IconSize : IconSize * 1.6f;

            // Título y subtítulo (capas: la 0 es la cara, las demás la extrusión hacia atrás y un poco hacia abajo a la
            // derecha, para que el volumen se lea aunque se mire de frente). Shader XRCollab/CueText: opaco y nítido en XR.
            Material textMat = Own(new Material(assets.Text));
            textMat.SetColor("_OutlineColor", Darken(_pal.Side, 0.55f));
            if (hasTitle)
            {
                Transform title = Child("Title", _body);
                for (int i = 0; i < TitleLayers; i++)
                {
                    TextMeshPro tmp = Text(title, $"L{i}", spec.Title, TitleSize, textMat, assets.Font, i * LayerStep, 4f);
                    tmp.transform.localPosition = new Vector3(i * ObliqueX, -i * ObliqueY, i * LayerStep);
                    if (i == 0) Gradient(tmp, _pal.FaceTop, _pal.FaceBottom);
                    else tmp.color = Darken(_pal.Side, Mathf.Lerp(1f, 0.45f, i / (float)(TitleLayers - 1)));
                    _texts.Add(new TextLayer { Tmp = tmp, Delay = 0.1f + i * 0.004f });
                }
                _titleWidth = _texts[0].Tmp.GetPreferredValues().x;
            }
            if (hasSub)
            {
                Transform sub = Child("Subtitle", _body);
                sub.localPosition = new Vector3(0f, hasTitle ? -0.11f : 0f, 0f);
                for (int i = 0; i < SubtitleLayers; i++)
                {
                    TextMeshPro tmp = Text(sub, $"S{i}", spec.Subtitle, SubtitleSize, textMat, assets.Font, i * LayerStep, 2f);
                    tmp.transform.localPosition = new Vector3(i * ObliqueX * 0.6f, -i * ObliqueY * 0.6f, i * LayerStep);
                    tmp.fontStyle = FontStyles.Bold;
                    if (i == 0) Gradient(tmp, Color.white, Color.Lerp(Color.white, _pal.FaceTop, 0.5f));
                    else tmp.color = Darken(_pal.Side, 0.5f);
                    _texts.Add(new TextLayer { Tmp = tmp, Delay = 0.32f + i * 0.004f });
                }
                _titleWidth = Mathf.Max(_titleWidth, _texts[_texts.Count - 1].Tmp.GetPreferredValues().x);
            }
            foreach (TextLayer t in _texts)
            {
                t.Tmp.ForceMeshUpdate();
                t.Source = t.Tmp.textInfo.CopyMeshInfoVertexData();
                foreach (Renderer r in t.Tmp.GetComponentsInChildren<Renderer>(true)) Setup(r);
            }
            HideLetters();

            // Sombra suave detrás (lectura sobre fondos claros) y brillo aditivo del color del tono.
            float w = Mathf.Max(_titleWidth, 0.35f) + 0.35f;
            float top = hasIcon ? iconY + iconScale * 0.6f : 0.1f, bottom = hasSub ? -0.16f : -0.08f;
            float h = top - bottom + 0.16f, cy = (top + bottom) * 0.5f;
            _backdrop = Quad("Backdrop", _body, assets.Alpha, assets.Shadow, new Color(0f, 0f, 0f, 0.42f), 2980, out _backdropMat);
            _backdrop.localPosition = new Vector3(0f, cy, 0.07f);
            _backdrop.localScale = new Vector3(w * 1.25f, h * 1.35f, 1f);
            _glowT = Quad("Glow", _body, assets.Additive, assets.Glow, _pal.Glow, 2990, out _glowMat);
            _glowT.localPosition = new Vector3(0f, cy, 0.06f);
            _glowT.localScale = new Vector3(w * 0.95f, h * 1.05f, 1f);

            if (hasIcon)
            {
                _icon = Child("Icon", _body);
                _icon.localPosition = new Vector3(0f, iconY, -0.01f);
                _icon.localScale = Vector3.zero;
                _iconSpin = Child("Spin", _icon);
                _iconSpin.localScale = Vector3.one * iconScale;
                BuildIcon(icon, assets);
                if (spec.Tone == CueTone.Great || spec.Tone == CueTone.Good)
                {
                    _rays = Quad("Rays", _body, assets.Additive, assets.Rays, _pal.Glow, 2990, out _raysMat);
                    _rays.localPosition = new Vector3(0f, iconY, 0.05f);
                    _rays.localScale = Vector3.one * iconScale * 4.2f;
                }
                _ring = Quad("Ring", _body, assets.Additive, assets.Ring, Color.Lerp(_pal.Glow, Color.white, 0.4f), 3010, out _ringMat);
                _ring.localPosition = new Vector3(0f, iconY, -0.02f);
                _ring.localScale = Vector3.zero;
            }

            // Partículas: chispas siempre; confeti en «great»; esquirlas que caen en «bad».
            Vector3 burstAt = new Vector3(0f, hasIcon ? iconY : 0f, -0.03f);
            _sparks = Particles("Sparks", assets.Additive, assets.Spark, burstAt, false);
            if (spec.Tone == CueTone.Great) _confetti = Particles("Confetti", assets.Alpha, null, burstAt + new Vector3(0f, 0.05f, 0f), true);
            else if (spec.Tone == CueTone.Bad) _confetti = Particles("Shards", assets.Alpha, null, burstAt, true);

            // Destello que recorre el texto (luz puntual solo de la capa de las señales).
            var glintGo = new GameObject("Glint") { layer = layer };
            glintGo.transform.SetParent(_body, false);
            _glint = glintGo.AddComponent<Light>();
            _glint.type = LightType.Point;
            _glint.range = 0.3f;
            _glint.intensity = 0f;
            _glint.color = Color.Lerp(Color.white, _pal.FaceTop, 0.3f);
            _glint.cullingMask = 1 << layer;
            _glint.renderMode = LightRenderMode.ForcePixel;
            _glint.shadows = LightShadows.None;

            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0.6f;
            _audio.rolloffMode = AudioRolloffMode.Linear;
            _audio.minDistance = 1f;
            _audio.maxDistance = 12f;
            _audio.volume = 0.9f;

            Tick(0f);
        }

        private void BuildIcon(CueIcon icon, CueAssets assets)
        {
            float depth = IconDepth, bevel = 0.035f;
            Color dark = new Color(0.07f, 0.07f, 0.08f);
            switch (icon)
            {
                case CueIcon.Check: Shape("Check", CueMeshes.Check(), depth, bevel, _pal.Icon, _pal.Metallic, _pal.Smoothness, 0f, assets); break;
                case CueIcon.Cross: Shape("Cross", CueMeshes.Cross(), depth, bevel, _pal.Icon, _pal.Metallic, _pal.Smoothness, 0f, assets); break;
                case CueIcon.Star: Shape("Star", CueMeshes.Star(), depth, bevel, _pal.Icon, _pal.Metallic, _pal.Smoothness, 0f, assets); break;
                case CueIcon.Bolt: Shape("Bolt", CueMeshes.Bolt(), depth, bevel, _pal.Icon, _pal.Metallic, _pal.Smoothness, 0f, assets); break;
                case CueIcon.Warning:
                    Shape("Triangle", CueMeshes.Triangle(), depth, bevel, _pal.Icon, _pal.Metallic, _pal.Smoothness, 0f, assets);
                    Shape("Bang", CueMeshes.Bar(new Vector2(0f, -0.03f), new Vector2(0f, 0.2f), 0.075f), depth * 0.5f, 0.015f, dark, 0.2f, 0.6f, -depth * 0.62f, assets);
                    Shape("Dot", CueMeshes.Circle(0.045f, 20, new Vector2(0f, -0.13f)), depth * 0.5f, 0.015f, dark, 0.2f, 0.6f, -depth * 0.62f, assets);
                    break;
                case CueIcon.Clock:
                    Shape("Rim", CueMeshes.Circle(0.5f), depth, bevel, _pal.Icon, _pal.Metallic, _pal.Smoothness, 0f, assets);
                    Shape("Dial", CueMeshes.Circle(0.4f), depth * 0.3f, 0.02f, new Color(0.96f, 0.94f, 0.9f), 0.05f, 0.55f, -depth * 0.55f, assets);
                    _clockHour = Shape("Hour", CueMeshes.Bar(new Vector2(0f, -0.03f), new Vector2(0f, 0.2f), 0.065f), depth * 0.25f, 0.012f, dark, 0.5f, 0.7f, -depth * 0.78f, assets);
                    _clockMinute = Shape("Minute", CueMeshes.Bar(new Vector2(0f, -0.04f), new Vector2(0f, 0.32f), 0.045f), depth * 0.25f, 0.01f, dark, 0.5f, 0.7f, -depth * 0.92f, assets);
                    Shape("Pin", CueMeshes.Circle(0.05f, 20), depth * 0.3f, 0.01f, _pal.Icon, 0.9f, 0.8f, -depth * 1.02f, assets);
                    break;
            }
        }

        private Transform Shape(string name, Vector2[] outline, float depth, float bevel, Color color, float metallic, float smoothness, float z, CueAssets assets)
        {
            var go = new GameObject(name) { layer = _layer };
            go.transform.SetParent(_iconSpin, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            go.AddComponent<MeshFilter>().sharedMesh = CueMeshes.Extrude(new[] { outline }, depth, bevel, name);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = assets.Metal;
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", color);   // en gamma: Unity lo pasa a lineal
            block.SetFloat("_Metallic", metallic);
            block.SetFloat("_Glossiness", smoothness);
            r.SetPropertyBlock(block);
            Setup(r);
            return go.transform;
        }

        private TextMeshPro Text(Transform parent, string name, string text, float size, Material mat, TMP_FontAsset font, float z, float spacing)
        {
            var go = new GameObject(name) { layer = _layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = font;
            tmp.fontSharedMaterial = mat;
            tmp.fontSize = size;
            tmp.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            tmp.characterSpacing = spacing;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.rectTransform.sizeDelta = new Vector2(4f, 0.6f);
            tmp.text = text;
            return tmp;
        }

        private static void Gradient(TextMeshPro tmp, Color top, Color bottom)
        {
            tmp.color = Color.white;
            tmp.enableVertexGradient = true;
            tmp.colorGradient = new VertexGradient(top, top, bottom, bottom);
        }

        private Transform Quad(string name, Transform parent, Material baseMat, Texture tex, Color color, int queue, out Material mat)
        {
            if (s_quad == null) s_quad = MakeQuad();
            var go = new GameObject(name) { layer = _layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = s_quad;
            var r = go.AddComponent<MeshRenderer>();
            mat = Own(new Material(baseMat));
            mat.mainTexture = tex;
            mat.color = color;
            mat.renderQueue = queue;
            r.sharedMaterial = mat;
            Setup(r);
            return go.transform;
        }

        private ParticleSystem Particles(string name, Material baseMat, Texture tex, Vector3 localPos, bool solid)
        {
            var go = new GameObject(name) { layer = _layer };
            go.transform.SetParent(_body, false);
            go.transform.localPosition = localPos;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            var shape = ps.shape;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = fade;
            var drag = ps.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.drag = solid ? 1.2f : 3.5f;
            drag.limit = 10f;

            if (!solid)
            {
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 1.0f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.35f, 1.25f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.02f);
                main.startColor = new ParticleSystem.MinMaxGradient(Color.Lerp(_pal.Glow, Color.white, 0.5f), _pal.Glow);
                main.gravityModifier = 0.05f;
                main.maxParticles = 120;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 80) });
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.04f;
                var size = ps.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0f));
            }
            else if (Spec.Tone == CueTone.Great)
            {
                main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.0f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.7f, 1.6f);
                main.startSize3D = true;
                main.startSizeX = new ParticleSystem.MinMaxCurve(0.008f, 0.013f);
                main.startSizeY = new ParticleSystem.MinMaxCurve(0.013f, 0.022f);
                main.startSizeZ = 1f;
                var confetti = new Gradient();
                confetti.SetKeys(new[]
                {
                    new GradientColorKey(new Color(1f, 0.82f, 0.25f), 0f), new GradientColorKey(new Color(0.3f, 0.85f, 1f), 0.25f),
                    new GradientColorKey(new Color(1f, 0.35f, 0.55f), 0.5f), new GradientColorKey(new Color(0.45f, 1f, 0.5f), 0.75f),
                    new GradientColorKey(Color.white, 1f),
                }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
                main.startColor = new ParticleSystem.MinMaxGradient(confetti) { mode = ParticleSystemGradientMode.RandomColor };
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.gravityModifier = 0.35f;
                main.maxParticles = 160;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 110) });
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 38f;
                shape.radius = 0.05f;
                shape.rotation = new Vector3(-90f, 0f, 0f);   // hacia arriba
                var rot = ps.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-8f, 8f);
            }
            else
            {
                // Esquirlas rojas oscuras que caen (bad).
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.2f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.7f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.007f, 0.016f);
                main.startColor = new ParticleSystem.MinMaxGradient(Darken(_pal.Icon, 0.55f), _pal.Side);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.gravityModifier = 0.9f;
                main.maxParticles = 60;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 40) });
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.06f;
                var rot = ps.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-6f, 6f);
            }

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            Material mat = Own(new Material(baseMat));
            if (tex != null) mat.mainTexture = tex;
            mat.renderQueue = 3010;
            renderer.sharedMaterial = mat;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            Setup(renderer);
            _particles.Add(ps);
            return ps;
        }

        // ---------------- animación ----------------

        private void Update() => Tick(Time.deltaTime);

        /// <summary>Avanza la animación <paramref name="dt"/> segundos.</summary>
        public void Tick(float dt)
        {
            if (Done) return;
            _t += dt;
            float t = _t;
            if (!Leaving && t >= _holdEnd) _outroStart = t;
            float outro = Leaving ? Mathf.Clamp01((t - _outroStart) / OutroSeconds) : 0f;

            if (!_whooshPlayed && Application.isPlaying) { _whooshPlayed = true; _audio.PlayOneShot(CueSynth.Whoosh(), 0.45f); }
            if (!_tonePlayed && t >= 0.16f && Application.isPlaying) { _tonePlayed = true; _audio.PlayOneShot(CueSynth.For(Spec.Tone), 1f); }
            if (!_burstFired && t >= 0.18f)
            {
                _burstFired = true;
                foreach (ParticleSystem ps in _particles)
                {
                    if (ManualParticles) ps.Simulate(0f, true, true, false);
                    else ps.Play(true);
                }
            }
            else if (ManualParticles && _burstFired && dt > 0f) foreach (ParticleSystem ps in _particles) ps.Simulate(dt, true, false, false);

            // Cuerpo: giro de entrada, sacudida corta (bad), subida al salir. Entre medio queda quieto en la sala
            // (José, 2026-10-13: nada de flotar ni mecerse; tiene que verse fijo en su lugar).
            float intro = Mathf.Clamp01(t / IntroSeconds);
            float swing = (1f - EaseOutCubic(intro)) * 28f;
            float shake = Spec.Tone == CueTone.Bad && t > 0.25f ? 0.014f * Mathf.Sin(t * 58f) * Mathf.Exp(-(t - 0.25f) * 7f) : 0f;
            _body.localPosition = new Vector3(shake, EaseInCubic(outro) * 0.05f, 0f);
            _body.localRotation = Quaternion.Euler(0f, swing, 0f);

            // Letras: cascada al entrar, al revés al salir.
            if (Leaving) AnimateLetters(outro, true);
            else if (!_lettersSettled) AnimateLetters(0f, false);

            // Ícono: aparece con rebote girando; extras por tono.
            if (_icon != null)
            {
                float pi = Mathf.Clamp01(t / 0.5f);
                float s = EaseOutBack(pi) * (1f - EaseInBack(outro));
                if (Spec.Tone == CueTone.Warn) s *= 1f + 0.07f * Mathf.Sin(t * 9f) * EaseOutCubic(intro);
                _icon.localScale = Vector3.one * Mathf.Max(0f, s);
                float spin = (1f - EaseOutCubic(Mathf.Clamp01(t / 0.7f))) * 540f;
                float idle = Spec.Tone == CueTone.Great ? Mathf.Sin(t * 1.6f) * 14f : 0f;
                float tilt = Spec.Tone == CueTone.Bad && t > 0.25f ? 12f * Mathf.Sin(t * 40f) * Mathf.Exp(-(t - 0.25f) * 6f) : 0f;
                _iconSpin.localRotation = Quaternion.Euler(0f, spin + idle, tilt);
                if (_clockMinute != null)
                {
                    float speed = Mathf.Lerp(900f, 120f, Mathf.Clamp01(t / 1.2f));
                    _clockMinute.localRotation = Quaternion.Euler(0f, 0f, -t * speed);
                    _clockHour.localRotation = Quaternion.Euler(0f, 0f, -t * speed / 12f - 60f);
                }
            }

            // Brillos: el aditivo entra fuerte y queda suave; rayos girando; onda expansiva al aparecer.
            float glowIn = Mathf.Clamp01(t / 0.3f);
            float glow = Mathf.Lerp(1.6f, 0.75f, EaseOutCubic(Mathf.Clamp01((t - 0.3f) / 0.8f))) * glowIn * (1f - outro);
            if (Spec.Tone == CueTone.Warn) glow *= 0.8f + 0.25f * Mathf.Sin(t * 9f);
            _glowMat.SetFloat("_Intensity", glow * 0.35f);
            _backdropMat.SetFloat("_Intensity", 1f);
            _backdropMat.color = new Color(0f, 0f, 0f, 0.42f * EaseOutCubic(Mathf.Clamp01(t / 0.35f)) * (1f - outro));
            if (_rays != null)
            {
                _rays.localRotation = Quaternion.Euler(0f, 0f, -t * 14f);
                float ri = EaseOutCubic(Mathf.Clamp01((t - 0.1f) / 0.5f)) * (1f - outro) * (Spec.Tone == CueTone.Great ? 0.9f : 0.45f);
                _raysMat.SetFloat("_Intensity", ri * (0.85f + 0.15f * Mathf.Sin(t * 3f)));
            }
            if (_ring != null)
            {
                float pr = Mathf.Clamp01((t - 0.12f) / 0.55f);
                _ring.localScale = Vector3.one * Mathf.Lerp(0.05f, 0.75f, EaseOutCubic(pr));
                _ringMat.SetFloat("_Intensity", pr <= 0f || pr >= 1f ? 0f : 1.6f * (1f - pr));
            }

            // Destello: una luz puntual cruza el título de izquierda a derecha.
            float g = Mathf.Clamp01((t - 0.7f) / 0.75f);
            float half = Mathf.Max(0.25f, _titleWidth * 0.6f);
            _glint.transform.localPosition = new Vector3(Mathf.Lerp(-half, half, EaseInOutSine(g)), 0.03f, -0.07f);
            _glint.intensity = (g <= 0f || g >= 1f) ? 0f : Mathf.Sin(g * Mathf.PI) * 2.2f * Mathf.Max(0.6f, _keyIntensity);

            if (outro >= 1f) Finish();
        }

        /// <summary>Sale antes de tiempo (otra señal la reemplaza o «cue clear»).</summary>
        public void Dismiss()
        {
            if (Leaving) return;
            _outroStart = _t;
        }

        private void Finish()
        {
            Done = true;
            if (Application.isPlaying) Destroy(gameObject);
        }

        private void HideLetters()
        {
            foreach (TextLayer layer in _texts) ApplyLetters(layer, _ => 0f, 0f);
        }

        private void AnimateLetters(float outro, bool leaving)
        {
            bool allDone = true;
            foreach (TextLayer layer in _texts)
            {
                TextLayer l = layer;
                if (!leaving)
                {
                    ApplyLetters(l, i =>
                    {
                        float p = Mathf.Clamp01((_t - l.Delay - i * LetterStagger) / LetterSeconds);
                        if (p < 1f) allDone = false;
                        return EaseOutBack(p);
                    }, 1f);
                }
                else
                {
                    // Al revés: la última letra se va primero; todas dentro de OutroSeconds.
                    int count = Mathf.Max(1, l.Tmp.textInfo.characterCount - 1);
                    const float spread = 0.15f;
                    ApplyLetters(l, i =>
                    {
                        float delay = (count - i) / (float)count * spread;
                        float p = Mathf.Clamp01((outro * OutroSeconds - delay) / (OutroSeconds - spread));
                        return 1f - EaseInBack(p);
                    }, -1f);
                }
            }
            if (!leaving && allDone) _lettersSettled = true;
        }

        // Escala cada letra desde su centro (y la sube o baja un poco mientras escala).
        private void ApplyLetters(TextLayer layer, System.Func<int, float> scaleOf, float riseSign)
        {
            TMP_TextInfo info = layer.Tmp.textInfo;
            if (layer.Source == null) return;
            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo c = info.characterInfo[i];
                if (!c.isVisible) continue;
                int m = c.materialReferenceIndex, v = c.vertexIndex;
                if (m >= layer.Source.Length || m >= info.meshInfo.Length) continue;
                Vector3[] src = layer.Source[m].vertices, dst = info.meshInfo[m].vertices;
                if (src == null || dst == null || v + 3 >= src.Length) continue;
                float s = scaleOf(i);
                Vector3 center = (src[v] + src[v + 2]) * 0.5f;
                Vector3 rise = new Vector3(0f, (1f - Mathf.Clamp01(s)) * 0.06f * riseSign, 0f);
                for (int k = 0; k < 4; k++) dst[v + k] = center + (src[v + k] - center) * s - rise;
            }
            for (int m = 0; m < info.meshInfo.Length; m++)
            {
                if (info.meshInfo[m].mesh == null) continue;
                info.meshInfo[m].mesh.vertices = info.meshInfo[m].vertices;
                layer.Tmp.UpdateGeometry(info.meshInfo[m].mesh, m);
            }
        }

        // ---------------- utilidades ----------------

        private Transform Child(string name, Transform parent)
        {
            var go = new GameObject(name) { layer = _layer };
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private Material Own(Material m)
        {
            m.hideFlags = HideFlags.DontSave;
            _ownedMaterials.Add(m);
            return m;
        }

        private void Setup(Renderer r)
        {
            r.gameObject.layer = _layer;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        private static Color Darken(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

        private static Mesh MakeQuad()
        {
            var m = new Mesh { name = "CueQuad", hideFlags = HideFlags.DontSave };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private void OnDestroy()
        {
            foreach (Material m in _ownedMaterials) if (m != null) DestroyImmediate(m);
            _ownedMaterials.Clear();
        }

        private static float EaseOutCubic(float x) => 1f - Mathf.Pow(1f - x, 3f);
        private static float EaseInCubic(float x) => x * x * x;
        private static float EaseInOutSine(float x) => -(Mathf.Cos(Mathf.PI * x) - 1f) * 0.5f;

        private static float EaseOutBack(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            return x <= 0f ? 0f : 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f);
        }

        private static float EaseInBack(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            return c3 * x * x * x - c1 * x * x;
        }
    }
}
