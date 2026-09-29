using System;
using NUnit.Framework;

namespace XRCollab.Measurement.Mirroring.Tests
{
    public class FrameAnalysisTests
    {
        private static byte[] Solid(int w, int h, byte r, byte g, byte b)
        {
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }
            return rgba;
        }

        [Test]
        public void Black_IsDark()
        {
            Assert.IsTrue(FrameAnalysis.IsDark(Solid(128, 64, 0, 0, 0), 128, 64));
        }

        [Test]
        public void ViveWaitingScreen_IsDark()
        {
            // Fondo de la pantalla de espera de VIVE Streaming medido en una captura: ~(7, 8, 9).
            Assert.IsTrue(FrameAnalysis.IsDark(Solid(128, 64, 7, 8, 9), 128, 64));
        }

        [Test]
        public void DimRoom_IsNotDark()
        {
            Assert.IsFalse(FrameAnalysis.IsDark(Solid(128, 64, 40, 35, 30), 128, 64));
        }

        [Test]
        public void SmallBrightSpinnerOnDarkBackground_IsStillDark()
        {
            byte[] frame = Solid(128, 64, 8, 8, 9);
            for (int y = 28; y < 36; y++)
            for (int x = 60; x < 68; x++)
            {
                int i = (y * 128 + x) * 4;
                frame[i] = frame[i + 1] = frame[i + 2] = 230;
            }
            Assert.IsTrue(FrameAnalysis.IsDark(frame, 128, 64));
        }

        [Test]
        public void UsesTheBrightestChannel()
        {
            // Un cuadro rojo intenso no es "negro" aunque verde y azul estén en cero.
            Assert.IsFalse(FrameAnalysis.IsDark(Solid(64, 32, 200, 0, 0), 64, 32));
        }

        [Test]
        public void InvalidInput_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => FrameAnalysis.IsDark(null, 2, 2));
            Assert.Throws<ArgumentException>(() => FrameAnalysis.IsDark(new byte[4], 2, 2));
        }
    }
}
