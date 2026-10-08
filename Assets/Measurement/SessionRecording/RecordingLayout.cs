using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Dónde queda cada grabación. Una carpeta por «start» (o por apertura de la app con -rec-auto):
    /// <code>
    ///   Recordings/20261001_150005_STIMULUS1_S01/
    ///     segments/seg_00000.ts, seg_00001.ts ...   pedazos de 60 s (respaldo; se conservan siempre)
    ///     session.json                             PC, label, horas (pedida, programada, primer cuadro), encoder, cierre
    ///     frames.csv                               una fila por cuadro del video: hora UTC exacta de captura
    ///     marks.csv                                marcas («mark ...») durante la grabación
    ///     audio_app.wav / audio_mic.wav            audio de la app (lo que oye el participante) y del micrófono del visor
    ///     audio_app_sync.csv / audio_mic_sync.csv  muestra de audio ↔ hora UTC (una fila por segundo)
    ///     audio_mux.log / audio_mux.done|failed    resultado de meter el audio en el .mp4
    ///     20261001_150005_STIMULUS1_S01.mp4        video completo (+ pistas de audio «app» y «mic»): al detener, o al
    ///                                              próximo arranque si se cortó
    /// </code>
    /// </summary>
    public static class RecordingLayout
    {
        public const string SegmentsFolder = "segments";
        public const string InfoFile = "session.json";
        public const string ConcatListFile = "concat.txt";
        public const string FramesFile = "frames.csv";
        public const string MarksFile = "marks.csv";
        public const string FramesHeader = "video_frame,capture_utc_iso,capture_unix_ms,unity_frame,unity_realtime_s";
        public const string MarksHeader = "mark_utc_iso,mark_unix_ms,unity_frame,computer,recording_label,text";
        public const string AppAudioFile = "audio_app.wav";
        public const string MicAudioFile = "audio_mic.wav";
        public const string AppAudioSyncFile = "audio_app_sync.csv";
        public const string MicAudioSyncFile = "audio_mic_sync.csv";
        public const string AudioSyncHeader = "audio_frame,utc_iso,unix_ms";
        public const string AudioMuxLogFile = "audio_mux.log";
        public const string AudioMuxDoneFile = "audio_mux.done";
        public const string AudioMuxFailedFile = "audio_mux.failed";

        /// <summary>Hora UTC con µs (session.json de audio, *_sync.csv).</summary>
        public static string FormatUtcMicros(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

        public static string AudioSyncRow(long frame, DateTime utc) =>
            string.Join(",",
                frame.ToString(CultureInfo.InvariantCulture),
                FormatUtcMicros(utc),
                PreciseClock.ToUnixMs(utc).ToString("F3", CultureInfo.InvariantCulture));

        /// <summary>Video con audio a medio armar (se renombra al .mp4 final solo si ffmpeg terminó bien).</summary>
        public static string AudioMuxPartialPath(string sessionDir) => FinalVideoPath(sessionDir) + ".av.partial";

        public static string SessionName(DateTime localStart, string computerName) =>
            $"{localStart:yyyyMMdd_HHmmss}_{computerName}";

        public static string SessionName(DateTime localStart, string computerName, string label) =>
            string.IsNullOrEmpty(label) ? SessionName(localStart, computerName) : $"{SessionName(localStart, computerName)}_{label}";

        /// <summary>Carpeta nueva: si ya existe (dos «start» en el mismo segundo), agrega _2, _3...</summary>
        public static string UniqueSessionDir(string root, string name)
        {
            string dir = Path.Combine(root, name);
            for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(root, $"{name}_{i}");
            return dir;
        }

        public static string FramesRow(long videoFrame, DateTime captureUtc, int unityFrame, double unityTime) =>
            string.Join(",",
                videoFrame.ToString(CultureInfo.InvariantCulture),
                captureUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
                PreciseClock.ToUnixMs(captureUtc).ToString("F3", CultureInfo.InvariantCulture),
                unityFrame.ToString(CultureInfo.InvariantCulture),
                unityTime.ToString("F6", CultureInfo.InvariantCulture));

        public static string MarksRow(DateTime utc, int unityFrame, string computer, string label, string text) =>
            string.Join(",",
                utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
                PreciseClock.ToUnixMs(utc).ToString("F3", CultureInfo.InvariantCulture),
                unityFrame.ToString(CultureInfo.InvariantCulture),
                computer, label, text);

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
        public string label;
        /// <summary>Cuándo llegó el «start» (UTC, ms).</summary>
        public string requestedUtc;
        /// <summary>Hora programada de inicio (la misma en todos los PCs de una sesión).</summary>
        public string startAtUtc;
        /// <summary>Hora real del primer cuadro del video (fila 0 de frames.csv).</summary>
        public string firstFrameUtc;
        public string lastFrameUtc;
        public string stopRequestedUtc;
        public string framesFile;
        public long framesWritten;
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
        /// <summary>Audio de la app (lo que oye el participante). file vacío = no se grabó (ver error).</summary>
        public AudioTrackInfo audioApp;
        /// <summary>Micrófono del visor. file vacío = no se grabó (ver error).</summary>
        public AudioTrackInfo audioMic;
    }

    /// <summary>
    /// Una pista de audio en session.json. <see cref="startUtc"/> es la hora UTC estimada de la primera muestra del
    /// WAV (ajuste lineal muestra↔hora sobre toda la grabación); el desfase con el video es
    /// <c>startUtc − firstFrameUtc</c>. <see cref="effectiveRate"/> es la frecuencia real medida contra el reloj del
    /// sistema (deriva del reloj de audio).
    /// </summary>
    [Serializable]
    public sealed class AudioTrackInfo
    {
        public string track;
        public string device;
        public string file;
        public string syncFile;
        public int sampleRate;
        public int channels;
        public string startUtc;
        public string firstChunkUtc;
        public double effectiveRate;
        public long frames;
        public double seconds;
        public long framesSilenced;
        public int syncPoints;
        public string error;
    }
}
