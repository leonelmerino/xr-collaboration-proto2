using System.Collections;
using UnityEngine;

public class AcquisitionEventManager : MonoBehaviour
{
    public static AcquisitionEventManager Instance { get; private set; }

    [Header("References")]
    public AcquisitionNodeConfig config;
    public ExperimentEventLogger eventLogger;
    public AcquisitionMockServer mockServer;

    [Header("Auto Flow")]
    public bool autoStartSession = true;
    public float autoStartDelaySeconds = 2f;

    [Header("Clock Sync")]
    [Tooltip("Si esta activo, los clientes esperan al handshake de NetworkClockSync antes de iniciar la sesion (con timeout).")]
    public bool waitForClockSync = true;
    public float clockSyncWaitTimeoutSec = 5f;

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
        if (Instance == null) Instance = this;

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

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private IEnumerator Start()
    {
        if (!autoStartSession)
            yield break;

        yield return new WaitForSeconds(autoStartDelaySeconds);

        // Si no soy host, espero a que el handshake de clock sync termine (o timeout).
        if (waitForClockSync && config != null && !config.IsHost)
        {
            float startedAt = Time.realtimeSinceStartup;
            while (NetworkClockSync.Instance != null
                   && !NetworkClockSync.Instance.IsSynced
                   && (Time.realtimeSinceStartup - startedAt) < clockSyncWaitTimeoutSec)
            {
                yield return null;
            }

            if (NetworkClockSync.Instance != null && NetworkClockSync.Instance.IsSynced)
            {
                Debug.Log($"[AcquisitionEventManager] Clock sync OK antes de SESSION_START. offset_ms={NetworkClockSync.Instance.OffsetToHost * 1000:F3}");
            }
            else
            {
                Debug.LogWarning("[AcquisitionEventManager] Clock sync NO confirmado. Continuamos con offset=0; los eventos quedan tagueados como CLOCK_NOT_SYNCED.");
                LogLocal("CLOCK_SYNC", "TIMEOUT_PROCEED_WITHOUT_SYNC");
            }
        }

        BeginExperimentalSession();
    }

    public void BeginExperimentalSession()
    {
        Debug.Log($"[AcquisitionEventManager] BeginExperimentalSession requested (node={config.nodeId}, role={config.role}).");

        if (sessionStarted)
        {
            LogLocal("SESSION_CONTROL", "SESSION_START_IGNORED_ALREADY_STARTED");
            return;
        }

        StartCoroutine(BeginExperimentalSessionRoutine());
    }

    public void EndExperimentalSession()
    {
        Debug.Log($"[AcquisitionEventManager] EndExperimentalSession requested (node={config.nodeId}).");

        StartCoroutine(EndExperimentalSessionRoutine());
    }

    public void BeginTask(string taskId)
    {
        Debug.Log($"[AcquisitionEventManager] BeginTask: {taskId} (node={config.nodeId})");

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

        LogAndForward("TASK_START", BuildMetadataPayload("TASK_START"));
    }

    public void EndTask()
    {
        Debug.Log($"[AcquisitionEventManager] EndTask requested (node={config.nodeId}).");

        if (!taskRunning)
        {
            LogLocal("TASK_CONTROL", "TASK_END_REJECTED_NO_ACTIVE_TASK");
            return;
        }

        taskRunning = false;

        LogAndForward("TASK_END", BuildMetadataPayload("TASK_END"));

        currentTaskId = "";
    }

    /// <summary>
    /// Punto de entrada para eventos discretos de interaccion (grab, release, etc.).
    /// Cada nodo loggea y reenvia su propio evento tagueado con su node_id.
    /// </summary>
    public void EmitInteractionEvent(string eventLabel, string detailKey = null, string detailValue = null)
    {
        if (!sessionStarted)
        {
            LogLocal("INTERACTION_REJECTED", eventLabel, "Session not started");
            return;
        }

        string payload = BuildMetadataPayload(eventLabel);
        if (!string.IsNullOrEmpty(detailKey))
            payload += $"|{detailKey}={detailValue}";

        LogAndForward("INTERACTION", payload);
    }

    private IEnumerator BeginExperimentalSessionRoutine()
    {
        Debug.Log($"[AcquisitionEventManager] Starting experimental session routine (node={config.nodeId}).");

        LogLocal("SESSION_CONTROL", "SESSION_START_REQUEST");

        // PING: cada nodo verifica conectividad por su cuenta.
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

        // START: SOLO el host le dice a BioLab que arranque la grabacion.
        if (config.IsHost)
        {
            var startTask = udpClient.StartAcquisitionAsync();
            yield return new WaitUntil(() => startTask.IsCompleted);

            string startResponse = startTask.Result;
            acquisitionRunning = startResponse == "OK";

            Debug.Log($"[AcquisitionEventManager] Acquisition START response: {startResponse}");
            LogLocal("SESSION_CONTROL", "START_RESULT", startResponse);

            if (!acquisitionRunning && config.requireAcquisitionForSessionStart)
                yield break;
        }
        else
        {
            // Client/Helper asumen que el host ya inicio (o lo hara) la adquisicion.
            acquisitionRunning = true;
            LogLocal("SESSION_CONTROL", "START_SKIPPED_NON_HOST");
        }

        sessionStarted = true;

        // SESSION_START tagueado con node_id (cada nodo envia el suyo).
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
        Debug.Log($"[AcquisitionEventManager] Ending experimental session routine (node={config.nodeId}).");

        if (!sessionStarted)
        {
            LogLocal("SESSION_CONTROL", "SESSION_END_IGNORED_NOT_STARTED");
            yield break;
        }

        StopHeartbeat();

        if (taskRunning)
            LogLocal("SESSION_CONTROL", "SESSION_END_WITH_ACTIVE_TASK", currentTaskId);

        // SESSION_END por nodo.
        yield return StartCoroutine(
            ForwardEventRoutine(
                "SESSION_END",
                BuildMetadataPayload("SESSION_END")
            )
        );

        // STOP: SOLO el host le dice a BioLab que pare la grabacion.
        if (config.IsHost && acquisitionReachable && acquisitionRunning)
        {
            var stopTask = udpClient.StopAcquisitionAsync();
            yield return new WaitUntil(() => stopTask.IsCompleted);

            string stopResponse = stopTask.Result;

            Debug.Log($"[AcquisitionEventManager] Acquisition STOP response: {stopResponse}");
            LogLocal("SESSION_CONTROL", "STOP_RESULT", stopResponse);

            acquisitionRunning = false;
        }
        else if (!config.IsHost)
        {
            LogLocal("SESSION_CONTROL", "STOP_SKIPPED_NON_HOST");
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

        Debug.Log($"[AcquisitionEventManager] Starting heartbeat every {heartbeatIntervalSeconds} seconds (node={config.nodeId}).");
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

        double tLocal = Time.realtimeSinceStartupAsDouble;
        double tHost = tLocal;
        string syncStatus = "host";
        if (NetworkClockSync.Instance != null)
        {
            tHost = NetworkClockSync.Instance.GetHostTime();
            syncStatus = NetworkClockSync.Instance.IsSynced ? "synced" : "unsynced";
        }

        return
            $"{eventLabel}" +
            $"|participant={participantId}" +
            $"|session={sessionId}" +
            $"|task={taskId}" +
            $"|trial={trialId}" +
            $"|node={config.nodeId}" +
            $"|t_local={tLocal:F6}" +
            $"|t_host={tHost:F6}" +
            $"|sync={syncStatus}";
    }

    // Punto de entrada publico para que otros componentes (ej. NetworkClockSync)
    // loggeen un evento solo localmente sin forwardear a BioLab.
    public void LogLocalExternal(string eventType, string detail)
    {
        LogLocal(eventType, detail);
    }

    // Punto de entrada publico para que otros componentes loggeen + forwardeen
    // a BioLab (usado por el host para SYNC_MARKERs).
    public void LogAndForwardExternal(string eventType, string detail)
    {
        LogAndForward(eventType, detail);
    }

    private void LogAndForward(string eventType, string eventValue)
    {
        Debug.Log($"[AcquisitionEventManager] Local event: {eventType} -> {eventValue}");

        LogLocal(eventType, eventValue);
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

        if (udpClient == null)
        {
            Debug.LogWarning("[AcquisitionEventManager] udpClient is null; skipping forward.");
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
