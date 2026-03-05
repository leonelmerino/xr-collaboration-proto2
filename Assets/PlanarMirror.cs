using UnityEngine;

[ExecuteAlways]
public class PlanarMirror : MonoBehaviour
{
    [Header("Assign in Inspector")]
    public Transform playerCamera;   // XR Main Camera
    public Camera mirrorCamera;      // Camera rendering to MirrorRT
    public Transform mirrorPlane;    // The plane object (this mirror)

    void LateUpdate()
    {
        if (playerCamera == null || mirrorCamera == null || mirrorPlane == null)
            return;

        // Mirror plane definition
        Vector3 p = mirrorPlane.position;
        Vector3 n = mirrorPlane.forward; // normal pointing "out" of mirror front face
        n.Normalize();

        // Reflect player camera position across plane
        Vector3 camPos = playerCamera.position;
        Vector3 reflectedPos = camPos - 2f * Vector3.Dot(camPos - p, n) * n;
        mirrorCamera.transform.position = reflectedPos;

        // Reflect forward and up vectors to get correct rotation
        Vector3 camForward = playerCamera.forward;
        Vector3 camUp = playerCamera.up;

        Vector3 reflectedForward = Vector3.Reflect(camForward, n);
        Vector3 reflectedUp = Vector3.Reflect(camUp, n);

        mirrorCamera.transform.rotation = Quaternion.LookRotation(reflectedForward, reflectedUp);

        // Optional: match FOV
        if (playerCamera.TryGetComponent<Camera>(out var pc))
            mirrorCamera.fieldOfView = pc.fieldOfView;
    }
}