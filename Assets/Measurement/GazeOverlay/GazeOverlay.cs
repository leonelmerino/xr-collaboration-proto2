using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using XRCollab.Measurement.Mirroring;

/// <summary>
/// Mirada en vivo en la ventana del PC: un anillo donde mira el participante (+ una estela corta que se desvanece) y
/// una línea de estado chica arriba a la izquierda (% de muestras de mirada válidas en los últimos 5 s, manos
/// seguidas). Como se dibuja en la ventana, queda en el video de SessionRecorder.
///
/// Cómo se arma la ventana del PC y por qué se dibuja así:
///   - Modo medición: la ventana NO es el espejo XR de Unity (ese queda negro: el passthrough se compone dentro del
///     visor). Es <see cref="HeadsetMirror"/>: la pantalla del visor por scrcpy (ambos ojos lado a lado, o un ojo)
///     en un Canvas Screen Space - Overlay. Nada que renderice la cámara XR aparece ahí sin aparecer también en el
///     visor, así que no sirve una capa «solo escritorio»: el punto se dibuja en 2D con OnGUI (encima del Canvas,
///     nunca en el visor), proyectando el punto de mirada con la proyección de cada ojo de la cámara XR sobre la
///     mitad correspondiente de la imagen.
///   - Modo VR (-vr) o sin espejo: la ventana es el espejo XR; se proyecta con el ojo izquierdo sobre toda la ventana.
///
/// En el visor está APAGADO por defecto (sesgaría al participante). Shift+G (o -gaze-hmd on) lo enciende para
/// pruebas: un punto magenta en el mundo en la capa <see cref="HmdLayer"/>, que solo se agrega a la máscara de la
/// cámara mientras está encendido. Como el espejo es la pantalla del visor, ese punto también se ve en el espejo:
/// sirve para verificar que el anillo 2D cae en el mismo lugar (y para medir el retraso del espejo).
///
/// Punto de mirada: rayo combinado de <see cref="ViveEyeTrackingProvider"/> (espacio de tracking → mundo con el padre
/// de la cámara, igual que las manos en BodyTrackingSessionLogger) contra los colliders de la escena; si no choca
/// con nada (en modo medición casi nunca: el entorno virtual está apagado y el passthrough no tiene geometría), a
/// 1,5 m. Para el anillo 2D la distancia casi no importa (se proyecta por el mismo rayo); solo cambia el paralaje
/// entre ojos.
///
/// Teclas: G muestra/oculta todo en el PC; Shift+G enciende/apaga el punto en el visor.
/// Argumentos: -gaze-overlay on|off (on), -gaze-hmd on|off (off), -gaze-distance M (1.5),
/// -gaze-overlay-delay-ms N (0; retrasa el anillo para que coincida con el espejo, que llega ~100–200 ms tarde),
/// -gaze-mirror-scale S (1) y -gaze-mirror-offset X,Y (0,0): ajuste fino del anillo sobre la imagen del espejo.
/// No hay mirada de los otros participantes: la mirada no viaja por la red (solo se registra en cada PC).
/// Solo Windows (el build del visor no tiene ventana de PC).
/// </summary>
[DisallowMultipleComponent]
public sealed class GazeOverlay : MonoBehaviour
{
    public const int HmdLayer = 30;   // capa sin nombre (la 31 la usa RecordingIndicator en el build del visor)

    private const string LogPrefix = "[GazeOverlay] ";
    private const float DefaultDistance = 1.5f;
    private const float MaxRayDistance = 10f;
    private const float RayStartOffset = 0.1f;    // no chocar con colliders de la propia cabeza
    private const float TrailSeconds = 0.5f;
    private const float StaleSeconds = 0.15f;
    private const float ValidWindowSeconds = 5f;
    private const float LookupSeconds = 2f;
    private const float HmdDotDegrees = 0.8f;
    private const float ReferenceHeight = 1080f;

    private static readonly Color ReticleColor = new Color(0.35f, 1f, 0.25f, 0.95f);
    private static readonly Color OutlineColor = new Color(0f, 0f, 0f, 0.6f);
    private static readonly Color HmdDotColor = new Color(1f, 0.2f, 0.9f, 0.85f);

    private struct Sample
    {
        public float Time;
        public bool Valid;
        public bool LeftOk;
        public bool RightOk;
        public Vector2 Left;
        public Vector2 Right;
    }

    private struct ValidMark
    {
        public float Time;
        public bool Valid;
    }

    private bool _pcVisible = true;
    private bool _hmdVisible;
    private float _distance = DefaultDistance;
    private float _delaySeconds;
    private float _uvScale = 1f;
    private Vector2 _uvOffset = Vector2.zero;

    private ViveEyeTrackingProvider _eye;
    private HeadsetMirror _mirror;
    private XRHandSubsystem _hands;
    private readonly List<XRHandSubsystem> _handList = new List<XRHandSubsystem>();
    private float _nextLookup;

    private readonly Queue<Sample> _samples = new Queue<Sample>();
    private readonly Queue<ValidMark> _validMarks = new Queue<ValidMark>();
    private int _validCount;
    private string _hitName;

    private GameObject _hmdDot;
    private Camera _maskedCamera;

    private Texture2D _ring;
    private Texture2D _disc;
    private GUIStyle _style;
    private readonly GUIContent _content = new GUIContent();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Init()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (FindObjectOfType<GazeOverlay>() != null) return;
        var go = new GameObject("[GazeOverlay]");
        DontDestroyOnLoad(go);
        go.AddComponent<GazeOverlay>();
#endif
    }

    private void Awake()
    {
        ApplyCommandLine(Environment.GetCommandLineArgs());
        _ring = MakeCircle(64, 0.62f);
        _disc = MakeCircle(32, 0f);
        Debug.Log($"{LogPrefix}PC {(_pcVisible ? "visible" : "oculta")} (G) · visor {(_hmdVisible ? "ENCENDIDO" : "apagado")} (Shift+G) · " +
                  $"distancia {_distance:0.##} m · retraso {_delaySeconds * 1000f:0} ms · ajuste espejo x{_uvScale:0.###} {_uvOffset}");
    }

    private void ApplyCommandLine(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "-gaze-overlay": if (TryOnOff(value, ref _pcVisible)) i++; break;
                case "-gaze-hmd": if (TryOnOff(value, ref _hmdVisible)) i++; break;
                case "-gaze-distance":
                    if (TryFloat(value, out float d)) { _distance = Mathf.Clamp(d, 0.2f, MaxRayDistance); i++; }
                    break;
                case "-gaze-overlay-delay-ms":
                    if (TryFloat(value, out float ms)) { _delaySeconds = Mathf.Clamp(ms, 0f, 1000f) / 1000f; i++; }
                    break;
                case "-gaze-mirror-scale":
                    if (TryFloat(value, out float s) && s > 0.1f && s < 10f) { _uvScale = s; i++; }
                    break;
                case "-gaze-mirror-offset":
                    if (TryVector2(value, out Vector2 o)) { _uvOffset = o; i++; }
                    break;
            }
        }
    }

    // ---------------- cada cuadro ----------------

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.G)) return;
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (shift)
        {
            _hmdVisible = !_hmdVisible;
            Debug.Log($"{LogPrefix}punto de mirada EN EL VISOR {(_hmdVisible ? "ENCENDIDO" : "apagado")} a las {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} (Shift+G)");
        }
        else
        {
            _pcVisible = !_pcVisible;
            Debug.Log($"{LogPrefix}mirada en la ventana del PC {(_pcVisible ? "visible" : "oculta")} (G)");
        }
    }

    // LateUpdate: el proveedor de mirada y la pose de la cabeza ya se actualizaron este cuadro.
    private void LateUpdate()
    {
        float now = Time.unscaledTime;
        Lookup(now);
        Camera cam = Camera.main;

        bool valid = _eye != null && _eye.isActiveAndEnabled && _eye.CombinedGazeValid && _eye.CombinedDirection.sqrMagnitude > 0.25f;
        TrackValidity(now, valid);

        var sample = new Sample { Time = now };
        Vector3 point = default;
        if (valid && cam != null)
        {
            point = GazePoint(cam, out _hitName);
            sample.Valid = true;
            sample.LeftOk = Project(cam, Camera.StereoscopicEye.Left, point, out sample.Left);
            sample.RightOk = Project(cam, Camera.StereoscopicEye.Right, point, out sample.Right);
        }
        else
        {
            _hitName = null;
        }
        _samples.Enqueue(sample);
        float keep = _delaySeconds + TrailSeconds + 0.5f;
        while (_samples.Count > 0 && now - _samples.Peek().Time > keep) _samples.Dequeue();

        UpdateHmdDot(cam, point, sample.Valid);
        UpdateCullingMask(cam);
    }

    private void Lookup(float now)
    {
        if (now < _nextLookup) return;
        _nextLookup = now + LookupSeconds;
        if (_eye == null) _eye = FindObjectOfType<ViveEyeTrackingProvider>();
        if (_mirror == null) _mirror = FindObjectOfType<HeadsetMirror>();
        if (_hands == null || !_hands.running)
        {
            _handList.Clear();
            SubsystemManager.GetSubsystems(_handList);
            _hands = null;
            foreach (XRHandSubsystem h in _handList)
            {
                if (_hands == null || h.running) _hands = h;
                if (h.running) break;
            }
        }
    }

    private void TrackValidity(float now, bool valid)
    {
        _validMarks.Enqueue(new ValidMark { Time = now, Valid = valid });
        if (valid) _validCount++;
        while (_validMarks.Count > 0 && now - _validMarks.Peek().Time > ValidWindowSeconds)
            if (_validMarks.Dequeue().Valid) _validCount--;
    }

    private Vector3 GazePoint(Camera cam, out string hitName)
    {
        // Las poses de OpenXR (mirada, cabeza) están en el espacio de tracking; el padre de la cámara lo pasa a mundo.
        Transform space = cam.transform.parent;
        Vector3 origin = _eye.CombinedOrigin;
        Vector3 dir = _eye.CombinedDirection.normalized;
        if (space != null)
        {
            origin = space.TransformPoint(origin);
            dir = space.TransformDirection(dir).normalized;
        }
        int mask = Physics.DefaultRaycastLayers & ~(1 << HmdLayer);
        if (Physics.Raycast(origin + dir * RayStartOffset, dir, out RaycastHit hit, MaxRayDistance, mask, QueryTriggerInteraction.Ignore))
        {
            hitName = hit.collider.name;
            return hit.point;
        }
        hitName = null;
        return origin + dir * _distance;
    }

    /// <summary>Punto del mundo → coordenadas 0..1 de la imagen de un ojo (origen abajo a la izquierda).</summary>
    private static bool Project(Camera cam, Camera.StereoscopicEye eye, Vector3 world, out Vector2 uv)
    {
        Matrix4x4 view = cam.worldToCameraMatrix;
        Matrix4x4 proj = cam.projectionMatrix;
        if (cam.stereoEnabled)
        {
            Matrix4x4 stereoProj = cam.GetStereoProjectionMatrix(eye);
            if (stereoProj.m00 != 0f)
            {
                view = cam.GetStereoViewMatrix(eye);
                proj = stereoProj;
            }
        }
        Vector4 clip = proj * (view * new Vector4(world.x, world.y, world.z, 1f));
        uv = default;
        if (clip.w <= 1e-4f) return false;   // detrás del ojo
        uv = new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
        return !float.IsNaN(uv.x) && !float.IsNaN(uv.y);
    }

    // ---------------- en el visor (solo si se pidió) ----------------

    private void UpdateHmdDot(Camera cam, Vector3 point, bool valid)
    {
        bool show = _hmdVisible && valid && cam != null;
        if (!show)
        {
            if (_hmdDot != null && _hmdDot.activeSelf) _hmdDot.SetActive(false);
            return;
        }
        if (_hmdDot == null) _hmdDot = CreateHmdDot();
        Vector3 toPoint = point - cam.transform.position;
        float distance = toPoint.magnitude;
        if (distance < 0.05f)
        {
            _hmdDot.SetActive(false);
            return;
        }
        Vector3 forward = toPoint / distance;
        Transform t = _hmdDot.transform;
        t.position = point - forward * 0.01f;   // apenas delante de la superficie
        t.rotation = Quaternion.LookRotation(forward, cam.transform.up);
        float size = 2f * distance * Mathf.Tan(HmdDotDegrees * 0.5f * Mathf.Deg2Rad);
        t.localScale = Vector3.one * (size / 100f);   // el canvas mide 100 unidades
        if (!_hmdDot.activeSelf) _hmdDot.SetActive(true);
    }

    // MeasurementMode deja la máscara de la cámara en 0 (nada virtual): la capa del punto se agrega solo mientras
    // el punto está encendido, y se saca al apagarlo.
    private void UpdateCullingMask(Camera cam)
    {
        int bit = 1 << HmdLayer;
        if (_maskedCamera != null && (_maskedCamera != cam || !_hmdVisible))
        {
            _maskedCamera.cullingMask &= ~bit;
            _maskedCamera = null;
        }
        if (_hmdVisible && cam != null && (cam.cullingMask & bit) == 0)
        {
            cam.cullingMask |= bit;
            _maskedCamera = cam;
        }
    }

    private GameObject CreateHmdDot()
    {
        var canvasGo = new GameObject("GazeHmdDot", typeof(Canvas));
        canvasGo.layer = HmdLayer;
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 1000;
        ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(100f, 100f);

        var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(Image));
        dotGo.layer = HmdLayer;
        dotGo.transform.SetParent(canvasGo.transform, false);
        var rect = (RectTransform)dotGo.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(100f, 100f);
        var image = dotGo.GetComponent<Image>();
        image.sprite = Sprite.Create(_disc, new Rect(0f, 0f, _disc.width, _disc.height), new Vector2(0.5f, 0.5f), 100f);
        image.color = HmdDotColor;
        image.raycastTarget = false;
        return canvasGo;
    }

    // ---------------- en la ventana del PC ----------------

    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || !_pcVisible) return;
        float scale = Mathf.Max(0.5f, Screen.height / ReferenceHeight);
        DrawStatus(scale);

        // Dónde dibujar: sobre la imagen del espejo (cada ojo en su mitad) o, sin espejo, sobre toda la ventana.
        Rect left, right = default;
        bool hasRight = false;
        if (_mirror != null && _mirror.isActiveAndEnabled && _mirror.TryGetImageScreenRect(out Rect image, out MirrorLayout layout))
        {
            if (layout == MirrorLayout.BothEyes)
            {
                left = new Rect(image.x, image.y, image.width * 0.5f, image.height);
                right = new Rect(image.x + image.width * 0.5f, image.y, image.width * 0.5f, image.height);
                hasRight = true;
            }
            else
            {
                left = image;
            }
        }
        else
        {
            left = new Rect(0f, 0f, Screen.width, Screen.height);
        }

        float showTime = Time.unscaledTime - _delaySeconds;
        Sample current = default;
        bool haveCurrent = false;
        Color previous = GUI.color;
        foreach (Sample s in _samples)
        {
            if (s.Time > showTime) break;
            current = s;
            haveCurrent = true;
            float age = showTime - s.Time;
            if (!s.Valid || age > TrailSeconds) continue;
            var trail = new Color(ReticleColor.r, ReticleColor.g, ReticleColor.b, 0.5f * (1f - age / TrailSeconds));
            float size = 7f * scale;
            if (s.LeftOk) DrawAt(left, s.Left, _disc, size, trail);
            if (hasRight && s.RightOk) DrawAt(right, s.Right, _disc, size, trail);
        }

        if (haveCurrent && current.Valid && showTime - current.Time <= StaleSeconds)
        {
            float ring = 30f * scale;
            if (current.LeftOk) DrawReticle(left, current.Left, ring);
            if (hasRight && current.RightOk) DrawReticle(right, current.Right, ring);
        }
        GUI.color = previous;
    }

    private void DrawReticle(Rect eyeRect, Vector2 uv, float size)
    {
        DrawAt(eyeRect, uv, _ring, size * 1.18f, OutlineColor);
        DrawAt(eyeRect, uv, _ring, size, ReticleColor);
        DrawAt(eyeRect, uv, _disc, size * 0.2f, ReticleColor);
    }

    // uv de un ojo → píxel de GUI (origen arriba a la izquierda) dentro del rectángulo de ese ojo.
    private void DrawAt(Rect eyeRect, Vector2 uv, Texture2D texture, float size, Color color)
    {
        Vector2 u = (uv - new Vector2(0.5f, 0.5f)) * _uvScale + new Vector2(0.5f, 0.5f) + _uvOffset;
        if (u.x < -0.02f || u.x > 1.02f || u.y < -0.02f || u.y > 1.02f) return;   // fuera de la vista de ese ojo
        float x = eyeRect.x + u.x * eyeRect.width;
        float y = Screen.height - (eyeRect.y + u.y * eyeRect.height);
        GUI.color = color;
        GUI.DrawTexture(new Rect(x - size * 0.5f, y - size * 0.5f, size, size), texture);
    }

    private void DrawStatus(float scale)
    {
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.MiddleLeft, wordWrap = false };
            _style.normal.textColor = new Color(1f, 1f, 1f, 0.92f);
        }
        _style.fontSize = Mathf.RoundToInt(14f * scale);

        string gaze;
        if (_eye == null) gaze = "<color=#ffb040>sin eye tracking</color>";
        else
        {
            int total = _validMarks.Count;
            float pct = total > 0 ? 100f * _validCount / total : 0f;
            string color = pct >= 80f ? "#7cfc00" : pct >= 50f ? "#ffd040" : "#ff6060";
            gaze = $"<color={color}>{pct:0}%</color> válida (5 s)";
            if (!string.IsNullOrEmpty(_hitName)) gaze += $" · mira {_hitName}";
        }
        string hands = _hands == null || !_hands.running
            ? "manos: sin seguimiento"
            : $"mano I {YesNo(_hands.leftHand.isTracked)} D {YesNo(_hands.rightHand.isTracked)}";
        string hmd = _hmdVisible ? "<color=#ff60ff>EN EL VISOR</color>" : "visor off";
        _content.text = $"Mirada {gaze} · {hands} · [G ocultar · Shift+G {hmd}]";

        Vector2 size = _style.CalcSize(_content);
        var rect = new Rect(10f, 34f * scale, size.x + 16f, size.y + 6f);
        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
        GUI.Label(new Rect(rect.x + 8f, rect.y + 3f, size.x, size.y), _content, _style);
    }

    private static string YesNo(bool tracked) => tracked ? "<color=#7cfc00>sí</color>" : "<color=#ff6060>no</color>";

    private void OnDestroy()
    {
        if (_maskedCamera != null) _maskedCamera.cullingMask &= ~(1 << HmdLayer);
        if (_ring != null) Destroy(_ring);
        if (_disc != null) Destroy(_disc);
    }

    // ---------------- utilidades ----------------

    /// <summary>Círculo blanco con borde suave; <paramref name="innerFraction"/> &gt; 0 lo deja como anillo.</summary>
    private static Texture2D MakeCircle(int size, float innerFraction)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "GazeOverlay circle",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        float r = size * 0.5f;
        float inner = r * innerFraction;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - r, dy = y + 0.5f - r;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(r - d);
                if (innerFraction > 0f) a = Mathf.Min(a, Mathf.Clamp01(d - inner + 0.5f));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static bool TryOnOff(string value, ref bool target)
    {
        switch (value?.ToLowerInvariant())
        {
            case "on": case "1": case "true": target = true; return true;
            case "off": case "0": case "false": target = false; return true;
            default: return false;
        }
    }

    private static bool TryFloat(string value, out float result) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static bool TryVector2(string value, out Vector2 result)
    {
        result = Vector2.zero;
        if (string.IsNullOrEmpty(value)) return false;
        string[] parts = value.Split(',');
        if (parts.Length != 2 || !TryFloat(parts[0], out float x) || !TryFloat(parts[1], out float y)) return false;
        result = new Vector2(x, y);
        return true;
    }
}
