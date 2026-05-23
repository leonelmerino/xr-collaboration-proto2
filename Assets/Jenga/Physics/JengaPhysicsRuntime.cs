using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Singleton de escena que mantiene el "live config" de fisica de Jenga y lo aplica
/// a todos los bloques + project settings + grabbables.
///
/// Flujo de uso:
/// 1. En la escena hay un GameObject con este componente.
/// 2. Inspector: se asigna `blockMaterial` (el JengaBlockPhysics.physicMaterial asset) y
///    `presets` (lista de JengaPhysicsConfig assets).
/// 3. Awake: crea un Instantiate del primer preset como `liveConfig` (deep copy in-memory).
/// 4. Start: aplica liveConfig a todo (project physics + material + blocks + grabbables).
/// 5. HUD modifica `liveConfig` directamente y llama `ApplyActive()` para re-aplicar.
/// 6. LoadPreset(idx) copia los valores del preset[idx] a liveConfig y re-aplica.
///
/// Notas:
/// - Mutamos el PhysicMaterial asset in-memory. En Editor, Unity revierte estos cambios al
///   salir de Play. En Build no hay asset, solo runtime. Seguro en ambos casos.
/// - Si NGO esta corriendo, ESTO ES LOCAL: los cambios afectan solo a esta instancia. Para
///   sincronizar via red habria que enviar la config completa por ServerRpc. Por ahora,
///   single-player tuning.
/// </summary>
[DefaultExecutionOrder(-50)] // Antes que la torre y los grabbables, para que apliquen project settings antes de empezar a simular.
public class JengaPhysicsRuntime : MonoBehaviour
{
    public static JengaPhysicsRuntime Instance { get; private set; }

    [Header("Assets")]
    [Tooltip("El PhysicMaterial usado por los BoxCollider de los bloques. Tipicamente JengaBlockPhysics.physicMaterial.")]
    [SerializeField] private PhysicMaterial blockMaterial;

    [Tooltip("Presets disponibles. El primero se carga como baseline en Awake.")]
    [SerializeField] private JengaPhysicsConfig[] presets;

    [Tooltip("Si se asigna manualmente, este config se usa como inicial en vez de presets[0]. " +
             "Util para arrancar Play con un tuning custom sin tocar la lista de presets.")]
    [SerializeField] private JengaPhysicsConfig overrideInitialConfig;

    // El "live" es una copia in-memory que se muta libremente sin afectar los assets.
    private JengaPhysicsConfig liveConfig;
    public JengaPhysicsConfig LiveConfig => liveConfig;

    private int activePresetIndex = 0;
    public int ActivePresetIndex => activePresetIndex;

    public int PresetCount => presets != null ? presets.Length : 0;

    public string ActivePresetName => liveConfig != null ? liveConfig.presetName : "(none)";

    /// <summary>Se dispara cada vez que se aplica liveConfig. Util para HUDs que muestran el estado.</summary>
    public event System.Action<JengaPhysicsConfig> OnConfigApplied;

    /// <summary>Reportado por ApplyToBlocks; util para feedback en HUD.</summary>
    public int LastAppliedBlockCount { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Crear live config como deep copy del preset inicial.
        JengaPhysicsConfig source = overrideInitialConfig != null
            ? overrideInitialConfig
            : (presets != null && presets.Length > 0 ? presets[0] : null);

        if (source != null)
        {
            liveConfig = Instantiate(source);
            liveConfig.name = $"Live ({source.presetName})";
        }
        else
        {
            // Fallback: crear vacio con defaults del SO.
            liveConfig = ScriptableObject.CreateInstance<JengaPhysicsConfig>();
            liveConfig.presetName = "Default (no preset assigned)";
            Debug.LogWarning("[JengaPhysicsRuntime] No hay presets asignados ni override. Usando defaults del ScriptableObject.");
        }
    }

    private void Start()
    {
        ApplyActive();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (liveConfig != null)
        {
            Destroy(liveConfig);
            liveConfig = null;
        }
    }

    /// <summary>
    /// Copia los valores del preset[idx] al live config y aplica.
    /// idx fuera de rango = no-op.
    /// </summary>
    public void LoadPreset(int idx)
    {
        if (presets == null || idx < 0 || idx >= presets.Length) return;
        if (liveConfig == null) return;

        string newName = $"Live ({presets[idx].presetName})";
        liveConfig.CopyFrom(presets[idx]);
        liveConfig.presetName = presets[idx].presetName;
        liveConfig.name = newName;
        activePresetIndex = idx;

        Debug.Log($"[JengaPhysicsRuntime] Preset cargado: [{idx}] {presets[idx].presetName}");
        ApplyActive();
    }

    /// <summary>Carga el siguiente preset (con wrap).</summary>
    public void CycleNextPreset()
    {
        if (presets == null || presets.Length == 0) return;
        LoadPreset((activePresetIndex + 1) % presets.Length);
    }

    /// <summary>Carga el preset anterior (con wrap).</summary>
    public void CyclePrevPreset()
    {
        if (presets == null || presets.Length == 0) return;
        LoadPreset((activePresetIndex - 1 + presets.Length) % presets.Length);
    }

    /// <summary>
    /// Re-aplica el liveConfig a todos los sistemas. Llamado automaticamente en Start y al cargar preset.
    /// El HUD debe llamarlo despues de modificar liveConfig manualmente.
    /// </summary>
    public void ApplyActive()
    {
        if (liveConfig == null) return;

        ApplyToProjectPhysics(liveConfig);
        ApplyToPhysicMaterial(liveConfig);
        LastAppliedBlockCount = ApplyToBlocks(liveConfig);
        ApplyToGrabbables(liveConfig);

        OnConfigApplied?.Invoke(liveConfig);
    }

    /// <summary>
    /// Pide a JengaTowerGenerator un rebuild de la torre. Necesario para que cambios de
    /// spacing/dropHeight tomen efecto sobre los bloques nuevos.
    /// </summary>
    public void RebuildTower()
    {
        var gen = JengaTowerGenerator.Instance;
        if (gen == null)
        {
            Debug.LogWarning("[JengaPhysicsRuntime] JengaTowerGenerator.Instance no disponible; no se puede reconstruir torre.");
            return;
        }

        // Pasar los spacings actuales al generator antes del reset.
        gen.horizontalSpacing = liveConfig.horizontalSpacing;
        gen.verticalSpacing = liveConfig.verticalSpacing;
        gen.dropHeight = liveConfig.dropHeight;

        gen.ResetTower();
    }

    // --- Apply helpers -------------------------------------------------------

    private static void ApplyToProjectPhysics(JengaPhysicsConfig c)
    {
        Physics.defaultSolverIterations = c.solverIterations;
        Physics.defaultSolverVelocityIterations = c.solverVelocityIterations;
        Physics.defaultContactOffset = c.contactOffset;
        Physics.bounceThreshold = c.bounceThreshold;
        Physics.sleepThreshold = c.sleepThreshold;
    }

    private void ApplyToPhysicMaterial(JengaPhysicsConfig c)
    {
        if (blockMaterial == null) return;
        blockMaterial.staticFriction = c.staticFriction;
        blockMaterial.dynamicFriction = c.dynamicFriction;
        blockMaterial.bounciness = c.bounciness;
        blockMaterial.frictionCombine = c.frictionCombine;
        blockMaterial.bounceCombine = c.bounceCombine;
    }

    private static int ApplyToBlocks(JengaPhysicsConfig c)
    {
        // FindObjectsOfType es relativamente caro pero solo lo llamamos cuando cambia el config.
        var blocks = FindObjectsOfType<JengaBlockTag>();
        int applied = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            var rb = blocks[i].GetComponent<Rigidbody>();
            if (rb == null) continue;
            rb.mass = c.mass;
            rb.drag = c.drag;
            rb.angularDrag = c.angularDrag;
            rb.collisionDetectionMode = c.collisionDetection;
            rb.interpolation = c.interpolation;
            applied++;
        }
        return applied;
    }

    private static void ApplyToGrabbables(JengaPhysicsConfig c)
    {
        var grabbables = FindObjectsOfType<JengaGrabbable>();
        for (int i = 0; i < grabbables.Length; i++)
        {
            grabbables[i].SetTuning(c.grabPositionLerp, c.preserveVelocityOnRelease,
                                    c.releaseVelocitySamples, c.releaseVelocityMax);
        }
    }
}
