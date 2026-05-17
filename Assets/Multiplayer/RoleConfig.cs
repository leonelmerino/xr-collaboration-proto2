using UnityEngine;

[CreateAssetMenu(fileName = "RoleConfig", menuName = "XR Collab/Role Config")]
public class RoleConfig : ScriptableObject
{
    [System.Serializable]
    public class RoleEntry
    {
        public PlayerRole role;
        public Color color = Color.white;
        public Vector3 spawnPosition;
        public Vector3 spawnEuler;
    }

    [Tooltip("Una entrada por rol (Host / Client / Helper). El servicio asigna roles en este mismo orden a los clientes que se conectan.")]
    public RoleEntry[] entries = new RoleEntry[]
    {
        new RoleEntry { role = PlayerRole.Host,   color = Color.red,   spawnPosition = new Vector3(0f,     0f, -0.766f) },
        new RoleEntry { role = PlayerRole.Client, color = Color.green, spawnPosition = new Vector3(-1.055f, 0f,  0f)     },
        new RoleEntry { role = PlayerRole.Helper, color = Color.blue,  spawnPosition = new Vector3( 1.071f, 0f,  0f)     },
    };

    public bool TryGet(PlayerRole role, out RoleEntry entry)
    {
        if (entries != null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].role == role)
                {
                    entry = entries[i];
                    return true;
                }
            }
        }
        entry = null;
        return false;
    }
}
