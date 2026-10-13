using System;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Lo que el resto de la app (Assembly-CSharp) le ofrece al canal de control (127.0.0.1:47811) sin que este
    /// ensamblado dependa de ella. Cada módulo registra su función al arrancar; sin registro, la orden responde
    /// ok=false con un mensaje claro. Todas se llaman en el hilo principal (<see cref="RecordingControlServer.Pump"/>).
    /// </summary>
    public static class AppControlHooks
    {
        /// <summary>«cue …»: muestra (o con «clear» quita) una señal 3D en el visor. Devuelve null si salió bien o el error.</summary>
        public static Func<string, string> Cue;

        /// <summary>«snapshot»: JPG en base64 de lo que ve el visor, de a lo más ese ancho; null sin imagen.</summary>
        public static Func<int, string> Snapshot;

        /// <summary>Estado en vivo de la app como objeto JSON ("{...}"); va en "app" de cada respuesta. null = nada.</summary>
        public static Func<string> Health;

        /// <summary>«occlusion on|off|show|hide»: oclusión por las manos reales. Devuelve null si salió bien o el error.</summary>
        public static Func<string, string> Occlusion;

        /// <summary>«turn start|end|place|show|hide»: botón de turno del participante. Devuelve null si salió bien o el error.</summary>
        public static Func<string, string> Turn;
    }
}
