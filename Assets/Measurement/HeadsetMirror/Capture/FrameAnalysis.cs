using System;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>Análisis barato de un cuadro RGBA, en el hilo de captura.</summary>
    public static class FrameAnalysis
    {
        /// <summary>
        /// Brillo medio (canal más alto de cada píxel, 0–255) bajo el cual un cuadro cuenta como "negro". La pantalla de
        /// espera de VIVE Streaming (fondo ~#08090D con un círculo de carga) queda debajo; una sala con passthrough,
        /// aunque esté poco iluminada, queda muy por encima (medido el 2026-09-29).
        /// </summary>
        public const int DarkThreshold = 14;

        private const int GridColumns = 32;
        private const int GridRows = 16;

        /// <summary>Muestrea una grilla de 32x16 píxeles: menos de 0,1 ms por cuadro de 1280x640.</summary>
        public static bool IsDark(byte[] rgba, int width, int height, int threshold = DarkThreshold)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) throw new ArgumentException("tamaño de cuadro inválido");

            long sum = 0;
            int samples = 0;
            for (int gy = 0; gy < GridRows; gy++)
            {
                int y = (int)((gy + 0.5) * height / GridRows);
                for (int gx = 0; gx < GridColumns; gx++)
                {
                    int x = (int)((gx + 0.5) * width / GridColumns);
                    int i = (y * width + x) * 4;
                    sum += Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2]));
                    samples++;
                }
            }
            return sum < (long)threshold * samples;
        }
    }
}
