using UnityEngine;

public class JengaGrabbable : MonoBehaviour
{
    private Rigidbody rb;
    private bool isGrabbed = false;
    private Transform grabPoint;

    private Vector3 initialGrabOffset;
    private Vector3 allowedAxisWorld;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    public bool IsGrabbed()
    {
        return isGrabbed;
    }

    public void BeginGrab(Transform pinchTransform)
    {
        if (rb == null) return;

        isGrabbed = true;
        grabPoint = pinchTransform;

        initialGrabOffset = transform.position - grabPoint.position;

        Vector3 right = transform.right;
        Vector3 forward = transform.forward;

        if (Mathf.Abs(Vector3.Dot(right, Vector3.right)) >
            Mathf.Abs(Vector3.Dot(forward, Vector3.right)))
        {
            allowedAxisWorld = right.normalized;
        }
        else
        {
            allowedAxisWorld = forward.normalized;
        }

        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
    }

    public void EndGrab()
    {
        if (rb == null) return;

        isGrabbed = false;
        grabPoint = null;
        rb.constraints = RigidbodyConstraints.None;
    }

    void FixedUpdate()
    {
        if (!isGrabbed || grabPoint == null || rb == null) return;

        Vector3 desired = grabPoint.position + initialGrabOffset;
        Vector3 delta = desired - transform.position;

        Vector3 constrainedDelta = Vector3.Project(delta, allowedAxisWorld);
        Vector3 target = transform.position + constrainedDelta;

        rb.MovePosition(Vector3.Lerp(rb.position, target, 0.35f));
    }
}