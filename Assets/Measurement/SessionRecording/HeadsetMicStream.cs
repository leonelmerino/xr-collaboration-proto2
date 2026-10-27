using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Micrófono del visor en vivo para el panel de sesión (build nativo, 2026-10-27). Los micrófonos de la sala no
    /// funcionan y en standalone el visor no graba audio; así el operador escucha a cada participante desde el PC de
    /// monitoreo.
    ///
    /// 127.0.0.1:<see cref="Port"/> del visor (el PC llega con <c>adb forward</c>, como al canal de control 47811).
    /// Un oyente a la vez (uno nuevo reemplaza al anterior). Al conectarse se prende el micrófono y se manda una línea
    /// «PCM16 16000 1» (formato, Hz, canales) y después audio crudo s16le mono 16 kHz en bloques de ~40 ms; al cortarse
    /// se apaga el micrófono. Si falla, una línea «ERROR …» y se cierra. Sin oyente no hay micrófono abierto.
    /// La latencia se mantiene baja: si el PC no lee a tiempo se descartan los bloques más viejos (máx. ~0,5 s en cola).
    /// El permiso RECORD_AUDIO (Unity lo pone en el manifiesto al usar Microphone) lo da Deploy-LabHeadsetApp.ps1 al
    /// instalar (<c>adb install -r -g</c>): nunca aparece un diálogo en el visor.
    /// </summary>
    public sealed class HeadsetMicStream : MonoBehaviour
    {
        public const int Port = 47812;
        public const int Rate = 16000;
        private const string LogPrefix = "[MicStream] ";
        private const int MicClipSeconds = 2;
        private const int MaxQueued = 12;   // bloques de ~40 ms

        /// <summary>Para el estado de la app: «sin oyente», «escuchando», «sin permiso de micrófono», …</summary>
        public static string Status { get; private set; } = "sin oyente";

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _stopping;
        private readonly object _lock = new object();
        private readonly Queue<byte[]> _queue = new Queue<byte[]>();
        private readonly AutoResetEvent _queued = new AutoResetEvent(false);
        private TcpClient _client;
        private int _clientId;
        private volatile bool _wantMic;

        private AudioClip _clip;
        private string _device;
        private int _micRate;
        private int _readPos;
        private float[] _tmp = new float[4096];
        private double _resamplePos;   // posición fraccionaria en la entrada (remuestreo lineal a 16 kHz)
        private float _lastSample;

#if UNITY_ANDROID && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            if (FindObjectOfType<HeadsetMicStream>() != null) return;
            var go = new GameObject("[HeadsetMicStream]");
            DontDestroyOnLoad(go);
            go.AddComponent<HeadsetMicStream>();
        }
#endif

        private void Awake()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MicStreamAccept" };
                _acceptThread.Start();
                Debug.Log($"{LogPrefix}esperando al panel en 127.0.0.1:{Port}");
            }
            catch (Exception e)
            {
                Status = "puerto ocupado";
                Debug.LogWarning($"{LogPrefix}no se pudo abrir el puerto {Port}: {e.Message}");
            }
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (_stopping) return; Thread.Sleep(200); continue; }
                c.NoDelay = true;
                c.SendTimeout = 2000;
                int id;
                lock (_lock)
                {
                    try { _client?.Close(); } catch { }
                    _client = c;
                    id = ++_clientId;
                    _queue.Clear();
                }
                _wantMic = true;
                new Thread(() => SendLoop(c, id)) { IsBackground = true, Name = "MicStreamSend" }.Start();
            }
        }

        private void SendLoop(TcpClient c, int id)
        {
            try
            {
                NetworkStream s = c.GetStream();
                while (!_stopping)
                {
                    byte[] chunk = null;
                    lock (_lock)
                    {
                        if (id != _clientId) return;
                        if (_queue.Count > 0) chunk = _queue.Dequeue();
                    }
                    if (chunk == null) { _queued.WaitOne(250); continue; }
                    if (chunk.Length == 0) return;   // fin pedido (error ya enviado)
                    s.Write(chunk, 0, chunk.Length);
                }
            }
            catch { }
            finally
            {
                lock (_lock)
                {
                    if (id == _clientId) { _client = null; _wantMic = false; _queue.Clear(); }
                }
                try { c.Close(); } catch { }
            }
        }

        private void Enqueue(byte[] chunk)
        {
            lock (_lock)
            {
                if (_client == null) return;
                while (_queue.Count >= MaxQueued) _queue.Dequeue();   // PC atrasado: mejor perder audio viejo que acumular latencia
                _queue.Enqueue(chunk);
            }
            _queued.Set();
        }

        private void Update()
        {
            if (_wantMic && _clip == null) StartMic();
            else if (!_wantMic && _clip != null) StopMic("sin oyente");
            if (_clip != null) Pump();
        }

        private void StartMic()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                Fail("sin permiso de micrófono (reinstalar con Deploy-LabHeadsetApp.ps1 install: adb install -g)");
                return;
            }
#endif
            string[] devices = Microphone.devices;
            if (devices.Length == 0) { Fail("el visor no reporta micrófono"); return; }
            _device = devices[0];
            Microphone.GetDeviceCaps(_device, out int minRate, out int maxRate);
            _micRate = (minRate == 0 && maxRate == 0) || (Rate >= minRate && Rate <= maxRate) ? Rate : Mathf.Clamp(48000, minRate, maxRate);
            _clip = Microphone.Start(_device, true, MicClipSeconds, _micRate);
            if (_clip == null) { Fail("Microphone.Start falló"); return; }
            _micRate = _clip.frequency;
            _readPos = 0;
            _resamplePos = 0;
            Enqueue(Encoding.ASCII.GetBytes($"PCM16 {Rate} 1\n"));
            Status = "escuchando";
            Debug.Log($"{LogPrefix}micrófono «{_device}» a {_micRate} Hz → panel ({Rate} Hz)");
        }

        private void Fail(string why)
        {
            Status = why;
            Debug.LogWarning(LogPrefix + why);
            Enqueue(Encoding.UTF8.GetBytes($"ERROR {why}\n"));
            Enqueue(Array.Empty<byte>());
            _wantMic = false;
        }

        private void Pump()
        {
            int pos = Microphone.GetPosition(_device);
            int total = _clip.samples;
            int n = (pos - _readPos + total) % total;
            if (n < _micRate / 25) return;   // ~40 ms
            if (_tmp.Length < n) _tmp = new float[n];
            // GetData envuelve solo al final del clip: en dos partes si cruza el borde.
            int first = Math.Min(n, total - _readPos);
            var a = new float[first];
            _clip.GetData(a, _readPos);
            Array.Copy(a, 0, _tmp, 0, first);
            if (n > first)
            {
                var b = new float[n - first];
                _clip.GetData(b, 0);
                Array.Copy(b, 0, _tmp, first, n - first);
            }
            _readPos = (_readPos + n) % total;
            Enqueue(ToPcm16(_tmp, n));
        }

        /// <summary>Remuestreo lineal a <see cref="Rate"/> (el micrófono puede dar 48 kHz) y float → s16le.</summary>
        private byte[] ToPcm16(float[] src, int n)
        {
            double step = (double)_micRate / Rate;
            var outSamples = new List<short>((int)(n / step) + 2);
            // p en [-1, 0) = entre la última muestra del bloque anterior y src[0].
            double p = _resamplePos;
            while (p < n - 1)
            {
                int i = (int)Math.Floor(p);
                float s0 = i < 0 ? _lastSample : src[i];
                float s1 = src[i + 1];
                float v = Mathf.Lerp(s0, s1, (float)(p - i));
                outSamples.Add((short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32768, 32767));
                p += step;
            }
            _resamplePos = p - n;
            _lastSample = src[n - 1];
            var bytes = new byte[outSamples.Count * 2];
            for (int k = 0; k < outSamples.Count; k++) { bytes[2 * k] = (byte)outSamples[k]; bytes[2 * k + 1] = (byte)(outSamples[k] >> 8); }
            return bytes;
        }

        private void StopMic(string status)
        {
            try { Microphone.End(_device); } catch { }
            _clip = null;
            Status = status;
            Debug.Log($"{LogPrefix}micrófono apagado ({status})");
        }

        private void OnApplicationPause(bool paused)
        {
            // Visor dormido o app al fondo: se suelta el micrófono; al volver, sigue si el panel sigue conectado.
            if (paused && _clip != null) StopMic("app en pausa");
        }

        private void OnDestroy()
        {
            _stopping = true;
            try { _listener?.Stop(); } catch { }
            lock (_lock) { try { _client?.Close(); } catch { } _client = null; }
            if (_clip != null) StopMic("app cerrada");
        }
    }
}
