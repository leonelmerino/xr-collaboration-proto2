using System;
using System.Collections;
using System.Collections.Concurrent;
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
    /// como respaldo de la telemetría. Diseño:
    ///   1. Al final de cada cuadro (a <see cref="SessionRecorderSettings.fps"/>): copia de la pantalla a una
    ///      RenderTexture, escalado en la GPU y lectura asíncrona (AsyncGPUReadback): no frena el loop de VR.
    ///   2. Un hilo le pasa los cuadros RGBA a ffmpeg por stdin; ffmpeg codifica H.264 por hardware (NVENC, si no
    ///      QuickSync, si no x264) en segmentos MPEG-TS de 60 s. Un corte (visor desconectado, app cerrada de golpe)
    ///      pierde como mucho unos segundos: los segmentos anteriores quedan intactos.
    ///   3. Al cerrar normal se arma el .mp4 final uniendo los segmentos sin recodificar, en un proceso aparte que
    ///      sigue aunque la app ya se cerró. Si la app murió de golpe, se arma al próximo arranque.
    /// Si la cola se llena (disco o encoder lentos) se descartan cuadros: nunca se bloquea el hilo principal.
    /// Carpetas: <see cref="RecordingLayout"/>. No toca los loggers de telemetría.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SessionRecorder : MonoBehaviour
    {
        private const string LogPrefix = "[SessionRecorder] ";
        private const int MaxReadbacksInFlight = 3;
        private const int QueueCapacity = 8;          // ~9 MB por cuadro a 1920x1200
        private const int MaxEncoderRestarts = 5;
        private const float StatsLogIntervalSeconds = 60f;
        private const float DiskCheckIntervalSeconds = 5f;

        // Orientación: en Direct3D 11, CaptureScreenshotIntoRenderTexture + AsyncGPUReadback ya entregan la fila de
        // arriba primero, que es lo que espera ffmpeg. No se invierte nada (verificado el 2026-09-29 con un cuadro del
        // video: invirtiendo, salía cabeza abajo).

        [SerializeField] private SessionRecorderSettings settings = new SessionRecorderSettings();

        private readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        private BlockingCollection<byte[]> _queue;
        private Thread _worker;
        private volatile ChildProcess _encoder;
        private volatile bool _stopping;
        private volatile bool _failed;
        private volatile bool _infoDirty;
        private volatile string _status = "iniciando";
        private volatile string _codec = "…";
        private RecordingEncoder _chosen = RecordingEncoder.Auto;

        private string _root;
        private string _sessionDir;
        private string _ffmpeg;
        private RecordingSessionInfo _info;
        private RenderTexture _screenRt;
        private RenderTexture _scaledRt;
        private int _outWidth;
        private int _outHeight;
        private int _inFlight;
        private long _captured;
        private long _written;
        private long _dropped;
        private float _startTime;
        private float _nextCapture;
        private float _nextStatsLog;
        private float _nextDiskCheck;
        private long _bytesOnDisk;
        private int _segmentCount;
        private bool _finished;
        private GUIStyle _overlayStyle;

        /// <summary>Carpeta de esta grabación (null si no arrancó).</summary>
        public string SessionDirectory => _sessionDir;

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
            DateTime localStart = DateTime.Now;
            _sessionDir = Path.Combine(_root, RecordingLayout.SessionName(localStart, Environment.MachineName));
            Directory.CreateDirectory(Path.Combine(_sessionDir, RecordingLayout.SegmentsFolder));
            (_outWidth, _outHeight) = RecordingFfmpeg.OutputSize(Screen.width, Screen.height, settings.width);

            _info = new RecordingSessionInfo
            {
                computer = Environment.MachineName,
                startedUtc = localStart.ToUniversalTime().ToString("o"),
                startedLocal = localStart.ToString("o"),
                utcOffset = FormatOffset(TimeZoneInfo.Local.GetUtcOffset(localStart)),
                width = _outWidth,
                height = _outHeight,
                fps = settings.fps,
                segmentSeconds = settings.segmentSeconds,
                encoder = "(eligiendo)",
                note = "closedCleanly=false: la app no se cerró normal (el video final se arma al próximo arranque)",
            };
            WriteInfo();

            _ffmpeg = ExternalTools.FindFfmpeg(settings.ffmpegPath);
            if (_ffmpeg == null)
            {
                Fail("no está ffmpeg (winget install Gyan.FFmpeg)");
                return;
            }

            _queue = new BlockingCollection<byte[]>(QueueCapacity);
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "SessionRecorder encoder" };
            _worker.Start();

            _startTime = _nextCapture = Time.unscaledTime;
            _nextStatsLog = _startTime + StatsLogIntervalSeconds;
            StartCoroutine(CaptureLoop());
            Debug.Log($"{LogPrefix}grabando la ventana en {_sessionDir} ({_outWidth}x{_outHeight} @ {settings.fps} fps, segmentos de {settings.segmentSeconds} s)");
        }

        private void Update()
        {
            if (_infoDirty)
            {
                _infoDirty = false;
                _info.encoder = _codec;
                WriteInfo();
            }
            float now = Time.unscaledTime;
            if (now >= _nextDiskCheck && _sessionDir != null)
            {
                _nextDiskCheck = now + DiskCheckIntervalSeconds;
                long bytes = 0;
                IReadOnlyList<string> segments = RecordingLayout.Segments(_sessionDir);
                foreach (string s in segments) bytes += new FileInfo(s).Length;
                _bytesOnDisk = bytes;
                _segmentCount = segments.Count;
            }
            if (now >= _nextStatsLog && !_finished)
            {
                _nextStatsLog = now + StatsLogIntervalSeconds;
                Debug.Log($"{LogPrefix}{_status} · {_codec} · {Interlocked.Read(ref _written)} cuadros escritos, {Interlocked.Read(ref _dropped)} descartados · {_segmentCount} segmentos, {_bytesOnDisk / (1024 * 1024)} MB");
            }
        }

        // ---------------- captura (hilo principal) ----------------

        private IEnumerator CaptureLoop()
        {
            var endOfFrame = new WaitForEndOfFrame();
            float interval = 1f / settings.fps;
            while (!_stopping)
            {
                yield return endOfFrame;   // después de OnGUI: el cuadro ya tiene los HUD y el reloj
                float now = Time.unscaledTime;
                if (now < _nextCapture) continue;
                _nextCapture = _nextCapture + interval < now ? now + interval : _nextCapture + interval;   // sin ráfagas
                if (_failed) continue;
                if (_inFlight >= MaxReadbacksInFlight)
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }
                EnsureTargets();
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_screenRt);
                Graphics.Blit(_screenRt, _scaledRt);   // escalado en la GPU
                _inFlight++;
                AsyncGPUReadback.Request(_scaledRt, 0, TextureFormat.RGBA32, OnReadback);
            }
        }

        private void OnReadback(AsyncGPUReadbackRequest request)
        {
            _inFlight--;
            if (_stopping || request.hasError || _queue == null || _queue.IsAddingCompleted) return;
            NativeArray<byte> data = request.GetData<byte>();
            if (!_pool.TryTake(out byte[] buffer) || buffer.Length != data.Length) buffer = new byte[data.Length];
            data.CopyTo(buffer);
            if (!_queue.TryAdd(buffer))
            {
                Interlocked.Increment(ref _dropped);
                _pool.Add(buffer);
                return;
            }
            Interlocked.Increment(ref _captured);
        }

        private void EnsureTargets()
        {
            if (_screenRt == null || _screenRt.width != Screen.width || _screenRt.height != Screen.height)
            {
                ReleaseTexture(ref _screenRt);
                _screenRt = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32) { name = "SessionRecorder screen" };
            }
            if (_scaledRt == null)   // tamaño fijo: ffmpeg recibe siempre el mismo; si la ventana cambia, se estira
                _scaledRt = new RenderTexture(_outWidth, _outHeight, 0, RenderTextureFormat.ARGB32) { name = "SessionRecorder output" };
        }

        // ---------------- reloj en pantalla (también queda en el video) ----------------

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || _sessionDir == null) return;
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
            DateTime utc = DateTime.UtcNow;
            DateTime local = utc.ToLocalTime();
            string rec = _failed
                ? $"<color=#ffb040>REC detenido: {_status}</color>"
                : $"<color=#ff5050>REC</color> {FormatElapsed(Time.unscaledTime - _startTime)} · {_bytesOnDisk / (1024 * 1024)} MB · {_codec}";
            string text = $"{rec}   |   {Environment.MachineName} · {utc:yyyy-MM-dd HH:mm:ss.fff} UTC · local {local:HH:mm:ss.fff} " +
                          $"({FormatOffset(TimeZoneInfo.Local.GetUtcOffset(utc))}) · cuadro {Time.frameCount}";
            const float width = 1180f, height = 26f;
            var rect = new Rect((Screen.width - width) / 2f, 4f, width, height);
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(rect, text, _overlayStyle);
        }

        // ---------------- encoder (hilo propio) ----------------

        private void WorkerLoop()
        {
            try
            {
                FinalizePendingSessions(_root, _sessionDir, _ffmpeg);
                if (!ChooseEncoder()) return;
                int restarts = 0;
                while (!_stopping)
                {
                    StartEncoder();
                    _status = "grabando";
                    if (!PumpFrames()) break;   // false = se pidió parar
                    if (++restarts > MaxEncoderRestarts)
                    {
                        Fail("ffmpeg se cortó demasiadas veces (ver Player.log)");
                        return;
                    }
                    _status = "ffmpeg se cortó; reiniciando";
                    Debug.LogWarning($"{LogPrefix}ffmpeg se cortó; reinicio {restarts}/{MaxEncoderRestarts} (sigue la numeración de segmentos)");
                    Thread.Sleep(1000);
                }
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        private bool ChooseEncoder()
        {
            foreach (RecordingEncoder candidate in RecordingFfmpeg.Candidates(settings.encoder))
            {
                if (_stopping) return false;
                ProcessResult probe = ChildProcess.Run(_ffmpeg, RecordingFfmpeg.ProbeArguments(candidate), 15_000);
                if (probe.Succeeded)
                {
                    _chosen = candidate;
                    _codec = RecordingFfmpeg.CodecName(candidate);
                    _infoDirty = true;
                    Debug.Log($"{LogPrefix}encoder: {_codec}");
                    return true;
                }
                Debug.Log($"{LogPrefix}{RecordingFfmpeg.CodecName(candidate)} no disponible: {probe.Summary}");
            }
            Fail($"ningún encoder funcionó ({RecordingFfmpeg.Describe(RecordingFfmpeg.Candidates(settings.encoder))})");
            return false;
        }

        private void StartEncoder()
        {
            int start = RecordingLayout.NextSegmentNumber(_sessionDir);
            string args = RecordingFfmpeg.RecordArguments(_chosen, _outWidth, _outHeight, settings.fps, settings.segmentSeconds,
                RecordingLayout.SegmentPattern(_sessionDir), start);
            int logged = 0;
            _encoder = ChildProcess.Start(_ffmpeg, args, null,
                line => { if (Interlocked.Increment(ref logged) <= 20) Debug.Log($"{LogPrefix}ffmpeg: {line}"); },
                redirectInput: true);
        }

        /// <returns>true si el pipe se rompió (hay que reiniciar ffmpeg); false si se pidió parar.</returns>
        private bool PumpFrames()
        {
            Stream input = _encoder.Input;
            foreach (byte[] frame in _queue.GetConsumingEnumerable())   // termina con CompleteAdding, tras vaciar la cola
            {
                try
                {
                    input.Write(frame, 0, frame.Length);
                    Interlocked.Increment(ref _written);
                }
                catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    _pool.Add(frame);
                    _encoder?.Kill();
                    return !_stopping;
                }
                _pool.Add(frame);
            }
            return false;
        }

        // ---------------- cierre y video final ----------------

        private void OnApplicationQuit() => Finish();

        private void OnDestroy() => Finish();

        private void Finish()
        {
            if (_finished || _sessionDir == null) return;
            _finished = true;
            _stopping = true;
            StopAllCoroutines();
            _queue?.CompleteAdding();
            _worker?.Join(3000);   // termina de escribir lo que había en cola

            ChildProcess encoder = _encoder;
            if (encoder != null)
            {
                try { encoder.Input.Close(); }   // EOF: ffmpeg cierra bien el último segmento
                catch (Exception) { }
                if (!encoder.WaitForExit(5000)) encoder.Kill();
                encoder.Dispose();
            }

            _info.encoder = _codec;
            _info.stoppedUtc = DateTime.UtcNow.ToString("o");
            _info.closedCleanly = true;
            _info.framesCaptured = Interlocked.Read(ref _captured);
            _info.framesDropped = Interlocked.Read(ref _dropped);
            _info.note = _failed ? "grabación con error: " + _status : "";
            WriteInfo();

            if (_ffmpeg != null)
            {
                try { LaunchFinalize(_sessionDir, _ffmpeg); }
                catch (Exception e) { Debug.LogWarning($"{LogPrefix}no se pudo lanzar el armado del video final: {e.Message}"); }
            }
            ReleaseTexture(ref _screenRt);
            ReleaseTexture(ref _scaledRt);
            Debug.Log($"{LogPrefix}detenido: {_info.framesCaptured} cuadros, {_info.framesDropped} descartados. Video final: {RecordingLayout.FinalVideoPath(_sessionDir)}");
        }

        private static void FinalizePendingSessions(string root, string currentDir, string ffmpeg)
        {
            foreach (string dir in RecordingLayout.PendingSessions(root, currentDir))
            {
                try
                {
                    if (LaunchFinalize(dir, ffmpeg)) Debug.Log($"{LogPrefix}armando el video de una sesión anterior sin cerrar: {Path.GetFileName(dir)}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"{LogPrefix}no se pudo armar {dir}: {e.Message}");
                }
            }
        }

        /// <summary>Une los segmentos en el .mp4 final, en un proceso aparte que sigue aunque la app se cierre.</summary>
        private static bool LaunchFinalize(string sessionDir, string ffmpeg)
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

        private void Fail(string reason)
        {
            _failed = true;
            _status = reason;
            Debug.LogWarning($"{LogPrefix}sin grabación: {reason}");
        }

        private void WriteInfo()
        {
            try { File.WriteAllText(Path.Combine(_sessionDir, RecordingLayout.InfoFile), JsonUtility.ToJson(_info, true)); }
            catch (Exception e) { Debug.LogWarning($"{LogPrefix}no se pudo escribir session.json: {e.Message}"); }
        }

        private static void ReleaseTexture(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Destroy(rt);
            rt = null;
        }

        private static string FormatElapsed(float seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        public static string FormatOffset(TimeSpan offset) =>
            (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm");
    }
}
