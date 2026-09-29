using System;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>
    /// Triple buffer de cuadros RGBA para un productor (hilo de captura) y un consumidor (hilo principal).
    /// El productor escribe siempre en <see cref="Back"/> y llama <see cref="Publish"/>; el consumidor llama
    /// <see cref="TryAcquire"/> y lee el buffer que recibe. Solo se intercambian referencias bajo un lock
    /// corto: sin copias ni allocations por cuadro. Si el consumidor se atrasa, ve siempre el último cuadro.
    /// </summary>
    public sealed class FrameTripleBuffer
    {
        public const int BytesPerPixel = 4;

        private readonly object _gate = new object();
        private byte[] _back;
        private byte[] _middle;
        private byte[] _front;
        private bool _middleIsNew;

        public int Width { get; }
        public int Height { get; }
        public int FrameBytes { get; }

        public FrameTripleBuffer(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            Width = width;
            Height = height;
            FrameBytes = checked(width * height * BytesPerPixel);
            _back = new byte[FrameBytes];
            _middle = new byte[FrameBytes];
            _front = new byte[FrameBytes];
        }

        /// <summary>Buffer donde el productor escribe el próximo cuadro. Solo para el productor.</summary>
        public byte[] Back => _back;

        /// <summary>Entrega el cuadro escrito en <see cref="Back"/>. Solo para el productor.</summary>
        public void Publish()
        {
            lock (_gate)
            {
                (_back, _middle) = (_middle, _back);
                _middleIsNew = true;
            }
        }

        /// <summary>
        /// Solo para el consumidor. Devuelve true si llegó un cuadro desde la llamada anterior. <paramref name="front"/>
        /// queda válido (y sin cambios) hasta la próxima llamada.
        /// </summary>
        public bool TryAcquire(out byte[] front)
        {
            lock (_gate)
            {
                if (_middleIsNew)
                {
                    (_front, _middle) = (_middle, _front);
                    _middleIsNew = false;
                    front = _front;
                    return true;
                }
            }
            front = _front;
            return false;
        }
    }
}
