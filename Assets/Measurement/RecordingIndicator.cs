using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Punto chico arriba a la derecha de la vista del visor, para saber de un vistazo si el PC recibe la imagen:
///   - azul semitransparente: STIMULUS1 está recibiendo la imagen en vivo de este visor (Wi-Fi del router o USB);
///   - rojo: además se está grabando el video de la sesión;
///   - amarillo semitransparente: STIMULUS1 NO está recibiendo la imagen de este visor (sin Wi-Fi/USB o ventana cerrada).
/// Solo en el build del visor (Android). La app no sabe nada de la red ni del video: la ventana «Visores en vivo» de
/// STIMULUS1 (tools\headset-wall) escribe por adb, en Application.persistentDataPath, live_state.txt cada ~3 s
/// mientras recibe imagen y rec_state.txt mientras graba. Acá solo se miran esos archivos.
/// La telemetría no depende de esto.
/// </summary>
public class RecordingIndicator : MonoBehaviour
{
    public const string RecFile = "rec_state.txt";
    public const string LiveFile = "live_state.txt";
    private const float LiveTimeoutSeconds = 10f;   // sin latido en 10 s = el PC ya no recibe la imagen
    private const int Layer = 31;                   // capa sin nombre, solo para el punto (la cámara de medición no dibuja nada más)
    private const float PollSeconds = 0.5f;

    private static readonly Color Live = new Color(0.25f, 0.55f, 1f, 0.4f);
    private static readonly Color NoLink = new Color(1f, 0.85f, 0.1f, 0.4f);
    private static readonly Color Rec = new Color(1f, 0.12f, 0.12f, 1f);

    private string _recPath;
    private string _livePath;
    private GameObject _dot;
    private Image _image;
    private float _next;
    private int _lastState = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Init()
    {
        if (Application.platform != RuntimePlatform.Android || !MeasurementMode.IsActive) return;
        var go = new GameObject("[RecordingIndicator]");
        DontDestroyOnLoad(go);
        go.AddComponent<RecordingIndicator>();
    }

    private void Start()
    {
        _recPath = Path.Combine(Application.persistentDataPath, RecFile);
        _livePath = Path.Combine(Application.persistentDataPath, LiveFile);
    }

    private void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + PollSeconds;

        bool rec = File.Exists(_recPath);
        bool live = false;
        try { live = File.Exists(_livePath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(_livePath)).TotalSeconds < LiveTimeoutSeconds; }
        catch (Exception) { }
        int state = rec ? 2 : live ? 1 : 0;

        if (_dot == null) _dot = CreateDot(out _image);
        if (_dot == null) return;
        _dot.SetActive(true);
        _image.color = state == 2 ? Rec : state == 1 ? Live : NoLink;
        Camera cam = Camera.main;
        if (cam != null) cam.cullingMask |= 1 << Layer;   // MeasurementMode deja la máscara en 0
        if (state != _lastState)
        {
            _lastState = state;
            Debug.Log($"[RecordingIndicator] {(state == 2 ? "rojo (grabando)" : state == 1 ? "azul (imagen en vivo en el PC)" : "amarillo (el PC no recibe la imagen)")}");
        }
    }

    private static GameObject CreateDot(out Image image)
    {
        image = null;
        Camera cam = Camera.main;
        if (cam == null) return null;
        // Canvas en el espacio del mundo, pegado a la cámara: arriba a la derecha, a 0,6 m (~1° de tamaño).
        var canvasGo = new GameObject("RecDot", typeof(Canvas));
        canvasGo.layer = Layer;
        canvasGo.transform.SetParent(cam.transform, false);
        canvasGo.transform.localPosition = new Vector3(0.14f, 0.11f, 0.6f);
        canvasGo.transform.localRotation = Quaternion.identity;
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = (RectTransform)canvasGo.transform;
        rt.sizeDelta = new Vector2(100f, 100f);
        rt.localScale = Vector3.one * 0.0001f;   // 100 unidades = 1 cm

        var dotGo = new GameObject("Dot", typeof(Image));
        dotGo.layer = Layer;
        dotGo.transform.SetParent(canvasGo.transform, false);
        image = dotGo.GetComponent<Image>();
        image.raycastTarget = false;
        var drt = (RectTransform)dotGo.transform;
        drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0.5f);
        drt.sizeDelta = new Vector2(100f, 100f);
        return canvasGo;
    }
}
