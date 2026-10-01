using System;
using System.Runtime.InteropServices;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Hora UTC con resolución de microsegundos. En Mono, DateTime.UtcNow puede avanzar a saltos de ~1–16 ms en
    /// Windows; GetSystemTimePreciseAsFileTime da la misma hora del sistema (la que corrige NTP) sin esos saltos.
    /// Es la hora que se anota en frames.csv, marks.csv y session.json para sincronizar PCs y video con la telemetría.
    /// </summary>
    public static class PreciseClock
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("kernel32.dll")]
        private static extern void GetSystemTimePreciseAsFileTime(out long fileTime);

        private static bool _precise = true;
#endif

        public static DateTime UtcNow
        {
            get
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                if (_precise)
                {
                    try
                    {
                        GetSystemTimePreciseAsFileTime(out long fileTime);
                        return DateTime.FromFileTimeUtc(fileTime);
                    }
                    catch (Exception)
                    {
                        _precise = false;   // Windows anterior a 8: hora normal
                    }
                }
#endif
                return DateTime.UtcNow;
            }
        }

        /// <summary>Milisegundos desde 1970-01-01 UTC, con decimales.</summary>
        public static double ToUnixMs(DateTime utc) => (utc.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }
}
