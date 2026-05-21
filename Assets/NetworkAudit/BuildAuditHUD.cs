using System.Text;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Overlay en pantalla con informacion del audit: estado de red, ultima tecla, conteo de NetworkObjects.
/// Util en builds standalone donde no hay consola visible.
/// </summary>
public class BuildAuditHUD : MonoBehaviour
{
    [SerializeField] private bool showInEditor = true;
    [SerializeField] private bool showInBuild = true;
    [SerializeField] private int fontSize = 16;
    [SerializeField] private Vector2 margin = new Vector2(12f, 12f);
    [Tooltip("Ancho fijo del overlay (pixeles). Evita que la caja cambie de tamano con el contenido.")]
    [SerializeField] private float fixedWidth = 420f;
    [Tooltip("Toggle visible-no visible en runtime con esta tecla.")]
    [SerializeField] private KeyCode toggleKey = KeyCode.F4;

    private string lastKey = "(ninguna)";
    private float lastKeyTime = -10f;
    private GUIStyle style;
    private bool visible = true;

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            visible = !visible;

        if (Input.anyKeyDown)
        {
            foreach (var code in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (Input.GetKeyDown((KeyCode)code))
                {
                    lastKey = code.ToString();
                    lastKeyTime = Time.realtimeSinceStartup;
                    break;
                }
            }
        }
    }

    private void OnGUI()
    {
        if (!visible) return;
#if UNITY_EDITOR
        if (!showInEditor) return;
#else
        if (!showInBuild) return;
#endif

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box);
            style.alignment = TextAnchor.UpperLeft;
            style.fontSize = fontSize;
            style.normal.textColor = Color.white;
            style.padding = new RectOffset(10, 10, 8, 8);
        }

        var sb = new StringBuilder();
        var nm = NetworkManager.Singleton;

        sb.AppendLine("== AUDIT HUD ==");

        if (nm == null)
        {
            sb.AppendLine("NetworkManager: NULL");
        }
        else
        {
            string role = nm.IsHost ? "HOST" : nm.IsServer ? "SERVER" : nm.IsClient ? "CLIENT" : "IDLE";
            sb.AppendLine($"Rol: {role}");
            sb.AppendLine($"IsListening: {nm.IsListening}");

            if (nm.IsListening)
            {
                sb.AppendLine($"LocalClientId: {nm.LocalClientId}");
                if (nm.IsServer)
                    sb.AppendLine($"ConnectedClients: {nm.ConnectedClients.Count}");
                int spawnCount = nm.SpawnManager?.SpawnedObjectsList?.Count ?? 0;
                sb.AppendLine($"SpawnedObjects: {spawnCount}");
            }
            else
            {
                sb.AppendLine("Pulsa H para Host  |  C para Client");
            }
        }

        // LAN discovery summary
        var disc = LanDiscoveryService.Instance;
        if (disc != null && disc.UseLanDiscovery)
        {
            sb.AppendLine();
            sb.AppendLine($"LAN Discovery (puerto {disc.DiscoveryPort})");
            var hosts = disc.DiscoveredHosts;
            if (hosts == null || hosts.Count == 0)
            {
                sb.AppendLine("  (sin hosts visibles)");
            }
            else
            {
                float now = Time.realtimeSinceStartup;
                for (int i = 0; i < hosts.Count; i++)
                {
                    var h = hosts[i];
                    sb.AppendLine($"  [{i + 1}] {h.hostName}@{h.ip}:{h.gamePort} session={h.sessionId} age={now - h.lastSeenLocalTime:F1}s");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Ultima tecla: {lastKey} ({Time.realtimeSinceStartup - lastKeyTime:F1}s ago)");
        sb.AppendLine("F1 freeze cam | F2 snapshot");

        string text = sb.ToString();

        float height = style.CalcHeight(new GUIContent(text), fixedWidth);
        Rect rect = new Rect(margin.x, margin.y, fixedWidth, height + 16f);

        GUI.color = new Color(0f, 0f, 0f, 0.7f);
        GUI.Box(rect, GUIContent.none);
        GUI.color = Color.white;
        GUI.Label(rect, text, style);
    }
}
