using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Unity.XR.CoreUtils;

/// <summary>
/// Muestra esferas en la posición de cada tracker VIVE Ultimate Tracker.
///
/// Color de esfera:
///   Verde   = tracking activo y posición válida
///   Amarillo = tracker encontrado pero isTracked = false
///   Rojo     = tracker no encontrado / no disponible
///   Cian    = tracking activo, calibrado (se muestra la posición CALIBRADA)
///   Magenta = rol asignado por calibración (waist / pie)
///
/// Requiere que el XR Origin esté en la escena para la conversión de coordenadas.
/// Si TrackerBodyCalibration está calibrado, muestra también las posiciones
/// corregidas (calibradas) de cada parte del cuerpo.
/// </summary>
public class TrackerVisualizer : MonoBehaviour
{
    [Header("Configuración")]
    [Tooltip("Número máximo de trackers a mostrar.")]
    [SerializeField] private int maxTrackers = 3;

    [Tooltip("Tamaño de las esferas de tracker raw.")]
    [SerializeField] private float rawSphereSize = 0.08f;

    [Tooltip("Tamaño de las esferas de posición calibrada.")]
    [SerializeField] private float calSphereSize = 0.12f;

    [Tooltip("Mostrar también las esferas de posición calibrada (waist/feet).")]
    [SerializeField] private bool showCalibratedPositions = true;

    // ── Esferas raw (una por tracker, ordenadas por nombre) ──────────────
    private GameObject[] _rawSpheres;
    private GameObject[] _rawLabels;     // TextMesh para cada esfera raw

    // ── Esferas calibradas (waist, footL, footR) ──────────────────────────
    private GameObject _waistSphere;
    private GameObject _footLSphere;
    private GameObject _footRSphere;

    // ── Colores ────────────────────────────────────────────────────────────
    private static readonly Color ColorNoDevice  = Color.red;
    private static readonly Color ColorLostTrack = Color.yellow;
    private static readonly Color ColorTracked   = Color.green;

    private static readonly Color ColorWaist  = new Color(1f, 0.4f, 0f);    // naranja
    private static readonly Color ColorFootL  = new Color(0f, 0.8f, 1f);    // cian
    private static readonly Color ColorFootR  = new Color(0.8f, 0f, 1f);    // magenta

    // ── Refs ───────────────────────────────────────────────────────────────
    private Transform _trackingParent;   // XR rig camera offset (tracking→world)
    private TrackerBodyCalibration _cal;

    // ── Estado previo para log de cambios ─────────────────────────────────
    private bool[] _prevTracked;
    private float _diagTimer;

    // ──────────────────────────────────────────────────────────────────────
    void Start()
    {
        // Buscar XR Origin para conversión de coordenadas
        var origin = FindObjectOfType<XROrigin>();
        if (origin != null && origin.Camera != null)
            _trackingParent = origin.Camera.transform.parent;

        // Buscar calibración
        _cal = FindObjectOfType<TrackerBodyCalibration>();

        // Crear esferas raw
        _rawSpheres = new GameObject[maxTrackers];
        _rawLabels  = new GameObject[maxTrackers];
        _prevTracked = new bool[maxTrackers];

        Color[] rawColors = { Color.red, Color.green, Color.blue };
        string[] labels   = { "T0", "T1", "T2" };

        for (int i = 0; i < maxTrackers; i++)
        {
            _rawSpheres[i] = CreateSphere($"TrackerVis_Raw_{i}", rawColors[i % rawColors.Length],
                                          rawSphereSize);

            // Label TextMesh
            _rawLabels[i] = new GameObject($"TrackerVis_Label_{i}");
            var tm = _rawLabels[i].AddComponent<TextMesh>();
            tm.text      = labels[i];
            tm.fontSize  = 48;
            tm.characterSize = 0.02f;
            tm.color     = Color.white;
            tm.anchor    = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
        }

        // Crear esferas calibradas
        _waistSphere = CreateSphere("TrackerVis_Waist",  ColorWaist, calSphereSize);
        _footLSphere = CreateSphere("TrackerVis_FootL",  ColorFootL, calSphereSize);
        _footRSphere = CreateSphere("TrackerVis_FootR",  ColorFootR, calSphereSize);

        // Ocultar esferas calibradas hasta que haya calibración
        SetActive(_waistSphere, false);
        SetActive(_footLSphere, false);
        SetActive(_footRSphere, false);
    }

    void Update()
    {
        // Actualizar ref a calibración si no estaba disponible
        if (_cal == null) _cal = FindObjectOfType<TrackerBodyCalibration>();
        if (_trackingParent == null)
        {
            var origin = FindObjectOfType<XROrigin>();
            if (origin != null && origin.Camera != null)
                _trackingParent = origin.Camera.transform.parent;
        }

        // ── Diagnóstico: log de todos los dispositivos cada 5 s ────────────
        _diagTimer -= Time.deltaTime;
        if (_diagTimer <= 0f)
        {
            _diagTimer = 5f;
            var allDev = new List<InputDevice>();
            InputDevices.GetDevices(allDev);
            if (allDev.Count == 0)
            {
                Debug.Log("[TrackerVisualizer] InputDevices: 0 dispositivos.");
            }
            else
            {
                var sb = new System.Text.StringBuilder($"[TrackerVisualizer] {allDev.Count} dispositivos XR:\n");
                foreach (var d in allDev)
                {
                    d.TryGetFeatureValue(CommonUsages.isTracked, out bool t);
                    sb.AppendLine($"  '{d.name}' tracked={t} chars={d.characteristics}");
                }
                Debug.Log(sb.ToString());
            }
        }

        // ── Esferas raw ────────────────────────────────────────────────────
        var devices = new List<InputDevice>();
        InputDevices.GetDevices(devices);

        var ultimateTrackers = new List<InputDevice>();
        foreach (var d in devices)
        {
            if (d.name.Contains("VIVE Ultimate Tracker"))
                ultimateTrackers.Add(d);
        }

        for (int i = 0; i < maxTrackers; i++)
        {
            if (i >= ultimateTrackers.Count)
            {
                SetActive(_rawSpheres[i], false);
                SetActive(_rawLabels[i], false);
                continue;
            }

            var dev = ultimateTrackers[i];
            bool tracked = false;
            dev.TryGetFeatureValue(CommonUsages.isTracked, out tracked);

            bool gotPos = dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rawPos);

            // Log si cambió el estado de tracking
            if (tracked != _prevTracked[i])
            {
                string state = tracked ? "SEGUIMIENTO ACTIVO" : "SEGUIMIENTO PERDIDO";
                Debug.Log($"[TrackerVisualizer] {dev.name}: {state}");
                _prevTracked[i] = tracked;
            }

            if (gotPos && tracked)
            {
                // Convertir a mundo
                Vector3 worldPos = TrackingToWorld(rawPos);
                _rawSpheres[i].transform.position = worldPos;
                SetActive(_rawSpheres[i], true);
                SetSphereColor(_rawSpheres[i], ColorTracked);

                // Label
                SetActive(_rawLabels[i], true);
                _rawLabels[i].transform.position = worldPos + Vector3.up * (rawSphereSize * 0.8f);
                _rawLabels[i].transform.rotation = Quaternion.LookRotation(
                    Camera.main != null ? Camera.main.transform.forward : Vector3.forward);
                UpdateLabel(i, dev.name);
            }
            else if (gotPos)
            {
                // Tiene posición pero no tracking activo
                Vector3 worldPos = TrackingToWorld(rawPos);
                _rawSpheres[i].transform.position = worldPos;
                SetActive(_rawSpheres[i], true);
                SetSphereColor(_rawSpheres[i], ColorLostTrack);
                SetActive(_rawLabels[i], false);
            }
            else
            {
                SetActive(_rawSpheres[i], false);
                SetActive(_rawLabels[i], false);
            }
        }

        // ── Esferas calibradas ─────────────────────────────────────────────
        bool showCal = showCalibratedPositions && _cal != null && _cal.IsCalibrated;

        SetActive(_waistSphere, showCal);
        SetActive(_footLSphere, showCal);
        SetActive(_footRSphere, showCal);

        if (showCal)
        {
            TryPlaceCalibratedSphere(_cal.TrackerWaist, _cal.WaistOffset, _waistSphere);
            TryPlaceCalibratedSphere(_cal.TrackerFootL, _cal.FootLOffset, _footLSphere);
            TryPlaceCalibratedSphere(_cal.TrackerFootR, _cal.FootROffset, _footRSphere);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    Vector3 TrackingToWorld(Vector3 trackingPos)
    {
        // Si la calibración ya está disponible, usa su método (es el mismo transform)
        if (_cal != null && _cal.TrackingParent != null)
            return _cal.TrackingToWorld(trackingPos);

        // Fallback: usar el trackingParent que buscamos en Start
        if (_trackingParent != null)
            return _trackingParent.TransformPoint(trackingPos);

        // Sin XR Origin disponible: usar posición raw directamente
        return trackingPos;
    }

    void TryPlaceCalibratedSphere(InputDevice dev, Vector3 offset, GameObject sphere)
    {
        if (!dev.isValid) return;
        if (!dev.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked) return;
        if (!dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 raw)) return;

        // Usar el mismo cálculo que TrackerPoseDriver
        Vector3 corrected = (_cal != null ? _cal.TrackingToWorld(raw) : raw) - offset;
        sphere.transform.position = corrected;
    }

    void UpdateLabel(int idx, string deviceName)
    {
        if (_cal == null || !_cal.IsCalibrated)
        {
            _rawLabels[idx].GetComponent<TextMesh>().text = $"T{idx}";
            return;
        }

        // Identificar rol
        string rol = "?";
        if (_cal.TrackerWaist.isValid && _cal.TrackerWaist.name == deviceName) rol = "Waist";
        else if (_cal.TrackerFootL.isValid && _cal.TrackerFootL.name == deviceName) rol = "FootL";
        else if (_cal.TrackerFootR.isValid && _cal.TrackerFootR.name == deviceName) rol = "FootR";

        _rawLabels[idx].GetComponent<TextMesh>().text = $"T{idx}\n{rol}";
    }

    void SetSphereColor(GameObject sphere, Color c)
    {
        if (sphere == null) return;
        sphere.GetComponent<Renderer>().material.color = c;
    }

    void SetActive(GameObject go, bool active)
    {
        if (go != null && go.activeSelf != active)
            go.SetActive(active);
    }

    static GameObject CreateSphere(string name, Color color, float size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.localScale = Vector3.one * size;

        // Material simple (sin sombras para visibilidad)
        var mat = new Material(Shader.Find("Standard"));
        mat.color = color;
        mat.SetFloat("_Mode", 0); // Opaque
        go.GetComponent<Renderer>().material = mat;

        // Sin colisión
        Object.Destroy(go.GetComponent<Collider>());

        return go;
    }
}
