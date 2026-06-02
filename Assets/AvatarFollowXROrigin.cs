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

    [Tooltip("Offset Y (metros) sumado al root del avatar despues del calculo de altura. " +
             "Necesario para compensar el colapso de posicion world de los bones superiores " +
             "cuando se escala Bip01 Spine a ~0 para ocultar el torso del owner: los clavicles " +
             "heredan la posicion colapsada (~nivel lumbar) aunque su escala world se restaure. " +
             "Valor tipico: 0.35 para un avatar Rocketbox en HTC Vive Focus Vision. " +
             "Ajustar si brazos/cuerpo se ven demasiado bajos (positivo) o altos (negativo).")]
    [SerializeField] private float bodyYOffset = 0.35f;

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
        float bodyY = Mathf.Min(0f, hmd.position.y - standingHmdHeight) + bodyYOffset;
        transform.position = new Vector3(hmd.position.x, bodyY, hmd.position.z);
        transform.rotation = Quaternion.Euler(0f, hmd.eulerAngles.y, 0f);

        // Head = local pose relative to body
        if (head != null)
        {
            head.localPosition = transform.InverseTransformPoint(hmd.position);
            head.localRotation = Quaternion.Inverse(transform.rotation) * hmd.rotation;
        }
    }
}
