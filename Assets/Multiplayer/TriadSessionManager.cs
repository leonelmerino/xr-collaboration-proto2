using System.Collections.Generic;
using UnityEngine;

public class TriadSessionManager : MonoBehaviour
{
    public static TriadSessionManager Instance { get; private set; }

    [Header("Prefabs")]
    [SerializeField] private GameObject mockAvatarPrefab;

    [Header("Scene References")]
    [SerializeField] private Transform tableCenter;

    [Header("Player Slots")]
    [SerializeField] private List<PlayerSlotConfig> slots = new();

    private readonly Dictionary<PlayerRole, GameObject> spawnedMocks = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void Start()
    {
        SpawnMockPlayers();
    }

    private void SpawnMockPlayers()
    {
        foreach (var slot in slots)
        {
            if (slot.mode != PresenceMode.Mock)
                continue;

            if (mockAvatarPrefab == null)
            {
                Debug.LogError("TriadSessionManager: mockAvatarPrefab is missing.");
                return;
            }

            GameObject avatar = Instantiate(
                mockAvatarPrefab,
                slot.mockPosition,
                Quaternion.identity
            );

            avatar.name = $"Mock_{slot.role}";

            OrientAvatar(avatar.transform, slot);

            RoleAvatarPresenter presenter = avatar.GetComponent<RoleAvatarPresenter>();
            if (presenter != null)
            {
                presenter.Setup(slot.role, slot.avatarColor, true);
            }

            spawnedMocks[slot.role] = avatar;
        }
    }

    private void OrientAvatar(Transform avatarTransform, PlayerSlotConfig slot)
    {
        if (tableCenter != null)
        {
            Vector3 lookDir = tableCenter.position - avatarTransform.position;
            lookDir.y = 0f;

            if (lookDir.sqrMagnitude > 0.0001f)
            {
                avatarTransform.rotation = Quaternion.LookRotation(lookDir);
                return;
            }
        }

        avatarTransform.rotation = Quaternion.Euler(slot.mockEuler);
    }

    public Color GetColor(PlayerRole role)
    {
        foreach (var slot in slots)
        {
            if (slot.role == role)
                return slot.avatarColor;
        }

        return Color.white;
    }
}