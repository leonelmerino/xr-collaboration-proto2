using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using XRCollab.Measurement.Cues;

/// <summary>
/// Crea (o actualiza) los materiales de las señales 3D en Assets/Measurement/FeedbackCues/Resources/FeedbackCues.
/// Están en Resources para que el build incluya sus shaders y variantes (la app los arma en código).
///   - CueText:     XRCollab/CueText: atlas SDF de TMP con bisel e iluminación Standard (nítido en XR; ver el shader).
///   - CueMetal:    Standard: íconos metálicos que reflejan el cubemap hecho del passthrough.
///   - CueAdditive: XRCollab/CueFx aditivo sin alpha (brillos que suman luz a la sala).
///   - CueAlpha:    XRCollab/CueFx con alpha premultiplicado (sombra, confeti, esquirlas).
/// Menú: XR Collab → Señales 3D: regenerar materiales. MeasurementBuild lo llama antes de cada build.
/// </summary>
public static class FeedbackCueAssetsBuilder
{
    private const string Folder = "Assets/Measurement/FeedbackCues/Resources/" + CueAssets.ResourceFolder;
    private const string FontPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

    [MenuItem("XR Collab/Señales 3D: regenerar materiales")]
    public static void Ensure()
    {
        Directory.CreateDirectory(Folder);
        AssetDatabase.Refresh();

        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        Shader sdf = Shader.Find("XRCollab/CueText");
        if (font != null && sdf != null)
        {
            var text = new Material(sdf);
            text.CopyPropertiesFromMaterial(font.material);   // atlas y proporciones que TMP necesita
            text.SetColor("_FaceColor", Color.white);
            text.SetColor("_OutlineColor", new Color(0.08f, 0.05f, 0.03f, 1f));
            text.SetFloat("_OutlineWidth", 0.12f);
            text.SetFloat("_FaceDilate", 0.08f);
            text.SetFloat("_BoldDilate", 0.06f);
            text.SetFloat("_Bevel", 0.6f);
            text.SetFloat("_BevelWidth", 0.16f);
            text.SetFloat("_BevelRoundness", 0.6f);
            text.SetFloat("_Glossiness", 0.65f);
            text.SetFloat("_OutlineGlossiness", 0.35f);
            text.SetFloat("_Metallic", 0.1f);
            Save(text, CueAssets.TextMaterialName);
            string old = $"{Folder}/CueTextGlow.mat";   // material de la versión con shaders de TMP, ya no se usa
            if (AssetDatabase.LoadAssetAtPath<Material>(old) != null) AssetDatabase.DeleteAsset(old);
        }
        else Debug.LogError($"[FeedbackCues] no está {FontPath} o no compila XRCollab/CueText: falta CueText");

        var metal = new Material(Shader.Find("Standard"));
        metal.SetColor("_Color", Color.white);
        metal.SetFloat("_Metallic", 0.7f);
        metal.SetFloat("_Glossiness", 0.75f);
        Save(metal, CueAssets.MetalMaterialName);

        Shader fx = Shader.Find("XRCollab/CueFx");
        if (fx != null)
        {
            Save(Fx(fx, BlendMode.One, BlendMode.One, 14f), CueAssets.AdditiveMaterialName);
            Save(Fx(fx, BlendMode.One, BlendMode.OneMinusSrcAlpha, 15f), CueAssets.AlphaMaterialName);
        }
        else Debug.LogError("[FeedbackCues] no compila el shader XRCollab/CueFx: faltan CueAdditive/CueAlpha");

        AssetDatabase.SaveAssets();
        Debug.Log($"[FeedbackCues] materiales listos en {Folder}");
    }

    private static Material Fx(Shader shader, BlendMode src, BlendMode dst, float colorMask)
    {
        var m = new Material(shader);
        m.SetFloat("_SrcBlend", (float)src);
        m.SetFloat("_DstBlend", (float)dst);
        m.SetFloat("_ColorMask", colorMask);
        m.SetFloat("_Premul", 1f);
        m.SetFloat("_ZWrite", 0f);
        return m;
    }

    private static void Save(Material m, string name)
    {
        string path = $"{Folder}/{name}.mat";
        m.name = name;
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
        {
            existing.shader = m.shader;
            existing.CopyPropertiesFromMaterial(m);
            existing.shaderKeywords = m.shaderKeywords;
            EditorUtility.SetDirty(existing);
        }
        else AssetDatabase.CreateAsset(m, path);
    }
}
