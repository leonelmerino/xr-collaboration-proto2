using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Hands;

/// <summary>
/// Render decorativo del esqueleto de las manos del usuario local.
///
/// Lee XRHandSubsystem directamente, no es NetworkBehaviour. Cada nodo renderiza
/// sus propias manos. Los avatares remotos siguen mostrandose con el sistema
/// existente (NetworkedAvatarHands con ThumbMarker / IndexMarker / PinchLine /
/// RayDisplay). Una iteracion futura extendera HandPoseState para sincronizar
/// los joints y permitir que los demas vean los esqueletos remotos.
///
/// Composicion del esqueleto:
/// - Joints: hasta 20 esferas por mano renderizadas via Graphics.DrawMeshInstanced
///   en una sola draw call para ambas manos.
/// - Bones: 5 LineRenderers por mano (uno por dedo) con material compartido. Se
///   crean perezosamente como hijos del componente si drawBones esta activo.
///
/// NO INTERFIERE con poke / drag / raycast del Jenga:
/// - no agrega colliders,
/// - los LineRenderers no participan en physics,
/// - no toca el XR Origin ni los markers existentes,
/// - corre en LateUpdate (despues de Update / FixedUpdate, no afecta input ni physics).
/// </summary>
public class HandSkeletonRenderer : MonoBehaviour
{
    // Toggle de visuales. Si esta en false, el usuario local no ve las esferas + lineas
    // decorativas de su propio esqueleto. Las manos del avatar humanoid (driveadas por
    // AvatarPoseDriver con XR Hands) son la unica representacion.
    // Flippear a true si queres re-habilitar el render decorativo local.
    private const bool ShowVisualizers = false;

    [Header("Joints (esferas)")]
    [Tooltip("Mesh para cada joint. Si esta vacio se usa la esfera built-in de Unity (768 tris). " +
             "Para mejor rendimiento asigna un icosphere low-poly (20-80 tris).")]
    [SerializeField] private Mesh sphereMesh;

    [Tooltip("Material compartido para las esferas. DEBE tener 'Enable GPU Instancing' activado " +
             "para batchear en una sola draw call. Si esta vacio se crea un Standard runtime default.")]
    [SerializeField] private Material jointMaterial;

    [Tooltip("Radio de cada esfera en metros. 3-5 mm tipico para joints, un poco mas grande para tips.")]
    [SerializeField, Range(0.001f, 0.02f)] private float jointRadius = 0.004f;

    [Tooltip("Color del material fallback. Si pasaste tu propio material en el Inspector, este " +
             "valor se ignora.")]
    [SerializeField] private Color fallbackColor = new Color(1f, 1f, 1f, 0.9f);

    [Header("Bones (huesos)")]
    [Tooltip("Renderiza lineas conectando los joints (uno por dedo). Si false, solo se ven las esferas.")]
    [SerializeField] private bool drawBones = true;

    [Tooltip("Material para los LineRenderer de los huesos. Si esta vacio reusa jointMaterial. " +
             "Tip: si usas el mismo material para joints y bones, los engines de batching pueden " +
             "agruparlos juntos.")]
    [SerializeField] private Material boneMaterial;

    [Tooltip("Ancho de los huesos en metros. ~0.0025 (2.5 mm) combina bien con joints de 4 mm.")]
    [SerializeField, Range(0.0005f, 0.01f)] private float boneWidth = 0.0025f;

    [Header("Tracking")]
    [Tooltip("XR Origin para convertir poses de joint a world. Si esta vacio se auto-detecta.")]
    [SerializeField] private XROrigin xrOrigin;

    [Tooltip("Render la mano izquierda.")]
    [SerializeField] private bool renderLeft = true;

    [Tooltip("Render la mano derecha.")]
    [SerializeField] private bool renderRight = true;

    [Header("Role color binding")]
    [Tooltip("Si esta activo, el color del esqueleto sigue el rol del usuario local (rojo Host / " +
             "verde Client / azul Helper) leyendo de RoleAssignmentService.Config. " +
             "Si esta apagado, se usa fallbackColor (y los startColor / endColor del LineRenderer).")]
    [SerializeField] private bool bindColorToRole = true;

    [Tooltip("Cada cuantos segundos buscar el NetworkedAvatarRole local si todavia no lo encontramos. " +
             "El avatar se spawnea despues de StartHost/StartClient, asi que poll hasta que aparezca.")]
    [SerializeField, Range(0.1f, 5f)] private float roleLookupIntervalSec = 1f;

    /// <summary>
    /// Subconjunto de joints renderizados.
    /// Se omiten Palm y los Metacarpales (estan dentro de la palma y aportan poco visualmente).
    /// El orden importa porque s_fingerChains indexa esta tabla.
    /// </summary>
    private static readonly XRHandJointID[] s_jointIds = new XRHandJointID[]
    {
        XRHandJointID.Wrist,                                                                                        // 0
        XRHandJointID.ThumbProximal,    XRHandJointID.ThumbDistal,        XRHandJointID.ThumbTip,                   // 1, 2, 3 (pulgar sin Intermediate)
        XRHandJointID.IndexProximal,    XRHandJointID.IndexIntermediate,  XRHandJointID.IndexDistal,  XRHandJointID.IndexTip,   // 4-7
        XRHandJointID.MiddleProximal,   XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,  // 8-11
        XRHandJointID.RingProximal,     XRHandJointID.RingIntermediate,   XRHandJointID.RingDistal,   XRHandJointID.RingTip,    // 12-15
        XRHandJointID.LittleProximal,   XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip,  // 16-19
    };

    /// <summary>
    /// Cadenas de hueso por dedo, indices en s_jointIds.
    /// Cada cadena = una LineRenderer. Empieza en wrist y baja por el dedo.
    /// </summary>
    private static readonly int[][] s_fingerChains = new int[][]
    {
        new int[] { 0, 1, 2, 3 },         // Pulgar: wrist -> proximal -> distal -> tip (4 puntos)
        new int[] { 0, 4, 5, 6, 7 },      // Indice: wrist -> prox -> inter -> distal -> tip (5 puntos)
        new int[] { 0, 8, 9, 10, 11 },    // Medio
        new int[] { 0, 12, 13, 14, 15 },  // Anular
        new int[] { 0, 16, 17, 18, 19 },  // Menique
    };

    private const int JointsPerHand = 20;
    private const int MaxInstances = JointsPerHand * 2;

    private XRHandSubsystem handSubsystem;
    private Transform conversionSpace;

    private readonly Vector3[] _leftJointPositions = new Vector3[JointsPerHand];
    private readonly Vector3[] _rightJointPositions = new Vector3[JointsPerHand];
    private bool _leftValid, _rightValid;

    private readonly Matrix4x4[] _matrices = new Matrix4x4[MaxInstances];
    private int _activeCount;
    private MaterialPropertyBlock _mpb;

    private LineRenderer[] _bonesLeft;
    private LineRenderer[] _bonesRight;
    private GameObject _bonesRoot;

    private bool _materialOwned;

    // Role binding state.
    private NetworkedAvatarRole _localRole;
    private float _nextRoleLookupTime;
    private Color _currentColor;
    private static readonly int s_ColorProperty = Shader.PropertyToID("_Color");

    private void Awake()
    {
        EnsureSphereMesh();
        EnsureMaterial();
        _mpb = new MaterialPropertyBlock();
        _currentColor = fallbackColor;
        // Si el material default ya viene con fallbackColor, no hace falta tocar el MPB todavia.
        // Cuando llegue el rol, sobreescribimos el _Color via MPB.
    }

    private void OnDestroy()
    {
        UnsubscribeFromRole();
        if (_materialOwned && jointMaterial != null)
            Destroy(jointMaterial);
        // LineRenderers viven en _bonesRoot, Unity los limpia al destruir el GameObject.
    }

    private void LateUpdate()
    {
        // Si los visuales estan apagados, no renderizamos nada. Apagamos las LineRenderers
        // que pudieran haberse creado antes de flippear el flag.
        if (!ShowVisualizers)
        {
            if (_bonesLeft != null)
                for (int i = 0; i < _bonesLeft.Length; i++) if (_bonesLeft[i] != null) _bonesLeft[i].enabled = false;
            if (_bonesRight != null)
                for (int i = 0; i < _bonesRight.Length; i++) if (_bonesRight[i] != null) _bonesRight[i].enabled = false;
            return;
        }

        EnsureReferences();
        if (bindColorToRole) TryBindLocalRole();
        if (handSubsystem == null || sphereMesh == null || jointMaterial == null) return;

        _leftValid = renderLeft && SampleHand(handSubsystem.leftHand, _leftJointPositions);
        _rightValid = renderRight && SampleHand(handSubsystem.rightHand, _rightJointPositions);

        DrawJoints();
        if (drawBones)
        {
            EnsureBoneRenderers();
            UpdateBones(_bonesLeft, _leftJointPositions, _leftValid);
            UpdateBones(_bonesRight, _rightJointPositions, _rightValid);
        }
    }

    /// <summary>
    /// Busca el NetworkedAvatarRole local (IsOwner). Una vez encontrado, se suscribe a
    /// OnValueChanged y aplica el color del rol. Hace polling con intervalo configurable
    /// porque el avatar se spawnea despues de StartHost / StartClient.
    /// </summary>
    private void TryBindLocalRole()
    {
        if (_localRole != null) return;
        if (Time.unscaledTime < _nextRoleLookupTime) return;
        _nextRoleLookupTime = Time.unscaledTime + roleLookupIntervalSec;

        var all = FindObjectsOfType<NetworkedAvatarRole>();
        if (all == null || all.Length == 0)
        {
            // Avatar todavia no spawneado. Tipico antes de pulsar H / C.
            return;
        }

        int ownerCount = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].IsOwner)
            {
                ownerCount++;
                _localRole = all[i];
                _localRole.Role.OnValueChanged += HandleRoleChanged;
                ApplyRoleColor(_localRole.Role.Value);
                Debug.Log($"[HandSkeletonRenderer] Bound to local NetworkedAvatarRole on '{all[i].gameObject.name}'. " +
                          $"Role={_localRole.Role.Value} clientId={all[i].OwnerClientId}");
                return;
            }
        }

        // Encontramos avatares pero ninguno es nuestro todavia. Loggeamos una sola vez
        // por ciclo de poll para no inundar la consola.
        Debug.Log($"[HandSkeletonRenderer] Poll: {all.Length} NetworkedAvatarRole(s) en escena pero ninguno tiene IsOwner=true. " +
                  $"Probablemente todavia no terminamos de conectar. Reintentando en {roleLookupIntervalSec}s.");
    }

    private void UnsubscribeFromRole()
    {
        if (_localRole != null)
        {
            // OnValueChanged es Action<T,T>; check null por si el componente ya fue destruido.
            try { _localRole.Role.OnValueChanged -= HandleRoleChanged; } catch { }
            _localRole = null;
        }
    }

    private void HandleRoleChanged(PlayerRole previous, PlayerRole current) => ApplyRoleColor(current);

    private void ApplyRoleColor(PlayerRole role)
    {
        var svc = RoleAssignmentService.Instance;
        if (svc == null || svc.Config == null)
        {
            Debug.LogWarning("[HandSkeletonRenderer] RoleAssignmentService o Config no disponible al aplicar color.");
            return;
        }
        if (!svc.Config.TryGet(role, out var entry))
        {
            Debug.LogWarning($"[HandSkeletonRenderer] RoleConfig no tiene entry para {role}.");
            return;
        }

        // Conservamos el alpha del fallback (permite tener esqueleto semitransparente).
        Color c = entry.color;
        c.a = fallbackColor.a;
        _currentColor = c;

        // Esferas:
        // - Si somos duenos del material (fallback Standard creado en runtime), mutamos
        //   material.color directamente. ESTO ES CRITICO: en Standard shader con instancing,
        //   _Color esta dentro del UNITY_INSTANCING_BUFFER, asi que MPB.SetColor("_Color", c)
        //   con Graphics.DrawMeshInstanced es IGNORADO silenciosamente. Hay que tocar el
        //   material directamente.
        // - Si el usuario asigno su propio material, no lo mutamos (podria estar compartido
        //   entre instancias / sesiones de Editor). Intentamos via MPB como mejor esfuerzo.
        if (_materialOwned && jointMaterial != null)
        {
            jointMaterial.color = _currentColor;
        }
        else if (_mpb != null)
        {
            _mpb.SetColor(s_ColorProperty, _currentColor);
            // Nota: en Standard u otros shaders con _Color instanciado, esto no surte efecto.
            // Si necesitas color por rol sobre material custom, asegurate de que el shader
            // NO tenga _Color en el instancing buffer, o usa SetVectorArray.
        }

        // Bones: startColor / endColor son properties del LineRenderer, no del material.
        // Funcionan siempre.
        ApplyColorToBones(_bonesLeft);
        ApplyColorToBones(_bonesRight);

        Debug.Log($"[HandSkeletonRenderer] Color aplicado para rol {role}: RGBA=({_currentColor.r:F2},{_currentColor.g:F2},{_currentColor.b:F2},{_currentColor.a:F2})");
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

    /// <summary>
    /// Lee los 20 joints de la mano en world space. Devuelve true si la mano esta
    /// tracked y todos los joints respondieron pose valida.
    /// </summary>
    private bool SampleHand(XRHand hand, Vector3[] outPositions)
    {
        if (!hand.isTracked) return false;

        for (int i = 0; i < s_jointIds.Length; i++)
        {
            var joint = hand.GetJoint(s_jointIds[i]);
            if (!joint.TryGetPose(out var pose)) return false;

            outPositions[i] = conversionSpace != null
                ? conversionSpace.TransformPoint(pose.position)
                : pose.position;
        }
        return true;
    }

    private void DrawJoints()
    {
        _activeCount = 0;
        Vector3 scale = Vector3.one * (jointRadius * 2f);
        Quaternion rot = Quaternion.identity; // Esfera = invariante a rotacion.

        if (_leftValid)
            for (int i = 0; i < JointsPerHand && _activeCount < MaxInstances; i++)
                _matrices[_activeCount++] = Matrix4x4.TRS(_leftJointPositions[i], rot, scale);

        if (_rightValid)
            for (int i = 0; i < JointsPerHand && _activeCount < MaxInstances; i++)
                _matrices[_activeCount++] = Matrix4x4.TRS(_rightJointPositions[i], rot, scale);

        if (_activeCount == 0) return;

        Graphics.DrawMeshInstanced(
            sphereMesh,
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

    private void UpdateBones(LineRenderer[] bones, Vector3[] jointPositions, bool valid)
    {
        if (bones == null) return;

        if (!valid)
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
            // Sincronizar width por si el usuario lo modifico en Inspector entre frames.
            if (!Mathf.Approximately(lr.startWidth, boneWidth))
            {
                lr.startWidth = boneWidth;
                lr.endWidth = boneWidth;
            }

            for (int p = 0; p < chain.Length; p++)
                lr.SetPosition(p, jointPositions[chain[p]]);
        }
    }

    private void EnsureBoneRenderers()
    {
        if (_bonesLeft != null && _bonesRight != null) return;

        if (_bonesRoot == null)
        {
            _bonesRoot = new GameObject("Bones");
            _bonesRoot.transform.SetParent(transform, false);
            _bonesRoot.layer = gameObject.layer;
        }

        if (_bonesLeft == null) _bonesLeft = CreateBoneSet("Left");
        if (_bonesRight == null) _bonesRight = CreateBoneSet("Right");
    }

    private LineRenderer[] CreateBoneSet(string handLabel)
    {
        var parent = new GameObject(handLabel);
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

    private void EnsureReferences()
    {
        if (xrOrigin == null)
            xrOrigin = FindObjectOfType<XROrigin>();

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

    private void EnsureSphereMesh()
    {
        if (sphereMesh != null) return;

        var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(temp);
    }

    private void EnsureMaterial()
    {
        if (jointMaterial != null)
        {
            if (!jointMaterial.enableInstancing)
                Debug.LogWarning("[HandSkeletonRenderer] El material asignado no tiene 'Enable GPU Instancing' activo. " +
                                  "Las esferas pueden renderizarse superpuestas. Activa el checkbox en el material.");
            return;
        }

        Shader sh = Shader.Find("Standard");
        if (sh == null)
        {
            Debug.LogError("[HandSkeletonRenderer] No se encontro el shader 'Standard'. Asigna jointMaterial en el Inspector.");
            return;
        }

        jointMaterial = new Material(sh) { color = fallbackColor };
        jointMaterial.enableInstancing = true;
        if (jointMaterial.HasProperty("_Glossiness")) jointMaterial.SetFloat("_Glossiness", 0f);
        if (jointMaterial.HasProperty("_Metallic"))   jointMaterial.SetFloat("_Metallic", 0f);
        _materialOwned = true;

        Debug.Log("[HandSkeletonRenderer] Usando material default (Standard). Reemplaza por un Unlit/Color con " +
                  "GPU Instancing habilitado para mejor rendimiento en produccion.");
    }
}
