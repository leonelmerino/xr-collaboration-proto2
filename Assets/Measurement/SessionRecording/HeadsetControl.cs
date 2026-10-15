using System;
using System.IO;
using UnityEngine;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Canal de control del build nativo en el visor (Android, <c>HeadsetBuild</c>): el mismo protocolo de
    /// 127.0.0.1:47811 que <see cref="SessionRecorder"/> en el PC, sin video (el visor no graba pantalla).
    /// El panel (tools\lab-dashboard) llega por USB en el puerto fijo 127.0.0.1:47900 del PC
    /// (<c>adb forward tcp:47900 tcp:47811</c>, lo rehace el panel cada 5 s; Send-RecordingCommand.ps1 -Visor).
    /// «start»/«stop» siguen a la sesión de adquisición (BioLab) y quedan como marcas; «cue», «turn» y el
    /// estado de la app funcionan igual. «snapshot» no tiene imagen (usar adb screencap).
    /// Marcas: Application.persistentDataPath/control-marks.csv.
    /// </summary>
    public sealed class HeadsetControl : MonoBehaviour
    {
        private const string LogPrefix = "[HeadsetControl] ";
        public const int DefaultPort = 47811;

        private RecordingControlServer _server;
        private string _marksPath;
        private string _label = "";
        private DateTime? _startUtc;

#if UNITY_ANDROID && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            if (FindObjectOfType<HeadsetControl>() != null) return;
            var go = new GameObject("[HeadsetControl]");
            DontDestroyOnLoad(go);
            go.AddComponent<HeadsetControl>();
        }
#endif

        private void Awake()
        {
            _marksPath = Path.Combine(Application.persistentDataPath, "control-marks.csv");
            _server = RecordingControlServer.TryStart(DefaultPort);
            WriteMark(PreciseClock.UtcNow, "app abierta");
        }

        private void Update() => _server?.Pump(HandleCommand);

        private void OnDestroy()
        {
            _server?.Dispose();
            _server = null;
        }

        private string HandleCommand(string line)
        {
            RecordingCommand cmd = RecordingCommand.Parse(line, out string error);
            if (cmd == null) return RecordingControlServer.ErrorJson(error);
            DateTime now = PreciseClock.UtcNow;
            switch (cmd.Kind)
            {
                case RecordingCommandKind.Start:
                    if (_startUtc != null) return Reply(false, $"ya hay una sesión en curso ({_label}): mandar «stop» primero");
                    _label = cmd.Label ?? "";
                    _startUtc = cmd.AtUtc.HasValue && cmd.AtUtc.Value > now ? cmd.AtUtc.Value : now;
                    Session(true);
                    WriteMark(now, "start " + _label);
                    return Reply(true, "visor: sin video, solo marcas y BioLab");
                case RecordingCommandKind.Stop:
                    if (_startUtc == null) return Reply(false, "no hay sesión en curso");
                    _startUtc = null;
                    Session(false);
                    WriteMark(now, "stop " + _label);
                    return Reply(true, "");
                case RecordingCommandKind.Mark:
                    WriteMark(now, cmd.Text);
                    return Reply(true, "");
                case RecordingCommandKind.Cue:
                {
                    if (AppControlHooks.Cue == null) return Reply(false, "esta app no tiene señales 3D (FeedbackCues)");
                    string e = AppControlHooks.Cue(cmd.Text);
                    if (e == null) WriteMark(now, "señal " + RecordingCommand.Sanitize(cmd.Text.Replace('|', ' '), RecordingCommand.MaxMarkLength));
                    return Reply(e == null, e ?? "");
                }
                case RecordingCommandKind.Snapshot:
                    return Reply(false, "visor nativo: sin snapshot por este canal (adb exec-out screencap -p)");
                case RecordingCommandKind.Turn:
                {
                    if (AppControlHooks.Turn == null) return Reply(false, "esta app no tiene botón de turno");
                    string e = AppControlHooks.Turn(cmd.Text);
                    return Reply(e == null, e ?? "");
                }
                default:
                    return Reply(true, "");
            }
        }

        private void Session(bool start)
        {
            try { AppControlHooks.AcquisitionSession?.Invoke(start); }
            catch (Exception e) { Debug.LogWarning($"{LogPrefix}sesión de adquisición: {e.Message}"); }
        }

        private void WriteMark(DateTime utc, string text)
        {
            try { File.AppendAllText(_marksPath, $"{RecordingCommand.FormatUtc(utc)},\"{(text ?? "").Replace("\"", "'")}\"\n"); }
            catch (Exception e) { Debug.LogWarning($"{LogPrefix}marca: {e.Message}"); }
        }

        private string Reply(bool ok, string message)
        {
            DateTime now = PreciseClock.UtcNow;
            var s = new RecordingStatus
            {
                ok = ok,
                error = message ?? "",
                computer = SystemInfo.deviceModel,
                encoder = "none (visor)",
                appUtc = RecordingCommand.FormatUtc(now),
                audioApp = "off",
                audioMic = "off",
                state = _startUtc == null ? "idle" : now < _startUtc.Value ? "armed" : "recording",
                label = _label,
                dir = _startUtc == null ? "" : Application.persistentDataPath,
                startAtUtc = _startUtc == null ? "" : RecordingCommand.FormatUtc(_startUtc.Value),
                firstFrameUtc = _startUtc == null ? "" : RecordingCommand.FormatUtc(_startUtc.Value),
                elapsedSeconds = _startUtc == null || now < _startUtc.Value ? 0 : Math.Round((now - _startUtc.Value).TotalSeconds, 3),
            };
            string json = JsonUtility.ToJson(s);
            string health = null;
            try { health = AppControlHooks.Health?.Invoke(); }
            catch (Exception e) { Debug.LogWarning($"{LogPrefix}estado de la app: {e.Message}"); }
            return string.IsNullOrEmpty(health) ? json : json.Substring(0, json.Length - 1) + ",\"app\":" + health + "}";
        }
    }
}
