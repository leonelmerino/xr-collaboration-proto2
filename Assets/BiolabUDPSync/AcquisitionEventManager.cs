using System.Collections;
using UnityEngine;

public class AcquisitionEventManager : MonoBehaviour
{
    [Header("References")]
    public AcquisitionNodeConfig config;
    public ExperimentEventLogger eventLogger;
    public AcquisitionMockServer mockServer;

    [Header("Auto Flow")]
    public bool autoStartSession = true;
    public float autoStartDelaySeconds = 2f;

    [Header("Heartbeat")]
    public bool sendTaskHeartbeat = true;
    public float heartbeatIntervalSeconds = 60f;

    [Header("Runtime State")]
    public bool acquisitionReachable;
    public bool acquisitionRunning;
    public bool sessionStarted;
    public bool taskRunning;
    public string currentTaskId = "";

    private BioLabUdpClient udpClient;
    private Coroutine heartbeatCoroutine;

    private void Awake()
    {
        if (config == null)
            config = GetComponent<AcquisitionNodeConfig>();

        if (eventLogger == null)
            eventLogger = GetComponent<ExperimentEventLogger>();

        if (mockServer == null)
            mockServer = GetComponent<AcquisitionMockServer>();

        if (config == null)
        {
            Debug.LogError("[AcquisitionEventManager] Missing AcquisitionNodeConfig.");
            enabled = false;
            return;
        }

        if (eventLogger != null)
            eventLogger.nodeId = config.nodeId;

        if (config.IsMockOnly)
        {
            Debug.Log("[AcquisitionEventManager] Running in MOCK ONLY mode.");

            if (mockServer == null)
                mockServer = gameObject.AddComponent<AcquisitionMockServer>();

            mockServer.listenPort = config.acquisitionPort;
            mockServer.autoStart = false;
            mockServer.StartServer();

            return;
        }

        if (config.IsHost && config.useEmbeddedAcquisitionMock)
        {
            Debug.Log("[AcquisitionEventManager] Starting embedded acquisition mock.");

            if (mockServer == null)
                mockServer = gameObject.AddComponent<AcquisitionMockServer>();

            mockServer.listenPort = config.acquisitionPort;
            mockServer.autoStart = false;
            mockServer.StartServer();

            config.acquisitionIp = "127.0.0.1";
        }

        udpClient = new BioLabUdpClient(
            config.acquisitionIp,
            config.acquisitionPort,
            config.responseTimeoutMs
        );
    }

    private IEnumerator Start()
    {
        if (!autoStartSession)
            yield break;

        yield return new WaitForSeconds(autoStartDelaySeconds);

        BeginExperimentalSession();
    }

    public void BeginExperimentalSession()
    {
        Debug.Log("[AcquisitionEventManager] BeginExperimentalSession requested.");

        if (!config.IsHost)
        {
            LogLocal("SESSION_CONTROL", "SESSION_START_IGNORED_NON_HOST");
            return;
        }

        if (sessionStarted)
        {
            LogLocal("SESSION_CONTROL", "SESSION_START_IGNORED_ALREADY_STARTED");
            return;
        }

        StartCoroutine(BeginExperimentalSessionRoutine());
    }

    public void EndExperimentalSession()
    {
        Debug.Log("[AcquisitionEventManager] EndExperimentalSession requested.");

        if (!config.IsHost)
        {
            LogLocal("SESSION_CONTROL", "SESSION_END_IGNORED_NON_HOST");
            return;
        }

        StartCoroutine(EndExperimentalSessionRoutine());
    }

    public void BeginTask(string taskId)
    {
        Debug.Log($"[AcquisitionEventManager] BeginTask: {taskId}");

        if (!sessionStarted)
        {
            LogLocal("TASK_CONTROL", $"TASK_START_REJECTED_{taskId}", "Session not started");
            return;
        }

        if (taskRunning)
        {
            LogLocal("TASK_CONTROL", $"TASK_START_REJECTED_{taskId}", "Another task already running");
            return;
        }

        taskRunning = true;
        currentTaskId = taskId;

        LogAndMaybeForward("TASK_START", BuildMetadataPayload("TASK_START"));
    }

    public void EndTask()
    {
        Debug.Log("[AcquisitionEventManager] EndTask requested.");

        if (!taskRunning)
        {
            LogLocal("TASK_CONTROL", "TASK_END_REJECTED_NO_ACTIVE_TASK");
            return;
        }

        taskRunning = false;

        LogAndMaybeForward("TASK_END", BuildMetadataPayload("TASK_END"));

        currentTaskId = "";
    }

    private IEnumerator BeginExperimentalSessionRoutine()
    {
        Debug.Log("[AcquisitionEventManager] Starting experimental session routine.");

        LogLocal("SESSION_CONTROL", "SESSION_START_REQUEST");

        var pingTask = udpClient.PingAsync();
        yield return new WaitUntil(() => pingTask.IsCompleted);

        string pingResponse = pingTask.Result;
        acquisitionReachable = pingResponse == "PONG";

        Debug.Log($"[AcquisitionEventManager] Ping response: {pingResponse}");
        LogLocal("SESSION_CONTROL", "PING_RESULT", pingResponse);

        if (!acquisitionReachable)
        {
            Debug.LogWarning("[AcquisitionEventManager] Acquisition not reachable.");

            if (config.requireAcquisitionForSessionStart)
            {
                LogLocal("SESSION_CONTROL", "SESSION_ABORTED_NO_ACQUISITION");
                yield break;
            }

            sessionStarted = true;

            LogLocal("SESSION_START", "SESSION_START_NO_ACQ");

            yield return StartCoroutine(
                ForwardEventRoutine(
                    "SESSION_START",
                    BuildMetadataPayload("SESSION_START_NO_ACQ")
                )
            );

            yield return StartCoroutine(
                ForwardEventRoutine(
                    "TASK_CONTEXT",
                    BuildMetadataPayload("TASK_CONTEXT_NO_ACQ")
                )
            );

            StartHeartbeatIfNeeded();

            yield break;
        }

        var startTask = udpClient.StartAcquisitionAsync();
        yield return new WaitUntil(() => startTask.IsCompleted);

        string startResponse = startTask.Result;
        acquisitionRunning = startResponse == "OK";

        Debug.Log($"[AcquisitionEventManager] Acquisition START response: {startResponse}");
        LogLocal("SESSION_CONTROL", "START_RESULT", startResponse);

        if (!acquisitionRunning && config.requireAcquisitionForSessionStart)
            yield break;

        sessionStarted = true;

        yield return StartCoroutine(
            ForwardEventRoutine(
                "SESSION_START",
                BuildMetadataPayload("SESSION_START")
            )
        );

        yield return StartCoroutine(
            ForwardEventRoutine(
                "TASK_CONTEXT",
                BuildMetadataPayload("TASK_CONTEXT")
            )
        );

        StartHeartbeatIfNeeded();
    }

    private IEnumerator EndExperimentalSessionRoutine()
    {
        Debug.Log("[AcquisitionEventManager] Ending experimental session routine.");

        if (!sessionStarted)
        {
            LogLocal("SESSION_CONTROL", "SESSION_END_IGNORED_NOT_STARTED");
            yield break;
        }

        StopHeartbeat();

        if (taskRunning)
            LogLocal("SESSION_CONTROL", "SESSION_END_WITH_ACTIVE_TASK", currentTaskId);

        yield return StartCoroutine(
            ForwardEventRoutine(
                "SESSION_END",
                BuildMetadataPayload("SESSION_END")
            )
        );

        if (acquisitionReachable && acquisitionRunning)
        {
            var stopTask = udpClient.StopAcquisitionAsync();
            yield return new WaitUntil(() => stopTask.IsCompleted);

            string stopResponse = stopTask.Result;

            Debug.Log($"[AcquisitionEventManager] Acquisition STOP response: {stopResponse}");
            LogLocal("SESSION_CONTROL", "STOP_RESULT", stopResponse);

            acquisitionRunning = false;
        }

        sessionStarted = false;
        taskRunning = false;
        currentTaskId = "";
    }

    private void StartHeartbeatIfNeeded()
    {
        if (!sendTaskHeartbeat)
            return;

        if (heartbeatCoroutine != null)
            return;

        Debug.Log($"[AcquisitionEventManager] Starting heartbeat every {heartbeatIntervalSeconds} seconds.");
        heartbeatCoroutine = StartCoroutine(HeartbeatRoutine());
    }

    private void StopHeartbeat()
    {
        if (heartbeatCoroutine == null)
            return;

        Debug.Log("[AcquisitionEventManager] Stopping heartbeat.");

        StopCoroutine(heartbeatCoroutine);
        heartbeatCoroutine = null;
    }

    private IEnumerator HeartbeatRoutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(heartbeatIntervalSeconds);

            if (!sessionStarted)
                yield break;

            yield return StartCoroutine(
                ForwardEventRoutine(
                    "TASK_CONTEXT_HEARTBEAT",
                    BuildMetadataPayload("TASK_CONTEXT_HEARTBEAT")
                )
            );
        }
    }

    private string BuildMetadataPayload(string eventLabel)
    {
        string participantId = "UNKNOWN_PARTICIPANT";
        string sessionId = "UNKNOWN_SESSION";
        string taskId = "UNKNOWN_TASK";
        string trialId = "UNKNOWN_TRIAL";

        if (eventLogger != null)
        {
            participantId = eventLogger.participantId;
            sessionId = eventLogger.sessionId;
            taskId = eventLogger.taskId;
            trialId = eventLogger.trialId;
        }

        return
            $"{eventLabel}" +
            $"|participant={participantId}" +
            $"|session={sessionId}" +
            $"|task={taskId}" +
            $"|trial={trialId}" +
            $"|node={config.nodeId}";
    }

    private void LogAndMaybeForward(string eventType, string eventValue)
    {
        Debug.Log($"[AcquisitionEventManager] Local event: {eventType} -> {eventValue}");

        LogLocal(eventType, eventValue);

        if (config.IsHost)
            StartCoroutine(ForwardEventRoutine(eventType, eventValue));
    }

    private IEnumerator ForwardEventRoutine(string eventType, string eventValue)
    {
        string encoded = eventValue;

        Debug.Log($"[AcquisitionEventManager] Forwarding event: {encoded}");

        if (!acquisitionReachable)
        {
            Debug.Log($"[AcquisitionEventManager] Acquisition unreachable. Event kept local: {encoded}");
            yield break;
        }

        var task = udpClient.SendEventAsync(config.nodeId, encoded);

        yield return new WaitUntil(() => task.IsCompleted);

        Debug.Log($"[AcquisitionEventManager] Forward result: {encoded} -> {task.Result}");

        LogLocal("FORWARD_RESULT", encoded, task.Result);
    }

    private void LogLocal(string eventType, string eventValue, string notes = "")
    {
        if (eventLogger != null)
            eventLogger.LogEvent(eventType, eventValue, notes);
    }
}