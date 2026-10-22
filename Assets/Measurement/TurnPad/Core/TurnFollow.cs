using System;

namespace XRCollab.Measurement.Turns
{
    /// <summary>
    /// El botón de turno acompaña al participante «con calma», sin Unity (tests en EditMode). Guarda un ancla: el eje
    /// vertical del participante (x, z) y hacia dónde mira su cuerpo (yaw). El botón vive en un círculo alrededor de
    /// ese eje: a un ángulo respecto del frente del cuerpo, a una distancia y a una altura desde el piso (fija).
    ///  - Movimientos chicos (inclinarse sobre la torre, mirar de reojo) no lo mueven: zona muerta de
    ///    <see cref="PosDeadzone"/> m y <see cref="YawDeadzone"/>°, que además tiene que durar <see cref="PosDwell"/> /
    ///    <see cref="YawDwell"/> s.
    ///  - Pasada la zona muerta, el ancla sigue a la cabeza con un resorte críticamente amortiguado (sin rebote) hasta
    ///    quedar a <see cref="PosSettle"/> m y <see cref="YawSettle"/>°: ahí se detiene. Caminar = lo sigue suave;
    ///    detenerse = se queda en su lugar cómodo.
    ///  - Congelado (una mano va hacia el botón, se está presionando o moviendo): no se mueve nada.
    /// Ángulos en grados, yaw como Unity (0 = +z, 90 = +x).
    /// </summary>
    public sealed class TurnFollow
    {
        public float PosDeadzone = 0.22f, PosSettle = 0.03f, PosDwell = 0.4f, PosSmooth = 0.45f;
        public float YawDeadzone = 45f, YawSettle = 3f, YawDwell = 0.8f, YawSmooth = 0.6f;

        public float X { get; private set; }
        public float Z { get; private set; }
        public float Yaw { get; private set; }
        public bool Initialized { get; private set; }
        public bool Following => _posFollow || _yawFollow;

        private bool _posFollow, _yawFollow;
        private float _posOut, _yawOut, _vx, _vz, _vyaw;

        /// <summary>El ancla salta a la cabeza (al colocar el botón).</summary>
        public void Snap(float x, float z, float yaw)
        {
            X = x;
            Z = z;
            Yaw = Wrap(yaw);
            Initialized = true;
            _posFollow = _yawFollow = false;
            _posOut = _yawOut = _vx = _vz = _vyaw = 0f;
        }

        /// <summary>Solo la posición salta a la cabeza; el frente del cuerpo queda.</summary>
        public void SnapPosition(float x, float z)
        {
            if (!Initialized) { Snap(x, z, 0f); return; }
            X = x;
            Z = z;
            _posFollow = false;
            _posOut = _vx = _vz = 0f;
        }

        public void Update(float headX, float headZ, float headYaw, float dt, bool frozen)
        {
            if (!Initialized) { Snap(headX, headZ, headYaw); return; }
            if (dt <= 0f) return;
            if (frozen)
            {
                _posOut = _yawOut = 0f;
                _vx = _vz = _vyaw = 0f;
                return;
            }

            float d = Dist(headX - X, headZ - Z);
            if (!_posFollow)
            {
                _posOut = d > PosDeadzone ? _posOut + dt : 0f;
                if (_posOut >= PosDwell) _posFollow = true;
            }
            if (_posFollow)
            {
                X = SmoothDamp(X, headX, ref _vx, PosSmooth, dt);
                Z = SmoothDamp(Z, headZ, ref _vz, PosSmooth, dt);
                if (Dist(headX - X, headZ - Z) < PosSettle) { _posFollow = false; _posOut = _vx = _vz = 0f; }
            }

            float dy = Delta(Yaw, headYaw);
            if (!_yawFollow)
            {
                _yawOut = Math.Abs(dy) > YawDeadzone ? _yawOut + dt : 0f;
                if (_yawOut >= YawDwell) _yawFollow = true;
            }
            if (_yawFollow)
            {
                // Amortiguar el ángulo que falta (camino corto): sin saltos al cruzar ±180°.
                float rest = SmoothDamp(dy, 0f, ref _vyaw, YawSmooth, dt);
                Yaw = Wrap(headYaw - rest);
                if (Math.Abs(Delta(Yaw, headYaw)) < YawSettle) { _yawFollow = false; _yawOut = _vyaw = 0f; }
            }
        }

        /// <summary>Punto (x, z) del círculo: <paramref name="angle"/>° desde el frente del cuerpo (+ = derecha).</summary>
        public void PointAt(float angle, float radius, out float x, out float z)
        {
            double a = (Yaw + angle) * Math.PI / 180.0;
            x = X + (float)(Math.Sin(a) * radius);
            z = Z + (float)(Math.Cos(a) * radius);
        }

        /// <summary>Al revés: ángulo desde el frente del cuerpo y distancia horizontal al eje de un punto (x, z).</summary>
        public void ToPolar(float x, float z, out float angle, out float radius)
        {
            float dx = x - X, dz = z - Z;
            radius = Dist(dx, dz);
            angle = radius < 1e-4f ? 0f : Delta(Yaw, (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI));
        }

        /// <summary>Diferencia b − a en (−180, 180].</summary>
        public static float Delta(float a, float b)
        {
            float d = Wrap(b - a);
            return d > 180f ? d - 360f : d;
        }

        private static float Wrap(float deg)
        {
            float w = deg % 360f;
            return w < 0f ? w + 360f : w;
        }

        private static float Dist(float dx, float dz) => (float)Math.Sqrt(dx * dx + dz * dz);

        /// <summary>Resorte críticamente amortiguado (la misma fórmula que Mathf.SmoothDamp).</summary>
        public static float SmoothDamp(float current, float target, ref float velocity, float smoothTime, float dt)
        {
            smoothTime = Math.Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;
            float x = omega * dt;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float temp = (velocity + omega * change) * dt;
            velocity = (velocity - omega * temp) * exp;
            float output = target + (change + temp) * exp;
            // Sin pasarse del objetivo.
            if ((target - current > 0f) == (output > target)) { output = target; velocity = 0f; }
            return output;
        }
    }
}
