using System;
using UnityEngine;
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

    private void Update()
    {
        TimestampRelativeSeconds = Time.realtimeSinceStartupAsDouble;

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
            XR_HTC_eye_tracker.Interop.GetEyeGazeData(out XrSingleEyeGazeDataHTC[] gazeData);

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