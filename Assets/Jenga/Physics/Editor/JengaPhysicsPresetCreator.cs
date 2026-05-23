#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool. Menu: XR Collab > Jenga > Create / Reset Default Physics Presets.
///
/// Crea (o sobreescribe) 5 presets de fisica de Jenga en Assets/Jenga/Physics/Presets/.
/// Re-correr es seguro: borra los assets existentes en esa carpeta y los regenera.
///
/// Despues de correrlo, hay que arrastrar los 5 .asset al array `presets` del componente
/// JengaPhysicsRuntime en la escena (orden sugerido = el de los presets, asi 1 = Baseline).
/// </summary>
public static class JengaPhysicsPresetCreator
{
    private const string PresetFolder = "Assets/Jenga/Physics/Presets";

    [MenuItem("XR Collab/Jenga/Create or Reset Default Physics Presets")]
    public static void CreatePresets()
    {
        if (!AssetDatabase.IsValidFolder(PresetFolder))
        {
            // Crear toda la cadena de carpetas si no existe.
            string parent = "Assets/Jenga/Physics";
            if (!AssetDatabase.IsValidFolder(parent))
                AssetDatabase.CreateFolder("Assets/Jenga", "Physics");
            AssetDatabase.CreateFolder(parent, "Presets");
        }

        WritePreset("01_Baseline",      BuildBaseline());
        WritePreset("02_RealWood",      BuildRealWood());
        WritePreset("03_StableDemo",    BuildStableDemo());
        WritePreset("04_LoosePlay",     BuildLoosePlay());
        WritePreset("05_PolishedVR",    BuildPolishedVR());

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[JengaPhysicsPresetCreator] 5 presets creados/actualizados en " + PresetFolder);
        EditorUtility.DisplayDialog(
            "Jenga Physics Presets",
            "Se generaron 5 presets en:\n\n" + PresetFolder + "\n\n" +
            "Pasos siguientes:\n" +
            "1. Selecciona el GameObject que tiene JengaPhysicsRuntime en la escena.\n" +
            "2. En el campo 'Presets' del Inspector, arrastra los 5 .asset (en el orden 01..05).\n" +
            "3. Asegurate que 'Block Material' apunta a JengaBlockPhysics.physicMaterial.\n" +
            "4. Play. Pulsa F6 para abrir el HUD.",
            "OK");
    }

    private static void WritePreset(string fileName, JengaPhysicsConfig cfg)
    {
        string path = $"{PresetFolder}/{fileName}.asset";

        // Si existe, borrar primero para que la operacion sea idempotente.
        var existing = AssetDatabase.LoadAssetAtPath<JengaPhysicsConfig>(path);
        if (existing != null) AssetDatabase.DeleteAsset(path);

        AssetDatabase.CreateAsset(cfg, path);
    }

    // --- Definicion de los 5 presets ---------------------------------------

    /// <summary>
    /// Replica exacta del setup actual del proyecto. Punto de comparacion para A/B.
    /// </summary>
    private static JengaPhysicsConfig BuildBaseline()
    {
        var c = ScriptableObject.CreateInstance<JengaPhysicsConfig>();
        c.presetName = "Baseline (actual)";

        c.staticFriction = 0.8f;
        c.dynamicFriction = 0.8f;
        c.bounciness = 0f;
        c.frictionCombine = PhysicMaterialCombine.Maximum;
        c.bounceCombine = PhysicMaterialCombine.Minimum;

        c.mass = 0.1f;
        c.drag = 0f;
        c.angularDrag = 0.2f;
        c.collisionDetection = CollisionDetectionMode.ContinuousDynamic;
        c.interpolation = RigidbodyInterpolation.Interpolate;

        c.solverIterations = 12;
        c.solverVelocityIterations = 4;
        c.contactOffset = 0.005f;
        c.bounceThreshold = 2f;
        c.sleepThreshold = 0.005f;

        c.grabPositionLerp = 0.35f;
        c.preserveVelocityOnRelease = true;
        c.releaseVelocitySamples = 3;
        c.releaseVelocityMax = 3f;

        c.horizontalSpacing = 0.0005f;
        c.verticalSpacing = 0.0002f;
        c.dropHeight = 0.01f;
        return c;
    }

    /// <summary>
    /// Aproximacion fisica de Jenga real:
    /// - Coeficiente de friccion madera-madera ≈ 0.25-0.5 (static), 0.2-0.4 (dynamic).
    /// - Masa de un bloque Jenga oficial ≈ 14g.
    /// - Bounciness casi nula (madera no rebota).
    /// - Friction Combine Average: comportamiento natural, no "pegajoso" como Maximum.
    /// - Angular drag bajo: la madera real no amortigua mucho su rotacion.
    /// Se siente mas "viva" pero requiere mas cuidado para no tumbar la torre.
    /// </summary>
    private static JengaPhysicsConfig BuildRealWood()
    {
        var c = ScriptableObject.CreateInstance<JengaPhysicsConfig>();
        c.presetName = "Real Wood";

        c.staticFriction = 0.45f;
        c.dynamicFriction = 0.40f;
        c.bounciness = 0.02f;
        c.frictionCombine = PhysicMaterialCombine.Average;
        c.bounceCombine = PhysicMaterialCombine.Average;

        c.mass = 0.015f;  // 15g, cerca del Jenga real
        c.drag = 0f;
        c.angularDrag = 0.05f;
        c.collisionDetection = CollisionDetectionMode.ContinuousDynamic;
        c.interpolation = RigidbodyInterpolation.Interpolate;

        c.solverIterations = 12;
        c.solverVelocityIterations = 4;
        c.contactOffset = 0.003f;  // mas fino, para bloques tan ligeros
        c.bounceThreshold = 2f;
        c.sleepThreshold = 0.005f;

        c.grabPositionLerp = 0.5f;  // follow mas rapido, el bloque liviano lo permite
        c.preserveVelocityOnRelease = true;
        c.releaseVelocitySamples = 3;
        c.releaseVelocityMax = 3f;

        c.horizontalSpacing = 0.0005f;
        c.verticalSpacing = 0.0002f;
        c.dropHeight = 0.008f;  // menor altura porque el bloque es mas liviano
        return c;
    }

    /// <summary>
    /// Torre super estable. Friction Combine Maximum + mass alta + spacing cero.
    /// Util para demos a usuarios que recien prueban VR, o para sesiones donde no se quiere
    /// que la torre se desestabilice por accidente.
    /// </summary>
    private static JengaPhysicsConfig BuildStableDemo()
    {
        var c = ScriptableObject.CreateInstance<JengaPhysicsConfig>();
        c.presetName = "Stable Demo";

        c.staticFriction = 1.0f;
        c.dynamicFriction = 0.85f;
        c.bounciness = 0f;
        c.frictionCombine = PhysicMaterialCombine.Maximum;
        c.bounceCombine = PhysicMaterialCombine.Minimum;

        c.mass = 0.15f;
        c.drag = 0.05f;
        c.angularDrag = 0.5f;  // damping rotacional alto = bloques no giran indefinidamente
        c.collisionDetection = CollisionDetectionMode.ContinuousDynamic;
        c.interpolation = RigidbodyInterpolation.Interpolate;

        c.solverIterations = 15;
        c.solverVelocityIterations = 5;
        c.contactOffset = 0.005f;
        c.bounceThreshold = 4f;  // suprime micro-rebotes
        c.sleepThreshold = 0.01f;  // entra en sleep mas rapido

        c.grabPositionLerp = 0.30f;
        c.preserveVelocityOnRelease = true;
        c.releaseVelocitySamples = 4;  // promedio mas largo = release mas suave
        c.releaseVelocityMax = 2f;  // velocidad maxima reducida

        c.horizontalSpacing = 0f;
        c.verticalSpacing = 0f;
        c.dropHeight = 0.005f;
        return c;
    }

    /// <summary>
    /// Torre suelta y resbalosa. Friction baja + spacing leve + bounciness minima.
    /// Util para evaluar comportamiento de caidas y sensacion al "sacudir" un bloque.
    /// </summary>
    private static JengaPhysicsConfig BuildLoosePlay()
    {
        var c = ScriptableObject.CreateInstance<JengaPhysicsConfig>();
        c.presetName = "Loose Play";

        c.staticFriction = 0.30f;
        c.dynamicFriction = 0.25f;
        c.bounciness = 0.05f;
        c.frictionCombine = PhysicMaterialCombine.Average;
        c.bounceCombine = PhysicMaterialCombine.Average;

        c.mass = 0.07f;
        c.drag = 0f;
        c.angularDrag = 0.1f;
        c.collisionDetection = CollisionDetectionMode.ContinuousDynamic;
        c.interpolation = RigidbodyInterpolation.Interpolate;

        c.solverIterations = 10;
        c.solverVelocityIterations = 3;
        c.contactOffset = 0.005f;
        c.bounceThreshold = 2f;
        c.sleepThreshold = 0.005f;

        c.grabPositionLerp = 0.4f;
        c.preserveVelocityOnRelease = true;
        c.releaseVelocitySamples = 4;
        c.releaseVelocityMax = 4f;  // mas margen, los bloques livianos se mueven mas rapido

        c.horizontalSpacing = 0.001f;
        c.verticalSpacing = 0.0003f;
        c.dropHeight = 0.015f;
        return c;
    }

    /// <summary>
    /// Tuning "redondeado" para VR. Compromiso entre estabilidad (no se cae sola) y
    /// realismo (responde naturalmente al grab/poke). Friction Average elimina la
    /// sensacion "pegajosa" del Maximum sin desestabilizar la torre.
    /// Mass intermedia para que los bloques se sientan presentes sin ser pesados.
    /// </summary>
    private static JengaPhysicsConfig BuildPolishedVR()
    {
        var c = ScriptableObject.CreateInstance<JengaPhysicsConfig>();
        c.presetName = "Polished VR";

        c.staticFriction = 0.60f;
        c.dynamicFriction = 0.50f;
        c.bounciness = 0.01f;
        c.frictionCombine = PhysicMaterialCombine.Average;
        c.bounceCombine = PhysicMaterialCombine.Average;

        c.mass = 0.08f;
        c.drag = 0f;
        c.angularDrag = 0.15f;
        c.collisionDetection = CollisionDetectionMode.ContinuousDynamic;
        c.interpolation = RigidbodyInterpolation.Interpolate;

        c.solverIterations = 12;
        c.solverVelocityIterations = 4;
        c.contactOffset = 0.004f;
        c.bounceThreshold = 2.5f;
        c.sleepThreshold = 0.005f;

        c.grabPositionLerp = 0.50f;  // follow rapido = sensacion 1:1 con la mano
        c.preserveVelocityOnRelease = true;
        c.releaseVelocitySamples = 3;
        c.releaseVelocityMax = 3.5f;

        c.horizontalSpacing = 0.0007f;
        c.verticalSpacing = 0.0002f;
        c.dropHeight = 0.012f;
        return c;
    }
}
#endif
