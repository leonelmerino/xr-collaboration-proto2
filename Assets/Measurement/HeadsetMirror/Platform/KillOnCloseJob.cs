using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>
    /// Job object de Windows con KILL_ON_JOB_CLOSE para los procesos hijos (adb shell, ffmpeg). Si la app
    /// termina mal (crash, "Finalizar tarea", Stop del Editor a medias), Windows cierra el handle del job y
    /// mata a los hijos: no quedan ffmpeg ni servidores de scrcpy huérfanos ocupando el encoder del visor.
    /// El handle no se cierra a propósito; se libera cuando termina el proceso de Unity.
    /// Si el job no se puede crear, se sigue igual: los hijos también se matan en Stop/OnDisable.
    /// </summary>
    internal static class KillOnCloseJob
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private static readonly object Gate = new object();
        private static IntPtr _job;
        private static bool _initialized;

        public static void TryAdd(Process process)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            try
            {
                IntPtr job = GetOrCreate();
                if (job != IntPtr.Zero) AssignProcessToJobObject(job, process.Handle);
            }
            catch (Exception)
            {
                // Sin job: el ciclo de vida normal (Stop/OnDisable) igual mata al hijo.
            }
        }

        private static IntPtr GetOrCreate()
        {
            lock (Gate)
            {
                if (_initialized) return _job;
                _initialized = true;

                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;

                var info = new JobObjectExtendedLimitInfo
                {
                    BasicLimitInformation = new JobObjectBasicLimitInfo { LimitFlags = JobObjectLimitKillOnJobClose },
                };
                int length = Marshal.SizeOf(typeof(JobObjectExtendedLimitInfo));
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
                    {
                        CloseHandle(job);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                _job = job;
                return _job;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInfo
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInfo
        {
            public JobObjectBasicLimitInfo BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
