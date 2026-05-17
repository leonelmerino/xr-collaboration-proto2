using UnityEngine;

/// <summary>
/// Camara libre por teclado/mouse para inspeccionar la escena en builds sin XR.
/// En Editor no hace nada. En build, se activa al inicio si hay una camara hija activa.
///
/// Controles:
///   WASD              moverse en plano
///   Espacio / Ctrl    subir / bajar
///   Shift             correr
///   Mouse derecho     mantener para mirar alrededor
///   F1                congelar / descongelar la camara
/// </summary>
public class BuildFlyCamera : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 1.5f;
    [SerializeField] private float runMultiplier = 3f;
    [SerializeField] private float lookSensitivity = 2.0f;
    [SerializeField] private bool enableInEditor = false;

    private Camera targetCamera;
    private float yaw;
    private float pitch;
    private bool frozen = false;

    private void Awake()
    {
#if UNITY_EDITOR
        if (!enableInEditor)
        {
            enabled = false;
            return;
        }
#endif
        targetCamera = GetComponentInChildren<Camera>();
        if (targetCamera == null) targetCamera = Camera.main;

        if (targetCamera == null)
        {
            Debug.LogWarning("[BuildFlyCamera] No se encontro Camera para controlar.");
            enabled = false;
            return;
        }

        Vector3 e = targetCamera.transform.eulerAngles;
        yaw = e.y;
        pitch = e.x;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F1)) frozen = !frozen;
        if (frozen || targetCamera == null) return;

        if (Input.GetMouseButton(1))
        {
            yaw += Input.GetAxis("Mouse X") * lookSensitivity;
            pitch -= Input.GetAxis("Mouse Y") * lookSensitivity;
            pitch = Mathf.Clamp(pitch, -89f, 89f);
            targetCamera.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? runMultiplier : 1f) * Time.deltaTime;

        Vector3 forward = targetCamera.transform.forward;
        Vector3 right = targetCamera.transform.right;
        Vector3 up = Vector3.up;

        Vector3 delta = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) delta += forward;
        if (Input.GetKey(KeyCode.S)) delta -= forward;
        if (Input.GetKey(KeyCode.D)) delta += right;
        if (Input.GetKey(KeyCode.A)) delta -= right;
        if (Input.GetKey(KeyCode.Space)) delta += up;
        if (Input.GetKey(KeyCode.LeftControl)) delta -= up;

        targetCamera.transform.position += delta.normalized * speed;
    }
}
