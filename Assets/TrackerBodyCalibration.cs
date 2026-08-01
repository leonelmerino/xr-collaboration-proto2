using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Hands;

/// <summary>
/// Presioná C para calibrar. El usuario debe estar parado derecho con los trackers
/// puestos en cintura y pies, separados ~1m entre los pies.
///
/// IMPORTANTE sobre espacios de coordenadas:
/// Los trackers (InputDevices API) reportan posiciones en el espacio LOCAL de tracking
/// (OpenXR local/stage space), que es el espacio ANTES de que el XR Origin le aplique
/// su transformación (posición + rotación). Por eso, cuando el XR Origin tiene una
/// rotación (p.ej. al teleportar el player), las posiciones raw del tracker y la
/// posición del HMD en mundo están en sistemas diferentes.
///
/// Para resolver esto, todas las posiciones raw se transforman a espacio MUNDO
/// usando hmd.parent.TransformPoint(), que es el mismo transform que el XR subsystem
/// usa para llevar el HMD de tracking space a world space.
///
/// Fórmula final (todo en espacio MUNDO):
///   correctedPos = TrackingParent.TransformPoint(rawTrackerPos) - offset
/// </summary>
public class TrackerBodyCalibration : MonoBehaviour
{
    [Header("Referencias (auto-buscadas si quedan vacías)")]
    [SerializeField] private Transform hmd;

    [Header("Parámetros de pose de calibración")]
    [Tooltip("Distancia entre pies al calibrar (cada pie queda a la mitad de este valor del centro).")]
    [SerializeField] private float footSeparation = 0f;

    [Tooltip("Desplazamiento de los pies en la dirección que mira el HMD. " +
             "Negativo = los pies están detrás del HMD (valor típico: -0.07 a -0.15 m).")]
    [SerializeField] private float footForwardOffset = -0.08f;

    [Tooltip("Proporción de la altura del HMD donde se espera la cintura. " +
             "0.55 = cintura ≈ 55% de la altura del HMD desde el suelo.")]
    [SerializeField] private float waistHeightRatio = 0.55f;

    [Header("Refinamiento con manos (presioná R)")]
    [Tooltip("Tecla para refinar los offsets tomando cada tracker en la mano. Requiere calibración previa (C).")]
    [SerializeField] private KeyCode refineKey = KeyCode.R;
    [Tooltip("Radio máximo (m) para asociar un tracker calibrado a una mano. " +
             "Si la esfera del tracker está más lejos de ambas manos, no se refina.")]
    [SerializeField] private float refineMatchRadius = 0.30f;

    // ── Trackers identificados ─────────────────────────────────────────────
    public InputDevice TrackerWaist { get; private set; }
    public InputDevice TrackerFootL { get; private set; }
    public InputDevice TrackerFootR { get; private set; }

    // ── Offsets en espacio MUNDO: correctedWorld = TransformPoint(raw) - offset ──
    public Vector3 WaistOffset { get; private set; }
    public Vector3 FootLOffset { get; private set; }
    public Vector3 FootROffset { get; private set; }

    // ── Estado ────────────────────────────────────────────────────────────
    public bool IsCalibrated { get; private set; }

    // ── Transform que convierte posiciones de tracking → mundo ────────────
    /// <summary>
    /// Parent del HMD en la jerarquía del XR rig (Camera Offset / XR Origin).
    /// Usar TrackingParent.TransformPoint(rawPos) para pasar de tracking a mundo.
    /// </summary>
    public Transform TrackingParent { get; private set; }

    private XRHandSubsystem _handSubsystem;

    // ── Posiciones esperadas en mundo (para debug / TrackerPoseDriver) ─────
    public Vector3 ExpectedWaist { get; private set; }
    public Vector3 ExpectedFootL { get; private set; }
    public Vector3 ExpectedFootR { get; private set; }

    void Start()
    {
        TryFindHmd();
        if (hmd != null)
            TrackingParent = hmd.parent;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.C))
            Calibrate();
        if (Input.GetKeyDown(refineKey) && IsCalibrated)
            RefineWithHands();
    }

    // ── Calibración ────────────────────────────────────────────────────────
    public void Calibrate()
    {
        if (!TryFindHmd())
        {
            Debug.LogError("[Calibration] No se encontró el HMD. Abortando.");
            return;
        }

        // 1. Recoger todos los dispositivos y loguear para diagnóstico
        var devices = new List<InputDevice>();
        InputDevices.GetDevices(devices);

        if (devices.Count == 0)
        {
            Debug.LogWarning("[Calibration] InputDevices.GetDevices() devolvió 0 dispositivos. " +
                             "¿Está XR inicializado?");
        }
        else
        {
            var sb = new System.Text.StringBuilder($"[Calibration] {devices.Count} dispositivos XR encontrados:\n");
            foreach (var d in devices)
            {
                d.TryGetFeatureValue(CommonUsages.isTracked, out bool t);
                d.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p);
                sb.AppendLine($"  name='{d.name}' isTracked={t} pos={p:F2} chars={d.characteristics}");
            }
            Debug.Log(sb.ToString());
        }

        // Filtrar trackers VIVE Ultimate Tracker con tracking activo
        var tracked = new List<(InputDevice dev, Vector3 pos)>();
        foreach (var d in devices)
        {
            if (!d.name.Contains("VIVE Ultimate Tracker")) continue;
            if (!d.TryGetFeatureValue(CommonUsages.isTracked, out bool ok) || !ok) continue;
            if (!d.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos)) continue;
            tracked.Add((d, pos));
        }

        if (tracked.Count != 3)
        {
            Debug.LogWarning($"[Calibration] Esperaba 3 trackers con tracking activo, encontré {tracked.Count}. " +
                             "Revisá los nombres de dispositivos en el log anterior.");
            return;
        }

        // 2. Guardar el TrackingParent (convierte tracking space → world space)
        TrackingParent = hmd.parent;

        // 3. Identificar cintura (Y más alta en tracking space) y pies
        tracked.Sort((a, b) => b.pos.y.CompareTo(a.pos.y));
        var waist = tracked[0];
        var foot0 = tracked[1];
        var foot1 = tracked[2];

        TrackerWaist = waist.dev;

        // Pie derecho = más a la derecha del HMD en mundo
        // IMPORTANTE: comparar en espacio mundo, no en tracking space
        Vector3 foot0World_prelim = TrackingToWorld(foot0.pos);
        Vector3 foot1World_prelim = TrackingToWorld(foot1.pos);
        float dot0 = Vector3.Dot(foot0World_prelim - hmd.position, hmd.right);
        float dot1 = Vector3.Dot(foot1World_prelim - hmd.position, hmd.right);

        Vector3 footRPos, footLPos;
        if (dot0 > dot1)
        {
            TrackerFootR = foot0.dev; footRPos = foot0.pos;
            TrackerFootL = foot1.dev; footLPos = foot1.pos;
        }
        else
        {
            TrackerFootL = foot0.dev; footLPos = foot0.pos;
            TrackerFootR = foot1.dev; footRPos = foot1.pos;
        }

        // 4. Posiciones esperadas en espacio MUNDO (basadas en HMD world)
        Vector3 hmdFloor = new Vector3(hmd.position.x, 0f, hmd.position.z);
        float half = footSeparation * 0.5f;

        Vector3 hmdForwardXZ = new Vector3(hmd.forward.x, 0f, hmd.forward.z).normalized;
        ExpectedWaist = hmdFloor + Vector3.up * (hmd.position.y * waistHeightRatio);
        ExpectedFootR = hmdFloor + hmd.right * half + hmdForwardXZ * footForwardOffset;
        ExpectedFootL = hmdFloor + hmd.right * -half + hmdForwardXZ * footForwardOffset;

        // 5. Convertir posiciones raw de trackers a espacio MUNDO
        //    CRÍTICO: raw viene del InputDevices API en tracking-local space.
        //    TrackingParent.TransformPoint() aplica la misma transformación que
        //    el XR subsystem usa para el HMD (posición + rotación del XR Origin).
        Vector3 waistWorld = TrackingToWorld(waist.pos);
        Vector3 footLWorld = TrackingToWorld(footLPos);
        Vector3 footRWorld = TrackingToWorld(footRPos);

        // 6. Offsets en espacio mundo
        WaistOffset = waistWorld - ExpectedWaist;
        FootLOffset = footLWorld - ExpectedFootL;
        FootROffset = footRWorld - ExpectedFootR;

        IsCalibrated = true;

        Debug.Log($"[Calibration] ✓  Waist={TrackerWaist.name} | FootL={TrackerFootL.name} | FootR={TrackerFootR.name}");
        Debug.Log($"[Calibration] HMD height={hmd.position.y:F2}m | Waist esperada Y={ExpectedWaist.y:F2}m");
        Debug.Log($"[Calibration] Raw (local)   → waist={waist.pos:F2} | footL={footLPos:F2} | footR={footRPos:F2}");
        Debug.Log($"[Calibration] Raw (mundo)   → waist={waistWorld:F2} | footL={footLWorld:F2} | footR={footRWorld:F2}");
        Debug.Log($"[Calibration] Esperado      → waist={ExpectedWaist:F2} | footL={ExpectedFootL:F2} | footR={ExpectedFootR:F2}");
        Debug.Log($"[Calibration] Offsets mundo → waist={WaistOffset:F2} | footL={FootLOffset:F2} | footR={FootROffset:F2}");

        if (TrackingParent != null)
            Debug.Log($"[Calibration] XR rig parent: pos={TrackingParent.position:F2} euler={TrackingParent.eulerAngles:F1}");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Convierte una posición del espacio de tracking (InputDevices API)
    /// al espacio mundo, usando el mismo transform que el XR subsystem.
    /// </summary>
    public Vector3 TrackingToWorld(Vector3 trackingPos)
    {
        // El VIVE XR Tracker reporta en un sistema donde +X = izquierda física y -Z = adelante.
        // Para convertir a Unity (+X = derecha, +Z = adelante): negar tanto X como Z.
        trackingPos.x = -trackingPos.x;
        trackingPos.z = -trackingPos.z;
        return TrackingParent != null
            ? TrackingParent.TransformPoint(trackingPos)
            : trackingPos;
    }

    bool TryFindHmd()
    {
        if (hmd != null) return true;
        var origin = FindObjectOfType<XROrigin>();
        if (origin != null && origin.Camera != null)
            hmd = origin.Camera.transform;
        return hmd != null;
    }

    // ── Refinamiento con manos ─────────────────────────────────────────────

    /// <summary>
    /// Tomá un tracker en la mano y presioná R.
    /// El sistema detecta qué tracker está más cerca de cada mano y ajusta su offset
    /// para que coincida exactamente con la posición de la muñeca/palma.
    ///
    /// Procedimiento:
    ///   1. Calibrá (C) con los trackers en su posición normal.
    ///   2. Tomá un tracker en la mano izquierda o derecha.
    ///   3. Presioná R — el tracker más cercano a tu mano queda corregido.
    ///   4. Repetí con los otros trackers.
    /// </summary>
    public void RefineWithHands()
    {
        Vector3? lHand = TryGetHandWorld(isLeft: true);
        Vector3? rHand = TryGetHandWorld(isLeft: false);

        if (lHand == null && rHand == null)
        {
            Debug.LogWarning("[Calibration] Refine: ninguna mano con tracking activo. Mostrá las manos al visor.");
            return;
        }

        var sb = new System.Text.StringBuilder("[Calibration] Refine con manos:\n");
        int count = 0;

        count += TryRefineOne(TrackerWaist, WaistOffset, lHand, rHand, "Waist", sb, out Vector3 newWaist);
        count += TryRefineOne(TrackerFootL, FootLOffset, lHand, rHand, "FootL", sb, out Vector3 newFootL);
        count += TryRefineOne(TrackerFootR, FootROffset, lHand, rHand, "FootR", sb, out Vector3 newFootR);

        // Aplicar sólo los offsets que se refinaron (count > 0 indica al menos uno)
        // Usamos los valores locales — si TryRefineOne no refinó, devuelve el offset original
        WaistOffset = newWaist;
        FootLOffset = newFootL;
        FootROffset = newFootR;

        if (count == 0)
            Debug.LogWarning($"[Calibration] Refine: ningún tracker dentro de {refineMatchRadius:F2} m de alguna mano.");
        else
            Debug.Log(sb.ToString());
    }

    int TryRefineOne(InputDevice dev, Vector3 currentOffset,
                     Vector3? lHand, Vector3? rHand, string label,
                     System.Text.StringBuilder sb, out Vector3 outOffset)
    {
        outOffset = currentOffset; // sin cambio por defecto

        if (!dev.isValid) return 0;
        if (!dev.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked) return 0;
        if (!dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 raw)) return 0;

        Vector3 rawWorld  = TrackingToWorld(raw);
        Vector3 curPos    = rawWorld - currentOffset;

        float dL = lHand.HasValue ? Vector3.Distance(curPos, lHand.Value) : float.MaxValue;
        float dR = rHand.HasValue ? Vector3.Distance(curPos, rHand.Value) : float.MaxValue;

        if (Mathf.Min(dL, dR) > refineMatchRadius) return 0;

        bool    useLeft  = dL < dR;
        Vector3 handPos  = useLeft ? lHand.Value : rHand.Value;
        Vector3 error    = curPos - handPos;
        outOffset        = rawWorld - handPos;

        sb.AppendLine($"  {label} ({(useLeft ? "mano izq" : "mano der")}): " +
                      $"error anterior={error:F3} ({error.magnitude * 100f:F1} cm) " +
                      $"→ offset nuevo={outOffset:F3}");
        return 1;
    }

    Vector3? TryGetHandWorld(bool isLeft)
    {
        if (_handSubsystem == null)
        {
            var subs = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            if (subs.Count == 0) return null;
            _handSubsystem = subs[0];
        }

        var hand = isLeft ? _handSubsystem.leftHand : _handSubsystem.rightHand;
        if (!hand.isTracked) return null;

        // Intentar primero la palma (centro de la mano), luego la muñeca como fallback.
        // Las poses de XRHands están en session-local space — igual que el HMD —
        // pero sin la inversión de X/Z específica de los trackers VIVE.
        XRHandJoint joint = hand.GetJoint(XRHandJointID.Palm);
        if (!joint.TryGetPose(out Pose pose))
        {
            joint = hand.GetJoint(XRHandJointID.Wrist);
            if (!joint.TryGetPose(out pose)) return null;
        }

        return TrackingParent != null ? TrackingParent.TransformPoint(pose.position) : pose.position;
    }
}
