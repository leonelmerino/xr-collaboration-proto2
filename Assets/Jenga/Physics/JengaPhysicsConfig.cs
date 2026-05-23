using UnityEngine;

/// <summary>
/// ScriptableObject que agrupa todos los parametros tuneables del comportamiento fisico
/// de los bloques Jenga. Pensado para iterar tuning sin sacarse el visor:
/// - El usuario crea N instancias (presets) en Project (Create -> XR Collab -> Jenga Physics Config).
/// - JengaPhysicsRuntime mantiene un "live config" (copia runtime de uno de los presets) y
///   reaplica los valores a la escena cuando se modifican via HUD o cuando se carga otro preset.
///
/// El asset NO se muta en disco. JengaPhysicsRuntime crea un Instantiate (deep copy) en Awake
/// y mantiene esa copia in-memory. Si el usuario quiere persistir un tuning, edita el preset
/// fuera de Play mode.
///
/// Los defaults aqui REPLICAN el setup actual del prefab + project settings + grabbable, asi
/// que cargar "Baseline" debe sentirse igual a la version pre-tuner.
/// </summary>
[CreateAssetMenu(fileName = "JengaPhysicsConfig", menuName = "XR Collab/Jenga Physics Config")]
public class JengaPhysicsConfig : ScriptableObject
{
    [Header("Identificacion")]
    [Tooltip("Nombre del preset, visible en el HUD.")]
    public string presetName = "Custom";

    [Header("Block PhysicMaterial")]
    [Tooltip("Resistencia a empezar a deslizar. Real wood ≈ 0.4-0.5. Default actual: 0.8 (muy adherente).")]
    [Range(0f, 2f)] public float staticFriction = 0.8f;

    [Tooltip("Resistencia al deslizar. Tipicamente ≤ static. Default actual: 0.8.")]
    [Range(0f, 2f)] public float dynamicFriction = 0.8f;

    [Tooltip("Cuanto rebota al chocar. Madera real ≈ 0. Default actual: 0.")]
    [Range(0f, 1f)] public float bounciness = 0f;

    [Tooltip("Como se combina la friccion con la del otro objeto. Average = mas natural, " +
             "Maximum = mas pegajoso (cualquier superficie adherente domina). Default actual: Maximum.")]
    public PhysicMaterialCombine frictionCombine = PhysicMaterialCombine.Maximum;

    [Tooltip("Como se combina la bounciness. Default actual: Minimum.")]
    public PhysicMaterialCombine bounceCombine = PhysicMaterialCombine.Minimum;

    [Header("Block Rigidbody")]
    [Tooltip("Masa por bloque (kg). Jenga real ≈ 0.007 kg. Default actual: 0.1 (mas pesado, mas estable).")]
    [Range(0.005f, 1f)] public float mass = 0.1f;

    [Tooltip("Amortiguacion lineal. 0 = fisica pura. 0.05-0.2 reduce jitter sin matar inercia.")]
    [Range(0f, 2f)] public float drag = 0f;

    [Tooltip("Amortiguacion rotacional. Critico para Jenga: evita que un bloque rote indefinidamente despues de un toque suave.")]
    [Range(0f, 2f)] public float angularDrag = 0.2f;

    [Tooltip("Modo de deteccion de colisiones. Continuous Dynamic es lo mejor para objetos pequeños en movimiento rapido (bloques agarrados/movidos).")]
    public CollisionDetectionMode collisionDetection = CollisionDetectionMode.ContinuousDynamic;

    [Tooltip("Interpolacion del render entre ticks de fisica. Interpolate suaviza visualmente sin tocar tickrate.")]
    public RigidbodyInterpolation interpolation = RigidbodyInterpolation.Interpolate;

    [Header("Project Physics (globales — afectan toda la escena)")]
    [Tooltip("Iteraciones del solver de restricciones. Mas iteraciones = mas estabilidad en pilas, mas costo CPU. " +
             "Default Unity: 6. Default proyecto actual: 12.")]
    [Range(1, 30)] public int solverIterations = 12;

    [Tooltip("Iteraciones del solver de velocidad. Reduce penetracion entre bloques en pilas altas. " +
             "Default Unity: 1. Default proyecto actual: 4.")]
    [Range(1, 20)] public int solverVelocityIterations = 4;

    [Tooltip("Distancia minima de contacto. Para objetos pequeños (bloques de ~15mm) conviene bajarlo " +
             "del default Unity de 0.01 para evitar 'flotacion' entre bloques. Default proyecto actual: 0.005.")]
    [Range(0.001f, 0.02f)] public float contactOffset = 0.005f;

    [Tooltip("Velocidades por debajo de este umbral no rebotan. Subirlo elimina micro-vibraciones que se sienten como temblor. Default: 2.")]
    [Range(0.5f, 10f)] public float bounceThreshold = 2f;

    [Tooltip("Cuanto cuesta que un Rigidbody entre en sleep. Default: 0.005.")]
    [Range(0.001f, 0.05f)] public float sleepThreshold = 0.005f;

    [Header("Grab feel")]
    [Tooltip("Factor de suavizado al seguir la pose de la mano. 1 = sigue exactamente (puede temblar con jitter); " +
             "0.35 = soft follow actual.")]
    [Range(0.05f, 1f)] public float grabPositionLerp = 0.35f;

    [Tooltip("Si true, al soltar el bloque conserva la velocidad estimada de los ultimos N frames de grab. " +
             "Sin esto el bloque cae en linea recta sin importar como lo movias (release plano).")]
    public bool preserveVelocityOnRelease = true;

    [Tooltip("Cuantas muestras (FixedUpdate ticks) usar para estimar la velocidad al soltar.")]
    [Range(1, 10)] public int releaseVelocitySamples = 3;

    [Tooltip("Velocidad maxima clampeada al estimar release. Evita que micro-jitter del hand tracking se convierta en velocidades irreales.")]
    [Range(0.5f, 10f)] public float releaseVelocityMax = 3f;

    [Header("Tower build (requiere rebuild para aplicarse)")]
    [Tooltip("Espacio horizontal entre bloques del mismo nivel. Default actual: 0.0005 m (0.5 mm).")]
    [Range(0f, 0.01f)] public float horizontalSpacing = 0.0005f;

    [Tooltip("Espacio vertical entre niveles. Default actual: 0.0002 m (0.2 mm).")]
    [Range(0f, 0.01f)] public float verticalSpacing = 0.0002f;

    [Tooltip("Altura desde la que cada bloque se suelta durante el build. Mas alto = mas energia al asentarse.")]
    [Range(0f, 0.05f)] public float dropHeight = 0.01f;

    /// <summary>
    /// Copia los valores de otro config a este. No copia el nombre.
    /// Util para inicializar el live config desde un preset.
    /// </summary>
    public void CopyFrom(JengaPhysicsConfig other)
    {
        if (other == null) return;

        staticFriction = other.staticFriction;
        dynamicFriction = other.dynamicFriction;
        bounciness = other.bounciness;
        frictionCombine = other.frictionCombine;
        bounceCombine = other.bounceCombine;

        mass = other.mass;
        drag = other.drag;
        angularDrag = other.angularDrag;
        collisionDetection = other.collisionDetection;
        interpolation = other.interpolation;

        solverIterations = other.solverIterations;
        solverVelocityIterations = other.solverVelocityIterations;
        contactOffset = other.contactOffset;
        bounceThreshold = other.bounceThreshold;
        sleepThreshold = other.sleepThreshold;

        grabPositionLerp = other.grabPositionLerp;
        preserveVelocityOnRelease = other.preserveVelocityOnRelease;
        releaseVelocitySamples = other.releaseVelocitySamples;
        releaseVelocityMax = other.releaseVelocityMax;

        horizontalSpacing = other.horizontalSpacing;
        verticalSpacing = other.verticalSpacing;
        dropHeight = other.dropHeight;
    }
}
