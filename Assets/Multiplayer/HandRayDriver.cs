using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Calcula el aim ray al estilo Meta Horizon: el ray se proyecta desde un "hombro virtual"
/// estimado a partir de la pose del HMD, pasando por la muneca real (joint XRHandJointID.Wrist).
///
/// Origen visible del ray = posicion de la muneca (world space).
/// Direccion = (wristWorld - shoulderEstimated).normalized.
///
/// Estable porque la muneca casi no se mueve con la flexion de dedos, y la estimacion de hombro
/// produce un vector natural alineado con la intencion de apuntar (como cuando uno apunta con el indice).
///
/// Sobreescribe transform.position y transform.rotation en world space cada frame, asi que
/// no importa donde esten parenteados los anchors (pero conviene reparentarlos al XR Origin
/// para que sean mas faciles de inspeccionar).
/// </summary>
public class HandRayDriver : MonoBehaviour
{
    public static HandRayDriver Instance { get; private set; }

    [Header("Targets")]
    [Tooltip("Anchor que JengaRayGrabInteractor (mano izquierda) usa como rayOrigin.")]
    [SerializeField] private Transform leftRayAnchor;
    [Tooltip("Anchor que JengaRayGrabInteractor (mano derecha) usa como rayOrigin.")]
    [SerializeField] private Transform rightRayAnchor;

    [Header("Shoulder Estimation (Meta-style)")]
    [Tooltip("Distancia lateral desde el HMD al hombro virtual (m). ~0.17 para adulto promedio.")]
    [SerializeField] private float shoulderLateralOffset = 0.17f;
    [Tooltip("Distancia hacia abajo desde el HMD al hombro virtual (m). ~0.15.")]
    [SerializeField] private float shoulderDownOffset = 0.15f;
    [Tooltip("Distancia hacia atras desde el HMD al hombro virtual (m). 0 funciona; valor positivo mueve el hombro detras del HMD.")]
    [SerializeField] private float shoulderBackOffset = 0.05f;

    [Header("Origen visible (palm placement)")]
    [Tooltip("Desplaza el origen visible del ray desde la muneca hacia adelante a lo largo de la direccion del ray, " +
             "para que el punto de partida se vea entre los dedos/palma en vez de en la muneca. " +
             "~0.05-0.09 m corresponde a la distancia muneca->nudillos en un adulto promedio.")]
    [SerializeField, Range(0f, 0.2f)] private float originForwardOffset = 0.05f;
    [Tooltip("Desplaza el origen visible perpendicularmente, hacia el lado del pulgar (positivo) o del menique (negativo). " +
             "0 = sobre el eje del hueso. Util para meter el origen 'entre los dedos' en vez de en el eje central. " +
             "Rango amplio para permitir correcciones grandes si la palma del avatar no esta centrada con la muneca trackeada.")]
    [SerializeField, Range(-0.2f, 0.2f)] private float originLateralOffset = 0f;
    [Tooltip("Desplaza el origen visible verticalmente respecto al ray, hacia el dorso (positivo) o hacia la palma (negativo). " +
             "Util si la muneca trackeada queda por arriba o por debajo de la palma visual del avatar.")]
    [SerializeField, Range(-0.2f, 0.2f)] private float originVerticalOffset = 0f;

    [Header("Smoothing (opcional, agrega latencia)")]
    [Tooltip("0 = sin smoothing (latencia minima). 0.3-0.5 reduce jitter pero agrega 30-50 ms de lag.")]
    [SerializeField, Range(0f, 1f)] private float rotationSmoothK = 0f;
    [Tooltip("0 = sin smoothing. Para posicion conviene 0 (la muneca ya es estable).")]
    [SerializeField, Range(0f, 1f)] private float positionSmoothK = 0f;

    [Header("References")]
    [Tooltip("Transform del HMD. Si esta vacio, se auto-detecta via Camera.main.")]
    [SerializeField] private Transform headTransform;
    [Tooltip("XR Origin para conversion de joints a world space. Si esta vacio, se auto-detecta.")]
    [SerializeField] private XROrigin xrOrigin;

    public float RotationSmoothK
    {
        get => rotationSmoothK;
        set => rotationSmoothK = Mathf.Clamp01(value);
    }

    public Vector3 LastLeftDirection { get; private set; } = Vector3.forward;
    public Vector3 LastRightDirection { get; private set; } = Vector3.forward;
    public bool LeftActiveThisFrame { get; private set; }
    public bool RightActiveThisFrame { get; private set; }

    private XRHandSubsystem handSubsystem;
    private Transform conversionSpace;
    private Vector3 smoothedLeftPos, smoothedRightPos;
    private Vector3 smoothedLeftDir = Vector3.forward, smoothedRightDir = Vector3.forward;
    private bool leftInitialized, rightInitialized;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        EnsureReferences();

        Debug.Log($"[HandRayDriver] Awake. leftAnchor={(leftRayAnchor ? leftRayAnchor.name : "MISS")} rightAnchor={(rightRayAnchor ? rightRayAnchor.name : "MISS")} head={(headTransform ? headTransform.name : "MISS")} xrOrigin={(xrOrigin ? xrOrigin.name : "MISS")}");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void EnsureReferences()
    {
        if (headTransform == null && Camera.main != null)
            headTransform = Camera.main.transform;

        if (xrOrigin == null)
            xrOrigin = FindObjectOfType<XROrigin>();

        if (xrOrigin != null)
        {
            conversionSpace = xrOrigin.CameraFloorOffsetObject != null
                ? xrOrigin.CameraFloorOffsetObject.transform
                : xrOrigin.transform;
        }
    }

    private void TryAttachSubsystem()
    {
        if (handSubsystem != null) return;
        var list = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(list);
        if (list.Count > 0)
            handSubsystem = list[0];
    }

    private void Update()
    {
        if (headTransform == null || xrOrigin == null) EnsureReferences();
        if (handSubsystem == null) TryAttachSubsystem();

        if (handSubsystem == null || headTransform == null)
        {
            LeftActiveThisFrame = false;
            RightActiveThisFrame = false;
            return;
        }

        LeftActiveThisFrame = UpdateHand(handSubsystem.leftHand, leftRayAnchor, isLeft: true,
            ref smoothedLeftPos, ref smoothedLeftDir, ref leftInitialized,
            out var leftDir);
        RightActiveThisFrame = UpdateHand(handSubsystem.rightHand, rightRayAnchor, isLeft: false,
            ref smoothedRightPos, ref smoothedRightDir, ref rightInitialized,
            out var rightDir);

        LastLeftDirection = leftDir;
        LastRightDirection = rightDir;
    }

    private bool UpdateHand(
        XRHand hand,
        Transform anchor,
        bool isLeft,
        ref Vector3 smoothedPos,
        ref Vector3 smoothedDir,
        ref bool initialized,
        out Vector3 outDir)
    {
        outDir = Vector3.forward;
        if (anchor == null) return false;

        if (!hand.isTracked)
        {
            anchor.gameObject.SetActive(false);
            initialized = false;
            return false;
        }

        var wrist = hand.GetJoint(XRHandJointID.Wrist);
        if (!wrist.TryGetPose(out var wristPose))
        {
            anchor.gameObject.SetActive(false);
            initialized = false;
            return false;
        }

        // Joints vienen en local space del tracking origin; convertir a world.
        Vector3 wristWorld = conversionSpace != null
            ? conversionSpace.TransformPoint(wristPose.position)
            : wristPose.position;

        // Estimar hombro virtual desde el HMD.
        Vector3 headPos = headTransform.position;
        Vector3 headRight = headTransform.right;
        Vector3 headForward = headTransform.forward;
        Vector3 lateral = isLeft ? -headRight : headRight;
        Vector3 shoulder = headPos
                         + lateral * shoulderLateralOffset
                         - Vector3.up * shoulderDownOffset
                         - headForward * shoulderBackOffset;

        // Direccion: del hombro a la muneca.
        Vector3 dir = wristWorld - shoulder;
        if (dir.sqrMagnitude < 0.0001f)
        {
            anchor.gameObject.SetActive(false);
            initialized = false;
            return false;
        }
        dir = dir.normalized;

        // Corrimos el origen visible desde la muneca hacia la palma/nudillos.
        // Para que sea simetrico entre ambas manos (independiente de como tenga el brazo el usuario),
        // el eje FORWARD se ancla a la anatomia: vector muneca -> middle-proximal (nudillo medio).
        // Esto garantiza que "5 cm forward" cae siempre en el mismo punto anatomico de cada mano,
        // mientras que la direccion del ray (shoulder->wrist) puede ser ligeramente asimetrica.
        // Fallback: si no hay joint disponible, usamos dir.
        Vector3 handForward = dir;
        var middleProximal = hand.GetJoint(XRHandJointID.MiddleProximal);
        if (middleProximal.TryGetPose(out var middleProxPose))
        {
            Vector3 middleProxWorld = conversionSpace != null
                ? conversionSpace.TransformPoint(middleProxPose.position)
                : middleProxPose.position;
            Vector3 anatomicalFwd = middleProxWorld - wristWorld;
            if (anatomicalFwd.sqrMagnitude > 1e-6f)
                handForward = anatomicalFwd.normalized;
        }

        Vector3 originWorld = wristWorld + handForward * originForwardOffset;

        bool useLateral = Mathf.Abs(originLateralOffset) > 1e-5f;
        bool useVertical = Mathf.Abs(originVerticalOffset) > 1e-5f;

        if (useLateral || useVertical)
        {
            // Lateral RAW: perpendicular al ray y al up del HMD = vector lateral en el plano de la mano.
            Vector3 lateralAxisRaw = Vector3.Cross(Vector3.up, dir).normalized;

            // Vertical: lo calculamos a partir del lateral RAW (sin invertir por mano), asi queda igual
            // para ambas manos. Si lo calcularamos despues de invertir lateral, el vertical tambien se
            // invertiria entre manos y el mismo valor numerico iria arriba en una y abajo en la otra.
            Vector3 verticalAxis = Vector3.Cross(dir, lateralAxisRaw).normalized;

            // Lateral final: invertido para la mano izquierda, asi positivo siempre apunta al pulgar
            // de cada mano (mirror-symmetric, que es lo natural cuando uno escribe "+0.02 hacia el pulgar").
            Vector3 lateralAxis = isLeft ? -lateralAxisRaw : lateralAxisRaw;

            if (useLateral) originWorld += lateralAxis * originLateralOffset;
            if (useVertical) originWorld += verticalAxis * originVerticalOffset;
        }

        // Smoothing opcional.
        if (!initialized)
        {
            smoothedPos = originWorld;
            smoothedDir = dir;
            initialized = true;
        }
        else
        {
            if (positionSmoothK > 0f)
                smoothedPos = Vector3.Lerp(smoothedPos, originWorld, positionSmoothK);
            else
                smoothedPos = originWorld;

            if (rotationSmoothK > 0f)
                smoothedDir = Vector3.Slerp(smoothedDir, dir, rotationSmoothK).normalized;
            else
                smoothedDir = dir;
        }

        anchor.gameObject.SetActive(true);
        anchor.position = smoothedPos;
        anchor.rotation = Quaternion.LookRotation(smoothedDir, Vector3.up);

        outDir = smoothedDir;
        return true;
    }
}
