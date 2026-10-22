using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XRCollab.Measurement.Recording
{
    public enum RecordingCommandKind
    {
        Status,
        Start,
        Stop,
        Mark,
        /// <summary>Señal 3D en el visor (Assets/Measurement/FeedbackCues): «cue tono|ícono|título|subtítulo» o «cue clear».</summary>
        Cue,
        /// <summary>JPG chico de lo que ve el visor (espejo), para el panel de sesión.</summary>
        Snapshot,
        /// <summary>Botón de turno del participante (Assets/Measurement/TurnPad): «turn start|end|place|show|hide».</summary>
        Turn,
    }

    /// <summary>
    /// Una orden de control de la grabación: una línea de texto (ASCII/UTF-8) por orden, terminada en <c>\n</c>.
    /// <code>
    ///   status
    ///   start [label=S01] [at=2026-10-01T18:00:05.000Z]
    ///   stop  [at=2026-10-01T18:40:00.000Z]
    ///   mark  texto libre (aplauso inicio, cambio de turno...)
    ///   cue   bad|cross|MAL COLOCADO|El bloque quedó torcido     (señal 3D en el visor; «cue clear» la quita)
    ///   snapshot [w=480]                                           (JPG de lo que ve el visor, en "jpg" de la respuesta)
    ///   turn start|end|place|show|hide                             (turno del participante, como si tocara el botón)
    ///   turn style field|solid|ghost|fade|ring · turn mode pad|gesture|both   (diseño del botón · botón y/o gesto)
    ///   turn latest PC CLAVE                                       (panel: turno más nuevo visto en otro PC; un solo turno a la vez)
    /// </code>
    /// <c>at</c> es una hora UTC ISO 8601: así varios PCs con los relojes sincronizados (NTP) empiezan en el mismo
    /// instante aunque la orden les llegue con distinta demora. Sin <c>at</c>, la orden corre apenas llega.
    /// La respuesta es una línea JSON (<see cref="RecordingStatus"/>). Funciones puras, con tests.
    /// </summary>
    public sealed class RecordingCommand
    {
        public const int MaxLabelLength = 40;
        public const int MaxMarkLength = 200;
        public const int MaxCueLength = 300;
        public const int DefaultSnapshotWidth = 480;
        private static readonly Regex LabelPattern = new Regex("^[A-Za-z0-9_-]+$");
        private static readonly string[] TurnArgs = { "start", "end", "place", "show", "hide" };
        private static readonly Regex TurnArg = new Regex(@"^(start|end|place|show|hide|style (field|solid|ghost|fade|ring)|mode (pad|gesture|both)|latest [a-z0-9_-]{1,64} \d{1,19})$");

        public RecordingCommandKind Kind { get; private set; }
        public string Label { get; private set; } = "";
        public DateTime? AtUtc { get; private set; }
        public string Text { get; private set; } = "";
        /// <summary>Ancho máximo del JPG de «snapshot».</summary>
        public int Width { get; private set; } = DefaultSnapshotWidth;

        /// <returns>null y <paramref name="error"/> si la línea no es una orden válida.</returns>
        public static RecordingCommand Parse(string line, out string error)
        {
            error = null;
            string trimmed = (line ?? "").Trim();
            if (trimmed.Length == 0)
            {
                error = "orden vacía";
                return null;
            }
            int space = trimmed.IndexOf(' ');
            string verb = (space < 0 ? trimmed : trimmed.Substring(0, space)).ToLowerInvariant();
            string rest = space < 0 ? "" : trimmed.Substring(space + 1).Trim();
            var cmd = new RecordingCommand();
            switch (verb)
            {
                case "status":
                case "ping":
                    cmd.Kind = RecordingCommandKind.Status;
                    return cmd;
                case "mark":
                    cmd.Kind = RecordingCommandKind.Mark;
                    cmd.Text = Sanitize(rest, MaxMarkLength);
                    return cmd;
                case "cue":
                    cmd.Kind = RecordingCommandKind.Cue;
                    cmd.Text = StripControl(rest, MaxCueLength);
                    if (cmd.Text.Length == 0)
                    {
                        error = "cue vacío (cue tono|ícono|título|subtítulo, o cue clear)";
                        return null;
                    }
                    return cmd;
                case "snapshot":
                    cmd.Kind = RecordingCommandKind.Snapshot;
                    if (rest.StartsWith("w=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(rest.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) || w < 64 || w > 1920)
                        {
                            error = $"ancho inválido '{rest}' (w=64..1920)";
                            return null;
                        }
                        cmd.Width = w;
                    }
                    else if (rest.Length > 0)
                    {
                        error = $"parámetro desconocido '{rest}' (snapshot [w=480])";
                        return null;
                    }
                    return cmd;
                case "turn":
                    cmd.Kind = RecordingCommandKind.Turn;
                    cmd.Text = Regex.Replace(rest.ToLowerInvariant(), @"\s+", " ");
                    if (!TurnArg.IsMatch(cmd.Text))
                    {
                        error = $"turn {string.Join("|", TurnArgs)}|style field|solid|ghost|fade|ring|mode pad|gesture|both";
                        return null;
                    }
                    return cmd;
                case "start":
                    cmd.Kind = RecordingCommandKind.Start;
                    break;
                case "stop":
                    cmd.Kind = RecordingCommandKind.Stop;
                    break;
                default:
                    error = $"orden desconocida '{verb}' (status | start [label=X] [at=UTC] | stop [at=UTC] | mark texto | cue ... | snapshot | turn ...)";
                    return null;
            }

            foreach (string token in rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                string key = eq < 0 ? token.ToLowerInvariant() : token.Substring(0, eq).ToLowerInvariant();
                string value = eq < 0 ? "" : token.Substring(eq + 1);
                switch (key)
                {
                    case "label" when cmd.Kind == RecordingCommandKind.Start:
                        if (value.Length == 0 || value.Length > MaxLabelLength || !LabelPattern.IsMatch(value))
                        {
                            error = $"label inválido '{value}' (letras, números, - y _, hasta {MaxLabelLength})";
                            return null;
                        }
                        cmd.Label = value;
                        break;
                    case "at":
                        if (!TryParseUtc(value, out DateTime at))
                        {
                            error = $"hora inválida '{value}' (UTC ISO 8601, ej. 2026-10-01T18:00:05.000Z)";
                            return null;
                        }
                        cmd.AtUtc = at;
                        break;
                    default:
                        error = $"parámetro desconocido '{token}'";
                        return null;
                }
            }
            return cmd;
        }

        public static bool TryParseUtc(string value, out DateTime utc)
        {
            bool ok = DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
            if (ok) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return ok;
        }

        /// <summary>Hora UTC con milisegundos, como la escriben session.json, frames.csv y marks.csv.</summary>
        public static string FormatUtc(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // Una sola línea (sin caracteres de control); conserva comas, tildes y signos.
        private static string StripControl(string text, int max)
        {
            var chars = (text ?? "").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] < ' ') chars[i] = ' ';
            string s = new string(chars).Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // Una sola línea, sin comas ni comillas: va tal cual a un CSV.
        public static string Sanitize(string text, int max)
        {
            var chars = (text ?? "").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] < ' ' || chars[i] == ',' || chars[i] == '"') chars[i] = ' ';
            string s = new string(chars).Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }
    }

    /// <summary>Respuesta a una orden y estado de la grabación (JSON de una línea).</summary>
    [Serializable]
    public sealed class RecordingStatus
    {
        public bool ok = true;
        public string error = "";
        /// <summary>disabled | idle | armed | recording | failed</summary>
        public string state = "idle";
        public string computer = "";
        public string label = "";
        public string dir = "";
        public string encoder = "";
        public string requestedUtc = "";
        public string startAtUtc = "";
        public string firstFrameUtc = "";
        public string stopAtUtc = "";
        public double elapsedSeconds;
        public long framesWritten;
        public long framesDropped;
        public string lastVideo = "";
        public string appUtc = "";
        /// <summary>Audio de la app: fuente (AudioListener · Hz), "off" o "error: ...".</summary>
        public string audioApp = "";
        /// <summary>Micrófono: nombre del dispositivo (· Hz), "off" o "error: ...".</summary>
        public string audioMic = "";
        /// <summary>Audio dentro de lastVideo: "" (sin audio) | pendiente | ok | error: ... (WAVs y video sin audio quedan).</summary>
        public string audioMux = "";
    }
}
