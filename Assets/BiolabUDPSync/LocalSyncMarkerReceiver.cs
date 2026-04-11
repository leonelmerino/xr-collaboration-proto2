using UnityEngine;

public class LocalSyncMarkerReceiver : MonoBehaviour
{
    public ExperimentEventLogger eventLogger;
    public string nodeId = "VR_NODE";

    private void Awake()
    {
        if (eventLogger == null)
            eventLogger = FindObjectOfType<ExperimentEventLogger>();
    }

    public void ReceiveSyncMarker(string marker)
    {
        Debug.Log($"[LocalSyncMarkerReceiver:{nodeId}] Received marker: {marker}");

        if (eventLogger != null)
            eventLogger.LogEvent("SYNC", marker);
    }

    public void ReceiveInteractionMarker(string eventValue)
    {
        if (eventLogger != null)
            eventLogger.LogEvent("INTERACTION", eventValue);
    }
}