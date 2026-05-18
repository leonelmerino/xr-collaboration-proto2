using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Sincronizacion de relojes entre nodos VR.
///
/// - El HOST es la referencia de tiempo (offset = 0).
/// - Cada CLIENT/HELPER, al conectarse, ejecuta un handshake tipo NTP
///   con N round trips y estima un offset al reloj del host.
/// - Periodicamente, el host emite SYNC_MARKERs que todos los nodos loggean
///   localmente y que el host tambien manda a BioLab. Sirven como anchors
///   para validar/corregir drift en analisis offline.
///
/// Setup:
/// - Pone el script en un GameObject de escena con un componente NetworkObject.
/// - Marca el NetworkObject como "Scene Object" (en escena, persistente).
/// - El offset estimado se accede via NetworkClockSync.Instance.OffsetToHost.
/// - El "tiempo del host" estimado: NetworkClockSync.Instance.GetHostTime().
/// </summary>
public class NetworkClockSync : NetworkBehaviour
{
    public static NetworkClockSync Instance { get; private set; }

    [Header("Handshake")]
    [Tooltip("Cantidad de samples PING para estimar offset.")]
    [SerializeField] private int handshakeSamples = 10;
    [Tooltip("Tiempo entre samples (segundos).")]
    [SerializeField] private float sampleSpacingSec = 0.05f;
    [Tooltip("Timeout total del handshake.")]
    [SerializeField] private float handshakeTimeoutSec = 5f;

    [Header("Sync Markers")]
    [SerializeField] private bool emitSyncMarkers = true;
    [SerializeField] private float syncMarkerIntervalSec = 10f;

    // Estado del cliente (offset al reloj del host).
    private double offsetToHost = 0.0;
    private double estimatedRttSec = 0.0;
    private bool isSynced = false;

    public bool IsSynced => isSynced;
    public double OffsetToHost => offsetToHost;
    public double EstimatedRttMs => estimatedRttSec * 1000.0;

    /// <summary>Tiempo monotonico LOCAL (segundos).</summary>
    public double GetLocalTime() => Time.realtimeSinceStartupAsDouble;

    /// <summary>Tiempo estimado del HOST (segundos). En el host, == local.</summary>
    public double GetHostTime() => Time.realtimeSinceStartupAsDouble + offsetToHost;

    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // Host es la referencia. Offset = 0 desde el inicio.
            offsetToHost = 0.0;
            estimatedRttSec = 0.0;
            isSynced = true;
            Debug.Log("[NetworkClockSync] Server side ready (offset=0).");

            if (emitSyncMarkers)
                StartCoroutine(SyncMarkerLoop());
        }

        if (IsClient && !IsServer)
        {
            // Cliente no-host: arranca handshake.
            StartCoroutine(InitialHandshake());
        }
    }

    // ============================================================
    // Handshake: client -> server -> client
    // ============================================================

    private readonly List<(double offset, double rtt)> handshakeData = new();
    private readonly Dictionary<long, double> pendingPings = new();
    private long nextCorrelationId = 0;

    private IEnumerator InitialHandshake()
    {
        Debug.Log($"[NetworkClockSync] Starting handshake ({handshakeSamples} samples).");
        handshakeData.Clear();

        float startedAt = Time.realtimeSinceStartup;

        for (int i = 0; i < handshakeSamples; i++)
        {
            if (Time.realtimeSinceStartup - startedAt > handshakeTimeoutSec)
            {
                Debug.LogWarning("[NetworkClockSync] Handshake hit timeout while sending samples.");
                break;
            }
            SendOnePing();
            yield return new WaitForSeconds(sampleSpacingSec);
        }

        // Esperamos un poco mas para que vuelvan las respuestas pendientes.
        float waitExtra = Mathf.Max(0.5f, handshakeTimeoutSec - (Time.realtimeSinceStartup - startedAt));
        float doneAt = Time.realtimeSinceStartup + waitExtra;
        while (Time.realtimeSinceStartup < doneAt && handshakeData.Count < handshakeSamples)
            yield return null;

        FinishHandshake();
    }

    private void SendOnePing()
    {
        long corr = ++nextCorrelationId;
        double t1 = Time.realtimeSinceStartupAsDouble;
        pendingPings[corr] = t1;
        ClockPingServerRpc(corr, t1);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ClockPingServerRpc(long correlationId, double clientT1, ServerRpcParams rpc = default)
    {
        double t2 = Time.realtimeSinceStartupAsDouble;
        // t3 lo recalculamos justo antes de mandar la respuesta.
        var resp = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { rpc.Receive.SenderClientId } }
        };
        double t3 = Time.realtimeSinceStartupAsDouble;
        ClockPongClientRpc(correlationId, clientT1, t2, t3, resp);
    }

    [ClientRpc]
    private void ClockPongClientRpc(long correlationId, double clientT1, double serverT2, double serverT3, ClientRpcParams rpc = default)
    {
        if (IsServer) return;

        double t4 = Time.realtimeSinceStartupAsDouble;
        if (!pendingPings.TryGetValue(correlationId, out _))
            return;
        pendingPings.Remove(correlationId);

        double offset = ((serverT2 - clientT1) + (serverT3 - t4)) * 0.5;
        double rtt = (t4 - clientT1) - (serverT3 - serverT2);
        if (rtt < 0) rtt = t4 - clientT1;

        handshakeData.Add((offset, rtt));
    }

    private void FinishHandshake()
    {
        if (handshakeData.Count == 0)
        {
            Debug.LogWarning("[NetworkClockSync] Handshake fallo: 0 samples. offset=0, isSynced=false.");
            offsetToHost = 0.0;
            estimatedRttSec = 0.0;
            isSynced = false;
            LogToAcquisition("CLOCK_SYNC_FAILED", $"samples=0/{handshakeSamples}");
            return;
        }

        // Filtramos por RTT: nos quedamos con la mitad de menor RTT (mas precisa).
        handshakeData.Sort((a, b) => a.rtt.CompareTo(b.rtt));
        int keep = Mathf.Max(1, handshakeData.Count / 2);

        double sumOffset = 0;
        double sumRtt = 0;
        for (int i = 0; i < keep; i++)
        {
            sumOffset += handshakeData[i].offset;
            sumRtt += handshakeData[i].rtt;
        }
        offsetToHost = sumOffset / keep;
        estimatedRttSec = sumRtt / keep;
        isSynced = true;

        Debug.Log($"[NetworkClockSync] Handshake OK. offset={offsetToHost * 1000:F3} ms, avgRTT={estimatedRttSec * 1000:F3} ms (best {keep}/{handshakeData.Count} samples).");

        LogToAcquisition("CLOCK_SYNC_OK",
            $"offset_ms={offsetToHost * 1000:F3}|rtt_ms={estimatedRttSec * 1000:F3}|samples_used={keep}/{handshakeSamples}");
    }

    // ============================================================
    // Sync markers (host -> all clients + BioLab)
    // ============================================================

    private int markerSequence = 0;

    private IEnumerator SyncMarkerLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(syncMarkerIntervalSec);
            if (!IsServer) yield break;

            int seq = markerSequence++;
            double tHost = Time.realtimeSinceStartupAsDouble;
            // Mandamos a todos los clientes.
            SyncMarkerClientRpc(seq, tHost);
            // Logueamos local en el host y le mandamos a BioLab.
            LogToAcquisition("SYNC_MARKER",
                $"seq={seq}|t_host_auth={tHost:F6}|t_host_est={tHost:F6}|t_local={tHost:F6}|offset_ms=0.000",
                forwardToBioLab: true);
        }
    }

    [ClientRpc]
    private void SyncMarkerClientRpc(int sequence, double tHostAuthoritative, ClientRpcParams rpc = default)
    {
        if (IsServer) return; // ya loggeado en el host

        double tLocal = Time.realtimeSinceStartupAsDouble;
        double tHostEst = tLocal + offsetToHost;
        double drift = (tHostEst - tHostAuthoritative) * 1000.0; // ms

        LogToAcquisition("SYNC_MARKER",
            $"seq={sequence}|t_host_auth={tHostAuthoritative:F6}|t_host_est={tHostEst:F6}|t_local={tLocal:F6}|offset_ms={offsetToHost * 1000:F3}|drift_ms={drift:F3}");
    }

    // ============================================================
    // Helpers
    // ============================================================

    private void LogToAcquisition(string eventType, string detail, bool forwardToBioLab = false)
    {
        var mgr = AcquisitionEventManager.Instance;
        if (mgr == null) return;

        if (forwardToBioLab)
            mgr.LogAndForwardExternal(eventType, detail);
        else
            mgr.LogLocalExternal(eventType, detail);
    }
}
