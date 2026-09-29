using System.IO;
using UnityEngine;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>
    /// Espejo del visor en la ventana del PC: lo que ve el participante (la sala de las cámaras a color más la
    /// capa de la app), para quien observa desde el laptop. En modo medición la ventana del PC queda negra
    /// porque el visor compone la sala por su cuenta; esa imagen nunca viaja por VIVE Streaming.
    ///
    /// Punto de entrada del módulo: arma la fuente de cuadros (<see cref="IHeadsetFrameSource"/>) y la vista
    /// (<see cref="HeadsetMirrorView"/>), pasa los cuadros de una a otra en el hilo principal, y maneja teclas,
    /// estado y ciclo de vida. Diseño, requisitos y problemas conocidos: README.md de esta carpeta.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HeadsetMirror : MonoBehaviour
    {
        private const string LogPrefix = "[HeadsetMirror] ";
        private const float StatsLogIntervalSeconds = 60f;

        [SerializeField] private HeadsetMirrorSettings settings = new HeadsetMirrorSettings();

        private IHeadsetFrameSource _source;
        private HeadsetMirrorView _view;
        private int _framesShownInWindow;
        private float _windowStart;
        private float _displayFps;
        private float _nextStatsLog;
        private long _framesReceivedAtLastLog;

        public MirrorStatus Status => _source?.Status ?? MirrorStatus.Stopped;
        public float DisplayFps => _displayFps;

        /// <summary>Crea el espejo como hijo de <paramref name="parent"/> y lo arranca con esta configuración.</summary>
        public static HeadsetMirror Create(Transform parent, HeadsetMirrorSettings settings)
        {
            var go = new GameObject("[HeadsetMirror]");
            go.transform.SetParent(parent, false);
            go.SetActive(false);   // configurar antes de que corra OnEnable
            var mirror = go.AddComponent<HeadsetMirror>();
            mirror.settings = settings.Clone();
            go.SetActive(true);
            return mirror;
        }

        private void OnEnable()
        {
            settings.Validate();
            if (!TryGetComponent(out _view)) _view = gameObject.AddComponent<HeadsetMirrorView>();
            _view.Layout = settings.layout;
            _view.Visible = settings.visibleOnStart;

            // streamingAssetsPath solo se puede leer en el hilo principal: se resuelve aquí y se pasa a la fuente.
            string serverAsset = Path.Combine(Application.streamingAssetsPath, ScrcpyProtocol.ServerAssetPath);
            _source = new ScrcpyFrameSource(settings, serverAsset, message => Debug.Log(LogPrefix + message));
            _source.Start();

            _windowStart = Time.unscaledTime;
            _nextStatsLog = Time.unscaledTime + StatsLogIntervalSeconds;
            RefreshStatusText();
        }

        private void OnDisable()
        {
            _source?.Dispose();
            _source = null;
            if (_view != null) _view.ClearFrame();   // != de Unity: la vista puede estar destruyéndose
        }

        private void Update()
        {
            if (_source == null) return;
            HandleInput();
            PumpFrame();
            UpdateStatistics();
        }

        private void HandleInput()
        {
            bool changed = false;
            if (Input.GetKeyDown(settings.toggleKey))
            {
                _view.Visible = !_view.Visible;
                changed = true;
            }
            if (Input.GetKeyDown(settings.layoutKey))
            {
                _view.Layout = _view.Layout == MirrorLayout.BothEyes ? MirrorLayout.LeftEye : MirrorLayout.BothEyes;
                changed = true;
            }
            if (changed) RefreshStatusText();
        }

        private void PumpFrame()
        {
            FrameTripleBuffer frames = _source.Frames;
            if (frames == null)
            {
                _view.ClearFrame();
                return;
            }
            if (frames.TryAcquire(out byte[] rgba))
            {
                _view.Present(rgba, frames.Width, frames.Height);
                _framesShownInWindow++;
            }
        }

        private void UpdateStatistics()
        {
            float now = Time.unscaledTime;
            float window = now - _windowStart;
            if (window >= 1f)
            {
                _displayFps = _framesShownInWindow / window;
                _framesShownInWindow = 0;
                _windowStart = now;
                RefreshStatusText();
            }
            if (now >= _nextStatsLog)
            {
                long received = _source.FramesReceived;
                Debug.Log($"{LogPrefix}{Status} · {received - _framesReceivedAtLastLog} cuadros en {StatsLogIntervalSeconds:0} s · mostrando {_displayFps:0.0} fps");
                _framesReceivedAtLastLog = received;
                _nextStatsLog = now + StatsLogIntervalSeconds;
            }
        }

        private void RefreshStatusText()
        {
            MirrorStatus status = Status;
            string head = status.State == MirrorState.Streaming
                ? $"Espejo del visor · {status.DeviceSerial} · {_displayFps:0} fps"
                : $"Espejo del visor: {status.Detail}";
            string keys = $"{settings.toggleKey} {(_view.Visible ? "ocultar" : "mostrar")} · " +
                          $"{settings.layoutKey} {(_view.Layout == MirrorLayout.BothEyes ? "un ojo" : "ambos ojos")}";
            _view.SetStatus($"{head}    [{keys}]");
        }
    }
}
