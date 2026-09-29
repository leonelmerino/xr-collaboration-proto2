using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XRCollab.Measurement.Recording
{
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
        public static string FinalizeCommandLine(string ffmpegPath, string listPath, string partialPath, string finalPath) =>
            "/d /s /c \"" +
            $"{Quote(ffmpegPath)} -hide_banner -loglevel error -f concat -safe 0 -i {Quote(listPath)} -c copy -movflags +faststart -f mp4 -y {Quote(partialPath)}" +
            $" && move /y {Quote(partialPath)} {Quote(finalPath)} >nul" +
            "\"";

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
