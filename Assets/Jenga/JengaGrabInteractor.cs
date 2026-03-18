using UnityEngine;

public class JengaGrabInteractor : MonoBehaviour
{
    [Header("References")]
    public Transform pinchPoint;

    [Header("Grab Settings")]
    public float grabRadius = 0.015f;

    private JengaGrabbable currentGrabbed;
    private bool wasPinching = false;

    public void SetPinchState(bool isPinching)
    {
        // Pinch start
        if (isPinching && !wasPinching)
        {
            TryGrab();
        }

        // Pinch end
        if (!isPinching && wasPinching)
        {
            Release();
        }

        wasPinching = isPinching;
    }

    void TryGrab()
    {
        if (pinchPoint == null) return;
        if (currentGrabbed != null) return;

        Collider[] hits = Physics.OverlapSphere(pinchPoint.position, grabRadius);

        float bestDist = float.MaxValue;
        JengaGrabbable best = null;

        foreach (Collider hit in hits)
        {
            JengaGrabbable g = hit.GetComponentInParent<JengaGrabbable>();
            if (g == null) continue;
            if (g.IsGrabbed()) continue;

            float d = Vector3.Distance(hit.ClosestPoint(pinchPoint.position),
                                       pinchPoint.position);

            if (d < bestDist)
            {
                bestDist = d;
                best = g;
            }
        }

        if (best != null)
        {
            currentGrabbed = best;
            currentGrabbed.BeginGrab(pinchPoint);
        }
    }

    void Release()
    {
        if (currentGrabbed != null)
        {
            currentGrabbed.EndGrab();
            currentGrabbed = null;
        }
    }

    void OnDrawGizmos()
    {
        if (pinchPoint == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(pinchPoint.position, grabRadius);
    }
}