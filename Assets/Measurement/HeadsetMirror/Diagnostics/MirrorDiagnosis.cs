namespace XRCollab.Measurement.Mirroring
{
    /// <summary>Si el visor está mostrando la app, según la sesión OpenXR de la app en el PC.</summary>
    public enum XrVisibility
    {
        /// <summary>Sin información (no hay proveedor, o no hay sesión XR).</summary>
        Unknown,
        /// <summary>XR_SESSION_STATE_FOCUSED: el visor muestra la app y le da input.</summary>
        Focused,
        /// <summary>XR_SESSION_STATE_VISIBLE: el visor muestra la app, sin input (por ejemplo, con un menú encima).</summary>
        Visible,
        /// <summary>
        /// IDLE, READY o SYNCHRONIZED: la app corre pero el visor no la muestra. Pasa con el visor en la frente o
        /// sacado (VIVE Streaming pausa la imagen) o con otra app en primer plano. SteamVR baja la app a ~20 fps.
        /// </summary>
        NotVisible,
    }

    /// <summary>Lo que la app (no el espejo) sabe del lado XR. Lo entrega quien integra el espejo; ver MeasurementMode.</summary>
    public readonly struct AppXrState
    {
        public static readonly AppXrState Unknown = new AppXrState(XrVisibility.Unknown, null);

        public readonly XrVisibility Visibility;

        /// <summary>Qué le pasa al passthrough de la app, o null si funciona o no se sabe.</summary>
        public readonly string PassthroughProblem;

        public AppXrState(XrVisibility visibility, string passthroughProblem)
        {
            Visibility = visibility;
            PassthroughProblem = string.IsNullOrWhiteSpace(passthroughProblem) ? null : passthroughProblem;
        }

        /// <summary>Traduce un XrSessionState de OpenXR (1 IDLE … 5 FOCUSED … 8 EXITING); 0 o desconocido = Unknown.</summary>
        public static XrVisibility FromSessionState(int xrSessionState)
        {
            switch (xrSessionState)
            {
                case 5: return XrVisibility.Focused;
                case 4: return XrVisibility.Visible;
                case 1: case 2: case 3: case 6: case 7: return XrVisibility.NotVisible;
                default: return XrVisibility.Unknown;
            }
        }
    }

    /// <summary>Aviso para el centro de la ventana del PC: qué pasa (título) y qué hacer (indicación).</summary>
    public sealed class MirrorNotice
    {
        public string Title { get; }
        public string Hint { get; }

        public MirrorNotice(string title, string hint)
        {
            Title = title;
            Hint = hint ?? "";
        }

        public override string ToString() => Hint.Length == 0 ? Title : $"{Title} — {Hint}";
    }

    /// <summary>Todo lo que hace falta para explicar una pantalla negra. Se arma en el hilo principal.</summary>
    public readonly struct DiagnosisInput
    {
        public readonly MirrorStatus Status;
        public readonly bool Visible;
        public readonly double SecondsSinceLastFrame;
        public readonly double SecondsDark;
        public readonly AppXrState App;
        public readonly HeadsetWear Wear;

        public DiagnosisInput(MirrorStatus status, bool visible, double secondsSinceLastFrame, double secondsDark, AppXrState app,
            HeadsetWear wear = HeadsetWear.Unknown)
        {
            Status = status ?? MirrorStatus.Stopped;
            Visible = visible;
            SecondsSinceLastFrame = secondsSinceLastFrame;
            SecondsDark = secondsDark;
            App = app;
            Wear = wear;
        }
    }

    /// <summary>
    /// Explica por qué la ventana del PC está negra, con lo aprendido en las pruebas del 2026-09-24 y 2026-09-29.
    /// Función pura: mismas entradas, mismo aviso (tiene tests). Devuelve null cuando se ve imagen y todo está bien.
    /// Orden: primero lo que impide cualquier imagen (herramientas, cable, autorización, reposo), después la sesión,
    /// y al final lo que se deduce de la imagen misma (se detuvo, o llega pero es negra).
    /// </summary>
    public static class MirrorDiagnosis
    {
        /// <summary>Segundos sin cuadros nuevos antes de avisar (a 30 fps son ~60 cuadros perdidos).</summary>
        public const double StalledAfterSeconds = 2.0;

        /// <summary>
        /// Segundos de cuadros negros antes de avisar. Al ponerse el visor, VIVE hace un fundido desde negro de ~2 s:
        /// con 1,5 s aparecía un "muestra negro" falso (visto el 2026-09-29).
        /// </summary>
        public const double DarkAfterSeconds = 2.5;

        // Sin símbolos fuera de la fuente LegacyRuntime (el ⚡ salía como un hueco).
        private const string UsbHint = "Usar un USB-C sin video o un USB-A: un puerto Thunderbolt o DisplayPort pone al visor en modo DP.";
        private const string PausedHint =
            "El visor no está puesto (¿en la frente?) o tiene otra app o el menú en primer plano, y VIVE Streaming pausa la imagen. " +
            "Ponérselo bien sobre los ojos; la imagen vuelve sola.";
        private const string OffFaceHint =
            "Con el visor en la frente o sobre la mesa, VIVE Streaming pausa la imagen y muestra su pantalla de espera. " +
            "Ponérselo bien sobre los ojos; la imagen vuelve sola.";

        public static MirrorNotice Evaluate(in DiagnosisInput input)
        {
            MirrorStatus s = input.Status;
            if (!input.Visible) return new MirrorNotice("Espejo oculto", "F5 para mostrarlo.");

            switch (s.Issue)
            {
                case MirrorIssue.AdbMissing:
                    return new MirrorNotice("Falta el adb de VIVE Hub en este PC", "Instalar VIVE Hub. " + s.Detail);
                case MirrorIssue.FfmpegMissing:
                    return new MirrorNotice("Falta ffmpeg en este PC", "winget install Gyan.FFmpeg y reiniciar la app.");
                case MirrorIssue.ServerAssetMissing:
                    return new MirrorNotice("El build está incompleto", s.Detail + ". Volver a compilar o copiar el build.");
                case MirrorIssue.NoDevice:
                    return new MirrorNotice("El visor no está conectado por USB",
                        "Conectar el cable USB del visor a este PC y elegir «VIVE Streaming» en el visor. Por Wi-Fi no llega imagen al PC. " + UsbHint);
                case MirrorIssue.DeviceUnauthorized:
                    return new MirrorNotice("El visor no autorizó la depuración USB",
                        "Ponerse el visor y aceptar «Permitir depuración USB» (marcar «Permitir siempre desde este equipo»).");
                case MirrorIssue.DeviceNotFound:
                    return new MirrorNotice("No está el visor pedido", s.Detail + ". Revisar -mirror-serial o conectar ese visor.");
                case MirrorIssue.AdbFailed:
                    return new MirrorNotice("adb no responde", s.Detail + ". Reiniciar VIVE Hub; si sigue, reconectar el cable del visor.");
                case MirrorIssue.DeviceAsleep:
                    return new MirrorNotice("El visor está en reposo",
                        "Nadie lo tiene puesto y su pantalla se apagó. Ponérselo (o presionar su botón de encendido); la imagen vuelve sola.");
            }

            switch (s.State)
            {
                case MirrorState.Stopped:
                    return new MirrorNotice("Espejo detenido", "");
                case MirrorState.Connecting:
                    return new MirrorNotice("Conectando con el visor…", s.DeviceSerial ?? "");
                case MirrorState.Reconnecting:
                    switch (s.Issue)
                    {
                        case MirrorIssue.StreamCut:
                            return new MirrorNotice("Se cortó la imagen del visor",
                                "Reconectando solo… Pasa al desconectar el USB o cuando el visor entra en reposo.");
                        case MirrorIssue.NoImage:
                            return input.App.Visibility == XrVisibility.NotVisible
                                ? new MirrorNotice("El visor pausó la imagen", PausedHint)
                                : new MirrorNotice("El visor no envía imagen", "Reconectando solo… Si se repite, desconectar y conectar el USB del visor.");
                        default:
                            return new MirrorNotice("Falló la conexión con el visor", "Reconectando solo… " + s.Detail);
                    }
                case MirrorState.MissingTools:
                case MirrorState.WaitingForDevice:
                case MirrorState.DeviceAsleep:
                    return new MirrorNotice("Espejo sin imagen", s.Detail);   // sin Issue conocido: se muestra el detalle tal cual
            }

            // Streaming: se deduce de la imagen.
            bool stalled = input.SecondsSinceLastFrame >= StalledAfterSeconds;
            bool dark = input.SecondsDark >= DarkAfterSeconds;
            if (!stalled && !dark) return null;

            // El sensor de proximidad manda: es la causa más común (visor en la frente) y la más segura de detectar.
            if (input.Wear == HeadsetWear.OffFace) return new MirrorNotice("El visor no está puesto", OffFaceHint);
            if (input.App.Visibility == XrVisibility.NotVisible) return new MirrorNotice("El visor pausó la imagen", PausedHint);
            if (stalled)
                return new MirrorNotice("El visor dejó de enviar imagen",
                    "Esperando… Si se lo sacaron entra en reposo; si se desconectó el USB, reconectarlo. Se recupera solo.");
            if (input.App.PassthroughProblem != null)
                return new MirrorNotice("El passthrough no está funcionando",
                    input.App.PassthroughProblem + ". Revisar que VIVE Streaming sea por USB (no DisplayPort) y que «MR with passthrough» esté activado en VIVE Hub.");
            const string blackCauses = "VIVE Streaming por USB (en DisplayPort el passthrough se ve negro) y «MR with passthrough» activado en VIVE Hub. ";
            return input.Wear == HeadsetWear.OnFace
                ? new MirrorNotice("El visor muestra negro", "El visor está puesto, así que revisar: " + blackCauses + UsbHint)
                : new MirrorNotice("El visor muestra negro",
                    "Si no está bien puesto sobre los ojos, VIVE Streaming muestra su pantalla de espera: ponérselo. Si está puesto, revisar: " + blackCauses);
        }
    }
}
