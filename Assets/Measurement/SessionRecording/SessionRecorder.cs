using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using XRCollab.Measurement.Mirroring;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Graba la ventana del PC tal como se ve (espejo del visor, HUDs, avisos en blanco y un reloj con milisegundos),
    /// como respaldo de la telemetría. La telemetría, los eventos y todo lo demás se registran siempre, grabe o no.
    ///
    /// Control: la app abre y queda <b>en espera</b>. Una herramienta de afuera manda «start» / «stop» por
    /// <see cref="RecordingControlServer"/> (TCP solo en 127.0.0.1; protocolo en <see cref="RecordingCommand"/>).
    /// «start at=UTC» programa el inicio a una hora exacta: así los 3 PCs empiezan juntos aunque la orden llegue
    /// con distinta demora. Con <c>-rec-auto</c> graba desde que abre (como antes). Cada «start» crea su carpeta
    /// (<see cref="RecordingLayout"/>).
    ///
    /// Captura: al final de cada cuadro (a <see cref="SessionRecorderSettings.fps"/>) copia la pantalla a una
    /// RenderTexture, escala en la GPU y lee asíncrono (AsyncGPUReadback): no frena el loop de VR. Cada grabación
    /// (<see cref="RecordingRun"/>) tiene su hilo, que le pasa los cuadros a ffmpeg (H.264 por GPU, segmentos de
    /// 60 s) y anota en frames.csv la hora UTC exacta de cada cuadro. Si la cola se llena se descartan cuadros:
    /// nunca se bloquea el hilo principal, tampoco al detener.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SessionRecorder : MonoBehaviour
    {
        private const string LogPrefix = "[SessionRecorder] ";
        private const int MaxReadbacksInFlight = 3;
        private const float StatsLogIntervalSeconds = 60f;
        private const float DiskCheckIntervalSeconds = 5f;
        private const double LateStartToleranceSeconds = 2.0;

        // Orientación: en Direct3D 11, CaptureScreenshotIntoRenderTexture + AsyncGPUReadback ya entregan la fila de
        // arriba primero, que es lo que espera ffmpeg. No se invierte nada (verificado el 2026-09-29 con un cuadro del
        // video: invirtiendo, salía cabeza abajo).

        [SerializeField] private SessionRecorderSettings settings = new SessionRecorderSettings();

        private readonly EncoderProbe _probe = new EncoderProbe();
        private RecordingControlServer _control;
        private string _root;
        private string _ffmpeg;
        private string _unavailable;          // null = se puede grabar
        private RecordingRun _run;            // armada o grabando (null = en espera)
        private RecordingRun _previous;       // la última detenida (puede seguir cerrando)
        private RenderTexture _screenRt;
        private RenderTexture _scaledRt;
        private int _inFlight;
        private float _nextCapture;
        private float _nextStatsLog;
        private float _nextDiskCheck;
        private long _bytesOnDisk;
        private int _segmentCount;
        private bool _quitting;
        private GUIStyle _overlayStyle;

        /// <summary>Carpeta de la grabación en curso (null si está en espera).</summary>
        public string SessionDirectory => _run?.Dir;

        public static SessionRecorder Create(Transform parent, SessionRecorderSettings settings)
        {
            var go = new GameObject("[SessionRecorder]");
            go.transform.SetParent(parent, false);
            go.SetActive(false);   // configurar antes de que corra Awake/Start
            var recorder = go.AddComponent<SessionRecorder>();
            recorder.settings = settings.Clone();
            go.SetActive(true);
            return recorder;
        }

        private void Start()
        {
            settings.Validate();
            _root = string.IsNullOrEmpty(settings.outputRoot) ? Path.Combine(Application.persistentDataPath, "Recordings") : settings.outputRoot;
            Directory.CreateDirectory(_root);

            _ffmpeg = ExternalTools.FindFfmpeg(settings.ffmpegPath);
            if (_ffmpeg == null)
            {
                _unavailable = "no está ffmpeg (winget install Gyan.FFmpeg)";
                Debug.LogWarning($"{LogPrefix}sin grabación: {_unavailable}");
                _probe.Error = _unavailable;
                _probe.Ready.Set();
            }
            else
            {
                // Una sola vez y en segundo plano: arma los videos de sesiones cortadas y elige el encoder.
                new Thread(ProbeLoop) { IsBackground = true, Name = "SessionRecorder probe" }.Start();
            }

            if (settings.controlPort > 0) _control = RecordingControlServer.TryStart(settings.controlPort);
            _nextStatsLog = Time.unscaledTime + StatsLogIntervalSeconds;
            StartCoroutine(CaptureLoop());

            if (settings.autoStart && _unavailable == null)
            {
                DateTime now = PreciseClock.UtcNow;
                BeginRun("", now, now);
            }
            else
            {
                Debug.Log($"{LogPrefix}en espera de «start» en 127.0.0.1:{settings.controlPort} (tools\\lab-remote\\Lab-Recording.ps1)");
            }
        }

        private void ProbeLoop()
        {
            try
            {
                foreach (string dir in RecordingLayout.PendingSessions(_root, null))
                {
                    try
                    {
                        if (LaunchFinalize(dir, _ffmpeg)) Debug.Log($"{LogPrefix}armando el video de una grabación anterior sin cerrar: {Path.GetFileName(dir)}");
                    }
                    catch (Exception e) { Debug.LogWarning($"{LogPrefix}no se pudo armar {dir}: {e.Message}"); }
                }

                foreach (RecordingEncoder candidate in RecordingFfmpeg.Candidates(settings.encoder))
                {
                    ProcessResult probe = ChildProcess.Run(_ffmpeg, RecordingFfmpeg.ProbeArguments(candidate), 15_000);
                    if (probe.Succeeded)
                    {
                        _probe.Encoder = candidate;
                        _probe.Codec = RecordingFfmpeg.CodecName(candidate);
                        _probe.Ok = true;
                        Debug.Log($"{LogPrefix}encoder: {_probe.Codec}");
                        return;
                    }
                    Debug.Log($"{LogPrefix}{RecordingFfmpeg.CodecName(candidate)} no disponible: {probe.Summary}");
                }
                _probe.Error = $"ningún encoder funcionó ({RecordingFfmpeg.Describe(RecordingFfmpeg.Candidates(settings.encoder))})";
                Debug.LogWarning($"{LogPrefix}sin grabación: {_probe.Error}");
            }
            catch (Exception e)
            {
                _probe.Error = e.Message;
            }
            finally
            {
                _probe.Ready.Set();
            }
        }

        private void Update()
        {
            _control?.Pump(HandleCommand);

            float now = Time.unscaledTime;
            if (now >= _nextDiskCheck)
            {
                _nextDiskCheck = now + DiskCheckIntervalSeconds;
                if (_run != null) _bytesOnDisk = _run.BytesOnDisk(out _segmentCount);
            }
            if (now >= _nextStatsLog)
            {
                _nextStatsLog = now + StatsLogIntervalSeconds;
                if (_run != null)
                    Debug.Log($"{LogPrefix}{_run.Status} · {_probe.Codec} · {_run.Written} cuadros escritos, {_run.Dropped} descartados · {_segmentCount} segmentos, {_bytesOnDisk / (1024 * 1024)} MB");
            }
        }

        // ---------------- órdenes de control (hilo principal) ----------------

        private string HandleCommand(string line)
        {
            RecordingCommand cmd = RecordingCommand.Parse(line, out string error);
            if (cmd == null) return RecordingControlServer.ErrorJson(error);
            DateTime now = PreciseClock.UtcNow;
            switch (cmd.Kind)
            {
                case RecordingCommandKind.Start:
                {
                    if (_unavailable != null) return Reply(false, _unavailable);
                    if (_probe.Ready.IsSet && !_probe.Ok) return Reply(false, _probe.Error);
                    if (_run != null) return Reply(false, $"ya hay una grabación {(IsArmed(now) ? "armada" : "en curso")} ({_run.Label}): mandar «stop» primero");
                    DateTime at = cmd.AtUtc ?? now;
                    string note = "";
                    if (at < now)
                    {
                        if ((now - at).TotalSeconds > LateStartToleranceSeconds) note = $"la hora pedida ya pasó hace {(now - at).TotalSeconds:F1} s: empieza ahora";
                        at = now;
                    }
                    BeginRun(cmd.Label, now, at);
                    return Reply(true, note);
                }
                case RecordingCommandKind.Stop:
                {
                    if (_run == null) return Reply(false, "no hay grabación en curso");
                    if (cmd.AtUtc.HasValue && cmd.AtUtc.Value > now)
                    {
                        _run.StopAtUtc = cmd.AtUtc.Value;
                        return Reply(true, "");
                    }
                    StopRun(now);
                    return Reply(true, "");
                }
                case RecordingCommandKind.Mark:
                    WriteMark(now, cmd.Text);
                    return Reply(true, "");
                default:
                    return Reply(true, "");
            }
        }

        private void BeginRun(string label, DateTime requestedUtc, DateTime startAtUtc)
        {
            (int w, int h) = RecordingFfmpeg.OutputSize(Screen.width, Screen.height, settings.width);
            _run = new RecordingRun(settings, _ffmpeg, _probe, _root, label, w, h, requestedUtc, startAtUtc);
            _bytesOnDisk = 0;
            _segmentCount = 0;
            Debug.Log($"{LogPrefix}grabación {(string.IsNullOrEmpty(label) ? "" : label + " ")}programada para {RecordingCommand.FormatUtc(startAtUtc)} → {_run.Dir} ({w}x{h} @ {settings.fps} fps, segmentos de {settings.segmentSeconds} s)");
        }

        private void StopRun(DateTime now)
        {
            if (_run == null) return;
            _run.RequestStop(now);
            _previous = _run;
            _run = null;
            Debug.Log($"{LogPrefix}«stop» a las {RecordingCommand.FormatUtc(now)}: cerrando {Path.GetFileName(_previous.Dir)}");
        }

        private bool IsArmed(DateTime now) => _run != null && now < _run.StartAtUtc;

        private string Reply(bool ok, string message)
        {
            DateTime now = PreciseClock.UtcNow;
            var s = new RecordingStatus
            {
                ok = ok,
                error = message ?? "",
                computer = Environment.MachineName,
                encoder = _probe.Codec,
                appUtc = RecordingCommand.FormatUtc(now),
                lastVideo = _previous == null ? "" : _previous.FinalVideo,
            };
            if (_unavailable != null || (_probe.Ready.IsSet && !_probe.Ok))
                s.state = "disabled";
            else if (_run == null)
                s.state = "idle";
            else
            {
                s.state = _run.Failed ? "failed" : IsArmed(now) ? "armed" : "recording";
                s.label = _run.Label;
                s.dir = _run.Dir;
                s.requestedUtc = RecordingCommand.FormatUtc(_run.RequestedUtc);
                s.startAtUtc = RecordingCommand.FormatUtc(_run.StartAtUtc);
                DateTime? first = _run.FirstFrameUtc;
                s.firstFrameUtc = first.HasValue ? RecordingCommand.FormatUtc(first.Value) : "";
                s.stopAtUtc = _run.StopAtUtc.HasValue ? RecordingCommand.FormatUtc(_run.StopAtUtc.Value) : "";
                s.elapsedSeconds = first.HasValue ? Math.Round((now - first.Value).TotalSeconds, 3) : 0;
                s.framesWritten = _run.Written;
                s.framesDropped = _run.Dropped;
                if (_run.Failed && s.error.Length == 0) s.error = _run.Status;
            }
            return JsonUtility.ToJson(s);
        }

        private void WriteMark(DateTime utc, string text)
        {
            string path = _run != null ? Path.Combine(_run.Dir, RecordingLayout.MarksFile) : Path.Combine(_root, RecordingLayout.MarksFile);
            try
            {
                bool header = !File.Exists(path);
                using (var w = new StreamWriter(path, true, new UTF8Encoding(false)) { NewLine = "\n" })
                {
                    if (header) w.WriteLine(RecordingLayout.MarksHeader);
                    w.WriteLine(RecordingLayout.MarksRow(utc, Time.frameCount, Environment.MachineName, _run?.Label ?? "", text));
                }
                Debug.Log($"{LogPrefix}marca {RecordingCommand.FormatUtc(utc)}: {text}");
            }
            catch (Exception e) { Debug.LogWarning($"{LogPrefix}no se pudo escribir la marca: {e.Message}"); }
        }

        // ---------------- captura (hilo principal) ----------------

        private IEnumerator CaptureLoop()
        {
            var endOfFrame = new WaitForEndOfFrame();
            float interval = 1f / settings.fps;
            RecordingRun capturing = null;
            while (!_quitting)
            {
                yield return endOfFrame;   // después de OnGUI: el cuadro ya tiene los HUD y el reloj
                RecordingRun run = _run;
                if (run == null || run.Stopping) continue;
                DateTime utc = PreciseClock.UtcNow;
                if (run.StopAtUtc.HasValue && utc >= run.StopAtUtc.Value)
                {
                    StopRun(utc);
                    continue;
                }
                if (utc < run.StartAtUtc) continue;   // armada: todavía no es la hora
                float now = Time.unscaledTime;
                if (!ReferenceEquals(run, capturing))
                {
                    capturing = run;
                    _nextCapture = now;   // primer cuadro apenas llega la hora
                }
                if (now < _nextCapture) continue;
                _nextCapture = _nextCapture + interval < now ? now + interval : _nextCapture + interval;   // sin ráfagas
                if (run.Failed) continue;
                if (_inFlight >= MaxReadbacksInFlight)
                {
                    run.CountDropped();
                    continue;
                }
                EnsureTargets(run.Width, run.Height);
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_screenRt);
                Graphics.Blit(_screenRt, _scaledRt);   // escalado en la GPU
                _inFlight++;
                int unityFrame = Time.frameCount;
                double unityTime = Time.realtimeSinceStartupAsDouble;
                AsyncGPUReadback.Request(_scaledRt, 0, TextureFormat.RGBA32, r => OnReadback(r, run, utc, unityFrame, unityTime));
            }
        }

        private void OnReadback(AsyncGPUReadbackRequest request, RecordingRun run, DateTime captureUtc, int unityFrame, double unityTime)
        {
            _inFlight--;
            if (request.hasError || run.Stopping) return;
            NativeArray<byte> data = request.GetData<byte>();
            byte[] buffer = run.RentBuffer(data.Length);
            data.CopyTo(buffer);
            run.Enqueue(buffer, captureUtc, unityFrame, unityTime);
        }

        private void EnsureTargets(int outWidth, int outHeight)
        {
            if (_screenRt == null || _screenRt.width != Screen.width || _screenRt.height != Screen.height)
            {
                ReleaseTexture(ref _screenRt);
                _screenRt = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32) { name = "SessionRecorder screen" };
            }
            // Tamaño fijo durante una grabación: ffmpeg recibe siempre el mismo; si la ventana cambia, se estira.
            if (_scaledRt == null || _scaledRt.width != outWidth || _scaledRt.height != outHeight)
            {
                ReleaseTexture(ref _scaledRt);
                _scaledRt = new RenderTexture(outWidth, outHeight, 0, RenderTextureFormat.ARGB32) { name = "SessionRecorder output" };
            }
        }

        // ---------------- reloj en pantalla (también queda en el video) ----------------

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    richText = true,
                };
                _overlayStyle.normal.textColor = Color.white;
            }
            DateTime utc = PreciseClock.UtcNow;
            DateTime local = utc.ToLocalTime();
            string text = $"{StateText(utc)}   |   {Environment.MachineName} · {utc:yyyy-MM-dd HH:mm:ss.fff} UTC · local {local:HH:mm:ss.fff} " +
                          $"({FormatOffset(TimeZoneInfo.Local.GetUtcOffset(utc))}) · cuadro {Time.frameCount}";
            const float width = 1240f, height = 26f;
            var rect = new Rect((Screen.width - width) / 2f, 4f, width, height);
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(rect, text, _overlayStyle);
        }

        private string StateText(DateTime utc)
        {
            if (_unavailable != null) return $"<color=#ffb040>VIDEO no disponible: {_unavailable}</color>";
            if (_probe.Ready.IsSet && !_probe.Ok) return $"<color=#ffb040>VIDEO no disponible: {_probe.Error}</color>";
            RecordingRun run = _run;
            if (run == null)
            {
                // Sin «REC» a la vista: en espera no se graba video (la telemetría sí se registra igual).
                bool closing = _previous != null && !_previous.Finished;
                return closing ? "<color=#c0c0c0>VIDEO detenido · terminando el archivo anterior</color>" : "<color=#c0c0c0>VIDEO sin grabar</color>";
            }
            string label = string.IsNullOrEmpty(run.Label) ? "" : " · " + run.Label;
            if (run.Failed) return $"<color=#ffb040>VIDEO detenido por error: {run.Status}</color>{label}";
            if (utc < run.StartAtUtc) return $"<color=#ffd040>VIDEO empieza en {(run.StartAtUtc - utc).TotalSeconds:F1} s</color>{label}";
            DateTime? first = run.FirstFrameUtc;
            double elapsed = first.HasValue ? (utc - first.Value).TotalSeconds : 0;
            return $"<color=#ff5050>REC</color> {FormatElapsed(elapsed)} · {_bytesOnDisk / (1024 * 1024)} MB · {_probe.Codec}{label}";
        }

        // ---------------- cierre ----------------

        private void OnApplicationQuit() => Shutdown();

        private void OnDestroy() => Shutdown();

        private void Shutdown()
        {
            if (_quitting) return;
            _quitting = true;
            StopAllCoroutines();
            _control?.Dispose();
            _control = null;
            // Al cerrar la app sí se espera (un rato): el último segmento queda bien cerrado y el .mp4 se arma afuera.
            if (_run != null) StopRun(PreciseClock.UtcNow);
            _previous?.WaitFinished(10_000);
            ReleaseTexture(ref _screenRt);
            ReleaseTexture(ref _scaledRt);
        }

        /// <summary>Une los segmentos en el .mp4 final, en un proceso aparte que sigue aunque la app se cierre.</summary>
        internal static bool LaunchFinalize(string sessionDir, string ffmpeg)
        {
            IReadOnlyList<string> segments = RecordingLayout.Segments(sessionDir);
            if (segments.Count == 0) return false;
            string partial = RecordingLayout.PartialVideoPath(sessionDir);
            // Otro armado de la misma sesión en curso (se cerró y abrió la app enseguida): no pisarlo.
            if (File.Exists(partial) && (DateTime.Now - File.GetLastWriteTime(partial)).TotalMinutes < 2) return false;
            string list = Path.Combine(sessionDir, RecordingLayout.ConcatListFile);
            File.WriteAllText(list, RecordingFfmpeg.ConcatList(segments), new UTF8Encoding(false));
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            ChildProcess.StartDetached(cmd, RecordingFfmpeg.FinalizeCommandLine(ffmpeg, list, partial, RecordingLayout.FinalVideoPath(sessionDir)));
            return true;
        }

        // ---------------- utilidades ----------------

        private static void ReleaseTexture(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Destroy(rt);
            rt = null;
        }

        private static string FormatElapsed(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        public static string FormatOffset(TimeSpan offset) =>
            (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm");
    }
}
