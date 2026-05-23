#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool: puebla la habitacion nueva (RoomNew) con muebles del HDRPFurniturePack.
///
/// Los prefabs se instancian en posiciones predefinidas, se les aplica el swap de materiales
/// Built-in (los converted del HdrpToBuiltinConverter) y se marcan como Static para que entren
/// al lightmap bake.
///
/// Todos los muebles quedan agrupados bajo un parent "RoomFurniture" en la jerarquia, para que
/// puedas activar/desactivar el conjunto y reposicionar facilmente.
///
/// Re-correr el menu: te pregunta si sobreescribir. Idempotente.
/// </summary>
public static class RoomPopulator
{
    private const string FurnitureRootName = "RoomFurniture";
    private const string BuiltinMaterialsFolder = "Assets/HDRPFurniturePack_BuiltIn";

    private struct FurnitureItem
    {
        public string prefabPath;
        public Vector3 position;
        public Vector3 eulerRotation;
        public float uniformScale;
        public string newName; // optional, si queres renombrar la instancia
    }

    // Layout propuesto para la habitacion 6x3x6m. Posiciones en world space asumiendo
    // que RoomNew esta centrado en el origen (segun los defaults de RoomShellBuilder).
    // Si vos centraste la habitacion en otra parte, ajusta el offset abajo o mueve
    // el GameObject 'RoomFurniture' despues de crearlo.
    private static readonly FurnitureItem[] FurnitureLayout = new FurnitureItem[]
    {
        // Mesa de Jenga (centro de la accion). Table_97_Artek = mesa cuadrada de
        // Alvar Aalto, altura tipica de comedor (~73cm). La altura puede no coincidir
        // con la mesa coffee anterior, asi que el JengaTowerGenerator probablemente
        // necesite ajuste de Y para que la torre se asiente sobre la superficie.
        new FurnitureItem
        {
            prefabPath = "Assets/HDRPFurniturePack/Artek/Table_97_Artek/Table_97_Artek.prefab",
            position = new Vector3(0f, 0f, 0f), // se sobreescribe con la mesa del Jenga si existe
            eulerRotation = new Vector3(0f, 0f, 0f),
            uniformScale = 1f,
            newName = "Table_JengaSurface",
        },

        // Banquetas alrededor de la mesa (3 — una por participante).
        new FurnitureItem
        {
            prefabPath = "Assets/HDRPFurniturePack/Artek/Stool 60 Artek/Stool 60 Artek.prefab",
            position = new Vector3(-0.9f, 0f, -0.5f),
            eulerRotation = new Vector3(0f, 30f, 0f),
            uniformScale = 1f,
            newName = "Stool_Host",
        },
        new FurnitureItem
        {
            prefabPath = "Assets/HDRPFurniturePack/Artek/Stool 60 Artek/Stool 60 Artek.prefab",
            position = new Vector3(0.9f, 0f, -0.5f),
            eulerRotation = new Vector3(0f, -30f, 0f),
            uniformScale = 1f,
            newName = "Stool_Client",
        },
        new FurnitureItem
        {
            prefabPath = "Assets/HDRPFurniturePack/Artek/Stool 60 Artek/Stool 60 Artek.prefab",
            position = new Vector3(0f, 0f, 0.9f),
            eulerRotation = new Vector3(0f, 180f, 0f),
            uniformScale = 1f,
            newName = "Stool_Helper",
        },

        // Alfombra bajo la mesa.
        new FurnitureItem
        {
            prefabPath = "Assets/HDRPFurniturePack/Rug_High_Pile_Grey/Rug_High_Pile_Grey.prefab",
            position = new Vector3(0f, 0.001f, 0f), // 1mm arriba para evitar Z-fight con el piso
            eulerRotation = Vector3.zero,
            uniformScale = 1f,
            newName = "Rug_UnderTable",
        },

        // Sofa contra una pared (esquina sur-oeste).
        new FurnitureItem
        {
            prefabPath = "Assets/HDRPFurniturePack/Sofa_SL03_Allemuir_Stirling/Sofa_SL03_Allemuir_Stirling.prefab",
            position = new Vector3(-1.5f, 0f, -2.3f),
            eulerRotation = new Vector3(0f, 0f, 0f),
            uniformScale = 1f,
            newName = "Sofa_South",
        },

        // Planta en esquina noreste.
        new FurnitureItem
        {
            prefabPath = "Assets/HDRPFurniturePack/Plant_Potted_Monstera_Deliciosa/Plant_Potted_Monstera_Deliciosa.prefab",
            position = new Vector3(2.2f, 0f, 2.2f),
            eulerRotation = new Vector3(0f, 215f, 0f),
            uniformScale = 1f,
            newName = "Plant_Corner",
        },

        // Lampara de pie en esquina noroeste (esta tambien tendra una luz puntual en RoomLighting si la sumas).
        new FurnitureItem
        {
            prefabPath = "Assets/HDRPFurniturePack/Artek/Floor_Light_A810_Artek/Floor_Light_A810_Artek.prefab",
            position = new Vector3(-2.3f, 0f, 2.2f),
            eulerRotation = Vector3.zero,
            uniformScale = 1f,
            newName = "FloorLamp",
        },

        // Cuadro decorativo en pared norte.
        new FurnitureItem
        {
            prefabPath = "Assets/HDRPFurniturePack/Wall_Decoration_Art_Zebra/Wall_Decoration_Art_Zebra.prefab",
            position = new Vector3(0f, 1.7f, 2.94f), // 6cm de la pared norte para no Z-fight
            eulerRotation = new Vector3(0f, 180f, 0f),
            uniformScale = 1f,
            newName = "WallArt_North",
        },
    };

    [MenuItem("XR Collab/Room/Populate Furniture")]
    public static void Populate()
    {
        if (!AssetDatabase.IsValidFolder(BuiltinMaterialsFolder))
        {
            if (!EditorUtility.DisplayDialog("Room Populator",
                $"No se encuentra '{BuiltinMaterialsFolder}'.\n\n" +
                "Necesitas correr primero 'XR Collab > Materials > Convert HDRP Furniture Pack to Built-in Standard'.\n\n" +
                "¿Continuar igual? Los muebles se veran rosa (HDRP shader sin pipeline).",
                "Continuar (no recomendado)", "Cancelar"))
                return;
        }

        // Si ya existe, sobreescribir.
        var existing = GameObject.Find(FurnitureRootName);
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("Room Populator",
                $"Ya existe '{FurnitureRootName}'. ¿Sobreescribir?",
                "Sobreescribir", "Cancelar"))
                return;
            Undo.DestroyObjectImmediate(existing);
        }

        var root = new GameObject(FurnitureRootName);
        Undo.RegisterCreatedObjectUndo(root, "Populate Room Furniture");

        // Si existe JengaTowerGenerator en escena, ajustar la posicion de la mesa al
        // mismo lugar (para que la torre quede sobre la nueva mesa Coffee_Table_90D).
        Vector3 tableOverride = Vector3.zero;
        bool hasTableOverride = false;
        var jengaGen = Object.FindObjectOfType<JengaTowerGenerator>();
        if (jengaGen != null)
        {
            tableOverride = jengaGen.transform.position;
            // El blockHeight medio del Jenga es ~0.015 y la torre se construye en
            // transform.position + dropHeight*up. La superficie de la mesa Coffee_Table_90D
            // queda ~0.45m del piso (varia segun el modelo). Aproximamos: mesa a y=0 del piso,
            // dejando que el Y del JengaTowerGenerator quede igual.
            tableOverride.y = 0f;
            hasTableOverride = true;
        }

        int spawned = 0;
        int missing = 0;
        int swappedTotal = 0;

        try
        {
            AssetDatabase.StartAssetEditing();

            for (int i = 0; i < FurnitureLayout.Length; i++)
            {
                var item = FurnitureLayout[i];
                EditorUtility.DisplayProgressBar("Populating Room",
                    $"{i + 1}/{FurnitureLayout.Length}: {System.IO.Path.GetFileName(item.prefabPath)}",
                    (float)i / FurnitureLayout.Length);

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.prefabPath);
                if (prefab == null)
                {
                    Debug.LogWarning($"[RoomPopulator] Prefab no encontrado: {item.prefabPath}");
                    missing++;
                    continue;
                }

                var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null) { missing++; continue; }

                instance.transform.SetParent(root.transform, false);

                // Posicion: si es la mesa Jenga (Table_97 o Coffee_Table_90D legacy) y
                // hay override del JengaTowerGenerator, usar override.
                Vector3 pos = item.position;
                if (hasTableOverride &&
                    (item.prefabPath.Contains("Table_97") || item.prefabPath.Contains("Coffee_Table_90D")))
                {
                    pos = tableOverride;
                }
                instance.transform.localPosition = pos;
                instance.transform.localRotation = Quaternion.Euler(item.eulerRotation);
                if (item.uniformScale > 0f && item.uniformScale != 1f)
                    instance.transform.localScale = Vector3.one * item.uniformScale;

                if (!string.IsNullOrEmpty(item.newName))
                    instance.name = item.newName;

                // Swap materiales HDRP -> Built-in.
                int swapped = SwapMaterialsRecursive(instance);
                swappedTotal += swapped;

                // Static para lightmap bake (no marcamos como occluder; el bake los considera).
                SetStaticRecursive(instance, true);

                spawned++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.StopAssetEditing();
        }

        Selection.activeGameObject = root;
        EditorGUIUtility.PingObject(root);

        Debug.Log($"[RoomPopulator] Hecho. Muebles: {spawned}/{FurnitureLayout.Length}. " +
                  $"Slots de material reemplazados: {swappedTotal}. Missing prefabs: {missing}.");

        EditorUtility.DisplayDialog("Room Populator",
            $"Muebles instanciados: {spawned}/{FurnitureLayout.Length}\n" +
            $"Slots de material Built-in aplicados: {swappedTotal}\n" +
            $"Prefabs no encontrados: {missing}\n\n" +
            "Pasos siguientes:\n" +
            "1. Ajustar manualmente posiciones si algun mueble queda mal ubicado.\n" +
            "2. Verificar que el sofa, lampara, planta no atraviesan paredes.\n" +
            "3. Bakear lightmaps (Lighting -> Generate Lighting) para iluminacion final.",
            "OK");
    }

    private static int SwapMaterialsRecursive(GameObject go)
    {
        int swapped = 0;
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (m.shader != null && m.shader.name == "Standard") continue;

                string candidatePath = $"{BuiltinMaterialsFolder}/{m.name}_Builtin.mat";
                var converted = AssetDatabase.LoadAssetAtPath<Material>(candidatePath);
                if (converted != null)
                {
                    mats[i] = converted;
                    swapped++;
                    changed = true;
                }
            }
            if (changed)
            {
                r.sharedMaterials = mats;
                EditorUtility.SetDirty(r);
            }
        }
        return swapped;
    }

    private static void SetStaticRecursive(GameObject go, bool isStatic)
    {
        go.isStatic = isStatic;
        foreach (Transform child in go.transform)
            SetStaticRecursive(child.gameObject, isStatic);
    }
}
#endif
