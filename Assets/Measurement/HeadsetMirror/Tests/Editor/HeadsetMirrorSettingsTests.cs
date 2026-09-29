using NUnit.Framework;

namespace XRCollab.Measurement.Mirroring.Tests
{
    public class HeadsetMirrorSettingsTests
    {
        [Test]
        public void NoArguments_KeepsDefaults()
        {
            HeadsetMirrorSettings s = HeadsetMirrorSettings.FromCommandLine(new string[0]);
            Assert.IsTrue(s.enabled);
            Assert.AreEqual(1280, s.maxSize);
            Assert.AreEqual(30, s.maxFps);
            Assert.AreEqual(6_000_000, s.videoBitRate);
            Assert.AreEqual(MirrorLayout.BothEyes, s.layout);
            Assert.IsTrue(s.visibleOnStart);
            Assert.AreEqual("", s.deviceSerial);
        }

        [Test]
        public void NullArguments_KeepsDefaults()
        {
            Assert.IsTrue(HeadsetMirrorSettings.FromCommandLine(null).enabled);
        }

        [Test]
        public void NoMirror_Disables()
        {
            Assert.IsFalse(HeadsetMirrorSettings.FromCommandLine(new[] { "-measurement", "-nomirror" }).enabled);
        }

        [Test]
        public void ParsesEveryFlag()
        {
            HeadsetMirrorSettings s = HeadsetMirrorSettings.FromCommandLine(new[]
            {
                "-mirror-serial", "FA5AR3N00187", "-mirror-size", "960", "-mirror-fps", "20",
                "-mirror-bitrate", "4M", "-mirror-layout", "left", "-mirror-hidden",
                "-mirror-adb", @"C:\adb\adb.exe", "-mirror-ffmpeg", @"C:\ff\ffmpeg.exe",
            });
            Assert.AreEqual("FA5AR3N00187", s.deviceSerial);
            Assert.AreEqual(960, s.maxSize);
            Assert.AreEqual(20, s.maxFps);
            Assert.AreEqual(4_000_000, s.videoBitRate);
            Assert.AreEqual(MirrorLayout.LeftEye, s.layout);
            Assert.IsFalse(s.visibleOnStart);
            Assert.AreEqual(@"C:\adb\adb.exe", s.adbPath);
            Assert.AreEqual(@"C:\ff\ffmpeg.exe", s.ffmpegPath);
        }

        [Test]
        public void FlagsAreCaseInsensitive()
        {
            Assert.AreEqual(640, HeadsetMirrorSettings.FromCommandLine(new[] { "-Mirror-Size", "640" }).maxSize);
        }

        [Test]
        public void InvalidValue_IsIgnoredAndNextFlagStillParsed()
        {
            HeadsetMirrorSettings s = HeadsetMirrorSettings.FromCommandLine(new[] { "-mirror-size", "grande", "-mirror-fps", "15" });
            Assert.AreEqual(1280, s.maxSize);
            Assert.AreEqual(15, s.maxFps);
        }

        [Test]
        public void TextFlag_DoesNotSwallowTheNextFlag()
        {
            HeadsetMirrorSettings s = HeadsetMirrorSettings.FromCommandLine(new[] { "-mirror-serial", "-nomirror" });
            Assert.AreEqual("", s.deviceSerial);
            Assert.IsFalse(s.enabled);
        }

        [Test]
        public void MissingValueAtTheEnd_IsIgnored()
        {
            Assert.AreEqual(1280, HeadsetMirrorSettings.FromCommandLine(new[] { "-mirror-size" }).maxSize);
        }

        [Test]
        public void Validate_ClampsAndKeepsSizeEven()
        {
            Assert.AreEqual(HeadsetMirrorSettings.MinSize, HeadsetMirrorSettings.FromCommandLine(new[] { "-mirror-size", "10" }).maxSize);
            Assert.AreEqual(HeadsetMirrorSettings.MaxSizeLimit, HeadsetMirrorSettings.FromCommandLine(new[] { "-mirror-size", "99999" }).maxSize);
            Assert.AreEqual(1000, HeadsetMirrorSettings.FromCommandLine(new[] { "-mirror-size", "1001" }).maxSize);
            Assert.AreEqual(90, HeadsetMirrorSettings.FromCommandLine(new[] { "-mirror-fps", "240" }).maxFps);
        }

        [TestCase("6000000", 6_000_000)]
        [TestCase("6M", 6_000_000)]
        [TestCase("2.5m", 2_500_000)]
        [TestCase("8000k", 8_000_000)]
        public void BitRate_AcceptsSuffixes(string text, int expected)
        {
            Assert.IsTrue(HeadsetMirrorSettings.TryParseBitRate(text, out int bits, 0));
            Assert.AreEqual(expected, bits);
        }

        [TestCase("")]
        [TestCase("M")]
        [TestCase("-3M")]
        [TestCase("rápido")]
        [TestCase("99999M")]
        public void BitRate_RejectsInvalid(string text)
        {
            Assert.IsFalse(HeadsetMirrorSettings.TryParseBitRate(text, out int bits, 123));
            Assert.AreEqual(123, bits);
        }

        [Test]
        public void FromCommandLine_DoesNotModifyTheDefaultsObject()
        {
            var defaults = new HeadsetMirrorSettings();
            HeadsetMirrorSettings.FromCommandLine(new[] { "-mirror-size", "640" }, defaults);
            Assert.AreEqual(1280, defaults.maxSize);
        }
    }
}
