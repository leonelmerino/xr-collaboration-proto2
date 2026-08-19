using UnityEngine;

public class JengaPokeInteractor : MonoBehaviour
{
    [Header("References")]
    public Transform pokePoint;

    [Header("Poke Settings")]
    public float pokeRadius = 0.01f;
    public float pokeForce = 0.15f;
    public float cooldown = 0.05f;

    [Header("State")]
    public bool enablePoke = true;

    private float lastPokeTime;
    private Vector3 previousPosition;

    void Start()
    {
        if (pokePoint != null)
            previousPosition = pokePoint.position;
    }

    void Update()
    {
        if (!enablePoke) return;
        if (pokePoint == null) return;

        Vector3 movement = pokePoint.position - previousPosition;
        previousPosition = pokePoint.position;

        if (Time.time - lastPokeTime < cooldown) return;
        if (movement.magnitude < 0.001f) return;

        Collider[] hits = Physics.OverlapSphere(pokePoint.position, pokeRadius);

        foreach (Collider hit in hits)
        {
            Rigidbody rb = hit.attachedRigidbody;

            if (rb != null && hit.GetComponentInParent<JengaBlockTag>() != null)
            {
                Vector3 forceDir = movement.normalized;
                Vector3 force = forceDir * pokeForce;

                // Si el bloque es networked, ruteamos el push al owner (server) via ServerRpc.
                // Sin esto, Client/Helper aplicarian AddForce local que el OwnerNetworkTransform
                // pisa inmediatamente con la pose del server — parece que el dedo atraviesa el
                // bloque. Con el ruteo, el server aplica el impulso autoritativamente y
                // OwnerNetworkTransform propaga la nueva pose a todos.
                var netBlock = hit.GetComponentInParent<NetworkedJengaBlock>();
                if (netBlock != null)
                {
                    netBlock.RequestPush(force, pokePoint.position);
                }
                else
                {
                    // Fallback offline/standalone: aplicar directo.
                    rb.AddForce(force, ForceMode.Impulse);
                }

                lastPokeTime = Time.time;
                break;
            }
        }
    }

    public void SetPokeEnabled(bool value)
    {
        enablePoke = value;
    }

    void OnDrawGizmos()
    {
        if (pokePoint == null) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(pokePoint.position, pokeRadius);
    }
}