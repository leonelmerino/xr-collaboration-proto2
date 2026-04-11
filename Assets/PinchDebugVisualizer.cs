using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.SubsystemsImplementation;

public class PinchDebugVisualizer : MonoBehaviour
{
    [Header("Right Hand")]
    [SerializeField] private Transform rightThumb;
    [SerializeField] private Transform rightIndex;
    [SerializeField] private Transform rightPinch;
    [SerializeField] private LineRenderer rightPinchLine;
    [SerializeField] private Transform rightRayOrigin;
    [SerializeField] private JengaRayGrabInteractor rightRayGrabInteractor;

    [Header("Left Hand")]
    [SerializeField] private Transform leftThumb;
    [SerializeField] private Transform leftIndex;
    [SerializeField] private Transform leftPinch;
    [SerializeField] private LineRenderer leftPinchLine;
    [SerializeField] private Transform leftRayOrigin;
    [SerializeField] private JengaRayGrabInteractor leftRayGrabInteractor;

    [Header("Pinch Settings")]
    [SerializeField] private float pinchThreshold = 0.025f;

    [Header("Debug")]
    [SerializeField] private bool logTrackingWarnings = true;

    private XRHandSubsystem handSubsystem;

    private void Awake()
    {
        ConfigureLineRenderer(rightPinchLine);
        ConfigureLineRenderer(leftPinchLine);
    }

    private void Start()
    {
        List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);

        if (subsystems.Count == 0)
        {
            Debug.LogWarning("[PinchDebugVisualizer] No XRHandSubsystem found.");
            return;
        }

        handSubsystem = subsystems[0];
        handSubsystem.updatedHands += OnHandsUpdated;

        Debug.Log("[PinchDebugVisualizer] XRHandSubsystem connected.");
        Debug.Log("[PinchDebugVisualizer] This script expects its marker hierarchy to be under XR Origin / Camera Offset.");
    }

    private void OnDestroy()
    {
        if (handSubsystem != null)
            handSubsystem.updatedHands -= OnHandsUpdated;
    }

    private void ConfigureLineRenderer(LineRenderer line)
    {
        if (line == null)
            return;

        line.useWorldSpace = false;
        line.positionCount = 2;
        line.enabled = false;
    }

    private void OnHandsUpdated(
        XRHandSubsystem subsystem,
        XRHandSubsystem.UpdateSuccessFlags flags,
        XRHandSubsystem.UpdateType updateType)
    {
        // Unity reports hand updates twice per frame; Dynamic is the right one for physics/colliders.
        if (updateType != XRHandSubsystem.UpdateType.Dynamic)
            return;

        UpdateSingleHand(
            subsystem.rightHand,
            rightThumb,
            rightIndex,
            rightPinch,
            rightPinchLine,
            rightRayOrigin,
            rightRayGrabInteractor,
            "Right");

        UpdateSingleHand(
            subsystem.leftHand,
            leftThumb,
            leftIndex,
            leftPinch,
            leftPinchLine,
            leftRayOrigin,
            leftRayGrabInteractor,
            "Left");
    }

    private void UpdateSingleHand(
        XRHand hand,
        Transform thumbMarker,
        Transform indexMarker,
        Transform pinchMarker,
        LineRenderer pinchLine,
        Transform rayOrigin,
        JengaRayGrabInteractor rayGrabInteractor,
        string handLabel)
    {
        if (thumbMarker == null || indexMarker == null || pinchMarker == null)
            return;

        if (!hand.isTracked)
        {
            SetHandActive(
                thumbMarker,
                indexMarker,
                pinchMarker,
                pinchLine,
                rayOrigin,
                false);

            if (rayGrabInteractor != null)
                rayGrabInteractor.SetPinchState(false);

            return;
        }

        XRHandJoint thumbTip = hand.GetJoint(XRHandJointID.ThumbTip);
        XRHandJoint indexTip = hand.GetJoint(XRHandJointID.IndexTip);
        XRHandJoint palm = hand.GetJoint(XRHandJointID.Palm);
        XRHandJoint indexProximal = hand.GetJoint(XRHandJointID.IndexProximal);
        XRHandJoint thumbProximal = hand.GetJoint(XRHandJointID.ThumbProximal);

        if (!thumbTip.TryGetPose(out Pose thumbPose) || !indexTip.TryGetPose(out Pose indexPose))
        {
            if (logTrackingWarnings)
                Debug.LogWarning($"[PinchDebugVisualizer] Missing tip pose for {handLabel} hand.");
            return;
        }

        SetHandActive(
            thumbMarker,
            indexMarker,
            pinchMarker,
            pinchLine,
            rayOrigin,
            true);

        // Local-space assignment: this assumes the hierarchy is under XR Origin / Camera Offset.
        thumbMarker.localPosition = thumbPose.position;
        thumbMarker.localRotation = thumbPose.rotation;

        indexMarker.localPosition = indexPose.position;
        indexMarker.localRotation = indexPose.rotation;

        if (pinchLine != null)
        {
            pinchLine.enabled = true;
            pinchLine.SetPosition(0, thumbPose.position);
            pinchLine.SetPosition(1, indexPose.position);
        }

        float pinchDistance = Vector3.Distance(thumbPose.position, indexPose.position);
        bool isPinching = pinchDistance < pinchThreshold;

        if (isPinching)
        {
            pinchMarker.gameObject.SetActive(true);
            pinchMarker.localPosition = (thumbPose.position + indexPose.position) * 0.5f;
            pinchMarker.localRotation = Quaternion.identity;
        }
        else
        {
            pinchMarker.gameObject.SetActive(false);
        }

        UpdateRayOrigin(
            hand,
            palm,
            indexProximal,
            thumbProximal,
            indexPose.position,
            rayOrigin);

        if (rayGrabInteractor != null)
            rayGrabInteractor.SetPinchState(isPinching);
    }

    private void UpdateRayOrigin(
        XRHand hand,
        XRHandJoint palmJoint,
        XRHandJoint indexProximalJoint,
        XRHandJoint thumbProximalJoint,
        Vector3 indexTipLocal,
        Transform rayOrigin)
    {
        if (rayOrigin == null)
            return;

        if (!palmJoint.TryGetPose(out Pose palmPose))
        {
            rayOrigin.gameObject.SetActive(false);
            return;
        }

        Vector3 palmLocal = palmPose.position;

        Vector3 indexProxLocal = palmLocal + Vector3.forward;
        if (indexProximalJoint.TryGetPose(out Pose indexProxPose))
            indexProxLocal = indexProxPose.position;

        Vector3 thumbProxLocal = palmLocal + Vector3.right;
        if (thumbProximalJoint.TryGetPose(out Pose thumbProxPose))
            thumbProxLocal = thumbProxPose.position;

        Vector3 v1 = (indexProxLocal - palmLocal).normalized;
        Vector3 v2 = (thumbProxLocal - palmLocal).normalized;

        Vector3 palmNormal = Vector3.Cross(v1, v2).normalized;

        if (palmNormal.sqrMagnitude < 0.0001f)
        {
            rayOrigin.gameObject.SetActive(false);
            return;
        }

        if (Vector3.Dot(palmNormal, (indexTipLocal - palmLocal)) < 0f)
            palmNormal = -palmNormal;

        rayOrigin.gameObject.SetActive(true);
        rayOrigin.localPosition = palmLocal;
        rayOrigin.localRotation = Quaternion.LookRotation(palmNormal, Vector3.up);
    }

    private void SetHandActive(
        Transform thumbMarker,
        Transform indexMarker,
        Transform pinchMarker,
        LineRenderer pinchLine,
        Transform rayOrigin,
        bool active)
    {
        if (thumbMarker != null)
            thumbMarker.gameObject.SetActive(active);

        if (indexMarker != null)
            indexMarker.gameObject.SetActive(active);

        if (pinchMarker != null && !active)
            pinchMarker.gameObject.SetActive(false);

        if (pinchLine != null)
            pinchLine.enabled = active;

        if (rayOrigin != null)
            rayOrigin.gameObject.SetActive(active);
    }
}