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
        var index = hand.GetJoint(XRHandJointID.IndexTip);

        if (!thumb.TryGetPose(out Pose thumbPose) ||
            !index.TryGetPose(out Pose indexPose))
        {
            return;
        }

        // Convertir a world space usando el XR Origin
        Vector3 thumbWorld = thumbPose.position;
        Vector3 indexWorld = indexPose.position;

        if (trackingRoot != null)
        {
            thumbWorld = trackingRoot.TransformPoint(thumbPose.position);
            indexWorld = trackingRoot.TransformPoint(indexPose.position);
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

        // Ray origin sale desde la punta del índice
        if (rayOrigin != null)
        {
            rayOrigin.gameObject.SetActive(true);
            rayOrigin.position = indexWorld;

            if (Camera.main != null)
            {
                rayOrigin.rotation = Camera.main.transform.rotation;
            }
        }

        if (rayGrabInteractor != null)
            rayGrabInteractor.SetPinchState(isPinching);
    }
}