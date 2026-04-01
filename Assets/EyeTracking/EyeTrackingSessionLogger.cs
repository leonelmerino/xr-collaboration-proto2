using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

public class EyeTrackingSessionLogger : MonoBehaviour
{
    public ViveEyeTrackingProvider eyeProvider;
    public GazeTargetRaycaster raycaster;
    public Transform headTransform;

    [Header("Session Info")]
    public string participantId = "P001";
    public string sessionId = "S001";
    public string taskId = "task_01";
    public string trialId = "trial_01";
    public string condition = "baseline";

    [Header("Logging")]
    public bool autoStart = true;
    public bool flushEveryFrame = false;

    private StreamWriter writer;
    private bool isLogging;
    private string filePath;
    private int sampleIndex = 0;

    private void Start()
    {
        if (eyeProvider == null) eyeProvider = FindObjectOfType<ViveEyeTrackingProvider>();
        if (raycaster == null) raycaster = FindObjectOfType<GazeTargetRaycaster>();
        if (headTransform == null && Camera.main != null) headTransform = Camera.main.transform;

        if (autoStart) StartLogging();
    }

    public void StartLogging()
    {
        if (isLogging) return;

        string folder = Path.Combine(Application.persistentDataPath, "EyeTrackingLogs", participantId, sessionId);
        Directory.CreateDirectory(folder);

        string prefix = $"{taskId}_{trialId}_";
        string[] existing = Directory.GetFiles(folder, $"{prefix}*_gaze.csv");

        int nextIndex = existing
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Select(name => name.Replace(prefix, "").Replace("_gaze", ""))
            .Select(text => int.TryParse(text, out int n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        filePath = Path.Combine(folder, $"{prefix}{nextIndex:000}_gaze.csv");
        writer = new StreamWriter(filePath, false, Encoding.UTF8);

        writer.WriteLine(
            "sample_index,timestamp_rel_s,timestamp_utc_iso," +
            "participant_id,session_id,task_id,trial_id,condition," +
            "combined_valid,combined_origin_x,combined_origin_y,combined_origin_z,combined_dir_x,combined_dir_y,combined_dir_z," +
            "vergence_angle_deg,interocular_distance," +
            "left_valid,left_origin_x,left_origin_y,left_origin_z,left_dir_x,left_dir_y,left_dir_z," +
            "right_valid,right_origin_x,right_origin_y,right_origin_z,right_dir_x,right_dir_y,right_dir_z," +
            "left_pupil_diameter,right_pupil_diameter," +
            "left_pupil_pos_x,left_pupil_pos_y,right_pupil_pos_x,right_pupil_pos_y," +
            "left_eye_openness,right_eye_openness," +
            "head_x,head_y,head_z,head_qx,head_qy,head_qz,head_qw," +
            "hit_valid,hit_object_name,hit_aoi,hit_aoi_type,hit_x,hit_y,hit_z"
        );

        isLogging = true;
        sampleIndex = 0;

        Debug.Log($"[EyeTrackingLogger] Logging to: {filePath}");
    }

    private void Update()
    {
        if (!isLogging || eyeProvider == null) return;

        var c = CultureInfo.InvariantCulture;
        sampleIndex++;

        Vector3 hp = headTransform ? headTransform.position : Vector3.zero;
        Quaternion hr = headTransform ? headTransform.rotation : Quaternion.identity;

        writer.WriteLine(string.Join(",",
            sampleIndex.ToString(c),
            eyeProvider.TimestampRelativeSeconds.ToString(c),
            Csv(eyeProvider.TimestampUtcIso),

            Csv(participantId),
            Csv(sessionId),
            Csv(taskId),
            Csv(trialId),
            Csv(condition),

            B(eyeProvider.CombinedGazeValid),
            V3x(eyeProvider.CombinedGazeValid, eyeProvider.CombinedOrigin),
            V3y(eyeProvider.CombinedGazeValid, eyeProvider.CombinedOrigin),
            V3z(eyeProvider.CombinedGazeValid, eyeProvider.CombinedOrigin),
            V3x(eyeProvider.CombinedGazeValid, eyeProvider.CombinedDirection),
            V3y(eyeProvider.CombinedGazeValid, eyeProvider.CombinedDirection),
            V3z(eyeProvider.CombinedGazeValid, eyeProvider.CombinedDirection),

            NF(eyeProvider.VergenceAngleDeg),
            NF(eyeProvider.InterocularDistance),

            B(eyeProvider.LeftGazeValid),
            V3x(eyeProvider.LeftGazeValid, eyeProvider.LeftOrigin),
            V3y(eyeProvider.LeftGazeValid, eyeProvider.LeftOrigin),
            V3z(eyeProvider.LeftGazeValid, eyeProvider.LeftOrigin),
            V3x(eyeProvider.LeftGazeValid, eyeProvider.LeftDirection),
            V3y(eyeProvider.LeftGazeValid, eyeProvider.LeftDirection),
            V3z(eyeProvider.LeftGazeValid, eyeProvider.LeftDirection),

            B(eyeProvider.RightGazeValid),
            V3x(eyeProvider.RightGazeValid, eyeProvider.RightOrigin),
            V3y(eyeProvider.RightGazeValid, eyeProvider.RightOrigin),
            V3z(eyeProvider.RightGazeValid, eyeProvider.RightOrigin),
            V3x(eyeProvider.RightGazeValid, eyeProvider.RightDirection),
            V3y(eyeProvider.RightGazeValid, eyeProvider.RightDirection),
            V3z(eyeProvider.RightGazeValid, eyeProvider.RightDirection),

            NF(eyeProvider.LeftPupilDiameter),
            NF(eyeProvider.RightPupilDiameter),

            NF(eyeProvider.LeftPupilPosition?.x),
            NF(eyeProvider.LeftPupilPosition?.y),
            NF(eyeProvider.RightPupilPosition?.x),
            NF(eyeProvider.RightPupilPosition?.y),

            NF(eyeProvider.LeftEyeOpenness),
            NF(eyeProvider.RightEyeOpenness),

            F(hp.x), F(hp.y), F(hp.z),
            F(hr.x), F(hr.y), F(hr.z), F(hr.w),

            B(raycaster != null && raycaster.HasHit),
            Csv(raycaster != null ? raycaster.HitObjectName : ""),
            Csv(raycaster != null ? raycaster.HitAOI : ""),
            Csv(raycaster != null ? raycaster.HitAOIType : ""),
            raycaster != null && raycaster.HasHit ? F(raycaster.HitPoint.x) : "",
            raycaster != null && raycaster.HasHit ? F(raycaster.HitPoint.y) : "",
            raycaster != null && raycaster.HasHit ? F(raycaster.HitPoint.z) : ""
        ));

        if (flushEveryFrame)
            writer.Flush();

        string F(float v) => v.ToString(c);
        string NF(float? v) => v.HasValue ? v.Value.ToString(c) : "";
        string B(bool b) => b ? "1" : "0";
        string V3x(bool valid, Vector3 v) => valid ? v.x.ToString(c) : "";
        string V3y(bool valid, Vector3 v) => valid ? v.y.ToString(c) : "";
        string V3z(bool valid, Vector3 v) => valid ? v.z.ToString(c) : "";
    }

    private string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public void StopLogging()
    {
        if (!isLogging) return;

        writer.Flush();
        writer.Close();
        writer = null;
        isLogging = false;

        Debug.Log("[EyeTrackingLogger] Logging stopped.");
    }

    private void OnApplicationQuit()
    {
        StopLogging();
    }
}