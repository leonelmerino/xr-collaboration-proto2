using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// Bootstrap de NGO via teclado.
/// - H: arranca el host y empieza a broadcastear discovery en LAN (si LanDiscoveryService.useLanDiscovery=true).
/// - C: arranca como cliente. Si el discovery encontro un host, usa esa IP. Si no, cae al
///   Address estatico configurado en UnityTransport (Inspector).
/// </summary>
public class NetworkLauncher : MonoBehaviour
{
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.H))
            StartHost();

        if (Input.GetKeyDown(KeyCode.C))
            StartClientWithDiscovery();
    }

    private void StartHost()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (nm.IsListening) return;

        nm.StartHost();

        // Una vez listening, anuncio el host en LAN.
        var disc = LanDiscoveryService.Instance;
        if (disc != null && disc.UseLanDiscovery)
        {
            int gamePort = GetGamePort(nm);
            disc.StartServer(gamePort);
        }
        else
        {
            Debug.Log("[NetworkLauncher] LAN discovery deshabilitado. Host no anuncia en la red.");
        }
    }

    private void StartClientWithDiscovery()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (nm.IsListening) return;

        var disc = LanDiscoveryService.Instance;
        var ut = nm.GetComponent<UnityTransport>();

        if (disc != null && disc.UseLanDiscovery)
        {
            var host = disc.PickPreferredHost();
            if (host != null && ut != null)
            {
                ut.ConnectionData.Address = host.ip;
                ut.ConnectionData.Port = (ushort)host.gamePort;
                Debug.Log($"[NetworkLauncher] Auto-conectando a host descubierto: {host.hostName}@{host.ip}:{host.gamePort} session={host.sessionId}");
            }
            else
            {
                Debug.LogWarning($"[NetworkLauncher] No hay hosts descubiertos en LAN. Cayendo a IP estatica: {ut?.ConnectionData.Address}:{ut?.ConnectionData.Port}");
            }
        }
        else
        {
            Debug.Log($"[NetworkLauncher] LAN discovery deshabilitado. Conectando a IP estatica: {ut?.ConnectionData.Address}:{ut?.ConnectionData.Port}");
        }

        nm.StartClient();
    }

    private static int GetGamePort(NetworkManager nm)
    {
        var ut = nm.GetComponent<UnityTransport>();
        return ut != null ? ut.ConnectionData.Port : 7777;
    }
}
