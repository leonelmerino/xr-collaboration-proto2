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
    /// Sincroniza isKinematic con IsOwner. Llamado en el spawn inicial y en cada ownership change.
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
        // Salgo de kinematic y arranco limpio: sin velocidades stale acumuladas ni residuos de la
        // simulacion que corria antes de recibir ownership. Esto elimina el "salto" inicial.
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

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
        // recibiendo la pose del nuevo owner.
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    public void RequestGrab(Transform handTransform)
    {
        if (!IsSpawned)
        {
            // Fallback single-player.
            grabbable.BeginGrab(handTransform);
            return;
        }

        pendingGrabHand = handTransform;

        if (IsOwner)
        {
            // Soy host y dueño actual (el server agarra su propio bloque): grab inmediato.
            grabbable.BeginGrab(handTransform);
            pendingGrabHand = null;
            return;
        }

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
    /// autoritativa en el owner (el server, cuando el bloque esta libre). Client/Helper llaman
    /// esto en vez de hacer AddForce local — su AddForce no tiene efecto porque el
    /// OwnerNetworkTransform sobreescribe la pose cuadro a cuadro con la del server.
    ///
    /// Optimizacion: si YO soy el owner del bloque (caso Host cuando el bloque esta libre, o
    /// cualquiera cuando lo esta agarrando), aplico el force directo sin roundtrip por red.
    /// </summary>
    public void RequestPush(Vector3 forceWorld, Vector3 applicationPointWorld)
    {
        if (rb == null) return;

        if (!IsSpawned)
        {
            // Fallback single-player: aplicar directo.
            rb.AddForceAtPosition(forceWorld, applicationPointWorld, ForceMode.Impulse);
            return;
        }

        if (IsOwner)
        {
            rb.AddForceAtPosition(forceWorld, applicationPointWorld, ForceMode.Impulse);
            return;
        }

        ApplyPushServerRpc(forceWorld, applicationPointWorld);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ApplyPushServerRpc(Vector3 forceWorld, Vector3 applicationPointWorld)
    {
        // Solo aplicar si el server es el owner (bloque libre). Si otro cliente lo esta
        // agarrando, ignorar — el owner actual es dueño de su fisica y no queremos pushes
        // ajenos interfiriendo con el grab.
        if (OwnerClientId != NetworkManager.ServerClientId)
            return;

        if (rb == null) return;
        rb.AddForceAtPosition(forceWorld, applicationPointWorld, ForceMode.Impulse);
    }
}
