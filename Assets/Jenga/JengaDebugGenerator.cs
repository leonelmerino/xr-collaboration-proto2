using UnityEngine;

public class JengaDebugGenerator : MonoBehaviour
{
    public GameObject blockPrefab;

    public float blockWidth = 0.025f;
    public float blockHeight = 0.015f;
    public float spacing = 0.002f;

    void Start()
    {
        GenerateDebugLevel();
    }

    void GenerateDebugLevel()
    {
        float y = blockHeight * 0.5f;

        for (int i = -1; i <= 1; i++)
        {
            Vector3 localPos = new Vector3(
                i * (blockWidth + spacing),
                y,
                0f
            );

            Quaternion rot = Quaternion.identity;

            GameObject block = Instantiate(
                blockPrefab,
                transform.position + localPos,
                rot,
                transform
            );

            Debug.Log(
                "Block index: " + i +
                " | World Pos: " + block.transform.position +
                " | Local Pos: " + block.transform.localPosition +
                " | Rotation: " + block.transform.rotation.eulerAngles
            );

            // Dibuja una línea vertical para ver dónde nace
            Debug.DrawLine(
                block.transform.position,
                block.transform.position + Vector3.up * 0.1f,
                Color.red,
                10f
            );
        }
    }
}