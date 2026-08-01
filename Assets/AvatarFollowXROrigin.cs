using Unity.Netcode;
using UnityEngine;
using Unity.XR.CoreUtils;

public class AvatarFollowXROrigin : NetworkBehaviour
{
    public Transform head;

    [Header("Height tracking")]
    [Tooltip("Si esta activo, calibra la altura de pie en el primer frame valido del HMD. " +
             "Desactivar si se quiere fijar manualmente standingHmdHeight.")]
    [SerializeField] private bool autoCalibrateHeight = true;

    [Tooltip("Altura del HMD sobre el suelo cuando el usuario esta de pie (metros). " +
             "Se sobreescribe automaticamente al calibrar si autoCalibrateHeight=true.")]
    [SerializeField] private float standingHmdHeight = 1.7f;

    [Header("Camera offset (para evitar ver el interior del avatar)")]
    [Tooltip("Desplazamiento del cuerpo del avatar hacia adelante en la direccion que mira el HMD (plano XZ). " +
             "Empuja el cuerpo delante de la camara: al mirar hacia abajo se ve el exterior del torso. " +
             "Rango sugerido: 0.1 – 0.25 m.")]
    [SerializeField] private float bodyForwardOffset = 0.15f;

    [Tooltip("Desplazamiento vertical adicional del cuerpo respecto al suelo. " +
             "Negativo = baja el cuerpo (camara queda mas arriba dentro del avatar). " +
             "Rango sugerido: 0.0 – (-0.10) m.")]
    [SerializeField] private float bodyVerticalOffset = 0.0f;

    private Transform hmd;
    private bool heightCalibrated = false;

    [Header("Owner visibility")]
    [Tooltip("Renderers del cuerpo (torso, cabeza) que se ocultan cuando este cliente es el owner, " +
             "para evitar verse el avatar desde adentro al agacharse. " +
             "Arrastrar aqui SOLO el SkinnedMeshRenderer del cuerpo principal. " +
             "NO incluir brazos ni manos: el owner debe seguir viendo sus extremidades.")]
    [SerializeField] private Renderer[] ownBodyRenderers = new Renderer[0];

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;
        TryFindXR();
        HideOwnAvatarRenderers();
    }

    // Oculta los renderers del cuerpo configurados para el owner.
    // Los clientes remotos no llaman este metodo (return si !IsOwner).
    private void HideOwnAvatarRenderers()
    {
        if (ownBodyRenderers == null || ownBodyRenderers.Length == 0)
        {
            Debug.LogWarning("[AvatarFollowXROrigin] ownBodyRenderers esta vacio. " +
                             "Asigna en el Inspector el SkinnedMeshRenderer del torso/cuerpo " +
                             "para evitar verse el avatar desde adentro.");
            return;
        }
        int count = 0;
        foreach (var r in ownBodyRenderers)
        {
            if (r != null) { r.enabled = false; count++; }
        }
        Debug.Log($"[AvatarFollowXROrigin] {count} renderer(s) del cuerpo propio ocultados (IsOwner).");
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

        // Calibracion automatica de altura de pie: primer frame con HMD valido.
        // Esto hace que el root quede en Y=0 cuando el usuario esta de pie,
        // y descienda proporcionalmente cuando se agacha.
        if (autoCalibrateHeight && !heightCalibrated && hmd.position.y > 0.1f)
        {
            standingHmdHeight = hmd.position.y;
            heightCalibrated = true;
            Debug.Log($"[AvatarFollowXROrigin] Altura calibrada: {standingHmdHeight:F2}m");
        }

        // Root sigue al HMD en XZ y en Y relativo a la altura calibrada de pie.
        // Si el usuario se agacha, bodyY baja (el avatar se hunde bajo el suelo).
        // Clamp a 0 para no flotar: el avatar solo puede hundirse, no subir sobre el suelo.
        float bodyY = Mathf.Min(0f, hmd.position.y - standingHmdHeight) + bodyVerticalOffset;

        // Offset forward: desplaza el cuerpo en la direccion XZ que mira el HMD
        // para que la camara quede ligeramente detras del torso y al mirar hacia
        // abajo se vea el exterior del avatar en lugar del interior.
        Vector3 hmdForwardXZ = new Vector3(hmd.forward.x, 0f, hmd.forward.z);
        if (hmdForwardXZ.sqrMagnitude > 0.001f)
            hmdForwardXZ.Normalize();
        // Empujar el body HACIA ATRAS del HMD forward:
        // la camara queda delante del torso → mirando abajo se ve el exterior (frente del avatar).
        Vector3 bodyPos = new Vector3(hmd.position.x, bodyY, hmd.position.z)
                          - hmdForwardXZ * bodyForwardOffset;

        transform.position = bodyPos;
        transform.rotation = Quaternion.Euler(0f, hmd.eulerAngles.y, 0f);

        // Head = local pose relative to body
        if (head != null)
        {
            head.localPosition = transform.InverseTransformPoint(hmd.position);
            head.localRotation = Quaternion.Inverse(transform.rotation) * hmd.rotation;
        }
    }
}
