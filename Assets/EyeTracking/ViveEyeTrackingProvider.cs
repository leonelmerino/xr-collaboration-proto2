using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using VIVE.OpenXR;
using VIVE.OpenXR.EyeTracker;

public class ViveEyeTrackingProvider : MonoBehaviour
{
    [Header("Debug")]
    public bool drawDebugRays = true;
    public bool logOccasionalErrors = true;
    public float debugRayLength = 5f;
    public float errorLogCooldown = 2.0f;

    public double TimestampRelativeSeconds { get; private set; }
    public string TimestampUtcIso => DateTime.UtcNow.ToString("o");

    public bool LeftGazeValid { get; private set; }
    public bool RightGazeValid { get; private set; }
    public bool CombinedGazeValid { get; private set; }

    public Vector3 LeftOrigin { get; private set; }
    public Vector3 LeftDirection { get; private set; }

    public Vector3 RightOrigin { get; private set; }
    public Vector3 RightDirection { get; private set; }

    public Vector3 CombinedOrigin { get; private set; }
    public Vector3 CombinedDirection { get; private set; }

    public float? LeftPupilDiameter { get; private set; }
    public float? RightPupilDiameter { get; private set; }

    public Vector2? LeftPupilPosition { get; private set; }
    public Vector2? RightPupilPosition { get; private set; }

    public float? LeftEyeOpenness { get; private set; }
    public float? RightEyeOpenness { get; private set; }

    public float? VergenceAngleDeg { get; private set; }
    public float? InterocularDistance { get; private set; }

    private float lastErrorLogTime = -999f;

    /// <summary>Hay eye tracker de VIVE creado (false mientras la app lo reintenta; ver trackerMissing).</summary>
    public bool TrackerReady => IsXrSessionReady() && !trackerMissing;

    // El plugin de VIVE crea el eye tracker una sola vez, al crear la sesión. Y la capa de eye tracking de VIVE
    // Streaming decide si hay eye tracking una sola vez, al crear la instancia OpenXR: si en ese momento el visor
    // está en reposo (nadie lo tiene puesto, o está en la frente), RRServer dice "VBS 0" y queda
    // supportsEyeTracking: 0 hasta cerrar la app; cada lectura tiraba NullReference.
    // Arreglo en dos pasos, sin intervención:
    //   1. Mientras no haya tracker, se reintenta crearlo cada RetrySeconds (barato; casi siempre alcanza: VIVE
    //      pasa a "VBS 1" unos segundos después de que alguien se pone el visor).
    //   2. Si con el visor puesto (sesión FOCUSED) sigue sin tracker durante FocusedSeconds, la app se reabre sola
    //      una vez (cierra limpio, guarda los CSV, y un proceso aparte la vuelve a abrir con los mismos argumentos
    //      unos segundos después, ya con el visor despierto). Antes se reiniciaba XR dentro de la app
    //      (DeinitializeLoader/InitializeLoader): el 2026-10-13 eso hizo caer la app al apagar XR. No volver a eso.
    private const float RetrySeconds = 2f;
    private const float FocusedSeconds = 20f;
    private const string RelaunchedFlag = "-relaunched-for-eyes";
    private const int XrSessionStateFocused = 5;
    private float nextCreateAttempt;
    private bool trackerMissing;
    private float focusedSince = -1f;
    private static bool s_relaunching;

    private static readonly List<XRDisplaySubsystem> s_displaySubsystems = new();

    private static bool IsXrSessionReady()
    {
        // 1) Loader XR debe estar inicializado.
        var settings = XRGeneralSettings.Instance;
        if (settings == null || settings.Manager == null) return false;
        if (!settings.Manager.isInitializationComplete) return false;
        if (settings.Manager.activeLoader == null) return false;

        // 2) Y debe haber un XRDisplaySubsystem corriendo. Esto solo es true
        //    despues que StartSubsystems() corrio y la sesion OpenXR esta activa.
        //    Antes de eso, el eye tracker tira XR_ERROR_SESSION_LOST.
        s_displaySubsystems.Clear();
        SubsystemManager.GetSubsystems(s_displaySubsystems);
        for (int i = 0; i < s_displaySubsystems.Count; i++)
        {
            if (s_displaySubsystems[i].running)
                return true;
        }
        return false;
    }

    private void Update()
    {
        TimestampRelativeSeconds = Time.realtimeSinceStartupAsDouble;

        // No leer eye-tracker hasta que XR este inicializado.
        // Esto evita spam de XR_ERROR_SESSION_LOST durante el bootstrap de OpenXR.
        if (!IsXrSessionReady())
            return;

        if (trackerMissing && !TryCreateEyeTracker())
        {
            MaybeRelaunch();
            return;
        }
        focusedSince = -1f;

        ReadGazeDataSafe();
        ReadPupilDataSafe();
        ReadGeometricDataSafe();

        if (drawDebugRays)
        {
            if (CombinedGazeValid)
                Debug.DrawRay(CombinedOrigin, CombinedDirection * debugRayLength, Color.cyan);

            if (LeftGazeValid)
                Debug.DrawRay(LeftOrigin, LeftDirection * debugRayLength, Color.green);

            if (RightGazeValid)
                Debug.DrawRay(RightOrigin, RightDirection * debugRayLength, Color.magenta);
        }
    }

    private void ReadGazeDataSafe()
    {
        LeftGazeValid = false;
        RightGazeValid = false;
        CombinedGazeValid = false;

        LeftOrigin = Vector3.zero;
        LeftDirection = Vector3.zero;
        RightOrigin = Vector3.zero;
        RightDirection = Vector3.zero;
        CombinedOrigin = Vector3.zero;
        CombinedDirection = Vector3.zero;

        VergenceAngleDeg = null;
        InterocularDistance = null;

        try
        {
            if (!XR_HTC_eye_tracker.Interop.GetEyeGazeData(out XrSingleEyeGazeDataHTC[] gazeData) || gazeData == null)
            {
                trackerMissing = true;
                return;
            }

            var left = gazeData[(int)XrEyePositionHTC.XR_EYE_POSITION_LEFT_HTC];
            var right = gazeData[(int)XrEyePositionHTC.XR_EYE_POSITION_RIGHT_HTC];

            if (left.isValid)
            {
                LeftGazeValid = true;
                LeftOrigin = left.gazePose.position.ToUnityVector();
                LeftDirection = (left.gazePose.orientation.ToUnityQuaternion() * Vector3.forward).normalized;
            }

            if (right.isValid)
            {
                RightGazeValid = true;
                RightOrigin = right.gazePose.position.ToUnityVector();
                RightDirection = (right.gazePose.orientation.ToUnityQuaternion() * Vector3.forward).normalized;
            }

            if (LeftGazeValid && RightGazeValid)
            {
                CombinedGazeValid = true;
                CombinedOrigin = (LeftOrigin + RightOrigin) * 0.5f;
                CombinedDirection = (LeftDirection + RightDirection).normalized;

                VergenceAngleDeg = Vector3.Angle(LeftDirection, RightDirection);
                InterocularDistance = Vector3.Distance(LeftOrigin, RightOrigin);
            }
            else if (LeftGazeValid)
            {
                CombinedGazeValid = true;
                CombinedOrigin = LeftOrigin;
                CombinedDirection = LeftDirection;
            }
            else if (RightGazeValid)
            {
                CombinedGazeValid = true;
                CombinedOrigin = RightOrigin;
                CombinedDirection = RightDirection;
            }
        }
        catch (Exception e)
        {
            ThrottledError($"[EyeTracking] Gaze read failed: {e.Message}");
        }
    }

    // Apagado (p. ej. LaunchDisable «--es off ViveEyeTrackingProvider»): suelta el eye tracker del runtime para que
    // el servicio de ojos del visor deje de trabajar. Al volver a habilitar, Update lo crea de nuevo.
    private void OnDisable()
    {
        var feature = OpenXRSettings.Instance != null ? OpenXRSettings.Instance.GetFeature<ViveEyeTracker>() : null;
        if (feature != null && feature.enabled && feature.DestroyEyeTracker()) Debug.Log("[EyeTracking] Eye tracker destroyed (provider disabled).");
        trackerMissing = true;
    }

    /// <summary>Reintenta crear el eye tracker (cada RetrySeconds). True si ya hay tracker.</summary>
    private bool TryCreateEyeTracker()
    {
        if (Time.unscaledTime < nextCreateAttempt) return false;
        nextCreateAttempt = Time.unscaledTime + RetrySeconds;
        var feature = OpenXRSettings.Instance != null ? OpenXRSettings.Instance.GetFeature<ViveEyeTracker>() : null;
        if (feature == null || !feature.enabled)
        {
            ThrottledError("[EyeTracking] VIVE XR Eye Tracker feature not enabled: no gaze.");
            return false;
        }
        if (!feature.CreateEyeTracker())
        {
            ThrottledError("[EyeTracking] No eye tracker yet (runtime reports no eye tracking); retrying every 2 s.");
            return false;
        }
        Debug.Log("[EyeTracking] Eye tracker created; gaze is live.");
        trackerMissing = false;
        return true;
    }

    /// <summary>Paso 2: con el visor puesto (FOCUSED) y sin tracker por FocusedSeconds → reabrir la app una vez.</summary>
    private void MaybeRelaunch()
    {
        if (s_relaunching) return;
        if (PassthroughUnderlayFeature.SessionState != XrSessionStateFocused) { focusedSince = -1f; return; }
        if (focusedSince < 0f) focusedSince = Time.unscaledTime;
        if (Time.unscaledTime - focusedSince < FocusedSeconds) return;
        string[] args = Environment.GetCommandLineArgs();
        if (Application.isEditor || Array.IndexOf(args, RelaunchedFlag) >= 0)
        {
            ThrottledError($"[EyeTracking] Headset on for {FocusedSeconds:0} s and VIVE still gives no eye tracker: close and reopen the app.");
            return;
        }
        s_relaunching = true;
        try
        {
            // Un cmd aparte espera ~4 s (que esta app ya haya cerrado su sesión XR: nunca dos clientes OpenXR a la
            // vez, ver vive-921) y la abre de nuevo con los mismos argumentos.
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i < args.Length; i++) sb.Append(" \"").Append(args[i].Replace("\"", "")).Append('"');
            sb.Append(' ').Append(RelaunchedFlag);
            // UseShellExecute = true: el cmd NO hereda los handles de esta app. Con false heredaba el socket del canal
            // de control (127.0.0.1:47811) y la app nueva no podía abrirlo: el panel la veía cerrada (2026-10-13).
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping -n 5 127.0.0.1 >nul & start \"\" \"{exe}\"{sb}")
            {
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            System.Diagnostics.Process.Start(psi);
            Debug.LogWarning($"[EyeTracking] Headset on for {FocusedSeconds:0} s and VIVE still gives no eye tracker (XR started while it was asleep): reopening the app once.");
            Application.Quit();
        }
        catch (Exception e)
        {
            s_relaunching = false;
            ThrottledError($"[EyeTracking] Could not reopen the app ({e.Message}): close and reopen it.");
        }
    }

    private void ReadPupilDataSafe()
    {
        LeftPupilDiameter = null;
        RightPupilDiameter = null;
        LeftPupilPosition = null;
        RightPupilPosition = null;

        try
        {
            XR_HTC_eye_tracker.Interop.GetEyePupilData(out XrSingleEyePupilDataHTC[] pupilData);

            var left = pupilData[(int)XrEyePositionHTC.XR_EYE_POSITION_LEFT_HTC];
            var right = pupilData[(int)XrEyePositionHTC.XR_EYE_POSITION_RIGHT_HTC];

            if (left.isDiameterValid) LeftPupilDiameter = left.pupilDiameter;
            if (right.isDiameterValid) RightPupilDiameter = right.pupilDiameter;

            if (left.isPositionValid) LeftPupilPosition = new Vector2(left.pupilPosition.x, left.pupilPosition.y);
            if (right.isPositionValid) RightPupilPosition = new Vector2(right.pupilPosition.x, right.pupilPosition.y);
        }
        catch (Exception e)
        {
            ThrottledError($"[EyeTracking] Pupil read failed: {e.Message}");
        }
    }

    private void ReadGeometricDataSafe()
    {
        LeftEyeOpenness = null;
        RightEyeOpenness = null;

        try
        {
            XR_HTC_eye_tracker.Interop.GetEyeGeometricData(out XrSingleEyeGeometricDataHTC[] geomData);

            var left = geomData[(int)XrEyePositionHTC.XR_EYE_POSITION_LEFT_HTC];
            var right = geomData[(int)XrEyePositionHTC.XR_EYE_POSITION_RIGHT_HTC];

            if (left.isValid) LeftEyeOpenness = left.eyeOpenness;
            if (right.isValid) RightEyeOpenness = right.eyeOpenness;
        }
        catch (Exception e)
        {
            ThrottledError($"[EyeTracking] Geometric read failed: {e.Message}");
        }
    }

    private void ThrottledError(string message)
    {
        if (!logOccasionalErrors) return;

        if (Time.unscaledTime - lastErrorLogTime >= errorLogCooldown)
        {
            Debug.LogWarning(message);
            lastErrorLogTime = Time.unscaledTime;
        }
    }
}