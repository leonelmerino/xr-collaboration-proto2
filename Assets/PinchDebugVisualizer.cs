using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using Unity.XR.CoreUtils;

public class PinchDebugVisualizer : MonoBehaviour
{
    private XRHandSubsystem handSubsystem;
    private XROrigin xrOrigin;
    private Transform trackingRoot;

    [Header("Right Hand")]
    public Transform rightThumb;
    public Transform rightIndex;
    public Transform rightPinch;
    public LineRenderer rightPinchLine;
    public Transform rightRayOrigin;
    public JengaRayGrabInteractor rightRayGrabInteractor;

    [Header("Left Hand")]
    public Transform leftThumb;
    public Transform leftIndex;
    public Transform leftPinch;
    public LineRenderer leftPinchLine;
    public Transform leftRayOrigin;
    public JengaRayGrabInteractor leftRayGrabInteractor;

    [Header("Pinch Settings")]
    public float pinchThreshold = 0.025f;

    void Start()
    {
        List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);

        if (subsystems.Count > 0)
        {
            handSubsystem = subsystems[0];
            handSubsystem.updatedHands += OnHandsUpdated;
            Debug.Log("XRHandSubsystem connected.");
        }
        else
        {
            Debug.LogWarning("No XRHandSubsystem found.");
        }

        xrOrigin = FindObjectOfType<XROrigin>();
        if (xrOrigin != null)
        {
            trackingRoot = xrOrigin.transform;
            Debug.Log("XROrigin found: " + xrOrigin.name);
        }
        else
        {
            Debug.LogWarning("No XROrigin found.");
        }
    }

    void OnDestroy()
    {
        if (handSubsystem != null)
            handSubsystem.updatedHands -= OnHandsUpdated;
    }

    void OnHandsUpdated(
        XRHandSubsystem subsystem,
        XRHandSubsystem.UpdateSuccessFlags flags,
        XRHandSubsystem.UpdateType updateType)
    {
        UpdateSingleHand(
            subsystem.rightHand,
            rightThumb,
            rightIndex,
            rightPinch,
            rightPinchLine,
            rightRayOrigin,
            rightRayGrabInteractor
        );

        UpdateSingleHand(
            subsystem.leftHand,
            leftThumb,
            leftIndex,
            leftPinch,
            leftPinchLine,
            leftRayOrigin,
            leftRayGrabInteractor
        );
    }

    void UpdateSingleHand(
        XRHand hand,
        Transform thumbMarker,
        Transform indexMarker,
        Transform pinchMarker,
        LineRenderer pinchLine,
        Transform rayOrigin,
        JengaRayGrabInteractor rayGrabInteractor)
    {
        if (thumbMarker == null || indexMarker == null || pinchMarker == null)
            return;

        if (!hand.isTracked)
        {
            thumbMarker.gameObject.SetActive(false);
            indexMarker.gameObject.SetActive(false);
            pinchMarker.gameObject.SetActive(false);

            if (pinchLine != null)
                pinchLine.enabled = false;

            if (rayOrigin != null)
                rayOrigin.gameObject.SetActive(false);

            if (rayGrabInteractor != null)
                rayGrabInteractor.SetPinchState(false);

            return;
        }

        var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
        var indexTip = hand.GetJoint(XRHandJointID.IndexTip);
        var indexProximal = hand.GetJoint(XRHandJointID.IndexProximal);
        var wrist = hand.GetJoint(XRHandJointID.Wrist);

        if (!thumb.TryGetPose(out Pose thumbPose) ||
            !indexTip.TryGetPose(out Pose indexTipPose))
        {
            return;
        }

        Vector3 thumbWorld = thumbPose.position;
        Vector3 indexWorld = indexTipPose.position;
        Vector3 rayReferenceWorld = indexWorld;

        if (trackingRoot != null)
        {
            thumbWorld = trackingRoot.TransformPoint(thumbPose.position);
            indexWorld = trackingRoot.TransformPoint(indexTipPose.position);
        }

        // Try to use index proximal for finger direction
        bool hasProximal = indexProximal.TryGetPose(out Pose indexProximalPose);
        bool hasWrist = wrist.TryGetPose(out Pose wristPose);

        if (hasProximal)
        {
            rayReferenceWorld = trackingRoot != null
                ? trackingRoot.TransformPoint(indexProximalPose.position)
                : indexProximalPose.position;
        }
        else if (hasWrist)
        {
            rayReferenceWorld = trackingRoot != null
                ? trackingRoot.TransformPoint(wristPose.position)
                : wristPose.position;
        }

        thumbMarker.gameObject.SetActive(true);
        indexMarker.gameObject.SetActive(true);

        thumbMarker.position = thumbWorld;
        indexMarker.position = indexWorld;

        if (pinchLine != null)
        {
            pinchLine.enabled = true;
            pinchLine.positionCount = 2;
            pinchLine.SetPosition(0, thumbWorld);
            pinchLine.SetPosition(1, indexWorld);
        }

        float dist = Vector3.Distance(thumbWorld, indexWorld);
        bool isPinching = dist < pinchThreshold;

        if (isPinching)
        {
            pinchMarker.gameObject.SetActive(true);
            pinchMarker.position = (thumbWorld + indexWorld) * 0.5f;
        }
        else
        {
            pinchMarker.gameObject.SetActive(false);
        }

        // Ray origin and direction
        /*if (rayOrigin != null)
        {
            rayOrigin.gameObject.SetActive(true);
            rayOrigin.position = indexWorld;

            Vector3 fingerDir = (indexWorld - rayReferenceWorld).normalized;

            if (fingerDir.sqrMagnitude > 0.0001f)
            {
                rayOrigin.rotation = Quaternion.LookRotation(fingerDir, Vector3.up);
            }
        }*/
        var palm = hand.GetJoint(XRHandJointID.Palm);
        var indexProx = hand.GetJoint(XRHandJointID.IndexProximal);
        var thumbProx = hand.GetJoint(XRHandJointID.ThumbProximal);

        if (palm.TryGetPose(out Pose palmPose))
        {
            Vector3 palmWorld = trackingRoot != null
                ? trackingRoot.TransformPoint(palmPose.position)
                : palmPose.position;

            Vector3 indexWorld = indexProx.TryGetPose(out Pose ipPose)
                ? (trackingRoot != null ? trackingRoot.TransformPoint(ipPose.position) : ipPose.position)
                : palmWorld + Vector3.forward;

            Vector3 thumbWorld = thumbProx.TryGetPose(out Pose tpPose)
                ? (trackingRoot != null ? trackingRoot.TransformPoint(tpPose.position) : tpPose.position)
                : palmWorld + Vector3.right;

            // vectors on the palm plane
            Vector3 v1 = (indexWorld - palmWorld).normalized;
            Vector3 v2 = (thumbWorld - palmWorld).normalized;

            // palm normal (this is the ray direction)
            Vector3 palmNormal = Vector3.Cross(v1, v2).normalized;

            // IMPORTANT: flip if pointing backwards
            if (Vector3.Dot(palmNormal, (indexWorld - palmWorld)) < 0)
                palmNormal = -palmNormal;

            if (rayOrigin != null)
            {
                rayOrigin.gameObject.SetActive(true);
                rayOrigin.position = palmWorld;
                rayOrigin.rotation = Quaternion.LookRotation(palmNormal, Vector3.up);
            }
        }

        if (rayGrabInteractor != null)
            rayGrabInteractor.SetPinchState(isPinching);
    }
}