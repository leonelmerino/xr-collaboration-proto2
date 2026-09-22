using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Wrapper de red para cada bloque Jenga. Va en el JengaBlock.prefab junto a:
/// - NetworkObject
/// - OwnerNetworkTransform (owner-authoritative)
/// - Rigidbody (existente)
/// - JengaGrabbable (existente; sigue manejando la mecanica local del grab)
///
/// Flujo:
/// - Server-owned por default; el server simula la fisica.
/// - Cliente pide grab via ServerRpc. Server cambia ownership al cliente.
/// - Cliente recibe OnGainedOwnership y arranca el grab local (JengaGrabbable.BeginGrab).
/// - Cliente pide release. Server reasume ownership y la fisica continua.
///
/// Rigidbody.isKinematic se maneja aca segun ownership (no hay NetworkRigidbody en el prefab).
/// Sin este manejo, los no-owners tienen el Rigidbody simulando localmente (gravedad + colisiones
/// entre bloques adyacentes en la torre), pero OwnerNetworkTransform pisa la pose cada frame
/// desde el owner. La simulacion local acumula correccion de overlaps sin poder aplicarla
/// visualmente. Cuando ownership cambia al cliente (grab), NetworkTransform deja de pisar y la
/// correccion se descarga de un golpe -> el bloque salta ~20cm en el momento del grab.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(JengaGrabbable))]
public class NetworkedJengaBlock : NetworkBehaviour
{
    private JengaGrabbable grabbable;
    private Rigidbody rb;
    private Renderer cachedRenderer;
    private Transform pendingGrabHand;

    private NetworkVariable<int> materialIndex = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public bool IsGrabbedAnywhere =>
        IsSpawned && OwnerClientId != NetworkManager.ServerClientId;

    private void Awake()
    {
        grabbable = GetComponent<JengaGrabbable>();
        rb = GetComponent<Rigidbody>();
        cachedRenderer = GetComponentInChildren<Renderer>();
    }

    public override void OnNetworkSpawn()
    {
        materialIndex.OnValueChanged += OnMaterialChanged;
        ApplyMaterial(materialIndex.Value);
        ApplyOwnershipKinematic();   // owner simula fisica; no-owners quedan kinematic (visual driveado por NetworkTransform).
    }

    /// <summary>
    /// Sincroniza isKinematic con IsOwner. Llamado en el spawn inicial.
    /// Sin esto, los clientes no-owner corren simulacion local de fisica que compite con
    /// OwnerNetworkTransform y produce el "salto" al pasar a ser owner (ver comentario del summary).
    /// </summary>
    private void ApplyOwnershipKinematic()
    {
        if (rb == null) return;
        rb.isKinematic = !IsOwner;
    }

    public override void OnNetworkDespawn()
    {
        materialIndex.OnValueChanged -= OnMaterialChanged;
    }

    private void OnMaterialChanged(int previous, int current) => ApplyMaterial(current);

    private void ApplyMaterial(int idx)
    {
        if (idx < 0 || cachedRenderer == null) return;
        var gen = JengaTowerGenerator.Instance;
        if (gen == null) return;
        var mat = gen.GetMaterial(idx);
        if (mat != null)
            cachedRenderer.sharedMaterial = mat;
    }

    public void SetMaterialIndex(int idx)
    {
        if (!IsServer) return;
        materialIndex.Value = idx;
    }

    public override void OnGainedOwnership()
    {
        bool preKinematic = rb != null && rb.isKinematic;
        Vector3 preTPos = transform.position;
        Vector3 preRbPos = rb != null ? rb.position : Vector3.zero;
        Vector3 handPos = pendingGrabHand != null ? pendingGrabHand.position : Vector3.zero;
        Debug.Log($"[JengaGrab] OnGainedOwnership '{name}' IsServer={IsServer} preKinematic={preKinematic} tPos={preTPos:F3} rbPos={preRbPos:F3} handPos={handPos:F3} hasPending={(pendingGrabHand != null)}");

        // Solo el server necesita simular fisica local del bloque. Cuando un Client/Helper gana
        // ownership por un grab remoto, mantenemos el bloque kinematic — JengaGrabbable.MovePosition
        // funciona sobre kinematic, no se pierde nada.
        //
        // Por que NO sacar kinematic en el Client: los vecinos siguen kinematic con su transform
        // interpolado por OwnerNetworkTransform (pose ~1-2 frames atras). Al activar fisica local
        // en el bloque agarrado, Unity detecta overlap contra las interpolaciones ligeramente
        // desalineadas de los vecinos y dispara una correccion instantanea de colision — el bloque
        // "salta" ~20cm en direccion para salir del overlap. Por eso la direccion depende del
        // bloque (con que vecino tiene overlap) y por eso no ocurre en el Host (donde IsOwner=true
        // desde el spawn, no hay ChangeOwnership ni transicion kinematic al agarrar).
        if (rb != null && IsServer)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = false;
        }

        Vector3 postTPos = transform.position;
        Vector3 postRbPos = rb != null ? rb.position : Vector3.zero;
        Debug.Log($"[JengaGrab] OnGainedOwnership '{name}' POST tPos={postTPos:F3} rbPos={postRbPos:F3} tPosDelta={(postTPos - preTPos).magnitude:F4} rbPosDelta={(postRbPos - preRbPos).magnitude:F4}");

        if (pendingGrabHand != null)
        {
            grabbable.BeginGrab(pendingGrabHand);
            pendingGrabHand = null;
        }
    }

    public override void OnLostOwnership()
    {
        if (grabbable.IsGrabbed())
            grabbable.EndGrab();
        pendingGrabHand = null;

        // Vuelvo a kinematic: no soy owner, no simulo. El visual lo maneja OwnerNetworkTransform
        // recibiendo la pose del nuevo owner. Orden: velocidades a cero PRIMERO (mientras aun es
        // no-kinematic), despues isKinematic=true. Setear velocity/angularVelocity en un body ya
        // kinematic dispara warning "Setting linear velocity of a kinematic body is not supported".
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }
    }

    public void RequestGrab(Transform handTransform)
    {
        if (!IsSpawned)
        {
            // Fallback single-player.
            Debug.Log($"[JengaGrab] RequestGrab '{name}' path=OFFLINE blockPos={transform.position:F3} handPos={handTransform.position:F3}");
            grabbable.BeginGrab(handTransform);
            return;
        }

        pendingGrabHand = handTransform;

        if (IsOwner)
        {
            // Soy host y dueño actual (el server agarra su propio bloque): grab inmediato.
            Debug.Log($"[JengaGrab] RequestGrab '{name}' path=LOCAL_OWNER blockPos={transform.position:F3} handPos={handTransform.position:F3}");
            grabbable.BeginGrab(handTransform);
            pendingGrabHand = null;
            return;
        }

        Debug.Log($"[JengaGrab] RequestGrab '{name}' path=SERVER_RPC blockPos={transform.position:F3} rbPos={(rb != null ? rb.position : Vector3.zero):F3} handPos={handTransform.position:F3} kinematic={(rb != null && rb.isKinematic)}");
        RequestGrabServerRpc();
    }

    public void RequestRelease()
    {
        if (!IsSpawned)
        {
            grabbable.EndGrab();
            return;
        }

        if (IsOwner && IsServer)
        {
            // Server-owned grab (raro pero posible si host se auto-asigno).
            grabbable.EndGrab();
            return;
        }

        RequestReleaseServerRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestGrabServerRpc(ServerRpcParams rpc = default)
    {
        ulong senderId = rpc.Receive.SenderClientId;

        // Solo permitir si el bloque esta libre (server-owned).
        if (OwnerClientId != NetworkManager.ServerClientId)
            return;

        NetworkObject.ChangeOwnership(senderId);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestReleaseServerRpc(ServerRpcParams rpc = default)
    {
        ulong senderId = rpc.Receive.SenderClientId;

        // Solo el grabber actual puede soltar.
        if (OwnerClientId != senderId)
            return;

        NetworkObject.RemoveOwnership();
    }

    /// <summary>
    /// API publica para pedir un push al bloque desde cualquier cliente. La fisica del bloque es
    /// autoritativa en el owner (server-owned cuando el bloque esta libre). Client/Helper llaman
    /// esto en vez de hacer AddForce local — su AddForce no tiene efecto porque el
    /// OwnerNetworkTransform sobreescribe la pose cuadro a cuadro con la del server.
    ///
    /// Usa AddForceAtPosition para aplicar el impulso en el punto exacto del contacto (dedo),
    /// no en el centro de masa. Esto genera torque ademas de traslacion — el bloque rota un poco
    /// en vez de "volar" en linea recta, comportamiento mas natural y contenido.
    ///
    /// Path del Host: si YO soy el owner del bloque, aplico el force local sin roundtrip por red.
    /// </summary>
    public void RequestPush(Vector3 forceWorld, Vector3 applicationPointWorld)
    {
        if (rb == null) { Debug.LogWarning($"[NetworkedJengaBlock] RequestPush '{name}' abortado: rb=null"); return; }

        if (!IsSpawned)
        {
            // Fallback offline/single-player.
            Debug.Log($"[NetworkedJengaBlock] RequestPush '{name}' path=OFFLINE (not spawned). force={forceWorld.magnitude:F3}");
            rb.AddForceAtPosition(forceWorld, applicationPointWorld, ForceMode.Impulse);
            return;
        }

        if (IsOwner)
        {
            // Host cuando el bloque esta libre: fisica local, sin roundtrip.
            Debug.Log($"[NetworkedJengaBlock] RequestPush '{name}' path=LOCAL_OWNER. force={forceWorld.magnitude:F3}");
            rb.AddForceAtPosition(forceWorld, applicationPointWorld, ForceMode.Impulse);
            return;
        }

        Debug.Log($"[NetworkedJengaBlock] RequestPush '{name}' path=SERVER_RPC (IsOwner=false, OwnerClientId={OwnerClientId}). force={forceWorld.magnitude:F3}");
        ApplyPushServerRpc(forceWorld, applicationPointWorld);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ApplyPushServerRpc(Vector3 forceWorld, Vector3 applicationPointWorld)
    {
        // Solo aplicar si el server es el owner del bloque (bloque libre). Si otro cliente lo
        // esta agarrando, ignorar — no queremos pushes ajenos interfiriendo con su grab.
        if (OwnerClientId != NetworkManager.ServerClientId)
        {
            Debug.Log($"[NetworkedJengaBlock] ApplyPushServerRpc '{name}' IGNORADO: OwnerClientId={OwnerClientId} != Server. force={forceWorld.magnitude:F3}");
            return;
        }

        if (rb == null) { Debug.LogWarning($"[NetworkedJengaBlock] ApplyPushServerRpc '{name}' abortado: rb=null"); return; }

        Debug.Log($"[NetworkedJengaBlock] ApplyPushServerRpc '{name}' APLICADO en server. force={forceWorld.magnitude:F3}");
        rb.AddForceAtPosition(forceWorld, applicationPointWorld, ForceMode.Impulse);
    }
}
