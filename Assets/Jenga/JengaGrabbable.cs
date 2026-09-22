using UnityEngine;

public class JengaGrabbable : MonoBehaviour
{
    private Rigidbody rb;
    private bool isGrabbed = false;
    private Transform grabPoint;

    private Vector3 initialGrabOffset;
    private Vector3 allowedAxisWorld;

    // Debug: contador de FixedUpdate post-grab para log rate-limited (primeros N frames).
    private int fixedFramesPostGrab;
    private const int kDebugFixedFrames = 5;

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
        fixedFramesPostGrab = 0;

        initialGrabOffset = transform.position - grabPoint.position;
        Debug.Log($"[JengaGrab] BeginGrab '{name}' isKinematic={rb.isKinematic} blockPos={transform.position:F3} rbPos={rb.position:F3} grabPos={grabPoint.position:F3} offset={initialGrabOffset:F3} right={transform.right:F3}");

        //Vector3 right = transform.right;
        //Vector3 forward = transform.forward;

        //if (Mathf.Abs(Vector3.Dot(right, Vector3.right)) >
        //    Mathf.Abs(Vector3.Dot(forward, Vector3.right)))
       // {
            allowedAxisWorld = transform.right.normalized;
       // }
        //else
        //{
        //    allowedAxisWorld = forward.normalized;
        //}

        if (!rb.isKinematic)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
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

        Vector3 lerpTarget = Vector3.Lerp(rb.position, target, 0.35f);

        if (fixedFramesPostGrab < kDebugFixedFrames)
        {
            Debug.Log($"[JengaGrab] FixedUpdate f={fixedFramesPostGrab} '{name}' tPos={transform.position:F3} rbPos={rb.position:F3} grabPos={grabPoint.position:F3} desired={desired:F3} target={target:F3} lerpTarget={lerpTarget:F3} deltaMag={delta.magnitude:F4} constrMag={constrainedDelta.magnitude:F4}");
            fixedFramesPostGrab++;
        }

        rb.MovePosition(lerpTarget);
    }
}