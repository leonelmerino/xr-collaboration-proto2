using UnityEngine;
using UnityEngine.XR.Hands;
using System.Collections.Generic;

public class PinchDebugVisualizer : MonoBehaviour
{
    XRHandSubsystem handSubsystem;

    public Transform thumbMarker;
    public Transform indexMarker;
    public Transform pinchMarker;

    [SerializeField] float pinchThreshold = 0.025f;

    bool isPinching = false;

    void Start()
    {
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
        var hand = subsystem.rightHand;

        if (!hand.isTracked)
        {
            pinchMarker.gameObject.SetActive(false);
            return;
        }

        var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
        var index = hand.GetJoint(XRHandJointID.IndexTip);

        if (!thumb.TryGetPose(out var thumbPose)) return;
        if (!index.TryGetPose(out var indexPose)) return;

        thumbMarker.position = thumbPose.position;
        indexMarker.position = indexPose.position;

        float dist = Vector3.Distance(
            thumbPose.position,
            indexPose.position
        );

        bool pinchNow = dist < pinchThreshold;

        if (pinchNow)
        {
            pinchMarker.gameObject.SetActive(true);
            pinchMarker.position =
                (thumbPose.position + indexPose.position) * 0.5f;
        }
        else
        {
            pinchMarker.gameObject.SetActive(false);
        }

        isPinching = pinchNow;
    }
}