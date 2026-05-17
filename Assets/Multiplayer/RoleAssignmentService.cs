using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-only: asigna roles a los clientes en orden de conexion (Host -> Client -> Helper)
/// y los teleporta a la posicion configurada en RoleConfig mirando al table center.
/// Es un singleton de escena; el RoleConfig se expone a NetworkedAvatarRole por Instance.Config.
/// </summary>
public class RoleAssignmentService : MonoBehaviour
{
    public static RoleAssignmentService Instance { get; private set; }

    [SerializeField] private RoleConfig config;

    [Tooltip("Referencia opcional al centro de la mesa. Si esta puesto, todos los avatares spawnean mirando ahi (ignora spawnEuler del RoleConfig).")]
    [SerializeField] private Transform tableCenter;

    [Tooltip("Orden en que se asignan los roles a las conexiones entrantes.")]
    [SerializeField] private PlayerRole[] assignmentOrder = new[] { PlayerRole.Host, PlayerRole.Client, PlayerRole.Helper };

    public RoleConfig Config => config;

    private int nextRoleIndex = 0;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnEnable()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        nm.OnServerStarted += HandleServerStarted;
        nm.OnClientConnectedCallback += HandleClientConnected;
        nm.OnClientDisconnectCallback += HandleClientDisconnected;
    }

    private void OnDisable()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        nm.OnServerStarted -= HandleServerStarted;
        nm.OnClientConnectedCallback -= HandleClientConnected;
        nm.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    private void HandleServerStarted()
    {
        nextRoleIndex = 0;
        Debug.Log("[RoleAssignmentService] Server started. Role assignment reset.");
    }

    private void HandleClientConnected(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;

        if (config == null)
        {
            Debug.LogError("[RoleAssignmentService] No RoleConfig asignado.");
            return;
        }

        if (nextRoleIndex >= assignmentOrder.Length)
        {
            Debug.LogWarning($"[RoleAssignmentService] No quedan roles disponibles para clientId={clientId}. Conectados: {nm.ConnectedClients.Count}");
            return;
        }

        PlayerRole role = assignmentOrder[nextRoleIndex++];
        StartCoroutine(AssignRoleWhenReady(clientId, role));
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        Debug.Log($"[RoleAssignmentService] Disconnect clientId={clientId}. (Pool de roles no se reasigna automaticamente en esta version.)");
    }

    private IEnumerator AssignRoleWhenReady(ulong clientId, PlayerRole role)
    {
        var nm = NetworkManager.Singleton;
        const float timeoutSec = 5f;
        float elapsed = 0f;

        NetworkObject playerObject = null;
        while (elapsed < timeoutSec)
        {
            if (nm.ConnectedClients.TryGetValue(clientId, out var client) && client.PlayerObject != null)
            {
                playerObject = client.PlayerObject;
                break;
            }
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (playerObject == null)
        {
            Debug.LogWarning($"[RoleAssignmentService] PlayerObject de clientId={clientId} no aparecio antes del timeout.");
            yield break;
        }

        var roleComp = playerObject.GetComponent<NetworkedAvatarRole>();
        if (roleComp == null)
        {
            Debug.LogWarning("[RoleAssignmentService] El Avatar.prefab no tiene NetworkedAvatarRole.");
            yield break;
        }

        roleComp.Role.Value = role;

        if (config.TryGet(role, out var entry))
        {
            Vector3 pos = entry.spawnPosition;
            Quaternion rot = ComputeFacing(pos, entry.spawnEuler);
            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            };
            roleComp.TeleportClientRpc(pos, rot.eulerAngles, rpcParams);

            Debug.Log($"[RoleAssignmentService] clientId={clientId} -> rol {role}, spawn={pos}, facing={rot.eulerAngles.y:F1}");
        }
    }

    private Quaternion ComputeFacing(Vector3 spawnPos, Vector3 fallbackEuler)
    {
        if (tableCenter != null)
        {
            Vector3 dir = tableCenter.position - spawnPos;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f)
                return Quaternion.LookRotation(dir);
        }
        return Quaternion.Euler(fallbackEuler);
    }
}
