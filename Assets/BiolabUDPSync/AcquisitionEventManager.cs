using System.Collections;
using UnityEngine;

public class AcquisitionEventManager : MonoBehaviour
{
    [Header("References")]
    public AcquisitionNodeConfig config;
    public ExperimentEventLogger eventLogger;
    public AcquisitionMockServer mockServer;

    [Header("Runtime State")]
    public bool acquisitionReachable;
    public bool acquisitionRunning;
    public bool sessionStarted;
    public bool taskRunning;
    public string currentTaskId = "";

    private BioLabUdpClient udpClient;

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
            if (mockServer == null)
                mockServer = gameObject.AddComponent<AcquisitionMockServer>();

            mockServer.listenPort = config.acquisitionPort;
            mockServer.autoStart = false;
            mockServer.StartServer();
            return;
        }

        if (config.IsHost && config.useEmbeddedAcquisitionMock)
        {
            if (mockServer == null)
                mockServer = gameObject.AddComponent<AcquisitionMockServer>();

            mockServer.listenPort = config.acquisitionPort;
            mockServer.autoStart = false;
            mockServer.StartServer();

            config.acquisitionIp = "127.0.0.1";
        }

        udpClient = new BioLabUdpClient(config.acquisitionIp, config.acquisitionPort, config.responseTimeoutMs);
    }

    public void BeginExperimentalSession()
    {
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
        if (!config.IsHost)
        {
            LogLocal("SESSION_CONTROL", "SESSION_END_IGNORED_NON_HOST");
            return;
        }

        StartCoroutine(EndExperimentalSessionRoutine());
    }

    public void BeginTask(string taskId)
    {
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
        LogAndMaybeForward("TASK_START", taskId);
    }

    public void EndTask()
    {
        if (!taskRunning)
        {
            LogLocal("TASK_CONTROL", "TASK_END_REJECTED_NO_ACTIVE_TASK");
            return;
        }

        string taskId = currentTaskId;
        taskRunning = false;
        currentTaskId = "";
        LogAndMaybeForward("TASK_END", taskId);
    }

    private IEnumerator BeginExperimentalSessionRoutine()
    {
        LogLocal("SESSION_CONTROL", "SESSION_START_REQUEST");

        var pingTask = udpClient.PingAsync();
        yield return new WaitUntil(() => pingTask.IsCompleted);

        string pingResponse = pingTask.Result;
        acquisitionReachable = pingResponse == "PONG";
        LogLocal("SESSION_CONTROL", "PING_RESULT", pingResponse);

        if (!acquisitionReachable)
        {
            if (config.requireAcquisitionForSessionStart)
            {
                LogLocal("SESSION_CONTROL", "SESSION_ABORTED_NO_ACQUISITION");
                yield break;
            }

            sessionStarted = true;
            LogLocal("SESSION_START", "SESSION_START_NO_ACQ");
            yield break;
        }

        var startTask = udpClient.StartAcquisitionAsync();
        yield return new WaitUntil(() => startTask.IsCompleted);

        string startResponse = startTask.Result;
        acquisitionRunning = startResponse == "OK";
        LogLocal("SESSION_CONTROL", "START_RESULT", startResponse);

        if (!acquisitionRunning && config.requireAcquisitionForSessionStart)
            yield break;

        sessionStarted = true;
        yield return StartCoroutine(ForwardEventRoutine("SESSION_START", "SESSION_START"));
    }

    private IEnumerator EndExperimentalSessionRoutine()
    {
        if (!sessionStarted)
        {
            LogLocal("SESSION_CONTROL", "SESSION_END_IGNORED_NOT_STARTED");
            yield break;
        }

        if (taskRunning)
        {
            LogLocal("SESSION_CONTROL", "SESSION_END_WITH_ACTIVE_TASK", currentTaskId);
        }

        yield return StartCoroutine(ForwardEventRoutine("SESSION_END", "SESSION_END"));

        if (acquisitionReachable && acquisitionRunning)
        {
            var stopTask = udpClient.StopAcquisitionAsync();
            yield return new WaitUntil(() => stopTask.IsCompleted);

            string stopResponse = stopTask.Result;
            LogLocal("SESSION_CONTROL", "STOP_RESULT", stopResponse);
            acquisitionRunning = false;
        }

        sessionStarted = false;
        taskRunning = false;
        currentTaskId = "";
    }

    private void LogAndMaybeForward(string eventType, string eventValue)
    {
        LogLocal(eventType, eventValue);

        if (config.IsHost)
            StartCoroutine(ForwardEventRoutine(eventType, eventValue));
    }

    private IEnumerator ForwardEventRoutine(string eventType, string eventValue)
    {
        if (!acquisitionReachable)
            yield break;

        string encoded = $"{eventType}_{eventValue}";
        var task = udpClient.SendEventAsync(config.nodeId, encoded);
        yield return new WaitUntil(() => task.IsCompleted);

        LogLocal("FORWARD_RESULT", encoded, task.Result);
    }

    private void LogLocal(string eventType, string eventValue, string notes = "")
    {
        if (eventLogger != null)
            eventLogger.LogEvent(eventType, eventValue, notes);
    }
}