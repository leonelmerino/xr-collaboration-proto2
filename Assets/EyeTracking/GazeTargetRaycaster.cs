using UnityEngine;

public class GazeTargetRaycaster : MonoBehaviour
{
    public ViveEyeTrackingProvider eyeProvider;
    public float maxDistance = 20f;
    public LayerMask layers = ~0;

    public bool HasHit { get; private set; }
    public string HitObjectName { get; private set; }
    public string HitAOI { get; private set; }
    public string HitAOIType { get; private set; }
    public Vector3 HitPoint { get; private set; }

    private void Reset()
    {
        eyeProvider = FindObjectOfType<ViveEyeTrackingProvider>();
    }

    private void Update()
    {
        HasHit = false;
        HitObjectName = "";
        HitAOI = "";
        HitAOIType = "";
        HitPoint = Vector3.zero;

        if (eyeProvider == null || !eyeProvider.CombinedGazeValid)
            return;

        Ray ray = new Ray(eyeProvider.CombinedOrigin, eyeProvider.CombinedDirection);

        if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, layers))
        {
            HasHit = true;
            HitObjectName = hit.collider.gameObject.name;
            HitPoint = hit.point;

            AOITag tag = hit.collider.GetComponentInParent<AOITag>();
            if (tag != null)
            {
                HitAOI = tag.aoiId;
                HitAOIType = tag.aoiType;
            }
        }
    }
}