using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;

public class NetworkAuditLogger : MonoBehaviour
{
    [Header("Logging")]
    [SerializeField] private bool logToConsole = true;
    [SerializeField] private bool logToFile = true;
    [SerializeField] private string fileNamePrefix = "network_audit";

    [Header("Snapshots")]
    [Tooltip("Tecla para volcar un snapshot de todos los NetworkObjects spawneados con su owner y posicion.")]
    [SerializeField] private KeyCode snapshotKey = KeyCode.F2;

    private StreamWriter writer;
    private string filePath;
    private float startTime;

    private void Awake()
    {
        startTime = Time.realtimeSinceStartup;

        if (!logToFile) return;

        try
        {
            string folder = Path.Combine(Application.persistentDataPath, "NetworkAudit");
            Directory.CreateDirectory(folder);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            filePath = Path.Combine(folder, $"{fileNamePrefix}_{stamp}.csv");
            writer = new StreamWriter(filePath, false, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("ts_rel_s,ts_utc_iso,role,localClientId,event,detail");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NetAudit] Could not open log file: {ex.Message}");
            writer = null;
        }
    }

    private void OnEnable()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Write("none", -1, "INIT_ERROR", "NetworkManager.Singleton is null at OnEnable");
            return;
        }

        nm.OnServerStarted += HandleServerStarted;
        nm.OnClientStarted += HandleClientStarted;
        nm.OnServerStopped += HandleServerStopped;
        nm.OnClientStopped += HandleClientStopped;
        nm.OnClientConnectedCallback += HandleClientConnected;
        nm.OnClientDisconnectCallback += HandleClientDisconnected;
        nm.OnTransportFailure += HandleTransportFailure;

        Write(RoleString(), Self(), "AUDIT_LOGGER_READY", $"file={filePath ?? "(disabled)"}");
    }

    private void OnDisable()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnServerStarted -= HandleServerStarted;
            nm.OnClientStarted -= HandleClientStarted;
            nm.OnServerStopped -= HandleServerStopped;
            nm.OnClientStopped -= HandleClientStopped;
            nm.OnClientConnectedCallback -= HandleClientConnected;
            nm.OnClientDisconnectCallback -= HandleClientDisconnected;
            nm.OnTransportFailure -= HandleTransportFailure;
        }

        writer?.Close();
        writer = null;
    }

    private void Update()
    {
        if (Input.GetKeyDown(snapshotKey))
        {
            DumpSpawnedSnapshot();
        }
    }

    private void HandleServerStarted() => Write(RoleString(), Self(), "SERVER_STARTED", "");
    private void HandleClientStarted() => Write(RoleString(), Self(), "CLIENT_STARTED", "");
    private void HandleServerStopped(bool wasHost) => Write(RoleString(), Self(), "SERVER_STOPPED", $"wasHost={wasHost}");
    private void HandleClientStopped(bool wasHost) => Write(RoleString(), Self(), "CLIENT_STOPPED", $"wasHost={wasHost}");
    private void HandleClientConnected(ulong clientId) => Write(RoleString(), Self(), "CLIENT_CONNECTED", $"connectedClientId={clientId}");
    private void HandleClientDisconnected(ulong clientId) => Write(RoleString(), Self(), "CLIENT_DISCONNECTED", $"disconnectedClientId={clientId}");
    private void HandleTransportFailure() => Write(RoleString(), Self(), "TRANSPORT_FAILURE", "");

    private void DumpSpawnedSnapshot()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            Write(RoleString(), Self(), "SNAPSHOT_SKIPPED", "NetworkManager not listening");
            return;
        }

        var spawned = nm.SpawnManager?.SpawnedObjectsList;
        int count = spawned == null ? 0 : spawned.Count;
        Write(RoleString(), Self(), "SNAPSHOT_BEGIN", $"spawnedCount={count}");

        if (spawned == null) return;

        foreach (var no in spawned)
        {
            if (no == null) continue;
            var t = no.transform;
            string detail =
                $"netId={no.NetworkObjectId}|prefabHash={no.PrefabIdHash}" +
                $"|owner={no.OwnerClientId}|isPlayer={no.IsPlayerObject}" +
                $"|pos={t.position.x:F2},{t.position.y:F2},{t.position.z:F2}" +
                $"|name={no.name}";
            Write(RoleString(), Self(), "SPAWNED_OBJECT", detail);
        }

        Write(RoleString(), Self(), "SNAPSHOT_END", "");
    }

    private string RoleString()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return "none";
        if (nm.IsHost) return "host";
        if (nm.IsServer) return "server";
        if (nm.IsClient) return "client";
        return "idle";
    }

    private long Self()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) return -1;
        return (long)nm.LocalClientId;
    }

    private void Write(string role, long clientId, string ev, string detail)
    {
        float t = Time.realtimeSinceStartup - startTime;
        string iso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        string safeDetail = detail?.Replace(",", ";") ?? "";
        string line = $"{t:F3},{iso},{role},{clientId},{ev},{safeDetail}";

        if (logToConsole) Debug.Log($"[NetAudit] {line}");
        writer?.WriteLine(line);
    }
}
