using UnityEngine;
using UnityEngine.XR.Hands;
using Unity.XR.CoreUtils;
using System.Collections.Generic;

public class PinchDebugVisualizer : MonoBehaviour
{
    XRHandSubsystem handSubsystem;
    XROrigin xrOrigin;
    Transform hmd;

    public Transform rightThumb;
    public Transform rightIndex;
    public Transform rightPinch;
    public LineRenderer rightPinchLine;

    public Transform leftThumb;
    public Transform leftIndex;
    public Transform leftPinch;
    public LineRenderer leftPinchLine;

    public float pinchThreshold = 0.025f;

    void Start()
    {
        xrOrigin = FindObjectOfType<XROrigin>();

        if (xrOrigin != null && xrOrigin.Camera != null)
        {
            hmd = xrOrigin.Camera.transform;
        }

        List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);

        if (subsystems.Count > 0)
        {
            handSubsystem = subsystems[0];
            handSubsystem.updatedHands += OnHandsUpdated;
        }
    }

    void OnDestroy()
    {
        if (handSubsystem != null)
            handSubsystem.updatedHands -= OnHandsUpdated;
    }

    void OnHandsUpdated(XRHandSubsystem subsystem,
        XRHandSubsystem.UpdateSuccessFlags flags,
        XRHandSubsystem.UpdateType updateType)
    {
        UpdateHand(subsystem.rightHand, rightThumb, rightIndex, rightPinch, rightPinchLine);
        UpdateHand(subsystem.leftHand, leftThumb, leftIndex, leftPinch, leftPinchLine);
    }

    void UpdateHand(XRHand hand,
                    Transform thumbMarker,
                    Transform indexMarker,
                    Transform pinchMarker,
                    LineRenderer pinchLine)
    {
        if (!hand.isTracked)
        {
            thumbMarker.gameObject.SetActive(false);
            indexMarker.gameObject.SetActive(false);
            pinchMarker.gameObject.SetActive(false);

            if (pinchLine != null)
                pinchLine.enabled = false;

            return;
        }

        var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
        var index = hand.GetJoint(XRHandJointID.IndexTip);

        if (!thumb.TryGetPose(out var thumbPose)) return;
        if (!index.TryGetPose(out var indexPose)) return;

        Vector3 thumbWorld = thumbPose.position;
        Vector3 indexWorld = indexPose.position;

        if (hmd != null)
        {
            Vector3 trackingToWorldOffset = hmd.position - hmd.localPosition;
            thumbWorld = trackingToWorldOffset + thumbPose.position;
            indexWorld = trackingToWorldOffset + indexPose.position;
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

        if (dist < pinchThreshold)
        {
            pinchMarker.gameObject.SetActive(true);
            pinchMarker.position = (thumbWorld + indexWorld) * 0.5f;
        }
        else
        {
            pinchMarker.gameObject.SetActive(false);
        }
    }
}