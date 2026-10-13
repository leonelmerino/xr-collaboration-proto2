using System.IO;
using UnityEditor;
using UnityEngine;
using XRCollab.Measurement.Occlusion;

/// <summary>
/// Crea los materiales de los oclusores (XRCollab/HandCut recorte suave, XRCollab/DepthOnly, y XRCollab/OccluderDebug
/// para verlos) en Assets/Measurement/Occlusion/Resources/Occlusion, para que el build incluya los shaders. Las mallas
/// de mano (Resources/Occlusion/Hands) son las del ejemplo Hand Visualizer de XR Hands 1.4.3. MeasurementBuild lo
/// llama antes de cada build. Menú: XR Collab → Oclusión.
/// </summary>
public static class OcclusionAssetsBuilder
{
    private const string Folder = "Assets/Measurement/Occlusion/Resources/Occlusion";

    [MenuItem("XR Collab/Oclusión: regenerar material")]
    public static void Ensure()
    {
        Directory.CreateDirectory(Folder);
        AssetDatabase.Refresh();
        EnsureMaterial("XRCollab/DepthOnly", Occluders.MaterialResource, "DepthOnly");
        EnsureMaterial("XRCollab/HandCut", Occluders.SoftMaterialResource, "HandCut");
        EnsureMaterial("XRCollab/OccluderDebug", Occluders.DebugMaterialResource, "OccluderDebug");
        AssetDatabase.SaveAssets();
    }

    private static void EnsureMaterial(string shaderName, string resource, string name)
    {
        Shader shader = Shader.Find(shaderName);
        if (shader == null) { Debug.LogError($"[Occluders] no compila {shaderName}"); return; }
        string path = $"Assets/Measurement/Occlusion/Resources/{resource}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing == null) AssetDatabase.CreateAsset(new Material(shader) { name = name, enableInstancing = true }, path);
        else { existing.shader = shader; existing.enableInstancing = true; EditorUtility.SetDirty(existing); }
        Debug.Log($"[Occluders] material listo en {path}");
    }
}
