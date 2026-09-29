using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XRCollab.Measurement.Mirroring
{
    public readonly struct AdbDevice
    {
        public readonly string Serial;
        public readonly string State;

        public AdbDevice(string serial, string state)
        {
            Serial = serial;
            State = state;
        }

        /// <summary>"device" = conectado y con la depuración USB autorizada.</summary>
        public bool IsReady => State == "device";
        public bool IsUnauthorized => State == "unauthorized";

        public override string ToString() => $"{Serial} ({State})";
    }

    public readonly struct AdbForward
    {
        public readonly string Serial;
        public readonly string Local;
        public readonly string Remote;

        public AdbForward(string serial, string local, string remote)
        {
            Serial = serial;
            Local = local;
            Remote = remote;
        }

        /// <summary>Puerto TCP local ("tcp:63852" → 63852), o 0 si no es TCP.</summary>
        public int LocalTcpPort =>
            Local.StartsWith("tcp:", StringComparison.Ordinal) && int.TryParse(Local.Substring(4), out int port) ? port : 0;
    }

    /// <summary>
    /// Los comandos de adb que usa el espejo. Siempre con el adb de VIVE Hub (ver <see cref="ExternalTools.FindAdb"/>).
    /// El parseo es estático y puro para poder testearlo sin visor.
    /// </summary>
    internal sealed class AdbClient
    {
        private const int CommandTimeoutMs = 15_000;

        private static readonly Regex SizePattern = new Regex(@"(Physical|Override) size:\s*(\d+)x(\d+)", RegexOptions.Compiled);
        private static readonly Regex WakefulnessPattern = new Regex(@"mWakefulness=(\w+)", RegexOptions.Compiled);

        public string ExecutablePath { get; }

        public AdbClient(string executablePath) => ExecutablePath = executablePath;

        public IReadOnlyList<AdbDevice> ListDevices()
        {
            ProcessResult r = Run("devices");
            if (!r.Succeeded) throw new MirrorSetupException(MirrorState.WaitingForDevice, $"adb devices: {r.Summary}");
            return ParseDevices(r.Output);
        }

        public bool TryGetDisplaySize(string serial, out int width, out int height)
        {
            ProcessResult r = Run($"-s {serial} shell wm size");
            width = height = 0;
            return r.Succeeded && ParseDisplaySize(r.Output, out width, out height);
        }

        /// <summary>Estado de energía del visor ("Awake", "Asleep", "Dozing"...) según <c>dumpsys power</c>.</summary>
        public bool TryGetWakefulness(string serial, out string wakefulness)
        {
            ProcessResult r = Run($"-s {serial} shell \"dumpsys power | grep mWakefulness=\"");
            wakefulness = null;
            return r.Succeeded && ParseWakefulness(r.Output, out wakefulness);
        }

        public void Push(string serial, string localPath, string remotePath) =>
            Require(Run($"-s {serial} push \"{localPath}\" {remotePath}"), "adb push");

        public void Forward(string serial, int localPort, string abstractSocket) =>
            Require(Run($"-s {serial} forward tcp:{localPort} localabstract:{abstractSocket}"), "adb forward");

        /// <summary>Sin errores: se llama al limpiar y el forward puede no existir ya.</summary>
        public void RemoveForward(string serial, int localPort) => Run($"-s {serial} forward --remove tcp:{localPort}");

        public IReadOnlyList<AdbForward> ListForwards()
        {
            ProcessResult r = Run("forward --list");
            return r.Succeeded ? ParseForwards(r.Output) : Array.Empty<AdbForward>();
        }

        /// <summary>Salida de <c>adb forward --list</c>: "serial local remoto" por línea.</summary>
        public static IReadOnlyList<AdbForward> ParseForwards(string output)
        {
            var forwards = new List<AdbForward>();
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string[] parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3) forwards.Add(new AdbForward(parts[0], parts[1], parts[2]));
            }
            return forwards;
        }

        /// <summary>Lanza <c>adb shell</c> de larga duración; stdout y stderr llegan por líneas a <paramref name="onLine"/>.</summary>
        public ChildProcess StartShell(string serial, string command, Action<string> onLine) =>
            ChildProcess.Start(ExecutablePath, $"-s {serial} shell {command}", onLine, onLine);

        private ProcessResult Run(string arguments) => ChildProcess.Run(ExecutablePath, arguments, CommandTimeoutMs);

        private static void Require(ProcessResult r, string what)
        {
            if (!r.Succeeded) throw new InvalidOperationException($"{what} falló: {r.Summary}");
        }

        /// <summary>Salida de <c>adb devices</c> (o <c>-l</c>): una línea "serial&lt;tab&gt;estado [detalles]" por dispositivo.</summary>
        public static IReadOnlyList<AdbDevice> ParseDevices(string output)
        {
            var devices = new List<AdbDevice>();
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal))
                    continue;
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) devices.Add(new AdbDevice(parts[0], parts[1]));
            }
            return devices;
        }

        /// <summary>Línea <c>mWakefulness=Awake</c> de <c>dumpsys power</c> (se ignora <c>mWakefulnessChanging</c>).</summary>
        public static bool ParseWakefulness(string output, out string wakefulness)
        {
            Match m = WakefulnessPattern.Match(output ?? "");
            wakefulness = m.Success ? m.Groups[1].Value : null;
            return m.Success;
        }

        /// <summary>Salida de <c>wm size</c>. Si hay "Override size", manda sobre "Physical size".</summary>
        public static bool ParseDisplaySize(string output, out int width, out int height)
        {
            width = height = 0;
            bool found = false;
            foreach (Match m in SizePattern.Matches(output ?? ""))
            {
                bool isOverride = m.Groups[1].Value == "Override";
                if (found && !isOverride) continue;
                width = int.Parse(m.Groups[2].Value);
                height = int.Parse(m.Groups[3].Value);
                found = width > 0 && height > 0;
            }
            return found;
        }
    }
}
