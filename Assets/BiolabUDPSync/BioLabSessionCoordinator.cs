using System.Collections;
using UnityEngine;

public class BioLabSessionCoordinator : MonoBehaviour
{
    [Header("Config")]
    public AcquisitionConfig config;
    public ExperimentEventLogger eventLogger;

    [Header("Eye Tracking Metadata Source")]
    public EyeTrackingSessionLogger eyeTrackingLogger;

    [Header("State")]
    public bool acquisitionReachable;
    public bool acquisitionRunning;

    [Header("Fixed Experiment Flow")]
    public bool autoBeginSession = true;
    public float autoBeginDelaySeconds = 2f;

    [Header("Metadata Heartbeat")]
    public bool sendMetadataHeartbeat = true;
    public float metadataHeartbeatSeconds = 60f;

    private BioLabUdpClient client;
    private Coroutine metadataHeartbeatCoroutine;
    private bool sessionStarted;

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

        if (eyeTrackingLogger == null)
            eyeTrackingLogger = FindObjectOfType<EyeTrackingSessionLogger>();

        client = new BioLabUdpClient(config.acquisitionIp, config.acquisitionPort, config.receiveTimeoutMs);
    }

    private IEnumerator Start()
    {
        if (!autoBeginSession)
            yield break;

        yield return new WaitForSeconds(autoBeginDelaySeconds);

        BeginSession();
    }

    public void BeginSession()
    {
        if (sessionStarted)
        {
            Debug.Log("[BioLabSessionCoordinator] BeginSession ignored: session already started.");

            if (eventLogger != null)
                eventLogger.LogEvent("SESSION_CONTROL", "BEGIN_SESSION_IGNORED_ALREADY_STARTED");

            return;
        }

        StartCoroutine(BeginSessionRoutine());
    }

    public void EndSession()
    {
        StartCoroutine(EndSessionRoutine());
    }

    private IEnumerator BeginSessionRoutine()
    {
        Debug.Log("[BioLabSessionCoordinator] Begin session requested.");

        if (eventLogger != null)
            eventLogger.LogEvent("SESSION_CONTROL", "BEGIN_SESSION_REQUEST");

        var pingTask = client.PingAsync();
        yield return new WaitUntil(() => pingTask.IsCompleted);

        string pingResponse = pingTask.Result;
        acquisitionReachable = pingResponse == "PONG";

        Debug.Log($"[BioLabSessionCoordinator] Ping result: {pingResponse}");

        if (eventLogger != null)
            eventLogger.LogEvent("SESSION_CONTROL", "PING_RESULT", pingResponse);

        if (!acquisitionReachable)
        {
            Debug.LogWarning($"[BioLabSessionCoordinator] Acquisition not reachable: {pingResponse}");

            if (config.requireAcquisitionForSessionStart)
            {
                Debug.LogWarning("[BioLabSessionCoordinator] Session aborted because acquisition is required.");

                if (eventLogger != null)
                    eventLogger.LogEvent("SESSION_CONTROL", "SESSION_ABORTED_NO_ACQUISITION");

                yield break;
            }

            sessionStarted = true;

            yield return StartCoroutine(SendExperimentMetadataEventRoutine("SESSION_START_NO_ACQ"));
            yield return StartCoroutine(SendExperimentMetadataEventRoutine("TASK_CONTEXT_NO_ACQ"));

            StartMetadataHeartbeatIfNeeded();

            yield break;
        }

        var startTask = client.StartAcquisitionAsync();
        yield return new WaitUntil(() => startTask.IsCompleted);

        string startResponse = startTask.Result;
        acquisitionRunning = startResponse == "OK";

        Debug.Log($"[BioLabSessionCoordinator] Start acquisition result: {startResponse}");

        if (eventLogger != null)
            eventLogger.LogEvent("SESSION_CONTROL", "START_RESULT", startResponse);

        if (!acquisitionRunning)
        {
            Debug.LogWarning($"[BioLabSessionCoordinator] START failed: {startResponse}");

            if (config.requireAcquisitionForSessionStart)
                yield break;
        }

        sessionStarted = true;

        yield return StartCoroutine(SendExperimentMetadataEventRoutine("SESSION_START"));
        yield return StartCoroutine(SendExperimentMetadataEventRoutine("TASK_CONTEXT"));

        if (config.sendSyncMarkers)
        {
            yield return StartCoroutine(SendSyncMarkerRoutine("SYNC_T0"));
            yield return new WaitForSeconds(2f);
            yield return StartCoroutine(SendSyncMarkerRoutine("SYNC_T1"));
        }

        StartMetadataHeartbeatIfNeeded();
    }

    private IEnumerator EndSessionRoutine()
    {
        Debug.Log("[BioLabSessionCoordinator] End session requested.");

        StopMetadataHeartbeat();

        if (eventLogger != null)
            eventLogger.LogEvent("SESSION_CONTROL", "END_SESSION_REQUEST");

        yield return StartCoroutine(SendExperimentMetadataEventRoutine("SESSION_END"));

        if (acquisitionReachable && acquisitionRunning)
        {
            Debug.Log("[BioLabSessionCoordinator] Sending SESSION_STOP event to BioLab.");

            var stopEventTask = client.SendEventAsync(config.nodeId, "SESSION_STOP");
            yield return new WaitUntil(() => stopEventTask.IsCompleted);

            Debug.Log($"[BioLabSessionCoordinator] SESSION_STOP result: {stopEventTask.Result}");

            if (eventLogger != null)
                eventLogger.LogEvent("SESSION_CONTROL", "SESSION_STOP_EVENT_RESULT", stopEventTask.Result);

            Debug.Log("[BioLabSessionCoordinator] Sending STOP command to BioLab.");

            var stopTask = client.StopAcquisitionAsync();
            yield return new WaitUntil(() => stopTask.IsCompleted);

            string stopResponse = stopTask.Result;

            Debug.Log($"[BioLabSessionCoordinator] Stop acquisition result: {stopResponse}");

            if (eventLogger != null)
                eventLogger.LogEvent("SESSION_CONTROL", "STOP_RESULT", stopResponse);

            acquisitionRunning = false;
        }
        else
        {
            Debug.Log("[BioLabSessionCoordinator] Session stop kept local only: acquisition not reachable or not running.");
            BroadcastLocalSyncMarker("SESSION_STOP_NO_ACQ");
        }

        sessionStarted = false;
    }

    public void ReportInteractionEvent(string eventValue)
    {
        StartCoroutine(ReportInteractionEventRoutine(eventValue));
    }

    private IEnumerator ReportInteractionEventRoutine(string eventValue)
    {
        Debug.Log($"[BioLabSessionCoordinator] Interaction event: {eventValue}");

        if (eventLogger != null)
            eventLogger.LogEvent("INTERACTION", eventValue);

        if (!acquisitionReachable)
        {
            Debug.Log($"[BioLabSessionCoordinator] Interaction event kept local only: {eventValue}");
            yield break;
        }

        var task = client.SendEventAsync(config.nodeId, eventValue);
        yield return new WaitUntil(() => task.IsCompleted);

        Debug.Log($"[BioLabSessionCoordinator] Interaction event forwarded to BioLab: {eventValue} | result={task.Result}");

        if (eventLogger != null)
            eventLogger.LogEvent("INTERACTION_FORWARD", eventValue, task.Result);
    }

    private void StartMetadataHeartbeatIfNeeded()
    {
        if (!sendMetadataHeartbeat)
        {
            Debug.Log("[BioLabSessionCoordinator] Metadata heartbeat disabled.");
            return;
        }

        if (metadataHeartbeatCoroutine != null)
        {
            Debug.Log("[BioLabSessionCoordinator] Metadata heartbeat already running.");
            return;
        }

        Debug.Log($"[BioLabSessionCoordinator] Starting metadata heartbeat every {metadataHeartbeatSeconds} seconds.");
        metadataHeartbeatCoroutine = StartCoroutine(MetadataHeartbeatRoutine());
    }

    private void StopMetadataHeartbeat()
    {
        if (metadataHeartbeatCoroutine == null)
            return;

        Debug.Log("[BioLabSessionCoordinator] Stopping metadata heartbeat.");

        StopCoroutine(metadataHeartbeatCoroutine);
        metadataHeartbeatCoroutine = null;
    }

    private IEnumerator MetadataHeartbeatRoutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(metadataHeartbeatSeconds);

            if (!sessionStarted)
                yield break;

            yield return StartCoroutine(SendExperimentMetadataEventRoutine("TASK_CONTEXT_HEARTBEAT"));
        }
    }

    private IEnumerator SendExperimentMetadataEventRoutine(string eventType)
    {
        string payload = BuildExperimentMetadataPayload(eventType);

        Debug.Log($"[BioLabSessionCoordinator] Sending metadata event: {payload}");

        if (eventLogger != null)
            eventLogger.LogEvent("SESSION_METADATA", payload);

        if (acquisitionReachable)
        {
            var task = client.SendEventAsync(config.nodeId, payload);
            yield return new WaitUntil(() => task.IsCompleted);

            Debug.Log($"[BioLabSessionCoordinator] Metadata event forwarded to BioLab: {payload} | result={task.Result}");

            if (eventLogger != null)
                eventLogger.LogEvent("SESSION_METADATA_FORWARD", payload, task.Result);
        }
        else
        {
            Debug.Log($"[BioLabSessionCoordinator] Metadata event kept local only: {payload}");
        }

        BroadcastLocalSyncMarker(payload);
    }

    private string BuildExperimentMetadataPayload(string eventType)
    {
        string participantId = "UNKNOWN_PARTICIPANT";
        string sessionId = "UNKNOWN_SESSION";
        string taskId = "UNKNOWN_TASK";
        string trialId = "UNKNOWN_TRIAL";
        string condition = "UNKNOWN_CONDITION";

        if (eyeTrackingLogger != null)
        {
            participantId = eyeTrackingLogger.participantId;
            sessionId = eyeTrackingLogger.sessionId;
            taskId = eyeTrackingLogger.taskId;
            trialId = eyeTrackingLogger.trialId;
            condition = eyeTrackingLogger.condition;
        }
        else if (eventLogger != null)
        {
            participantId = eventLogger.participantId;
            sessionId = eventLogger.sessionId;
            taskId = eventLogger.taskId;
        }

        return $"{eventType}|participant={participantId}|session={sessionId}|task={taskId}|trial={trialId}|condition={condition}";
    }

    private IEnumerator SendSyncMarkerRoutine(string marker)
    {
        Debug.Log($"[BioLabSessionCoordinator] Sending sync marker: {marker}");

        if (eventLogger != null)
            eventLogger.LogEvent("SYNC", marker);

        if (acquisitionReachable)
        {
            var task = client.SendEventAsync(config.nodeId, marker);
            yield return new WaitUntil(() => task.IsCompleted);

            Debug.Log($"[BioLabSessionCoordinator] Sync marker forwarded to BioLab: {marker} | result={task.Result}");

            if (eventLogger != null)
                eventLogger.LogEvent("SYNC_FORWARD", marker, task.Result);
        }
        else
        {
            Debug.Log($"[BioLabSessionCoordinator] Sync marker kept local only: {marker}");
        }

        BroadcastLocalSyncMarker(marker);
    }

    private void BroadcastLocalSyncMarker(string marker)
    {
        Debug.Log($"[BioLabSessionCoordinator] Broadcast local sync marker: {marker}");

        LocalSyncMarkerReceiver[] receivers = FindObjectsOfType<LocalSyncMarkerReceiver>();
        foreach (var receiver in receivers)
        {
            receiver.ReceiveSyncMarker(marker);
        }
    }
}