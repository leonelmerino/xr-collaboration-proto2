using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

public class ExperimentEventLogger : MonoBehaviour
{
    [Header("Session Info")]
    public string participantId = "P001";
    public string sessionId = "S001";
    public string taskId = "task_01";
    public string trialId = "trial_01";
    public string nodeId = "VR_HOST";

    [Header("Logging")]
    public bool autoStart = true;

    private StreamWriter writer;
    private bool isLogging;
    private int eventIndex = 0;
    private string filePath;

    private void Start()
    {
        if (autoStart)
            StartLogging();
    }

    public void StartLogging()
    {
        if (isLogging) return;

        string folder = Path.Combine(
            Application.persistentDataPath,
            "EyeTrackingLogs",
            participantId,
            sessionId
        );

        Directory.CreateDirectory(folder);

        string prefix = $"{taskId}_{trialId}_{nodeId}_";
        string[] existing = Directory.GetFiles(folder, $"{prefix}*_events.csv");

        int nextIndex = existing
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Select(name => name.Replace(prefix, "").Replace("_events", ""))
            .Select(text => int.TryParse(text, out int n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        filePath = Path.Combine(folder, $"{prefix}{nextIndex:000}_events.csv");
        writer = new StreamWriter(filePath, false, Encoding.UTF8);

        writer.WriteLine("event_index,timestamp_rel_s,timestamp_utc_iso,node_id,event_type,event_value,notes");
        isLogging = true;

        Debug.Log($"[ExperimentEventLogger] Logging to: {filePath}");
    }

    public void LogEvent(string eventType, string eventValue, string notes = "")
    {
        if (!isLogging) return;

        eventIndex++;
        string timestampRel = Time.realtimeSinceStartupAsDouble.ToString(CultureInfo.InvariantCulture);
        string timestampUtc = DateTime.UtcNow.ToString("o");

        writer.WriteLine(string.Join(",",
            eventIndex.ToString(CultureInfo.InvariantCulture),
            timestampRel,
            Csv(timestampUtc),
            Csv(nodeId),
            Csv(eventType),
            Csv(eventValue),
            Csv(notes)
        ));

        writer.Flush();
        Debug.Log($"[EVENT] {eventType} | {eventValue} | {notes}");
    }

    public void StopLogging()
    {
        if (!isLogging) return;

        writer.Flush();
        writer.Close();
        writer = null;
        isLogging = false;
    }

    private void OnApplicationQuit()
    {
        StopLogging();
    }

    private string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}