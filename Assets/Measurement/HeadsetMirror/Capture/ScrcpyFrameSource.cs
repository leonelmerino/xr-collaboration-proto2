using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>
    /// Cuadros de la pantalla del visor vía adb, con scrcpy-server en el visor y ffmpeg en el PC:
    /// <code>
    ///   visor: scrcpy-server (encoder H.264 por hardware, raw_stream)
    ///     └─ socket abstracto ──adb forward (USB)──► tcp://127.0.0.1:PUERTO
    ///                                                  └─ ffmpeg: H.264 → RGBA WxH, stdout
    ///                                                       └─ este hilo → FrameTripleBuffer → hilo principal
    /// </code>
    /// Cada sesión se arma y desarma completa (push, forward, servidor, ffmpeg). Si algo falla o la imagen
    /// se detiene más de <see cref="HeadsetMirrorSettings.stallTimeoutSeconds"/>, se desarma y se reintenta
    /// con espera creciente. La imagen es la pantalla física del visor: sala de las cámaras a color más la capa
    /// de la app, tal como la ve el participante. No pasa por VIVE Streaming ni toca los loggers.
    /// </summary>
    public sealed class ScrcpyFrameSource : IHeadsetFrameSource
    {
        private const int ServerReadyTimeoutMs = 5_000;
        // El servidor escribe ReadyLogMarker y enseguida abre el socket; este margen evita que ffmpeg llegue antes.
        private const int ServerSocketGraceMs = 300;
        private const int ProcessLogLinesPerSession = 40;
        private const int StopJoinTimeoutMs = 3_000;

        private readonly HeadsetMirrorSettings _settings;
        private readonly string _serverAssetPath;
        private readonly Action<string> _log;
        private readonly Random _random = new Random();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _sessionGate = new object();

        private Thread _thread;
        private Timer _watchdog;
        private volatile bool _stopping;
        private volatile MirrorStatus _status = MirrorStatus.Stopped;
        private volatile FrameTripleBuffer _frames;
        private long _framesReceived;
        private long _lastProgressTicks;
        private bool _warnedMultipleDevices;
        private readonly HashSet<string> _cleanedSerials = new HashSet<string>();

        // Recursos de la sesión en curso; se liberan en EndSession (también desde Stop, en otro hilo).
        private AdbClient _sessionAdb;
        private string _sessionSerial;
        private int _sessionPort;
        private ChildProcess _server;
        private ChildProcess _decoder;

        /// <param name="settings">Se copia: cambios posteriores no afectan a la fuente.</param>
        /// <param name="serverAssetPath">Ruta absoluta de scrcpy-server (StreamingAssets, resuelta en el hilo principal).</param>
        /// <param name="log">Recibe mensajes de diagnóstico desde cualquier hilo.</param>
        public ScrcpyFrameSource(HeadsetMirrorSettings settings, string serverAssetPath, Action<string> log)
        {
            _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Clone();
            _settings.Validate();
            _serverAssetPath = serverAssetPath ?? throw new ArgumentNullException(nameof(serverAssetPath));
            _log = log ?? (_ => { });
        }

        public MirrorStatus Status => _status;
        public FrameTripleBuffer Frames => _frames;
        public long FramesReceived => Interlocked.Read(ref _framesReceived);

        public void Start()
        {
            if (_thread != null) return;
            _stopping = false;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "HeadsetMirror capture" };
            _thread.Start();
            _watchdog = new Timer(_ => CheckForStall(), null, 1000, 1000);
        }

        public void Stop()
        {
            if (_thread == null) return;
            _stopping = true;
            _wake.Set();
            _watchdog?.Dispose();
            _watchdog = null;
            KillSessionProcesses();   // desbloquea la lectura de stdout de ffmpeg
            if (!_thread.Join(StopJoinTimeoutMs)) _log("el hilo de captura no terminó a tiempo");
            _thread = null;
            _frames = null;
            _status = MirrorStatus.Stopped;
        }

        public void Dispose()
        {
            Stop();
            _wake.Dispose();
        }

        // ---------------- hilo de captura ----------------

        private void RunLoop()
        {
            float retryDelay = _settings.retryMinSeconds;
            while (!_stopping)
            {
                bool streamed = false;
                try
                {
                    streamed = RunSession();
                }
                catch (MirrorSetupException e)
                {
                    SetStatus(e.State, e.Message);
                }
                catch (Exception e) when (!_stopping)
                {
                    _log($"falló la sesión: {e.Message}");
                    SetStatus(MirrorState.Reconnecting, e.Message);
                }
                catch (Exception)
                {
                    // Stop() mató los procesos a mitad de camino: esperable.
                }
                finally
                {
                    EndSession();
                }
                if (_stopping) break;

                // Tras una sesión con imagen se reintenta rápido; si no, la espera crece hasta el máximo.
                // Sin visor o con el visor en reposo se sondea al mínimo, para mostrar la imagen apenas esté listo.
                MirrorState state = _status.State;
                if (streamed || state == MirrorState.WaitingForDevice || state == MirrorState.DeviceAsleep)
                    retryDelay = _settings.retryMinSeconds;
                _wake.WaitOne(TimeSpan.FromSeconds(retryDelay));
                if (!streamed) retryDelay = Math.Min(retryDelay * 2f, _settings.retryMaxSeconds);
            }
        }

        /// <returns>true si llegó al menos un cuadro.</returns>
        private bool RunSession()
        {
            string adbPath = ExternalTools.FindAdb(_settings.adbPath)
                ?? throw new MirrorSetupException(MirrorState.MissingTools,
                    string.IsNullOrEmpty(_settings.adbPath) ? "no está el adb de VIVE Hub (instalar VIVE Hub)" : $"no existe {_settings.adbPath}");
            string ffmpegPath = ExternalTools.FindFfmpeg(_settings.ffmpegPath)
                ?? throw new MirrorSetupException(MirrorState.MissingTools, "no está ffmpeg (winget install Gyan.FFmpeg)");
            if (!File.Exists(_serverAssetPath))
                throw new MirrorSetupException(MirrorState.MissingTools, $"falta StreamingAssets/{ScrcpyProtocol.ServerAssetPath}");

            var adb = new AdbClient(adbPath);
            string serial = PickDevice(adb.ListDevices());

            // En reposo la pantalla del visor no produce cuadros: se espera aquí, sin arrancar servidor ni ffmpeg.
            // Si no se puede leer el estado se intenta igual (el watchdog cubre el caso).
            if (adb.TryGetWakefulness(serial, out string wakefulness) && wakefulness != "Awake")
                throw new MirrorSetupException(MirrorState.DeviceAsleep, $"visor en reposo ({wakefulness}): ponérselo para ver la imagen");

            SetStatus(MirrorState.Connecting, "conectando", serial);
            MarkProgress();

            (int width, int height) = adb.TryGetDisplaySize(serial, out int displayW, out int displayH)
                ? FrameGeometry.Fit(displayW, displayH, _settings.maxSize)
                : FrameGeometry.Fit(2, 1, _settings.maxSize);   // pantallas de visor: dos ojos lado a lado

            // El servidor borra su propio jar al arrancar (cleanup=true), así que se sube en cada sesión (~0,7 MB).
            adb.Push(serial, _serverAssetPath, ScrcpyProtocol.DeviceJarPath);

            RemoveStaleForwards(adb, serial);
            string sessionId = ScrcpyProtocol.NewSessionId(_random);
            int port = FindFreeLocalPort();
            adb.Forward(serial, port, ScrcpyProtocol.SocketName(sessionId));
            lock (_sessionGate)
            {
                _sessionAdb = adb;
                _sessionSerial = serial;
                _sessionPort = port;
            }

            // Sin using: el callback del servidor puede llegar después de esta espera, en otro hilo.
            var ready = new ManualResetEventSlim(false);
            Action<string> serverLog = LimitedLog("server");
            ChildProcess server = adb.StartShell(serial,
                ScrcpyProtocol.ServerCommand(sessionId, _settings.maxSize, _settings.maxFps, _settings.videoBitRate),
                line =>
                {
                    if (line.Contains(ScrcpyProtocol.ReadyLogMarker)) ready.Set();
                    serverLog(line);
                });
            if (!TrackSessionProcess(ref _server, server)) return false;
            if (!ready.Wait(ServerReadyTimeoutMs)) _log("el servidor no confirmó el arranque; se intenta conectar igual");
            if (_stopping) return false;
            Thread.Sleep(ServerSocketGraceMs);

            ChildProcess decoder = ChildProcess.Start(ffmpegPath, FfmpegDecoder.Arguments(port, width, height), null, LimitedLog("ffmpeg"));
            if (!TrackSessionProcess(ref _decoder, decoder)) return false;
            MarkProgress();
            return ReadFrames(decoder.Output, new FrameTripleBuffer(width, height), serial);
        }

        private bool ReadFrames(Stream stdout, FrameTripleBuffer frames, string serial)
        {
            bool any = false;
            while (!_stopping)
            {
                if (!ReadExactly(stdout, frames.Back, frames.FrameBytes))
                {
                    if (!_stopping) SetStatus(MirrorState.Reconnecting, any ? "se cortó la imagen" : "el visor no envió imagen", serial);
                    return any;
                }
                frames.Publish();
                Interlocked.Increment(ref _framesReceived);
                MarkProgress();
                if (!any)
                {
                    any = true;
                    _frames = frames;
                    SetStatus(MirrorState.Streaming, $"{frames.Width}x{frames.Height}", serial);
                }
            }
            return any;
        }

        private string PickDevice(IReadOnlyList<AdbDevice> devices)
        {
            List<AdbDevice> ready = devices.Where(d => d.IsReady).ToList();
            if (!string.IsNullOrEmpty(_settings.deviceSerial))
            {
                if (ready.Any(d => d.Serial == _settings.deviceSerial)) return _settings.deviceSerial;
                throw new MirrorSetupException(MirrorState.WaitingForDevice, $"no está el visor {_settings.deviceSerial}");
            }
            if (ready.Count > 0)
            {
                if (ready.Count > 1 && !_warnedMultipleDevices)
                {
                    _warnedMultipleDevices = true;
                    _log($"hay {ready.Count} visores ({string.Join(", ", ready.Select(d => d.Serial))}); se usa {ready[0].Serial}. Elegir con -mirror-serial.");
                }
                return ready[0].Serial;
            }
            if (devices.Any(d => d.IsUnauthorized))
                throw new MirrorSetupException(MirrorState.WaitingForDevice, "aceptar la depuración USB en el visor");
            throw new MirrorSetupException(MirrorState.WaitingForDevice, "conectar el visor por USB");
        }

        // ---------------- sesión: procesos y limpieza ----------------

        /// <summary>
        /// Si la app murió de golpe, el job mata a ffmpeg y al servidor pero el forward queda registrado en el
        /// servidor adb. Se quitan los forwards scrcpy_* de este visor (una app por visor; el scrcpy de escritorio
        /// usa reverse, no forward). Una vez por visor en la vida de la fuente.
        /// </summary>
        private void RemoveStaleForwards(AdbClient adb, string serial)
        {
            if (!_cleanedSerials.Add(serial)) return;
            foreach (AdbForward f in adb.ListForwards())
            {
                if (f.Serial != serial || f.LocalTcpPort == 0) continue;
                if (!f.Remote.StartsWith("localabstract:" + ScrcpyProtocol.SocketName(""), StringComparison.Ordinal)) continue;
                adb.RemoveForward(serial, f.LocalTcpPort);
                _log($"quitado un forward viejo: {f.Local} → {f.Remote}");
            }
        }

        /// <summary>Registra el proceso de la sesión. Si Stop() llegó mientras arrancaba, lo mata y devuelve false.</summary>
        private bool TrackSessionProcess(ref ChildProcess slot, ChildProcess process)
        {
            lock (_sessionGate)
            {
                slot = process;
                if (!_stopping) return true;
            }
            process.Kill();
            return false;
        }

        private void KillSessionProcesses()
        {
            lock (_sessionGate)
            {
                _decoder?.Kill();
                _server?.Kill();
            }
        }

        private void EndSession()
        {
            ChildProcess decoder, server;
            AdbClient adb;
            string serial;
            int port;
            lock (_sessionGate)
            {
                decoder = _decoder;
                server = _server;
                adb = _sessionAdb;
                serial = _sessionSerial;
                port = _sessionPort;
                _decoder = _server = null;
                _sessionAdb = null;
                _sessionSerial = null;
                _sessionPort = 0;
            }
            _frames = null;   // sin imagen vieja en pantalla mientras se reconecta
            decoder?.Dispose();
            server?.Dispose();
            if (adb != null && port != 0)
            {
                try { adb.RemoveForward(serial, port); }
                catch (Exception e) { _log($"no se pudo quitar el forward tcp:{port}: {e.Message}"); }
            }
        }

        // ---------------- watchdog ----------------

        private void MarkProgress() => Interlocked.Exchange(ref _lastProgressTicks, _clock.ElapsedTicks);

        private void CheckForStall()
        {
            MirrorState state = _status.State;
            if (_stopping || (state != MirrorState.Connecting && state != MirrorState.Streaming)) return;
            double idleSeconds = (_clock.ElapsedTicks - Interlocked.Read(ref _lastProgressTicks)) / (double)Stopwatch.Frequency;
            if (idleSeconds < _settings.stallTimeoutSeconds) return;
            _log($"sin imagen hace {idleSeconds:0} s: se reinicia la sesión");
            MarkProgress();   // un solo aviso por corte
            KillSessionProcesses();
        }

        // ---------------- utilidades ----------------

        private void SetStatus(MirrorState state, string detail, string serial = null)
        {
            var next = new MirrorStatus(state, detail, serial);
            MirrorStatus previous = _status;
            _status = next;
            if (previous.State != state || previous.Detail != next.Detail) _log(next.ToString());
        }

        /// <summary>Reenvía las líneas de un proceso al log, con tope por sesión (ffmpeg puede repetir errores al cortarse).</summary>
        private Action<string> LimitedLog(string source)
        {
            int remaining = ProcessLogLinesPerSession;
            return line =>
            {
                int left = Interlocked.Decrement(ref remaining);
                if (left >= 0) _log($"{source}: {line}");
                if (left == 0) _log($"{source}: (se omiten más líneas en esta sesión)");
            };
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n;
                try { n = stream.Read(buffer, read, count - read); }
                catch (IOException) { return false; }
                catch (ObjectDisposedException) { return false; }
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private static int FindFreeLocalPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
