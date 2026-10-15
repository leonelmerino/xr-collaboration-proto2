using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace XRCollab.Measurement.Recording.Tests
{
    public class SessionRecordingTests
    {
        [Test]
        public void Settings_DefaultsAndFlags()
        {
            SessionRecorderSettings d = SessionRecorderSettings.FromCommandLine(new string[0]);
            Assert.IsTrue(d.enabled);
            Assert.AreEqual(30, d.fps);
            Assert.AreEqual(1920, d.width);
            Assert.AreEqual(60, d.segmentSeconds);
            Assert.AreEqual(RecordingEncoder.Auto, d.encoder);

            SessionRecorderSettings s = SessionRecorderSettings.FromCommandLine(new[]
                { "-rec-fps", "20", "-rec-width", "1281", "-rec-segment", "30", "-rec-encoder", "qsv", "-rec-dir", @"D:\rec" });
            Assert.AreEqual(20, s.fps);
            Assert.AreEqual(1280, s.width);   // par
            Assert.AreEqual(30, s.segmentSeconds);
            Assert.AreEqual(RecordingEncoder.Qsv, s.encoder);
            Assert.AreEqual(@"D:\rec", s.outputRoot);

            Assert.IsFalse(SessionRecorderSettings.FromCommandLine(new[] { "-measurement", "-norecord" }).enabled);
            Assert.AreEqual(60, SessionRecorderSettings.FromCommandLine(new[] { "-rec-fps", "500" }).fps);
            Assert.AreEqual(RecordingEncoder.Auto, SessionRecorderSettings.FromCommandLine(new[] { "-rec-encoder", "av1" }).encoder);
        }

        [Test]
        public void Candidates_AutoTriesGpuEncodersFirst()
        {
            CollectionAssert.AreEqual(new[] { RecordingEncoder.Nvenc, RecordingEncoder.Qsv, RecordingEncoder.X264 },
                RecordingFfmpeg.Candidates(RecordingEncoder.Auto).ToArray());
            CollectionAssert.AreEqual(new[] { RecordingEncoder.X264 }, RecordingFfmpeg.Candidates(RecordingEncoder.X264).ToArray());
        }

        [Test]
        public void RecordArguments_RawRgbaFromStdinToSegmentedMpegTs()
        {
            string a = RecordingFfmpeg.RecordArguments(RecordingEncoder.Nvenc, 1920, 1200, 30, 60, @"C:\Users\Jose Carter\rec\segments\seg_%05d.ts", 7);
            StringAssert.Contains("-use_wallclock_as_timestamps 1 -f rawvideo -pix_fmt rgba -video_size 1920x1200 -framerate 30 -i pipe:0", a);
            StringAssert.Contains("-c:v h264_nvenc", a);
            StringAssert.Contains("-g 60 -bf 0", a);
            StringAssert.Contains("-f segment -segment_time 60 -segment_format mpegts -reset_timestamps 1 -segment_start_number 7", a);
            StringAssert.EndsWith("\"C:\\Users\\Jose Carter\\rec\\segments\\seg_%05d.ts\"", a);   // ruta con espacio, entre comillas
        }

        [TestCase(RecordingEncoder.Nvenc, "h264_nvenc")]
        [TestCase(RecordingEncoder.Qsv, "h264_qsv")]
        [TestCase(RecordingEncoder.X264, "libx264")]
        public void EveryEncoderHasArguments(RecordingEncoder e, string codec)
        {
            StringAssert.Contains("-c:v " + codec, RecordingFfmpeg.EncoderArguments(e, 30));
            StringAssert.Contains("-f null -", RecordingFfmpeg.ProbeArguments(e));
        }

        [Test]
        public void ConcatList_UsesForwardSlashesAndEscapesQuotes()
        {
            string list = RecordingFfmpeg.ConcatList(new[] { @"C:\Users\Jose Carter\a\seg_00000.ts", @"C:\it's\seg_00001.ts" });
            Assert.AreEqual("file 'C:/Users/Jose Carter/a/seg_00000.ts'\nfile 'C:/it'\\''s/seg_00001.ts'\n", list);
        }

        [Test]
        public void FinalizeCommand_WritesPartialThenRenames()
        {
            string c = RecordingFfmpeg.FinalizeCommandLine(@"C:\ff\ffmpeg.exe", @"C:\r\s\concat.txt", @"C:\r\s\s.mp4.partial", @"C:\r\s\s.mp4");
            StringAssert.StartsWith("/d /s /c \"\"C:\\ff\\ffmpeg.exe\"", c);
            StringAssert.Contains("-f concat -safe 0 -i \"C:\\r\\s\\concat.txt\" -c copy", c);
            StringAssert.Contains("-f mp4 -y \"C:\\r\\s\\s.mp4.partial\" && move /y \"C:\\r\\s\\s.mp4.partial\" \"C:\\r\\s\\s.mp4\"", c);
            StringAssert.EndsWith("\"", c);
        }

        [TestCase(2560, 1600, 1920, 1920, 1200)]
        [TestCase(1280, 800, 1920, 1280, 800)]    // no agranda
        [TestCase(1921, 1081, 1920, 1920, 1080)]
        public void OutputSize_KeepsAspectAndEvenSides(int sw, int sh, int target, int w, int h)
        {
            Assert.AreEqual((w, h), RecordingFfmpeg.OutputSize(sw, sh, target));
        }

        [Test]
        public void Layout_NamesAndPendingSessions()
        {
            Assert.AreEqual("20261001_102345_STIMULUS1", RecordingLayout.SessionName(new DateTime(2026, 10, 1, 10, 23, 45), "STIMULUS1"));
            string root = Path.Combine(Path.GetTempPath(), "rec-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                string done = Path.Combine(root, "20261001_090000_S1");
                string crashed = Path.Combine(root, "20261001_100000_S1");
                string empty = Path.Combine(root, "20261001_110000_S1");
                string current = Path.Combine(root, "20261001_120000_S1");
                foreach (string d in new[] { done, crashed, empty, current }) Directory.CreateDirectory(Path.Combine(d, RecordingLayout.SegmentsFolder));
                File.WriteAllText(Path.Combine(done, RecordingLayout.SegmentsFolder, "seg_00000.ts"), "x");
                File.WriteAllText(RecordingLayout.FinalVideoPath(done), "x");
                File.WriteAllText(Path.Combine(crashed, RecordingLayout.SegmentsFolder, "seg_00000.ts"), "x");
                File.WriteAllText(Path.Combine(crashed, RecordingLayout.SegmentsFolder, "seg_00003.ts"), "x");
                File.WriteAllText(Path.Combine(current, RecordingLayout.SegmentsFolder, "seg_00000.ts"), "x");

                CollectionAssert.AreEqual(new[] { crashed }, RecordingLayout.PendingSessions(root, current).ToArray());
                Assert.AreEqual(4, RecordingLayout.NextSegmentNumber(crashed));
                Assert.AreEqual(0, RecordingLayout.NextSegmentNumber(empty));
                Assert.AreEqual(2, RecordingLayout.Segments(crashed).Count);
                StringAssert.EndsWith(@"20261001_090000_S1\20261001_090000_S1.mp4", RecordingLayout.FinalVideoPath(done));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void Layout_MissingRootIsEmpty()
        {
            CollectionAssert.IsEmpty(RecordingLayout.PendingSessions(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid()), null).ToArray());
        }

        [Test]
        public void Settings_ControlFlags()
        {
            SessionRecorderSettings d = SessionRecorderSettings.FromCommandLine(new string[0]);
            Assert.IsFalse(d.autoStart);   // por defecto espera un «start» externo
            Assert.AreEqual(SessionRecorderSettings.DefaultControlPort, d.controlPort);
            Assert.IsTrue(SessionRecorderSettings.FromCommandLine(new[] { "-rec-auto" }).autoStart);
            Assert.AreEqual(0, SessionRecorderSettings.FromCommandLine(new[] { "-rec-nocontrol" }).controlPort);
            Assert.AreEqual(50000, SessionRecorderSettings.FromCommandLine(new[] { "-rec-control-port", "50000" }).controlPort);
            Assert.AreEqual(SessionRecorderSettings.DefaultControlPort, SessionRecorderSettings.FromCommandLine(new[] { "-rec-control-port", "99999" }).controlPort);
        }

        [Test]
        public void Command_StartWithLabelAndTime()
        {
            RecordingCommand c = RecordingCommand.Parse("  START label=S01 at=2026-10-01T18:00:05.250Z\r", out string error);
            Assert.IsNull(error);
            Assert.AreEqual(RecordingCommandKind.Start, c.Kind);
            Assert.AreEqual("S01", c.Label);
            Assert.AreEqual(new DateTime(2026, 10, 1, 18, 0, 5, 250, DateTimeKind.Utc), c.AtUtc);
            Assert.AreEqual(DateTimeKind.Utc, c.AtUtc.Value.Kind);

            RecordingCommand bare = RecordingCommand.Parse("start", out error);
            Assert.AreEqual("", bare.Label);
            Assert.IsNull(bare.AtUtc);
        }

        [Test]
        public void Command_OffsetTimesBecomeUtc()
        {
            RecordingCommand c = RecordingCommand.Parse("stop at=2026-10-01T15:00:00.000-03:00", out _);
            Assert.AreEqual(new DateTime(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc), c.AtUtc);
        }

        [Test]
        public void Command_CueKeepsTextAndSnapshotWidth()
        {
            RecordingCommand c = RecordingCommand.Parse("cue bad|cross|MAL COLOCADO|Quedó torcido, otra vez\r", out string error);
            Assert.IsNull(error);
            Assert.AreEqual(RecordingCommandKind.Cue, c.Kind);
            Assert.AreEqual("bad|cross|MAL COLOCADO|Quedó torcido, otra vez", c.Text);
            Assert.AreEqual("clear", RecordingCommand.Parse("CUE clear", out _).Text);

            RecordingCommand s = RecordingCommand.Parse("snapshot", out error);
            Assert.AreEqual(RecordingCommandKind.Snapshot, s.Kind);
            Assert.AreEqual(RecordingCommand.DefaultSnapshotWidth, s.Width);
            Assert.AreEqual(320, RecordingCommand.Parse("snapshot w=320", out _).Width);
        }

        [Test]
        public void Command_OcclusionAndTurn()
        {
            RecordingCommand o = RecordingCommand.Parse("occlusion SHOW", out string error);
            Assert.IsNull(error);
            Assert.AreEqual(RecordingCommandKind.Occlusion, o.Kind);
            Assert.AreEqual("show", o.Text);
            Assert.AreEqual("offset=-35", RecordingCommand.Parse("occlusion offset=-35", out _).Text);
            Assert.AreEqual("feather=0.4", RecordingCommand.Parse("occlusion feather=0.4", out _).Text);

            RecordingCommand t = RecordingCommand.Parse("turn start\r", out error);
            Assert.IsNull(error);
            Assert.AreEqual(RecordingCommandKind.Turn, t.Kind);
            Assert.AreEqual("start", t.Text);
            Assert.AreEqual("place", RecordingCommand.Parse("turn place", out _).Text);
            Assert.AreEqual("style ghost", RecordingCommand.Parse("turn style  Ghost", out _).Text);
            Assert.AreEqual("mode gesture", RecordingCommand.Parse("turn mode gesture", out _).Text);
        }

        [TestCase("cue")]
        [TestCase("occlusion")]
        [TestCase("occlusion maybe")]
        [TestCase("occlusion offset=abc")]
        [TestCase("turn")]
        [TestCase("turn next")]
        [TestCase("turn style glass")]
        [TestCase("turn mode")]
        [TestCase("snapshot w=10")]
        [TestCase("snapshot big")]
        [TestCase("record")]
        [TestCase("start label=S 01")]
        [TestCase("start label=../x")]
        [TestCase("start at=mañana")]
        [TestCase("stop label=S01")]
        [TestCase("start foo=1")]
        [TestCase("")]
        public void Command_InvalidIsRejected(string line)
        {
            Assert.IsNull(RecordingCommand.Parse(line, out string error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void Command_StatusAndMark()
        {
            Assert.AreEqual(RecordingCommandKind.Status, RecordingCommand.Parse("status", out _).Kind);
            Assert.AreEqual(RecordingCommandKind.Status, RecordingCommand.Parse("ping", out _).Kind);
            RecordingCommand m = RecordingCommand.Parse("mark aplauso, inicio \"S01\"", out _);
            Assert.AreEqual(RecordingCommandKind.Mark, m.Kind);
            Assert.AreEqual("aplauso  inicio  S01", m.Text);   // sin comas ni comillas: va directo al CSV
        }

        [Test]
        public void Layout_LabeledNamesFramesAndMarks()
        {
            Assert.AreEqual("20261001_150005_STIMULUS1_S01", RecordingLayout.SessionName(new DateTime(2026, 10, 1, 15, 0, 5), "STIMULUS1", "S01"));
            Assert.AreEqual("20261001_150005_STIMULUS1", RecordingLayout.SessionName(new DateTime(2026, 10, 1, 15, 0, 5), "STIMULUS1", ""));
            var utc = new DateTime(2026, 10, 1, 18, 0, 5, 123, DateTimeKind.Utc);
            Assert.AreEqual("12,2026-10-01T18:00:05.123000Z,1790877605123.000,4567,12.500000", RecordingLayout.FramesRow(12, utc, 4567, 12.5));
            Assert.AreEqual(5, RecordingLayout.FramesHeader.Split(',').Length);
            Assert.AreEqual(6, RecordingLayout.MarksHeader.Split(',').Length);
            Assert.AreEqual(6, RecordingLayout.MarksRow(utc, 1, "S1", "S01", "aplauso").Split(',').Length);

            string root = Path.Combine(Path.GetTempPath(), "rec-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "a"));
                Assert.AreEqual(Path.Combine(root, "a_2"), RecordingLayout.UniqueSessionDir(root, "a"));
                Assert.AreEqual(Path.Combine(root, "b"), RecordingLayout.UniqueSessionDir(root, "b"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void Status_IsOneJsonLine()
        {
            string json = RecordingControlServer.ErrorJson("x");
            StringAssert.DoesNotContain("\n", json);
            StringAssert.Contains("\"ok\":false", json);
            StringAssert.Contains("\"error\":\"x\"", json);
        }

        // ---------------- audio ----------------

        [Test]
        public void Settings_AudioFlags()
        {
            SessionRecorderSettings d = SessionRecorderSettings.FromCommandLine(new string[0]);
            Assert.IsTrue(d.audio);
            Assert.IsTrue(d.mic);
            Assert.AreEqual("", d.micDevice);
            Assert.IsFalse(SessionRecorderSettings.FromCommandLine(new[] { "-rec-audio", "off" }).audio);
            Assert.IsTrue(SessionRecorderSettings.FromCommandLine(new[] { "-rec-audio", "on" }).audio);
            Assert.IsFalse(SessionRecorderSettings.FromCommandLine(new[] { "-rec-mic", "off" }).mic);
            SessionRecorderSettings named = SessionRecorderSettings.FromCommandLine(new[] { "-rec-mic", "Focus Vision", "-rec-fps", "20" });
            Assert.IsTrue(named.mic);
            Assert.AreEqual("Focus Vision", named.micDevice);
            Assert.AreEqual(20, named.fps);
            Assert.AreEqual("", SessionRecorderSettings.FromCommandLine(new[] { "-rec-mic", "-rec-auto" }).micDevice);   // falta el valor
        }

        [Test]
        public void ChooseMic_PrefersHeadsetThenDefault()
        {
            string[] devices = { "Micrófono (Realtek(R) Audio)", "Microphone (VIVE Streaming Audio)", "Headset Microphone (Oculus Virtual Audio Device)" };
            Assert.AreEqual("Microphone (VIVE Streaming Audio)", RecordingAudioDevices.ChooseMic(devices, "", out _));
            Assert.AreEqual("Headset Microphone (Oculus Virtual Audio Device)", RecordingAudioDevices.ChooseMic(devices, "oculus", out _));
            Assert.AreEqual("Micrófono (Realtek(R) Audio)", RecordingAudioDevices.ChooseMic(devices, "nada, realtek", out _));
            Assert.AreEqual("", RecordingAudioDevices.ChooseMic(new[] { "Micrófono (Realtek(R) Audio)" }, "", out string why));   // predeterminado
            StringAssert.Contains("predeterminado", why);
            Assert.IsNull(RecordingAudioDevices.ChooseMic(new string[0], "", out _));
        }

        [Test]
        public void WavHeader_Is44BytesPcm16()
        {
            byte[] h = AudioWavWriter.Header(48000, 2, 1000);
            Assert.AreEqual(44, h.Length);
            Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(h, 0, 4));
            Assert.AreEqual(1036u, BitConverter.ToUInt32(h, 4));
            Assert.AreEqual("WAVEfmt ", System.Text.Encoding.ASCII.GetString(h, 8, 8));
            Assert.AreEqual(1, BitConverter.ToUInt16(h, 20));          // PCM
            Assert.AreEqual(2, BitConverter.ToUInt16(h, 22));          // canales
            Assert.AreEqual(48000u, BitConverter.ToUInt32(h, 24));
            Assert.AreEqual(192000u, BitConverter.ToUInt32(h, 28));    // bytes/s
            Assert.AreEqual(4, BitConverter.ToUInt16(h, 32));
            Assert.AreEqual(16, BitConverter.ToUInt16(h, 34));
            Assert.AreEqual("data", System.Text.Encoding.ASCII.GetString(h, 36, 4));
            Assert.AreEqual(1000u, BitConverter.ToUInt32(h, 40));

            var pcm = new byte[6];
            AudioWavWriter.ToPcm16(new[] { 0f, 2f, -1f }, 3, pcm);
            Assert.AreEqual(0, BitConverter.ToInt16(pcm, 0));
            Assert.AreEqual(32767, BitConverter.ToInt16(pcm, 2));     // recortado
            Assert.AreEqual(-32767, BitConverter.ToInt16(pcm, 4));
        }

        [Test]
        public void WavWriter_WritesReadableFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "wav-test-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                var w = new AudioWavWriter(path, 8000, 1);
                var block = new float[800];
                for (int i = 0; i < 25; i++) w.Write(block, block.Length);   // 2,5 s
                w.Close();
                Assert.IsTrue(w.WaitClosed(5000));
                Assert.AreEqual(20000, w.FramesWritten);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.AreEqual(44 + 40000, bytes.Length);
                Assert.AreEqual(40000u, BitConverter.ToUInt32(bytes, 40));
                Assert.AreEqual(40036u, BitConverter.ToUInt32(bytes, 4));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void ClockFit_EstimatesStartAndDrift()
        {
            var t0 = new DateTime(2026, 10, 8, 15, 0, 0, DateTimeKind.Utc);
            var fit = new AudioClockFit(48000);
            // El reloj de audio corre 100 ppm rápido: 48004,8 muestras por segundo real. Primera observación en la
            // muestra 4800 (la muestra 0 pasó 0,1 s antes, medido con el reloj real).
            const double realRate = 48004.8;
            var rng = new Random(1);
            for (long frame = 4800; frame < 48000L * 120; frame += 1024)
            {
                double seconds = frame / realRate + (rng.NextDouble() - 0.5) * 0.02;   // ±10 ms de ruido por callback
                fit.Add(frame, t0.AddTicks((long)(seconds * TimeSpan.TicksPerSecond)));
            }
            Assert.IsTrue(fit.TryFit(out _, out _));
            Assert.AreEqual(realRate, fit.EffectiveRate, 0.5);
            Assert.AreEqual(0.0, (fit.StartUtc - t0).TotalMilliseconds, 2.0);

            var few = new AudioClockFit(48000);
            few.Add(4800, t0.AddSeconds(0.1));
            Assert.IsFalse(few.TryFit(out _, out _));
            Assert.AreEqual(48000, few.EffectiveRate);
            Assert.AreEqual(0.0, (few.StartUtc - t0).TotalMilliseconds, 0.01);
        }

        [Test]
        public void AudioFilterChain_DelaysTrimsAndCorrectsDrift()
        {
            Assert.AreEqual("anull", RecordingFfmpeg.AudioFilterChain(0.0001, 1.0));
            Assert.AreEqual("adelay=delays=250:all=1", RecordingFfmpeg.AudioFilterChain(0.25, 1.0));
            Assert.AreEqual("atrim=start=0.125000,asetpts=PTS-STARTPTS", RecordingFfmpeg.AudioFilterChain(-0.125, 1.0));
            Assert.AreEqual("atempo=1.00010000,adelay=delays=40:all=1", RecordingFfmpeg.AudioFilterChain(0.04, 1.0001));

            Assert.AreEqual(1.0, RecordingFfmpeg.DriftTempo(48000, 48004.8, 10));      // corta: no se corrige
            Assert.AreEqual(1.0001, RecordingFfmpeg.DriftTempo(48000, 48004.8, 600), 1e-9);
            Assert.AreEqual(1.0, RecordingFfmpeg.DriftTempo(48000, 48000.2, 600));     // < 10 ppm
            Assert.AreEqual(1.0, RecordingFfmpeg.DriftTempo(48000, 49000, 600));       // absurdo
        }

        [Test]
        public void AudioMux_TwoNamedTracksVideoCopied()
        {
            var tracks = new[]
            {
                new AudioMuxTrack { Name = "app", FilePath = @"C:\r\s\audio_app.wav", OffsetSeconds = 0.05, Tempo = 1.0 },
                new AudioMuxTrack { Name = "mic", FilePath = @"C:\r\s\audio_mic.wav", OffsetSeconds = -0.2, Tempo = 1.0 },
            };
            string a = RecordingFfmpeg.AudioMuxArguments(@"C:\r\s\s.mp4", tracks, @"C:\r\s\s.mp4.av.partial");
            StringAssert.Contains("-i \"C:\\r\\s\\s.mp4\" -i \"C:\\r\\s\\audio_app.wav\" -i \"C:\\r\\s\\audio_mic.wav\"", a);
            StringAssert.Contains("-filter_complex \"[1:a]adelay=delays=50:all=1[a0];[2:a]atrim=start=0.200000,asetpts=PTS-STARTPTS[a1]\"", a);
            StringAssert.Contains("-map 0:v:0 -map \"[a0]\" -map \"[a1]\" -c:v copy -c:a aac", a);
            StringAssert.Contains("-metadata:s:a:0 title=app", a);
            StringAssert.Contains("-metadata:s:a:1 title=mic", a);
            StringAssert.EndsWith("-f mp4 -y \"C:\\r\\s\\s.mp4.av.partial\"", a);

            string part = RecordingFfmpeg.AudioMuxCommandPart(@"C:\ff\ffmpeg.exe", "ARGS", @"C:\r\s\s.mp4.av.partial", @"C:\r\s\s.mp4",
                @"C:\r\s\audio_mux.log", @"C:\r\s\audio_mux.done", @"C:\r\s\audio_mux.failed");
            Assert.AreEqual("(\"C:\\ff\\ffmpeg.exe\" ARGS 2>\"C:\\r\\s\\audio_mux.log\" && move /y \"C:\\r\\s\\s.mp4.av.partial\" \"C:\\r\\s\\s.mp4\" >nul" +
                            " && echo ok>\"C:\\r\\s\\audio_mux.done\" || echo failed>\"C:\\r\\s\\audio_mux.failed\")", part);

            // El audio va después del video final, solo si éste quedó bien (&&).
            string c = RecordingFfmpeg.FinalizeCommandLine(@"C:\ff\ffmpeg.exe", @"C:\r\s\concat.txt", @"C:\r\s\s.mp4.partial", @"C:\r\s\s.mp4", part);
            StringAssert.Contains("move /y \"C:\\r\\s\\s.mp4.partial\" \"C:\\r\\s\\s.mp4\" >nul && (\"C:\\ff\\ffmpeg.exe\" ARGS", c);
            StringAssert.EndsWith(")\"", c);
        }

        [Test]
        public void AudioMux_TracksFromSessionInfo()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rec-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, RecordingLayout.AppAudioFile), new byte[44 + 100]);
                var info = new RecordingSessionInfo
                {
                    firstFrameUtc = "2026-10-08T15:00:00.000Z",
                    audioApp = new AudioTrackInfo { file = RecordingLayout.AppAudioFile, startUtc = "2026-10-08T15:00:00.120000Z", sampleRate = 48000, effectiveRate = 48000, seconds = 1 },
                    audioMic = new AudioTrackInfo { file = RecordingLayout.MicAudioFile, startUtc = "2026-10-08T14:59:59.900000Z" },   // sin WAV
                };
                var tracks = RecordingAudioMux.Tracks(dir, info);
                Assert.AreEqual(1, tracks.Count);
                Assert.AreEqual("app", tracks[0].Name);
                Assert.AreEqual(0.12, tracks[0].OffsetSeconds, 1e-6);
                Assert.AreEqual(1.0, tracks[0].Tempo);
                Assert.IsTrue(RecordingAudioMux.HasAudioFiles(dir));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [TestCase(-3, 0, "-03:00")]
        [TestCase(5, 30, "+05:30")]
        [TestCase(0, 0, "+00:00")]
        public void OffsetFormat(int h, int m, string expected)
        {
            Assert.AreEqual(expected, SessionRecorder.FormatOffset(new TimeSpan(h, h < 0 ? -m : m, 0)));
        }
    }
}
