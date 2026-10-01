using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using XRCollab.Measurement.Mirroring;

namespace XRCollab.Measurement.Recording
{
    /// <summary>Encoder elegido una sola vez al abrir la app (en segundo plano), para que «start» sea inmediato.</summary>
    internal sealed class EncoderProbe
    {
        public readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
        public volatile bool Ok;
        public volatile string Codec = "…";
        public volatile string Error = "";
        public RecordingEncoder Encoder = RecordingEncoder.Auto;
    }

    /// <summary>
    /// Una grabación, de «start» a «stop»: su carpeta, su cola de cuadros, su ffmpeg y su frames.csv.
    /// El hilo principal solo encola cuadros y pide parar (nunca espera); el hilo propio de la grabación escribe a
    /// ffmpeg, reinicia ffmpeg si se corta, cierra el último segmento y lanza el armado del .mp4 final.
    /// frames.csv tiene una fila por cuadro que recibió ffmpeg, en orden: la fila N es el cuadro N del video, con la
    /// hora UTC exacta (µs) en que se capturó. Es lo que permite alinear el video con la telemetría y con otros PCs.
    /// </summary>
    internal sealed class RecordingRun
    {
        private const string LogPrefix = "[SessionRecorder] ";
        private const int QueueCapacity = 8;          // ~9 MB por cuadro a 1920x1200
        private const int MaxEncoderRestarts = 5;
        private const int EncoderWaitMs = 60_000;
        private const int FramesFlushEvery = 30;

        private readonly struct Frame
        {
            public readonly byte[] Data;
            public readonly DateTime Utc;
            public readonly int UnityFrame;
            public readonly double UnityTime;

            public Frame(byte[] data, DateTime utc, int unityFrame, double unityTime)
            {
                Data = data;
                Utc = utc;
                UnityFrame = unityFrame;
                UnityTime = unityTime;
            }
        }

        private readonly SessionRecorderSettings _settings;
        private readonly string _ffmpeg;
        private readonly EncoderProbe _probe;
        private readonly BlockingCollection<Frame> _queue = new BlockingCollection<Frame>(QueueCapacity);
        private readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        private readonly Thread _worker;
        private readonly object _infoLock = new object();
        private readonly RecordingSessionInfo _info;
        private volatile ChildProcess _encoder;
        private volatile bool _stopping;
        private volatile bool _failed;
        private volatile bool _finished;
        private volatile string _status = "armada";
        private long _captured;
        private long _written;
        private long _dropped;
        private long _firstFrameTicks;

        public string Dir { get; }
        public string Label { get; }
        public int Width { get; }
        public int Height { get; }
        public DateTime RequestedUtc { get; }
        public DateTime StartAtUtc { get; }
        public DateTime? StopAtUtc { get; set; }
        public bool Stopping => _stopping;
        public bool Failed => _failed;
        public bool Finished => _finished;
        public string Status => _status;
        public long Written => Interlocked.Read(ref _written);
        public long Dropped => Interlocked.Read(ref _dropped);
        public DateTime? FirstFrameUtc
        {
            get
            {
                long t = Interlocked.Read(ref _firstFrameTicks);
                return t == 0 ? (DateTime?)null : new DateTime(t, DateTimeKind.Utc);
            }
        }
        public string FinalVideo => RecordingLayout.FinalVideoPath(Dir);

        public RecordingRun(SessionRecorderSettings settings, string ffmpeg, EncoderProbe probe, string root, string label,
            int width, int height, DateTime requestedUtc, DateTime startAtUtc)
        {
            _settings = settings;
            _ffmpeg = ffmpeg;
            _probe = probe;
            Label = label ?? "";
            Width = width;
            Height = height;
            RequestedUtc = requestedUtc;
            StartAtUtc = startAtUtc;
            DateTime localStart = startAtUtc.ToLocalTime();
            Dir = RecordingLayout.UniqueSessionDir(root, RecordingLayout.SessionName(localStart, Environment.MachineName, Label));
            Directory.CreateDirectory(Path.Combine(Dir, RecordingLayout.SegmentsFolder));

            _info = new RecordingSessionInfo
            {
                computer = Environment.MachineName,
                label = Label,
                requestedUtc = RecordingCommand.FormatUtc(requestedUtc),
                startAtUtc = RecordingCommand.FormatUtc(startAtUtc),
                startedUtc = startAtUtc.ToString("o"),
                startedLocal = localStart.ToString("o"),
                utcOffset = SessionRecorder.FormatOffset(TimeZoneInfo.Local.GetUtcOffset(localStart)),
                width = width,
                height = height,
                fps = settings.fps,
                segmentSeconds = settings.segmentSeconds,
                encoder = probe.Codec,
                framesFile = RecordingLayout.FramesFile,
                note = "closedCleanly=false: la grabación no se cerró normal (el video final se arma al próximo arranque)",
            };
            WriteInfo();

            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "SessionRecorder " + Path.GetFileName(Dir) };
            _worker.Start();
        }

        // ---------------- hilo principal ----------------

        public byte[] RentBuffer(int length)
        {
            if (!_pool.TryTake(out byte[] buffer) || buffer.Length != length) buffer = new byte[length];
            return buffer;
        }

        /// <summary>Encola un cuadro ya leído de la GPU. Nunca bloquea: si la cola está llena se descarta.</summary>
        public void Enqueue(byte[] data, DateTime captureUtc, int unityFrame, double unityTime)
        {
            if (_stopping || _queue.IsAddingCompleted)
            {
                _pool.Add(data);
                return;
            }
            bool added;
            try { added = _queue.TryAdd(new Frame(data, captureUtc, unityFrame, unityTime)); }
            catch (InvalidOperationException) { added = false; }   // CompleteAdding justo entre medio
            if (!added)
            {
                Interlocked.Increment(ref _dropped);
                _pool.Add(data);
                return;
            }
            Interlocked.Increment(ref _captured);
        }

        public void CountDropped() => Interlocked.Increment(ref _dropped);

        /// <summary>Pide parar: no espera. El hilo de la grabación vacía la cola, cierra ffmpeg y arma el .mp4.</summary>
        public void RequestStop(DateTime stopUtc)
        {
            if (_stopping) return;
            _stopping = true;
            lock (_infoLock) _info.stopRequestedUtc = RecordingCommand.FormatUtc(stopUtc);
            _queue.CompleteAdding();
        }

        /// <summary>Solo al cerrar la app: espera a que el último segmento quede bien cerrado.</summary>
        public bool WaitFinished(int timeoutMs) => _worker.Join(timeoutMs);

        public long BytesOnDisk(out int segments)
        {
            long bytes = 0;
            IReadOnlyList<string> list = RecordingLayout.Segments(Dir);
            foreach (string s in list)
            {
                try { bytes += new FileInfo(s).Length; }
                catch (IOException) { }
            }
            segments = list.Count;
            return bytes;
        }

        // ---------------- hilo de la grabación ----------------

        private void WorkerLoop()
        {
            StreamWriter frames = null;
            try
            {
                frames = new StreamWriter(Path.Combine(Dir, RecordingLayout.FramesFile), false, new UTF8Encoding(false)) { NewLine = "\n" };
                frames.WriteLine(RecordingLayout.FramesHeader);
                frames.Flush();

                if (!_probe.Ready.Wait(EncoderWaitMs) || !_probe.Ok)
                {
                    Fail(_probe.Ready.IsSet ? _probe.Error : "el encoder no estuvo listo a tiempo");
                    Drain();
                    return;
                }
                lock (_infoLock) _info.encoder = _probe.Codec;
                WriteInfo();

                int restarts = 0;
                while (true)
                {
                    StartEncoder();
                    _status = "grabando";
                    if (!PumpFrames(frames)) break;   // false = se pidió parar y la cola quedó vacía
                    if (++restarts > MaxEncoderRestarts)
                    {
                        Fail("ffmpeg se cortó demasiadas veces (ver Player.log)");
                        Drain();
                        break;
                    }
                    _status = "ffmpeg se cortó; reiniciando";
                    Debug.LogWarning($"{LogPrefix}ffmpeg se cortó; reinicio {restarts}/{MaxEncoderRestarts} (sigue la numeración de segmentos)");
                    Thread.Sleep(1000);
                }
            }
            catch (Exception e)
            {
                Fail(e.Message);
                Drain();
            }
            finally
            {
                try { frames?.Dispose(); }
                catch (Exception) { }
                Close();
            }
        }

        private void StartEncoder()
        {
            int start = RecordingLayout.NextSegmentNumber(Dir);
            string args = RecordingFfmpeg.RecordArguments(_probe.Encoder, Width, Height, _settings.fps, _settings.segmentSeconds,
                RecordingLayout.SegmentPattern(Dir), start);
            int logged = 0;
            _encoder = ChildProcess.Start(_ffmpeg, args, null,
                line => { if (Interlocked.Increment(ref logged) <= 20) Debug.Log($"{LogPrefix}ffmpeg: {line}"); },
                redirectInput: true);
        }

        /// <returns>true si el pipe se rompió (hay que reiniciar ffmpeg); false si se pidió parar.</returns>
        private bool PumpFrames(StreamWriter frames)
        {
            Stream input = _encoder.Input;
            foreach (Frame frame in _queue.GetConsumingEnumerable())   // termina con CompleteAdding, tras vaciar la cola
            {
                try
                {
                    input.Write(frame.Data, 0, frame.Data.Length);
                }
                catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    Interlocked.Increment(ref _dropped);
                    _pool.Add(frame.Data);
                    _encoder?.Kill();
                    return true;
                }
                long index = Interlocked.Increment(ref _written) - 1;
                if (index == 0)
                {
                    Interlocked.Exchange(ref _firstFrameTicks, frame.Utc.Ticks);
                    lock (_infoLock)
                    {
                        _info.firstFrameUtc = RecordingCommand.FormatUtc(frame.Utc);
                        _info.startedUtc = frame.Utc.ToString("o");
                        _info.startedLocal = frame.Utc.ToLocalTime().ToString("o");
                    }
                    WriteInfo();
                }
                frames.WriteLine(RecordingLayout.FramesRow(index, frame.Utc, frame.UnityFrame, frame.UnityTime));
                if (index % FramesFlushEvery == 0) frames.Flush();
                lock (_infoLock) _info.lastFrameUtc = RecordingCommand.FormatUtc(frame.Utc);
                _pool.Add(frame.Data);
            }
            return false;
        }

        private void Drain()
        {
            foreach (Frame f in _queue.GetConsumingEnumerable()) Interlocked.Increment(ref _dropped);
        }

        private void Close()
        {
            ChildProcess encoder = _encoder;
            if (encoder != null)
            {
                try { encoder.Input.Close(); }   // EOF: ffmpeg cierra bien el último segmento
                catch (Exception) { }
                if (!encoder.WaitForExit(8000)) encoder.Kill();
                encoder.Dispose();
            }

            lock (_infoLock)
            {
                _info.stoppedUtc = PreciseClock.UtcNow.ToString("o");
                _info.closedCleanly = true;
                _info.framesCaptured = Interlocked.Read(ref _captured);
                _info.framesWritten = Interlocked.Read(ref _written);
                _info.framesDropped = Interlocked.Read(ref _dropped);
                _info.note = _failed ? "grabación con error: " + _status : "";
            }
            WriteInfo();

            if (_ffmpeg != null)
            {
                try { SessionRecorder.LaunchFinalize(Dir, _ffmpeg); }
                catch (Exception e) { Debug.LogWarning($"{LogPrefix}no se pudo lanzar el armado del video final: {e.Message}"); }
            }
            if (!_failed) _status = "detenida";
            _finished = true;
            Debug.Log($"{LogPrefix}grabación detenida: {Written} cuadros escritos, {Dropped} descartados. Video final: {FinalVideo}");
        }

        private void Fail(string reason)
        {
            _failed = true;
            _status = reason;
            Debug.LogWarning($"{LogPrefix}sin grabación: {reason}");
        }

        private void WriteInfo()
        {
            try
            {
                string json;
                lock (_infoLock) json = JsonUtility.ToJson(_info, true);
                File.WriteAllText(Path.Combine(Dir, RecordingLayout.InfoFile), json);
            }
            catch (Exception e) { Debug.LogWarning($"{LogPrefix}no se pudo escribir session.json: {e.Message}"); }
        }
    }
}
