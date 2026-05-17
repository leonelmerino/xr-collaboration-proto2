using System.Collections;
using UnityEngine;
using UnityEngine.XR.Management;

/// <summary>
/// Inicializa XR explicitamente al entrar en Play Mode dentro del Editor.
/// Requiere que en Project Settings -> XR Plug-in Management -> Windows Standalone
/// este DESMARCADO "Initialize XR on Startup", para que el build no reclame el headset.
/// En builds standalone este script no hace nada.
/// </summary>
public class EditorXRBootstrap : MonoBehaviour
{
    [Tooltip("Si esta activo, el Editor inicializa XR al arrancar Play Mode.")]
    [SerializeField] private bool initXRInEditor = true;

    private IEnumerator Start()
    {
#if UNITY_EDITOR
        if (!initXRInEditor) yield break;

        var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (manager == null)
        {
            Debug.LogWarning("[EditorXRBootstrap] XRGeneralSettings.Manager no encontrado.");
            yield break;
        }

        if (manager.isInitializationComplete)
        {
            Debug.Log("[EditorXRBootstrap] XR ya estaba inicializado.");
            yield break;
        }

        Debug.Log("[EditorXRBootstrap] Inicializando XR loader...");
        yield return manager.InitializeLoader();

        if (manager.activeLoader != null)
        {
            manager.StartSubsystems();
            Debug.Log("[EditorXRBootstrap] XR loader OK, subsystems started.");
        }
        else
        {
            Debug.LogWarning("[EditorXRBootstrap] No se pudo inicializar ningun loader XR. Revisa Project Settings.");
        }
#else
        yield break;
#endif
    }

#if UNITY_EDITOR
    private void OnApplicationQuit()
    {
        var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (manager == null) return;
        if (!manager.isInitializationComplete) return;

        manager.StopSubsystems();
        manager.DeinitializeLoader();
        Debug.Log("[EditorXRBootstrap] XR detenido al salir de Play Mode.");
    }
#endif
}
