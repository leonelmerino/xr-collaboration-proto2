using System.Collections;
using UnityEngine;

public class JengaTowerGenerator : MonoBehaviour
{
    [Header("References")]
    public GameObject blockPrefab;
    public Material[] blockMaterials;

    [Header("Block Dimensions")]
    public float blockLength = 0.075f;
    public float blockHeight = 0.015f;
    public float blockWidth = 0.025f;

    [Header("Tower Settings")]
    public int levels = 6;
    public float horizontalSpacing = 0.0005f;
    public float verticalSpacing = 0.0002f;
    public float dropHeight = 0.01f;

    [Header("Physics")]
    public float settleVelocity = 0.01f;
    public float settleAngularVelocity = 0.01f;
    public float maxSettleTime = 1.0f;

    [Header("AOI Tagging")]
    public bool addAOITags = true;
    public string aoiType = "jenga_block";
    public bool renameBlocksToAOI = true;

    private bool isBuilding = false;

    void Start()
    {
        StartCoroutine(BuildTower());
    }

    IEnumerator BuildTower()
    {
        isBuilding = true;

        for (int level = 0; level < levels; level++)
        {
            bool rotate = (level % 2 == 1);
            float y = level * (blockHeight + verticalSpacing) + (blockHeight * 0.5f);

            for (int i = -1; i <= 1; i++)
            {
                float lateralOffset = i * (blockWidth + horizontalSpacing);

                Vector3 localPos;
                Quaternion rot;

                if (!rotate)
                {
                    localPos = new Vector3(0f, y, lateralOffset);
                    rot = Quaternion.identity;
                }
                else
                {
                    localPos = new Vector3(lateralOffset, y, 0f);
                    rot = Quaternion.Euler(0f, 90f, 0f);
                }

                Vector3 spawnPos = transform.position + localPos + Vector3.up * dropHeight;

                GameObject block = Instantiate(
                    blockPrefab,
                    spawnPos,
                    rot,
                    transform
                );

                ConfigureBlockMaterial(block);
                ConfigureBlockAOI(block, level, i);

                Rigidbody rb = block.GetComponent<Rigidbody>();

                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.isKinematic = false;
                    rb.useGravity = true;

                    yield return StartCoroutine(WaitUntilSettled(rb));
                }
            }
        }

        isBuilding = false;
    }

    private void ConfigureBlockMaterial(GameObject block)
    {
        Renderer r = block.GetComponentInChildren<Renderer>();

        if (r != null && blockMaterials != null && blockMaterials.Length > 0)
        {
            r.material = blockMaterials[Random.Range(0, blockMaterials.Length)];
        }
    }

    private void ConfigureBlockAOI(GameObject block, int level, int i)
    {
        if (!addAOITags || block == null)
            return;

        AOITag tag = block.GetComponent<AOITag>();
        if (tag == null)
        {
            tag = block.AddComponent<AOITag>();
        }

        string side = GetSideLabel(i);
        string orientation = (level % 2 == 0) ? "z" : "x";
        string aoiId = $"jenga_l{level:00}_{side}_{orientation}";

        tag.aoiId = aoiId;
        tag.aoiType = aoiType;
        tag.level = level;
        tag.indexInLevel = SideIndexToZeroBased(i);

        if (renameBlocksToAOI)
        {
            block.name = aoiId;
        }
    }

    private string GetSideLabel(int i)
    {
        switch (i)
        {
            case -1: return "left";
            case 0: return "center";
            case 1: return "right";
            default: return "unknown";
        }
    }

    private int SideIndexToZeroBased(int i)
    {
        switch (i)
        {
            case -1: return 0;
            case 0: return 1;
            case 1: return 2;
            default: return -1;
        }
    }

    IEnumerator WaitUntilSettled(Rigidbody rb)
    {
        float timer = 0f;

        while (timer < maxSettleTime)
        {
            if (rb == null)
                yield break;

            if (rb.velocity.magnitude < settleVelocity &&
                rb.angularVelocity.magnitude < settleAngularVelocity)
            {
                yield break;
            }

            timer += Time.fixedDeltaTime;
            yield return new WaitForFixedUpdate();
        }
    }

    [ContextMenu("Reset Tower")]
    public void ResetTower()
    {
        if (!Application.isPlaying || isBuilding) return;

        StartCoroutine(ResetCoroutine());
    }

    IEnumerator ResetCoroutine()
    {
        ClearTower();
        yield return new WaitForFixedUpdate();
        yield return StartCoroutine(BuildTower());
    }

    [ContextMenu("Clear Tower")]
    public void ClearTower()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Destroy(transform.GetChild(i).gameObject);
        }
    }
}