using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Overlay OnGUI para tunear la fisica de Jenga en runtime sin sacarse el visor.
///
/// Hotkeys (configurables en Inspector):
/// - F6: toggle visibilidad.
/// - 1..9: cargar preset N directamente.
/// - [ / ]: preset anterior / siguiente (alternativa).
/// - Tab / Shift+Tab: seleccionar siguiente / anterior parametro tuneable.
/// - Up / Down (o + / -): aumentar / disminuir el parametro seleccionado por su step.
///                        Shift = step x10 (cambio grueso). Alt = step x0.1 (fino).
/// - R: re-aplicar config actual (util si cargaste un preset y quedaron bloques con valores stale).
/// - B: rebuild de la torre (necesario para que tomen efecto cambios de spacing/dropHeight).
/// - Esc: cerrar HUD (toggle off).
///
/// El HUD modifica directamente JengaPhysicsRuntime.LiveConfig y llama ApplyActive() en cada
/// cambio. Los presets son read-only (no se mutan los assets).
/// </summary>
public class JengaPhysicsTunerHUD : MonoBehaviour
{
    [Header("Desktop OnGUI overlay")]
    [SerializeField] private bool showInEditor = true;
    [SerializeField] private bool showInBuild = true;
    [SerializeField] private int fontSize = 13;
    [SerializeField] private Vector2 margin = new Vector2(12f, 12f);
    [SerializeField] private float fixedWidth = 460f;

    [Header("VR world-space panel")]
    [Tooltip("Si esta activo, ademas del overlay desktop se muestra un Canvas world-space flotando enfrente del visor. Para VR sin esto el HUD no se ve.")]
    [SerializeField] private bool showInVR = true;
    [Tooltip("Distancia (m) del panel al centro del HMD cuando se ancla.")]
    [SerializeField] private float vrPanelDistance = 0.6f;
    [Tooltip("Offset vertical relativo al HMD (m). Negativo = abajo de la linea de vision.")]
    [SerializeField] private float vrPanelVerticalOffset = -0.08f;
    [Tooltip("Offset lateral relativo al HMD (m). Positivo = derecha, negativo = izquierda.")]
    [SerializeField] private float vrPanelLateralOffset = 0f;
    [Tooltip("Ancho del panel en metros (alto se ajusta automaticamente por content size fitter).")]
    [SerializeField] private float vrPanelWidthM = 0.45f;
    [Tooltip("Si esta activo, el panel sigue suavemente la rotacion del HMD. Si esta off se ancla cuando se toggle F6 y queda en world space hasta el siguiente toggle.")]
    [SerializeField] private bool vrPanelFollowHead = false;

    [Header("Keys")]
    [SerializeField] private KeyCode toggleKey = KeyCode.F6;
    [SerializeField] private KeyCode rebuildKey = KeyCode.B;
    [SerializeField] private KeyCode reapplyKey = KeyCode.R;
    [SerializeField] private KeyCode repinKey = KeyCode.P;
    [SerializeField] private KeyCode nextPresetKey = KeyCode.RightBracket;  // ]
    [SerializeField] private KeyCode prevPresetKey = KeyCode.LeftBracket;   // [

    private bool visible = true;
    private GUIStyle style;
    private int selectedParam = 0;
    private string _cachedText;

    // VR canvas state.
    private Canvas _vrCanvas;
    private TextMeshProUGUI _vrText;
    private RectTransform _vrCanvasRect;
    private Transform _hmdTransform;

    // Definicion de los parametros editables.
    // Cada Tunable describe: como leer y escribir el valor sobre el liveConfig,
    // el step base, los limites, y si requiere rebuild de la torre para aplicarse.
    private class Tunable
    {
        public string label;
        public System.Func<JengaPhysicsConfig, string> read;
        public System.Action<JengaPhysicsConfig, int> step;  // dir = +1 / -1; magnitud = step * multiplicador
        public bool requiresRebuild;
    }

    private List<Tunable> _tunables;

    private void Awake()
    {
        BuildTunables();
    }

    private void BuildTunables()
    {
        _tunables = new List<Tunable>
        {
            // PhysicMaterial
            FloatT("Static Friction",   c => c.staticFriction,   (c, v) => c.staticFriction = v, 0.05f, 0f, 2f),
            FloatT("Dynamic Friction",  c => c.dynamicFriction,  (c, v) => c.dynamicFriction = v, 0.05f, 0f, 2f),
            FloatT("Bounciness",        c => c.bounciness,       (c, v) => c.bounciness = v, 0.05f, 0f, 1f),
            EnumT_FrictionCombine(),
            EnumT_BounceCombine(),

            // Rigidbody
            FloatT("Mass (kg)",         c => c.mass,             (c, v) => c.mass = v, 0.01f, 0.005f, 1f),
            FloatT("Linear Drag",       c => c.drag,             (c, v) => c.drag = v, 0.05f, 0f, 2f),
            FloatT("Angular Drag",      c => c.angularDrag,      (c, v) => c.angularDrag = v, 0.05f, 0f, 2f),
            EnumT_CollisionDetection(),
            EnumT_Interpolation(),

            // Project Physics
            IntT("Solver Iterations",   c => c.solverIterations, (c, v) => c.solverIterations = v, 1, 1, 30),
            IntT("Solver Vel.Iter",     c => c.solverVelocityIterations, (c, v) => c.solverVelocityIterations = v, 1, 1, 20),
            FloatT("Contact Offset",    c => c.contactOffset,    (c, v) => c.contactOffset = v, 0.001f, 0.001f, 0.02f),
            FloatT("Bounce Threshold",  c => c.bounceThreshold,  (c, v) => c.bounceThreshold = v, 0.5f, 0.5f, 10f),
            FloatT("Sleep Threshold",   c => c.sleepThreshold,   (c, v) => c.sleepThreshold = v, 0.005f, 0.001f, 0.05f),

            // Grab feel
            FloatT("Grab Pos Lerp",     c => c.grabPositionLerp, (c, v) => c.grabPositionLerp = v, 0.05f, 0.05f, 1f),
            BoolT("Preserve Vel @Release", c => c.preserveVelocityOnRelease, (c, v) => c.preserveVelocityOnRelease = v),
            IntT("Release Vel Samples", c => c.releaseVelocitySamples, (c, v) => c.releaseVelocitySamples = v, 1, 1, 6),
            FloatT("Release Vel Max",   c => c.releaseVelocityMax, (c, v) => c.releaseVelocityMax = v, 0.5f, 0.5f, 10f),

            // Tower
            FloatT("H-Spacing",         c => c.horizontalSpacing, (c, v) => c.horizontalSpacing = v, 0.0001f, 0f, 0.01f, true),
            FloatT("V-Spacing",         c => c.verticalSpacing,   (c, v) => c.verticalSpacing = v, 0.0001f, 0f, 0.01f, true),
            FloatT("Drop Height",       c => c.dropHeight,        (c, v) => c.dropHeight = v, 0.005f, 0f, 0.05f, true),
        };
    }

    // --- Helpers de definicion de tunables ----------------------------------

    private static Tunable FloatT(string label, System.Func<JengaPhysicsConfig, float> get,
                                  System.Action<JengaPhysicsConfig, float> set,
                                  float step, float min, float max, bool requiresRebuild = false)
    {
        return new Tunable
        {
            label = label,
            read = c => get(c).ToString("F4"),
            step = (c, dir) =>
            {
                float mult = GetStepMultiplier();
                float newVal = Mathf.Clamp(get(c) + dir * step * mult, min, max);
                set(c, newVal);
            },
            requiresRebuild = requiresRebuild,
        };
    }

    private static Tunable IntT(string label, System.Func<JengaPhysicsConfig, int> get,
                                System.Action<JengaPhysicsConfig, int> set,
                                int step, int min, int max, bool requiresRebuild = false)
    {
        return new Tunable
        {
            label = label,
            read = c => get(c).ToString(),
            step = (c, dir) =>
            {
                int mult = Mathf.Max(1, (int)GetStepMultiplier());
                int newVal = Mathf.Clamp(get(c) + dir * step * mult, min, max);
                set(c, newVal);
            },
            requiresRebuild = requiresRebuild,
        };
    }

    private static Tunable BoolT(string label, System.Func<JengaPhysicsConfig, bool> get,
                                 System.Action<JengaPhysicsConfig, bool> set,
                                 bool requiresRebuild = false)
    {
        return new Tunable
        {
            label = label,
            read = c => get(c) ? "ON" : "off",
            step = (c, dir) => set(c, !get(c)),
            requiresRebuild = requiresRebuild,
        };
    }

    private static Tunable EnumT_FrictionCombine()
    {
        return new Tunable
        {
            label = "Friction Combine",
            read = c => c.frictionCombine.ToString(),
            step = (c, dir) => c.frictionCombine = CycleEnum(c.frictionCombine, dir),
        };
    }

    private static Tunable EnumT_BounceCombine()
    {
        return new Tunable
        {
            label = "Bounce Combine",
            read = c => c.bounceCombine.ToString(),
            step = (c, dir) => c.bounceCombine = CycleEnum(c.bounceCombine, dir),
        };
    }

    private static Tunable EnumT_CollisionDetection()
    {
        return new Tunable
        {
            label = "Collision Detect",
            read = c => c.collisionDetection.ToString(),
            step = (c, dir) => c.collisionDetection = CycleEnum(c.collisionDetection, dir),
        };
    }

    private static Tunable EnumT_Interpolation()
    {
        return new Tunable
        {
            label = "Interpolation",
            read = c => c.interpolation.ToString(),
            step = (c, dir) => c.interpolation = CycleEnum(c.interpolation, dir),
        };
    }

    private static T CycleEnum<T>(T current, int dir) where T : System.Enum
    {
        var values = (T[])System.Enum.GetValues(typeof(T));
        int idx = System.Array.IndexOf(values, current);
        if (idx < 0) idx = 0;
        idx = (idx + dir + values.Length) % values.Length;
        return values[idx];
    }

    private static float GetStepMultiplier()
    {
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return 10f;
        if (Input.GetKey(KeyCode.LeftAlt)   || Input.GetKey(KeyCode.RightAlt))   return 0.1f;
        return 1f;
    }

    // --- Input + render -----------------------------------------------------

    private void Update()
    {
        // Toggle visibility (siempre habilitado, incluso si no hay runtime).
        if (Input.GetKeyDown(toggleKey))
        {
            visible = !visible;
            if (visible) PinVrCanvasToView();
            UpdateVrCanvasVisibility();
        }

        if (!visible) return;

        var rt = JengaPhysicsRuntime.Instance;
        if (rt == null) return;

        // Cerrar con Esc.
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            visible = false;
            UpdateVrCanvasVisibility();
            return;
        }

        // Repin del panel VR al campo de vision actual.
        if (Input.GetKeyDown(repinKey)) PinVrCanvasToView();

        // Preset hotkeys 1..9.
        for (int i = 0; i < 9; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
            {
                if (i < rt.PresetCount) rt.LoadPreset(i);
            }
        }

        // Preset cycling.
        if (Input.GetKeyDown(nextPresetKey)) rt.CycleNextPreset();
        if (Input.GetKeyDown(prevPresetKey)) rt.CyclePrevPreset();

        // Reapply / rebuild.
        if (Input.GetKeyDown(reapplyKey)) rt.ApplyActive();
        if (Input.GetKeyDown(rebuildKey)) rt.RebuildTower();

        // Seleccion de parametro con Tab.
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            int dir = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? -1 : 1;
            selectedParam = (selectedParam + dir + _tunables.Count) % _tunables.Count;
        }

        // Ajuste del parametro seleccionado.
        int adj = 0;
        if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))
            adj = +1;
        else if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
            adj = -1;

        if (adj != 0 && selectedParam >= 0 && selectedParam < _tunables.Count)
        {
            var t = _tunables[selectedParam];
            t.step(rt.LiveConfig, adj);
            rt.ApplyActive();
        }

        // Cachear el texto una vez por frame para que OnGUI + VR canvas lean lo mismo
        // sin recalcular en cada Repaint.
        _cachedText = BuildText(rt);
    }

    private void LateUpdate()
    {
        if (!showInVR) return;
        if (!visible) return;
        if (_vrCanvas == null) return;
        if (vrPanelFollowHead) PinVrCanvasToView();
        if (_vrText != null && !string.IsNullOrEmpty(_cachedText)) _vrText.text = _cachedText;
    }

    /// <summary>
    /// Genera el texto formateado del HUD. Lo consume tanto OnGUI (desktop) como el TMP_Text del VR canvas.
    /// Se cachea una vez por frame en Update.
    /// </summary>
    private string BuildText(JengaPhysicsRuntime rt)
    {
        var cfg = rt.LiveConfig;
        if (cfg == null) return "";

        var sb = new System.Text.StringBuilder(2048);
        sb.AppendLine("<b>== JENGA PHYSICS TUNER (F6) ==</b>");
        sb.AppendLine($"Preset: [{rt.ActivePresetIndex + 1}/{rt.PresetCount}] <b>{cfg.presetName}</b>");
        sb.AppendLine($"Blocks applied: {rt.LastAppliedBlockCount}");
        sb.AppendLine();

        for (int i = 0; i < _tunables.Count; i++)
        {
            var t = _tunables[i];
            bool isSel = (i == selectedParam);
            string marker = isSel ? "<color=#7fcfff>></color> " : "  ";
            string rebuildTag = t.requiresRebuild ? " <color=#ffb060>[B]</color>" : "";
            string line = $"{marker}<b>{t.label,-22}</b> = {t.read(cfg),-18}{rebuildTag}";
            if (isSel) line = $"<color=#7fcfff>{line}</color>";
            sb.AppendLine(line);
        }

        sb.AppendLine();
        sb.AppendLine("<color=#a0a0a0>");
        sb.AppendLine("Tab: next param | Shift+Tab: prev");
        sb.AppendLine("Up/Down (or +/-): adjust   Shift = x10, Alt = x0.1");
        sb.AppendLine("1-9: load preset N      [ / ]: cycle");
        sb.AppendLine("R: reapply | B: rebuild | P: repin VR panel");
        sb.AppendLine("Esc / F6: close");
        sb.AppendLine("</color>");

        return sb.ToString();
    }

    private void OnGUI()
    {
        if (!visible) return;
#if UNITY_EDITOR
        if (!showInEditor) return;
#else
        if (!showInBuild) return;
#endif

        var rt = JengaPhysicsRuntime.Instance;
        if (rt == null) return;
        if (string.IsNullOrEmpty(_cachedText)) _cachedText = BuildText(rt);

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box);
            style.alignment = TextAnchor.UpperLeft;
            style.fontSize = fontSize;
            style.normal.textColor = Color.white;
            style.padding = new RectOffset(10, 10, 8, 8);
            style.richText = true;
        }

        string text = _cachedText;
        float height = style.CalcHeight(new GUIContent(text), fixedWidth);
        float x = margin.x;
        float y = Screen.height - height - margin.y - 16f;
        Rect rect = new Rect(x, y, fixedWidth, height + 16f);

        GUI.color = new Color(0f, 0f, 0f, 0.75f);
        GUI.Box(rect, GUIContent.none);
        GUI.color = Color.white;
        GUI.Label(rect, text, style);
    }

    // --- VR Canvas (world-space) -------------------------------------------

    private void EnsureVrCanvas()
    {
        if (!showInVR) return;
        if (_vrCanvas != null) return;

        var canvasGO = new GameObject("JengaPhysicsTuner_VRCanvas");
        canvasGO.transform.SetParent(null, true); // world-space, no parented

        _vrCanvas = canvasGO.AddComponent<Canvas>();
        _vrCanvas.renderMode = RenderMode.WorldSpace;
        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>();

        _vrCanvasRect = canvasGO.GetComponent<RectTransform>();
        // sizeDelta en "pixeles UI" (alto se ajusta automatico por content fitter), localScale traduce a metros.
        _vrCanvasRect.sizeDelta = new Vector2(900f, 1100f);
        float pxPerMeter = 900f / Mathf.Max(0.05f, vrPanelWidthM);
        _vrCanvasRect.localScale = Vector3.one * (1f / pxPerMeter);

        // Background semi-transparente.
        var bgGO = new GameObject("Background");
        bgGO.transform.SetParent(canvasGO.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.82f);
        bgImg.raycastTarget = false;
        var bgRect = bgGO.GetComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;

        // Texto TMP. Fixed-width font ideal para alineacion de columnas pero default sirve.
        var textGO = new GameObject("Text");
        textGO.transform.SetParent(canvasGO.transform, false);
        _vrText = textGO.AddComponent<TextMeshProUGUI>();
        _vrText.fontSize = 26;
        _vrText.color = Color.white;
        _vrText.alignment = TextAlignmentOptions.TopLeft;
        _vrText.richText = true;
        _vrText.enableWordWrapping = false;
        _vrText.margin = new Vector4(28, 24, 28, 24);
        _vrText.raycastTarget = false;
        var textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        _vrCanvas.gameObject.SetActive(visible);
    }

    private void UpdateVrCanvasVisibility()
    {
        if (!showInVR) return;
        EnsureVrCanvas();
        if (_vrCanvas != null) _vrCanvas.gameObject.SetActive(visible);
    }

    private void PinVrCanvasToView()
    {
        if (!showInVR) return;
        EnsureVrCanvas();
        if (_vrCanvas == null) return;

        if (_hmdTransform == null)
        {
            var cam = Camera.main;
            if (cam == null) return;
            _hmdTransform = cam.transform;
        }

        Vector3 forward = _hmdTransform.forward;
        Vector3 right = _hmdTransform.right;
        // Up del mundo para mantener el panel verticalmente alineado (sin tilt cuando inclinas la cabeza).
        Vector3 up = Vector3.up;

        Vector3 pos = _hmdTransform.position
                    + forward * vrPanelDistance
                    + right   * vrPanelLateralOffset
                    + up      * vrPanelVerticalOffset;

        // Que el panel mire al HMD (back-facing al usuario seria invisible).
        Vector3 toHmd = _hmdTransform.position - pos;
        toHmd.y = 0f; // panel vertical
        if (toHmd.sqrMagnitude < 1e-4f) toHmd = -forward;
        Quaternion rot = Quaternion.LookRotation(-toHmd.normalized, Vector3.up);

        _vrCanvas.transform.SetPositionAndRotation(pos, rot);
    }

    private void OnDestroy()
    {
        if (_vrCanvas != null)
        {
            Destroy(_vrCanvas.gameObject);
            _vrCanvas = null;
            _vrText = null;
        }
    }
}
