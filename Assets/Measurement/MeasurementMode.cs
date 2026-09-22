using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using VIVE.OpenXR;
using VIVE.OpenXR.Passthrough;

/// <summary>
/// Modo medición: passthrough con Jenga físico para el equipo de registro.
///
/// Se activa solo al cargar la escena (no requiere setup en el Editor) y:
///   1. Desactiva — no borra — el entorno virtual, el Jenga virtual, la locomoción
///      y las interacciones de poke/pinch/rayo.
///   2. Deja la cámara transparente y sin renderizar capas virtuales, para que
///      se vea la sala real detrás.
///   3. Crea un passthrough planar como underlay (XR_HTC_passthrough).
/// Los loggers de mirada y cuerpo, los eventos y la sincronía de reloj siguen corriendo.
///
/// Encendido por defecto en este branch. Para volver a VR:
///   - Build: lanzar con el argumento -vr
///   - Editor: menú XR Collab → Modo medición (passthrough)
///
/// Requiere VIVE Streaming por USB o Wi-Fi: el modo DisplayPort no soporta passthrough.
/// </summary>
public class MeasurementMode : MonoBehaviour
{
    public const string EditorPrefKey = "XRCollab.MeasurementMode";

    // Raíces de la escena Room que se desactivan en modo medición.
    private static readonly string[] VirtualRoots =
    {
        "RoomNew", "RoomFurniture", "RoomLighting", "RoomLightProbes",
        "JengaTower", "JengaBlock",
        "Interaction", "HandRayDriver", "Locomotion System",
    };

    public static bool IsActive { get; private set; }

    [Tooltip("Capas que la cámara sigue renderizando sobre el passthrough. Vacío = nada virtual visible.")]
    [SerializeField] private LayerMask visibleLayers = 0;

    [SerializeField] private int maxPassthroughAttempts = 30;

    private XrPassthroughHTC _passthrough;
    private bool _hasPassthrough;
    private string _status = "esperando sesión XR";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        IsActive = ResolveEnabled();
        Debug.Log($"[MeasurementMode] {(IsActive ? "ACTIVO (passthrough, Jenga físico)" : "desactivado: modo VR")}");
        if (IsActive) SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static bool ResolveEnabled()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "-vr") >= 0) return false;
        if (Array.IndexOf(args, "-measurement") >= 0) return true;
#if UNITY_EDITOR
        return UnityEditor.EditorPrefs.GetBool(EditorPrefKey, true);
#else
        return true;
#endif
    }

    // sceneLoaded corre después de Awake/OnEnable y antes de Start: el Jenga
    // todavía no se construyó y nada alcanzó a spawnear.
    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (Array.IndexOf(VirtualRoots, root.name) >= 0 && root.activeSelf)
            {
                root.SetActive(false);
                Debug.Log($"[MeasurementMode] Desactivado: {root.name}");
            }
        }

        foreach (PinchDetector pinch in FindObjectsOfType<PinchDetector>(true))
            pinch.enabled = false;

        if (FindObjectOfType<MeasurementMode>() == null)
            new GameObject("[MeasurementMode]").AddComponent<MeasurementMode>();
    }

    private void Start()
    {
        // Bug 1: la tecla C conecta como Client y además calibra con offsets inválidos.
        // Sin calibración, las columnas de trackers quedan is_calibrated=0 en vez de datos falsos.
        // [TrackerSystem] se crea en AfterSceneLoad, que corre antes de este Start.
        foreach (TrackerBodyCalibration cal in FindObjectsOfType<TrackerBodyCalibration>(true))
            cal.enabled = false;

        ConfigureCamera();
        StartCoroutine(StartPassthroughWhenReady());
    }

    private void ConfigureCamera()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[MeasurementMode] No hay Camera.main; el fondo no quedará transparente.");
            return;
        }

        // Alpha 0 en el fondo deja ver el underlay de passthrough.
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = visibleLayers;
    }

    private IEnumerator StartPassthroughWhenReady()
    {
        for (int attempt = 1; attempt <= maxPassthroughAttempts; attempt++)
        {
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            if (manager != null && manager.activeLoader != null)
            {
                XrResult res = PassthroughAPI.CreatePlanarPassthrough(
                    out _passthrough,
                    VIVE.OpenXR.CompositionLayer.LayerType.Underlay,
                    OnPassthroughSessionDestroyed);

                if (res == XrResult.XR_SUCCESS)
                {
                    _hasPassthrough = true;
                    _status = "ON";
                    Debug.Log("[MeasurementMode] Passthrough creado (underlay).");
                    yield break;
                }
                _status = $"falló ({res}), intento {attempt}/{maxPassthroughAttempts}";
            }
            else
            {
                _status = $"XR no inicializado, intento {attempt}/{maxPassthroughAttempts}";
            }
            yield return new WaitForSeconds(1f);
        }

        _status = "NO DISPONIBLE — revisar VIVE Streaming por USB y 'MR with passthrough'";
        Debug.LogError("[MeasurementMode] No se pudo crear el passthrough: " + _status);
    }

    private void OnPassthroughSessionDestroyed(XrPassthroughHTC passthrough)
    {
        PassthroughAPI.DestroyPassthrough(passthrough);
        _hasPassthrough = false;
        _status = "sesión XR cerrada";
    }

    private void OnDestroy()
    {
        if (_hasPassthrough) PassthroughAPI.DestroyPassthrough(_passthrough);
        _hasPassthrough = false;
    }

    // Solo se ve en la ventana del PC, no en el visor.
    private void OnGUI()
    {
        const float w = 520f, h = 44f;
        GUI.Box(new Rect(10f, Screen.height - h - 10f, w, h),
            $"MODO MEDICIÓN · passthrough: {_status}\nLogs: {Application.persistentDataPath}/EyeTrackingLogs");
    }
}
