#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool: crea un Light Probe Group con probes distribuidos en la habitacion,
/// con densidad extra alrededor de la mesa (zona de actividad principal donde estan
/// los Jenga blocks dinamicos).
///
/// Los Light Probes son esenciales tras un bake: los objetos no-Static (Jenga blocks,
/// avatares, manos) NO reciben luz del lightmap directamente. En cambio interpolan
/// la luz ambiente de los Probes mas cercanos. Sin probes, esos objetos se ven planos
/// e "out of place" comparados con los muebles bakeados.
///
/// Distribucion creada:
/// - Grid principal: 3 capas (Y=0.4, 1.5, 2.5), 3x3 cada una -> 27 probes.
/// - Cluster denso alrededor de la mesa: 8 probes (4 esquinas x 2 alturas) en un cuadrado
///   de 1m centrado en la mesa, alturas 0.75 y 1.2m.
/// Total: 35 probes.
///
/// Re-ejecutar el menu: pregunta si sobreescribir. Idempotente.
/// </summary>
public static class RoomProbeGenerator
{
    private const string GroupName = "RoomLightProbes";

    // Configuracion del cuarto (debe coincidir con RoomShellBuilder).
    private static readonly Vector3 RoomSize = new Vector3(6f, 3f, 6f);
    private static readonly Vector3 RoomCenter = new Vector3(0f, 0f, 0f);
    private const float Margin = 0.5f; // separacion del borde del cuarto hacia adentro

    // Cluster alrededor de la mesa (donde estan los Jenga blocks).
    private static readonly Vector3 TableCenter = new Vector3(0f, 0f, 0f); // se sobreescribe si hay JengaTowerGenerator
    private const float TableClusterHalfSize = 0.5f; // 1m de lado
    private static readonly float[] TableClusterHeights = { 0.75f, 1.20f }; // altura mesa + altura mano

    [MenuItem("XR Collab/Room/Generate Light Probes")]
    public static void Generate()
    {
        // Sobreescribir si existe.
        var existing = GameObject.Find(GroupName);
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("Light Probe Generator",
                $"Ya existe '{GroupName}' en escena. ¿Sobreescribir?",
                "Sobreescribir", "Cancelar"))
                return;
            Undo.DestroyObjectImmediate(existing);
        }

        var root = new GameObject(GroupName);
        Undo.RegisterCreatedObjectUndo(root, "Generate Light Probes");
        var lpg = root.AddComponent<LightProbeGroup>();

        var positions = new List<Vector3>();
        positions.AddRange(GenerateMainGrid());
        positions.AddRange(GenerateTableCluster());

        lpg.probePositions = positions.ToArray();

        Selection.activeGameObject = root;
        EditorGUIUtility.PingObject(root);

        Debug.Log($"[RoomProbeGenerator] {positions.Count} probes creados " +
                  $"({GroupName}). Bakeá para que tomen efecto.");

        EditorUtility.DisplayDialog("Light Probe Generator",
            $"Creados {positions.Count} probes:\n" +
            $"  - Grid principal: 27 (3 capas x 3x3)\n" +
            $"  - Cluster alrededor de la mesa: 8\n\n" +
            "Despues del Generate Lighting, los objetos dinamicos (Jenga blocks, manos, " +
            "avatares) interpolan la luz de los probes mas cercanos.\n\n" +
            "Si necesitas mas densidad en alguna zona, podes agregar probes a mano " +
            "(seleccionar el GameObject 'RoomLightProbes' y editar en Inspector).",
            "OK");
    }

    private static IEnumerable<Vector3> GenerateMainGrid()
    {
        // 3 capas verticales: piso, media altura, techo (con margen).
        float[] heights = { 0.4f, 1.5f, 2.5f };

        // 3x3 grid en X-Z, separado del borde por Margin.
        float halfW = RoomSize.x * 0.5f - Margin;
        float halfD = RoomSize.z * 0.5f - Margin;
        float[] xs = { -halfW, 0f, halfW };
        float[] zs = { -halfD, 0f, halfD };

        var list = new List<Vector3>();
        foreach (var y in heights)
            foreach (var x in xs)
                foreach (var z in zs)
                    list.Add(new Vector3(
                        RoomCenter.x + x,
                        RoomCenter.y + y,
                        RoomCenter.z + z));
        return list;
    }

    private static IEnumerable<Vector3> GenerateTableCluster()
    {
        // Si existe JengaTowerGenerator, centramos el cluster ahi.
        Vector3 center = TableCenter;
        var jenga = Object.FindObjectOfType<JengaTowerGenerator>();
        if (jenga != null)
        {
            center = jenga.transform.position;
            center.y = 0f; // resetear Y, vamos a usar las alturas configuradas
        }

        // 4 esquinas x N alturas.
        float h = TableClusterHalfSize;
        float[] xs = { -h, h };
        float[] zs = { -h, h };

        var list = new List<Vector3>();
        foreach (var y in TableClusterHeights)
            foreach (var x in xs)
                foreach (var z in zs)
                    list.Add(new Vector3(center.x + x, center.y + y, center.z + z));
        return list;
    }
}
#endif
