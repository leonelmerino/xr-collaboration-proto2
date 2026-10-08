using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Meter el audio (audio_app.wav, audio_mic.wav) en el .mp4 final, como pistas «app» y «mic» alineadas al primer
    /// cuadro del video. Lee session.json (horas del primer cuadro y de la primera muestra de cada WAV), así que sirve
    /// igual al detener y al próximo arranque si la app se cortó. El armado corre en el mismo proceso aparte que une
    /// los segmentos (<see cref="SessionRecorder.LaunchFinalize"/>): primero queda el .mp4 solo video, y recién si el
    /// audio sale bien se reemplaza por el .mp4 con audio. Los WAV quedan siempre.
    /// </summary>
    public static class RecordingAudioMux
    {
        private const int MaxErrorLength = 200;

        /// <summary>Pistas listas para meter, según session.json y los WAV que hay en la carpeta. Vacía = sin audio.</summary>
        public static IReadOnlyList<AudioMuxTrack> Tracks(string sessionDir, RecordingSessionInfo info)
        {
            var tracks = new List<AudioMuxTrack>();
            if (info == null || !RecordingCommand.TryParseUtc(info.firstFrameUtc, out DateTime firstFrame)) return tracks;
            Add(tracks, sessionDir, info.audioApp, "app", firstFrame);
            Add(tracks, sessionDir, info.audioMic, "mic", firstFrame);
            return tracks;
        }

        private static void Add(List<AudioMuxTrack> tracks, string dir, AudioTrackInfo t, string name, DateTime firstFrame)
        {
            if (t == null || string.IsNullOrEmpty(t.file) || !RecordingCommand.TryParseUtc(t.startUtc, out DateTime start)) return;
            string path = Path.Combine(dir, t.file);
            if (!HasAudioData(path)) return;
            tracks.Add(new AudioMuxTrack
            {
                Name = name,
                FilePath = path,
                OffsetSeconds = (start - firstFrame).TotalSeconds,
                Tempo = RecordingFfmpeg.DriftTempo(t.sampleRate, t.effectiveRate, t.seconds),
            });
        }

        private static bool HasAudioData(string wavPath)
        {
            try { return File.Exists(wavPath) && new FileInfo(wavPath).Length > AudioWavWriter.HeaderBytes; }
            catch (IOException) { return false; }
        }

        public static bool HasAudioFiles(string sessionDir) =>
            HasAudioData(Path.Combine(sessionDir, RecordingLayout.AppAudioFile)) ||
            HasAudioData(Path.Combine(sessionDir, RecordingLayout.MicAudioFile));

        public static RecordingSessionInfo ReadInfo(string sessionDir)
        {
            try
            {
                string path = Path.Combine(sessionDir, RecordingLayout.InfoFile);
                return File.Exists(path) ? JsonUtility.FromJson<RecordingSessionInfo>(File.ReadAllText(path)) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Paso de cmd que mete el audio en el .mp4 final de <paramref name="sessionDir"/> (ver
        /// <see cref="RecordingFfmpeg.AudioMuxCommandPart"/>). null si no hay audio que meter.
        /// </summary>
        public static string CommandPart(string sessionDir, string ffmpeg)
        {
            if (!HasAudioFiles(sessionDir)) return null;
            IReadOnlyList<AudioMuxTrack> tracks = Tracks(sessionDir, ReadInfo(sessionDir));
            if (tracks.Count == 0) return null;
            string final = RecordingLayout.FinalVideoPath(sessionDir);
            string partial = RecordingLayout.AudioMuxPartialPath(sessionDir);
            // Un intento anterior fallido no debe confundir el estado de este.
            TryDelete(Path.Combine(sessionDir, RecordingLayout.AudioMuxFailedFile));
            TryDelete(Path.Combine(sessionDir, RecordingLayout.AudioMuxDoneFile));
            return RecordingFfmpeg.AudioMuxCommandPart(ffmpeg, RecordingFfmpeg.AudioMuxArguments(final, tracks, partial), partial, final,
                Path.Combine(sessionDir, RecordingLayout.AudioMuxLogFile),
                Path.Combine(sessionDir, RecordingLayout.AudioMuxDoneFile),
                Path.Combine(sessionDir, RecordingLayout.AudioMuxFailedFile));
        }

        /// <summary>Para el «status»: "" (sin audio) | pendiente | ok | error: ... .</summary>
        public static string State(string sessionDir)
        {
            if (string.IsNullOrEmpty(sessionDir) || !HasAudioFiles(sessionDir)) return "";
            if (File.Exists(Path.Combine(sessionDir, RecordingLayout.AudioMuxDoneFile))) return "ok";
            if (File.Exists(Path.Combine(sessionDir, RecordingLayout.AudioMuxFailedFile)))
            {
                string detail = "";
                try
                {
                    string log = Path.Combine(sessionDir, RecordingLayout.AudioMuxLogFile);
                    if (File.Exists(log))
                        detail = File.ReadAllLines(log).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                }
                catch (Exception) { }
                if (detail.Length > MaxErrorLength) detail = detail.Substring(0, MaxErrorLength);
                return "error: " + (detail.Length > 0 ? detail : "ffmpeg falló (ver audio_mux.log)") + " · quedan el .mp4 sin audio y los WAV";
            }
            if (Tracks(sessionDir, ReadInfo(sessionDir)).Count == 0)
                return "error: session.json no tiene la hora del primer cuadro o del audio · quedan el .mp4 sin audio y los WAV";
            return "pendiente";
        }

        /// <summary>Grabaciones con video final y WAV pero sin audio metido ni error (la app se cerró a mitad del armado).</summary>
        public static IEnumerable<string> Pending(string root)
        {
            if (!Directory.Exists(root)) yield break;
            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(RecordingLayout.FinalVideoPath(dir))) continue;   // eso lo arma LaunchFinalize (con audio)
                if (File.Exists(Path.Combine(dir, RecordingLayout.AudioMuxDoneFile))) continue;
                if (File.Exists(Path.Combine(dir, RecordingLayout.AudioMuxFailedFile))) continue;
                if (HasAudioFiles(dir)) yield return dir;
            }
        }

        /// <summary>Lanza solo el paso del audio (el .mp4 final ya existe), en un proceso aparte.</summary>
        internal static bool LaunchMuxOnly(string sessionDir, string ffmpeg)
        {
            string partial = RecordingLayout.AudioMuxPartialPath(sessionDir);
            if (File.Exists(partial) && (DateTime.Now - File.GetLastWriteTime(partial)).TotalMinutes < 2) return false;
            string part = CommandPart(sessionDir, ffmpeg);
            if (part == null) return false;
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            XRCollab.Measurement.Mirroring.ChildProcess.StartDetached(cmd, RecordingFfmpeg.AudioMuxOnlyCommandLine(part));
            return true;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }
    }
}
