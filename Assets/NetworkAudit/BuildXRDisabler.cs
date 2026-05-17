using UnityEngine;
#if !UNITY_EDITOR
using UnityEngine.XR.Management;
using UnityEngine.InputSystem.XR;
#endif

public class BuildXRDisabler : MonoBehaviour
{
    [Tooltip("Si esta activo, la build deshabilita XR al iniciar. En el Editor no tiene efecto.")]
    [SerializeField] private bool disableXROnBuild = true;

    [Tooltip("Tambien deshabilita los TrackedPoseDriver de la escena para que la camara no sea movida por inputs XR fantasma.")]
    [SerializeField] private bool disableTrackedPoseDrivers = true;

    private void Awake()
    {
#if !UNITY_EDITOR
        if (!disableXROnBuild) return;

        var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;

        if (manager != null && manager.isInitializationComplete)
        {
            manager.StopSubsystems();
            manager.DeinitializeLoader();
            Debug.Log("[BuildXRDisabler] XR subsystems stopped and loader deinitialized.");
        }
        else
        {
            Debug.LogWarning("[BuildXRDisabler] XRGeneralSettings.Manager no inicializado; nada que detener.");
        }

        if (disableTrackedPoseDrivers)
        {
            int count = 0;
            var drivers = FindObjectsOfType<TrackedPoseDriver>(true);
            foreach (var d in drivers)
            {
                d.enabled = false;
                count++;
            }
            Debug.Log($"[BuildXRDisabler] TrackedPoseDriver deshabilitados: {count}");
        }
#endif
    }
}
