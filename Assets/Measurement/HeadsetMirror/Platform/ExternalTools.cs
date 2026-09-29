using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>Ubica los ejecutables externos. La existencia de archivos se puede inyectar para los tests.</summary>
    public static class ExternalTools
    {
        /// <summary>
        /// adb que trae VIVE Hub (1.0.41). Hay que usar este: si otro adb de otra versión (Platform-Tools)
        /// toca el servidor adb, cada uno mata al del otro y se corta el enlace USB de VIVE Hub con el visor.
        /// En STIMULUS2 VIVE Hub está instalado por Steam.
        /// </summary>
        public static readonly string[] ViveHubAdbPaths =
        {
            @"C:\Program Files\VIVE Hub\VIVE Hub\CommonTools\ADB\adb.exe",
            @"C:\Program Files (x86)\Steam\steamapps\common\VIVE HUB\VIVE Hub\CommonTools\ADB\adb.exe",
        };

        /// <summary>WinGet deja ffmpeg aquí (máquina y usuario); en los PCs del laboratorio lo instala Install-LabDevTools.</summary>
        public const string WinGetMachineLinks = @"C:\Program Files\WinGet\Links";
        public const string WinGetUserLinks = @"Microsoft\WinGet\Links";

        public static string FindAdb(string overridePath, Func<string, bool> exists = null)
        {
            exists ??= File.Exists;
            if (!string.IsNullOrEmpty(overridePath)) return SafeExists(exists, overridePath) ? overridePath : null;
            return ViveHubAdbPaths.FirstOrDefault(p => SafeExists(exists, p));
        }

        public static string FindFfmpeg(string overridePath) =>
            FindFfmpeg(overridePath, Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        /// <summary>Orden: ruta explícita, PATH, WinGet de máquina, WinGet del usuario.</summary>
        public static string FindFfmpeg(string overridePath, string pathVariable, string localAppData, Func<string, bool> exists = null)
        {
            exists ??= File.Exists;
            if (!string.IsNullOrEmpty(overridePath)) return SafeExists(exists, overridePath) ? overridePath : null;
            return FfmpegCandidates(pathVariable, localAppData).FirstOrDefault(p => SafeExists(exists, p));
        }

        private static IEnumerable<string> FfmpegCandidates(string pathVariable, string localAppData)
        {
            foreach (string dir in (pathVariable ?? "").Split(';'))
            {
                string candidate = SafeCombine(dir.Trim().Trim('"'), "ffmpeg.exe");
                if (candidate != null) yield return candidate;
            }
            yield return Path.Combine(WinGetMachineLinks, "ffmpeg.exe");
            if (!string.IsNullOrEmpty(localAppData))
            {
                string user = SafeCombine(Path.Combine(localAppData, WinGetUserLinks), "ffmpeg.exe");
                if (user != null) yield return user;
            }
        }

        // Una entrada rota del PATH (caracteres inválidos) no debe impedir buscar en las demás.
        private static string SafeCombine(string dir, string file)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try { return Path.Combine(dir, file); }
            catch (ArgumentException) { return null; }
        }

        private static bool SafeExists(Func<string, bool> exists, string path)
        {
            try { return exists(path); }
            catch (Exception) { return false; }
        }
    }
}
