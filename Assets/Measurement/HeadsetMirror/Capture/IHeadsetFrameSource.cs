using System;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>En qué parte del ciclo de vida está la fuente.</summary>
    public enum MirrorState
    {
        Stopped,
        /// <summary>Falta adb, ffmpeg o el servidor de scrcpy. Se reintenta por si se instalan.</summary>
        MissingTools,
        /// <summary>No hay un visor listo por adb (desconectado, sin autorizar).</summary>
        WaitingForDevice,
        /// <summary>El visor está conectado pero en reposo (nadie lo lleva puesto): su pantalla no produce imagen.</summary>
        DeviceAsleep,
        Connecting,
        Streaming,
        /// <summary>Se cortó la imagen o falló la sesión; se vuelve a intentar con espera creciente.</summary>
        Reconnecting,
    }

    /// <summary>Por qué la fuente está en su estado actual. Es lo que usa <see cref="MirrorDiagnosis"/> para explicar una pantalla negra.</summary>
    public enum MirrorIssue
    {
        None,
        AdbMissing,
        FfmpegMissing,
        ServerAssetMissing,
        /// <summary>adb no ve ningún visor (cable desconectado, o conectado solo por Wi-Fi).</summary>
        NoDevice,
        /// <summary>El visor está conectado pero no aceptó la depuración USB.</summary>
        DeviceUnauthorized,
        /// <summary>Se pidió un serial con -mirror-serial y ese visor no está.</summary>
        DeviceNotFound,
        /// <summary>adb mismo falló (servidor adb caído o colgado).</summary>
        AdbFailed,
        DeviceAsleep,
        /// <summary>La sesión arrancó pero el visor nunca mandó un cuadro.</summary>
        NoImage,
        /// <summary>Había imagen y se cortó (cable, reposo, proceso caído).</summary>
        StreamCut,
        /// <summary>Error inesperado al armar la sesión (el detalle trae el mensaje).</summary>
        SessionFailed,
    }

    /// <summary>Si el visor está sobre la cara (sensor de proximidad). Solo se consulta cuando la imagen se ve mal.</summary>
    public enum HeadsetWear
    {
        Unknown,
        OnFace,
        /// <summary>En la frente o sobre la mesa: VIVE Streaming pausa la imagen y muestra su pantalla de espera.</summary>
        OffFace,
    }

    /// <summary>
    /// Instantánea inmutable del estado de una fuente. Se reemplaza entera en cada cambio, así que se puede
    /// leer desde cualquier hilo sin locks.
    /// </summary>
    public sealed class MirrorStatus
    {
        public static readonly MirrorStatus Stopped = new MirrorStatus(MirrorState.Stopped, MirrorIssue.None, "detenido");

        public MirrorState State { get; }
        public MirrorIssue Issue { get; }
        public string Detail { get; }
        public string DeviceSerial { get; }

        public MirrorStatus(MirrorState state, MirrorIssue issue, string detail, string deviceSerial = null)
        {
            State = state;
            Issue = issue;
            Detail = detail ?? "";
            DeviceSerial = deviceSerial;
        }

        public override string ToString() =>
            DeviceSerial == null ? $"{State}: {Detail}" : $"{State} ({DeviceSerial}): {Detail}";
    }

    /// <summary>
    /// Fuente de cuadros de la pantalla del visor. La implementación actual es <see cref="ScrcpyFrameSource"/>
    /// (adb + scrcpy-server + ffmpeg). La interfaz permite otras fuentes (Wi-Fi, una app nativa en el visor)
    /// sin tocar <see cref="HeadsetMirror"/> ni <see cref="HeadsetMirrorView"/>.
    /// Hilos: Start/Stop y las propiedades se usan desde el hilo principal; la fuente produce en su propio hilo.
    /// </summary>
    public interface IHeadsetFrameSource : IDisposable
    {
        MirrorStatus Status { get; }

        /// <summary>Buffer de la sesión actual, o null mientras no hay imagen. Cambia al reconectar (el tamaño puede cambiar).</summary>
        FrameTripleBuffer Frames { get; }

        /// <summary>Cuadros recibidos desde que se creó la fuente (diagnóstico).</summary>
        long FramesReceived { get; }

        /// <summary>Segundos desde el último cuadro de la sesión actual; infinito si todavía no llegó ninguno.</summary>
        double SecondsSinceLastFrame { get; }

        /// <summary>Segundos seguidos con cuadros casi negros (ver <see cref="FrameAnalysis"/>); 0 si el último tiene imagen.</summary>
        double SecondsDark { get; }

        /// <summary>Si el visor está puesto, cuando la imagen está negra o detenida; Unknown si la imagen está bien o no se pudo saber.</summary>
        HeadsetWear Wear { get; }

        void Start();
        void Stop();
    }

    /// <summary>Falla esperable de preparación (falta una herramienta, no hay visor): se informa y se reintenta sin traza.</summary>
    public sealed class MirrorSetupException : Exception
    {
        public MirrorState State { get; }
        public MirrorIssue Issue { get; }

        public MirrorSetupException(MirrorState state, MirrorIssue issue, string message) : base(message)
        {
            State = state;
            Issue = issue;
        }
    }
}
