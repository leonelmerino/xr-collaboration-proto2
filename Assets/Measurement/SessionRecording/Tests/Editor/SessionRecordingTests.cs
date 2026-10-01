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

        [TestCase(-3, 0, "-03:00")]
        [TestCase(5, 30, "+05:30")]
        [TestCase(0, 0, "+00:00")]
        public void OffsetFormat(int h, int m, string expected)
        {
            Assert.AreEqual(expected, SessionRecorder.FormatOffset(new TimeSpan(h, h < 0 ? -m : m, 0)));
        }
    }
}
