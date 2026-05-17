using System.Collections.Generic;
using Unity.Netcode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SubsystemsImplementation;
using UnityEngine.XR.Hands;

/// <summary>
/// Sincroniza posicion / pinch / raycast de las dos manos del avatar.
/// - Solo el OWNER lee del XRHandSubsystem y de las LineRenderers locales del rig (rayos de cada mano)
///   y publica el estado a un NetworkVariable.
/// - TODOS los clientes (incluyendo el owner) leen el NetworkVariable y aplican el estado
///   a los markers y a las LineRenderers que viven bajo el Avatar prefab.
/// </summary>
public class NetworkedAvatarHands : NetworkBehaviour
{
    [Header("Left hand visuals (hijos del Avatar)")]
    [SerializeField] private GameObject leftHandRoot;
    [SerializeField] private Transform leftThumbMarker;
    [SerializeField] private Transform leftIndexMarker;
    [SerializeField] private Transform leftPinchMarker;
    [SerializeField] private LineRenderer leftPinchLine;
    [SerializeField] private LineRenderer leftRayDisplay;

    [Header("Right hand visuals (hijos del Avatar)")]
    [SerializeField] private GameObject rightHandRoot;
    [SerializeField] private Transform rightThumbMarker;
    [SerializeField] private Transform rightIndexMarker;
    [SerializeField] private Transform rightPinchMarker;
    [SerializeField] private LineRenderer rightPinchLine;
    [SerializeField] private LineRenderer rightRayDisplay;

    [Header("Local ray sources (solo se leen en el owner)")]
    [Tooltip("LineRenderer local del rig del owner que dibuja el ray izquierdo (suele ser el de JengaRayGrabInteractor). Si esta vacio se autobusca por nombre.")]
    [SerializeField] private LineRenderer leftRaySource;
    [Tooltip("LineRenderer local del rig del owner que dibuja el ray derecho.")]
    [SerializeField] private LineRenderer rightRaySource;

    [Tooltip("Nombre del GameObject de escena con el LineRenderer del ray izquierdo. Usado si leftRaySource esta vacio.")]
    [SerializeField] private string leftRaySourceName = "LeftGrabRay";
    [Tooltip("Nombre del GameObject de escena con el LineRenderer del ray derecho. Usado si rightRaySource esta vacio.")]
    [SerializeField] private string rightRaySourceName = "RightGrabRay";

    [Header("Settings")]
    [SerializeField] private float pinchThreshold = 0.025f;
    [Tooltip("Si se deja vacio, busca FindObjectOfType<XROrigin>() en el owner para convertir poses a world space.")]
    [SerializeField] private XROrigin overrideOriginForConversion;

    private NetworkVariable<HandPoseState> LeftHand = new NetworkVariable<HandPoseState>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private NetworkVariable<HandPoseState> RightHand = new NetworkVariable<HandPoseState>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private XRHandSubsystem handSubsystem;
    private Transform conversionSpace;

    public override void OnNetworkSpawn()
    {
        LogWiringStatus();
        LeftHand.OnValueChanged += OnLeftChanged;
        RightHand.OnValueChanged += OnRightChanged;

        ApplyHandState(LeftHand.Value, leftHandRoot, leftThumbMarker, leftIndexMarker, leftPinchMarker, leftPinchLine, leftRayDisplay);
        ApplyHandState(RightHand.Value, rightHandRoot, rightThumbMarker, rightIndexMarker, rightPinchMarker, rightPinchLine, rightRayDisplay);

        if (IsOwner)
        {
            ResolveConversionSpace();
            TryAttachHandSubsystem();
            AutoWireRaySources();
        }
    }

    private bool autoWireLogged;

    private void AutoWireRaySources()
    {
        if (leftRaySource == null && !string.IsNullOrEmpty(leftRaySourceName))
            leftRaySource = FindLineRendererByName(leftRaySourceName);

        if (rightRaySource == null && !string.IsNullOrEmpty(rightRaySourceName))
            rightRaySource = FindLineRendererByName(rightRaySourceName);

        if (!autoWireLogged && (leftRaySource != null || rightRaySource != null))
        {
            Debug.Log($"[NetworkedAvatarHands] AutoWire ray sources: left='{(leftRaySource ? leftRaySource.gameObject.name : "MISS")}', right='{(rightRaySource ? rightRaySource.gameObject.name : "MISS")}'");
            autoWireLogged = true;
        }
    }

    private void LogWiringStatus()
    {
        string role = IsOwner ? "owner" : "remote";
        Debug.Log(
            $"[NetworkedAvatarHands] OnNetworkSpawn ({role}) wiring: " +
            $"leftPinchLine={(leftPinchLine ? "OK" : "MISS")}, " +
            $"rightPinchLine={(rightPinchLine ? "OK" : "MISS")}, " +
            $"leftRayDisplay={(leftRayDisplay ? "OK" : "MISS")}, " +
            $"rightRayDisplay={(rightRayDisplay ? "OK" : "MISS")}, " +
            $"leftThumb={(leftThumbMarker ? "OK" : "MISS")}, " +
            $"rightThumb={(rightThumbMarker ? "OK" : "MISS")}");
    }

    private LineRenderer FindLineRendererByName(string name)
    {
        var all = FindObjectsOfType<LineRenderer>(true);
        foreach (var lr in all)
        {
            if (lr.gameObject.name == name) return lr;
        }
        // Fallback: GameObject con ese nombre que tenga LineRenderer en algun hijo.
        var go = GameObject.Find(name);
        if (go != null) return go.GetComponentInChildren<LineRenderer>(true);
        return null;
    }

    public override void OnNetworkDespawn()
    {
        LeftHand.OnValueChanged -= OnLeftChanged;
        RightHand.OnValueChanged -= OnRightChanged;
        DetachHandSubsystem();
    }

    private void OnLeftChanged(HandPoseState _, HandPoseState n)
        => ApplyHandState(n, leftHandRoot, leftThumbMarker, leftIndexMarker, leftPinchMarker, leftPinchLine, leftRayDisplay);

    private void OnRightChanged(HandPoseState _, HandPoseState n)
        => ApplyHandState(n, rightHandRoot, rightThumbMarker, rightIndexMarker, rightPinchMarker, rightPinchLine, rightRayDisplay);

    private void ResolveConversionSpace()
    {
        if (overrideOriginForConversion != null)
        {
            conversionSpace = overrideOriginForConversion.CameraFloorOffsetObject != null
                ? overrideOriginForConversion.CameraFloorOffsetObject.transform
                : overrideOriginForConversion.transform;
            return;
        }

        var origin = FindObjectOfType<XROrigin>();
        if (origin != null)
        {
            conversionSpace = origin.CameraFloorOffsetObject != null
                ? origin.CameraFloorOffsetObject.transform
                : origin.transform;
        }
    }

    private void TryAttachHandSubsystem()
    {
        if (handSubsystem != null) return;

        var list = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(list);
        if (list.Count == 0) return;

        handSubsystem = list[0];
    }

    private void DetachHandSubsystem()
    {
        handSubsystem = null;
    }

    private void Update()
    {
        if (!IsOwner) return;
        if (handSubsystem == null) { TryAttachHandSubsystem(); return; }
        if (conversionSpace == null) { ResolveConversionSpace(); return; }
        if (leftRaySource == null || rightRaySource == null) AutoWireRaySources();

        var left = BuildState(handSubsystem.leftHand, leftRaySource);
        var right = BuildState(handSubsystem.rightHand, rightRaySource);

        if (!LeftHand.Value.Equals(left)) LeftHand.Value = left;
        if (!RightHand.Value.Equals(right)) RightHand.Value = right;
    }

    private HandPoseState BuildState(XRHand hand, LineRenderer raySource)
    {
        HandPoseState s = default;

        if (hand.isTracked)
        {
            var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
            var index = hand.GetJoint(XRHandJointID.IndexTip);
            var palm = hand.GetJoint(XRHandJointID.Palm);

            if (thumb.TryGetPose(out var thumbPose)
                && index.TryGetPose(out var indexPose)
                && palm.TryGetPose(out var palmPose))
            {
                Vector3 thumbWorld = ToWorld(thumbPose.position);
                Vector3 indexWorld = ToWorld(indexPose.position);
                Vector3 palmWorld = ToWorld(palmPose.position);
                Quaternion palmWorldRot = conversionSpace != null
                    ? conversionSpace.rotation * palmPose.rotation
                    : palmPose.rotation;

                s.tracked = true;
                s.thumbTipPos = thumbWorld;
                s.indexTipPos = indexWorld;
                s.palmPos = palmWorld;
                s.palmRot = palmWorldRot;
                s.pinching = Vector3.Distance(thumbWorld, indexWorld) < pinchThreshold;
            }
        }

        // Ray state se publica aun si la mano no esta tracked, por si el rig sigue dibujando algo.
        if (raySource != null && raySource.enabled && raySource.positionCount >= 2)
        {
            s.rayActive = true;
            s.rayStart = ReadLinePoint(raySource, 0);
            s.rayEnd = ReadLinePoint(raySource, 1);
        }

        return s;
    }

    private Vector3 ReadLinePoint(LineRenderer line, int index)
    {
        Vector3 p = line.GetPosition(index);
        return line.useWorldSpace ? p : line.transform.TransformPoint(p);
    }

    private Vector3 ToWorld(Vector3 localPos)
    {
        return conversionSpace != null ? conversionSpace.TransformPoint(localPos) : localPos;
    }

    private void ApplyHandState(
        HandPoseState state,
        GameObject handRoot,
        Transform thumb,
        Transform index,
        Transform pinch,
        LineRenderer pinchLine,
        LineRenderer rayDisplay)
    {
        if (handRoot != null) handRoot.SetActive(state.tracked || state.rayActive);

        if (state.tracked)
        {
            if (thumb != null) thumb.position = state.thumbTipPos;
            if (index != null) index.position = state.indexTipPos;

            if (pinch != null)
            {
                // Renderer.enabled (no SetActive) para no afectar hijos/componentes hermanos como el PinchLine.
                var rend = pinch.GetComponent<Renderer>();
                if (rend != null) rend.enabled = state.pinching;
                if (state.pinching)
                    pinch.position = (state.thumbTipPos + state.indexTipPos) * 0.5f;
            }

            if (pinchLine != null)
            {
                pinchLine.enabled = true;
                pinchLine.useWorldSpace = true;
                pinchLine.positionCount = 2;
                pinchLine.SetPosition(0, state.thumbTipPos);
                pinchLine.SetPosition(1, state.indexTipPos);
            }
        }
        else
        {
            if (pinchLine != null) pinchLine.enabled = false;
            if (pinch != null)
            {
                var rend = pinch.GetComponent<Renderer>();
                if (rend != null) rend.enabled = false;
            }
        }

        if (rayDisplay != null)
        {
            if (state.rayActive)
            {
                rayDisplay.enabled = true;
                rayDisplay.useWorldSpace = true;
                rayDisplay.positionCount = 2;
                rayDisplay.SetPosition(0, state.rayStart);
                rayDisplay.SetPosition(1, state.rayEnd);
            }
            else
            {
                rayDisplay.enabled = false;
            }
        }
    }
}
