using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// WAV PCM de 16 bits escrito en su propio hilo. <see cref="Write"/> se puede llamar desde cualquier hilo (también
    /// el de audio de Unity): solo copia a un buffer del pool y encola, nunca toca el disco ni bloquea.
    /// El encabezado se actualiza cada ~1 s: si la app muere, el WAV queda legible hasta ese punto.
    /// Si el disco no da abasto (más de 10 s encolados), se escribe silencio del mismo largo en lugar de descartar:
    /// así el WAV mantiene la línea de tiempo (se cuenta en <see cref="FramesSilenced"/>).
    /// Límite del formato: 4 GB (≈ 6 h estéreo a 48 kHz).
    /// </summary>
    public sealed class AudioWavWriter
    {
        public const int HeaderBytes = 44;
        private const double MaxQueuedSeconds = 10.0;

        private readonly struct Chunk
        {
            public readonly float[] Data;   // null = silencio
            public readonly int Count;      // muestras intercaladas

            public Chunk(float[] data, int count)
            {
                Data = data;
                Count = count;
            }
        }

        private readonly ConcurrentQueue<Chunk> _queue = new ConcurrentQueue<Chunk>();
        private readonly ConcurrentBag<float[]> _pool = new ConcurrentBag<float[]>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _thread;
        private readonly FileStream _file;
        private readonly long _maxQueuedSamples;
        private long _queuedSamples;
        private long _samplesWritten;
        private long _samplesSilenced;
        private volatile bool _closing;
        private volatile string _error = "";

        public string FilePath { get; }
        public int SampleRate { get; }
        public int Channels { get; }
        public long FramesWritten => Interlocked.Read(ref _samplesWritten) / Channels;
        public long FramesSilenced => Interlocked.Read(ref _samplesSilenced) / Channels;
        public string Error => _error;

        public AudioWavWriter(string filePath, int sampleRate, int channels)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0 || channels > 16) throw new ArgumentOutOfRangeException(nameof(channels));
            FilePath = filePath;
            SampleRate = sampleRate;
            Channels = channels;
            _maxQueuedSamples = (long)(MaxQueuedSeconds * sampleRate * channels);
            _file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            byte[] header = Header(sampleRate, channels, 0);
            _file.Write(header, 0, header.Length);
            _thread = new Thread(Loop) { IsBackground = true, Name = "SessionRecorder " + Path.GetFileName(filePath) };
            _thread.Start();
        }

        /// <summary>Encola <paramref name="count"/> muestras intercaladas (float −1..1). Cualquier hilo; no bloquea.</summary>
        public void Write(float[] data, int count)
        {
            if (_closing || data == null || count <= 0) return;
            count = Math.Min(count, data.Length);
            count -= count % Channels;
            if (count == 0) return;
            if (Interlocked.Read(ref _queuedSamples) + count > _maxQueuedSamples)
            {
                _queue.Enqueue(new Chunk(null, count));
            }
            else
            {
                if (!_pool.TryTake(out float[] buffer) || buffer.Length < count) buffer = new float[count];
                Array.Copy(data, buffer, count);
                Interlocked.Add(ref _queuedSamples, count);
                _queue.Enqueue(new Chunk(buffer, count));
            }
            _signal.Set();
        }

        /// <summary>Termina: el hilo escribe lo encolado, cierra el encabezado y el archivo. No espera.</summary>
        public void Close()
        {
            _closing = true;
            _signal.Set();
        }

        public bool WaitClosed(int timeoutMs) => _thread.Join(timeoutMs);

        private void Loop()
        {
            byte[] bytes = new byte[0];
            long sinceHeader = 0;
            long headerEvery = (long)SampleRate * Channels;   // ~1 s
            bool ioOk = true;
            try
            {
                while (true)
                {
                    bool closing = _closing;
                    while (_queue.TryDequeue(out Chunk c))
                    {
                        if (c.Data != null)
                        {
                            Interlocked.Add(ref _queuedSamples, -c.Count);
                        }
                        if (!ioOk)
                        {
                            if (c.Data != null) _pool.Add(c.Data);
                            continue;
                        }
                        int byteCount = c.Count * 2;
                        if (bytes.Length < byteCount) bytes = new byte[byteCount];
                        if (c.Data == null)
                        {
                            Array.Clear(bytes, 0, byteCount);
                            Interlocked.Add(ref _samplesSilenced, c.Count);
                        }
                        else
                        {
                            ToPcm16(c.Data, c.Count, bytes);
                            _pool.Add(c.Data);
                        }
                        try
                        {
                            _file.Write(bytes, 0, byteCount);
                            Interlocked.Add(ref _samplesWritten, c.Count);
                            sinceHeader += c.Count;
                            if (sinceHeader >= headerEvery)
                            {
                                sinceHeader = 0;
                                UpdateHeader();
                            }
                        }
                        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is UnauthorizedAccessException)
                        {
                            ioOk = false;
                            _error = "no se pudo escribir " + Path.GetFileName(FilePath) + ": " + e.Message;
                        }
                    }
                    if (closing) break;
                    _signal.WaitOne(200);
                }
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
            finally
            {
                try
                {
                    if (ioOk) UpdateHeader();
                }
                catch (Exception e)
                {
                    if (_error.Length == 0) _error = e.Message;
                }
                try { _file.Dispose(); }
                catch (Exception) { }
            }
        }

        private void UpdateHeader()
        {
            long dataBytes = Math.Min(Interlocked.Read(ref _samplesWritten) * 2, uint.MaxValue - 36L);
            long end = _file.Position;
            _file.Seek(4, SeekOrigin.Begin);
            WriteUInt32(_file, (uint)(36 + dataBytes));
            _file.Seek(40, SeekOrigin.Begin);
            WriteUInt32(_file, (uint)dataBytes);
            _file.Seek(end, SeekOrigin.Begin);
            _file.Flush();
        }

        private static void WriteUInt32(Stream s, uint v)
        {
            s.WriteByte((byte)v);
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 24));
        }

        /// <summary>float −1..1 → PCM 16 bits little-endian (con recorte).</summary>
        public static void ToPcm16(float[] samples, int count, byte[] output)
        {
            for (int i = 0; i < count; i++)
            {
                float v = samples[i];
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                else if (float.IsNaN(v)) v = 0f;
                short s = (short)Math.Round(v * 32767f);
                output[2 * i] = (byte)s;
                output[2 * i + 1] = (byte)(s >> 8);
            }
        }

        /// <summary>Encabezado RIFF/WAVE PCM 16 bits de 44 bytes.</summary>
        public static byte[] Header(int sampleRate, int channels, long dataBytes)
        {
            var header = new byte[HeaderBytes];
            using (var ms = new MemoryStream(header))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write((uint)(36 + dataBytes));
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16u);                                  // tamaño del bloque fmt
                w.Write((ushort)1);                            // PCM
                w.Write((ushort)channels);
                w.Write((uint)sampleRate);
                w.Write((uint)(sampleRate * channels * 2));    // bytes por segundo
                w.Write((ushort)(channels * 2));               // bytes por cuadro
                w.Write((ushort)16);                           // bits por muestra
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write((uint)dataBytes);
            }
            return header;
        }
    }
}
