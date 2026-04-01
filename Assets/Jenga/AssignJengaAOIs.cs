using UnityEngine;
using UnityEditor;

public class AssignJengaAOIs
{
    [MenuItem("Tools/Jenga/Assign AOI Tags To Selected Children")]
    public static void AssignAOIsToSelectedChildren()
    {
        GameObject root = Selection.activeGameObject;

        if (root == null)
        {
            Debug.LogWarning("Selecciona el objeto padre que contiene los bloques de Jenga.");
            return;
        }

        int count = 0;

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child == root.transform)
                continue;

            Collider col = child.GetComponent<Collider>();
            if (col == null)
                continue;

            AOITag tag = child.GetComponent<AOITag>();
            if (tag == null)
            {
                tag = Undo.AddComponent<AOITag>(child.gameObject);
            }

            count++;
            tag.aoiType = "jenga_block";
            tag.aoiId = $"jenga_block_{count:00}";

            EditorUtility.SetDirty(child.gameObject);
        }

        Debug.Log($"AOI tags asignados a {count} objetos con collider bajo {root.name}.");
    }
}