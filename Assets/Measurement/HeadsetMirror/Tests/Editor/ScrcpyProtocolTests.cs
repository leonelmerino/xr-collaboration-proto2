using System;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace XRCollab.Measurement.Mirroring.Tests
{
    public class ScrcpyProtocolTests
    {
        [Test]
        public void ServerCommand_RequestsVideoOnlyRawStreamOverForwardTunnel()
        {
            string cmd = ScrcpyProtocol.ServerCommand("1a2b3c4d", 1280, 30, 6_000_000);
            StringAssert.StartsWith($"CLASSPATH={ScrcpyProtocol.DeviceJarPath} app_process / com.genymobile.scrcpy.Server {ScrcpyProtocol.ServerVersion} ", cmd);
            foreach (string option in new[]
            {
                "scid=1a2b3c4d", "tunnel_forward=true", "audio=false", "control=false", "cleanup=true",
                "raw_stream=true", "max_size=1280", "max_fps=30", "video_bit_rate=6000000", "log_level=info",
            })
                StringAssert.Contains(option, cmd);
        }

        [Test]
        public void ServerLogLevel_KeepsTheReadyMarker()
        {
            // El arranque espera ReadyLogMarker, que es una línea INFO: con log_level más alto nunca aparecería.
            StringAssert.StartsWith("INFO:", ScrcpyProtocol.ReadyLogMarker);
            StringAssert.Contains("log_level=info", ScrcpyProtocol.ServerCommand("1a2b3c4d", 1280, 30, 1));
        }

        [Test]
        public void SessionId_IsEightHexDigitsOf31Bits()
        {
            var rng = new System.Random(1234);
            for (int i = 0; i < 1000; i++)
            {
                string id = ScrcpyProtocol.NewSessionId(rng);
                Assert.AreEqual(8, id.Length, id);
                uint value = uint.Parse(id, NumberStyles.HexNumber);
                Assert.Less(value, 0x80000000u, id);
            }
        }

        [Test]
        public void SocketName_MatchesServerConvention()
        {
            Assert.AreEqual("scrcpy_1a2b3c4d", ScrcpyProtocol.SocketName("1a2b3c4d"));
        }

        [Test]
        public void ServerAsset_IsInStreamingAssets()
        {
            string path = Path.Combine(Application.streamingAssetsPath, ScrcpyProtocol.ServerAssetPath);
            Assert.IsTrue(File.Exists(path), $"falta {path}");
            Assert.Greater(new FileInfo(path).Length, 100_000, "scrcpy-server parece truncado");
        }

        [Test]
        public void FfmpegArguments_DecodeToFlippedRgbaOfExactSize()
        {
            string args = FfmpegDecoder.Arguments(27183, 1280, 640);
            StringAssert.Contains("-f h264 -i tcp://127.0.0.1:27183", args);
            StringAssert.Contains("-vf scale=1280:640,vflip", args);
            StringAssert.Contains("-pix_fmt rgba -f rawvideo pipe:1", args);
            StringAssert.Contains("-nostdin", args);
        }

        [Test]
        public void FfmpegArguments_AvoidFlagsThatFreezeTheStream()
        {
            // Regresión 2026-09-29: con estos flags ffmpeg decodificaba un solo cuadro y se quedaba esperando.
            string args = FfmpegDecoder.Arguments(27183, 1280, 640);
            StringAssert.DoesNotContain("nobuffer", args);
            StringAssert.DoesNotContain("probesize", args);
        }

        [TestCase(4896, 2448, 1280, 1280, 640)]    // VIVE Focus Vision
        [TestCase(2448, 4896, 1280, 640, 1280)]    // vertical
        [TestCase(1000, 500, 4096, 1000, 500)]     // no agranda
        [TestCase(1921, 1081, 1920, 1920, 1080)]   // lados pares
        [TestCase(3000, 1, 1280, 1280, 2)]         // mínimo 2
        public void FrameGeometry_FitsLongestSideAndKeepsEvenSides(int dw, int dh, int max, int w, int h)
        {
            Assert.AreEqual((w, h), FrameGeometry.Fit(dw, dh, max));
        }

        [Test]
        public void FrameGeometry_RejectsInvalidInput()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameGeometry.Fit(0, 100, 1280));
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameGeometry.Fit(100, -1, 1280));
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameGeometry.Fit(100, 100, 1));
        }
    }
}
