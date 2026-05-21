using System.Collections.Generic;
using Unity.Netcode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Hands;

/// <summary>
/// Sincronizacion + rendering del esqueleto de manos en avatares NGO.
///
/// Lo agregas al Avatar.prefab (sibling de NetworkedAvatarHands y NetworkedAvatarRole).
///
/// Comportamiento:
/// - OWNER: cada Update samplea los 20 joints por mano desde XR Hands y los escribe
///   a la NetworkVariable de estado. NO renderiza (HandSkeletonRenderer de escena
///   sigue dibujando el esqueleto local con latencia 0).
/// - REMOTE: cada LateUpdate lee el estado sincronizado y dibuja el esqueleto con
///   Graphics.DrawMeshInstanced + LineRenderers, usando el color del rol del avatar
///   (leido de NetworkedAvatarRole co-locado).
///
/// No interfiere con poke/drag/raycast:
/// - Las esferas son DrawMeshInstanced (pura GPU, sin GameObjects ni colliders).
/// - Los LineRenderer cuelgan de "RemoteBones/{Left,Right}" sin colliders.
/// - El componente solo renderiza cuando NO es owner, asi el dueno usa el sistema
///   de escena de Paso 1.
/// </summary>
public class NetworkedAvatarSkeleton : NetworkBehaviour
{
    [Header("Joints (esferas)")]
    [Tooltip("Mesh de esfera para los joints. Si esta vacio se usa la built-in al spawn.")]
    [SerializeField] private Mesh sphereMesh;

    [Tooltip("Material para los joints. Debe tener 'Enable GPU Instancing'. Si esta vacio se crea " +
             "un Standard runtime default. Cada avatar puede tener su material propio (uno por skeleton).")]
    [SerializeField] private Material jointMaterial;

    [Tooltip("Radio en metros de cada esfera. 3-5 mm tipico.")]
    [SerializeField, Range(0.001f, 0.02f)] private float jointRadius = 0.004f;

    [Tooltip("Color usado si el RoleAssignmentService no esta disponible. Conserva su alpha al " +
             "aplicar el color del rol.")]
    [SerializeField] private Color fallbackColor = new Color(1f, 1f, 1f, 0.9f);

    [Header("Bones (huesos)")]
    [SerializeField] private bool drawBones = true;
    [SerializeField] private Material boneMaterial;
    [SerializeField, Range(0.0005f, 0.01f)] private float boneWidth = 0.0025f;

    [Header("Referencias")]
    [Tooltip("NetworkedAvatarRole en el mismo GameObject. Si esta vacio se busca con GetComponent.")]
    [SerializeField] private NetworkedAvatarRole roleSource;

    // Misma tabla que HandSkeletonRenderer. El orden importa para s_fingerChains.
    private static readonly XRHandJointID[] s_jointIds = new XRHandJointID[]
    {
        XRHandJointID.Wrist,
        XRHandJointID.ThumbProximal,    XRHandJointID.ThumbDistal,        XRHandJointID.ThumbTip,
        XRHandJointID.IndexProximal,    XRHandJointID.IndexIntermediate,  XRHandJointID.IndexDistal,  XRHandJointID.IndexTip,
        XRHandJointID.MiddleProximal,   XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
        XRHandJointID.RingProximal,     XRHandJointID.RingIntermediate,   XRHandJointID.RingDistal,   XRHandJointID.RingTip,
        XRHandJointID.LittleProximal,   XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip,
    };

    private static readonly int[][] s_fingerChains = new int[][]
    {
        new int[] { 0, 1, 2, 3 },
        new int[] { 0, 4, 5, 6, 7 },
        new int[] { 0, 8, 9, 10, 11 },
        new int[] { 0, 12, 13, 14, 15 },
        new int[] { 0, 16, 17, 18, 19 },
    };

    private const int JointsPerHand = HandSkeletonState.JointsPerHand;
    private const int MaxInstances = JointsPerHand * 2;

    /// <summary>
    /// Estado de los 20 joints x 2 manos. Owner escribe, todos leen. NGO compara via
    /// HandSkeletonState.Equals y solo broadcasta cuando hay diferencias.
    /// </summary>
    private readonly NetworkVariable<HandSkeletonState> _state = new NetworkVariable<HandSkeletonState>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    // --- Owner-side sampling ---
    private XROrigin xrOrigin;
    private Transform conversionSpace;
    private XRHandSubsystem handSubsystem;
    private HandSkeletonState _scratch; // buffer mutable para sampleo del owner

    // --- Remote-side render ---
    private Mesh _renderMesh;
    private bool _materialOwned;
    private MaterialPropertyBlock _mpb;
    private Matrix4x4[] _matrices = new Matrix4x4[MaxInstances];
    private int _activeCount;
    private LineRenderer[] _bonesLeft;
    private LineRenderer[] _bonesRight;
    private GameObject _bonesRoot;
    private Color _currentColor;
    private static readonly int s_ColorProperty = Shader.PropertyToID("_Color");

    private void Awake()
    {
        if (roleSource == null) roleSource = GetComponent<NetworkedAvatarRole>();
        _currentColor = fallbackColor;
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            // Owner solo necesita sampleo. El render local lo hace HandSkeletonRenderer de escena.
            return;
        }

        // Remote: inicializar pipeline de render y bindear color al rol.
        EnsureSphereMesh();
        EnsureMaterial();
        if (_mpb == null) _mpb = new MaterialPropertyBlock();

        if (roleSource != null)
        {
            roleSource.Role.OnValueChanged += HandleRoleChanged;
            ApplyRoleColor(roleSource.Role.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (roleSource != null)
        {
            try { roleSource.Role.OnValueChanged -= HandleRoleChanged; } catch { }
        }
    }

    private void OnDestroy()
    {
        if (_materialOwned && jointMaterial != null)
            Destroy(jointMaterial);
        // _bonesRoot y sus LineRenderers cuelgan del transform del avatar y se destruyen con el.
    }

    // ----------------- OWNER: sampleo + escritura a NetworkVariable -----------------

    private void Update()
    {
        if (!IsSpawned || !IsOwner) return;
        EnsureXrRefs();
        if (handSubsystem == null) return;

        bool leftOk = SampleHand(handSubsystem.leftHand, ref _scratch, isLeft: true);
        bool rightOk = SampleHand(handSubsystem.rightHand, ref _scratch, isLeft: false);

        _scratch.leftTracked = leftOk;
        _scratch.rightTracked = rightOk;

        // Assign by value. NGO compara con previous via HandSkeletonState.Equals y solo
        // broadcasta si hay diff (incluyendo "no tracked -> tracked" o cualquier cambio de joint).
        _state.Value = _scratch;
    }

    private bool SampleHand(XRHand hand, ref HandSkeletonState state, bool isLeft)
    {
        if (!hand.isTracked) return false;

        for (int i = 0; i < s_jointIds.Length; i++)
        {
            var joint = hand.GetJoint(s_jointIds[i]);
            if (!joint.TryGetPose(out var pose)) return false;

            Vector3 worldPos = conversionSpace != null
                ? conversionSpace.TransformPoint(pose.position)
                : pose.position;

            if (isLeft) state.SetLeft(i, worldPos);
            else state.SetRight(i, worldPos);
        }
        return true;
    }

    private void EnsureXrRefs()
    {
        if (xrOrigin == null) xrOrigin = FindObjectOfType<XROrigin>();
        if (xrOrigin != null && conversionSpace == null)
        {
            conversionSpace = xrOrigin.CameraFloorOffsetObject != null
                ? xrOrigin.CameraFloorOffsetObject.transform
                : xrOrigin.transform;
        }
        if (handSubsystem == null)
        {
            var list = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(list);
            if (list.Count > 0) handSubsystem = list[0];
        }
    }

    // ----------------- REMOTE: render desde el estado sincronizado -----------------

    private void LateUpdate()
    {
        if (!IsSpawned || IsOwner) return;

        var s = _state.Value;
        _activeCount = 0;

        if (s.leftTracked) AppendHandMatrices(ref s, isLeft: true);
        if (s.rightTracked) AppendHandMatrices(ref s, isLeft: false);

        DrawJoints();

        if (drawBones)
        {
            EnsureBoneRenderers();
            UpdateBones(_bonesLeft, ref s, isLeft: true);
            UpdateBones(_bonesRight, ref s, isLeft: false);
        }
    }

    private void AppendHandMatrices(ref HandSkeletonState s, bool isLeft)
    {
        Vector3 scale = Vector3.one * (jointRadius * 2f);
        Quaternion rot = Quaternion.identity;
        for (int i = 0; i < JointsPerHand && _activeCount < MaxInstances; i++)
        {
            Vector3 p = isLeft ? s.GetLeft(i) : s.GetRight(i);
            _matrices[_activeCount++] = Matrix4x4.TRS(p, rot, scale);
        }
    }

    private void DrawJoints()
    {
        if (_activeCount == 0 || _renderMesh == null || jointMaterial == null) return;
        Graphics.DrawMeshInstanced(
            _renderMesh,
            submeshIndex: 0,
            material: jointMaterial,
            matrices: _matrices,
            count: _activeCount,
            properties: _mpb,
            castShadows: ShadowCastingMode.Off,
            receiveShadows: false,
            layer: gameObject.layer,
            camera: null,
            lightProbeUsage: LightProbeUsage.Off
        );
    }

    private void UpdateBones(LineRenderer[] bones, ref HandSkeletonState s, bool isLeft)
    {
        if (bones == null) return;
        bool tracked = isLeft ? s.leftTracked : s.rightTracked;

        if (!tracked)
        {
            for (int f = 0; f < bones.Length; f++)
                if (bones[f] != null) bones[f].enabled = false;
            return;
        }

        for (int f = 0; f < s_fingerChains.Length; f++)
        {
            var chain = s_fingerChains[f];
            var lr = bones[f];
            if (lr == null) continue;

            lr.enabled = true;
            if (lr.positionCount != chain.Length) lr.positionCount = chain.Length;
            if (!Mathf.Approximately(lr.startWidth, boneWidth))
            {
                lr.startWidth = boneWidth;
                lr.endWidth = boneWidth;
            }
            for (int p = 0; p < chain.Length; p++)
            {
                Vector3 pos = isLeft ? s.GetLeft(chain[p]) : s.GetRight(chain[p]);
                lr.SetPosition(p, pos);
            }
        }
    }

    private void EnsureBoneRenderers()
    {
        if (_bonesLeft != null && _bonesRight != null) return;

        if (_bonesRoot == null)
        {
            _bonesRoot = new GameObject("RemoteBones");
            _bonesRoot.transform.SetParent(transform, false);
            _bonesRoot.layer = gameObject.layer;
        }
        if (_bonesLeft == null) _bonesLeft = CreateBoneSet("Left");
        if (_bonesRight == null) _bonesRight = CreateBoneSet("Right");
    }

    private LineRenderer[] CreateBoneSet(string label)
    {
        var parent = new GameObject(label);
        parent.transform.SetParent(_bonesRoot.transform, false);
        parent.layer = gameObject.layer;

        Material matToUse = boneMaterial != null ? boneMaterial : jointMaterial;

        var lrs = new LineRenderer[s_fingerChains.Length];
        for (int f = 0; f < s_fingerChains.Length; f++)
        {
            var go = new GameObject($"Finger{f}");
            go.transform.SetParent(parent.transform, false);
            go.layer = gameObject.layer;

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = s_fingerChains[f].Length;
            lr.sharedMaterial = matToUse;
            lr.startWidth = boneWidth;
            lr.endWidth = boneWidth;
            lr.numCornerVertices = 0;
            lr.numCapVertices = 0;
            lr.alignment = LineAlignment.View;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.lightProbeUsage = LightProbeUsage.Off;
            lr.startColor = _currentColor;
            lr.endColor = _currentColor;
            lr.enabled = false;
            lrs[f] = lr;
        }
        return lrs;
    }

    private void EnsureSphereMesh()
    {
        if (sphereMesh != null) { _renderMesh = sphereMesh; return; }
        var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _renderMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(temp);
    }

    private void EnsureMaterial()
    {
        if (jointMaterial != null)
        {
            if (!jointMaterial.enableInstancing)
                Debug.LogWarning("[NetworkedAvatarSkeleton] Material asignado sin 'Enable GPU Instancing'. " +
                                  "Las esferas pueden renderizarse superpuestas.");
            return;
        }

        Shader sh = Shader.Find("Standard");
        if (sh == null)
        {
            Debug.LogError("[NetworkedAvatarSkeleton] Shader 'Standard' no encontrado. Asigna jointMaterial en Inspector.");
            return;
        }

        // Cada avatar crea su propio material runtime para poder pintarlo con el color de su rol
        // sin afectar a otros avatares. Se libera en OnDestroy.
        jointMaterial = new Material(sh) { color = fallbackColor };
        jointMaterial.enableInstancing = true;
        if (jointMaterial.HasProperty("_Glossiness")) jointMaterial.SetFloat("_Glossiness", 0f);
        if (jointMaterial.HasProperty("_Metallic"))   jointMaterial.SetFloat("_Metallic", 0f);
        _materialOwned = true;
    }

    // ----------------- Color binding (mismo enfoque que HandSkeletonRenderer) -----------------

    private void HandleRoleChanged(PlayerRole prev, PlayerRole curr) => ApplyRoleColor(curr);

    private void ApplyRoleColor(PlayerRole role)
    {
        var svc = RoleAssignmentService.Instance;
        if (svc == null || svc.Config == null) return;
        if (!svc.Config.TryGet(role, out var entry)) return;

        Color c = entry.color;
        c.a = fallbackColor.a;
        _currentColor = c;

        // Esferas: si somos duenos del material (Standard runtime), mutamos color directo
        // porque MPB.SetColor("_Color", ...) con DrawMeshInstanced + Standard shader es no-op
        // (el _Color esta dentro del instancing buffer).
        if (_materialOwned && jointMaterial != null)
        {
            jointMaterial.color = _currentColor;
        }
        else if (_mpb != null)
        {
            _mpb.SetColor(s_ColorProperty, _currentColor);
        }

        ApplyColorToBones(_bonesLeft);
        ApplyColorToBones(_bonesRight);
    }

    private void ApplyColorToBones(LineRenderer[] bones)
    {
        if (bones == null) return;
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            bones[i].startColor = _currentColor;
            bones[i].endColor = _currentColor;
        }
    }
}
