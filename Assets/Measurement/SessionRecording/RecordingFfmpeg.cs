using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace XRCollab.Measurement.Recording
{
    /// <summary>Una pista de audio para meter en el .mp4: WAV, desfase respecto del primer cuadro y corrección de deriva.</summary>
    public sealed class AudioMuxTrack
    {
        public string Name;
        public string FilePath;
        /// <summary>Hora de la primera muestra − hora del primer cuadro del video, en segundos.</summary>
        public double OffsetSeconds;
        /// <summary>Factor de atempo (1 = sin corrección).</summary>
        public double Tempo = 1.0;
    }

    /// <summary>Argumentos de ffmpeg para grabar, probar encoders y armar el video final. Funciones puras (con tests).</summary>
    public static class RecordingFfmpeg
    {
        public static IReadOnlyList<RecordingEncoder> Candidates(RecordingEncoder preferred) =>
            preferred == RecordingEncoder.Auto
                ? new[] { RecordingEncoder.Nvenc, RecordingEncoder.Qsv, RecordingEncoder.X264 }
                : new[] { preferred };

        public static string CodecName(RecordingEncoder encoder)
        {
            switch (encoder)
            {
                case RecordingEncoder.Nvenc: return "h264_nvenc";
                case RecordingEncoder.Qsv: return "h264_qsv";
                case RecordingEncoder.X264: return "libx264";
                default: throw new ArgumentOutOfRangeException(nameof(encoder), "Auto no es un encoder concreto");
            }
        }

        /// <summary>
        /// H.264 con un keyframe cada 2 s y sin B-frames (cortes de segmento limpios). Calidad constante: el texto
        /// chico de los HUD queda legible y una ventana quieta casi no pesa.
        /// NVENC recibe RGBA y lo convierte a yuv420p en la GPU (High, compatible con cualquier reproductor).
        /// </summary>
        public static string EncoderArguments(RecordingEncoder encoder, int fps)
        {
            int gop = Math.Max(1, fps * 2);
            switch (encoder)
            {
                case RecordingEncoder.Nvenc:
                    return $"-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq 27 -b:v 0 -maxrate 10M -bufsize 20M -g {gop} -bf 0";
                case RecordingEncoder.Qsv:
                    return $"-c:v h264_qsv -preset veryfast -global_quality 27 -g {gop} -bf 0";
                case RecordingEncoder.X264:
                    return $"-c:v libx264 -preset veryfast -crf 26 -g {gop} -bf 0 -pix_fmt yuv420p";
                default:
                    throw new ArgumentOutOfRangeException(nameof(encoder));
            }
        }

        /// <summary>Codifica 0,2 s de negro: si falla (sin GPU compatible, driver viejo), se prueba el siguiente encoder.</summary>
        public static string ProbeArguments(RecordingEncoder encoder) =>
            "-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x144:r=30 -t 0.2 -pix_fmt rgba " +
            EncoderArguments(encoder, 30) + " -f null -";

        /// <summary>
        /// Cuadros RGBA crudos por stdin → H.264 en segmentos MPEG-TS. Cada cuadro se marca con la hora de llegada
        /// (<c>use_wallclock_as_timestamps</c>): si la app baja de fps (visor sacado), el video conserva el tiempo real.
        /// MPEG-TS se puede reproducir aunque el proceso muera a mitad: un corte pierde como mucho el final del
        /// segmento en curso.
        /// </summary>
        public static string RecordArguments(RecordingEncoder encoder, int width, int height, int fps, int segmentSeconds,
            string segmentPattern, int startNumber)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            return "-hide_banner -loglevel error " +
                   $"-use_wallclock_as_timestamps 1 -f rawvideo -pix_fmt rgba -video_size {width}x{height} -framerate {fps} -i pipe:0 " +
                   EncoderArguments(encoder, fps) + " -fps_mode passthrough " +
                   $"-f segment -segment_time {segmentSeconds} -segment_format mpegts -reset_timestamps 1 " +
                   $"-segment_start_number {startNumber} {Quote(segmentPattern)}";
        }

        /// <summary>Lista para el concat demuxer de ffmpeg (<c>-f concat -safe 0</c>): una línea <c>file '...'</c> por segmento.</summary>
        public static string ConcatList(IEnumerable<string> segmentPaths)
        {
            var sb = new StringBuilder();
            foreach (string path in segmentPaths)
                sb.Append("file '").Append(path.Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
            return sb.ToString();
        }

        /// <summary>
        /// Línea de <c>cmd /d /s /c</c> que arma el video final sin recodificar (copia de stream, segundos) y recién al
        /// terminar bien lo renombra al nombre final: si se corta a la mitad, no queda un .mp4 roto con nombre final.
        /// </summary>
        /// <param name="then">
        /// Opcional: otro paso que corre solo si el video final quedó bien (ej. <see cref="AudioMuxCommandPart"/>).
        /// </param>
        public static string FinalizeCommandLine(string ffmpegPath, string listPath, string partialPath, string finalPath, string then = null) =>
            "/d /s /c \"" +
            $"{Quote(ffmpegPath)} -hide_banner -loglevel error -f concat -safe 0 -i {Quote(listPath)} -c copy -movflags +faststart -f mp4 -y {Quote(partialPath)}" +
            $" && move /y {Quote(partialPath)} {Quote(finalPath)} >nul" +
            (string.IsNullOrEmpty(then) ? "" : " && " + then) +
            "\"";

        /// <summary>Línea de <c>cmd /d /s /c</c> que solo mete el audio en un .mp4 final que ya existe.</summary>
        public static string AudioMuxOnlyCommandLine(string muxPart) => "/d /s /c \"" + muxPart + "\"";

        // ---------------- audio ----------------

        /// <summary>
        /// Paso de cmd que mete el audio: escribe <paramref name="avPartialPath"/> y recién si ffmpeg terminó bien lo
        /// mueve sobre el .mp4 final (que hasta ahí es solo video) y deja <c>audio_mux.done</c>. Si algo falla, el
        /// .mp4 solo video y los WAV quedan intactos, el error en <paramref name="logPath"/> y una marca
        /// <paramref name="failedPath"/>. Va entre paréntesis para que el <c>||</c> no alcance a los pasos de antes.
        /// </summary>
        public static string AudioMuxCommandPart(string ffmpegPath, string muxArguments, string avPartialPath, string finalPath,
            string logPath, string donePath, string failedPath) =>
            $"({Quote(ffmpegPath)} {muxArguments} 2>{Quote(logPath)}" +
            $" && move /y {Quote(avPartialPath)} {Quote(finalPath)} >nul" +
            $" && echo ok>{Quote(donePath)}" +
            $" || echo failed>{Quote(failedPath)})";

        /// <summary>
        /// Argumentos de ffmpeg: video copiado tal cual + una pista AAC por WAV (nombres «app», «mic»), cada una
        /// alineada al primer cuadro del video (<see cref="AudioFilterChain"/>).
        /// </summary>
        public static string AudioMuxArguments(string videoPath, IReadOnlyList<AudioMuxTrack> tracks, string outputPath)
        {
            if (tracks == null || tracks.Count == 0) throw new ArgumentException("sin pistas de audio", nameof(tracks));
            var sb = new StringBuilder("-hide_banner -loglevel error -i ").Append(Quote(videoPath));
            foreach (AudioMuxTrack t in tracks) sb.Append(" -i ").Append(Quote(t.FilePath));
            sb.Append(" -filter_complex \"");
            for (int i = 0; i < tracks.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append('[').Append(i + 1).Append(":a]").Append(AudioFilterChain(tracks[i].OffsetSeconds, tracks[i].Tempo)).Append("[a").Append(i).Append(']');
            }
            sb.Append("\" -map 0:v:0");
            for (int i = 0; i < tracks.Count; i++) sb.Append(" -map \"[a").Append(i).Append("]\"");
            sb.Append(" -c:v copy -c:a aac -b:a 160k");
            for (int i = 0; i < tracks.Count; i++)
            {
                string name = SafeName(tracks[i].Name);
                sb.Append($" -metadata:s:a:{i} title={name} -metadata:s:a:{i} handler_name={name}");
                sb.Append($" -disposition:a:{i} {(i == 0 ? "default" : "0")}");
            }
            sb.Append(" -movflags +faststart -f mp4 -y ").Append(Quote(outputPath));
            return sb.ToString();
        }

        /// <summary>
        /// Filtro de una pista: primero corrige la deriva del reloj de audio (<c>atempo</c>, solo si
        /// <paramref name="tempo"/> ≠ 1), después la alinea: desfase positivo (el audio empezó después del primer
        /// cuadro) = silencio al principio (<c>adelay</c>); negativo = se corta el principio (<c>atrim</c>).
        /// </summary>
        public static string AudioFilterChain(double offsetSeconds, double tempo)
        {
            var parts = new List<string>();
            if (!double.IsNaN(tempo) && tempo > 0 && Math.Abs(tempo - 1.0) >= 1e-6)
                parts.Add("atempo=" + tempo.ToString("F8", CultureInfo.InvariantCulture));
            if (offsetSeconds >= 0.0005)
                parts.Add("adelay=delays=" + Math.Round(offsetSeconds * 1000.0).ToString("F0", CultureInfo.InvariantCulture) + ":all=1");
            else if (offsetSeconds <= -0.0005)
            {
                parts.Add("atrim=start=" + (-offsetSeconds).ToString("F6", CultureInfo.InvariantCulture));
                parts.Add("asetpts=PTS-STARTPTS");
            }
            return parts.Count == 0 ? "anull" : string.Join(",", parts);
        }

        /// <summary>
        /// Factor de <c>atempo</c> para llevar el audio al reloj del sistema (el del video): frecuencia medida /
        /// nominal. 1 si la grabación es corta (la medición no es confiable) o si la diferencia es despreciable
        /// (&lt; 10 ppm) o absurda (&gt; 0,2 %, algo anduvo mal: mejor no tocar).
        /// </summary>
        public static double DriftTempo(double nominalRate, double effectiveRate, double seconds)
        {
            if (nominalRate <= 0 || effectiveRate <= 0 || seconds < 30) return 1.0;
            double tempo = effectiveRate / nominalRate;
            double d = Math.Abs(tempo - 1.0);
            return d < 1e-5 || d > 2e-3 ? 1.0 : tempo;
        }

        private static string SafeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "") if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            return sb.Length == 0 ? "audio" : sb.ToString();
        }

        private static string Quote(string path) => "\"" + path + "\"";

        /// <summary>Tamaño de salida: ancho fijo, alto según la proporción de la ventana, ambos pares.</summary>
        public static (int width, int height) OutputSize(int screenWidth, int screenHeight, int targetWidth)
        {
            if (screenWidth <= 0 || screenHeight <= 0) throw new ArgumentOutOfRangeException(nameof(screenWidth));
            int w = Math.Min(targetWidth, screenWidth) & ~1;
            int h = Math.Max(2, (int)Math.Round((double)w * screenHeight / screenWidth) & ~1);
            return (Math.Max(2, w), h);
        }

        public static string Describe(IEnumerable<RecordingEncoder> encoders) => string.Join(", ", encoders.Select(CodecName));
    }
}
