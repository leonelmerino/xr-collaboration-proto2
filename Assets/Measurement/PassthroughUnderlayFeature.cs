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
/// Passthrough underlay para PC (VIVE Streaming por USB), sin las features de passthrough de VIVE.
///
/// Por qué no las de VIVE (com.htc.upm.vive.openxr 2.5.1, backend Mono de PC y Editor):
///   - "VIVE XR Passthrough" engancha xrWaitFrame vía ViveInterceptors con un tipo de delegate distinto al
///     del "VIVE XR Eye Tracker"; Marshal.GetDelegateForFunctionPointer lanza InvalidCastException dentro
///     del callback nativo y la app se congela en el primer frame (los loggers nunca arrancan).
///   - "VIVE XR Composition Layer (Passthrough)" crea el passthrough pero en Standalone nunca lo envía:
///     XR_HTC_passthrough_impls no implementa SubmitLayers/GetOriginEndFrameLayerList, así que el visor
///     se ve negro.
/// Esta feature hace lo mismo que tools\xr-passthrough-test (que sí muestra la sala): crea un passthrough
/// PLANAR con xrCreatePassthroughHTC y en cada xrEndFrame agrega una XrCompositionLayerPassthroughHTC
/// DEBAJO de la capa de proyección de Unity, marcando esa proyección con BLEND_TEXTURE_SOURCE_ALPHA para
/// que el fondo transparente de la cámara deje ver la sala.
///
/// Solo usa OpenXRHelper.xrGetInstanceProcAddrDelegate / xrEndFrameDelegate (los mismos tipos que las
/// features de VIVE en la cadena de hooks) y no toca xrWaitFrame, así que convive con el Eye Tracker.
/// No hace nada mientras <see cref="Active"/> esté apagado (modo VR). Lo enciende MeasurementMode.
/// </summary>
#if UNITY_EDITOR
[OpenXRFeature(UiName = "XR Collab: Passthrough underlay (PC)",
    Desc = "Passthrough HTC planar como capa inferior, enviado directamente en xrEndFrame. Compatible con VIVE XR Eye Tracker en Mono.",
    Company = "DICTUC",
    Version = "1.0.0",
    OpenxrExtensionStrings = "XR_HTC_passthrough",
    BuildTargetGroups = new[] { BuildTargetGroup.Standalone },
    FeatureId = FeatureId)]
#endif
public class PassthroughUnderlayFeature : OpenXRFeature
{
    public const string FeatureId = "cl.dictuc.xrcollab.passthrough-underlay";

    /// <summary>Encendido por MeasurementMode. Apagado = no se crea ni se envía nada.</summary>
    public static volatile bool Active;
    /// <summary>Estado legible para la pantalla del PC.</summary>
    public static string Status { get; private set; } = "sin sesión XR";
    public static bool IsShowing { get; private set; }
    /// <summary>Falla real del passthrough (extensión ausente, creación o envío fallidos), o null. Arrancar no cuenta como falla.</summary>
    public static string Problem { get; private set; }
    /// <summary>
    /// Último XrSessionState de la sesión (1 IDLE, 2 READY, 3 SYNCHRONIZED, 4 VISIBLE, 5 FOCUSED, 6 STOPPING…); 0 sin sesión.
    /// SYNCHRONIZED = la app corre pero el visor no la muestra (visor en la frente, menú del visor): lo usa el espejo del visor.
    /// </summary>
    public static int SessionState { get; private set; }

    // --- OpenXR (layouts del spec, 64 bits) ---
    private const int XR_TYPE_COMPOSITION_LAYER_PROJECTION = 35;
    private const int XR_TYPE_PASSTHROUGH_CREATE_INFO_HTC = 1000317001;
    private const int XR_TYPE_PASSTHROUGH_COLOR_HTC = 1000317002;
    private const int XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_HTC = 1000317004;
    private const int XR_PASSTHROUGH_FORM_PLANAR_HTC = 0;
    private const ulong XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT = 0x2;
    private const int LayerFlagsOffset = 16;   // XrCompositionLayerBaseHeader: type @0, next @8, layerFlags @16, space @24
    private const int LayerSpaceOffset = 24;

    [StructLayout(LayoutKind.Sequential)]
    private struct PassthroughCreateInfo { public int type; public IntPtr next; public int form; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PassthroughColor { public int type; public IntPtr next; public float alpha; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionLayerPassthrough
    {
        public int type; public IntPtr next; public ulong layerFlags; public ulong space; public ulong passthrough; public PassthroughColor color;
    }

    private delegate XrResult CreatePassthroughFn(ulong session, ref PassthroughCreateInfo createInfo, out ulong passthrough);
    private delegate XrResult DestroyPassthroughFn(ulong passthrough);

    private static ulong s_instance, s_session, s_passthrough;
    private static IntPtr s_getInstanceProcAddrPrev, s_endFramePrev;
    private static OpenXRHelper.xrGetInstanceProcAddrDelegate s_getInstanceProcAddrPrevDel;
    private static OpenXRHelper.xrEndFrameDelegate s_endFramePrevDel;
    private static readonly OpenXRHelper.xrGetInstanceProcAddrDelegate s_getInstanceProcAddrHook = GetInstanceProcAddrHook;
    private static readonly OpenXRHelper.xrEndFrameDelegate s_endFrameHook = EndFrameHook;
    private static CreatePassthroughFn s_create;
    private static DestroyPassthroughFn s_destroy;
    private static IntPtr s_layer = IntPtr.Zero;          // XrCompositionLayerPassthroughHTC, unmanaged
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
        if (!OpenXRRuntime.IsExtensionEnabled("XR_HTC_passthrough"))
        {
            Problem = Status = "XR_HTC_passthrough no disponible (¿VIVE Streaming conectado?)";
            Debug.LogWarning("[PassthroughUnderlay] " + Status);
        }
        return true;
    }

    protected override void OnSessionCreate(ulong xrSession) { s_session = xrSession; Status = "sesión XR creada"; }

    protected override void OnSessionStateChange(int oldState, int newState)
    {
        SessionState = newState;
        // FOCUSED = alguien se puso el visor: si el passthrough aún no existe, se intenta en el próximo cuadro.
        if (newState == XrSessionStateFocused && s_passthrough == 0) s_framesSinceAttempt = RetryEveryFrames;
    }

    protected override void OnSessionDestroy(ulong xrSession)
    {
        DestroyPassthrough();
        s_session = 0;
        SessionState = 0;
        Status = "sesión XR cerrada";
    }

    protected override void OnInstanceDestroy(ulong xrInstance)
    {
        s_instance = 0;
        s_create = null;
        s_destroy = null;
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

    private static bool ResolveFunctions()
    {
        if (s_create != null && s_destroy != null) return true;
        if (s_instance == 0 || s_getInstanceProcAddrPrevDel == null) return false;
        if (s_getInstanceProcAddrPrevDel(s_instance, "xrCreatePassthroughHTC", out IntPtr create) != XrResult.XR_SUCCESS || create == IntPtr.Zero) return false;
        if (s_getInstanceProcAddrPrevDel(s_instance, "xrDestroyPassthroughHTC", out IntPtr destroy) != XrResult.XR_SUCCESS || destroy == IntPtr.Zero) return false;
        s_create = Marshal.GetDelegateForFunctionPointer<CreatePassthroughFn>(create);
        s_destroy = Marshal.GetDelegateForFunctionPointer<DestroyPassthroughFn>(destroy);
        return true;
    }

    // Sin tope de intentos: si la app arranca con el visor en reposo (autostart, Start-LabSession), VIVE Streaming
    // todavía no expone el passthrough y xrCreatePassthroughHTC devuelve XR_ERROR_FUNCTION_UNSUPPORTED hasta que
    // alguien se pone el visor. Con el tope anterior (30 intentos) el passthrough podía no aparecer nunca.
    private static void TryCreatePassthrough()
    {
        if (s_passthrough != 0 || s_session == 0) return;
        if (++s_framesSinceAttempt < RetryEveryFrames && s_createAttempts > 0) return;   // ~1 s a 90 fps, ~4 s a 20 fps
        s_framesSinceAttempt = 0;
        s_createAttempts++;
        if (!ResolveFunctions()) { Problem = Status = $"xrCreatePassthroughHTC no disponible (intento {s_createAttempts})"; return; }
        var info = new PassthroughCreateInfo { type = XR_TYPE_PASSTHROUGH_CREATE_INFO_HTC, next = IntPtr.Zero, form = XR_PASSTHROUGH_FORM_PLANAR_HTC };
        XrResult res = s_create(s_session, ref info, out ulong handle);
        if (res == XrResult.XR_SUCCESS && handle != 0)
        {
            s_passthrough = handle;
            if (s_layer == IntPtr.Zero) s_layer = Marshal.AllocHGlobal(Marshal.SizeOf<CompositionLayerPassthrough>());
            if (s_layers == IntPtr.Zero) s_layers = Marshal.AllocHGlobal(IntPtr.Size * MaxLayers);
            Status = "ON";
            Problem = null;
            Debug.Log($"[PassthroughUnderlay] xrCreatePassthroughHTC OK (handle 0x{handle:X}).");
        }
        else
        {
            Problem = Status = $"xrCreatePassthroughHTC falló: {res} (intento {s_createAttempts})";
            if (s_createAttempts <= 3 || s_createAttempts % 30 == 0) Debug.LogWarning("[PassthroughUnderlay] " + Status);
        }
    }

    private static void DestroyPassthrough()
    {
        if (s_passthrough != 0 && s_destroy != null) s_destroy(s_passthrough);
        s_passthrough = 0;
        s_createAttempts = 0;
        IsShowing = false;
    }

    [MonoPInvokeCallback(typeof(OpenXRHelper.xrEndFrameDelegate))]
    private static XrResult EndFrameHook(XrSession session, ref XrFrameEndInfo frameEndInfo)
    {
        if (s_endFramePrevDel == null)
            s_endFramePrevDel = Marshal.GetDelegateForFunctionPointer<OpenXRHelper.xrEndFrameDelegate>(s_endFramePrev);

        if (!Active)
        {
            IsShowing = false;
            return s_endFramePrevDel(session, ref frameEndInfo);
        }

        TryCreatePassthrough();
        int count = (int)frameEndInfo.layerCount;
        if (s_passthrough == 0 || frameEndInfo.layers == IntPtr.Zero || count <= 0 || count >= MaxLayers)
            return s_endFramePrevDel(session, ref frameEndInfo);

        // Espacio de la capa de proyección de Unity (el mismo espacio de referencia de la app).
        ulong space = 0;
        for (int i = 0; i < count; i++)
        {
            IntPtr layer = Marshal.ReadIntPtr(frameEndInfo.layers, i * IntPtr.Size);
            if (layer == IntPtr.Zero || Marshal.ReadInt32(layer, 0) != XR_TYPE_COMPOSITION_LAYER_PROJECTION) continue;
            ulong flags = (ulong)Marshal.ReadInt64(layer, LayerFlagsOffset);
            Marshal.WriteInt64(layer, LayerFlagsOffset, (long)(flags | XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT));
            if (space == 0) space = (ulong)Marshal.ReadInt64(layer, LayerSpaceOffset);
        }
        if (space == 0) return s_endFramePrevDel(session, ref frameEndInfo);   // sin proyección (p. ej. sesión no visible)

        Marshal.StructureToPtr(new CompositionLayerPassthrough
        {
            type = XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_HTC,
            next = IntPtr.Zero,
            layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT,
            space = space,
            passthrough = s_passthrough,
            color = new PassthroughColor { type = XR_TYPE_PASSTHROUGH_COLOR_HTC, next = IntPtr.Zero, alpha = 1f },
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
            Debug.Log($"[PassthroughUnderlay] primer frame con passthrough: {count + 1} capas, xrEndFrame={res}");
        }
        else if (!IsShowing) Problem = Status = $"xrEndFrame con passthrough devolvió {res}";
        return res;
    }
}
