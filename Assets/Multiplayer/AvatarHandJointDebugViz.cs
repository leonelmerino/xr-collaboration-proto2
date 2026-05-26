using UnityEngine;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Hands;

/// <summary>
/// Visualizacion de diagnostico para validar la estrategia de alineacion geometrica de manos.
///
/// Muestra en escena, en tiempo real:
/// - USER SIDE (verde):  posicion de 3 joints de la mano real (wrist, middle metacarpal, thumb
///   metacarpal) leidos desde el XRHandSubsystem.
/// - AVATAR SIDE (rojo): posicion equivalente en los bones del avatar humanoid (RightHand,
///   RightMiddleProximal, RightThumbProximal y sus equivalentes izquierdos).
/// - Ejes derivados (magenta = fingers forward, cian = palm normal) en cada lado.
///
/// Si los ejes apuntan en direcciones COINCIDENTES cuando la mano real esta en la misma
/// orientacion que la mano del avatar, entonces el algoritmo de alineacion geometrica va a
/// dar el resultado correcto sin necesidad de offsets manuales.
///
/// SETUP: agregar este componente a cualquier GameObject de la escena (p.ej. el mismo que
/// tiene AvatarHandTunerHUD). El script se busca solito las referencias en runtime.
/// </summary>
public class AvatarHandJointDebugViz : MonoBehaviour
{
    [Header("Display options")]
    // Default = false: en uso normal no se ven las esferas/lineas de diagnostico. Si querés
    // re-habilitarlo para diagnosticar problemas de alineacion de manos, apretá F5 en runtime.
    [SerializeField] private bool showViz = false;
    [SerializeField] private KeyCode toggleKey = KeyCode.F5;

    [Header("Geometry")]
    [Tooltip("Radio de las esferas marcadoras de joints.")]
    [SerializeField] private float sphereRadius = 0.012f;

    [Tooltip("Largo de las flechas de eje (en metros).")]
    [SerializeField] private float axisLength = 0.08f;

    [Tooltip("Ancho de las lineas de eje.")]
    [SerializeField] private float lineWidth = 0.004f;

    [Header("Colors")]
    [SerializeField] private Color userJointColor = new Color(0.2f, 1f, 0.2f);   // verde
    [SerializeField] private Color avatarJointColor = new Color(1f, 0.2f, 0.2f); // rojo
    [SerializeField] private Color forwardColor = new Color(1f, 0.2f, 1f);       // magenta
    [SerializeField] private Color normalColor = new Color(0.2f, 1f, 1f);        // cian

    [Header("Show options")]
    [SerializeField] private bool showUserSide = true;
    [SerializeField] private bool showAvatarSide = true;
    [SerializeField] private bool showAxisLines = true;

    // Cached refs.
    private NetworkedAvatarPose _ownerPose;
    private XRHandSubsystem _handSubsystem;
    private Transform _conversionSpace;
    private Animator _ownerAvatarAnimator; // Animator del sub-mesh activo del avatar local.

    // Pool de objetos viz. Por mano: 3 sphere + 2 line (forward, normal). Total = 5 por mano.
    private HandViz _userLeft, _userRight, _avatarLeft, _avatarRight;

    private class HandViz
    {
        public Transform wristSphere;
        public Transform middleSphere;
        public Transform thumbSphere;
        public LineRenderer forwardLine;
        public LineRenderer normalLine;
        public LineRenderer wristToMiddleLine;
        public LineRenderer wristToThumbLine;
    }

    private void Awake()
    {
        _userLeft = CreateHandViz("DebugViz_UserL", userJointColor);
        _userRight = CreateHandViz("DebugViz_UserR", userJointColor);
        _avatarLeft = CreateHandViz("DebugViz_AvatarL", avatarJointColor);
        _avatarRight = CreateHandViz("DebugViz_AvatarR", avatarJointColor);
    }

    private void OnEnable()
    {
        ApplyVisibility(showViz);
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            showViz = !showViz;
            ApplyVisibility(showViz);
        }
        if (!showViz) return;

        TryAcquireRefs();

        // Lado usuario: solo en el owner local (donde XR Hands esta activo).
        if (showUserSide && _ownerPose != null && _ownerPose.IsOwner &&
            _handSubsystem != null && _conversionSpace != null)
        {
            UpdateUserViz(_handSubsystem.leftHand, _userLeft, /*isLeft*/ true);
            UpdateUserViz(_handSubsystem.rightHand, _userRight, /*isLeft*/ false);
        }
        else
        {
            SetActive(_userLeft, false);
            SetActive(_userRight, false);
        }

        // Lado avatar: si tenemos animator del avatar local.
        if (showAvatarSide && _ownerAvatarAnimator != null && _ownerAvatarAnimator.isHuman)
        {
            UpdateAvatarViz(_ownerAvatarAnimator,
                            HumanBodyBones.LeftHand,
                            HumanBodyBones.LeftMiddleProximal,
                            HumanBodyBones.LeftThumbProximal,
                            _avatarLeft, /*isLeft*/ true);
            UpdateAvatarViz(_ownerAvatarAnimator,
                            HumanBodyBones.RightHand,
                            HumanBodyBones.RightMiddleProximal,
                            HumanBodyBones.RightThumbProximal,
                            _avatarRight, /*isLeft*/ false);
        }
        else
        {
            SetActive(_avatarLeft, false);
            SetActive(_avatarRight, false);
        }
    }

    private void TryAcquireRefs()
    {
        if (_ownerPose == null || !_ownerPose.IsSpawned || !_ownerPose.IsOwner)
        {
            var all = FindObjectsOfType<NetworkedAvatarPose>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].IsSpawned && all[i].IsOwner) { _ownerPose = all[i]; break; }
            }
        }

        if (_ownerPose != null && _ownerAvatarAnimator == null)
        {
            // Buscar el AvatarPoseDriver activo (la sub-mesh del rol asignado al owner).
            var drivers = _ownerPose.GetComponentsInChildren<AvatarPoseDriver>(includeInactive: false);
            if (drivers.Length > 0)
                _ownerAvatarAnimator = drivers[0].GetComponent<Animator>();
        }

        if (_handSubsystem == null)
        {
            var list = new System.Collections.Generic.List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(list);
            if (list.Count > 0) _handSubsystem = list[0];
        }

        if (_conversionSpace == null)
        {
            var origin = FindObjectOfType<XROrigin>();
            if (origin != null)
            {
                _conversionSpace = origin.CameraFloorOffsetObject != null
                    ? origin.CameraFloorOffsetObject.transform
                    : origin.transform;
            }
        }
    }

    private void UpdateUserViz(XRHand hand, HandViz viz, bool isLeft)
    {
        if (!hand.isTracked) { SetActive(viz, false); return; }
        if (!TryGetJointWorld(hand, XRHandJointID.Wrist, out Vector3 wristW)) { SetActive(viz, false); return; }
        if (!TryGetJointWorld(hand, XRHandJointID.MiddleMetacarpal, out Vector3 midW)) { SetActive(viz, false); return; }
        if (!TryGetJointWorld(hand, XRHandJointID.ThumbMetacarpal, out Vector3 thumbW)) { SetActive(viz, false); return; }

        SetActive(viz, true);
        viz.wristSphere.position = wristW;
        viz.middleSphere.position = midW;
        viz.thumbSphere.position = thumbW;

        // Ejes derivados de la base ortonormal.
        Vector3 forward = (midW - wristW).normalized;
        Vector3 thumbDir = (thumbW - wristW).normalized;
        Vector3 normal = Vector3.Cross(forward, thumbDir).normalized;
        // Para la mano izquierda, el thumb queda a la "derecha" del wrist en coord humanas;
        // el cross product de (forward, thumb) apunta para abajo. Para que la normal apunte
        // HACIA ARRIBA del dorso de la mano siempre, flippeamos en la izquierda.
        if (isLeft) normal = -normal;

        UpdateAxisLines(viz, wristW, forward, normal);
        UpdateConnectorLines(viz, wristW, midW, thumbW);
    }

    private void UpdateAvatarViz(Animator animator, HumanBodyBones handB, HumanBodyBones midB,
                                  HumanBodyBones thumbB, HandViz viz, bool isLeft)
    {
        Transform handT = animator.GetBoneTransform(handB);
        Transform midT = animator.GetBoneTransform(midB);
        Transform thumbT = animator.GetBoneTransform(thumbB);
        if (handT == null || midT == null || thumbT == null) { SetActive(viz, false); return; }

        Vector3 handW = handT.position;
        Vector3 midW = midT.position;
        Vector3 thumbW = thumbT.position;
        // Sanity: si los joints estan colapsados (mismo punto), el rig todavia no inicializo.
        if (Vector3.Distance(handW, midW) < 0.005f) { SetActive(viz, false); return; }

        SetActive(viz, true);
        viz.wristSphere.position = handW;
        viz.middleSphere.position = midW;
        viz.thumbSphere.position = thumbW;

        Vector3 forward = (midW - handW).normalized;
        Vector3 thumbDir = (thumbW - handW).normalized;
        Vector3 normal = Vector3.Cross(forward, thumbDir).normalized;
        if (isLeft) normal = -normal;

        UpdateAxisLines(viz, handW, forward, normal);
        UpdateConnectorLines(viz, handW, midW, thumbW);
    }

    private bool TryGetJointWorld(XRHand hand, XRHandJointID id, out Vector3 worldPos)
    {
        worldPos = Vector3.zero;
        var joint = hand.GetJoint(id);
        if (!joint.TryGetPose(out Pose p)) return false;
        worldPos = _conversionSpace.TransformPoint(p.position);
        return true;
    }

    private void UpdateAxisLines(HandViz viz, Vector3 origin, Vector3 forward, Vector3 normal)
    {
        if (!showAxisLines)
        {
            viz.forwardLine.enabled = false;
            viz.normalLine.enabled = false;
            return;
        }
        viz.forwardLine.enabled = true;
        viz.normalLine.enabled = true;
        viz.forwardLine.SetPosition(0, origin);
        viz.forwardLine.SetPosition(1, origin + forward * axisLength);
        viz.normalLine.SetPosition(0, origin);
        viz.normalLine.SetPosition(1, origin + normal * axisLength);
    }

    private void UpdateConnectorLines(HandViz viz, Vector3 wrist, Vector3 mid, Vector3 thumb)
    {
        viz.wristToMiddleLine.SetPosition(0, wrist);
        viz.wristToMiddleLine.SetPosition(1, mid);
        viz.wristToThumbLine.SetPosition(0, wrist);
        viz.wristToThumbLine.SetPosition(1, thumb);
    }

    private HandViz CreateHandViz(string namePrefix, Color sphereColor)
    {
        var v = new HandViz();
        v.wristSphere = CreateSphere(namePrefix + "_Wrist", sphereColor);
        v.middleSphere = CreateSphere(namePrefix + "_Middle", sphereColor);
        v.thumbSphere = CreateSphere(namePrefix + "_Thumb", sphereColor);
        v.forwardLine = CreateLine(namePrefix + "_AxisForward", forwardColor);
        v.normalLine = CreateLine(namePrefix + "_AxisNormal", normalColor);
        // Connector lines mas finitos.
        v.wristToMiddleLine = CreateLine(namePrefix + "_ConnMid", sphereColor, lineWidth * 0.5f);
        v.wristToThumbLine = CreateLine(namePrefix + "_ConnThumb", sphereColor, lineWidth * 0.5f);
        return v;
    }

    private Transform CreateSphere(string name, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.localScale = Vector3.one * sphereRadius * 2f;
        // Sin colliders, sin sombras.
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        var rend = go.GetComponent<MeshRenderer>();
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;
        // Material desechable basado en Unlit/Color para que el color se vea aun sin lights.
        var mat = new Material(Shader.Find("Unlit/Color"));
        if (mat.shader == null) mat = new Material(Shader.Find("Standard"));
        mat.color = color;
        rend.sharedMaterial = mat;
        return go.transform;
    }

    private LineRenderer CreateLine(string name, Color color, float width = -1f)
    {
        var go = new GameObject(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.startWidth = width > 0 ? width : lineWidth;
        lr.endWidth = lr.startWidth;
        lr.useWorldSpace = true;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.color = color;
        lr.sharedMaterial = mat;
        lr.startColor = color;
        lr.endColor = color;
        return lr;
    }

    private void SetActive(HandViz viz, bool active)
    {
        if (viz == null) return;
        if (viz.wristSphere != null) viz.wristSphere.gameObject.SetActive(active);
        if (viz.middleSphere != null) viz.middleSphere.gameObject.SetActive(active);
        if (viz.thumbSphere != null) viz.thumbSphere.gameObject.SetActive(active);
        if (viz.forwardLine != null) viz.forwardLine.gameObject.SetActive(active);
        if (viz.normalLine != null) viz.normalLine.gameObject.SetActive(active);
        if (viz.wristToMiddleLine != null) viz.wristToMiddleLine.gameObject.SetActive(active);
        if (viz.wristToThumbLine != null) viz.wristToThumbLine.gameObject.SetActive(active);
    }

    private void ApplyVisibility(bool active)
    {
        SetActive(_userLeft, active && showUserSide);
        SetActive(_userRight, active && showUserSide);
        SetActive(_avatarLeft, active && showAvatarSide);
        SetActive(_avatarRight, active && showAvatarSide);
    }

    private void OnDestroy()
    {
        DestroyHandViz(_userLeft);
        DestroyHandViz(_userRight);
        DestroyHandViz(_avatarLeft);
        DestroyHandViz(_avatarRight);
    }

    private void DestroyHandViz(HandViz v)
    {
        if (v == null) return;
        if (v.wristSphere != null) Destroy(v.wristSphere.gameObject);
        if (v.middleSphere != null) Destroy(v.middleSphere.gameObject);
        if (v.thumbSphere != null) Destroy(v.thumbSphere.gameObject);
        if (v.forwardLine != null) Destroy(v.forwardLine.gameObject);
        if (v.normalLine != null) Destroy(v.normalLine.gameObject);
        if (v.wristToMiddleLine != null) Destroy(v.wristToMiddleLine.gameObject);
        if (v.wristToThumbLine != null) Destroy(v.wristToThumbLine.gameObject);
    }
}
