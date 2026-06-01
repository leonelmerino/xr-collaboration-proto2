using UnityEngine;

/// <summary>
/// Aplica la pose sincronizada (HMD rotation + L/R wrist) al rig humanoid de esta sub-mesh.
///
/// Va en CADA sub-mesh del AvatarHumanoid.prefab (Avatar_Host, Avatar_Client, Avatar_Helper).
/// El AvatarRoleMeshSwitcher activa solo el sub-mesh del rol activo; las otras dos quedan
/// inactivas y este driver no corre en ellas (los componentes en GameObjects inactivos no
/// ejecutan Update).
///
/// Funcionamiento:
/// 1. Lee la NetworkVariable de NetworkedAvatarPose (que esta en el root del prefab).
/// 2. En LateUpdate (despues del Animator):
///    - Convierte hmdRotLocal a world rotation y la aplica al head bone.
///    - Convierte leftWristPos/Rot y rightWristPos/Rot a world space y los aplica a los
///      Transforms de IK target.
/// 3. Las Two-Bone IK constraints (configuradas en Animation Rigging) leen esos targets y
///    deforman el brazo para que la muneca llegue al target con la orientacion correcta.
///
/// IK targets se crean manualmente como children del avatar (instrucciones en la guia).
/// Si no se asignan, el driver solo controla la cabeza.
/// </summary>
public class AvatarPoseDriver : MonoBehaviour
{
    [Header("Required references")]
    [Tooltip("NetworkedAvatarPose en el root del prefab. Auto-resuelto si esta vacio.")]
    [SerializeField] private NetworkedAvatarPose poseSync;

    [Tooltip("Animator de este sub-mesh (Rocketbox Humanoid). Auto-resuelto.")]
    [SerializeField] private Animator animator;

    [Header("IK targets (asignar tras crear los GameObjects del rig)")]
    [Tooltip("Transform que sera target del Two-Bone IK del brazo izquierdo. Crear como child " +
             "del rig (vacio); este driver lo posiciona cada frame.")]
    [SerializeField] private Transform leftHandIKTarget;

    [SerializeField] private Transform rightHandIKTarget;

    [Header("Tuning")]
    [Tooltip("Offset rotacional que se aplica al IK target para que la palma de la mano del " +
             "avatar coincida con la palma real. Tipico: rotar 90 en algun eje si el avatar usa " +
             "convencion distinta a XR Hands. Empezar con identity y ajustar.")]
    [SerializeField] private Vector3 leftWristRotationOffsetEuler = Vector3.zero;
    [SerializeField] private Vector3 rightWristRotationOffsetEuler = Vector3.zero;

    [Tooltip("Si esta activo, el head bone tambien copia la rotacion HMD. Si el avatar es del " +
             "OWNER local, conviene desactivar el head (se ve raro estar dentro de tu propia cabeza).")]
    [SerializeField] private bool driveHeadBone = true;

    [Tooltip("Si esta activo, los IK targets se actualizan. Desactivar si querés que el avatar " +
             "quede en T-pose para debug.")]
    [SerializeField] private bool driveHandTargets = true;

    [Header("Local owner visibility")]
    [Tooltip("Si esta activo y este avatar pertenece al OWNER local, se OCULTA SOLO LA CABEZA " +
             "(escalando el head bone a un valor casi cero). Asi el host ve su torso, brazos y " +
             "manos (necesario para jugar al Jenga: ver tus manos para alcanzar las piezas) pero " +
             "no ve su propia cara desde adentro del HMD. Los OTROS jugadores siguen viendo " +
             "este avatar completo (instancian su propia copia donde isLocalOwner=False).")]
    [SerializeField] private bool hideRenderersForLocalOwner = true;

    private Transform _headBone;
    private Transform _avatarRoot; // el root del prefab (no este sub-mesh)

    // Bind pose offset para la CABEZA: capturado en el primer LateUpdate. Necesario porque el
    // Bip01 Head bone del Rocketbox Biped tiene convencion de ejes distinta a Unity (X=up,
    // Y=forward, no Z=forward).
    private bool _bindCaptured;
    private Quaternion _headBindRelLocalRot;

    // Bones de manos y dedos (para la estrategia geometrica de orientacion de palma).
    private Transform _leftHandBone;
    private Transform _rightHandBone;
    private Transform _leftMiddleProxBone;
    private Transform _rightMiddleProxBone;
    private Transform _leftThumbProxBone;
    private Transform _rightThumbProxBone;

    // Orientacion de la palma EN EL FRAME LOCAL DEL HAND BONE. Es constante: depende solo de
    // donde estan los finger bones en la jerarquia del rig, no de la pose runtime. Por eso lo
    // capturamos una vez y queda valido para siempre.
    //
    // Math: si el hand bone se rota a R (world), la palma del avatar queda en world rotation
    //   palmWorld = R * palmRotInHand
    // Despejando R cuando queremos palmWorld = userPalmRot:
    //   R = userPalmRot * Inverse(palmRotInHand)
    // Esa R es el rotation que ponemos al IK target.
    private bool _palmBindCaptured;
    private Quaternion _leftPalmRotInHand;
    private Quaternion _rightPalmRotInHand;

    // Finger driving toggle. No es [SerializeField] para evitar problemas de class layout
    // mismatch al cambiar la struct AvatarPoseDriver en prefabs ya serializados. Si querés
    // desactivar el finger driving, cambiá el default a false y recompila.
    private bool driveFingerBones = true;

    // Para CADA finger bone driveado guardamos: el Transform del bone, el Transform del child
    // bone (de quien sale el "forward direction" del bone), y la direccion local del bone hacia
    // su child (invariante del rig, capturada una vez).
    //
    // Mapeo XR Hands → Humanoid bones (sigue Unity humanoid convention):
    //  Thumb anatomicamente tiene 3 segmentos: 1st metacarpal, proximal phalanx, distal phalanx.
    //  Humanoid los nombra ThumbProximal/Intermediate/Distal (no metacarpal/proximal/distal).
    //    ThumbProximal     → from XR.ThumbMetacarpal → XR.ThumbProximal
    //    ThumbIntermediate → from XR.ThumbProximal   → XR.ThumbDistal
    //    ThumbDistal       → from XR.ThumbDistal     → XR.ThumbTip
    //  Otros dedos (Index/Middle/Ring/Little): los 3 bones humanoid corresponden a los 3
    //  segmentos phalanges (proximal, intermediate, distal).
    //    XxxProximal     → from XR.XxxProximal     → XR.XxxIntermediate
    //    XxxIntermediate → from XR.XxxIntermediate → XR.XxxDistal
    //    XxxDistal       → from XR.XxxDistal       → XR.XxxTip
    private struct FingerBoneSlot
    {
        public Transform bone;
        public Vector3 childDirInBoneLocal; // direccion (en frame local del bone) del bone hacia su tip child
    }

    // 15 bones por mano. Acceso por indice (ver enum FB).
    private FingerBoneSlot[] _leftFingerBones = new FingerBoneSlot[15];
    private FingerBoneSlot[] _rightFingerBones = new FingerBoneSlot[15];
    private bool _fingerBonesCaptured;

    private enum FB
    {
        ThumbProx = 0, ThumbInt, ThumbDist,
        IndexProx, IndexInt, IndexDist,
        MidProx, MidInt, MidDist,
        RingProx, RingInt, RingDist,
        LitProx, LitInt, LitDist,
        Count
    }

    private void OnEnable()
    {
        if (animator == null) animator = GetComponent<Animator>();

        bool animatorOK = animator != null;
        bool humanoidOK = animatorOK && animator.isHuman;
        if (humanoidOK)
        {
            _headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            _leftHandBone = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _rightHandBone = animator.GetBoneTransform(HumanBodyBones.RightHand);
            _leftMiddleProxBone = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            _rightMiddleProxBone = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            _leftThumbProxBone = animator.GetBoneTransform(HumanBodyBones.LeftThumbProximal);
            _rightThumbProxBone = animator.GetBoneTransform(HumanBodyBones.RightThumbProximal);
        }

        if (poseSync == null)
        {
            poseSync = GetComponentInParent<NetworkedAvatarPose>();
        }

        _avatarRoot = poseSync != null ? poseSync.transform : transform.root;

        // El check de IsOwner se difiere porque OnEnable corre DURANTE Instantiate, ANTES de
        // que NGO complete el spawn. En ese momento IsOwner siempre devuelve false.
        if (hideRenderersForLocalOwner)
            StartCoroutine(EvaluateOwnerVisibilityWhenSpawned());

        // Diagnostic dump: una linea por sub-mesh al activarse. Util para identificar refs faltantes.
        Debug.Log($"[AvatarPoseDriver] OnEnable on '{gameObject.name}': " +
                  $"animator={(animatorOK ? "OK" : "NULL")} (isHuman={humanoidOK}), " +
                  $"headBone={(_headBone != null ? _headBone.name : "NULL")}, " +
                  $"poseSync={(poseSync != null ? "OK" : "NULL")}, " +
                  $"avatarRoot={(_avatarRoot != null ? _avatarRoot.name : "NULL")}, " +
                  $"leftIKTarget={(leftHandIKTarget != null ? leftHandIKTarget.name : "NULL")}, " +
                  $"rightIKTarget={(rightHandIKTarget != null ? rightHandIKTarget.name : "NULL")}. " +
                  $"(IsOwner check deferred a post-spawn.)");
    }

    /// <summary>
    /// Espera a que el NetworkObject este spawneado (IsOwner es invalido hasta entonces) y aplica
    /// el hide-for-owner. Hay un timeout para no quedarse en loop si algo falla.
    /// </summary>
    private System.Collections.IEnumerator EvaluateOwnerVisibilityWhenSpawned()
    {
        float deadline = Time.time + 5f;
        while (Time.time < deadline)
        {
            if (poseSync != null && poseSync.IsSpawned) break;
            yield return null;
        }

        bool isLocalOwner = poseSync != null && poseSync.IsOwner;
        if (isLocalOwner)
        {
            // En vez de ocultar TODOS los renderers (como hacíamos antes), ahora solo escalamos
            // el head bone a casi cero. Eso colapsa los vertices skinned a la cabeza (cara, pelo,
            // ojos, mandibula) a un punto invisible. El resto del avatar (torso, brazos, manos)
            // queda visible — necesario para jugar al Jenga: ver tus manos para alcanzar las piezas.
            //
            // No usamos Vector3.zero exacto para evitar matrices singulares (NaN en skinning);
            // 0.0001 da el mismo efecto visual (un punto microscopico) sin problemas numericos.
            if (_headBone != null)
            {
                _headBone.localScale = new Vector3(0.0001f, 0.0001f, 0.0001f);
                Debug.Log($"[AvatarPoseDriver] Post-spawn: '{gameObject.name}' isLocalOwner=True. " +
                          $"Head bone scaled to 0.0001 (avatar body/arms visible to owner, head hidden).");
            }
            else
            {
                Debug.LogWarning($"[AvatarPoseDriver] Post-spawn: '{gameObject.name}' isLocalOwner=True " +
                                  $"but _headBone is NULL, cannot hide head.");
            }
        }
        else
        {
            Debug.Log($"[AvatarPoseDriver] Post-spawn: '{gameObject.name}' isLocalOwner=False " +
                      $"(spawned={(poseSync != null && poseSync.IsSpawned)}). Renderers stay visible.");
        }
    }

    private void LateUpdate()
    {
        if (poseSync == null || _avatarRoot == null) return;

        // --- Captura de bind del head bone ---
        // El head bone NO va por IK targets, lo manejamos por bind capture clasico.
        if (!_bindCaptured && _headBone != null)
        {
            _headBindRelLocalRot = Quaternion.Inverse(_avatarRoot.rotation) * _headBone.rotation;
            _bindCaptured = true;
            Debug.Log($"[AvatarPoseDriver] Head bind captured on '{gameObject.name}': " +
                      $"head_euler={_headBindRelLocalRot.eulerAngles.ToString("F1")}.");
        }

        // --- Captura de orientacion de palma EN FRAME LOCAL DEL HAND BONE ---
        // Esto es INVARIANTE al runtime: solo depende de la jerarquia del rig (donde estan los
        // finger bones respecto al hand bone). Aun si el hand bone esta rotado por el IK,
        // InverseTransformPoint nos da la posicion en su frame local, que es fijo.
        TryCapturePalmBind();

        var state = poseSync.Pose.Value;

        // --- Cabeza ---
        if (driveHeadBone && _headBone != null && _bindCaptured)
        {
            Quaternion worldRot = _avatarRoot.rotation * state.hmdRotLocal * _headBindRelLocalRot;
            _headBone.rotation = worldRot;
        }

        // --- IK targets de manos: estrategia GEOMETRICA ---
        // En vez de aplicar la rotacion del wrist y compensar con offsets manuales (fragil
        // por las distintas convenciones de ejes entre XR Hands y el rig humanoid), derivamos
        // la orientacion de la palma desde 3 joints del usuario (wrist, middle metacarpal,
        // thumb metacarpal) y la mapeamos al frame del hand bone usando su orientacion
        // intrinseca de palma (capturada en _leftPalmRotInHand).
        //
        // Formula:
        //   userPalmWorldRot = LookRotation(fingersForward, palmNormal)        // de los joints
        //   IKTarget.rotation = userPalmWorldRot * Inverse(palmRotInHand) * extraOffset
        //
        // El extraOffset (Euler del HUD + SerializeField) queda para fine-tuning manual
        // residual, pero deberia ser cero o cerca de cero con la estrategia correcta.
        if (driveHandTargets && _palmBindCaptured)
        {
            Quaternion leftExtraOffset = Quaternion.Euler(leftWristRotationOffsetEuler + poseSync.LeftWristOffsetEuler.Value);
            Quaternion rightExtraOffset = Quaternion.Euler(rightWristRotationOffsetEuler + poseSync.RightWristOffsetEuler.Value);

            if (leftHandIKTarget != null && state.leftValid)
            {
                Vector3 wristW = _avatarRoot.TransformPoint(state.leftWristPosLocal);
                Vector3 middleW = _avatarRoot.TransformPoint(state.leftMiddleMetacarpalPosLocal);
                Vector3 thumbW = _avatarRoot.TransformPoint(state.leftThumbMetacarpalPosLocal);

                if (TryComputePalmRotation(wristW, middleW, thumbW, /*isLeft*/ true, out Quaternion userPalmWorldRot))
                {
                    Quaternion ikRot = userPalmWorldRot * Quaternion.Inverse(_leftPalmRotInHand) * leftExtraOffset;
                    leftHandIKTarget.SetPositionAndRotation(wristW, ikRot);
                }
            }

            if (rightHandIKTarget != null && state.rightValid)
            {
                Vector3 wristW = _avatarRoot.TransformPoint(state.rightWristPosLocal);
                Vector3 middleW = _avatarRoot.TransformPoint(state.rightMiddleMetacarpalPosLocal);
                Vector3 thumbW = _avatarRoot.TransformPoint(state.rightThumbMetacarpalPosLocal);

                if (TryComputePalmRotation(wristW, middleW, thumbW, /*isLeft*/ false, out Quaternion userPalmWorldRot))
                {
                    Quaternion ikRot = userPalmWorldRot * Quaternion.Inverse(_rightPalmRotInHand) * rightExtraOffset;
                    rightHandIKTarget.SetPositionAndRotation(wristW, ikRot);
                }
            }
        }

        // --- Finger bones ---
        // Mismo enfoque geometrico que las muñecas: el bone tiene una direccion fija (en su
        // frame local) hacia su child, calculada una vez del rig. Cada frame computamos la
        // direccion equivalente en el usuario (world), y aplicamos Quaternion.FromToRotation
        // para alinear. Driveamos en orden de jerarquia (Prox → Int → Dist).
        if (driveFingerBones)
        {
            TryCaptureFingerBoneChildDirs();
            if (_fingerBonesCaptured)
            {
                var fingers = poseSync.Fingers.Value;
                DriveFingerBones(_leftFingerBones, ref fingers, isLeft: true);
                DriveFingerBones(_rightFingerBones, ref fingers, isLeft: false);
            }
        }
    }

    /// <summary>
    /// Captura el "child direction" en frame local del bone para cada finger bone, una sola vez.
    /// Es invariante porque InverseTransformPoint cancela la orientacion runtime del bone.
    /// </summary>
    private void TryCaptureFingerBoneChildDirs()
    {
        if (_fingerBonesCaptured) return;
        if (animator == null || !animator.isHuman) return;

        // LEFT
        if (!CaptureFingerChain(_leftFingerBones,
                                HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
                                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
                                HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
                                HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
                                HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal))
            return;
        // RIGHT
        if (!CaptureFingerChain(_rightFingerBones,
                                HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
                                HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
                                HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
                                HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
                                HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal))
            return;

        _fingerBonesCaptured = true;
        Debug.Log($"[AvatarPoseDriver] Finger bone child dirs captured on '{gameObject.name}'.");
    }

    /// <summary>
    /// Para una cadena de 15 bones (3 por cada uno de 5 dedos), busca los Transform y calcula
    /// la direccion "hacia el child" en frame local de cada bone. El "child" del bone Distal
    /// se aproxima usando la direccion (intermedio → distal) extrapolada, ya que los bones
    /// distales no tienen child en Humanoid.
    /// </summary>
    private bool CaptureFingerChain(FingerBoneSlot[] slots,
                                     HumanBodyBones tProx, HumanBodyBones tInt, HumanBodyBones tDist,
                                     HumanBodyBones iProx, HumanBodyBones iInt, HumanBodyBones iDist,
                                     HumanBodyBones mProx, HumanBodyBones mInt, HumanBodyBones mDist,
                                     HumanBodyBones rProx, HumanBodyBones rInt, HumanBodyBones rDist,
                                     HumanBodyBones lProx, HumanBodyBones lInt, HumanBodyBones lDist)
    {
        var T = new HumanBodyBones[15] { tProx, tInt, tDist, iProx, iInt, iDist, mProx, mInt, mDist, rProx, rInt, rDist, lProx, lInt, lDist };
        for (int i = 0; i < 15; i++)
        {
            slots[i].bone = animator.GetBoneTransform(T[i]);
            if (slots[i].bone == null) return false;
        }
        // Calcular childDirInBoneLocal: bone Distal usa la direccion bone→tip aproximada con la
        // direccion del bone Intermediate (su parent en la cadena, mismo eje aprox). Bones
        // Proximal/Intermediate usan el siguiente bone de la cadena como child.
        // Indices: 0=ThumbProx, 1=ThumbInt, 2=ThumbDist, 3=IndProx, 4=IndInt, 5=IndDist, ...
        for (int finger = 0; finger < 5; finger++)
        {
            int prox = finger * 3;
            int inter = prox + 1;
            int dist = prox + 2;
            slots[prox].childDirInBoneLocal = slots[prox].bone.InverseTransformPoint(slots[inter].bone.position).normalized;
            slots[inter].childDirInBoneLocal = slots[inter].bone.InverseTransformPoint(slots[dist].bone.position).normalized;
            // Distal no tiene child humanoid. Aproximamos: usamos la misma direccion que el
            // bone intermediate proyectada al frame local del distal. Como inter→dist y dist→tip
            // son aproximadamente colineales, el child dir del distal en su propio frame es
            // ~ identica a (dist.pos - inter.pos) re-expresada en local del distal.
            Vector3 segDir = (slots[dist].bone.position - slots[inter].bone.position).normalized;
            slots[dist].childDirInBoneLocal = slots[dist].bone.InverseTransformDirection(segDir);
        }
        return true;
    }

    /// <summary>
    /// Aplica rotaciones a los 15 finger bones del lado dado, usando los joints sincronizados.
    /// Se driveea en orden Prox → Int → Dist para que cada bone afecte la posicion mundial de
    /// los siguientes antes de re-evaluarlos.
    /// </summary>
    private void DriveFingerBones(FingerBoneSlot[] slots, ref NetworkedAvatarPose.HandFingerPose fp, bool isLeft)
    {
        bool valid = isLeft ? fp.lValid : fp.rValid;
        if (!valid) return;

        // Acceder a los 4 joints por dedo (en avatar-root-local) y convertirlos a world.
        // Para thumb: usamos ThumbMetacarpal (vive en AvatarPoseState), no en fp.
        var pose = poseSync.Pose.Value;
        Vector3 thumbMetW = _avatarRoot.TransformPoint(isLeft ? pose.leftThumbMetacarpalPosLocal : pose.rightThumbMetacarpalPosLocal);

        // THUMB: 3 segments
        Vector3 thumbProxW = _avatarRoot.TransformPoint(isLeft ? fp.lThumbProx : fp.rThumbProx);
        Vector3 thumbDistW = _avatarRoot.TransformPoint(isLeft ? fp.lThumbDist : fp.rThumbDist);
        Vector3 thumbTipW = _avatarRoot.TransformPoint(isLeft ? fp.lThumbTip : fp.rThumbTip);
        AlignBoneToSegment(slots[(int)FB.ThumbProx], thumbMetW, thumbProxW);
        AlignBoneToSegment(slots[(int)FB.ThumbInt], thumbProxW, thumbDistW);
        AlignBoneToSegment(slots[(int)FB.ThumbDist], thumbDistW, thumbTipW);

        // INDEX
        Vector3 idxProxW = _avatarRoot.TransformPoint(isLeft ? fp.lIndexProx : fp.rIndexProx);
        Vector3 idxIntW = _avatarRoot.TransformPoint(isLeft ? fp.lIndexInt : fp.rIndexInt);
        Vector3 idxDistW = _avatarRoot.TransformPoint(isLeft ? fp.lIndexDist : fp.rIndexDist);
        Vector3 idxTipW = _avatarRoot.TransformPoint(isLeft ? fp.lIndexTip : fp.rIndexTip);
        AlignBoneToSegment(slots[(int)FB.IndexProx], idxProxW, idxIntW);
        AlignBoneToSegment(slots[(int)FB.IndexInt], idxIntW, idxDistW);
        AlignBoneToSegment(slots[(int)FB.IndexDist], idxDistW, idxTipW);

        // MIDDLE
        Vector3 midProxW = _avatarRoot.TransformPoint(isLeft ? fp.lMidProx : fp.rMidProx);
        Vector3 midIntW = _avatarRoot.TransformPoint(isLeft ? fp.lMidInt : fp.rMidInt);
        Vector3 midDistW = _avatarRoot.TransformPoint(isLeft ? fp.lMidDist : fp.rMidDist);
        Vector3 midTipW = _avatarRoot.TransformPoint(isLeft ? fp.lMidTip : fp.rMidTip);
        AlignBoneToSegment(slots[(int)FB.MidProx], midProxW, midIntW);
        AlignBoneToSegment(slots[(int)FB.MidInt], midIntW, midDistW);
        AlignBoneToSegment(slots[(int)FB.MidDist], midDistW, midTipW);

        // RING
        Vector3 rngProxW = _avatarRoot.TransformPoint(isLeft ? fp.lRingProx : fp.rRingProx);
        Vector3 rngIntW = _avatarRoot.TransformPoint(isLeft ? fp.lRingInt : fp.rRingInt);
        Vector3 rngDistW = _avatarRoot.TransformPoint(isLeft ? fp.lRingDist : fp.rRingDist);
        Vector3 rngTipW = _avatarRoot.TransformPoint(isLeft ? fp.lRingTip : fp.rRingTip);
        AlignBoneToSegment(slots[(int)FB.RingProx], rngProxW, rngIntW);
        AlignBoneToSegment(slots[(int)FB.RingInt], rngIntW, rngDistW);
        AlignBoneToSegment(slots[(int)FB.RingDist], rngDistW, rngTipW);

        // LITTLE
        Vector3 litProxW = _avatarRoot.TransformPoint(isLeft ? fp.lLitProx : fp.rLitProx);
        Vector3 litIntW = _avatarRoot.TransformPoint(isLeft ? fp.lLitInt : fp.rLitInt);
        Vector3 litDistW = _avatarRoot.TransformPoint(isLeft ? fp.lLitDist : fp.rLitDist);
        Vector3 litTipW = _avatarRoot.TransformPoint(isLeft ? fp.lLitTip : fp.rLitTip);
        AlignBoneToSegment(slots[(int)FB.LitProx], litProxW, litIntW);
        AlignBoneToSegment(slots[(int)FB.LitInt], litIntW, litDistW);
        AlignBoneToSegment(slots[(int)FB.LitDist], litDistW, litTipW);
    }

    /// <summary>
    /// Rota un finger bone de modo que su direccion "hacia el child" (definida en frame local
    /// del bone, capturada del rig) apunte en world hacia el target. Usa FromToRotation que da
    /// la rotacion minima entre dos vectores: no impone "twist" (la rotacion alrededor del eje
    /// del dedo queda como estaba). Eso esta bien para dedos cilindricos.
    /// </summary>
    private static void AlignBoneToSegment(FingerBoneSlot slot, Vector3 segStartWorld, Vector3 segEndWorld)
    {
        if (slot.bone == null) return;
        Vector3 targetDir = segEndWorld - segStartWorld;
        if (targetDir.sqrMagnitude < 1e-8f) return;
        targetDir.Normalize();

        Vector3 currentDir = slot.bone.TransformDirection(slot.childDirInBoneLocal);
        Quaternion delta = Quaternion.FromToRotation(currentDir, targetDir);
        slot.bone.rotation = delta * slot.bone.rotation;
    }

    /// <summary>
    /// Captura la orientacion de la palma en el frame local del hand bone, una sola vez. La
    /// captura solo es valida cuando los finger bones estan en posiciones razonables (a > 5mm
    /// del hand bone). Si los bones todavia no estan inicializados (p.ej. el rig builder no
    /// arranco en el cliente), reintenta el siguiente frame.
    ///
    /// Esta cantidad es INVARIANTE a la pose del avatar porque usa InverseTransformPoint, que
    /// proyecta al frame local del hand bone. La relacion espacial entre hand bone y sus
    /// finger children es geometrica del rig: no cambia con la pose.
    /// </summary>
    private void TryCapturePalmBind()
    {
        if (_palmBindCaptured) return;
        if (_leftHandBone == null || _leftMiddleProxBone == null || _leftThumbProxBone == null) return;
        if (_rightHandBone == null || _rightMiddleProxBone == null || _rightThumbProxBone == null) return;

        // Distance check: si los joints estan colapsados (todos en el mismo punto), el rig no
        // termino de inicializarse — reintentar.
        if (Vector3.Distance(_leftHandBone.position, _leftMiddleProxBone.position) < 0.005f) return;
        if (Vector3.Distance(_rightHandBone.position, _rightMiddleProxBone.position) < 0.005f) return;

        _leftPalmRotInHand = ComputePalmRotInBoneLocalFrame(_leftHandBone, _leftMiddleProxBone, _leftThumbProxBone, isLeft: true);
        _rightPalmRotInHand = ComputePalmRotInBoneLocalFrame(_rightHandBone, _rightMiddleProxBone, _rightThumbProxBone, isLeft: false);
        _palmBindCaptured = true;

        Debug.Log($"[AvatarPoseDriver] Palm bind captured on '{gameObject.name}': " +
                  $"left={_leftPalmRotInHand.eulerAngles.ToString("F1")}, " +
                  $"right={_rightPalmRotInHand.eulerAngles.ToString("F1")}.");
    }

    /// <summary>
    /// Computa la rotacion que representa "la palma" en el frame local del hand bone, dadas
    /// las posiciones (en world) del hand bone y dos finger bones de referencia.
    /// Para LEFT, el cross product de (forward, thumbDir) apunta para "abajo" del dorso de la
    /// mano (porque el thumb queda a la derecha del wrist en convencion human-anatomy). Lo
    /// flippeamos para que el "up" de la palma siempre apunte AL DORSO de la mano. Misma
    /// convencion usada para el lado del usuario, asi se cancelan.
    /// </summary>
    private static Quaternion ComputePalmRotInBoneLocalFrame(Transform hand, Transform middle, Transform thumb, bool isLeft)
    {
        // Los posiciones de middle y thumb expresadas en el frame del hand bone son
        // constantes (no dependen de la pose del avatar, solo del rig hierarchy).
        Vector3 middleLocal = hand.InverseTransformPoint(middle.position);
        Vector3 thumbLocal = hand.InverseTransformPoint(thumb.position);

        Vector3 forward = middleLocal.normalized;
        Vector3 thumbDir = thumbLocal.normalized;
        Vector3 normal = Vector3.Cross(forward, thumbDir).normalized;
        if (isLeft) normal = -normal;

        return Quaternion.LookRotation(forward, normal);
    }

    /// <summary>
    /// Computa la rotacion de la palma del usuario en world space, desde 3 joints en world.
    /// Mismo metodo y convencion (incluido el flip de la izquierda) que el lado del avatar.
    /// </summary>
    private static bool TryComputePalmRotation(Vector3 wrist, Vector3 middle, Vector3 thumb, bool isLeft, out Quaternion result)
    {
        result = Quaternion.identity;
        Vector3 forward = middle - wrist;
        Vector3 thumbDir = thumb - wrist;
        // Degenerate cases (joints colapsados o vectores casi paralelos): rechazar.
        if (forward.sqrMagnitude < 1e-6f) return false;
        if (thumbDir.sqrMagnitude < 1e-6f) return false;
        forward.Normalize();
        thumbDir.Normalize();
        Vector3 normal = Vector3.Cross(forward, thumbDir);
        if (normal.sqrMagnitude < 1e-6f) return false; // forward y thumb casi paralelos
        normal.Normalize();
        if (isLeft) normal = -normal;
        result = Quaternion.LookRotation(forward, normal);
        return true;
    }
}
