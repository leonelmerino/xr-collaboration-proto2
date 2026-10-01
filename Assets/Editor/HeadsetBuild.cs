using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

/// <summary>
/// Build del modo medición que corre DENTRO del visor (VIVE Focus Vision, Android), sin PC ni VIVE Streaming.
/// Mismo código que el build de PC: passthrough (PassthroughUnderlayFeature), mirada, cabeza, manos y eventos se
/// registran en el visor (Application.persistentDataPath = /sdcard/Android/data/&lt;paquete&gt;/files/). No graba video
/// ni muestra el espejo (son solo de Windows). Se une solo, si lo encuentra, a un host en la red (NetworkLauncher).
///
/// OpenXR para Android: solo las features de <see cref="AndroidFeatures"/> («VIVE XR Support», el passthrough
/// propio, mirada y manos); las de passthrough de VIVE quedan apagadas (una sola ruta de passthrough en los dos
/// builds).
/// Menú: XR Collab → Build visor (Android APK). CLI:
///   Unity.exe -batchmode -quit -projectPath . -buildTarget Android -executeMethod HeadsetBuild.BuildAndroid [-out "ruta\x.apk"]
/// Por defecto escribe Desktop\dictuc\development\unity\builds\xr-collaboration-proto2-headset\XRCollabMeasurement.apk.
/// Instalar en los visores: tools\lab-remote\Deploy-LabHeadsetApp.ps1.
/// </summary>
public static class HeadsetBuild
{
    public const string PackageId = "cl.dictuc.xrcollab.measurement";
    private const string ApkName = "XRCollabMeasurement.apk";

    // Lista cerrada para el visor (todo lo demás apagado): soporte VIVE, passthrough propio, mirada, manos y
    // perfiles básicos. Sin trackers ni controles hoy; «VIVE XR - Interaction Group» sin extensiones no valida.
    private static readonly string[] AndroidFeatures =
    {
        "VIVEFocus3Feature",              // VIVE XR Support (obligatoria en Android)
        nameof(PassthroughUnderlayFeature),
        "ViveEyeTracker",                 // mirada (ViveEyeTrackingProvider)
        "HandTracking",                   // manos (XR Hands)
        "HandCommonPosesInteraction",
        "VIVEFocus3Profile",
        "KHRSimpleControllerProfile",
    };

    [MenuItem("XR Collab/Configurar visor (Android)")]
    public static void ConfigureAndroid()
    {
        ConfigureLoader();
        ConfigureFeatures();
        ConfigurePlayer();
        AssetDatabase.SaveAssets();
    }

    [MenuItem("XR Collab/Build visor (Android APK)")]
    public static void BuildAndroid()
    {
        ConfigureAndroid();
        string outPath = ArgValue("-out") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..",
            "development", "unity", "builds", "xr-collaboration-proto2-headset", ApkName));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        Debug.Log($"[HeadsetBuild] Building {scenes.Length} scene(s) -> {outPath}");
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        });
        Debug.Log($"[HeadsetBuild] Result: {report.summary.result}, {report.summary.totalErrors} error(s), {report.summary.totalSize / (1024 * 1024)} MB");
        if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    private static void ConfigureLoader()
    {
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            throw new InvalidOperationException("No hay XRGeneralSettingsPerBuildTarget (XR Plug-in Management)");
        if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
        XRGeneralSettings general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        general.InitManagerOnStart = true;
        bool assigned = XRPackageMetadataStore.AssignLoader(general.Manager, typeof(OpenXRLoader).FullName, BuildTargetGroup.Android);
        Debug.Log($"[HeadsetBuild] OpenXR loader para Android: {(assigned ? "asignado" : "ya estaba")}");
        EditorUtility.SetDirty(general);
        EditorUtility.SetDirty(perTarget);
    }

    private static void ConfigureFeatures()
    {
        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
        OpenXRSettings android = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        foreach (var feature in android.GetFeatures())
        {
            string type = feature.GetType().Name;
            bool want = AndroidFeatures.Contains(type);
            if (feature.enabled != want)
            {
                feature.enabled = want;
                EditorUtility.SetDirty(feature);
            }
            if (want) Debug.Log($"[HeadsetBuild] Android OpenXR: {type} ON");
        }
        EditorUtility.SetDirty(android);
    }

    private static void ConfigurePlayer()
    {
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, PackageId);
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        if (PlayerSettings.Android.minSdkVersion < AndroidSdkVersions.AndroidApiLevel29)
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.forceInternetPermission = true;   // red con el host (opcional)
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;
        Debug.Log($"[HeadsetBuild] Player: {PackageId}, IL2CPP ARM64, API mín. {PlayerSettings.Android.minSdkVersion}, GLES3");
    }

    private static string ArgValue(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
