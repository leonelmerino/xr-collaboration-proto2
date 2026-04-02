using TMPro;
using UnityEngine;

public class RoleAvatarPresenter : MonoBehaviour
{
    [SerializeField] private Renderer[] bodyRenderers;
    [SerializeField] private TextMeshPro roleLabel;
    [SerializeField] private Transform labelAnchor;

    private Camera mainCamera;

    public void Setup(PlayerRole role, Color color, bool isMock)
    {
        ApplyColor(color);

        if (roleLabel != null)
        {
            roleLabel.text = isMock ? $"{role} (Mock)" : role.ToString();
        }
    }

    private void ApplyColor(Color color)
    {
        if (bodyRenderers == null) return;

        foreach (var r in bodyRenderers)
        {
            if (r == null) continue;

            Material[] mats = r.materials;
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i].color = color;
            }
        }
    }

    private void LateUpdate()
    {
        if (labelAnchor == null) return;

        if (mainCamera == null)
            mainCamera = Camera.main;

        if (mainCamera == null) return;

        Vector3 toCamera = mainCamera.transform.position - labelAnchor.position;
        toCamera.y = 0f;

        if (toCamera.sqrMagnitude < 0.0001f) return;

        labelAnchor.rotation = Quaternion.LookRotation(toCamera) * Quaternion.Euler(0f, 180f, 0f);
    }
}