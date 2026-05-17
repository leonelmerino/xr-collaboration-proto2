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
            if (IsBlockTaken(g)) continue;

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
            var net = best.GetComponent<NetworkedJengaBlock>();
            if (net != null)
                net.RequestGrab(pinchPoint);
            else
                best.BeginGrab(pinchPoint);

            EmitInteractionEvent("GRAB_BEGIN_PINCH", best.gameObject.name);
        }
    }

    void Release()
    {
        if (currentGrabbed != null)
        {
            string blockName = currentGrabbed.gameObject.name;
            var net = currentGrabbed.GetComponent<NetworkedJengaBlock>();
            if (net != null)
                net.RequestRelease();
            else
                currentGrabbed.EndGrab();
            currentGrabbed = null;

            EmitInteractionEvent("GRAB_END_PINCH", blockName);
        }
    }

    private static void EmitInteractionEvent(string label, string blockId)
    {
        var mgr = AcquisitionEventManager.Instance;
        if (mgr != null)
            mgr.EmitInteractionEvent(label, "block", blockId);
    }

    private static bool IsBlockTaken(JengaGrabbable g)
    {
        if (g.IsGrabbed()) return true;
        var net = g.GetComponent<NetworkedJengaBlock>();
        if (net != null && net.IsGrabbedAnywhere) return true;
        return false;
    }

    void OnDrawGizmos()
    {
        if (pinchPoint == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(pinchPoint.position, grabRadius);
    }
}
