using UnityEditor;

/// <summary>
/// Alterna el modo medición (passthrough) para el Play Mode del Editor.
/// En builds se controla con los argumentos -vr / -measurement.
/// </summary>
public static class MeasurementModeMenu
{
    private const string MenuPath = "XR Collab/Modo medición (passthrough)";

    [MenuItem(MenuPath)]
    private static void Toggle()
    {
        bool enabled = !EditorPrefs.GetBool(MeasurementMode.EditorPrefKey, true);
        EditorPrefs.SetBool(MeasurementMode.EditorPrefKey, enabled);
    }

    [MenuItem(MenuPath, true)]
    private static bool ToggleValidate()
    {
        Menu.SetChecked(MenuPath, EditorPrefs.GetBool(MeasurementMode.EditorPrefKey, true));
        return true;
    }
}
