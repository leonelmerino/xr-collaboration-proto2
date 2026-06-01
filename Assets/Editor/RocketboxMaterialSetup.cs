#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool: configura los materials de los avatares Rocketbox para que renderen
/// correctamente en Built-in Render Pipeline.
///
/// Que hace:
/// 1. Recorre cada subcarpeta de Assets/Avatars/Rocketbox/.
/// 2. Para cada FBX encontrado:
///    a. Marca como Normal Map las texturas que terminan en *_normal / *_normalgl / *_normaldx.
///    b. Extrae los materials embedded del FBX a una subcarpeta Materials/.
///    c. Re-importa el FBX para que tome las referencias externas.
///    d. Para cada material extraido:
///       - Cambia shader a "Standard (Specular setup)" (Rocketbox usa workflow especular).
///       - Asigna las texturas matching por convencion de nombres:
///           <materialName>_color    -> _MainTex (Albedo)
///           <materialName>_normal   -> _BumpMap (Normal Map)
///           <materialName>_specular -> _SpecGlossMap (Specular color + smoothness en alpha)
///       - Habilita _NORMALMAP y _SPECGLOSSMAP keywords.
///
/// Idempotente: re-correr el menu sobreescribe configuraciones existentes sin romper nada.
/// </summary>
public static class RocketboxMaterialSetup
{
    private const string RootFolder = "Assets/Avatars/Rocketbox";

    [MenuItem("XR Collab/Avatars/Setup Rocketbox Materials")]
    public static void SetupAll()
    {
        if (!AssetDatabase.IsValidFolder(RootFolder))
        {
            EditorUtility.DisplayDialog("Rocketbox Setup",
                $"No se encuentra '{RootFolder}'. Copia los avatares de Rocketbox " +
                "(carpetas como Male_Adult_07, etc) a esa ruta primero.", "OK");
            return;
        }

        int totalNormalsFixed = 0;
        int totalExtracted = 0;
        int totalConfigured = 0;
        int totalFbxs = 0;

        try
        {
            AssetDatabase.StartAssetEditing();

            string[] avatarFolders = AssetDatabase.GetSubFolders(RootFolder);
            foreach (var avatarFolder in avatarFolders)
            {
                ProcessAvatarFolder(avatarFolder,
                    out int normalsFixed,
                    out int extracted,
                    out int configured,
                    out int fbxs);

                totalNormalsFixed += normalsFixed;
                totalExtracted += extracted;
                totalConfigured += configured;
                totalFbxs += fbxs;
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        Debug.Log($"[RocketboxMaterialSetup] FBXs procesados: {totalFbxs}, " +
                  $"normal maps fixed: {totalNormalsFixed}, extraidos: {totalExtracted}, " +
                  $"configurados: {totalConfigured}.");

        EditorUtility.DisplayDialog("Rocketbox Material Setup",
            $"FBXs procesados: {totalFbxs}\n" +
            $"Normal maps marcados: {totalNormalsFixed}\n" +
            $"Materials extraidos: {totalExtracted}\n" +
            $"Materials configurados: {totalConfigured}\n\n" +
            "Si los avatares todavia se ven planos o rosa, revisa la Console por warnings.\n" +
            "Las texturas se buscan por convencion de nombres: <materialName>_color/_normal/_specular.",
            "OK");
    }

    private static void ProcessAvatarFolder(string avatarFolder,
        out int normalsFixed, out int extracted, out int configured, out int fbxs)
    {
        normalsFixed = 0;
        extracted = 0;
        configured = 0;
        fbxs = 0;

        // Donde estan las texturas (convencion Rocketbox: <avatar>/Textures/).
        string texturesFolder = $"{avatarFolder}/Textures";
        if (!AssetDatabase.IsValidFolder(texturesFolder))
        {
            // Buscar Textures en subcarpetas (a veces vienen en Export/Textures).
            var subs = AssetDatabase.GetSubFolders(avatarFolder);
            foreach (var sub in subs)
            {
                if (sub.EndsWith("/Textures"))
                {
                    texturesFolder = sub;
                    break;
                }
                // Buscar un nivel mas adentro.
                var subsubs = AssetDatabase.GetSubFolders(sub);
                foreach (var ss in subsubs)
                {
                    if (ss.EndsWith("/Textures")) { texturesFolder = ss; break; }
                }
            }
        }

        // Fix normal map import type.
        normalsFixed += FixNormalMapsInFolder(texturesFolder);

        // Encontrar FBX(s) en este avatar.
        string[] fbxGuids = AssetDatabase.FindAssets("t:Model", new[] { avatarFolder });
        foreach (var guid in fbxGuids)
        {
            string fbxPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!fbxPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
            fbxs++;

            // Crear Materials/ junto al FBX si no existe.
            string materialsFolder = $"{Path.GetDirectoryName(fbxPath).Replace('\\', '/')}/Materials";
            if (!AssetDatabase.IsValidFolder(materialsFolder))
            {
                string parent = Path.GetDirectoryName(fbxPath).Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, "Materials");
            }

            // Extraer materials embedded del FBX.
            var subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath);
            foreach (var asset in subAssets)
            {
                if (asset is Material srcMat)
                {
                    string extractPath = $"{materialsFolder}/{srcMat.name}.mat";
                    var existing = AssetDatabase.LoadAssetAtPath<Material>(extractPath);
                    if (existing != null)
                    {
                        // Ya extraido antes. Solo re-configuramos.
                        continue;
                    }

                    string result = AssetDatabase.ExtractAsset(srcMat, extractPath);
                    if (string.IsNullOrEmpty(result))
                    {
                        extracted++;
                    }
                    else
                    {
                        Debug.LogWarning($"[RocketboxMaterialSetup] No se pudo extraer '{srcMat.name}' " +
                                         $"del FBX {fbxPath}: {result}");
                    }
                }
            }

            // Force reimport del FBX para que tome las nuevas referencias.
            AssetDatabase.WriteImportSettingsIfDirty(fbxPath);
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);

            // Ahora configurar cada material extraido.
            string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { materialsFolder });
            foreach (var mGuid in matGuids)
            {
                string mPath = AssetDatabase.GUIDToAssetPath(mGuid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(mPath);
                if (mat == null) continue;
                if (ConfigureMaterial(mat, texturesFolder))
                    configured++;
            }
        }
    }

    private static bool ConfigureMaterial(Material mat, string texturesFolder)
    {
        if (mat == null) return false;

        // Standard (Specular setup) — Rocketbox usa workflow especular.
        Shader specShader = Shader.Find("Standard (Specular setup)");
        if (specShader != null)
        {
            mat.shader = specShader;
        }
        else
        {
            Debug.LogWarning("[RocketboxMaterialSetup] Shader 'Standard (Specular setup)' no encontrado.");
        }

        // Buscar texturas por convencion: <matName>_color, _normal, _specular.
        Texture color = FindTextureByName(texturesFolder, mat.name + "_color");
        Texture normal = FindTextureByName(texturesFolder, mat.name + "_normal");
        Texture specular = FindTextureByName(texturesFolder, mat.name + "_specular");

        bool anyAssigned = false;

        if (color != null)
        {
            mat.SetTexture("_MainTex", color);
            anyAssigned = true;
        }

        if (normal != null)
        {
            EnsureNormalMapImport(normal);
            mat.SetTexture("_BumpMap", normal);
            mat.EnableKeyword("_NORMALMAP");
            anyAssigned = true;
        }

        if (specular != null)
        {
            mat.SetTexture("_SpecGlossMap", specular);
            mat.EnableKeyword("_SPECGLOSSMAP");
            anyAssigned = true;
        }

        // Smoothness slider razonable para piel/ropa.
        mat.SetFloat("_Glossiness", 0.3f);
        mat.SetFloat("_GlossMapScale", 0.3f);

        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);

        if (!anyAssigned)
        {
            Debug.LogWarning($"[RocketboxMaterialSetup] No se encontraron texturas para '{mat.name}' " +
                             $"en {texturesFolder}. Esperaba: {mat.name}_color, {mat.name}_normal, {mat.name}_specular");
        }

        return anyAssigned;
    }

    private static Texture FindTextureByName(string folder, string namePattern)
    {
        if (!AssetDatabase.IsValidFolder(folder)) return null;

        string[] guids = AssetDatabase.FindAssets($"t:Texture {namePattern}", new[] { folder });
        foreach (var g in guids)
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            string n = Path.GetFileNameWithoutExtension(p);
            if (string.Equals(n, namePattern, StringComparison.OrdinalIgnoreCase))
            {
                return AssetDatabase.LoadAssetAtPath<Texture>(p);
            }
        }
        return null;
    }

    private static int FixNormalMapsInFolder(string folder)
    {
        if (!AssetDatabase.IsValidFolder(folder)) return 0;

        int fixedCount = 0;
        string[] guids = AssetDatabase.FindAssets("t:Texture", new[] { folder });
        foreach (var g in guids)
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            string n = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();

            bool isNormal = n.EndsWith("_normal") ||
                            n.EndsWith("_normalgl") ||
                            n.EndsWith("_normaldx") ||
                            n.Contains("_normalmap");

            if (!isNormal) continue;

            var imp = AssetImporter.GetAtPath(p) as TextureImporter;
            if (imp == null) continue;
            if (imp.textureType != TextureImporterType.NormalMap)
            {
                imp.textureType = TextureImporterType.NormalMap;
                imp.SaveAndReimport();
                fixedCount++;
            }
        }
        return fixedCount;
    }

    private static void EnsureNormalMapImport(Texture tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        if (string.IsNullOrEmpty(path)) return;
        var imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp == null) return;
        if (imp.textureType != TextureImporterType.NormalMap)
        {
            imp.textureType = TextureImporterType.NormalMap;
            imp.SaveAndReimport();
        }
    }
}
#endif
