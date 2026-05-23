#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Editor tool: arma una iluminacion cinematografica para la habitacion nueva (cuarto cerrado).
///
/// Que crea / modifica:
/// 1. Reconfigura el Directional Light existente (o crea uno si no existe):
///    - Modo Mixed (contribuye a bake + shadows realtime sobre dynamic objects).
///    - Intensity 0.5 (suave, simula sol indirecto entrando por hipoteticas ventanas).
///    - Color temperature 5000K (neutro).
///    - Soft shadows.
///
/// 2. Crea un Spot Light cenital sobre la mesa de Jenga ("KeyLight_Table"):
///    - Color temperature 4000K (calido, levemente neutro).
///    - Intensity 5, range 4m, spot angle 60.
///    - Soft shadows.
///    - Modo Mixed.
///
/// 3. Crea un Point Light de ambiente en el centro del cuarto a media altura ("FillLight_Center"):
///    - Color temperature 2800K (incandescente calido).
///    - Intensity 1.5, range 5m.
///    - Sin shadows (para ahorrar costo, ya hay del spot).
///    - Modo Baked.
///
/// 4. Crea un Reflection Probe Baked centrado en el cuarto ("RoomReflectionProbe"):
///    - Box-shaped, tamano del cuarto.
///    - Resolucion 256.
///    - Box projection ON (parallax-corrected reflections, importante para indoor).
///
/// 5. Ajusta los settings de ambient lighting de la escena:
///    - Ambient intensity multiplier subido a 1.2 (default 1.0).
///
/// Todo lo nuevo se agrupa bajo un GameObject "RoomLighting" como padre.
/// Re-correr el menu: te pregunta si sobreescribir. Idempotente.
/// </summary>
public static class RoomLighting
{
    private const string LightingRootName = "RoomLighting";
    private const string KeyLightName = "KeyLight_Table";
    private const string FillLightName = "FillLight_Center";
    private const string ReflectionProbeName = "RoomReflectionProbe";

    // Defaults — alineados al RoomNew (6 x 3 x 6 m centrado en origen).
    private static readonly Vector3 RoomSize = new Vector3(6f, 3f, 6f);
    private static readonly Vector3 RoomCenter = new Vector3(0f, 0f, 0f);

    [MenuItem("XR Collab/Room/Setup Lighting")]
    public static void SetupLighting()
    {
        var existing = GameObject.Find(LightingRootName);
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("Room Lighting",
                $"Ya existe '{LightingRootName}' en escena. ¿Sobreescribir?",
                "Sobreescribir", "Cancelar"))
                return;
            Undo.DestroyObjectImmediate(existing);
        }

        var root = new GameObject(LightingRootName);
        Undo.RegisterCreatedObjectUndo(root, "Create Room Lighting");

        // 1. Reconfigurar Directional Light existente, o crearlo.
        ConfigureDirectionalLight(root);

        // 2. KeyLight: spot cenital sobre la mesa.
        CreateKeyLight(root);

        // 3. FillLight: point light de ambiente.
        CreateFillLight(root);

        // 4. Reflection Probe.
        CreateReflectionProbe(root);

        // 5. Ambient intensity de la escena.
        TweakAmbientSettings();

        Selection.activeGameObject = root;
        EditorGUIUtility.PingObject(root);

        Debug.Log("[RoomLighting] Setup completo. Para iluminacion definitiva, bakear lightmaps " +
                  "(Lighting -> Generate Lighting). Hasta entonces, vas a ver con shadows + ambient realtime.");

        EditorUtility.DisplayDialog("Room Lighting",
            "Iluminacion creada:\n" +
            "  - Directional Light reconfigurado (Mixed, 5000K, intensity 0.5)\n" +
            "  - KeyLight_Table (Spot, cenital sobre mesa)\n" +
            "  - FillLight_Center (Point, ambient calido)\n" +
            "  - RoomReflectionProbe (Box, baked, parallax-corrected)\n" +
            "  - Ambient intensity = 1.2\n\n" +
            "Si la posicion del 'KeyLight_Table' no coincide con tu mesa Jenga, " +
            "movela manualmente al lugar correcto.\n\n" +
            "Para iluminacion final, bakear lightmaps con Lighting -> Generate Lighting.",
            "OK");
    }

    // --- Directional Light ---------------------------------------------------

    private static void ConfigureDirectionalLight(GameObject root)
    {
        Light dir = FindDirectionalLight();
        bool created = false;
        if (dir == null)
        {
            var go = new GameObject("DirectionalLight");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(0f, 5f, 0f);
            go.transform.localRotation = Quaternion.Euler(50f, -30f, 0f);
            dir = go.AddComponent<Light>();
            dir.type = LightType.Directional;
            created = true;
        }

        dir.color = new Color(1f, 0.96f, 0.88f, 1f); // warm white tint
        dir.colorTemperature = 5000f;
        dir.useColorTemperature = true;
        dir.intensity = 0.5f;
        dir.shadows = LightShadows.Soft;
        dir.shadowStrength = 0.8f;
        dir.shadowNormalBias = 0.4f;
        dir.shadowBias = 0.05f;
        dir.lightmapBakeType = LightmapBakeType.Mixed;
        dir.bounceIntensity = 1f;

        EditorUtility.SetDirty(dir);
        EditorUtility.SetDirty(dir.gameObject);

        if (created)
            Debug.Log("[RoomLighting] Directional Light creado (no habia uno en escena).");
        else
            Debug.Log($"[RoomLighting] Directional Light reconfigurado en GameObject '{dir.gameObject.name}'.");
    }

    private static Light FindDirectionalLight()
    {
        var lights = Object.FindObjectsOfType<Light>();
        foreach (var l in lights)
            if (l.type == LightType.Directional) return l;
        return null;
    }

    // --- KeyLight (Spot cenital sobre la mesa) -------------------------------

    private static void CreateKeyLight(GameObject root)
    {
        // Intentamos encontrar la mesa Jenga: el JengaTowerGenerator nos da el lugar.
        Vector3 targetPos = FindJengaTableCenter();

        var go = new GameObject(KeyLightName);
        go.transform.SetParent(root.transform, false);
        // Posicion: 1.5m arriba de la mesa, mirando hacia abajo.
        go.transform.position = new Vector3(targetPos.x, targetPos.y + 1.8f, targetPos.z);
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        var l = go.AddComponent<Light>();
        l.type = LightType.Spot;
        l.colorTemperature = 4000f;
        l.useColorTemperature = true;
        l.color = Color.white;
        l.intensity = 5f;
        l.range = 4f;
        l.spotAngle = 60f;
        l.innerSpotAngle = 30f;
        l.shadows = LightShadows.Soft;
        l.shadowStrength = 0.7f;
        l.lightmapBakeType = LightmapBakeType.Mixed;

        Debug.Log($"[RoomLighting] KeyLight_Table creado en {go.transform.position}. " +
                  "Si tu mesa esta en otra ubicacion, mueve este GameObject.");
    }

    private static Vector3 FindJengaTableCenter()
    {
        // 1. Si existe JengaTowerGenerator, usa su transform.
        var jengaGen = Object.FindObjectOfType<JengaTowerGenerator>();
        if (jengaGen != null) return jengaGen.transform.position;

        // 2. Buscar un GameObject llamado "TableCenter" como fallback.
        var tableCenter = GameObject.Find("TableCenter");
        if (tableCenter != null) return tableCenter.transform.position;

        // 3. Fallback final: centro del cuarto a altura de mesa (~0.7m).
        return new Vector3(RoomCenter.x, 0.7f, RoomCenter.z);
    }

    // --- FillLight (Point central de ambiente calida) ------------------------

    private static void CreateFillLight(GameObject root)
    {
        var go = new GameObject(FillLightName);
        go.transform.SetParent(root.transform, false);
        // Altura ~ 2/3 del cuarto, centrado horizontalmente.
        go.transform.position = new Vector3(RoomCenter.x, RoomSize.y * 0.65f, RoomCenter.z);

        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.colorTemperature = 2800f;
        l.useColorTemperature = true;
        l.color = Color.white;
        l.intensity = 1.5f;
        l.range = 5f;
        l.shadows = LightShadows.None; // Sin shadows, ahorra costo.
        l.lightmapBakeType = LightmapBakeType.Baked;
        l.bounceIntensity = 1f;

        Debug.Log($"[RoomLighting] FillLight_Center creado en {go.transform.position}.");
    }

    // --- Reflection Probe ----------------------------------------------------

    private static void CreateReflectionProbe(GameObject root)
    {
        var go = new GameObject(ReflectionProbeName);
        go.transform.SetParent(root.transform, false);
        go.transform.position = new Vector3(RoomCenter.x, RoomSize.y * 0.5f, RoomCenter.z);

        var probe = go.AddComponent<ReflectionProbe>();
        probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Baked;
        probe.size = new Vector3(RoomSize.x, RoomSize.y, RoomSize.z);
        probe.center = Vector3.zero;
        probe.boxProjection = true; // parallax correction para indoor reflections
        probe.resolution = 256;
        probe.intensity = 1f;
        probe.shadowDistance = 100f;
        probe.clearFlags = UnityEngine.Rendering.ReflectionProbeClearFlags.Skybox;

        Debug.Log($"[RoomLighting] RoomReflectionProbe creado, tamano {probe.size}.");
    }

    // --- Ambient settings de escena ------------------------------------------

    private static void TweakAmbientSettings()
    {
        RenderSettings.ambientIntensity = 1.2f;
        // El modo (Skybox / Trilight / Flat) lo dejamos como esta. Usuario decide.
        Debug.Log("[RoomLighting] RenderSettings.ambientIntensity = 1.2");
    }
}
#endif
