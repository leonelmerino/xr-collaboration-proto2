using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Dónde queda cada grabación. Una carpeta por apertura de la app:
    /// <code>
    ///   Recordings/20261001_102345_STIMULUS1/
    ///     segments/seg_00000.ts, seg_00001.ts ...   pedazos de 60 s (respaldo; se conservan siempre)
    ///     session.json                             PC, hora de inicio, tamaño, fps, encoder, cierre limpio
    ///     20261001_102345_STIMULUS1.mp4            video completo: al cerrar normal, o al próximo arranque
    /// </code>
    /// </summary>
    public static class RecordingLayout
    {
        public const string SegmentsFolder = "segments";
        public const string InfoFile = "session.json";
        public const string ConcatListFile = "concat.txt";

        public static string SessionName(DateTime localStart, string computerName) =>
            $"{localStart:yyyyMMdd_HHmmss}_{computerName}";

        public static string SegmentPattern(string sessionDir) => Path.Combine(sessionDir, SegmentsFolder, "seg_%05d.ts");

        public static string FinalVideoPath(string sessionDir) => Path.Combine(sessionDir, Path.GetFileName(sessionDir.TrimEnd('\\', '/')) + ".mp4");

        public static string PartialVideoPath(string sessionDir) => FinalVideoPath(sessionDir) + ".partial";

        /// <summary>Segmentos con datos, en orden.</summary>
        public static IReadOnlyList<string> Segments(string sessionDir)
        {
            string dir = Path.Combine(sessionDir, SegmentsFolder);
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "seg_*.ts")
                .Where(p => new FileInfo(p).Length > 0)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Número para el próximo segmento (si ffmpeg se reinicia, sigue la numeración sin pisar nada).</summary>
        public static int NextSegmentNumber(string sessionDir)
        {
            string dir = Path.Combine(sessionDir, SegmentsFolder);
            if (!Directory.Exists(dir)) return 0;
            int max = -1;
            foreach (string f in Directory.GetFiles(dir, "seg_*.ts"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f).Substring(4), out int n)) max = Math.Max(max, n);
            return max + 1;
        }

        /// <summary>Sesiones con segmentos pero sin video final (la app se cerró mal o se cortó el armado).</summary>
        public static IEnumerable<string> PendingSessions(string root, string exceptSessionDir)
        {
            if (!Directory.Exists(root)) yield break;
            string except = exceptSessionDir == null ? null : Path.GetFullPath(exceptSessionDir).TrimEnd('\\', '/');
            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (except != null && string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), except, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(FinalVideoPath(dir))) continue;
                if (Segments(dir).Count > 0) yield return dir;
            }
        }
    }

    /// <summary>Contenido de session.json.</summary>
    [Serializable]
    public sealed class RecordingSessionInfo
    {
        public string computer;
        public string startedUtc;
        public string startedLocal;
        public string utcOffset;
        public int width;
        public int height;
        public int fps;
        public int segmentSeconds;
        public string encoder;
        public string stoppedUtc;
        public bool closedCleanly;
        public long framesCaptured;
        public long framesDropped;
        public string note;
    }
}
