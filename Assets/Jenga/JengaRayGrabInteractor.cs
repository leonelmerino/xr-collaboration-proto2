using UnityEngine;

public class JengaRayGrabInteractor : MonoBehaviour
{
    [Header("References")]
    public Transform rayOrigin;
    public Transform pinchPoint;
    public LineRenderer rayLine;
    public JengaPokeInteractor pokeInteractor;

    [Header("Ray Settings")]
    public float rayLength = 0.15f;
    public LayerMask rayMask = ~0;

    private JengaGrabbable currentGrabbed;
    private bool wasPinching = false;

    void Update()
    {
        if (rayOrigin == null) return;

        Vector3 start = rayOrigin.position;
        Vector3 dir = rayOrigin.forward;
        Vector3 end = start + dir * rayLength;

        if (Physics.Raycast(start, dir, out RaycastHit hit, rayLength, rayMask))
        {
            end = hit.point;
            Debug.Log("Ray hit: " + hit.collider.name);
        }

        if (rayLine != null)
        {
            rayLine.enabled = true;
            rayLine.positionCount = 2;
            rayLine.SetPosition(0, start);
            rayLine.SetPosition(1, end);
        }

        Debug.DrawRay(start, dir * rayLength, Color.cyan);
    }

    public void SetPinchState(bool isPinching)
    {
        if (pokeInteractor != null)
            pokeInteractor.SetPokeEnabled(!isPinching && currentGrabbed == null);

        if (isPinching && !wasPinching)
            TryGrab();

        if (!isPinching && wasPinching)
            Release();

        wasPinching = isPinching;
    }

    void TryGrab()
    {
        if (pinchPoint == null || rayOrigin == null) return;
        if (currentGrabbed != null) return;

        Ray ray = new Ray(rayOrigin.position, rayOrigin.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, rayLength, rayMask))
        {
            JengaGrabbable g = hit.collider.GetComponentInParent<JengaGrabbable>();

            if (g != null && !g.IsGrabbed())
            {
                currentGrabbed = g;
                currentGrabbed.BeginGrab(pinchPoint);
                Debug.Log("Grab started on " + g.name);
            }
        }
    }

    void Release()
    {
        if (currentGrabbed != null)
        {
            currentGrabbed.EndGrab();
            currentGrabbed = null;
            Debug.Log("Grab released");
        }

        if (pokeInteractor != null)
            pokeInteractor.SetPokeEnabled(true);
    }
}