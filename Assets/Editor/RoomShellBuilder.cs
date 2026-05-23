#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool: construye el shell de una habitacion limpia, hecha de 6 Cube primitives
/// finos (slabs de 0.1m de espesor en una dimension, dimensiones de cuarto en las otras dos).
///
/// Por que Cube en vez de Quad:
/// - Cube tiene las 6 caras visibles desde afuera, no hay que pelearse con orientaciones
///   de normales / back-face culling.
/// - Da espesor fisico real a paredes/piso/techo (look mas natural en vistas no-VR).
/// - Trae BoxCollider built-in (Floor automaticamente queda con collider).
/// - Sigue siendo barato: 12 tris por cube * 6 = 72 tris totales para todo el shell.
///
/// Topologia (cuarto de WxHxD = 6x3x6 m por default, centrado en origen):
/// - Floor   : slab 6m x 0.1m x 6m, position (0, -0.05, 0)    -> top en y=0
/// - Ceiling : slab 6m x 0.1m x 6m, position (0, h+0.05, 0)   -> bottom en y=h
/// - WallSouth: slab 6m x 3m x 0.1m, position (0, h/2, -d/2-0.05)
/// - WallNorth: slab 6m x 3m x 0.1m, position (0, h/2, +d/2+0.05)
/// - WallEast : slab 0.1m x 3m x 6m, position (+w/2+0.05, h/2, 0)
/// - WallWest : slab 0.1m x 3m x 6m, position (-w/2-0.05, h/2, 0)
///
/// Cada cube queda fuera del volumen interior del cuarto, asi que su cara INTERIOR (la
/// que mira al cuarto) es visible cuando el usuario esta dentro.
///
/// Marca Static + asigna materiales por superficie.
/// </summary>
public static class RoomShellBuilder
{
    private const string DefaultRoomName = "RoomNew";

    // Defaults editables aqui en el script.
    private static readonly Vector3 DefaultSize = new Vector3(6f, 3f, 6f); // ancho, alto, profundidad
    private static readonly Vector3 DefaultCenter = new Vector3(0f, 0f, 0f); // piso en y=0
    private const float WallThickness = 0.1f;

    [MenuItem("XR Collab/Room/Create Room Shell")]
    public static void CreateRoomShell()
    {
        string roomName = DefaultRoomName;
        Vector3 size = DefaultSize;
        Vector3 center = DefaultCenter;

        // Sobreescribir si existe.
        var existing = GameObject.Find(roomName);
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("Room Shell Builder",
                $"Ya existe un GameObject llamado '{roomName}' en la escena.\n\n" +
                $"¿Sobreescribir? (Undo lo recupera)",
                "Sobreescribir", "Cancelar"))
            {
                return;
            }
            Undo.DestroyObjectImmediate(existing);
        }

        // Materiales (best effort).
        var floorMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/FloorMaterial.mat");
        var wallMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/WallMaterial.mat");
        var ceilingMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/CeilingMaterial.mat");

        if (floorMat == null) Debug.LogWarning("[RoomShellBuilder] FloorMaterial.mat no encontrado.");
        if (wallMat == null) Debug.LogWarning("[RoomShellBuilder] WallMaterial.mat no encontrado.");
        if (ceilingMat == null) Debug.LogWarning("[RoomShellBuilder] CeilingMaterial.mat no encontrado.");

        // Crear root.
        var root = new GameObject(roomName);
        root.transform.position = center;
        Undo.RegisterCreatedObjectUndo(root, "Create Room Shell");

        float halfW = size.x * 0.5f;
        float halfD = size.z * 0.5f;
        float h = size.y;
        float t = WallThickness;
        float tHalf = t * 0.5f;

        // Floor: slab fino, top alineado a y=0.
        CreateSlab(root.transform, "Floor",
            new Vector3(0f, -tHalf, 0f),
            new Vector3(size.x, t, size.z),
            floorMat);

        // Ceiling: slab fino, bottom alineado a y=h.
        CreateSlab(root.transform, "Ceiling",
            new Vector3(0f, h + tHalf, 0f),
            new Vector3(size.x, t, size.z),
            ceilingMat);

        // WallSouth: cara norte (interior) alineada a z=-halfD.
        CreateSlab(root.transform, "WallSouth",
            new Vector3(0f, h * 0.5f, -halfD - tHalf),
            new Vector3(size.x, h, t),
            wallMat);

        // WallNorth: cara sur (interior) alineada a z=+halfD.
        CreateSlab(root.transform, "WallNorth",
            new Vector3(0f, h * 0.5f, halfD + tHalf),
            new Vector3(size.x, h, t),
            wallMat);

        // WallEast: cara oeste (interior) alineada a x=+halfW.
        CreateSlab(root.transform, "WallEast",
            new Vector3(halfW + tHalf, h * 0.5f, 0f),
            new Vector3(t, h, size.z),
            wallMat);

        // WallWest: cara este (interior) alineada a x=-halfW.
        CreateSlab(root.transform, "WallWest",
            new Vector3(-halfW - tHalf, h * 0.5f, 0f),
            new Vector3(t, h, size.z),
            wallMat);

        // Marcar Static para lightmap bake.
        SetStaticRecursive(root, true);

        Selection.activeGameObject = root;
        EditorGUIUtility.PingObject(root);
        SceneView.lastActiveSceneView?.FrameSelected();

        Debug.Log($"[RoomShellBuilder] Creado '{roomName}' centrado en {center}, " +
                  $"interior {size.x}m x {size.y}m x {size.z}m. Espesor de muros {t}m. " +
                  "Static activado. BoxCollider en cada superficie (las paredes ahora bloquean fisica si el avatar se mueve).");

        EditorUtility.DisplayDialog("Room Shell Builder",
            $"Habitacion '{roomName}' creada.\n\n" +
            $"Interior: {size.x}m x {size.y}m x {size.z}m centrado en {center}.\n" +
            $"Espesor de muros: {t}m.\n\n" +
            "Pasos siguientes:\n" +
            "1. Desactivar la habitacion vieja para no chocar geometria.\n" +
            "2. Verificar que la mesa de Jenga y el XR Origin estan dentro del cuarto nuevo.\n" +
            "3. Cuando confirmes que se ve bien, continuamos con la Fase 4 (poblar con muebles).",
            "OK");
    }

    private static GameObject CreateSlab(Transform parent, string name,
        Vector3 localPos, Vector3 worldScale, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = worldScale;

        // El BoxCollider que viene con Cube primitive ya esta perfecto (1x1x1 en local
        // space, escalado por transform). No tocamos.

        if (material != null)
        {
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
        }

        return go;
    }

    private static void SetStaticRecursive(GameObject go, bool isStatic)
    {
        go.isStatic = isStatic;
        foreach (Transform child in go.transform)
            SetStaticRecursive(child.gameObject, isStatic);
    }
}
#endif
