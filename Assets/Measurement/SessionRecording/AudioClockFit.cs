using System;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Relación entre el número de muestra de un WAV y la hora UTC del sistema (la que corrige NTP, la del video).
    /// Cada observación es «la muestra N pasó por la app a la hora T» (callback de audio o lectura del micrófono).
    /// Un ajuste lineal por mínimos cuadrados sobre toda la grabación da:
    ///   - <see cref="StartUtc"/>: la hora de la muestra 0, sin el ruido de un solo callback (±1 buffer);
    ///   - <see cref="EffectiveRate"/>: la frecuencia real contra el reloj del sistema (los relojes de audio derivan
    ///     decenas de ppm: en 40 min son decenas de ms).
    /// Con pocas observaciones o menos de <see cref="MinSpanSeconds"/>, usa la primera observación tal cual.
    /// No es thread-safe: quien la usa la protege con su lock. Pura, con tests.
    /// </summary>
    public sealed class AudioClockFit
    {
        public const double MinSpanSeconds = 10.0;
        public const int MinPoints = 20;

        private readonly int _rate;
        private bool _has;
        private DateTime _t0;
        private long _frame0;
        private double _n, _sx, _sy, _sxx, _sxy;
        private double _maxX;

        public AudioClockFit(int nominalRate)
        {
            if (nominalRate <= 0) throw new ArgumentOutOfRangeException(nameof(nominalRate));
            _rate = nominalRate;
        }

        public int NominalRate => _rate;
        public bool HasData => _has;
        public int Points => (int)_n;

        /// <summary>Hora de la primera observación (UTC).</summary>
        public DateTime FirstUtc => _t0;

        public void Add(long frame, DateTime utc)
        {
            if (!_has)
            {
                _has = true;
                _t0 = utc;
                _frame0 = frame;
            }
            double x = (frame - _frame0) / (double)_rate;   // segundos nominales desde la primera observación
            double y = (utc - _t0).TotalSeconds;             // segundos reales desde la primera observación
            _n++;
            _sx += x;
            _sy += y;
            _sxx += x * x;
            _sxy += x * y;
            if (x > _maxX) _maxX = x;
        }

        /// <summary>y = intercept + slope·x (x: segundos nominales; y: segundos reales). slope = 1 / (deriva).</summary>
        public bool TryFit(out double slope, out double intercept)
        {
            slope = 1.0;
            intercept = 0.0;
            if (!_has || _n < MinPoints || _maxX < MinSpanSeconds) return false;
            double den = _n * _sxx - _sx * _sx;
            if (den <= 0) return false;
            double s = (_n * _sxy - _sx * _sy) / den;
            if (s < 0.98 || s > 1.02) return false;   // absurdo (reloj del sistema saltó, se perdieron muestras): no confiar
            slope = s;
            intercept = (_sy - s * _sx) / _n;
            return true;
        }

        /// <summary>Hora UTC estimada de la muestra 0 del archivo.</summary>
        public DateTime StartUtc
        {
            get
            {
                if (!_has) return default;
                double xOfFrameZero = -_frame0 / (double)_rate;
                double seconds = TryFit(out double s, out double b) ? b + s * xOfFrameZero : xOfFrameZero;
                return _t0.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
            }
        }

        /// <summary>Muestras por segundo real (reloj del sistema). Sin ajuste confiable, la nominal.</summary>
        public double EffectiveRate => TryFit(out double s, out _) ? _rate / s : _rate;
    }
}
