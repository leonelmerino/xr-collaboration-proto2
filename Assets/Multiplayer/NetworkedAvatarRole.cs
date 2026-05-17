using TMPro;
using Unity.Netcode;
using Unity.XR.CoreUtils;
using UnityEngine;

/// <summary>
/// Componente de red para cada Avatar instanciado por NGO.
/// - El rol vive en una NetworkVariable (server escribe, todos leen).
/// - Cuando cambia, cada cliente repinta los renderers del avatar y actualiza el label TMP.
/// - El owner recibe un ClientRpc para teleportar el XR Origin local al spawn del rol.
/// </summary>
public class NetworkedAvatarRole : NetworkBehaviour
{
    [Header("Visuales")]
    [Tooltip("Renderers cuyo material se va a pintar con el color del rol. Suele ser el cuerpo del avatar.")]
    [SerializeField] private Renderer[] bodyRenderers;

    [Tooltip("Texto TMP que muestra el nombre del rol arriba del avatar. Opcional.")]
    [SerializeField] private TMP_Text roleLabel;

    [Tooltip("Transform que se orienta hacia la camara local cada frame (billboard del label). Opcional.")]
    [SerializeField] private Transform labelAnchor;

    [Header("Teleport")]
    [Tooltip("Si esta vacio, el script busca el XR Origin local al recibir el ClientRpc de teleport.")]
    [SerializeField] private Transform overrideOriginToTeleport;

    public readonly NetworkVariable<PlayerRole> Role = new NetworkVariable<PlayerRole>(
        PlayerRole.Host,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private Camera mainCamera;

    public override void OnNetworkSpawn()
    {
        Role.OnValueChanged += HandleRoleChanged;
        ApplyRole(Role.Value);
    }

    public override void OnNetworkDespawn()
    {
        Role.OnValueChanged -= HandleRoleChanged;
    }

    private void HandleRoleChanged(PlayerRole previous, PlayerRole current) => ApplyRole(current);

    private void ApplyRole(PlayerRole role)
    {
        var config = RoleAssignmentService.Instance != null ? RoleAssignmentService.Instance.Config : null;
        if (config == null)
        {
            Debug.LogWarning("[NetworkedAvatarRole] No RoleConfig disponible al aplicar rol.");
            return;
        }

        if (!config.TryGet(role, out var entry))
        {
            Debug.LogWarning($"[NetworkedAvatarRole] RoleConfig no tiene entry para {role}.");
            return;
        }

        if (bodyRenderers != null)
        {
            foreach (var ren in bodyRenderers)
            {
                if (ren == null) continue;
                var mats = ren.materials;
                for (int i = 0; i < mats.Length; i++)
                    mats[i].color = entry.color;
            }
        }

        if (roleLabel != null)
        {
            roleLabel.text = role.ToString();
        }
    }

    private void LateUpdate()
    {
        if (labelAnchor == null) return;
        if (mainCamera == null) mainCamera = Camera.main;
        if (mainCamera == null) return;

        Vector3 toCamera = mainCamera.transform.position - labelAnchor.position;
        toCamera.y = 0f;
        if (toCamera.sqrMagnitude < 0.0001f) return;

        labelAnchor.rotation = Quaternion.LookRotation(toCamera) * Quaternion.Euler(0f, 180f, 0f);
    }

    [ClientRpc]
    public void TeleportClientRpc(Vector3 position, Vector3 euler, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;

        Transform target = overrideOriginToTeleport;
        if (target == null)
        {
            var origin = FindObjectOfType<XROrigin>();
            target = origin != null ? origin.transform : transform;
        }

        target.position = position;
        target.rotation = Quaternion.Euler(euler);

        Debug.Log($"[NetworkedAvatarRole] Owner clientId={OwnerClientId} teleport -> {position}, euler={euler}. Target: {target.name}");
    }
}
