using System.Collections;
using UnityEngine;

public class BioLabSessionCoordinator : MonoBehaviour
{
    [Header("Config")]
    public AcquisitionConfig config;
    public ExperimentEventLogger eventLogger;

    [Header("State")]
    public bool acquisitionReachable;
    public bool acquisitionRunning;

    private BioLabUdpClient client;

    private void Awake()
    {
        if (config == null)
        {
            Debug.LogError("[BioLabSessionCoordinator] Missing AcquisitionConfig.");
            enabled = false;
            return;
        }

        if (eventLogger == null)
            eventLogger = FindObjectOfType<ExperimentEventLogger>();

        client = new BioLabUdpClient(config.acquisitionIp, config.acquisitionPort, config.receiveTimeoutMs);
    }

    public void BeginSession()
    {
        StartCoroutine(BeginSessionRoutine());
    }

    public void EndSession()
    {
        StartCoroutine(EndSessionRoutine());
    }

    private IEnumerator BeginSessionRoutine()
    {
        if (eventLogger != null)
            eventLogger.LogEvent("SESSION_CONTROL", "BEGIN_SESSION_REQUEST");

        var pingTask = client.PingAsync();
        yield return new WaitUntil(() => pingTask.IsCompleted);

        string pingResponse = pingTask.Result;
        acquisitionReachable = pingResponse == "PONG";

        if (eventLogger != null)
            eventLogger.LogEvent("SESSION_CONTROL", "PING_RESULT", pingResponse);

        if (!acquisitionReachable)
        {
            Debug.LogWarning($"[BioLabSessionCoordinator] Acquisition not reachable: {pingResponse}");

            if (config.requireAcquisitionForSessionStart)
            {
                if (eventLogger != null)
                    eventLogger.LogEvent("SESSION_CONTROL", "SESSION_ABORTED_NO_ACQUISITION");
                yield break;
            }

            BroadcastLocalSyncMarker("SYNC_T0_NO_ACQ");
            yield break;
        }

        var startTask = client.StartAcquisitionAsync();
        yield return new WaitUntil(() => startTask.IsCompleted);

        string startResponse = startTask.Result;
        acquisitionRunning = startResponse == "OK";

        if (eventLogger != null)
            eventLogger.LogEvent("SESSION_CONTROL", "START_RESULT", startResponse);

        if (!acquisitionRunning)
        {
            Debug.LogWarning($"[BioLabSessionCoordinator] START failed: {startResponse}");

            if (config.requireAcquisitionForSessionStart)
                yield break;
        }

        if (config.sendSyncMarkers)
        {
            yield return StartCoroutine(SendSyncMarkerRoutine("SYNC_T0"));
            yield return new WaitForSeconds(2f);
            yield return StartCoroutine(SendSyncMarkerRoutine("SYNC_T1"));
        }
    }

    private IEnumerator EndSessionRoutine()
    {
        if (eventLogger != null)
            eventLogger.LogEvent("SESSION_CONTROL", "END_SESSION_REQUEST");

        if (acquisitionReachable && acquisitionRunning)
        {
            var stopEventTask = client.SendEventAsync(config.nodeId, "SESSION_STOP");
            yield return new WaitUntil(() => stopEventTask.IsCompleted);

            if (eventLogger != null)
                eventLogger.LogEvent("SESSION_CONTROL", "SESSION_STOP_EVENT_RESULT", stopEventTask.Result);

            var stopTask = client.StopAcquisitionAsync();
            yield return new WaitUntil(() => stopTask.IsCompleted);

            string stopResponse = stopTask.Result;
            if (eventLogger != null)
                eventLogger.LogEvent("SESSION_CONTROL", "STOP_RESULT", stopResponse);

            acquisitionRunning = false;
        }
        else
        {
            BroadcastLocalSyncMarker("SESSION_STOP_NO_ACQ");
        }
    }

    public void ReportInteractionEvent(string eventValue)
    {
        StartCoroutine(ReportInteractionEventRoutine(eventValue));
    }

    private IEnumerator ReportInteractionEventRoutine(string eventValue)
    {
        if (eventLogger != null)
            eventLogger.LogEvent("INTERACTION", eventValue);

        if (!acquisitionReachable)
            yield break;

        var task = client.SendEventAsync(config.nodeId, eventValue);
        yield return new WaitUntil(() => task.IsCompleted);

        if (eventLogger != null)
            eventLogger.LogEvent("INTERACTION_FORWARD", eventValue, task.Result);
    }

    private IEnumerator SendSyncMarkerRoutine(string marker)
    {
        if (eventLogger != null)
            eventLogger.LogEvent("SYNC", marker);

        if (acquisitionReachable)
        {
            var task = client.SendEventAsync(config.nodeId, marker);
            yield return new WaitUntil(() => task.IsCompleted);

            if (eventLogger != null)
                eventLogger.LogEvent("SYNC_FORWARD", marker, task.Result);
        }

        BroadcastLocalSyncMarker(marker);
    }

    private void BroadcastLocalSyncMarker(string marker)
    {
        // Aquí debes reemplazar esto por tu RPC real de red.
        // Por ahora lo dejamos como llamada local + placeholder.
        Debug.Log($"[BioLabSessionCoordinator] Broadcast local sync marker: {marker}");

        LocalSyncMarkerReceiver[] receivers = FindObjectsOfType<LocalSyncMarkerReceiver>();
        foreach (var receiver in receivers)
        {
            receiver.ReceiveSyncMarker(marker);
        }
    }
}