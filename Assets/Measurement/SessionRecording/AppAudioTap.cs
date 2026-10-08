using System;
using UnityEngine;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Va en el mismo GameObject que el AudioListener: Unity le pasa la mezcla final de la app (lo que sale por el
    /// dispositivo de audio, o sea lo que oye el participante en el visor por VIVE Streaming / Link) en el hilo de
    /// audio. No modifica el audio; solo copia hacia la pista de la grabación en curso (<see cref="Sink"/>).
    /// Sin grabación (Sink null) no hace nada.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class AppAudioTap : MonoBehaviour
    {
        private volatile AudioTrackRecorder _sink;
        private volatile bool _faulted;

        internal AudioTrackRecorder Sink
        {
            get => _sink;
            set
            {
                _sink = value;
                _faulted = false;
            }
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            AudioTrackRecorder sink = _sink;
            if (sink == null || _faulted) return;
            try
            {
                sink.WriteObserved(data, channels, PreciseClock.UtcNow);
            }
            catch (Exception e)
            {
                // Hilo de audio: nunca dejar que una excepción se repita en cada buffer.
                _faulted = true;
                sink.NoteError("captura del audio de la app: " + e.Message);
            }
        }
    }
}
