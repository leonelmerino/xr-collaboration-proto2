using Unity.Netcode;
using UnityEngine;

public class AvatarFollowXROrigin : NetworkBehaviour
{
    [Header("Assign these in the prefab")]
    public Transform xrOrigin;   // XR Origin transform (local player)
    public Transform head;       // Head mesh transform (Sphere)

    private Transform hmd;       // Main Camera in XR Origin

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        // Find XR Origin in scene
        var origin = FindObjectOfType<Unity.XR.CoreUtils.XROrigin>();
        if (origin != null)
        {
            xrOrigin = origin.transform;
            hmd = origin.Camera.transform;
        }
    }

    void Update()
    {
        if (!IsOwner) return;
        if (xrOrigin == null || hmd == null) return;

        // Move avatar root to XR Origin position (feet)
        transform.position = xrOrigin.position;
        transform.rotation = Quaternion.Euler(0f, hmd.eulerAngles.y, 0f);

        // Move head to HMD pose (relative)
        if (head != null)
        {
            head.position = hmd.position;
            head.rotation = hmd.rotation;
        }
    }
}