using System;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>
    /// Todo lo que depende de la versión de scrcpy-server, en un solo lugar. El archivo
    /// StreamingAssets/HeadsetMirror/scrcpy-server tiene que ser exactamente <see cref="ServerVersion"/>:
    /// el servidor rechaza un cliente de otra versión (el error aparece en el log con el prefijo "server:").
    /// </summary>
    public static class ScrcpyProtocol
    {
        public const string ServerVersion = "4.1";

        /// <summary>Ruta relativa a Application.streamingAssetsPath.</summary>
        public const string ServerAssetPath = "HeadsetMirror/scrcpy-server";

        /// <summary>Nombre propio para no chocar con el scrcpy de escritorio (tools\launch\mirror-headset.vbs).</summary>
        public const string DeviceJarPath = "/data/local/tmp/xrcollab-mirror.jar";

        /// <summary>El servidor escribe esta línea justo antes de abrir su socket.</summary>
        public const string ReadyLogMarker = "INFO: Device:";

        /// <summary>Identificador de sesión: 31 bits en 8 dígitos hex, como el cliente oficial.</summary>
        public static string NewSessionId(Random rng) => rng.Next(0x10000000, int.MaxValue).ToString("x8");

        /// <summary>Socket abstracto del servidor en el visor (destino del adb forward).</summary>
        public static string SocketName(string sessionId) => "scrcpy_" + sessionId;

        /// <summary>
        /// Comando de shell que lanza el servidor en el visor: solo video, H.264 crudo sin encabezados
        /// (raw_stream), sin audio ni control, y con el túnel en modo forward (el PC se conecta al visor).
        /// </summary>
        public static string ServerCommand(string sessionId, int maxSize, int maxFps, int videoBitRate) =>
            $"CLASSPATH={DeviceJarPath} app_process / com.genymobile.scrcpy.Server {ServerVersion} " +
            $"scid={sessionId} log_level=info tunnel_forward=true audio=false control=false cleanup=true " +
            $"raw_stream=true max_size={maxSize} max_fps={maxFps} video_bit_rate={videoBitRate}";
    }

    /// <summary>Argumentos de ffmpeg para decodificar el H.264 del visor.</summary>
    public static class FfmpegDecoder
    {
        /// <summary>
        /// H.264 crudo desde el túnel TCP → RGBA de tamaño fijo por stdout. <c>vflip</c> deja las filas de abajo
        /// hacia arriba, el orden de <c>Texture2D.LoadRawTextureData</c>, así la vista no invierte nada.
        /// No agregar <c>-fflags nobuffer</c> ni un <c>-probesize</c> chico: con eso ffmpeg decodifica un solo
        /// cuadro ("Missing reference picture") y se queda esperando (visto el 2026-09-29, hay un test).
        /// </summary>
        public static string Arguments(int port, int width, int height) =>
            "-hide_banner -loglevel error -nostdin -flags low_delay " +
            $"-f h264 -i tcp://127.0.0.1:{port} " +
            $"-vf scale={width}:{height},vflip -pix_fmt rgba -f rawvideo pipe:1";
    }

    public static class FrameGeometry
    {
        /// <summary>
        /// Tamaño del cuadro para una pantalla de <paramref name="displayWidth"/>x<paramref name="displayHeight"/>:
        /// misma proporción, lado mayor como máximo <paramref name="maxSize"/>, lados pares (lo exige el decoder).
        /// </summary>
        public static (int width, int height) Fit(int displayWidth, int displayHeight, int maxSize)
        {
            if (displayWidth <= 0) throw new ArgumentOutOfRangeException(nameof(displayWidth));
            if (displayHeight <= 0) throw new ArgumentOutOfRangeException(nameof(displayHeight));
            if (maxSize < 2) throw new ArgumentOutOfRangeException(nameof(maxSize));
            double scale = Math.Min(1.0, (double)maxSize / Math.Max(displayWidth, displayHeight));
            int w = Math.Max(2, (int)Math.Round(displayWidth * scale) & ~1);
            int h = Math.Max(2, (int)Math.Round(displayHeight * scale) & ~1);
            return (w, h);
        }
    }
}
