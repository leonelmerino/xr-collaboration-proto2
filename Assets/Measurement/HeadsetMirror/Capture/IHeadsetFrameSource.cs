using System;

namespace XRCollab.Measurement.Mirroring
{
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

    /// <summary>
    /// Instantánea inmutable del estado de una fuente. Se reemplaza entera en cada cambio, así que se puede
    /// leer desde cualquier hilo sin locks.
    /// </summary>
    public sealed class MirrorStatus
    {
        public static readonly MirrorStatus Stopped = new MirrorStatus(MirrorState.Stopped, "detenido");

        public MirrorState State { get; }
        public string Detail { get; }
        public string DeviceSerial { get; }

        public MirrorStatus(MirrorState state, string detail, string deviceSerial = null)
        {
            State = state;
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

        void Start();
        void Stop();
    }

    /// <summary>Falla esperable de preparación (falta una herramienta, no hay visor): se informa y se reintenta sin traza.</summary>
    public sealed class MirrorSetupException : Exception
    {
        public MirrorState State { get; }

        public MirrorSetupException(MirrorState state, string message) : base(message) => State = state;
    }
}
