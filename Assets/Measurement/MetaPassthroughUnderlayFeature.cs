using System;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using VIVE.OpenXR;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

/// <summary>
/// Passthrough underlay para Meta Quest 3 desde el PC (Meta Horizon Link por cable o Air Link), con XR_FB_passthrough.
/// Es la pareja de <see cref="PassthroughUnderlayFeature"/> (VIVE, XR_HTC_passthrough) y funciona igual: crea el
/// passthrough y una capa de reconstrucción, y en cada xrEndFrame agrega una XrCompositionLayerPassthroughFB DEBAJO de
/// la capa de proyección de Unity, marcando esa proyección con BLEND_TEXTURE_SOURCE_ALPHA para que el fondo transparente
/// de la cámara deje ver la sala.
///
/// No reemplaza nada del camino VIVE: con el runtime de SteamVR/VIVE la extensión no existe, <see cref="Supported"/>
/// queda en false y el hook solo pasa el frame tal cual. Con el runtime de Meta pasa lo contrario (la feature HTC no
/// encuentra su extensión). MeasurementMode enciende las dos y muestra el estado de la que corresponde.
///
/// Requisitos del lado Meta (PC): app Meta Horizon Link → Configuración → Beta → "Funciones de runtime para
/// desarrolladores" y "Passthrough en Meta Quest Link" activados; Unity abierto con el runtime de Meta
/// (tools\launch\open-unity-quest.cmd fija XR_RUNTIME_JSON solo para ese proceso, sin cambiar el runtime del sistema).
/// </summary>
#if UNITY_EDITOR
[OpenXRFeature(UiName = "XR Collab: Meta passthrough underlay",
    Desc = "Passthrough XR_FB_passthrough (Meta Quest por Link) como capa inferior, enviado directamente en xrEndFrame. Inerte con otros runtimes.",
    Company = "DICTUC",
    Version = "1.0.0",
    OpenxrExtensionStrings = "XR_FB_passthrough",
    BuildTargetGroups = new[] { BuildTargetGroup.Standalone },
    FeatureId = FeatureId)]
#endif
public class MetaPassthroughUnderlayFeature : OpenXRFeature
{
    public const string FeatureId = "cl.dictuc.xrcollab.meta-passthrough-underlay";

    /// <summary>Encendido por MeasurementMode. Apagado = no se crea ni se envía nada.</summary>
    public static volatile bool Active;
    /// <summary>true cuando el runtime activo expone XR_FB_passthrough (Meta Horizon Link con passthrough habilitado).</summary>
    public static bool Supported { get; private set; }
    /// <summary>Estado legible para la pantalla del PC.</summary>
    public static string Status { get; private set; } = "sin sesión XR";
    public static bool IsShowing { get; private set; }
    /// <summary>Falla real del passthrough (creación o envío fallidos), o null.</summary>
    public static string Problem { get; private set; }
    /// <summary>Último XrSessionState (1 IDLE … 5 FOCUSED); 0 sin sesión.</summary>
    public static int SessionState { get; private set; }

    /// <summary>
    /// Ajuste de imagen del passthrough (XrPassthroughBrightnessContrastSaturationFB): brillo -100..100 (0 = sin cambio),
    /// contraste y saturación 1 = sin cambio. Lo fija MeasurementMode desde -pt-brightness / -pt-contrast / -pt-saturation
    /// o EditorPrefs; cambiarlo en vivo lo aplica en el siguiente frame.
    /// </summary>
    public static float Brightness { get => s_brightness; set { s_brightness = Mathf.Clamp(value, -100f, 100f); s_styleDirty = true; } }
    public static float Contrast { get => s_contrast; set { s_contrast = Mathf.Max(0f, value); s_styleDirty = true; } }
    public static float Saturation { get => s_saturation; set { s_saturation = Mathf.Max(0f, value); s_styleDirty = true; } }
    private static float s_brightness, s_contrast = 1f, s_saturation = 1f;
    private static volatile bool s_styleDirty = true;

    // --- OpenXR (layouts del spec, 64 bits) ---
    private const int XR_TYPE_COMPOSITION_LAYER_PROJECTION = 35;
    private const int XR_TYPE_PASSTHROUGH_CREATE_INFO_FB = 1000118001;
    private const int XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB = 1000118002;
    private const int XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB = 1000118003;
    private const int XR_TYPE_PASSTHROUGH_STYLE_FB = 1000118020;
    private const int XR_TYPE_PASSTHROUGH_BRIGHTNESS_CONTRAST_SATURATION_FB = 1000118023;
    private const ulong XR_PASSTHROUGH_IS_RUNNING_AT_CREATION_BIT_FB = 0x1;
    private const int XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB = 0;
    private const ulong XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT = 0x2;
    private const int LayerFlagsOffset = 16;   // XrCompositionLayerBaseHeader: type @0, next @8, layerFlags @16, space @24
    private const int LayerSpaceOffset = 24;

    [StructLayout(LayoutKind.Sequential)]
    private struct PassthroughCreateInfo { public int type; public IntPtr next; public ulong flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PassthroughLayerCreateInfo { public int type; public IntPtr next; public ulong passthrough; public ulong flags; public int purpose; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionLayerPassthrough { public int type; public IntPtr next; public ulong flags; public ulong space; public ulong layerHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PassthroughStyle { public int type; public IntPtr next; public float textureOpacityFactor; public float edgeR, edgeG, edgeB, edgeA; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PassthroughBrightnessContrastSaturation { public int type; public IntPtr next; public float brightness, contrast, saturation; }

    private delegate XrResult SetStyleFn(ulong layer, ref PassthroughStyle style);
    private delegate XrResult CreatePassthroughFn(ulong session, ref PassthroughCreateInfo createInfo, out ulong passthrough);
    private delegate XrResult CreatePassthroughLayerFn(ulong session, ref PassthroughLayerCreateInfo createInfo, out ulong layer);
    private delegate XrResult DestroyHandleFn(ulong handle);

    private static ulong s_instance, s_session, s_passthrough, s_passthroughLayer;
    private static IntPtr s_getInstanceProcAddrPrev, s_endFramePrev;
    private static OpenXRHelper.xrGetInstanceProcAddrDelegate s_getInstanceProcAddrPrevDel;
    private static OpenXRHelper.xrEndFrameDelegate s_endFramePrevDel;
    private static readonly OpenXRHelper.xrGetInstanceProcAddrDelegate s_getInstanceProcAddrHook = GetInstanceProcAddrHook;
    private static readonly OpenXRHelper.xrEndFrameDelegate s_endFrameHook = EndFrameHook;
    private static CreatePassthroughFn s_create;
    private static CreatePassthroughLayerFn s_createLayer;
    private static DestroyHandleFn s_destroy, s_destroyLayer;
    private static SetStyleFn s_setStyle;
    private static IntPtr s_bcs = IntPtr.Zero;            // XrPassthroughBrightnessContrastSaturationFB, unmanaged
    private static IntPtr s_layer = IntPtr.Zero;          // XrCompositionLayerPassthroughFB, unmanaged
    private static IntPtr s_layers = IntPtr.Zero;         // layer pointer array we submit instead of Unity's
    private const int MaxLayers = 16;
    private const int RetryEveryFrames = 90;
    private const int XrSessionStateFocused = 5;
    private static int s_createAttempts;
    private static int s_framesSinceAttempt;
    private static bool s_loggedFirstFrame;

    protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
    {
        s_getInstanceProcAddrPrev = func;
        s_getInstanceProcAddrPrevDel = null;
        return Marshal.GetFunctionPointerForDelegate(s_getInstanceProcAddrHook);
    }

    protected override bool OnInstanceCreate(ulong xrInstance)
    {
        s_instance = xrInstance;
        Supported = OpenXRRuntime.IsExtensionEnabled("XR_FB_passthrough");
        Status = Supported ? "XR_FB_passthrough disponible" : "XR_FB_passthrough no disponible (runtime no es Meta Link)";
        Debug.Log($"[MetaPassthroughUnderlay] runtime {OpenXRRuntime.name} {OpenXRRuntime.version}: {Status}");
        return true;
    }

    protected override void OnSessionCreate(ulong xrSession) { s_session = xrSession; if (Supported) Status = "sesión XR creada"; }

    protected override void OnSessionStateChange(int oldState, int newState)
    {
        SessionState = newState;
        if (newState == XrSessionStateFocused && s_passthroughLayer == 0) s_framesSinceAttempt = RetryEveryFrames;
    }

    protected override void OnSessionDestroy(ulong xrSession)
    {
        DestroyPassthrough();
        s_session = 0;
        SessionState = 0;
        if (Supported) Status = "sesión XR cerrada";
    }

    protected override void OnInstanceDestroy(ulong xrInstance)
    {
        s_instance = 0;
        Supported = false;
        s_create = null; s_createLayer = null; s_destroy = null; s_destroyLayer = null; s_setStyle = null;
        if (s_bcs != IntPtr.Zero) { Marshal.FreeHGlobal(s_bcs); s_bcs = IntPtr.Zero; }
        if (s_layer != IntPtr.Zero) { Marshal.FreeHGlobal(s_layer); s_layer = IntPtr.Zero; }
        if (s_layers != IntPtr.Zero) { Marshal.FreeHGlobal(s_layers); s_layers = IntPtr.Zero; }
    }

    [MonoPInvokeCallback(typeof(OpenXRHelper.xrGetInstanceProcAddrDelegate))]
    private static XrResult GetInstanceProcAddrHook(XrInstance instance, string name, out IntPtr function)
    {
        if (s_getInstanceProcAddrPrevDel == null)
            s_getInstanceProcAddrPrevDel = Marshal.GetDelegateForFunctionPointer<OpenXRHelper.xrGetInstanceProcAddrDelegate>(s_getInstanceProcAddrPrev);
        XrResult result = s_getInstanceProcAddrPrevDel(instance, name, out function);
        if (name == "xrEndFrame" && result == XrResult.XR_SUCCESS && function != IntPtr.Zero)
        {
            s_endFramePrev = function;
            s_endFramePrevDel = null;
            function = Marshal.GetFunctionPointerForDelegate(s_endFrameHook);
        }
        return result;
    }

    private static T Resolve<T>(string name) where T : Delegate
    {
        if (s_getInstanceProcAddrPrevDel(s_instance, name, out IntPtr fn) != XrResult.XR_SUCCESS || fn == IntPtr.Zero) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static bool ResolveFunctions()
    {
        if (s_create != null && s_createLayer != null && s_destroy != null && s_destroyLayer != null) return true;
        if (s_instance == 0 || s_getInstanceProcAddrPrevDel == null) return false;
        s_create = Resolve<CreatePassthroughFn>("xrCreatePassthroughFB");
        s_createLayer = Resolve<CreatePassthroughLayerFn>("xrCreatePassthroughLayerFB");
        s_destroy = Resolve<DestroyHandleFn>("xrDestroyPassthroughFB");
        s_destroyLayer = Resolve<DestroyHandleFn>("xrDestroyPassthroughLayerFB");
        s_setStyle = Resolve<SetStyleFn>("xrPassthroughLayerSetStyleFB");   // opcional: sin ella no hay ajuste de brillo
        return s_create != null && s_createLayer != null && s_destroy != null && s_destroyLayer != null;
    }

    // Sin tope de intentos, como la feature HTC: Link puede no exponer la cámara hasta que alguien se pone el visor.
    private static void TryCreatePassthrough()
    {
        if (s_passthroughLayer != 0 || s_session == 0) return;
        if (++s_framesSinceAttempt < RetryEveryFrames && s_createAttempts > 0) return;
        s_framesSinceAttempt = 0;
        s_createAttempts++;
        if (!ResolveFunctions()) { Problem = Status = $"funciones XR_FB_passthrough no disponibles (intento {s_createAttempts})"; return; }

        if (s_passthrough == 0)
        {
            var info = new PassthroughCreateInfo { type = XR_TYPE_PASSTHROUGH_CREATE_INFO_FB, next = IntPtr.Zero, flags = XR_PASSTHROUGH_IS_RUNNING_AT_CREATION_BIT_FB };
            XrResult res = s_create(s_session, ref info, out ulong handle);
            if (res != XrResult.XR_SUCCESS || handle == 0)
            {
                Problem = Status = $"xrCreatePassthroughFB falló: {res} (intento {s_createAttempts}; ¿'Passthrough en Meta Quest Link' activado?)";
                if (s_createAttempts <= 3 || s_createAttempts % 30 == 0) Debug.LogWarning("[MetaPassthroughUnderlay] " + Status);
                return;
            }
            s_passthrough = handle;
        }

        var layerInfo = new PassthroughLayerCreateInfo
        {
            type = XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB, next = IntPtr.Zero, passthrough = s_passthrough,
            flags = XR_PASSTHROUGH_IS_RUNNING_AT_CREATION_BIT_FB, purpose = XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB,
        };
        XrResult lres = s_createLayer(s_session, ref layerInfo, out ulong layer);
        if (lres != XrResult.XR_SUCCESS || layer == 0)
        {
            Problem = Status = $"xrCreatePassthroughLayerFB falló: {lres} (intento {s_createAttempts})";
            if (s_createAttempts <= 3 || s_createAttempts % 30 == 0) Debug.LogWarning("[MetaPassthroughUnderlay] " + Status);
            return;
        }
        s_passthroughLayer = layer;
        s_styleDirty = true;
        if (s_layer == IntPtr.Zero) s_layer = Marshal.AllocHGlobal(Marshal.SizeOf<CompositionLayerPassthrough>());
        if (s_layers == IntPtr.Zero) s_layers = Marshal.AllocHGlobal(IntPtr.Size * MaxLayers);
        Status = "ON";
        Problem = null;
        Debug.Log($"[MetaPassthroughUnderlay] passthrough FB OK (passthrough 0x{s_passthrough:X}, layer 0x{layer:X}).");
    }

    // Brillo/contraste/saturación del passthrough. Si el runtime no lo soporta solo queda registrado en Status.
    private static void ApplyStyle()
    {
        s_styleDirty = false;
        if (s_setStyle == null || s_passthroughLayer == 0) return;
        if (s_bcs == IntPtr.Zero) s_bcs = Marshal.AllocHGlobal(Marshal.SizeOf<PassthroughBrightnessContrastSaturation>());
        Marshal.StructureToPtr(new PassthroughBrightnessContrastSaturation
        {
            type = XR_TYPE_PASSTHROUGH_BRIGHTNESS_CONTRAST_SATURATION_FB, next = IntPtr.Zero,
            brightness = s_brightness, contrast = s_contrast, saturation = s_saturation,
        }, s_bcs, false);
        var style = new PassthroughStyle { type = XR_TYPE_PASSTHROUGH_STYLE_FB, next = s_bcs, textureOpacityFactor = 1f };
        XrResult res = s_setStyle(s_passthroughLayer, ref style);
        Debug.Log($"[MetaPassthroughUnderlay] estilo brillo {s_brightness:0} contraste {s_contrast:0.00} saturación {s_saturation:0.00}: {res}");
        if (res != XrResult.XR_SUCCESS) Problem = $"xrPassthroughLayerSetStyleFB devolvió {res}";
    }

    private static void DestroyPassthrough()
    {
        if (s_passthroughLayer != 0 && s_destroyLayer != null) s_destroyLayer(s_passthroughLayer);
        if (s_passthrough != 0 && s_destroy != null) s_destroy(s_passthrough);
        s_passthroughLayer = 0;
        s_passthrough = 0;
        s_createAttempts = 0;
        IsShowing = false;
    }

    [MonoPInvokeCallback(typeof(OpenXRHelper.xrEndFrameDelegate))]
    private static XrResult EndFrameHook(XrSession session, ref XrFrameEndInfo frameEndInfo)
    {
        if (s_endFramePrevDel == null)
            s_endFramePrevDel = Marshal.GetDelegateForFunctionPointer<OpenXRHelper.xrEndFrameDelegate>(s_endFramePrev);

        if (!Active || !Supported)
        {
            IsShowing = false;
            return s_endFramePrevDel(session, ref frameEndInfo);
        }

        TryCreatePassthrough();
        if (s_styleDirty && s_passthroughLayer != 0) ApplyStyle();
        int count = (int)frameEndInfo.layerCount;
        if (s_passthroughLayer == 0 || frameEndInfo.layers == IntPtr.Zero || count <= 0 || count >= MaxLayers)
            return s_endFramePrevDel(session, ref frameEndInfo);

        bool hasProjection = false;
        for (int i = 0; i < count; i++)
        {
            IntPtr layer = Marshal.ReadIntPtr(frameEndInfo.layers, i * IntPtr.Size);
            if (layer == IntPtr.Zero || Marshal.ReadInt32(layer, 0) != XR_TYPE_COMPOSITION_LAYER_PROJECTION) continue;
            ulong flags = (ulong)Marshal.ReadInt64(layer, LayerFlagsOffset);
            Marshal.WriteInt64(layer, LayerFlagsOffset, (long)(flags | XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT));
            hasProjection |= Marshal.ReadInt64(layer, LayerSpaceOffset) != 0;
        }
        if (!hasProjection) return s_endFramePrevDel(session, ref frameEndInfo);   // sin proyección (p. ej. sesión no visible)

        // La capa FB no usa espacio (XR_NULL_HANDLE): el runtime la pone a pantalla completa.
        Marshal.StructureToPtr(new CompositionLayerPassthrough
        {
            type = XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB,
            next = IntPtr.Zero,
            flags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT,
            space = 0,
            layerHandle = s_passthroughLayer,
        }, s_layer, false);

        // [passthrough, ...capas de Unity]: el passthrough queda debajo de todo.
        Marshal.WriteIntPtr(s_layers, 0, s_layer);
        for (int i = 0; i < count; i++)
            Marshal.WriteIntPtr(s_layers, (i + 1) * IntPtr.Size, Marshal.ReadIntPtr(frameEndInfo.layers, i * IntPtr.Size));

        IntPtr originalLayers = frameEndInfo.layers;
        uint originalCount = frameEndInfo.layerCount;
        frameEndInfo.layers = s_layers;
        frameEndInfo.layerCount = (uint)(count + 1);
        XrResult res = s_endFramePrevDel(session, ref frameEndInfo);
        frameEndInfo.layers = originalLayers;
        frameEndInfo.layerCount = originalCount;

        IsShowing = res == XrResult.XR_SUCCESS;
        if (!s_loggedFirstFrame)
        {
            s_loggedFirstFrame = true;
            Debug.Log($"[MetaPassthroughUnderlay] primer frame con passthrough: {count + 1} capas, xrEndFrame={res}");
        }
        else if (!IsShowing) Problem = Status = $"xrEndFrame con passthrough devolvió {res}";
        return res;
    }
}
