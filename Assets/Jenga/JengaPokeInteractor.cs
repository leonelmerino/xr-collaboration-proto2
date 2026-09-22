using UnityEngine;

public class JengaPokeInteractor : MonoBehaviour
{
    [Header("References")]
    public Transform pokePoint;

    [Header("Poke Settings")]
    public float pokeRadius = 0.01f;
    public float pokeForce = 0.05f;
    public float cooldown = 0.3f;
    // Minimo dot product entre direccion del movimiento del dedo y direccion HACIA el bloque.
    // 0.5 = ~60 grados. Si el dedo se mueve lateral (rozando) el dot es bajo y no pokea.
    // Sin este check, cualquier micro-movimiento dentro de pokeRadius disparaba impulses acumulados
    // que hacian volar el bloque 10-20cm.
    public float minApproachDot = 0.5f;

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
                // Filtro direccional: el dedo tiene que estar acercandose al centro del bloque, no
                // rozando lateral. Sin esto, cualquier micromovimiento cerca del bloque generaba
                // impulses que se acumulaban en 20-30 pokes/segundo y hacian volar el bloque.
                Vector3 toBlock = (rb.worldCenterOfMass - pokePoint.position).normalized;
                float approach = Vector3.Dot(movement.normalized, toBlock);
                if (approach < minApproachDot) continue;

                Vector3 force = movement.normalized * pokeForce;

                // Si el bloque es networked, ruteamos el push al owner (server) via ServerRpc.
                // Sin esto, Client/Helper aplicarian AddForce local que el OwnerNetworkTransform
                // pisa inmediatamente con la pose del server — parece que el dedo atraviesa el
                // bloque. En el Host, RequestPush detecta IsOwner=true y aplica local sin roundtrip.
                var netBlock = hit.GetComponentInParent<NetworkedJengaBlock>();
                Debug.Log($"[JengaPokeInteractor] HIT block='{hit.gameObject.name}' netBlock={(netBlock != null ? "OK" : "NULL")} force={force.magnitude:F3} approach={approach:F2}");

                if (netBlock != null)
                {
                    // Punto de aplicacion = centro de masa. AddForceAtPosition en el pokePoint
                    // rotaba el bloque y se leia como "el bloque se mueve antes de tocarlo".
                    // Con el centro de masa el impulso es pura traslacion, mas predecible.
                    netBlock.RequestPush(force, rb.worldCenterOfMass);
                }
                else
                {
                    // Fallback offline/standalone (bloque sin NetworkedJengaBlock).
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