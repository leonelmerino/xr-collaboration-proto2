#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool: convierte los materiales del HDRPFurniturePack (HDRP/Lit) a materiales
/// Built-in Standard, escribiendo los nuevos assets en Assets/HDRPFurniturePack_BuiltIn/.
///
/// Mapping de propiedades:
///   _BaseColorMap     -> _MainTex
///   _BaseColor        -> _Color
///   _NormalMap        -> _BumpMap (y se fuerza Texture Type = NormalMap en el importer)
///   _NormalScale      -> _BumpScale
///   _MaskMap (HDRP)   -> _MetallicGlossMap + _OcclusionMap (mismo asset)
///                        HDRP MaskMap: R=Metallic, G=AO, B=DetailMask, A=Smoothness
///                        Built-in MetallicGlossMap: R=Metallic, A=Smoothness  -> match directo
///                        Built-in OcclusionMap: G channel                       -> match directo
///   _OcclusionMap     -> _OcclusionMap (si esta separado, se prefiere sobre MaskMap)
///   _Metallic         -> _Metallic
///   _Smoothness       -> _Glossiness
///   tiling/offset del BaseColorMap   -> tiling/offset del MainTex
///
/// Los assets originales NO se modifican. Si re-corres el menu, los outputs existentes
/// se sobreescriben (es idempotente).
///
/// Lectura robusta: la primera pasada usa Material API (Material.GetTexture/Color/Float).
/// Si una propiedad no existe en el shader actual (por ejemplo si HDRP no esta instalado
/// y la material renderiza pink), cae a SerializedObject leyendo el YAML del .mat
/// directamente.
/// </summary>
public static class HdrpToBuiltinConverter
{
    private const string SourceRoot = "Assets/HDRPFurniturePack";
    private const string OutputFolder = "Assets/HDRPFurniturePack_BuiltIn";

    [MenuItem("XR Collab/Materials/Convert HDRP Furniture Pack to Built-in Standard")]
    public static void ConvertAll()
    {
        if (!AssetDatabase.IsValidFolder(SourceRoot))
        {
            EditorUtility.DisplayDialog("HDRP -> Built-in",
                $"No se encontro la carpeta {SourceRoot}.", "OK");
            return;
        }

        if (!AssetDatabase.IsValidFolder(OutputFolder))
            AssetDatabase.CreateFolder("Assets", "HDRPFurniturePack_BuiltIn");

        Shader standardShader = Shader.Find("Standard");
        if (standardShader == null)
        {
            Debug.LogError("[HdrpToBuiltinConverter] Standard shader no encontrado.");
            return;
        }

        string[] guids = AssetDatabase.FindAssets("t:Material", new[] { SourceRoot });
        int converted = 0;
        int skipped = 0;
        int errored = 0;

        try
        {
            AssetDatabase.StartAssetEditing();

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                EditorUtility.DisplayProgressBar("HDRP -> Built-in",
                    $"{i + 1}/{guids.Length}: {Path.GetFileName(path)}",
                    (float)i / Mathf.Max(1, guids.Length));

                try
                {
                    var src = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (src == null) { skipped++; continue; }

                    // Filtrar: solo materiales HDRP. Si el shader no se puede resolver,
                    // intentamos por nombre via SerializedObject (a veces shader=null).
                    if (!LooksLikeHdrpMaterial(src))
                    {
                        skipped++;
                        continue;
                    }

                    Material newMat = ConvertOne(src, standardShader);
                    if (newMat == null) { errored++; continue; }

                    string outPath = $"{OutputFolder}/{src.name}_Builtin.mat";
                    var existing = AssetDatabase.LoadAssetAtPath<Material>(outPath);
                    if (existing != null)
                    {
                        // CRITICAL: mutar el material existente in-place en lugar de borrar+crear.
                        // Si lo borramos, las referencias en escena (que usan el GUID del asset)
                        // quedan rotas y todo se ve magenta. Esto es idempotente y safe para re-correr.
                        existing.shader = standardShader;
                        existing.CopyPropertiesFromMaterial(newMat);
                        existing.enableInstancing = true;
                        EditorUtility.SetDirty(existing);
                        Object.DestroyImmediate(newMat);
                    }
                    else
                    {
                        AssetDatabase.CreateAsset(newMat, outPath);
                    }
                    converted++;
                }
                catch (System.Exception e)
                {
                    errored++;
                    Debug.LogError($"[HdrpToBuiltinConverter] Error con {path}: {e.Message}");
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        Debug.Log($"[HdrpToBuiltinConverter] Done. Converted={converted}, Skipped={skipped}, Errored={errored}.");
        EditorUtility.DisplayDialog("HDRP -> Built-in",
            $"Convertidos: {converted}\n" +
            $"Saltados (no HDRP): {skipped}\n" +
            $"Con error: {errored}\n\n" +
            $"Output: {OutputFolder}/\n\n" +
            "Proximo paso: tras instanciar un prefab del HDRPFurniturePack en escena, " +
            "selecciona el root del prefab y usa el menu " +
            "'XR Collab/Materials/Swap Materials on Selected (HDRP -> Builtin)'.",
            "OK");
    }

    private static bool LooksLikeHdrpMaterial(Material m)
    {
        if (m == null) return false;
        if (m.shader != null && m.shader.name != null &&
            (m.shader.name.StartsWith("HDRP") || m.shader.name.StartsWith("Hidden/HDRP")))
            return true;

        // Fallback: revisar el YAML del .mat. Si tiene _BaseColorMap o _MaskMap definidos,
        // lo tratamos como HDRP-like (las defaults Built-in/URP no tienen esas propiedades).
        var so = new SerializedObject(m);
        return SoHasTexEnv(so, "_BaseColorMap") || SoHasTexEnv(so, "_MaskMap");
    }

    private static Material ConvertOne(Material src, Shader standardShader)
    {
        var so = new SerializedObject(src);
        var dst = new Material(standardShader);
        dst.name = src.name + "_Builtin";
        dst.enableInstancing = true;

        // --- Albedo + color tint ---
        // Fallback chain: _BaseColorMap es el slot canonico. Si esta vacio (caso de
        // ciertos assets HDRP donde el autor uso el detail slot como textura principal,
        // como Wall_Decoration_Art_Zebra), probar _DetailAlbedoMap. Si tambien vacio,
        // probar el legacy _MainTex.
        Texture baseColorMap = ReadTexture(src, so, "_BaseColorMap");
        string usedSlot = "_BaseColorMap";
        if (baseColorMap == null)
        {
            baseColorMap = ReadTexture(src, so, "_DetailAlbedoMap");
            if (baseColorMap != null) usedSlot = "_DetailAlbedoMap";
        }
        if (baseColorMap == null)
        {
            baseColorMap = ReadTexture(src, so, "_MainTex");
            if (baseColorMap != null) usedSlot = "_MainTex";
        }

        Vector2 baseScale = ReadTextureScale(src, so, usedSlot, Vector2.one);
        Vector2 baseOffset = ReadTextureOffset(src, so, usedSlot, Vector2.zero);
        if (baseColorMap != null)
        {
            dst.SetTexture("_MainTex", baseColorMap);
            dst.SetTextureScale("_MainTex", baseScale);
            dst.SetTextureOffset("_MainTex", baseOffset);

            if (usedSlot != "_BaseColorMap")
                Debug.Log($"[HdrpToBuiltinConverter] '{src.name}': BaseColorMap vacio, " +
                          $"usando textura de '{usedSlot}' como Albedo del material Built-in.");
        }

        Color baseColor = ReadColor(src, so, "_BaseColor", Color.white);
        dst.SetColor("_Color", baseColor);

        // --- Normal map ---
        Texture normalMap = ReadTexture(src, so, "_NormalMap");
        if (normalMap == null) normalMap = ReadTexture(src, so, "_BumpMap"); // legacy fallback
        if (normalMap != null)
        {
            dst.SetTexture("_BumpMap", normalMap);
            dst.EnableKeyword("_NORMALMAP");
            EnsureNormalMapImporter(normalMap);
        }
        float normalScale = ReadFloat(src, so, "_NormalScale", 1f);
        dst.SetFloat("_BumpScale", normalScale);

        // --- MaskMap -> MetallicGloss + Occlusion ---
        // HDRP MaskMap: R=Metallic, G=AO, B=DetailMask, A=Smoothness
        // Built-in MetallicGlossMap: R=Metallic, A=Smoothness   -> match
        // Built-in OcclusionMap: G channel                        -> match (HDRP MaskMap G = AO)
        Texture maskMap = ReadTexture(src, so, "_MaskMap");
        Texture separateOcclusion = ReadTexture(src, so, "_OcclusionMap");

        if (maskMap != null)
        {
            dst.SetTexture("_MetallicGlossMap", maskMap);
            dst.EnableKeyword("_METALLICGLOSSMAP");
            EnsureLinearTextureImporter(maskMap);

            // Si NO hay OcclusionMap separado, usamos el MaskMap tambien para AO.
            if (separateOcclusion == null)
                dst.SetTexture("_OcclusionMap", maskMap);
        }
        if (separateOcclusion != null)
        {
            dst.SetTexture("_OcclusionMap", separateOcclusion);
            EnsureLinearTextureImporter(separateOcclusion);
        }

        // --- Metallic / Smoothness sliders ---
        float metallic = ReadFloat(src, so, "_Metallic", 0f);
        float smoothness = ReadFloat(src, so, "_Smoothness", 0.5f);
        dst.SetFloat("_Metallic", metallic);
        dst.SetFloat("_Glossiness", smoothness);
        dst.SetFloat("_GlossMapScale", smoothness);
        dst.SetFloat("_SmoothnessTextureChannel", 0); // 0 = leer del alpha del MetallicGlossMap

        // --- Render mode opaque ---
        dst.SetFloat("_Mode", 0);
        dst.renderQueue = -1;
        dst.SetOverrideTag("RenderType", "Opaque");

        return dst;
    }

    // --- Lectores robustos: intentan Material API, caen a SerializedObject ----

    private static Texture ReadTexture(Material m, SerializedObject so, string prop)
    {
        if (m.HasProperty(prop))
        {
            try { return m.GetTexture(prop); } catch { /* fall through */ }
        }
        return SoGetTexture(so, prop);
    }

    private static Vector2 ReadTextureScale(Material m, SerializedObject so, string prop, Vector2 fallback)
    {
        if (m.HasProperty(prop))
        {
            try { return m.GetTextureScale(prop); } catch { }
        }
        Vector2 v = SoGetTextureScale(so, prop);
        return v == Vector2.zero ? fallback : v;
    }

    private static Vector2 ReadTextureOffset(Material m, SerializedObject so, string prop, Vector2 fallback)
    {
        if (m.HasProperty(prop))
        {
            try { return m.GetTextureOffset(prop); } catch { }
        }
        return SoGetTextureOffset(so, prop);
    }

    private static Color ReadColor(Material m, SerializedObject so, string prop, Color fallback)
    {
        if (m.HasProperty(prop))
        {
            try { return m.GetColor(prop); } catch { }
        }
        return SoGetColor(so, prop, fallback);
    }

    private static float ReadFloat(Material m, SerializedObject so, string prop, float fallback)
    {
        if (m.HasProperty(prop))
        {
            try { return m.GetFloat(prop); } catch { }
        }
        return SoGetFloat(so, prop, fallback);
    }

    // --- SerializedObject helpers (leen del YAML directamente) ----------------

    private static bool SoHasTexEnv(SerializedObject so, string prop)
    {
        return SoFindTexEnvEntry(so, prop) != null;
    }

    private static SerializedProperty SoFindTexEnvEntry(SerializedObject so, string prop)
    {
        var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
        if (texEnvs == null) return null;
        for (int i = 0; i < texEnvs.arraySize; i++)
        {
            var entry = texEnvs.GetArrayElementAtIndex(i);
            var first = entry.FindPropertyRelative("first");
            string key = first != null ? first.stringValue : null;
            if (key == prop) return entry;
        }
        return null;
    }

    private static Texture SoGetTexture(SerializedObject so, string prop)
    {
        var entry = SoFindTexEnvEntry(so, prop);
        if (entry == null) return null;
        var tex = entry.FindPropertyRelative("second.m_Texture");
        return tex != null ? tex.objectReferenceValue as Texture : null;
    }

    private static Vector2 SoGetTextureScale(SerializedObject so, string prop)
    {
        var entry = SoFindTexEnvEntry(so, prop);
        if (entry == null) return Vector2.one;
        var scale = entry.FindPropertyRelative("second.m_Scale");
        return scale != null ? scale.vector2Value : Vector2.one;
    }

    private static Vector2 SoGetTextureOffset(SerializedObject so, string prop)
    {
        var entry = SoFindTexEnvEntry(so, prop);
        if (entry == null) return Vector2.zero;
        var offset = entry.FindPropertyRelative("second.m_Offset");
        return offset != null ? offset.vector2Value : Vector2.zero;
    }

    private static Color SoGetColor(SerializedObject so, string prop, Color fallback)
    {
        var colors = so.FindProperty("m_SavedProperties.m_Colors");
        if (colors == null) return fallback;
        for (int i = 0; i < colors.arraySize; i++)
        {
            var entry = colors.GetArrayElementAtIndex(i);
            var first = entry.FindPropertyRelative("first");
            if (first != null && first.stringValue == prop)
            {
                var v = entry.FindPropertyRelative("second");
                if (v != null) return v.colorValue;
            }
        }
        return fallback;
    }

    private static float SoGetFloat(SerializedObject so, string prop, float fallback)
    {
        var floats = so.FindProperty("m_SavedProperties.m_Floats");
        if (floats == null) return fallback;
        for (int i = 0; i < floats.arraySize; i++)
        {
            var entry = floats.GetArrayElementAtIndex(i);
            var first = entry.FindPropertyRelative("first");
            if (first != null && first.stringValue == prop)
            {
                var v = entry.FindPropertyRelative("second");
                if (v != null) return v.floatValue;
            }
        }
        return fallback;
    }

    // --- Texture importer helpers --------------------------------------------

    private static void EnsureNormalMapImporter(Texture tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        if (string.IsNullOrEmpty(path)) return;
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;
        if (importer.textureType != TextureImporterType.NormalMap)
        {
            importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();
        }
    }

    private static void EnsureLinearTextureImporter(Texture tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        if (string.IsNullOrEmpty(path)) return;
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;
        if (importer.sRGBTexture)
        {
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
        }
    }

    // --- Swap helper para aplicar materiales convertidos a un GameObject ----

    /// <summary>
    /// Recovery tool: cuando el converter se re-ejecuto y los materials nuevos tuvieron GUID
    /// distinto al de los assets viejos (bug previo a la version "in-place"), los renderers en
    /// escena quedan apuntando a "missing material" -> aparecen magenta. Este menu repara las
    /// referencias mirando el prefab source de cada GameObject seleccionado y buscando el
    /// material Built-in equivalente por nombre.
    /// </summary>
    [MenuItem("XR Collab/Materials/Recover Magenta Materials from Prefab Source")]
    public static void RecoverMagentaMaterials()
    {
        var sel = Selection.gameObjects;
        if (sel.Length == 0)
        {
            EditorUtility.DisplayDialog("Recover Materials",
                "Selecciona uno o mas GameObjects (p.ej. RoomFurniture o sus hijos).", "OK");
            return;
        }

        int totalSlots = 0;
        int recovered = 0;
        int notFound = 0;
        int skipped = 0;
        var missingNames = new System.Collections.Generic.HashSet<string>();

        foreach (var go in sel)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                // El prefab source del Renderer (mismo Renderer en el prefab asset original).
                var prefabRenderer = PrefabUtility.GetCorrespondingObjectFromSource(r) as Renderer;
                if (prefabRenderer == null) continue;

                var prefabMats = prefabRenderer.sharedMaterials;
                var currentMats = r.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < currentMats.Length; i++)
                {
                    totalSlots++;

                    // Si ya tiene material valido y es Standard, asumimos que esta bien.
                    if (currentMats[i] != null &&
                        currentMats[i].shader != null &&
                        currentMats[i].shader.name == "Standard")
                    {
                        skipped++;
                        continue;
                    }

                    if (i >= prefabMats.Length || prefabMats[i] == null)
                    {
                        notFound++;
                        continue;
                    }

                    // El prefab tiene el material HDRP original. Buscamos su equivalente Built-in.
                    string sourceName = prefabMats[i].name;
                    string candidate = $"{OutputFolder}/{sourceName}_Builtin.mat";
                    var newMat = AssetDatabase.LoadAssetAtPath<Material>(candidate);
                    if (newMat != null)
                    {
                        currentMats[i] = newMat;
                        recovered++;
                        changed = true;
                    }
                    else
                    {
                        notFound++;
                        missingNames.Add(sourceName);
                    }
                }

                if (changed)
                {
                    Undo.RecordObject(r, "Recover Materials");
                    r.sharedMaterials = currentMats;
                    EditorUtility.SetDirty(r);
                }
            }
        }

        if (missingNames.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var n in missingNames) sb.AppendLine($"  - {n}");
            Debug.LogWarning($"[Recover] Materials sin equivalente Built-in convertido:\n{sb}");
        }

        Debug.Log($"[Recover] Slots procesados: {totalSlots}, recuperados: {recovered}, " +
                  $"sin equivalente: {notFound}, ya OK: {skipped}.");
        EditorUtility.DisplayDialog("Recover Materials",
            $"Slots totales: {totalSlots}\n" +
            $"Recuperados: {recovered}\n" +
            $"Sin equivalente Built-in: {notFound}\n" +
            $"Ya estaban OK: {skipped}",
            "OK");
    }

    [MenuItem("XR Collab/Materials/Swap Materials on Selected (HDRP -> Builtin)")]
    public static void SwapMaterialsOnSelection()
    {
        var sel = Selection.gameObjects;
        if (sel.Length == 0)
        {
            EditorUtility.DisplayDialog("Swap Materials",
                "Selecciona uno o mas GameObjects en la escena (con Renderers en hijos).", "OK");
            return;
        }

        int totalRenderers = 0;
        int swapped = 0;
        int notFound = 0;
        var missing = new HashSet<string>();

        foreach (var go in sel)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                totalRenderers++;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (m.shader != null && m.shader.name == "Standard") continue; // ya converted

                    string candidatePath = $"{OutputFolder}/{m.name}_Builtin.mat";
                    var converted_mat = AssetDatabase.LoadAssetAtPath<Material>(candidatePath);
                    if (converted_mat != null)
                    {
                        mats[i] = converted_mat;
                        swapped++;
                        changed = true;
                    }
                    else
                    {
                        notFound++;
                        missing.Add(m.name);
                    }
                }
                if (changed)
                {
                    Undo.RecordObject(r, "Swap HDRP Materials to Built-in");
                    r.sharedMaterials = mats;
                    EditorUtility.SetDirty(r);
                }
            }
        }

        if (missing.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var n in missing) sb.AppendLine($"  - {n}");
            Debug.LogWarning($"[HdrpToBuiltinConverter] Materials sin equivalente Built-in convertido:\n{sb}");
        }

        Debug.Log($"[HdrpToBuiltinConverter] Swap done. Renderers={totalRenderers}, swapped={swapped}, not found={notFound}.");
        EditorUtility.DisplayDialog("Swap Materials on Selection",
            $"Renderers procesados: {totalRenderers}\n" +
            $"Materials reemplazados: {swapped}\n" +
            $"Sin equivalente Built-in: {notFound}\n\n" +
            (notFound > 0 ? "Revisar Console para nombres faltantes (puede que algunos materiales no fueran convertidos por ser non-HDRP)." : "Todo OK."),
            "OK");
    }
}
#endif
