using System;
using Unity.Netcode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Sincroniza la pose minima necesaria para animar el avatar humanoide:
/// - HMD rotation (la cabeza del avatar gira con el visor)
/// - Wrist pose (position + rotation) de cada mano — usada como target del Two-Bone IK del brazo.
///
/// Los valores se sincronizan en el LOCAL space del avatar root (no en world), asi son
/// independientes de donde esta el avatar fisicamente en la escena.
///
/// El owner escribe cada frame en Update. Los remotos leen via NetworkVariable.
/// AvatarPoseDriver (en cada sub-mesh) consume esta data y aplica al rig.
///
/// Bandwidth: ~60 bytes por update * 30 Hz = ~1.8 KB/s por avatar. Trivial.
/// </summary>
public class NetworkedAvatarPose : NetworkBehaviour
{
    public struct AvatarPoseState : INetworkSerializable, IEquatable<AvatarPoseState>
    {
        public Quaternion hmdRotLocal;     // rotacion del HMD en local space del avatar root

        // LEFT hand - wrist + 2 reference joints (middle metacarpal y thumb metacarpal). Con
        // estos 3 puntos el receiver puede reconstruir una base ortonormal de la palma
        // (forward = wrist->middle, normal = cross(forward, thumb-wrist)) sin depender de la
        // convencion de ejes del wrist rotation que reporte el SDK.
        public Vector3 leftWristPosLocal;
        public Vector3 leftMiddleMetacarpalPosLocal;
        public Vector3 leftThumbMetacarpalPosLocal;
        public bool leftValid;

        // RIGHT hand
        public Vector3 rightWristPosLocal;
        public Vector3 rightMiddleMetacarpalPosLocal;
        public Vector3 rightThumbMetacarpalPosLocal;
        public bool rightValid;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref hmdRotLocal);
            serializer.SerializeValue(ref leftWristPosLocal);
            serializer.SerializeValue(ref leftMiddleMetacarpalPosLocal);
            serializer.SerializeValue(ref leftThumbMetacarpalPosLocal);
            serializer.SerializeValue(ref leftValid);
            serializer.SerializeValue(ref rightWristPosLocal);
            serializer.SerializeValue(ref rightMiddleMetacarpalPosLocal);
            serializer.SerializeValue(ref rightThumbMetacarpalPosLocal);
            serializer.SerializeValue(ref rightValid);
        }

        public bool Equals(AvatarPoseState other)
        {
            return hmdRotLocal == other.hmdRotLocal
                && leftWristPosLocal == other.leftWristPosLocal
                && leftMiddleMetacarpalPosLocal == other.leftMiddleMetacarpalPosLocal
                && leftThumbMetacarpalPosLocal == other.leftThumbMetacarpalPosLocal
                && leftValid == other.leftValid
                && rightWristPosLocal == other.rightWristPosLocal
                && rightMiddleMetacarpalPosLocal == other.rightMiddleMetacarpalPosLocal
                && rightThumbMetacarpalPosLocal == other.rightThumbMetacarpalPosLocal
                && rightValid == other.rightValid;
        }
    }

    public readonly NetworkVariable<AvatarPoseState> Pose = new NetworkVariable<AvatarPoseState>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    /// <summary>
    /// Posiciones de joints de los dedos en avatar-root-local space. Separado de AvatarPoseState
    /// para que NGO sincronice por separado: si solo se mueve la cabeza, no se broadcastea el
    /// blob entero de fingers.
    ///
    /// Mapeo XR Hands → Humanoid bones (3 bones por dedo, 5 dedos por mano):
    ///   Thumb:       ThumbMetacarpal* → ThumbProximal → ThumbDistal → ThumbTip
    ///                (ThumbMetacarpal vive en AvatarPoseState para wrist orientation)
    ///   Index/Mid/Ring/Little: Proximal → Intermediate → Distal → Tip
    /// </summary>
    public struct HandFingerPose : INetworkSerializable, IEquatable<HandFingerPose>
    {
        // LEFT hand (19 joints en local-del-avatar-root)
        public Vector3 lThumbProx, lThumbDist, lThumbTip;
        public Vector3 lIndexProx, lIndexInt, lIndexDist, lIndexTip;
        public Vector3 lMidProx, lMidInt, lMidDist, lMidTip;
        public Vector3 lRingProx, lRingInt, lRingDist, lRingTip;
        public Vector3 lLitProx, lLitInt, lLitDist, lLitTip;
        public bool lValid;

        // RIGHT hand (19 joints)
        public Vector3 rThumbProx, rThumbDist, rThumbTip;
        public Vector3 rIndexProx, rIndexInt, rIndexDist, rIndexTip;
        public Vector3 rMidProx, rMidInt, rMidDist, rMidTip;
        public Vector3 rRingProx, rRingInt, rRingDist, rRingTip;
        public Vector3 rLitProx, rLitInt, rLitDist, rLitTip;
        public bool rValid;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            // LEFT
            serializer.SerializeValue(ref lThumbProx); serializer.SerializeValue(ref lThumbDist); serializer.SerializeValue(ref lThumbTip);
            serializer.SerializeValue(ref lIndexProx); serializer.SerializeValue(ref lIndexInt); serializer.SerializeValue(ref lIndexDist); serializer.SerializeValue(ref lIndexTip);
            serializer.SerializeValue(ref lMidProx); serializer.SerializeValue(ref lMidInt); serializer.SerializeValue(ref lMidDist); serializer.SerializeValue(ref lMidTip);
            serializer.SerializeValue(ref lRingProx); serializer.SerializeValue(ref lRingInt); serializer.SerializeValue(ref lRingDist); serializer.SerializeValue(ref lRingTip);
            serializer.SerializeValue(ref lLitProx); serializer.SerializeValue(ref lLitInt); serializer.SerializeValue(ref lLitDist); serializer.SerializeValue(ref lLitTip);
            serializer.SerializeValue(ref lValid);
            // RIGHT
            serializer.SerializeValue(ref rThumbProx); serializer.SerializeValue(ref rThumbDist); serializer.SerializeValue(ref rThumbTip);
            serializer.SerializeValue(ref rIndexProx); serializer.SerializeValue(ref rIndexInt); serializer.SerializeValue(ref rIndexDist); serializer.SerializeValue(ref rIndexTip);
            serializer.SerializeValue(ref rMidProx); serializer.SerializeValue(ref rMidInt); serializer.SerializeValue(ref rMidDist); serializer.SerializeValue(ref rMidTip);
            serializer.SerializeValue(ref rRingProx); serializer.SerializeValue(ref rRingInt); serializer.SerializeValue(ref rRingDist); serializer.SerializeValue(ref rRingTip);
            serializer.SerializeValue(ref rLitProx); serializer.SerializeValue(ref rLitInt); serializer.SerializeValue(ref rLitDist); serializer.SerializeValue(ref rLitTip);
            serializer.SerializeValue(ref rValid);
        }

        public bool Equals(HandFingerPose o)
        {
            return lValid == o.lValid && rValid == o.rValid
                && lThumbProx == o.lThumbProx && lThumbDist == o.lThumbDist && lThumbTip == o.lThumbTip
                && lIndexProx == o.lIndexProx && lIndexInt == o.lIndexInt && lIndexDist == o.lIndexDist && lIndexTip == o.lIndexTip
                && lMidProx == o.lMidProx && lMidInt == o.lMidInt && lMidDist == o.lMidDist && lMidTip == o.lMidTip
                && lRingProx == o.lRingProx && lRingInt == o.lRingInt && lRingDist == o.lRingDist && lRingTip == o.lRingTip
                && lLitProx == o.lLitProx && lLitInt == o.lLitInt && lLitDist == o.lLitDist && lLitTip == o.lLitTip
                && rThumbProx == o.rThumbProx && rThumbDist == o.rThumbDist && rThumbTip == o.rThumbTip
                && rIndexProx == o.rIndexProx && rIndexInt == o.rIndexInt && rIndexDist == o.rIndexDist && rIndexTip == o.rIndexTip
                && rMidProx == o.rMidProx && rMidInt == o.rMidInt && rMidDist == o.rMidDist && rMidTip == o.rMidTip
                && rRingProx == o.rRingProx && rRingInt == o.rRingInt && rRingDist == o.rRingDist && rRingTip == o.rRingTip
                && rLitProx == o.rLitProx && rLitInt == o.rLitInt && rLitDist == o.rLitDist && rLitTip == o.rLitTip;
        }
    }

    public readonly NetworkVariable<HandFingerPose> Fingers = new NetworkVariable<HandFingerPose>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    /// <summary>
    /// Calibracion de muñecas: rotacion del wrist (en avatar-root-local space) que se considera
    /// "neutral" / "bind pose". Cuando el AvatarPoseDriver recibe una rotacion igual a esta,
    /// el hand bone del avatar queda en bind pose. Cualquier delta desde esta calibracion se
    /// aplica como rotacion del bone.
    ///
    /// Sincronizada para que todos los viewers apliquen el mismo offset visual.
    ///
    /// Identity = sin calibracion (comportamiento default, equivalente a "el usuario empezo
    /// con las manos justo en T-pose"). Despues de presionar la tecla de calibracion, los
    /// valores reflejan la pose actual del usuario.
    /// </summary>
    public readonly NetworkVariable<Quaternion> LeftWristCalibration = new NetworkVariable<Quaternion>(
        Quaternion.identity,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    public readonly NetworkVariable<Quaternion> RightWristCalibration = new NetworkVariable<Quaternion>(
        Quaternion.identity,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    /// <summary>
    /// Offset rotacional manual (Euler en grados) para fine-tuning de la orientacion del wrist.
    /// Se aplica DESPUES del bind pose offset y la calibracion. Util para corregir residuos
    /// como "palma 10 grados rotada" sin recalibrar.
    ///
    /// Sincronizadas para que todos los viewers tuneen al mismo tiempo. El AvatarHandTunerHUD
    /// modifica estos valores con teclado en runtime.
    /// </summary>
    public readonly NetworkVariable<Vector3> LeftWristOffsetEuler = new NetworkVariable<Vector3>(
        Vector3.zero,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    public readonly NetworkVariable<Vector3> RightWristOffsetEuler = new NetworkVariable<Vector3>(
        Vector3.zero,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    [Header("References (auto-find if null)")]
    [SerializeField] private XROrigin xrOrigin;
    [SerializeField] private Camera hmdCamera;

    [Header("Calibration")]
    [Tooltip("Tecla para ejecutar CalibrateHands(): captura la rotacion actual de las muñecas " +
             "como pose neutral del avatar. Pone las manos donde querés que el avatar tenga las " +
             "manos en T-pose neutral (palmas abajo, dedos hacia adelante) y apretala. Solo " +
             "tiene efecto en el owner del NetworkObject.")]
    [SerializeField] private KeyCode calibrationKey = KeyCode.C;

    private Transform _conversionSpace;
    private XRHandSubsystem _handSubsystem;
    private AvatarPoseState _scratch;
    private HandFingerPose _fingerScratch;

    private void Awake()
    {
        EnsureRefs();
    }

    private void Update()
    {
        if (!IsOwner) return;
        EnsureRefs();
        if (hmdCamera == null) return;

        // HMD rotation en local space del avatar root.
        _scratch.hmdRotLocal = Quaternion.Inverse(transform.rotation) * hmdCamera.transform.rotation;

        // Wrists desde XR Hands: leemos 3 joints por mano (wrist + middle metacarpal + thumb
        // metacarpal). El receiver usa los 3 puntos para reconstruir la orientacion de la palma
        // geometricamente, sin depender de la convencion de ejes del wrist rotation reportado.
        if (_handSubsystem != null && _conversionSpace != null)
        {
            ReadHandJoints(_handSubsystem.leftHand,
                           out _scratch.leftWristPosLocal,
                           out _scratch.leftMiddleMetacarpalPosLocal,
                           out _scratch.leftThumbMetacarpalPosLocal,
                           out _scratch.leftValid);
            ReadHandJoints(_handSubsystem.rightHand,
                           out _scratch.rightWristPosLocal,
                           out _scratch.rightMiddleMetacarpalPosLocal,
                           out _scratch.rightThumbMetacarpalPosLocal,
                           out _scratch.rightValid);
        }
        else
        {
            _scratch.leftValid = false;
            _scratch.rightValid = false;
        }

        // NGO dedupea: si _scratch == _previous, no broadcastea.
        Pose.Value = _scratch;

        // Finger pose: 19 joints adicionales por mano. Sync separado en su propia NetworkVariable
        // para que NGO pueda dedupar independientemente (si solo movés la cabeza, no se manda
        // el blob de fingers).
        if (_handSubsystem != null && _conversionSpace != null)
        {
            ReadFingerJoints(_handSubsystem.leftHand, ref _fingerScratch, isLeft: true);
            ReadFingerJoints(_handSubsystem.rightHand, ref _fingerScratch, isLeft: false);
        }
        else
        {
            _fingerScratch.lValid = false;
            _fingerScratch.rValid = false;
        }
        Fingers.Value = _fingerScratch;

        // Tecla para calibrar manos. Captura la pose actual como "neutral del avatar".
        if (Input.GetKeyDown(calibrationKey))
        {
            CalibrateHands();
        }
    }

    /// <summary>
    /// Lee los 19 joints de finger por mano y los escribe en la mitad correspondiente del
    /// HandFingerPose. Cada joint se transforma a avatar-root-local.
    /// </summary>
    private void ReadFingerJoints(XRHand hand, ref HandFingerPose target, bool isLeft)
    {
        if (!hand.isTracked)
        {
            if (isLeft) target.lValid = false; else target.rValid = false;
            return;
        }

        bool ok = true;
        if (isLeft)
        {
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.ThumbProximal, out target.lThumbProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.ThumbDistal, out target.lThumbDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.ThumbTip, out target.lThumbTip);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.IndexProximal, out target.lIndexProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.IndexIntermediate, out target.lIndexInt);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.IndexDistal, out target.lIndexDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.IndexTip, out target.lIndexTip);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.MiddleProximal, out target.lMidProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.MiddleIntermediate, out target.lMidInt);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.MiddleDistal, out target.lMidDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.MiddleTip, out target.lMidTip);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.RingProximal, out target.lRingProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.RingIntermediate, out target.lRingInt);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.RingDistal, out target.lRingDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.RingTip, out target.lRingTip);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.LittleProximal, out target.lLitProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.LittleIntermediate, out target.lLitInt);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.LittleDistal, out target.lLitDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.LittleTip, out target.lLitTip);
            target.lValid = ok;
        }
        else
        {
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.ThumbProximal, out target.rThumbProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.ThumbDistal, out target.rThumbDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.ThumbTip, out target.rThumbTip);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.IndexProximal, out target.rIndexProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.IndexIntermediate, out target.rIndexInt);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.IndexDistal, out target.rIndexDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.IndexTip, out target.rIndexTip);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.MiddleProximal, out target.rMidProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.MiddleIntermediate, out target.rMidInt);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.MiddleDistal, out target.rMidDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.MiddleTip, out target.rMidTip);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.RingProximal, out target.rRingProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.RingIntermediate, out target.rRingInt);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.RingDistal, out target.rRingDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.RingTip, out target.rRingTip);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.LittleProximal, out target.rLitProx);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.LittleIntermediate, out target.rLitInt);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.LittleDistal, out target.rLitDist);
            ok &= TryGetJointAvatarLocal(hand, XRHandJointID.LittleTip, out target.rLitTip);
            target.rValid = ok;
        }
    }

    /// <summary>
    /// Captura la rotacion actual de las muñecas (avatar-root-local) como la calibracion
    /// neutral. A partir de ahora el AvatarPoseDriver va a considerar este momento como
    /// "bind pose" y todos los deltas desde aca rotan el hand bone del avatar.
    ///
    /// Pre-condicion: el usuario debe estar con sus muñecas en la pose que quiera mappear
    /// al T-pose del avatar (tipicamente: palmas abajo, dedos hacia adelante, brazos extendidos).
    /// </summary>
    public void CalibrateHands()
    {
        // NOTE: este metodo quedo como no-op despues de migrar a la estrategia geometrica
        // (joint-based palm orientation). Lo dejamos por compatibilidad con keybindings
        // viejos pero ya no escribe nada significativo a las NV de calibracion.
        Debug.Log("[NetworkedAvatarPose] CalibrateHands() ignored: usando estrategia geometrica " +
                  "basada en posiciones de joints (no requiere calibracion manual).");
    }

    /// <summary>
    /// Lee 3 joints de la mano (wrist + middle metacarpal + thumb metacarpal), los convierte
    /// a local space del avatar root, y reporta validity.
    /// </summary>
    private void ReadHandJoints(XRHand hand,
                                out Vector3 wristLocal,
                                out Vector3 middleLocal,
                                out Vector3 thumbLocal,
                                out bool valid)
    {
        wristLocal = Vector3.zero;
        middleLocal = Vector3.zero;
        thumbLocal = Vector3.zero;
        valid = false;

        if (!hand.isTracked) return;
        if (!TryGetJointAvatarLocal(hand, XRHandJointID.Wrist, out wristLocal)) return;
        if (!TryGetJointAvatarLocal(hand, XRHandJointID.MiddleMetacarpal, out middleLocal)) return;
        if (!TryGetJointAvatarLocal(hand, XRHandJointID.ThumbMetacarpal, out thumbLocal)) return;
        valid = true;
    }

    private bool TryGetJointAvatarLocal(XRHand hand, XRHandJointID id, out Vector3 posLocal)
    {
        posLocal = Vector3.zero;
        var joint = hand.GetJoint(id);
        if (!joint.TryGetPose(out var pose)) return false;
        // Pose en local del tracking origin → world → local del avatar root.
        Vector3 worldPos = _conversionSpace.TransformPoint(pose.position);
        posLocal = transform.InverseTransformPoint(worldPos);
        return true;
    }

    private void EnsureRefs()
    {
        if (xrOrigin == null) xrOrigin = FindObjectOfType<XROrigin>();

        if (xrOrigin != null && _conversionSpace == null)
        {
            _conversionSpace = xrOrigin.CameraFloorOffsetObject != null
                ? xrOrigin.CameraFloorOffsetObject.transform
                : xrOrigin.transform;
        }

        if (hmdCamera == null && Camera.main != null)
            hmdCamera = Camera.main;

        if (_handSubsystem == null)
        {
            var list = new System.Collections.Generic.List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(list);
            if (list.Count > 0) _handSubsystem = list[0];
        }
    }
}
