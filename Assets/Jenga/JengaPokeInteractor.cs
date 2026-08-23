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
                Vector3 force = movement.normalized * pokeForce;

                // Si el bloque es networked, ruteamos el push al owner (server) via ServerRpc.
                // Sin esto, Client/Helper aplicarian AddForce local que el OwnerNetworkTransform
                // pisa inmediatamente con la pose del server — parece que el dedo atraviesa el
                // bloque. En el Host, RequestPush detecta IsOwner=true y aplica local sin
                // roundtrip: comportamiento identico al pre-fix.
                var netBlock = hit.GetComponentInParent<NetworkedJengaBlock>();
                Debug.Log($"[JengaPokeInteractor] HIT block='{hit.gameObject.name}' netBlock={(netBlock != null ? "OK" : "NULL")} force={force.magnitude:F3}");

                if (netBlock != null)
                {
                    // Pasamos el punto de contacto (pokePoint) — el server aplica la fuerza
                    // ahi con AddForceAtPosition, no en el centro de masa. Genera torque
                    // ademas de traslacion, el bloque rota un poco en vez de "volar" lineal.
                    netBlock.RequestPush(force, pokePoint.position);
                }
                else
                {
                    // Fallback offline/standalone (bloque sin NetworkedJengaBlock).
                    rb.AddForceAtPosition(force, pokePoint.position, ForceMode.Impulse);
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