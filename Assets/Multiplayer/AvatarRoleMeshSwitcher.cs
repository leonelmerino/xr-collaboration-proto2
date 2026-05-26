using System;
using UnityEngine;

/// <summary>
/// Componente del Avatar.prefab raiz que habilita el sub-mesh humanoid correspondiente al
/// rol asignado al avatar. Los otros sub-meshes quedan inactivos (Renderer + bones no corren).
///
/// Funcionamiento:
/// - Se subscribe a NetworkedAvatarRole.Role.OnValueChanged.
/// - Cuando el rol cambia (o al spawn), itera el array `roleMeshes` y enciende solo el
///   GameObject cuyo PlayerRole coincide con el rol activo.
///
/// Setup en el prefab:
/// - Arrastrar el GameObject parent de cada sub-mesh Rocketbox (Avatar_Host, Avatar_Client,
///   Avatar_Helper) al campo correspondiente en el array.
/// - El campo `networkedRole` se auto-resuelve si esta en el mismo GameObject.
///
/// Por defecto, los 3 sub-meshes estan ACTIVOS en el prefab (asi se pueden editar en
/// Editor). El componente apaga los que no corresponden al spawn del rol asignado.
/// </summary>
public class AvatarRoleMeshSwitcher : MonoBehaviour
{
    [Serializable]
    public class RoleMeshBinding
    {
        public PlayerRole role;
        public GameObject meshRoot;
    }

    [Tooltip("Una entrada por rol. El meshRoot apunta al GameObject parent de la sub-mesh " +
             "Rocketbox (con su Animator, Rig Builder y children).")]
    [SerializeField] private RoleMeshBinding[] roleMeshes;

    [Tooltip("Si esta vacio se auto-resuelve via GetComponent en este GameObject.")]
    [SerializeField] private NetworkedAvatarRole networkedRole;

    private bool _subscribed;

    private void Awake()
    {
        if (networkedRole == null)
            networkedRole = GetComponent<NetworkedAvatarRole>();
    }

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void OnDisable()
    {
        TryUnsubscribe();
    }

    private void Start()
    {
        // Aplicar el rol inicial. Si el NetworkVariable todavia no esta lista (caso server-spawn
        // antes que client connect), el OnValueChanged se va a disparar despues y se aplica.
        if (networkedRole != null)
        {
            ApplyRole(networkedRole.Role.Value);
        }
        else
        {
            Debug.LogWarning("[AvatarRoleMeshSwitcher] No hay NetworkedAvatarRole asignado. " +
                             "El switching no va a funcionar — todos los meshes quedan visibles.");
        }
    }

    private void TrySubscribe()
    {
        if (_subscribed) return;
        if (networkedRole == null) return;
        try
        {
            networkedRole.Role.OnValueChanged += HandleRoleChanged;
            _subscribed = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AvatarRoleMeshSwitcher] No pude subscribir a Role: {e.Message}");
        }
    }

    private void TryUnsubscribe()
    {
        if (!_subscribed) return;
        try { networkedRole.Role.OnValueChanged -= HandleRoleChanged; } catch { }
        _subscribed = false;
    }

    private void HandleRoleChanged(PlayerRole previous, PlayerRole current)
    {
        ApplyRole(current);
    }

    /// <summary>
    /// Habilita el GameObject del meshRoot cuyo rol coincide con `role`. Apaga el resto.
    /// </summary>
    private void ApplyRole(PlayerRole role)
    {
        if (roleMeshes == null) return;

        bool foundMatch = false;
        for (int i = 0; i < roleMeshes.Length; i++)
        {
            var binding = roleMeshes[i];
            if (binding == null || binding.meshRoot == null) continue;

            bool match = (binding.role == role);
            if (match) foundMatch = true;

            // Solo cambiamos el estado si difiere, para no spammear Unity.
            if (binding.meshRoot.activeSelf != match)
                binding.meshRoot.SetActive(match);
        }

        if (!foundMatch)
        {
            Debug.LogWarning($"[AvatarRoleMeshSwitcher] No hay meshRoot configurado para rol {role}. " +
                             "Todos los sub-meshes van a quedar apagados.");
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// En Editor (no Play mode), si modificas el array re-aplica el visible para reflejar el cambio.
    /// </summary>
    private void OnValidate()
    {
        if (!Application.isPlaying && networkedRole != null && roleMeshes != null)
            ApplyRole(networkedRole.Role.Value);
    }
#endif
}
