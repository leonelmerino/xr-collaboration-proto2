using Unity.Netcode;
using UnityEngine;
using Unity.XR.CoreUtils;

public class AvatarFollowXROrigin : NetworkBehaviour
{
    public Transform head;

    private Transform hmd;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;
        TryFindXR();
    }

    void TryFindXR()
    {
        var origin = FindObjectOfType<XROrigin>();

        if (origin != null && origin.Camera != null)
        {
            hmd = origin.Camera.transform;
        }
    }

    void LateUpdate()
    {
        if (!IsOwner)
            return;

        if (hmd == null)
        {
            TryFindXR();
            return;
        }

        // Root = body/feet approximation
        transform.position = new Vector3(hmd.position.x, 0f, hmd.position.z);
        transform.rotation = Quaternion.Euler(0f, hmd.eulerAngles.y, 0f);

        // Head = local pose relative to body
        if (head != null)
        {
            head.localPosition = transform.InverseTransformPoint(hmd.position);
            head.localRotation = Quaternion.Inverse(transform.rotation) * hmd.rotation;
        }
    }
}