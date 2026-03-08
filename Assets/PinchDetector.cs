using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.SubsystemsImplementation;
using System.Collections.Generic;

public class PinchDetector : MonoBehaviour
{
    XRHandSubsystem handSubsystem;

    bool rightPinching = false;
    bool leftPinching = false;

    [SerializeField] float pinchThreshold = 0.025f;

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
        CheckHand(subsystem.rightHand, ref rightPinching, "RIGHT");
        CheckHand(subsystem.leftHand, ref leftPinching, "LEFT");
    }

    void CheckHand(XRHand hand, ref bool wasPinching, string label)
    {
        if (!hand.isTracked)
        {
            wasPinching = false;
            return;
        }

        var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
        var index = hand.GetJoint(XRHandJointID.IndexTip);

        if (!thumb.TryGetPose(out var thumbPose)) return;
        if (!index.TryGetPose(out var indexPose)) return;

        float dist = Vector3.Distance(thumbPose.position, indexPose.position);
        bool isPinching = dist < pinchThreshold;

        if (isPinching && !wasPinching)
        {
            Debug.Log(label + " PINCH START");
        }

        if (!isPinching && wasPinching)
        {
            Debug.Log(label + " PINCH END");
        }

        wasPinching = isPinching;
    }
}