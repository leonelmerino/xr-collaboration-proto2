using Unity.Netcode;
using UnityEngine;
using Unity.XR.CoreUtils;

public class AvatarFollowXROrigin : NetworkBehaviour
{
    public Transform head;

    Transform xrOrigin;
    Transform hmd;

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
            xrOrigin = origin.transform;
            hmd = origin.Camera.transform;
        }
    }

    void Update()
    {
        if (!IsOwner) return;

        if (xrOrigin == null || hmd == null)
        {
            TryFindXR();
            return;
        }

        // Avatar root follows player feet
        transform.position = new Vector3(hmd.position.x, 0f, hmd.position.z);
        transform.rotation = Quaternion.Euler(0, hmd.eulerAngles.y, 0);

        // Head follows HMD
        if (head != null)
        {
            head.position = hmd.position;
            head.rotation = hmd.rotation;
        }
    }
}