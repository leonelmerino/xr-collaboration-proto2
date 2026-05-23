using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class JengaTowerGenerator : MonoBehaviour
{
    public static JengaTowerGenerator Instance { get; private set; }

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

    [Header("Single-player / tuning")]
    [Tooltip("Si NGO esta en escena pero nadie pulsa H/C dentro de autoBuildDelaySec segundos, construye la torre localmente. " +
             "Util para tuning de fisica sin tener que iniciar como host. Si pulsas H/C dentro del timeout, el auto-build se cancela y arranca el flujo NGO normal.")]
    public bool autoBuildIfNoServer = true;
    [Tooltip("Segundos de espera antes de auto-construir en modo standalone.")]
    public float autoBuildDelaySec = 2f;

    [Header("AOI Tagging")]
    public bool addAOITags = true;
    public string aoiType = "jenga_block";
    public bool renameBlocksToAOI = true;

    private bool isBuilding = false;
    private bool hasBuiltOnce = false;
    private readonly List<NetworkObject> spawnedBlocks = new();
    private bool serverHandlerRegistered;

    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void Start()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null)
        {
            // Esperamos a que el server arranque para construir.
            nm.OnServerStarted += HandleServerStarted;
            serverHandlerRegistered = true;

            // Si por alguna razon ya esta corriendo (recarga de escena), arrancamos ya.
            if (nm.IsServer)
            {
                StartCoroutine(InitialBuildRoutine());
            }
            else if (autoBuildIfNoServer)
            {
                // Modo tuning: si nadie inicia NGO, construimos local despues del timeout.
                StartCoroutine(AutoBuildIfStandaloneRoutine());
            }
        }
        else
        {
            // Sin NGO en escena: comportamiento local original.
            StartCoroutine(InitialBuildRoutine());
        }
    }

    private IEnumerator AutoBuildIfStandaloneRoutine()
    {
        yield return new WaitForSeconds(autoBuildDelaySec);

        if (hasBuiltOnce) yield break; // alguien ya disparo el build (server start dentro del timeout).

        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening) yield break;

        Debug.Log("[JengaTowerGenerator] No se inicio NGO; construyendo torre en modo standalone para tuning.");
        yield return StartCoroutine(InitialBuildRoutine());
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (serverHandlerRegistered && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted -= HandleServerStarted;
        }
    }

    public Material GetMaterial(int idx)
    {
        if (blockMaterials == null) return null;
        if (idx < 0 || idx >= blockMaterials.Length) return null;
        return blockMaterials[idx];
    }

    private void HandleServerStarted()
    {
        StartCoroutine(InitialBuildRoutine());
    }

    private IEnumerator InitialBuildRoutine()
    {
        if (hasBuiltOnce) yield break;
        hasBuiltOnce = true;
        yield return null;
        yield return new WaitForFixedUpdate();
        yield return new WaitForSeconds(0.25f);
        yield return StartCoroutine(ResetCoroutine());
    }

    private bool IsServerOrStandalone =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

    private bool IsNetworked =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    IEnumerator BuildTower()
    {
        if (!IsServerOrStandalone) yield break;

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

                // Cuando hay NGO, no parenteamos (los hijos de un Transform de escena no se sincronizan a clientes).
                Transform parent = IsNetworked ? null : transform;

                GameObject block = Instantiate(blockPrefab, spawnPos, rot, parent);

                int matIdx = ComputeMaterialIndex(level, i);
                ConfigureBlockAOI(block, level, i);

                if (IsNetworked)
                {
                    var netObj = block.GetComponent<NetworkObject>();
                    if (netObj != null)
                    {
                        netObj.Spawn(true);
                        spawnedBlocks.Add(netObj);

                        // El indice del material se sincroniza via NetworkVariable.
                        var netBlock = block.GetComponent<NetworkedJengaBlock>();
                        if (netBlock != null)
                            netBlock.SetMaterialIndex(matIdx);
                    }
                    else
                    {
                        Debug.LogWarning("[JengaTowerGenerator] blockPrefab no tiene NetworkObject; el bloque no se va a sincronizar.");
                    }
                }
                else
                {
                    // Standalone: aplicar material localmente.
                    ApplyMaterialDirect(block, matIdx);
                }

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

    private int ComputeMaterialIndex(int level, int i)
    {
        if (blockMaterials == null || blockMaterials.Length == 0) return -1;
        return ((level * 3) + (i + 1)) % blockMaterials.Length;
    }

    private void ApplyMaterialDirect(GameObject block, int matIdx)
    {
        if (matIdx < 0) return;
        Renderer r = block.GetComponentInChildren<Renderer>();
        if (r != null && blockMaterials != null && matIdx < blockMaterials.Length)
            r.sharedMaterial = blockMaterials[matIdx];
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
        if (!IsServerOrStandalone)
        {
            Debug.LogWarning("[JengaTowerGenerator] Solo el server puede reset.");
            return;
        }
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
        if (!IsServerOrStandalone)
            return;

        // Despawn de los bloques networked.
        foreach (var netObj in spawnedBlocks)
        {
            if (netObj != null && netObj.IsSpawned)
                netObj.Despawn(true);
        }
        spawnedBlocks.Clear();

        // Limpiar tambien posibles hijos directos (modo standalone sin NGO).
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Destroy(transform.GetChild(i).gameObject);
        }
    }
}
