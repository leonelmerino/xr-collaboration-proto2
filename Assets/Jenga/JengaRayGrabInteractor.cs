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

            if (g != null && !IsBlockTaken(g))
            {
                currentGrabbed = g;

                // Notificar al TurnManager ANTES del evento BioLab genérico:
                // si es la primera interacción del turno, BeginTask() se llama aquí,
                // lo que habilita la emisión de GRAB_BEGIN_RAY a continuación.
                JengaTurnManager.Instance?.NotifyBlockInteraction(g.gameObject.name);

                var net = g.GetComponent<NetworkedJengaBlock>();
                if (net != null)
                    net.RequestGrab(pinchPoint);
                else
                    g.BeginGrab(pinchPoint);

                EmitInteractionEvent("GRAB_BEGIN_RAY", g.gameObject.name);
            }
        }
    }

    void Release()
    {
        if (currentGrabbed != null)
        {
            string blockName = currentGrabbed.gameObject.name;
            Vector3 blockPos = currentGrabbed.transform.position;

            // Chequear si el bloque está fuera de la huella ANTES de soltarlo.
            bool outsideFootprint = JengaTowerMonitor.Instance != null &&
                                    JengaTowerMonitor.Instance.IsOutsideFootprint(blockPos);

            // Emitir evento genérico ANTES de NotifyBlockReleased, porque
            // NotifyBlockReleased puede llamar EndTask() cerrando la ventana de emisión.
            EmitInteractionEvent("GRAB_END_RAY", blockName);

            var net = currentGrabbed.GetComponent<NetworkedJengaBlock>();
            if (net != null)
                net.RequestRelease();
            else
                currentGrabbed.EndGrab();
            currentGrabbed = null;

            // Notificar al TurnManager: si el bloque quedó fuera de huella → BLOCK_REMOVED.
            JengaTurnManager.Instance?.NotifyBlockReleased(blockName, outsideFootprint);
        }

        if (pokeInteractor != null)
            pokeInteractor.SetPokeEnabled(true);
    }

    private static bool IsBlockTaken(JengaGrabbable g)
    {
        if (g.IsGrabbed()) return true;
        var net = g.GetComponent<NetworkedJengaBlock>();
        if (net != null && net.IsGrabbedAnywhere) return true;
        return false;
    }

    private static void EmitInteractionEvent(string label, string blockId)
    {
        var mgr = AcquisitionEventManager.Instance;
        if (mgr != null)
            mgr.EmitInteractionEvent(label, "block", blockId);
    }
}