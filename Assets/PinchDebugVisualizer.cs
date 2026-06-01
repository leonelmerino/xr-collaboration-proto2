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

    // Toggle de visuales. Si esta en false, las esferas del thumb/index y la pinch line NO
    // se muestran (las muñecas + dedos del avatar quedan como unica representacion de mano).
    // La logica de deteccion de pinch sigue funcionando (drivea grab interactor).
    // Flippear a true si queres re-habilitar las visualizaciones de debug local.
    private const bool ShowVisualizers = false;

    private XRHandSubsystem handSubsystem;

    private void Awake()
    {
        ConfigureLineRenderer(rightPinchLine);
        ConfigureLineRenderer(leftPinchLine);

        // Si los visuales estan apagados, ocultar los markers inmediatamente para que no
        // queden esferas en posicion (0,0,0) durante el primer frame.
        if (!ShowVisualizers)
        {
            HideMarker(rightThumb); HideMarker(rightIndex); HideMarker(rightPinch);
            HideMarker(leftThumb); HideMarker(leftIndex); HideMarker(leftPinch);
            if (rightPinchLine != null) rightPinchLine.enabled = false;
            if (leftPinchLine != null) leftPinchLine.enabled = false;
        }
    }

    private static void HideMarker(Transform t)
    {
        if (t == null) return;
        var rend = t.GetComponent<Renderer>();
        if (rend != null) rend.enabled = false;
        // Tambien apagamos renderers de hijos por si la esfera vive en un hijo.
        foreach (var r in t.GetComponentsInChildren<Renderer>(includeInactive: true))
            r.enabled = false;
    }

    private void Start()
    {
        TryAttachHandSubsystem();
    }

    private void Update()
    {
        if (handSubsystem == null)
            TryAttachHandSubsystem();
    }

    private void TryAttachHandSubsystem()
    {
        if (handSubsystem != null) return;

        List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);

        if (subsystems.Count == 0)
            return;

        handSubsystem = subsystems[0];
        handSubsystem.updatedHands += OnHandsUpdated;
        Debug.Log("[PinchDebugVisualizer] XRHandSubsystem connected.");
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

        float pinchDistance = Vector3.Distance(thumbPose.position, indexPose.position);
        bool isPinching = pinchDistance < pinchThreshold;

        // Posiciones: siempre actualizadas, independiente de ShowVisualizers.
        // Estos Transforms son referencias compartidas con JengaPokeInteractor (pokePoint)
        // y JengaRayGrabInteractor (pinchPoint) — son datos de fisica, no solo visuales.
        thumbMarker.localPosition = thumbPose.position;
        thumbMarker.localRotation = thumbPose.rotation;

        indexMarker.localPosition = indexPose.position;
        indexMarker.localRotation = indexPose.rotation;

        if (isPinching)
        {
            pinchMarker.localPosition = (thumbPose.position + indexPose.position) * 0.5f;
            pinchMarker.localRotation = Quaternion.identity;
        }

        // Visuales: solo cuando ShowVisualizers esta activo.
        // Los GameObjects de los markers quedan inactivos (Awake los desactivo) y nunca
        // se re-activan, eliminando las esferas/lineas decorativas de la vista.
        if (ShowVisualizers)
        {
            SetHandActive(thumbMarker, indexMarker, pinchMarker, pinchLine, rayOrigin, true);

            if (pinchLine != null)
            {
                pinchLine.enabled = true;
                pinchLine.SetPosition(0, thumbPose.position);
                pinchLine.SetPosition(1, indexPose.position);
            }

            if (isPinching)
                pinchMarker.gameObject.SetActive(true);
            else
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