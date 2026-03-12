using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class JengaTowerGenerator : MonoBehaviour
{
    [Header("References")]
    public GameObject blockPrefab;
    public Material[] blockMaterials;

    [Header("Real block dimensions")]
    public float blockLength = 0.075f;
    public float blockHeight = 0.015f;
    public float blockWidth = 0.025f;

    [Header("Tower settings")]
    public int levels = 6;
    public float horizontalSpacing = 0.0005f;
    public float verticalSpacing = 0.0002f;
    public bool generateOnStart = true;

    private readonly List<Rigidbody> spawnedBodies = new List<Rigidbody>();

    void Start()
    {
        if (generateOnStart)
            StartCoroutine(GenerateTowerStable());
    }

    [ContextMenu("Reset Tower")]
    public void ResetTower()
    {
        if (Application.isPlaying)
            StartCoroutine(ResetTowerCoroutine());
    }

    [ContextMenu("Clear Tower")]
    public void ClearTower()
    {
        if (Application.isPlaying)
            ClearTowerPlayMode();
    }

    private IEnumerator ResetTowerCoroutine()
    {
        ClearTowerPlayMode();
        yield return null;
        yield return new WaitForFixedUpdate();
        yield return StartCoroutine(GenerateTowerStable());
    }

    private void ClearTowerPlayMode()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Destroy(transform.GetChild(i).gameObject);
        }
    }

    private IEnumerator GenerateTowerStable()
    {
        spawnedBodies.Clear();

        for (int level = 0; level < levels; level++)
        {
            bool rotate = (level % 2 == 1);

            float y = level * (blockHeight + verticalSpacing) + (blockHeight * 0.5f);

            List<Rigidbody> levelBodies = new List<Rigidbody>();

            for (int i = -1; i <= 1; i++)
            {
                float lateralOffset = i * (blockWidth + horizontalSpacing);

                Vector3 localPos;
                Quaternion rot;

                if (!rotate)
                {
                    // Bloques largos en X, separados en Z
                    localPos = new Vector3(0f, y, lateralOffset);
                    rot = Quaternion.identity;
                }
                else
                {
                    // Bloques largos en Z, separados en X
                    localPos = new Vector3(lateralOffset, y, 0f);
                    rot = Quaternion.Euler(0f, 90f, 0f);
                }

                GameObject block = Instantiate(
                    blockPrefab,
                    transform.position + localPos,
                    rot,
                    transform
                );

                // Asignar color al hijo visual
                Renderer r = block.GetComponentInChildren<Renderer>();
                if (r != null && blockMaterials != null && blockMaterials.Length > 0)
                {
                    r.material = blockMaterials[Random.Range(0, blockMaterials.Length)];
                }

                Rigidbody rb = block.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    levelBodies.Add(rb);
                    spawnedBodies.Add(rb);
                }
            }

            yield return null;
            yield return new WaitForFixedUpdate();

            foreach (Rigidbody rb in levelBodies)
            {
                if (rb != null)
                    rb.isKinematic = false;
            }

            yield return new WaitForFixedUpdate();
        }
    }
}