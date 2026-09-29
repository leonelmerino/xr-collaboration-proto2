using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace XRCollab.Measurement.Mirroring
{
    public readonly struct ProcessResult
    {
        public readonly int ExitCode;
        public readonly string Output;
        public readonly string Error;
        public readonly bool TimedOut;

        public ProcessResult(int exitCode, string output, string error, bool timedOut)
        {
            ExitCode = exitCode;
            Output = output ?? "";
            Error = error ?? "";
            TimedOut = timedOut;
        }

        public bool Succeeded => !TimedOut && ExitCode == 0;

        /// <summary>Primera línea útil para mensajes de error (stderr, si no stdout).</summary>
        public string Summary
        {
            get
            {
                if (TimedOut) return "no respondió a tiempo";
                string text = Error.Trim().Length > 0 ? Error : Output;
                string first = text.Trim().Split('\n')[0].Trim();
                return first.Length > 0 ? first : $"código de salida {ExitCode}";
            }
        }
    }

    /// <summary>
    /// Proceso externo sin ventana, con stdout y stderr redirigidos, registrado en <see cref="KillOnCloseJob"/>.
    /// stderr siempre se drena (si no, el hijo se bloquea al llenar el pipe).
    /// </summary>
    internal sealed class ChildProcess : IDisposable
    {
        private readonly Process _process;

        public string Name { get; }

        /// <summary>stdout en binario. Solo si se lanzó sin <c>onOutputLine</c>.</summary>
        public Stream Output => _process.StandardOutput.BaseStream;

        private ChildProcess(Process process, string name)
        {
            _process = process;
            Name = name;
        }

        /// <param name="onOutputLine">Recibe stdout por líneas; null deja stdout para leerlo en binario con <see cref="Output"/>.</param>
        /// <param name="onErrorLine">Recibe stderr por líneas (puede ser null).</param>
        public static ChildProcess Start(string exe, string arguments, Action<string> onOutputLine, Action<string> onErrorLine)
        {
            var process = new Process { StartInfo = HiddenStartInfo(exe, arguments) };
            process.ErrorDataReceived += (_, e) => Deliver(onErrorLine, e.Data);
            if (onOutputLine != null)
                process.OutputDataReceived += (_, e) => Deliver(onOutputLine, e.Data);

            process.Start();
            KillOnCloseJob.TryAdd(process);
            process.BeginErrorReadLine();
            if (onOutputLine != null) process.BeginOutputReadLine();
            return new ChildProcess(process, Path.GetFileNameWithoutExtension(exe));
        }

        /// <summary>Ejecuta y espera. Si pasa <paramref name="timeoutMs"/>, mata el proceso y marca TimedOut.</summary>
        public static ProcessResult Run(string exe, string arguments, int timeoutMs)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            using var process = new Process { StartInfo = HiddenStartInfo(exe, arguments) };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };

            process.Start();
            KillOnCloseJob.TryAdd(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                return new ProcessResult(-1, output.ToString(), error.ToString(), timedOut: true);
            }
            process.WaitForExit();   // sin argumento espera también a que terminen los lectores asíncronos
            return new ProcessResult(process.ExitCode, output.ToString(), error.ToString(), timedOut: false);
        }

        // Los callbacks corren en hilos lectores del runtime: una excepción ahí no debe voltear la app.
        private static void Deliver(Action<string> callback, string line)
        {
            if (line == null || callback == null) return;
            try { callback(line); }
            catch (Exception) { }
        }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public void Kill() => TryKill(_process);

        public void Dispose()
        {
            Kill();
            _process.Dispose();
        }

        private static ProcessStartInfo HiddenStartInfo(string exe, string arguments) => new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }   // ya terminó o nunca arrancó
            catch (Win32Exception) { }              // terminando en este momento
        }
    }
}
