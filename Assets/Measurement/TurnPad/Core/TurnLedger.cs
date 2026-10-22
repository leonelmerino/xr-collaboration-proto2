using System;
using System.Globalization;
using System.Text;

namespace XRCollab.Measurement.Turns
{
    /// <summary>
    /// Quién tiene el turno entre los PCs, sin Unity (se prueba en EditMode, Tests/Editor). Es un registro «el más nuevo
    /// gana»: cada inicio de turno lleva una clave = máx(hora UTC en ticks, clave más nueva conocida + 1) y gana la clave
    /// mayor (empate: el nombre del PC).
    ///  - Un inicio posterior a otro que este PC ya conocía siempre gana, aunque su reloj esté atrasado o adelantado.
    ///  - Dos inicios sin noticia uno del otro (casi simultáneos, o con un PC desconectado) se ordenan por los relojes
    ///    (ntp.uc.cl, a pocos ms): gana el último.
    ///  - Fusionar es quedarse con el máximo: da igual el orden, los duplicados o por qué camino llegue (red del host o
    ///    panel), todos terminan con el mismo dueño.
    /// </summary>
    public sealed class TurnLedger
    {
        public string Pc { get; private set; } = "";
        public long Key { get; private set; }

        public static bool IsNewer(string pcA, long keyA, string pcB, long keyB) =>
            keyA > keyB || (keyA == keyB && string.CompareOrdinal(pcA ?? "", pcB ?? "") > 0);

        /// <summary>Turno que empieza ahora en <paramref name="me"/>: queda como el más nuevo. Devuelve su clave.</summary>
        public long Start(string me, long utcTicks)
        {
            Key = Math.Max(utcTicks, Key + 1);
            Pc = me ?? "";
            return Key;
        }

        /// <summary>Lo que sabe otro PC (o el archivo de estado). true si cambió el turno más nuevo conocido.</summary>
        public bool Merge(string pc, long key)
        {
            if (string.IsNullOrEmpty(pc) || key <= 0 || !IsNewer(pc, key, Pc, Key)) return false;
            Pc = pc;
            Key = key;
            return true;
        }

        /// <summary>Este PC sabe algo más nuevo que (pc, key): hay que contárselo a quien lo mandó.</summary>
        public bool NewerThan(string pc, long key) => Key > 0 && IsNewer(Pc, Key, pc, key);

        /// <summary>Un PC en turno lo cede si el turno más nuevo conocido es de otro.</summary>
        public bool ShouldYield(bool inTurn, string me) => inTurn && Key > 0 && !string.Equals(Pc, me, StringComparison.Ordinal);
    }

    /// <summary>
    /// Estado del botón de turno guardado en disco (Application.persistentDataPath/turn-state.txt), para que un reinicio
    /// de la app no pierda el turno en curso, el número de turno ni el turno más nuevo conocido.
    /// </summary>
    public sealed class TurnSnapshot
    {
        /// <summary>Un turno guardado más viejo que esto no se retoma (la app estuvo cerrada demasiado).</summary>
        public static readonly TimeSpan MaxResumeAge = TimeSpan.FromMinutes(30);

        public string Day = "";          // fecha local yyyy-MM-dd: el número de turno vuelve a 0 cada día
        public int Number;
        public bool InTurn;
        public long StartUtcTicks;
        public double LastDuration;
        public string LastUtc = "";
        public string Source = "";
        public string LatestPc = "";
        public long LatestKey;
        // Dónde vive el botón respecto del participante (TurnFollow): grados, m del eje, altura (m) y el origen del
        // seguimiento con que se midió la altura («Floor» = desde el piso). PadRadius 0 = nunca se colocó.
        public float PadAngle, PadRadius, PadHeight;
        public string PadOrigin = "";

        public string Serialize()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("day=").Append(Day).Append('\n');
            sb.Append("n=").Append(Number.ToString(c)).Append('\n');
            sb.Append("inTurn=").Append(InTurn ? "1" : "0").Append('\n');
            sb.Append("startUtcTicks=").Append(StartUtcTicks.ToString(c)).Append('\n');
            sb.Append("lastDuration=").Append(LastDuration.ToString("R", c)).Append('\n');
            sb.Append("lastUtc=").Append(LastUtc).Append('\n');
            sb.Append("source=").Append(Source).Append('\n');
            sb.Append("latestPc=").Append(LatestPc).Append('\n');
            sb.Append("latestKey=").Append(LatestKey.ToString(c)).Append('\n');
            sb.Append("padAngle=").Append(PadAngle.ToString("R", c)).Append('\n');
            sb.Append("padRadius=").Append(PadRadius.ToString("R", c)).Append('\n');
            sb.Append("padHeight=").Append(PadHeight.ToString("R", c)).Append('\n');
            sb.Append("padOrigin=").Append(PadOrigin).Append('\n');
            return sb.ToString();
        }

        /// <summary>Tolerante: líneas desconocidas o dañadas se ignoran (queda el valor por defecto).</summary>
        public static TurnSnapshot Parse(string text)
        {
            var c = CultureInfo.InvariantCulture;
            var s = new TurnSnapshot();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                switch (k)
                {
                    case "day": s.Day = v; break;
                    case "n": int.TryParse(v, NumberStyles.Integer, c, out s.Number); break;
                    case "inTurn": s.InTurn = v == "1"; break;
                    case "startUtcTicks": long.TryParse(v, NumberStyles.Integer, c, out s.StartUtcTicks); break;
                    case "lastDuration": double.TryParse(v, NumberStyles.Float, c, out s.LastDuration); break;
                    case "lastUtc": s.LastUtc = v; break;
                    case "source": s.Source = v; break;
                    case "latestPc": s.LatestPc = v; break;
                    case "latestKey": long.TryParse(v, NumberStyles.Integer, c, out s.LatestKey); break;
                    case "padAngle": float.TryParse(v, NumberStyles.Float, c, out s.PadAngle); break;
                    case "padRadius": float.TryParse(v, NumberStyles.Float, c, out s.PadRadius); break;
                    case "padHeight": float.TryParse(v, NumberStyles.Float, c, out s.PadHeight); break;
                    case "padOrigin": s.PadOrigin = v; break;
                }
            }
            return s;
        }

        /// <summary>El número de turno sigue si el estado es de hoy (si no, vuelve a 0).</summary>
        public bool SameDay(string today) => Day == today;

        /// <summary>Retomar el turno guardado: de hoy, en curso, y empezado hace menos de <see cref="MaxResumeAge"/>.</summary>
        public bool CanResume(string today, long nowUtcTicks) =>
            SameDay(today) && InTurn && StartUtcTicks > 0 && nowUtcTicks >= StartUtcTicks &&
            nowUtcTicks - StartUtcTicks <= MaxResumeAge.Ticks;
    }
}
