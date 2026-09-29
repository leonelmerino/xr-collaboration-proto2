using System;
using System.Globalization;
using UnityEngine;

namespace XRCollab.Measurement.Recording
{
    /// <summary>Encoder de video. Auto prueba en orden NVENC (GPU NVIDIA), QuickSync (GPU Intel) y x264 (CPU).</summary>
    public enum RecordingEncoder
    {
        Auto,
        Nvenc,
        Qsv,
        X264,
    }

    /// <summary>
    /// Configuración de la grabación de la ventana del PC. Los valores por defecto sirven en el laboratorio; cada uno
    /// se puede cambiar al lanzar el build: <c>-norecord</c>, <c>-rec-fps N</c>, <c>-rec-width N</c>,
    /// <c>-rec-segment S</c>, <c>-rec-encoder nvenc|qsv|x264</c>, <c>-rec-dir RUTA</c>, <c>-rec-ffmpeg RUTA</c>.
    /// </summary>
    [Serializable]
    public sealed class SessionRecorderSettings
    {
        public bool enabled = true;

        [Tooltip("Cuadros por segundo del video (-rec-fps).")]
        public int fps = 30;

        [Tooltip("Ancho del video en píxeles (-rec-width). El alto sale de la proporción de la ventana.")]
        public int width = 1920;

        [Tooltip("Duración de cada pedazo de video en segundos (-rec-segment). Es lo máximo que se pierde si algo falla.")]
        public int segmentSeconds = 60;

        public RecordingEncoder encoder = RecordingEncoder.Auto;

        [Tooltip("Carpeta raíz (-rec-dir). Vacío: <persistentDataPath>/Recordings, junto a la telemetría.")]
        public string outputRoot = "";

        [Tooltip("Ruta de ffmpeg (-rec-ffmpeg). Vacío: PATH y WinGet.")]
        public string ffmpegPath = "";

        public SessionRecorderSettings Clone() => (SessionRecorderSettings)MemberwiseClone();

        public void Validate()
        {
            fps = Mathf.Clamp(fps, 5, 60);
            width = Mathf.Clamp(width, 640, 3840) & ~1;
            segmentSeconds = Mathf.Clamp(segmentSeconds, 10, 600);
            outputRoot = outputRoot?.Trim() ?? "";
            ffmpegPath = ffmpegPath?.Trim() ?? "";
        }

        public static SessionRecorderSettings FromCommandLine(string[] args, SessionRecorderSettings defaults = null)
        {
            SessionRecorderSettings s = (defaults ?? new SessionRecorderSettings()).Clone();
            args ??= Array.Empty<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i].ToLowerInvariant())
                {
                    case "-norecord": s.enabled = false; break;
                    case "-rec-fps": if (TryInt(value, ref s.fps)) i++; break;
                    case "-rec-width": if (TryInt(value, ref s.width)) i++; break;
                    case "-rec-segment": if (TryInt(value, ref s.segmentSeconds)) i++; break;
                    case "-rec-dir": if (TryText(value, ref s.outputRoot)) i++; break;
                    case "-rec-ffmpeg": if (TryText(value, ref s.ffmpegPath)) i++; break;
                    case "-rec-encoder": if (TryEncoder(value, ref s.encoder)) i++; break;
                }
            }
            s.Validate();
            return s;
        }

        private static bool TryInt(string value, ref int target)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return false;
            target = v;
            return true;
        }

        private static bool TryText(string value, ref string target)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal)) return false;
            target = value;
            return true;
        }

        private static bool TryEncoder(string value, ref RecordingEncoder target)
        {
            switch (value?.ToLowerInvariant())
            {
                case "auto": target = RecordingEncoder.Auto; return true;
                case "nvenc": target = RecordingEncoder.Nvenc; return true;
                case "qsv": target = RecordingEncoder.Qsv; return true;
                case "x264": target = RecordingEncoder.X264; return true;
                default: return false;
            }
        }
    }
}
