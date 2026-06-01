using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Descubrimiento de hosts en LAN via UDP broadcast. 100% offline (sin servicios cloud).
/// - Host mode: broadcast periodico en TODAS las NICs activas no virtuales.
/// - Client mode: listener pasivo + lista deduplicada con timeout.
/// - El cliente arranca el listener desde Start, asi cuando el usuario pulsa "C" ya hay candidatos.
/// </summary>
public class LanDiscoveryService : MonoBehaviour
{
    public static LanDiscoveryService Instance { get; private set; }

    [Header("Discovery")]
    [Tooltip("Si esta apagado, el servicio no broadcasta ni escucha. NetworkLauncher cae a IP estatica del UnityTransport.")]
    [SerializeField] private bool useLanDiscovery = true;
    [Tooltip("Puerto UDP usado SOLO para discovery (distinto del game port y del BioLab).")]
    [SerializeField] private int discoveryPort = 7778;
    [Tooltip("Cada cuanto el host envia un broadcast.")]
    [SerializeField] private float serverBroadcastIntervalSec = 1.5f;
    [Tooltip("Si pasaron mas de N segundos sin oir un host, lo damos por perdido.")]
    [SerializeField] private float clientHostTimeoutSec = 5f;
    [SerializeField] private string protocolMagic = "XRCOLLAB";
    [SerializeField] private int protocolVersion = 1;

    [Header("References (auto-find if null)")]
    [SerializeField] private ExperimentEventLogger eventLogger;

    public bool UseLanDiscovery => useLanDiscovery;
    public int DiscoveryPort => discoveryPort;

    private readonly List<DiscoveryRecord> discoveredHosts = new();
    public IReadOnlyList<DiscoveryRecord> DiscoveredHosts => discoveredHosts;

    private UdpClient serverSocket;
    private UdpClient clientSocket;
    private Thread clientListenerThread;
    private CancellationTokenSource clientCts;
    private Coroutine serverBroadcastCoroutine;

    private readonly Queue<DiscoveryRecord> incomingQueue = new();
    private readonly object queueLock = new();

    private void Awake()
    {
        if (Instance == null) Instance = this;
        if (eventLogger == null) eventLogger = FindObjectOfType<ExperimentEventLogger>();
    }

    private void Start()
    {
        // El listener corre siempre que useLanDiscovery este activo, asi el HUD
        // puede mostrar candidatos antes de que el usuario decida arrancar como cliente.
        if (useLanDiscovery)
            StartClientListener();
    }

    private void OnDestroy()
    {
        StopServer();
        StopClientListener();
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        // Drenar paquetes recibidos en background thread.
        lock (queueLock)
        {
            while (incomingQueue.Count > 0)
                MergeDiscovery(incomingQueue.Dequeue());
        }

        // Pruning de hosts que dejaron de transmitir.
        float now = Time.realtimeSinceStartup;
        for (int i = discoveredHosts.Count - 1; i >= 0; i--)
        {
            if (now - discoveredHosts[i].lastSeenLocalTime > clientHostTimeoutSec)
            {
                Debug.Log($"[LanDiscovery] Host expirado: {discoveredHosts[i].hostName}@{discoveredHosts[i].ip}");
                discoveredHosts.RemoveAt(i);
            }
        }
    }

    public void StartServer(int gamePort)
    {
        if (!useLanDiscovery) return;
        if (serverSocket != null) return;

        try
        {
            serverSocket = new UdpClient();
            serverSocket.EnableBroadcast = true;
            serverBroadcastCoroutine = StartCoroutine(BroadcastLoop(gamePort));
            Debug.Log($"[LanDiscovery] Server broadcasting iniciado (gamePort={gamePort}, discoveryPort={discoveryPort}).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LanDiscovery] No se pudo abrir el server socket: {ex.Message}");
            serverSocket = null;
        }
    }

    public void StopServer()
    {
        if (serverBroadcastCoroutine != null)
        {
            StopCoroutine(serverBroadcastCoroutine);
            serverBroadcastCoroutine = null;
        }
        try { serverSocket?.Close(); } catch { }
        serverSocket = null;
    }

    public void StartClientListener()
    {
        if (!useLanDiscovery) return;
        if (clientSocket != null) return;

        try
        {
            clientSocket = new UdpClient();
            clientSocket.ExclusiveAddressUse = false;
            clientSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            clientSocket.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));

            clientCts = new CancellationTokenSource();
            clientListenerThread = new Thread(ClientListenLoop) { IsBackground = true };
            clientListenerThread.Start();
            Debug.Log($"[LanDiscovery] Client listener iniciado en UDP {discoveryPort}.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LanDiscovery] No se pudo abrir el listener UDP {discoveryPort}: {ex.Message}");
            try { clientSocket?.Close(); } catch { }
            clientSocket = null;
        }
    }

    public void StopClientListener()
    {
        try { clientCts?.Cancel(); } catch { }
        try { clientSocket?.Close(); } catch { }
        clientSocket = null;
        clientListenerThread = null;
    }

    /// <summary>
    /// El primer host activo (timestamp mas reciente). Null si no hay ninguno.
    /// Conveniente para auto-conectar.
    /// </summary>
    public DiscoveryRecord PickPreferredHost()
    {
        if (discoveredHosts.Count == 0) return null;
        // Tomamos el que vimos primero (FIFO de descubrimiento).
        return discoveredHosts[0];
    }

    private IEnumerator BroadcastLoop(int gamePort)
    {
        bool firstTick = true;
        while (serverSocket != null)
        {
            string payload = BuildBroadcastPayload(gamePort);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);

            // Broadcast en cada NIC activa no virtual.
            int sentCount = 0;
            var targets = new System.Text.StringBuilder();
            foreach (var subnetBroadcast in EnumerateBroadcastAddresses())
            {
                try
                {
                    serverSocket.Send(bytes, bytes.Length, new IPEndPoint(subnetBroadcast, discoveryPort));
                    sentCount++;
                    targets.Append(subnetBroadcast).Append(' ');
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LanDiscovery] Broadcast a {subnetBroadcast} fallo: {ex.Message}");
                }
            }

            // Fallback: limited broadcast a 255.255.255.255 (NIC default del SO).
            try
            {
                serverSocket.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, discoveryPort));
                sentCount++;
                targets.Append("255.255.255.255");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LanDiscovery] Broadcast a 255.255.255.255 fallo: {ex.Message}");
            }

            // Solo loguear en el primer tick para no spam, pero mostrar todas las IPs de destino.
            if (firstTick)
            {
                Debug.Log($"[LanDiscovery] DIAG HOST — enviando a {sentCount} destinos: [{targets}] payload={payload}");
                firstTick = false;
            }

            yield return new WaitForSeconds(serverBroadcastIntervalSec);
        }
    }

    private IEnumerable<IPAddress> EnumerateBroadcastAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            // Filtro heuristico para descartar NICs virtuales tipicas (Hyper-V, WSL, VMware, VirtualBox).
            string name = ni.Name?.ToLowerInvariant() ?? string.Empty;
            string desc = ni.Description?.ToLowerInvariant() ?? string.Empty;
            if (name.Contains("virtual") || name.Contains("vethernet") || name.Contains("hyper-v")
                || name.Contains("wsl") || desc.Contains("virtual") || desc.Contains("hyper-v")
                || desc.Contains("vmware") || desc.Contains("virtualbox"))
            {
                continue;
            }

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (addr.IPv4Mask == null) continue;

                byte[] ip = addr.Address.GetAddressBytes();
                byte[] mask = addr.IPv4Mask.GetAddressBytes();
                if (ip.Length != 4 || mask.Length != 4) continue;

                byte[] bcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    bcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bcast);
            }
        }
    }

    private string BuildBroadcastPayload(int gamePort)
    {
        string sessionId = "unknown";
        if (eventLogger != null
            && !string.IsNullOrEmpty(eventLogger.participantId)
            && !string.IsNullOrEmpty(eventLogger.sessionId))
        {
            sessionId = $"{eventLogger.participantId}_{eventLogger.sessionId}";
        }

        string host = SafeMachineName();
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{protocolMagic}|v={protocolVersion}|session={sessionId}|host={host}|game_port={gamePort}|ts={ts}";
    }

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return "unknown-host"; }
    }

    private void ClientListenLoop()
    {
        Debug.Log("[LanDiscovery] DIAG CLIENT — listener thread arrancado.");
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        while (clientCts != null && !clientCts.IsCancellationRequested)
        {
            try
            {
                byte[] data = clientSocket.Receive(ref remoteEP);
                string msg = Encoding.UTF8.GetString(data);

                // DIAG: loguear TODO lo que llega al socket, antes del filtro.
                Debug.Log($"[LanDiscovery] DIAG CLIENT — paquete recibido de {remoteEP.Address}:{remoteEP.Port} ({data.Length} bytes): \"{msg}\"");

                if (!msg.StartsWith(protocolMagic + "|"))
                {
                    Debug.LogWarning($"[LanDiscovery] DIAG CLIENT — magic NO coincide. Esperado='{protocolMagic}|' Recibido='{(msg.Length > 20 ? msg.Substring(0, 20) : msg)}'");
                    continue;
                }

                var rec = ParseBroadcast(msg, remoteEP);
                if (rec == null)
                {
                    Debug.LogWarning($"[LanDiscovery] DIAG CLIENT — ParseBroadcast devolvio null para: {msg}");
                    continue;
                }

                lock (queueLock)
                    incomingQueue.Enqueue(rec);
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                // ← antes era silencioso; ahora loguea el codigo de error.
                if (clientCts == null || clientCts.IsCancellationRequested) break;
                Debug.LogWarning($"[LanDiscovery] DIAG CLIENT — SocketException en Receive: {ex.SocketErrorCode} ({ex.ErrorCode}) — {ex.Message}");
                Thread.Sleep(100); // evitar spin si el socket falla repetidamente
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LanDiscovery] Listener error: {ex.Message}");
                Thread.Sleep(100);
            }
        }
        Debug.Log("[LanDiscovery] DIAG CLIENT — listener thread terminado.");
    }

    private DiscoveryRecord ParseBroadcast(string msg, IPEndPoint sender)
    {
        try
        {
            var parts = msg.Split('|');
            int version = -1;
            string session = "";
            string host = "";
            int gamePort = 0;

            for (int i = 1; i < parts.Length; i++)
            {
                var kv = parts[i].Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "v": int.TryParse(kv[1], out version); break;
                    case "session": session = kv[1]; break;
                    case "host": host = kv[1]; break;
                    case "game_port": int.TryParse(kv[1], out gamePort); break;
                }
            }

            if (version != protocolVersion) return null;
            if (gamePort <= 0) return null;

            return new DiscoveryRecord
            {
                ip = sender.Address.ToString(),
                hostName = string.IsNullOrEmpty(host) ? "unknown" : host,
                sessionId = string.IsNullOrEmpty(session) ? "unknown" : session,
                gamePort = gamePort,
                lastSeenLocalTime = Time.realtimeSinceStartup
            };
        }
        catch { return null; }
    }

    private void MergeDiscovery(DiscoveryRecord newRec)
    {
        for (int i = 0; i < discoveredHosts.Count; i++)
        {
            if (discoveredHosts[i].ip == newRec.ip && discoveredHosts[i].gamePort == newRec.gamePort)
            {
                discoveredHosts[i].lastSeenLocalTime = newRec.lastSeenLocalTime;
                discoveredHosts[i].sessionId = newRec.sessionId;
                discoveredHosts[i].hostName = newRec.hostName;
                return;
            }
        }
        discoveredHosts.Add(newRec);
        Debug.Log($"[LanDiscovery] Nuevo host descubierto: {newRec.hostName}@{newRec.ip}:{newRec.gamePort} session={newRec.sessionId}");
    }
}
