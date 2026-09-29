using System;
using System.Globalization;
using UnityEngine;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>Qué parte de la pantalla del visor se muestra en la ventana del PC.</summary>
    public enum MirrorLayout
    {
        /// <summary>Los dos ojos lado a lado, como la pantalla física del visor.</summary>
        BothEyes,

        /// <summary>Solo el ojo izquierdo: imagen más grande para quien observa desde el PC.</summary>
        LeftEye,
    }

    /// <summary>
    /// Configuración del espejo del visor. Los valores por defecto sirven en el laboratorio y cada uno se
    /// puede cambiar al lanzar el build, sin recompilar (ver <see cref="FromCommandLine"/>).
    /// </summary>
    [Serializable]
    public sealed class HeadsetMirrorSettings
    {
        public const int MinSize = 320;
        public const int MaxSizeLimit = 4096;

        [Tooltip("Desactiva el espejo por completo (-nomirror).")]
        public bool enabled = true;

        [Tooltip("Serial adb del visor. Vacío: el primer visor autorizado (-mirror-serial).")]
        public string deviceSerial = "";

        [Tooltip("Lado mayor del video en píxeles (-mirror-size). La pantalla del visor es 2:1: 1280 da 1280x640.")]
        public int maxSize = 1280;

        [Tooltip("Tope de cuadros por segundo del encoder del visor (-mirror-fps).")]
        public int maxFps = 30;

        [Tooltip("Bitrate H.264 en bit/s (-mirror-bitrate, acepta 6M o 6000k). Viaja por el mismo USB que VIVE Streaming.")]
        public int videoBitRate = 6_000_000;

        [Tooltip("Vista inicial (-mirror-layout both|left).")]
        public MirrorLayout layout = MirrorLayout.BothEyes;

        [Tooltip("Mostrar la imagen al arrancar (-mirror-hidden la deja oculta).")]
        public bool visibleOnStart = true;

        public KeyCode toggleKey = KeyCode.F5;
        public KeyCode layoutKey = KeyCode.F6;

        [Tooltip("Ruta de adb (-mirror-adb). Vacío: el de VIVE Hub. Otra versión de adb mata el servidor adb de VIVE Hub.")]
        public string adbPath = "";

        [Tooltip("Ruta de ffmpeg (-mirror-ffmpeg). Vacío: PATH y WinGet.")]
        public string ffmpegPath = "";

        [Tooltip("Segundos sin imagen antes de reconectar.")]
        public float stallTimeoutSeconds = 10f;

        [Tooltip("Espera entre reintentos fallidos: empieza en el mínimo y se duplica hasta el máximo.")]
        public float retryMinSeconds = 1f;
        public float retryMaxSeconds = 10f;

        public HeadsetMirrorSettings Clone() => (HeadsetMirrorSettings)MemberwiseClone();

        /// <summary>Lleva los valores a rangos válidos (tamaño par entre límites, fps y bitrate razonables).</summary>
        public void Validate()
        {
            maxSize = Mathf.Clamp(maxSize, MinSize, MaxSizeLimit) & ~1;
            maxFps = Mathf.Clamp(maxFps, 1, 90);
            videoBitRate = Mathf.Clamp(videoBitRate, 500_000, 50_000_000);
            stallTimeoutSeconds = Mathf.Max(2f, stallTimeoutSeconds);
            retryMinSeconds = Mathf.Max(0.1f, retryMinSeconds);
            retryMaxSeconds = Mathf.Max(retryMinSeconds, retryMaxSeconds);
            deviceSerial = deviceSerial?.Trim() ?? "";
            adbPath = adbPath?.Trim() ?? "";
            ffmpegPath = ffmpegPath?.Trim() ?? "";
        }

        /// <summary>
        /// Aplica los argumentos de lanzamiento sobre <paramref name="defaults"/> (o los valores por defecto):
        /// <c>-nomirror</c>, <c>-mirror-hidden</c>, <c>-mirror-serial S</c>, <c>-mirror-size N</c>,
        /// <c>-mirror-fps N</c>, <c>-mirror-bitrate N|6M|6000k</c>, <c>-mirror-layout both|left</c>,
        /// <c>-mirror-adb RUTA</c>, <c>-mirror-ffmpeg RUTA</c>. Un valor inválido se ignora y queda el anterior.
        /// </summary>
        public static HeadsetMirrorSettings FromCommandLine(string[] args, HeadsetMirrorSettings defaults = null)
        {
            HeadsetMirrorSettings s = (defaults ?? new HeadsetMirrorSettings()).Clone();
            args ??= Array.Empty<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i].ToLowerInvariant())
                {
                    case "-nomirror": s.enabled = false; break;
                    case "-mirror-hidden": s.visibleOnStart = false; break;
                    case "-mirror-serial": if (TakeText(value, ref s.deviceSerial)) i++; break;
                    case "-mirror-adb": if (TakeText(value, ref s.adbPath)) i++; break;
                    case "-mirror-ffmpeg": if (TakeText(value, ref s.ffmpegPath)) i++; break;
                    case "-mirror-size": if (TryParseInt(value, out s.maxSize, s.maxSize)) i++; break;
                    case "-mirror-fps": if (TryParseInt(value, out s.maxFps, s.maxFps)) i++; break;
                    case "-mirror-bitrate": if (TryParseBitRate(value, out s.videoBitRate, s.videoBitRate)) i++; break;
                    case "-mirror-layout": if (TryParseLayout(value, out s.layout, s.layout)) i++; break;
                }
            }
            s.Validate();
            return s;
        }

        private static bool TakeText(string value, ref string target)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal)) return false;
            target = value;
            return true;
        }

        private static bool TryParseInt(string value, out int result, int fallback)
        {
            bool ok = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            if (!ok) result = fallback;
            return ok;
        }

        /// <summary>Bits por segundo: entero, o con sufijo k/M (6M = 6000000).</summary>
        public static bool TryParseBitRate(string value, out int result, int fallback)
        {
            result = fallback;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            double multiplier = 1;
            char last = char.ToLowerInvariant(v[v.Length - 1]);
            if (last == 'k') multiplier = 1_000;
            else if (last == 'm') multiplier = 1_000_000;
            if (multiplier > 1) v = v.Substring(0, v.Length - 1);
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number <= 0) return false;
            double bits = number * multiplier;
            if (bits > int.MaxValue) return false;
            result = (int)bits;
            return true;
        }

        private static bool TryParseLayout(string value, out MirrorLayout result, MirrorLayout fallback)
        {
            switch (value?.ToLowerInvariant())
            {
                case "both": result = MirrorLayout.BothEyes; return true;
                case "left": result = MirrorLayout.LeftEye; return true;
                default: result = fallback; return false;
            }
        }
    }
}
