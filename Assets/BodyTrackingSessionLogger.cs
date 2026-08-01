using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

/// <summary>
/// Registra por frame las posiciones de los tres trackers (waist, pie L, pie R)
/// y de ambas manos (palma, con fallback a muñeca), en un CSV separado del gaze log.
///
/// Sistema de archivos idéntico a EyeTrackingSessionLogger:
///   Application.persistentDataPath/EyeTrackingLogs/{participantId}/{sessionId}/
///   {taskId}_{trialId}_{nextIndex:000}_body.csv
///
/// Los trackers se loggean en posición CORREGIDA (calibración aplicada, espacio mundo).
/// Las manos se loggean desde XRHandSubsystem (Palm, espacio mundo sin la
/// inversión X/Z específica de los trackers VIVE).
///
/// No sincroniza nada por red: cada nodo genera su propio archivo local.
/// </summary>
public class BodyTrackingSessionLogger : MonoBehaviour
{
    [Header("Referencias (auto-buscadas si quedan vacías)")]
    [SerializeField] private TrackerBodyCalibration calibration;

    [Header("Session Info")]
    public string participantId = "P001";
    public string sessionId     = "S001";
    public string taskId        = "task_01";
    public string trialId       = "trial_01";
    public string condition     = "baseline";

    [Header("Logging")]
    public bool autoStart      = true;
    public bool flushEveryFrame = false;

    // ── Estado interno ─────────────────────────────────────────────────────
    private StreamWriter    _writer;
    private bool            _isLogging;
    private int             _sampleIndex;
    private XRHandSubsystem _handSubsystem;
    private Transform       _head;

    // ── Unity ──────────────────────────────────────────────────────────────

    void Start()
    {
        if (calibration == null)
            calibration = FindObjectOfType<TrackerBodyCalibration>();
        if (_head == null && Camera.main != null)
            _head = Camera.main.transform;

        if (autoStart) StartLogging();
    }

    void Update()
    {
        if (!_isLogging) return;

        var c = CultureInfo.InvariantCulture;
        _sampleIndex++;

        // ── HMD ────────────────────────────────────────────────────────────
        Vector3    hp = _head ? _head.position : Vector3.zero;
        Quaternion hr = _head ? _head.rotation : Quaternion.identity;

        // ── Trackers (posición corregida en mundo) ──────────────────────────
        bool cal = calibration != null && calibration.IsCalibrated;

        Vector3 wPos = Vector3.zero, lPos = Vector3.zero, rPos = Vector3.zero;
        bool wValid = cal && TryGetCorrected(calibration.TrackerWaist, calibration.WaistOffset, out wPos);
        bool lValid = cal && TryGetCorrected(calibration.TrackerFootL, calibration.FootLOffset,  out lPos);
        bool rValid = cal && TryGetCorrected(calibration.TrackerFootR, calibration.FootROffset,  out rPos);

        // ── Manos (Palm/Wrist en mundo via XRHandSubsystem) ─────────────────
        bool    hlValid = TryGetHandPose(isLeft: true,  out Vector3 hlPos, out Quaternion hlRot);
        bool    hrValid = TryGetHandPose(isLeft: false, out Vector3 hrPos, out Quaternion hrRot);

        // ── Helpers de formato (locales, idénticos al patrón del gaze logger) ──
        string F(float v) => v.ToString(c);
        string B(bool b)  => b ? "1" : "0";
        string Px(bool v, Vector3 p) => v ? p.x.ToString(c) : "";
        string Py(bool v, Vector3 p) => v ? p.y.ToString(c) : "";
        string Pz(bool v, Vector3 p) => v ? p.z.ToString(c) : "";
        string Qx(bool v, Quaternion q) => v ? q.x.ToString(c) : "";
        string Qy(bool v, Quaternion q) => v ? q.y.ToString(c) : "";
        string Qz(bool v, Quaternion q) => v ? q.z.ToString(c) : "";
        string Qw(bool v, Quaternion q) => v ? q.w.ToString(c) : "";

        _writer.WriteLine(string.Join(",",
            // ── Identificación de muestra ───────────────────────────────────
            _sampleIndex.ToString(c),
            Time.realtimeSinceStartupAsDouble.ToString("F6", c),
            "\"" + DateTime.UtcNow.ToString("o") + "\"",

            // ── Metadata de sesión ──────────────────────────────────────────
            "\"" + participantId + "\"",
            "\"" + sessionId     + "\"",
            "\"" + taskId        + "\"",
            "\"" + trialId       + "\"",
            "\"" + condition     + "\"",

            // ── Cabeza (HMD) ────────────────────────────────────────────────
            F(hp.x), F(hp.y), F(hp.z),
            F(hr.x), F(hr.y), F(hr.z), F(hr.w),

            // ── Estado de calibración ───────────────────────────────────────
            B(cal),

            // ── Cintura ─────────────────────────────────────────────────────
            B(wValid), Px(wValid, wPos), Py(wValid, wPos), Pz(wValid, wPos),

            // ── Pie izquierdo ───────────────────────────────────────────────
            B(lValid), Px(lValid, lPos), Py(lValid, lPos), Pz(lValid, lPos),

            // ── Pie derecho ─────────────────────────────────────────────────
            B(rValid), Px(rValid, rPos), Py(rValid, rPos), Pz(rValid, rPos),

            // ── Mano izquierda (posición + rotación de palma) ───────────────
            B(hlValid),
            Px(hlValid, hlPos), Py(hlValid, hlPos), Pz(hlValid, hlPos),
            Qx(hlValid, hlRot), Qy(hlValid, hlRot), Qz(hlValid, hlRot), Qw(hlValid, hlRot),

            // ── Mano derecha (posición + rotación de palma) ─────────────────
            B(hrValid),
            Px(hrValid, hrPos), Py(hrValid, hrPos), Pz(hrValid, hrPos),
            Qx(hrValid, hrRot), Qy(hrValid, hrRot), Qz(hrValid, hrRot), Qw(hrValid, hrRot)
        ));

        if (flushEveryFrame) _writer.Flush();
    }

    void OnApplicationQuit() => StopLogging();

    // ── API pública ────────────────────────────────────────────────────────

    public void StartLogging()
    {
        if (_isLogging) return;

        string folder = Path.Combine(Application.persistentDataPath, "EyeTrackingLogs", participantId, sessionId);
        Directory.CreateDirectory(folder);

        string prefix = $"{taskId}_{trialId}_";
        string[] existing = Directory.GetFiles(folder, $"{prefix}*_body.csv");
        int nextIndex = existing
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Select(n => n.Replace(prefix, "").Replace("_body", ""))
            .Select(t => int.TryParse(t, out int n) ? n : 0)
            .DefaultIfEmpty(0).Max() + 1;

        string filePath = Path.Combine(folder, $"{prefix}{nextIndex:000}_body.csv");
        _writer = new StreamWriter(filePath, false, Encoding.UTF8);

        _writer.WriteLine(
            "sample_index,timestamp_rel_s,timestamp_utc_iso," +
            "participant_id,session_id,task_id,trial_id,condition," +
            "head_x,head_y,head_z,head_qx,head_qy,head_qz,head_qw," +
            "is_calibrated," +
            "waist_valid,waist_x,waist_y,waist_z," +
            "foot_l_valid,foot_l_x,foot_l_y,foot_l_z," +
            "foot_r_valid,foot_r_x,foot_r_y,foot_r_z," +
            "hand_l_valid,hand_l_x,hand_l_y,hand_l_z,hand_l_qx,hand_l_qy,hand_l_qz,hand_l_qw," +
            "hand_r_valid,hand_r_x,hand_r_y,hand_r_z,hand_r_qx,hand_r_qy,hand_r_qz,hand_r_qw"
        );

        _isLogging    = true;
        _sampleIndex  = 0;

        Debug.Log($"[BodyTrackingLogger] Logging to: {filePath}");
    }

    public void StopLogging()
    {
        if (!_isLogging) return;
        _writer.Flush();
        _writer.Close();
        _writer   = null;
        _isLogging = false;
        Debug.Log("[BodyTrackingLogger] Logging stopped.");
    }

    // ── Helpers privados ───────────────────────────────────────────────────

    bool TryGetCorrected(InputDevice dev, Vector3 offset, out Vector3 corrected)
    {
        corrected = Vector3.zero;
        if (!dev.isValid) return false;
        if (!dev.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked) return false;
        if (!dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 raw)) return false;
        corrected = calibration.TrackingToWorld(raw) - offset;
        return true;
    }

    bool TryGetHandPose(bool isLeft, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;

        if (_handSubsystem == null)
        {
            var list = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(list);
            if (list.Count == 0) return false;
            _handSubsystem = list[0];
        }

        var hand = isLeft ? _handSubsystem.leftHand : _handSubsystem.rightHand;
        if (!hand.isTracked) return false;

        // Palm primero (centro de la mano, más estable para posición).
        // Fallback a Wrist si Palm no está disponible en esta implementación del SDK.
        XRHandJoint joint = hand.GetJoint(XRHandJointID.Palm);
        if (!joint.TryGetPose(out Pose pose))
        {
            joint = hand.GetJoint(XRHandJointID.Wrist);
            if (!joint.TryGetPose(out pose)) return false;
        }

        // Las poses de XRHands están en session-local space (igual que el HMD),
        // sin la inversión X/Z específica de los trackers VIVE.
        // Convertir a mundo con el mismo TrackingParent que usa TrackerBodyCalibration.
        Transform parent = calibration != null ? calibration.TrackingParent : null;
        if (parent != null)
        {
            pos = parent.TransformPoint(pose.position);
            rot = parent.rotation * pose.rotation;
        }
        else
        {
            pos = pose.position;
            rot = pose.rotation;
        }
        return true;
    }
}
