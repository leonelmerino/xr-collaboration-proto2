using System;
using System.IO;
using System.Text;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Una pista de audio de una grabación: su WAV (<see cref="AudioWavWriter"/>), su reloj
    /// (<see cref="AudioClockFit"/>) y su archivo de sincronía (una fila muestra↔UTC por segundo, se escribe al cerrar).
    /// Thread-safe: la app la alimenta desde el hilo de audio de Unity y el micrófono desde el hilo principal.
    /// El WAV se crea con la primera escritura (recién ahí se sabe cuántos canales llegan).
    /// </summary>
    internal sealed class AudioTrackRecorder
    {
        private const double SyncRowEverySeconds = 1.0;

        private readonly object _lock = new object();
        private readonly AudioClockFit _fit;
        private readonly StringBuilder _syncRows = new StringBuilder();
        private AudioWavWriter _wav;
        private int _channels;
        private long _frames;
        private long _lastSyncFrame = long.MinValue;
        private int _syncPoints;
        private bool _closed;
        private bool _finished;
        private DateTime _firstChunkUtc;
        private string _error = "";

        public string Track { get; }
        public string Device { get; }
        public string FilePath { get; }
        public string SyncPath { get; }
        public int SampleRate { get; }

        public AudioTrackRecorder(string track, string device, string filePath, string syncPath, int sampleRate)
        {
            Track = track;
            Device = device;
            FilePath = filePath;
            SyncPath = syncPath;
            SampleRate = sampleRate;
            _fit = new AudioClockFit(sampleRate);
        }

        public bool HasData
        {
            get { lock (_lock) return _wav != null; }
        }

        public string Error
        {
            get
            {
                lock (_lock)
                {
                    string wavError = _wav?.Error ?? "";
                    return _error.Length > 0 ? _error : wavError;
                }
            }
        }

        /// <summary>Anota un problema (no detiene la pista).</summary>
        public void NoteError(string message)
        {
            lock (_lock)
                if (_error.Length == 0) _error = message ?? "";
        }

        /// <summary>
        /// Hilo de audio (OnAudioFilterRead): la primera muestra de <paramref name="data"/> pasó por la app a la hora
        /// <paramref name="utc"/>. Copia y vuelve enseguida.
        /// </summary>
        public void WriteObserved(float[] data, int channels, DateTime utc)
        {
            lock (_lock)
            {
                if (_closed) return;
                ObserveLocked(_frames, utc);
                WriteLocked(data, data.Length, channels);
            }
        }

        /// <summary>La muestra número <paramref name="frame"/> (desde el principio del WAV) llegó a la hora <paramref name="utc"/>.</summary>
        public void Observe(long frame, DateTime utc)
        {
            lock (_lock)
            {
                if (_closed) return;
                ObserveLocked(frame, utc);
            }
        }

        public void Write(float[] data, int count, int channels)
        {
            lock (_lock)
            {
                if (_closed) return;
                WriteLocked(data, count, channels);
            }
        }

        private void ObserveLocked(long frame, DateTime utc)
        {
            _fit.Add(frame, utc);
            if (_lastSyncFrame == long.MinValue || frame - _lastSyncFrame >= SampleRate * SyncRowEverySeconds)
            {
                _lastSyncFrame = frame;
                _syncPoints++;
                _syncRows.Append(RecordingLayout.AudioSyncRow(frame, utc)).Append('\n');
            }
        }

        private void WriteLocked(float[] data, int count, int channels)
        {
            if (data == null || count <= 0 || channels <= 0) return;
            if (_wav == null)
            {
                try
                {
                    _wav = new AudioWavWriter(FilePath, SampleRate, channels);
                    _channels = channels;
                    _firstChunkUtc = _fit.HasData ? _fit.FirstUtc : PreciseClock.UtcNow;
                }
                catch (Exception e)
                {
                    _closed = true;
                    _error = "no se pudo crear " + Path.GetFileName(FilePath) + ": " + e.Message;
                    return;
                }
            }
            if (channels != _channels)
            {
                // La salida de audio cambió a mitad (otro dispositivo): el resto no cabe en este WAV.
                if (_error.Length == 0) _error = $"cambió la cantidad de canales ({_channels} → {channels}): el audio se cortó ahí";
                _closed = true;
                _wav.Close();
                return;
            }
            _wav.Write(data, count);
            _frames += count / channels;
        }

        /// <summary>Deja de aceptar audio y cierra el WAV (en su hilo). No espera.</summary>
        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                _wav?.Close();
            }
        }

        /// <summary>Espera a que el WAV quede cerrado y escribe el archivo de sincronía. Llamar después de <see cref="Close"/>.</summary>
        public bool Finish(int timeoutMs)
        {
            AudioWavWriter wav;
            string rows;
            lock (_lock)
            {
                if (_finished) return true;
                wav = _wav;
                rows = _syncRows.ToString();
            }
            bool closed = wav == null || wav.WaitClosed(timeoutMs);
            if (wav != null && rows.Length > 0)
            {
                try
                {
                    File.WriteAllText(SyncPath, RecordingLayout.AudioSyncHeader + "\n" + rows, new UTF8Encoding(false));
                }
                catch (Exception e)
                {
                    NoteError("no se pudo escribir " + Path.GetFileName(SyncPath) + ": " + e.Message);
                }
            }
            lock (_lock) _finished = closed;
            return closed;
        }

        public AudioTrackInfo Info()
        {
            lock (_lock)
            {
                var info = new AudioTrackInfo
                {
                    track = Track,
                    device = Device ?? "",
                    sampleRate = SampleRate,
                    channels = _channels,
                    frames = _frames,
                    seconds = SampleRate > 0 ? Math.Round(_frames / (double)SampleRate, 3) : 0,
                    syncPoints = _syncPoints,
                    error = _error.Length > 0 ? _error : _wav?.Error ?? "",
                    file = "",
                    syncFile = "",
                    startUtc = "",
                    firstChunkUtc = "",
                };
                if (_wav != null)
                {
                    info.file = Path.GetFileName(FilePath);
                    info.syncFile = Path.GetFileName(SyncPath);
                    info.startUtc = RecordingLayout.FormatUtcMicros(_fit.StartUtc);
                    info.firstChunkUtc = RecordingLayout.FormatUtcMicros(_firstChunkUtc);
                    info.effectiveRate = Math.Round(_fit.EffectiveRate, 3);
                    info.framesSilenced = _wav.FramesSilenced;
                }
                return info;
            }
        }
    }
}
