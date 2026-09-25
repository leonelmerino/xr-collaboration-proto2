using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Modo medición: passthrough con Jenga físico para el equipo de registro.
///
/// Se activa solo al cargar la escena (no requiere setup en el Editor) y:
///   1. Desactiva — no borra — el entorno virtual, el Jenga virtual, la locomoción
///      y las interacciones de poke/pinch/rayo.
///   2. Deja la cámara transparente (alpha 0, sin HDR) y sin renderizar capas virtuales.
///   3. Enciende PassthroughUnderlayFeature, que crea el passthrough HTC planar y lo envía como
///      capa inferior en cada frame (ahí se explica por qué no se usan las features de passthrough
///      de VIVE: una congela la app junto al Eye Tracker en Mono y la otra nunca envía la capa).
/// Los loggers de mirada y cuerpo, los eventos y la sincronía de reloj siguen corriendo.
///
/// Encendido por defecto en este branch. Para volver a VR:
///   - Build: lanzar con el argumento -vr
///   - Editor: menú XR Collab → Modo medición (passthrough)
///
/// Requiere VIVE Streaming por USB o Wi-Fi: el modo DisplayPort no soporta passthrough.
/// Build con las features OpenXR correctas: menú XR Collab → Build medición (Win64) (MeasurementBuild).
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
        PassthroughUnderlayFeature.Active = true;
        Debug.Log("[MeasurementMode] Passthrough underlay solicitado (PassthroughUnderlayFeature).");
    }

    private void ConfigureCamera()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[MeasurementMode] No hay Camera.main; el fondo no quedará transparente.");
            return;
        }

        // Alpha 0 en el fondo deja ver el underlay de passthrough. Sin HDR para que el alpha
        // llegue intacto a la textura del ojo.
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = visibleLayers;
        cam.allowHDR = false;
    }

    private void OnDestroy()
    {
        PassthroughUnderlayFeature.Active = false;
    }

    // Solo se ve en la ventana del PC, no en el visor.
    private void OnGUI()
    {
        const float w = 520f, h = 44f;
        GUI.Box(new Rect(10f, Screen.height - h - 10f, w, h),
            $"MODO MEDICIÓN · passthrough: {PassthroughUnderlayFeature.Status}\nLogs: {Application.persistentDataPath}/EyeTrackingLogs");
    }
}
