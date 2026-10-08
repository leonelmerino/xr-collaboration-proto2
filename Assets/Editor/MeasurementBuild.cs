using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.OpenXR;

/// <summary>
/// Build del modo medición (Win64) con las features OpenXR correctas para passthrough + mirada en PC:
///   - XR Collab: Passthrough underlay (PC):    ON  (crea y envía el passthrough; ver PassthroughUnderlayFeature)
///   - XR Collab: Meta passthrough underlay:    ON  (Meta Quest 3 por Link, XR_FB_passthrough; inerte con VIVE/SteamVR)
///   - VIVE XR Passthrough:                     OFF (su hook de xrWaitFrame congela la app junto al Eye Tracker en Mono)
///   - VIVE XR Composition Layer (Passthrough): OFF (en Standalone crea el passthrough pero nunca lo envía: negro)
///   - VIVE XR Eye Tracker:                     sin cambios (lo usa el logger de mirada)
/// Menú: XR Collab → Build medición (Win64). CLI:
///   Unity.exe -batchmode -quit -projectPath . -executeMethod MeasurementBuild.BuildWin64 [-out "ruta\XR Collaboration Measurement.exe"]
/// Por defecto escribe en Desktop\dictuc\development\unity\builds\xr-collaboration-proto2-measurement.
/// </summary>
public static class MeasurementBuild
{
    private const string ExeName = "XR Collaboration Measurement.exe";

    [MenuItem("XR Collab/Configurar OpenXR para medición")]
    public static void ConfigureOpenXR()
    {
        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Standalone);
        OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        foreach (var feature in settings.GetFeatures())
        {
            string type = feature.GetType().Name;
            bool? want = type switch
            {
                "VivePassthrough" => false,
                "ViveCompositionLayerPassthrough" => false,
                nameof(PassthroughUnderlayFeature) => true,
                nameof(MetaPassthroughUnderlayFeature) => true,
                _ => (bool?)null,
            };
            if (want.HasValue && feature.enabled != want.Value)
            {
                feature.enabled = want.Value;
                EditorUtility.SetDirty(feature);
                Debug.Log($"[MeasurementBuild] {type} -> {(want.Value ? "ON" : "OFF")}");
            }
        }
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
    }

    // MetaPassthroughUnderlayFeature es nueva: el Editor la agrega apagada al descubrirla. Se enciende sola una vez
    // (es inerte con SteamVR/VIVE) para que el Play mode con Meta Quest por Link tenga passthrough sin pasos a mano.
    [InitializeOnLoadMethod]
    private static void EnsureMetaPassthroughFeature()
    {
        EditorApplication.delayCall += () =>
        {
            if (Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode) return;
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            if (settings == null) return;
            var meta = settings.GetFeature<MetaPassthroughUnderlayFeature>();
            if (meta == null)
            {
                FeatureHelpers.RefreshFeatures(BuildTargetGroup.Standalone);
                meta = settings.GetFeature<MetaPassthroughUnderlayFeature>();
            }
            if (meta == null || meta.enabled) return;
            meta.enabled = true;
            EditorUtility.SetDirty(meta);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log("[MeasurementBuild] MetaPassthroughUnderlayFeature -> ON (Standalone)");
        };
    }

    [MenuItem("XR Collab/Build medición (Win64)")]
    public static void BuildWin64()
    {
        ConfigureOpenXR();
        string outPath = ArgValue("-out") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..",
            "development", "unity", "builds", "xr-collaboration-proto2-measurement", ExeName));
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        Debug.Log($"[MeasurementBuild] Building {scenes.Length} scene(s) -> {outPath}");
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });
        Debug.Log($"[MeasurementBuild] Result: {report.summary.result}, {report.summary.totalErrors} error(s), {report.summary.totalSize / (1024 * 1024)} MB");
        if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    private static string ArgValue(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
