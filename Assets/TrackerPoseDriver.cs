using UnityEngine;
using UnityEngine.XR;
using Unity.XR.CoreUtils;

/// <summary>
/// Aplica cada frame los offsets calculados por TrackerBodyCalibration para
/// llevar las posiciones de los trackers a las posiciones correctas del avatar.
///
/// Fórmula (todo en espacio MUNDO):
///   correctedWorld = calibration.TrackingToWorld(rawTrackerPos) - offset
///
/// Esto es necesario porque los trackers reportan posiciones en el espacio de
/// tracking local del XR rig (antes de que se aplique la rotación/posición del
/// XR Origin). Si no se hace esta conversión, el movimiento horizontal del avatar
/// no coincide con el movimiento físico cuando el XR Origin está rotado (p.ej.
/// al teleportar al jugador).
///
/// Filtros:
///   - Anti-saltos por frame: rechaza cambios > maxFrameJump entre frames
///     consecutivos (pérdida de tracking, no movimiento real).
///   - Tracker atascado: si la posición no cambia mientras el HMD sí se mueve,
///     emite una advertencia y deja de sobreescribir el hueso.
///
/// Requiere TrackerBodyCalibration calibrado (tecla C).
/// El Animator del avatar se auto-busca en Avatar_Host.
/// </summary>
public class TrackerPoseDriver : MonoBehaviour
{
    [Header("Referencia a calibración")]
    [SerializeField] private TrackerBodyCalibration calibration;

    [Header("Filtro anti-saltos (por frame)")]
    [Tooltip("Salto máximo permitido (metros) entre frames consecutivos. " +
             "Un salto mayor indica pérdida de tracking. 0 = desactivado.\n" +
             "Referencia: caminar rápido ≈ 0.05 m/frame @90Hz. 0.3 bloquea saltos de >27 m/s.")]
    [SerializeField] private float maxFrameJump = 0.3f;

    [Header("Detección de tracker atascado")]
    [Tooltip("Si el tracker no se mueve más de esta cantidad (m) durante stuckFrameLimit frames " +
             "mientras el HMD sí se movió, se considera atascado y no se aplica al hueso. " +
             "0 = desactivado.")]
    [SerializeField] private float stuckMoveThreshold = 0.005f;
    [Tooltip("Cuántos frames consecutivos con delta < stuckMoveThreshold para declarar el tracker atascado.")]
    [SerializeField] private int stuckFrameLimit = 90;

    [Header("Debug")]
    [Tooltip("Muestra las posiciones corregidas en la consola cada N frames. 0 = desactivado.")]
    [SerializeField] private int debugLogInterval = 60;

    // ── Internos ───────────────────────────────────────────────────────────
    private Animator _avatarAnimator;
    private int      _frameCount;
    private Transform _hmd;
    private Vector3   _hmdPrevPos;
    private bool      _hmdPrevValid;

    private readonly TrackerFilterState _waistState = new TrackerFilterState();
    private readonly TrackerFilterState _footLState  = new TrackerFilterState();
    private readonly TrackerFilterState _footRState  = new TrackerFilterState();

    private sealed class TrackerFilterState
    {
        public Vector3 lastGoodPos;
        public bool    hasLast;
        public int     stuckFrames;
        public bool    stuckWarned;
    }

    // ── Unity ──────────────────────────────────────────────────────────────
    void Awake()
    {
        if (calibration == null)
            calibration = FindObjectOfType<TrackerBodyCalibration>();

        var origin = FindObjectOfType<XROrigin>();
        if (origin != null && origin.Camera != null)
            _hmd = origin.Camera.transform;
    }

    void LateUpdate()
    {
        if (calibration == null || !calibration.IsCalibrated) return;

        // Auto-buscar avatar host si todavía no está disponible
        if (_avatarAnimator == null)
        {
            var go = GameObject.Find("Avatar_Host");
            if (go != null) _avatarAnimator = go.GetComponentInChildren<Animator>();
            if (_avatarAnimator == null) return;
        }

        // Movimiento del HMD este frame (para detectar trackers atascados)
        float hmdMovedThisFrame = 0f;
        if (_hmd != null)
        {
            if (_hmdPrevValid)
                hmdMovedThisFrame = Vector3.Distance(_hmd.position, _hmdPrevPos);
            _hmdPrevPos   = _hmd.position;
            _hmdPrevValid = true;
        }

        // Obtener posiciones corregidas en espacio mundo
        bool gotWaist = TryGetCorrected(calibration.TrackerWaist, calibration.WaistOffset, calibration, out Vector3 waistPos);
        bool gotFootL = TryGetCorrected(calibration.TrackerFootL, calibration.FootLOffset, calibration, out Vector3 footLPos);
        bool gotFootR = TryGetCorrected(calibration.TrackerFootR, calibration.FootROffset, calibration, out Vector3 footRPos);

        // Filtro por delta entre frames consecutivos + detección de tracker atascado
        bool waistValid = gotWaist && IsNotJumping(waistPos, _waistState, hmdMovedThisFrame, "Waist");
        bool footLValid = gotFootL && IsNotJumping(footLPos, _footLState, hmdMovedThisFrame, "FootL");
        bool footRValid = gotFootR && IsNotJumping(footRPos, _footRState, hmdMovedThisFrame, "FootR");

        // Si el tracker no tiene datos válidos, resetear estado para no filtrar al volver
        if (!gotWaist) _waistState.hasLast = false;
        if (!gotFootL) _footLState.hasLast = false;
        if (!gotFootR) _footRState.hasLast = false;

        // Aplicar a los huesos del avatar
        // IMPORTANTE: usamos localPosition (relativa al root), no world position.
        // Si se usa world position, AvatarFollowXROrigin (que también corre en LateUpdate)
        // mueve el root DESPUÉS, desplazando el hueso otra vez → doble movimiento.
        // Con localPosition, el hueso queda fijo respecto al root aunque éste se mueva.
        Transform avatarRoot = _avatarAnimator.transform;

        if (waistValid)
        {
            var hip = _avatarAnimator.GetBoneTransform(HumanBodyBones.Hips);
            if (hip != null)
            {
                // Convertir world → local relativo al root del avatar
                hip.localPosition = avatarRoot.InverseTransformPoint(waistPos);

                // Debug: comparar root vs hip en mundo
                if (debugLogInterval > 0 && _frameCount % debugLogInterval == 0)
                    Debug.Log($"[TrackerPoseDriver] root.world={avatarRoot.position:F2} | hip.world≈{waistPos:F2} | HMD.world={(_hmd != null ? _hmd.position.ToString("F2") : "N/A")}");
            }
        }
        if (footLValid)
        {
            var lf = _avatarAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
            if (lf != null) lf.localPosition = avatarRoot.InverseTransformPoint(footLPos);
        }
        if (footRValid)
        {
            var rf = _avatarAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (rf != null) rf.localPosition = avatarRoot.InverseTransformPoint(footRPos);
        }

        // Log de debug opcional
        if (debugLogInterval > 0)
        {
            _frameCount++;
            if (_frameCount % debugLogInterval == 0)
            {
                string ws = FormatState(waistValid, gotWaist, waistPos, _waistState);
                string fL = FormatState(footLValid, gotFootL, footLPos, _footLState);
                string fR = FormatState(footRValid, gotFootR, footRPos, _footRState);
                Debug.Log($"[TrackerPoseDriver] waist={ws} | footL={fL} | footR={fR}");
                // El log root+hip ya se emite dentro del bloque waistValid arriba
            }
        }
    }

    // ── Filtro ─────────────────────────────────────────────────────────────

    bool IsNotJumping(Vector3 current, TrackerFilterState state,
                      float hmdMovedThisFrame, string trackerName)
    {
        if (!state.hasLast)
        {
            state.lastGoodPos  = current;
            state.hasLast      = true;
            state.stuckFrames  = 0;
            state.stuckWarned  = false;
            return true;
        }

        float delta = Vector3.Distance(current, state.lastGoodPos);

        // Detectar salto de tracking loss
        if (maxFrameJump > 0f && delta > maxFrameJump)
            return false;

        // Detectar tracker atascado
        if (stuckMoveThreshold > 0f && stuckFrameLimit > 0)
        {
            bool trackerMoved = delta > stuckMoveThreshold;
            bool hmdMoved     = hmdMovedThisFrame > stuckMoveThreshold;

            if (!trackerMoved && hmdMoved)
            {
                state.stuckFrames++;
                if (state.stuckFrames >= stuckFrameLimit && !state.stuckWarned)
                {
                    Debug.LogWarning(
                        $"[TrackerPoseDriver] ⚠ {trackerName} parece ATASCADO — no se mueve " +
                        $"({stuckFrameLimit} frames) mientras el HMD sí se mueve. " +
                        $"Verificá batería y conectividad del tracker.");
                    state.stuckWarned = true;
                }
                if (state.stuckFrames >= stuckFrameLimit)
                    return false;
            }
            else
            {
                if (state.stuckFrames > 0)
                {
                    state.stuckFrames = 0;
                    state.stuckWarned = false;
                }
            }
        }

        state.lastGoodPos = current;
        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    static string FormatState(bool valid, bool got, Vector3 pos, TrackerFilterState state)
    {
        if (!got)   return "NO TRACKING";
        if (!valid)
        {
            string reason = state.stuckFrames >= 1 ? "ATASCADO" : "FILTRADO";
            return $"{reason} {pos:F2}";
        }
        return $"{pos:F2}";
    }

    /// <summary>
    /// Lee la posición raw del tracker, la convierte de tracking-space a world-space
    /// usando el TrackingParent de la calibración, y aplica el offset.
    ///
    /// Fórmula: corrected = calibration.TrackingToWorld(raw) - offset
    ///
    /// Esto es equivalente a:
    ///   corrected = hmd.parent.TransformPoint(raw) - offset
    ///
    /// El offset fue calculado en calibración también en world-space, así que la resta
    /// da el desplazamiento correcto respecto a la posición esperada.
    /// </summary>
    static bool TryGetCorrected(InputDevice dev, Vector3 offset,
                                TrackerBodyCalibration cal, out Vector3 corrected)
    {
        corrected = Vector3.zero;
        if (!dev.isValid) return false;
        if (!dev.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked) return false;
        if (!dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 raw)) return false;

        // Convertir de tracking-local space a world space ANTES de restar el offset
        Vector3 rawWorld = cal.TrackingToWorld(raw);
        corrected = rawWorld - offset;
        return true;
    }
}
