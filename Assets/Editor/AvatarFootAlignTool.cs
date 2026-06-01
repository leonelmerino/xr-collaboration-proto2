#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool: alinea los sub-meshes humanoides para que la planta del pie (el punto MAS BAJO
/// de la mesh visible) quede en y=0 del sub-mesh local.
///
/// Por que no usa Animator.GetBoneTransform: en Prefab Mode el Animator no esta garantizado
/// inicializado en bind pose, asi que el bone "LeftFoot" puede devolver una posicion que NO
/// representa donde estan visualmente los pies en runtime. En cambio,
/// SkinnedMeshRenderer.localBounds.min.y nos da el punto mas bajo de la mesh real.
///
/// Como usar:
/// 1. Doble click en AvatarHumanoid.prefab para abrirlo en Prefab Mode.
/// 2. Click en el root (AvatarHumanoid) en la Hierarchy.
/// 3. Menu: XR Collab > Avatars > Auto-Align Feet To Floor (Mesh Bounds).
/// 4. El tool mide bounds.min.y de cada sub-mesh y aplica offset al localPosition.y.
/// 5. Ctrl+S para guardar el prefab.
///
/// Si querés REVERTIR: XR Collab > Avatars > Reset Sub-Mesh Y Offsets.
/// </summary>
public static class AvatarFootAlignTool
{
    [MenuItem("XR Collab/Avatars/Auto-Align Feet To Floor (Mesh Bounds)")]
    public static void AlignFeet()
    {
        var sel = Selection.activeGameObject;
        if (sel == null)
        {
            EditorUtility.DisplayDialog("Avatar Foot Align",
                "Seleccioná el root del AvatarHumanoid (en Prefab Mode).",
                "OK");
            return;
        }

        GameObject root = sel.transform.root.gameObject;

        var visuals = root.transform.Find("AvatarVisuals");
        if (visuals == null)
        {
            EditorUtility.DisplayDialog("Avatar Foot Align",
                "No encontré 'AvatarVisuals' en '" + root.name + "'.",
                "OK");
            return;
        }

        int aligned = 0;
        int skipped = 0;

        foreach (Transform sub in visuals)
        {
            // Buscar TODOS los SkinnedMeshRenderer dentro del sub-mesh (puede haber body+hair+clothes).
            var renderers = sub.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            if (renderers.Length == 0)
            {
                Debug.LogWarning($"[AvatarFootAlign] '{sub.name}': no SkinnedMeshRenderer found, skipping.");
                skipped++;
                continue;
            }

            // Obtener el punto MAS BAJO de toda la mesh visible, en el espacio LOCAL del sub-mesh.
            float minLocalY = float.PositiveInfinity;
            string lowestRendererName = "?";
            foreach (var r in renderers)
            {
                // r.bounds esta en WORLD space y refleja la mesh actual (incluyendo skinning).
                Vector3 worldMin = r.bounds.min;
                Vector3 localMin = sub.InverseTransformPoint(worldMin);
                if (localMin.y < minLocalY)
                {
                    minLocalY = localMin.y;
                    lowestRendererName = r.name;
                }
            }

            if (float.IsPositiveInfinity(minLocalY))
            {
                Debug.LogWarning($"[AvatarFootAlign] '{sub.name}': no se pudo medir mesh bounds. Skipping.");
                skipped++;
                continue;
            }

            // Si ya esta cerca de y=0 (tolerancia 1cm), no hacemos nada.
            if (Mathf.Abs(minLocalY) < 0.01f)
            {
                Debug.Log($"[AvatarFootAlign] '{sub.name}': mesh bottom already at y≈0 (minLocalY={minLocalY:F4}). Skipped.");
                skipped++;
                continue;
            }

            // Aplicar offset: subir el sub-mesh para que la mesh-bottom quede en y=0.
            // Si minLocalY=-0.85 (mesh esta 85cm bajo el origen), entonces lp.y -= -0.85 → lp.y += 0.85.
            Undo.RecordObject(sub, "Auto-Align Avatar Foot (Mesh)");
            var lp = sub.localPosition;
            float oldY = lp.y;
            lp.y -= minLocalY;
            sub.localPosition = lp;
            EditorUtility.SetDirty(sub);

            Debug.Log($"[AvatarFootAlign] '{sub.name}': mesh bottom was at localY={minLocalY:F4} " +
                      $"(via '{lowestRendererName}'). lp.y: {oldY:F4} → {lp.y:F4} (delta {lp.y - oldY:+0.0000;-0.0000}).");
            aligned++;
        }

        if (UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null)
        {
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage().scene);
        }

        EditorUtility.DisplayDialog("Avatar Foot Align",
            $"{aligned} sub-mesh(es) alineadas. {skipped} skipped (ver Console).",
            "OK");
    }

    [MenuItem("XR Collab/Avatars/Reset Sub-Mesh Y Offsets")]
    public static void ResetOffsets()
    {
        var sel = Selection.activeGameObject;
        if (sel == null)
        {
            EditorUtility.DisplayDialog("Reset", "Seleccioná el AvatarHumanoid root.", "OK");
            return;
        }

        var visuals = sel.transform.root.Find("AvatarVisuals");
        if (visuals == null) return;

        int reset = 0;
        foreach (Transform sub in visuals)
        {
            Undo.RecordObject(sub, "Reset Avatar Sub-Mesh Y");
            var lp = sub.localPosition;
            float old = lp.y;
            lp.y = 0;
            sub.localPosition = lp;
            EditorUtility.SetDirty(sub);
            if (Mathf.Abs(old) > 0.0001f)
            {
                Debug.Log($"[AvatarFootAlign] Reset '{sub.name}': lp.y {old:F4} → 0.");
                reset++;
            }
        }

        if (UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null)
        {
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage().scene);
        }

        EditorUtility.DisplayDialog("Reset", $"{reset} sub-mesh(es) reseteadas a Y=0.", "OK");
    }
}
#endif
