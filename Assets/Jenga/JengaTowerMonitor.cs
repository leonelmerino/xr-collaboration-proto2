using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Monitorea todos los bloques Jenga para detectar colapso de la torre.
///
/// Definición de colapso: un bloque sale de la huella de la torre SIN que
/// nadie lo esté agarrando (grab). Los bloques ya removidos legítimamente
/// (registrados en JengaTurnManager._removedBlocks) son ignorados.
///
/// La huella se calcula al inicio como el bounding box XZ de todos los bloques
/// en su posición de reposo, más un margen configurable.
///
/// SETUP:
/// - Agregar a cualquier GameObject de la escena.
/// - Si los bloques spawnnean tarde (NGO), llamar InitializeFootprint() desde
///   el componente que los spawnnea, después de que todos estén en escena.
/// </summary>
public class JengaTowerMonitor : MonoBehaviour
{
    public static JengaTowerMonitor Instance { get; private set; }

    [Header("Huella de la torre")]
    [Tooltip("Margen extra en XZ (metros) alrededor del bounding box inicial de los bloques.")]
    [SerializeField] private float footprintMarginXZ = 0.06f;

    [Tooltip("Un bloque se considera 'caído' si su Y está más de este valor por debajo del bloque más bajo inicial.")]
    [SerializeField] private float fallHeightMargin = 0.15f;

    [Header("Chequeo de colapso")]
    [Tooltip("Intervalo en segundos entre chequeos. 0.1 = 10 veces por segundo.")]
    [SerializeField] private float checkInterval = 0.1f;

    // ─── Estado interno ───────────────────────────────────────────────────────

    // Huella en XZ (Vector2 usa .x = mundo-X, .y = mundo-Z).
    private Vector2 _footprintCenter;
    private Vector2 _footprintHalfExt;
    private float _minBlockHeightAbs;   // altura mínima absoluta antes de considerar "caído"
    private bool _footprintReady;

    private List<JengaBlockTag> _allBlocks = new();
    private string _lastTouchedBlock = "";
    private float _nextCheckTime;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        InitializeFootprint();
    }

    // ─── API pública ──────────────────────────────────────────────────────────

    /// <summary>
    /// Recalcula la huella a partir de las posiciones actuales de todos los JengaBlockTag
    /// en escena. Llamar una vez al inicio, o cuando todos los bloques terminan de spawnear.
    /// </summary>
    public void InitializeFootprint()
    {
        _allBlocks.Clear();
        _allBlocks.AddRange(FindObjectsOfType<JengaBlockTag>());

        if (_allBlocks.Count == 0)
        {
            Debug.LogWarning("[JengaTowerMonitor] No se encontraron bloques JengaBlockTag. " +
                             "Llamar InitializeFootprint() después de que spawnneen.");
            return;
        }

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        float minY = float.MaxValue;

        foreach (var block in _allBlocks)
        {
            var p = block.transform.position;
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
            if (p.y < minY) minY = p.y;
        }

        _footprintCenter  = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
        _footprintHalfExt = new Vector2(
            (maxX - minX) * 0.5f + footprintMarginXZ,
            (maxZ - minZ) * 0.5f + footprintMarginXZ
        );
        _minBlockHeightAbs = minY - fallHeightMargin;
        _footprintReady = true;

        Debug.Log($"[JengaTowerMonitor] Huella calculada: " +
                  $"centro=({_footprintCenter.x:F3},{_footprintCenter.y:F3}) " +
                  $"halfExt=({_footprintHalfExt.x:F3},{_footprintHalfExt.y:F3}) " +
                  $"minY={_minBlockHeightAbs:F3} bloques={_allBlocks.Count}");
    }

    /// <summary>
    /// True si worldPos está dentro de la huella XZ de la torre y por encima del suelo.
    /// </summary>
    public bool IsInsideFootprint(Vector3 worldPos)
    {
        if (!_footprintReady) return true;
        if (worldPos.y < _minBlockHeightAbs) return false;
        // _footprintCenter.x = centro en mundo-X; _footprintCenter.y = centro en mundo-Z
        return Mathf.Abs(worldPos.x - _footprintCenter.x) <= _footprintHalfExt.x &&
               Mathf.Abs(worldPos.z - _footprintCenter.y) <= _footprintHalfExt.y;
    }

    /// <summary>True si el bloque está fuera de la huella (o por debajo del suelo).</summary>
    public bool IsOutsideFootprint(Vector3 worldPos)
    {
        if (!_footprintReady) return false;
        return !IsInsideFootprint(worldPos);
    }

    /// <summary>
    /// JengaTurnManager llama esto cada vez que el jugador activo toca un bloque,
    /// para que el colapso pueda reportar el último bloque relevante del turno.
    /// </summary>
    public void SetLastTouchedBlock(string blockTag)
    {
        _lastTouchedBlock = blockTag;
    }

    // ─── Detección de colapso ─────────────────────────────────────────────────

    private void Update()
    {
        if (!_footprintReady) return;
        if (Time.time < _nextCheckTime) return;
        _nextCheckTime = Time.time + checkInterval;

        var tm = JengaTurnManager.Instance;
        if (tm == null || !tm.IsSpawned) return;
        if (!tm.SessionActive.Value) return;
        if (!tm.IsMyTurn) return; // solo el dueño del turno emite eventos de colapso

        foreach (var block in _allBlocks)
        {
            if (block == null) continue;

            // Bloques ya removidos legítimamente → ignorar siempre.
            if (tm.IsBlockRemoved(block.gameObject.name)) continue;

            // ¿Está dentro de la huella? Si sí → sin problema.
            if (IsInsideFootprint(block.transform.position)) continue;

            // El bloque está fuera. ¿Alguien lo tiene agarrado?
            bool grabbed = false;

            var netBlock = block.GetComponent<NetworkedJengaBlock>();
            if (netBlock != null && netBlock.IsGrabbedAnywhere)
                grabbed = true;

            if (!grabbed)
            {
                var grabbable = block.GetComponent<JengaGrabbable>();
                if (grabbable != null && grabbable.IsGrabbed())
                    grabbed = true;
            }

            if (!grabbed)
            {
                // Bloque sin grab fuera de huella → COLAPSO.
                Debug.Log($"[JengaTowerMonitor] Colapso detectado: bloque={block.gameObject.name}");
                tm.NotifyTowerCollapse(block.gameObject.name);
                return; // un colapso por ciclo es suficiente
            }
        }
    }
}
