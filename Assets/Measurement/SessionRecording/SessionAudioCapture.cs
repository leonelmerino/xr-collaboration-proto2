using System;
using System.IO;
using UnityEngine;

namespace XRCollab.Measurement.Recording
{
    /// <summary>Elección del micrófono por nombre. Pura, con tests.</summary>
    public static class RecordingAudioDevices
    {
        /// <summary>Orden de búsqueda del micrófono si no se pide uno (-rec-mic): VIVE primero, después Meta/Quest.</summary>
        public static readonly string[] AutoMicNames = { "VIVE", "Focus", "Oculus", "Meta", "Headset" };

        /// <summary>
        /// Elige el micrófono: el primero cuyo nombre contenga alguno de los textos pedidos (en orden; separados por
        /// coma), o <see cref="AutoMicNames"/> si no se pidió nada. Si ninguno coincide: "" = el predeterminado de
        /// Windows. null = no hay micrófonos.
        /// </summary>
        public static string ChooseMic(string[] devices, string preference, out string description)
        {
            if (devices == null || devices.Length == 0)
            {
                description = "no hay micrófonos en Windows";
                return null;
            }
            string[] wanted = string.IsNullOrWhiteSpace(preference)
                ? AutoMicNames
                : preference.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in wanted)
            {
                string w = raw.Trim();
                if (w.Length == 0) continue;
                foreach (string d in devices)
                {
                    if (d != null && d.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        description = d;
                        return d;
                    }
                }
            }
            description = $"predeterminado de Windows (ninguno contiene {string.Join("/", wanted)})";
            return "";
        }
    }

    /// <summary>
    /// Audio de una grabación, en dos WAV aparte del video:
    ///   - <b>app</b>: la mezcla final de la app (lo que oye el participante), con <see cref="AppAudioTap"/> en el
    ///     AudioListener (OnAudioFilterRead, hilo de audio).
    ///   - <b>mic</b>: el micrófono del visor (VIVE Focus Vision por VIVE Streaming aparece como micrófono de
    ///     Windows; Quest por Link como «Headset Microphone (Oculus Virtual Audio Device)»), con UnityEngine.Microphone
    ///     leído en el hilo principal en bloques de 20 ms.
    /// Por qué no un input de audio en el ffmpeg del video (dshow): si el micrófono falla o desaparece (visor
    /// desconectado) ffmpeg se cae y se pierde el video; acá el audio nunca toca el video. La sincronía se hace con
    /// la hora UTC (<see cref="AudioClockFit"/>), igual que frames.csv, y se mete en el .mp4 al final.
    /// Todo lo de Unity (Start, Pump, Stop) en el hilo principal; <see cref="WaitFinished"/> y <see cref="FillInfo"/>
    /// desde cualquier hilo.
    /// </summary>
    internal sealed class SessionAudioCapture
    {
        private const string LogPrefix = "[SessionRecorder] ";
        private const int MicClipSeconds = 10;
        private const int PreferredMicRate = 48000;
        private const float ListenerCheckSeconds = 1f;

        private readonly string _dir;
        private AudioTrackRecorder _app;
        private AudioTrackRecorder _mic;
        private AppAudioTap _tap;
        private string _micApiDevice;     // null = micrófono predeterminado
        private AudioClip _micClip;
        private bool _micRunning;
        private bool _micStarted;
        private int _micReadPos;
        private long _micFramesRead;
        private float[] _micBlock;
        private int _micBlockFrames;
        private float _nextListenerCheck;
        private bool _stopped;
        private bool _configHooked;

        public string AppStatus { get; private set; } = "off";
        public string MicStatus { get; private set; } = "off";

        public bool HasStarted => (_app != null && _app.HasData) || (_mic != null && _mic.HasData);

        private SessionAudioCapture(string dir)
        {
            _dir = dir;
        }

        /// <summary>Hilo principal. Nunca lanza: cada problema queda en <see cref="AppStatus"/> / <see cref="MicStatus"/>.</summary>
        public static SessionAudioCapture Start(SessionRecorderSettings settings, string dir)
        {
            var capture = new SessionAudioCapture(dir);
            if (!settings.audio) return capture;
            try { capture.StartApp(); }
            catch (Exception e) { capture.AppStatus = "error: " + e.Message; }
            if (settings.mic)
            {
                try { capture.StartMic(settings.micDevice); }
                catch (Exception e) { capture.MicStatus = "error: " + e.Message; }
            }
            Debug.Log($"{LogPrefix}audio: app = {capture.AppStatus} · mic = {capture.MicStatus}");
            return capture;
        }

        /// <summary>Lo que se usaría al grabar (para el «status» en espera). Hilo principal.</summary>
        public static void Preview(SessionRecorderSettings settings, out string app, out string mic)
        {
            if (!settings.audio)
            {
                app = mic = "off";
                return;
            }
            AudioListener listener = FindListener();
            app = listener != null ? $"AudioListener {listener.name} · {AudioSettings.outputSampleRate} Hz" : "error: no hay AudioListener activo";
            if (!settings.mic)
            {
                mic = "off";
                return;
            }
            string chosen = RecordingAudioDevices.ChooseMic(Microphone.devices, settings.micDevice, out string description);
            mic = chosen == null ? "error: " + description : description;
        }

        // ---------------- app ----------------

        private void StartApp()
        {
            AudioListener listener = FindListener();
            if (listener == null)
            {
                AppStatus = "error: no hay AudioListener activo";
                return;
            }
            int rate = AudioSettings.outputSampleRate;
            if (rate <= 0)
            {
                AppStatus = "error: el audio de Unity está apagado";
                return;
            }
            _app = new AudioTrackRecorder("app", "AudioListener " + listener.name,
                Path.Combine(_dir, RecordingLayout.AppAudioFile), Path.Combine(_dir, RecordingLayout.AppAudioSyncFile), rate);
            AttachTap(listener);
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
            _configHooked = true;
            AppStatus = $"{_app.Device} · {rate} Hz";
        }

        private void AttachTap(AudioListener listener)
        {
            if (!listener.TryGetComponent(out AppAudioTap tap)) tap = listener.gameObject.AddComponent<AppAudioTap>();
            if (_tap != null && _tap != tap) _tap.Sink = null;
            _tap = tap;
            _tap.enabled = true;
            _tap.Sink = _app;
        }

        // Cambió el dispositivo de salida (visor reconectado, otro audio predeterminado): Unity reinicia el audio y
        // puede cambiar la frecuencia. El WAV no puede cambiar de frecuencia a mitad: se corta la pista y se avisa.
        private void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            if (_app == null || _stopped) return;
            int rate = AudioSettings.outputSampleRate;
            if (rate != _app.SampleRate)
            {
                string message = $"cambió la salida de audio ({_app.SampleRate} → {rate} Hz): el audio de la app se cortó ahí";
                _app.NoteError(message);
                if (_tap != null) _tap.Sink = null;
                _app.Close();
                AppStatus = "error: " + message;
                Debug.LogWarning(LogPrefix + message);
            }
            else if (deviceWasChanged)
            {
                _app.NoteError("cambió el dispositivo de salida de audio durante la grabación (puede haber un hueco)");
            }
        }

        private static AudioListener FindListener()
        {
            foreach (AudioListener l in UnityEngine.Object.FindObjectsOfType<AudioListener>())
                if (l.isActiveAndEnabled) return l;
            return null;
        }

        // ---------------- micrófono ----------------

        private void StartMic(string preference)
        {
            string chosen = RecordingAudioDevices.ChooseMic(Microphone.devices, preference, out string description);
            if (chosen == null)
            {
                MicStatus = "error: " + description;
                return;
            }
            _micApiDevice = chosen.Length == 0 ? null : chosen;
            int minRate = 0, maxRate = 0;
            try { Microphone.GetDeviceCaps(_micApiDevice, out minRate, out maxRate); }
            catch (Exception) { minRate = maxRate = 0; }   // predeterminado (null): si no responde, 48 kHz
            int rate = maxRate <= 0 ? PreferredMicRate : Mathf.Clamp(PreferredMicRate, Math.Max(1, minRate), maxRate);
            _micClip = Microphone.Start(_micApiDevice, true, MicClipSeconds, rate);
            if (_micClip == null)
            {
                MicStatus = $"error: no se pudo abrir el micrófono {description}";
                return;
            }
            _micRunning = true;
            int channels = Math.Max(1, _micClip.channels);
            int frequency = _micClip.frequency > 0 ? _micClip.frequency : rate;
            _micBlockFrames = Math.Max(1, frequency / 50);   // 20 ms
            _micBlock = new float[_micBlockFrames * channels];
            _mic = new AudioTrackRecorder("mic", description,
                Path.Combine(_dir, RecordingLayout.MicAudioFile), Path.Combine(_dir, RecordingLayout.MicAudioSyncFile), frequency);
            MicStatus = $"{description} · {frequency} Hz";
        }

        /// <summary>Hilo principal, cada cuadro: lee el micrófono y vuelve a enganchar la app si cambió el AudioListener.</summary>
        public void Pump()
        {
            if (_stopped) return;
            if (_app != null && (_tap == null || !_tap.isActiveAndEnabled) && Time.unscaledTime >= _nextListenerCheck)
            {
                _nextListenerCheck = Time.unscaledTime + ListenerCheckSeconds;
                AudioListener listener = FindListener();
                if (listener != null)
                {
                    AttachTap(listener);
                    _app.NoteError("cambió el AudioListener durante la grabación (puede haber un hueco en el audio de la app)");
                    Debug.LogWarning($"{LogPrefix}audio de la app: nuevo AudioListener {listener.name}");
                }
            }
            try { PumpMic(false); }
            catch (Exception e)
            {
                _mic?.NoteError("lectura del micrófono: " + e.Message);
                StopMic();
            }
        }

        private void PumpMic(bool final)
        {
            if (!_micRunning || _micClip == null || _mic == null) return;
            int length = _micClip.samples;   // cuadros del clip circular
            if (length <= 0) return;
            int pos = Microphone.GetPosition(_micApiDevice);
            if (pos < 0) return;
            if (!_micStarted)
            {
                if (pos == 0) return;   // el dispositivo todavía no entregó nada
                _micStarted = true;
                _micReadPos = 0;
            }
            int available = (pos - _micReadPos + length) % length;
            if (available > 0) _mic.Observe(_micFramesRead + available, PreciseClock.UtcNow);
            int channels = Math.Max(1, _micClip.channels);
            while (available >= _micBlockFrames)
            {
                // GetData lee circular: si el bloque pasa el final del clip, sigue desde el principio.
                _micClip.GetData(_micBlock, _micReadPos);
                _mic.Write(_micBlock, _micBlock.Length, channels);
                _micReadPos = (_micReadPos + _micBlockFrames) % length;
                _micFramesRead += _micBlockFrames;
                available -= _micBlockFrames;
            }
            if (final && available > 0)
            {
                var rest = new float[available * channels];
                _micClip.GetData(rest, _micReadPos);
                _mic.Write(rest, rest.Length, channels);
                _micReadPos = (_micReadPos + available) % length;
                _micFramesRead += available;
            }
        }

        private void StopMic()
        {
            if (!_micRunning) return;
            _micRunning = false;
            try { Microphone.End(_micApiDevice); }
            catch (Exception) { }
            if (_micClip != null) UnityEngine.Object.Destroy(_micClip);
            _micClip = null;
            _mic?.Close();
        }

        /// <summary>Hilo principal: deja de capturar. Los WAV se terminan de escribir en sus hilos.</summary>
        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            if (_configHooked)
            {
                AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
                _configHooked = false;
            }
            if (_tap != null) _tap.Sink = null;   // != de Unity: el listener puede estar destruyéndose
            _app?.Close();
            try { PumpMic(true); }
            catch (Exception e) { _mic?.NoteError("lectura final del micrófono: " + e.Message); }
            StopMic();
            _mic?.Close();
        }

        /// <summary>Cualquier hilo, después de <see cref="Stop"/>: espera los WAV y escribe los *_sync.csv.</summary>
        public bool WaitFinished(int timeoutMs)
        {
            bool ok = true;
            if (_app != null) ok &= _app.Finish(timeoutMs);
            if (_mic != null) ok &= _mic.Finish(timeoutMs);
            return ok;
        }

        /// <summary>Cualquier hilo: pone el estado de las dos pistas en session.json.</summary>
        public void FillInfo(RecordingSessionInfo info)
        {
            info.audioApp = _app != null ? _app.Info() : Missing("app", AppStatus);
            info.audioMic = _mic != null ? _mic.Info() : Missing("mic", MicStatus);
        }

        private static AudioTrackInfo Missing(string track, string status) => new AudioTrackInfo
        {
            track = track,
            device = "",
            file = "",
            syncFile = "",
            startUtc = "",
            firstChunkUtc = "",
            error = status ?? "",
        };
    }
}
