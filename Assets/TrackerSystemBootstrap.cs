using UnityEngine;

/// <summary>
/// Crea automáticamente el GameObject [TrackerSystem] con los componentes
/// TrackerBodyCalibration, TrackerPoseDriver y TrackerVisualizer si todavía
/// no existen en la escena. No requiere setup manual en el Editor.
///
/// Flujo:
///   1. Iniciar Play Mode → este método corre después de cargar la escena.
///   2. Presionar H para iniciar como Host (spawnea Avatar_Host).
///   3. Presionar C para calibrar (requiere los 3 trackers activos).
///   4. Las esferas calibradas aparecen: naranja=cintura, cian=pie izq, magenta=pie der.
/// </summary>
public static class TrackerSystemBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (Object.FindObjectOfType<TrackerBodyCalibration>() != null)
            return; // ya está en la escena (wired manualmente)

        var go = new GameObject("[TrackerSystem]");
        go.AddComponent<TrackerBodyCalibration>();
        go.AddComponent<TrackerPoseDriver>();
        go.AddComponent<TrackerVisualizer>();
        go.AddComponent<BodyTrackingSessionLogger>();

        Debug.Log("[TrackerSystemBootstrap] GameObject [TrackerSystem] creado con TrackerBodyCalibration + TrackerPoseDriver + TrackerVisualizer + BodyTrackingSessionLogger.");
    }
}
