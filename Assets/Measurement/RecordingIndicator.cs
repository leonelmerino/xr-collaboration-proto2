using System.IO;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Punto rojo chico arriba a la derecha de la vista del visor mientras se graba el video de la sesión.
/// Solo en el build del visor (Android). La app no sabe nada de la grabación: la ventana «Visores en vivo» de
/// STIMULUS1 (tools\headset-wall) crea por adb el archivo rec_state.txt en Application.persistentDataPath al
/// empezar a grabar y lo borra al terminar; acá solo se mira si existe. La telemetría no depende de esto.
/// </summary>
public class RecordingIndicator : MonoBehaviour
{
    public const string StateFile = "rec_state.txt";
    private const int Layer = 31;            // capa sin nombre, solo para el punto (la cámara de medición no dibuja nada más)
    private const float PollSeconds = 0.5f;

    private string _path;
    private GameObject _dot;
    private float _next;

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
        _path = Path.Combine(Application.persistentDataPath, StateFile);
    }

    private void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + PollSeconds;
        bool on = File.Exists(_path);
        if (on && _dot == null) _dot = CreateDot();
        if (_dot != null)
        {
            _dot.SetActive(on);
            Camera cam = Camera.main;
            if (on && cam != null) cam.cullingMask |= 1 << Layer;   // MeasurementMode deja la máscara en 0
        }
    }

    private static GameObject CreateDot()
    {
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
        var img = dotGo.GetComponent<Image>();
        img.color = new Color(1f, 0.12f, 0.12f, 1f);
        img.raycastTarget = false;
        var drt = (RectTransform)dotGo.transform;
        drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0.5f);
        drt.sizeDelta = new Vector2(100f, 100f);
        Debug.Log("[RecordingIndicator] grabando: punto rojo visible");
        return canvasGo;
    }
}
