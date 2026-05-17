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
/// - Rigidbody.isKinematic se ajusta automaticamente segun ownership.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(JengaGrabbable))]
public class NetworkedJengaBlock : NetworkBehaviour
{
    private JengaGrabbable grabbable;
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
        cachedRenderer = GetComponentInChildren<Renderer>();
    }

    public override void OnNetworkSpawn()
    {
        materialIndex.OnValueChanged += OnMaterialChanged;
        ApplyMaterial(materialIndex.Value);
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
        // NetworkRigidbody maneja el isKinematic automaticamente segun ownership.
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
}
