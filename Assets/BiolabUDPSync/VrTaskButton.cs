using System.Collections;
using UnityEngine;

public class VrTaskButton : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform visual;
    [SerializeField] private Renderer visualRenderer;

    [Header("Animation")]
    [SerializeField] private float pressDepth = 0.006f;
    [SerializeField] private float pressDuration = 0.06f;
    [SerializeField] private float releaseDuration = 0.12f;

    [Header("Colors")]
    [SerializeField] private Color normalColor = Color.white;
    [SerializeField] private Color pressedColor = Color.green;

    [Header("Safety")]
    [SerializeField] private float pressCooldown = 0.2f;

    private Vector3 initialLocalPos;
    private bool isAnimating = false;
    private bool isCooldown = false;
    private Material runtimeMaterial;

    private void Awake()
    {
        if (visual != null)
            initialLocalPos = visual.localPosition;

        if (visualRenderer != null)
        {
            runtimeMaterial = visualRenderer.material;
            runtimeMaterial.color = normalColor;
        }
    }

    public void Press()
    {
        if (isAnimating || isCooldown)
            return;

        StartCoroutine(PressRoutine());
    }

    private IEnumerator PressRoutine()
    {
        isAnimating = true;
        isCooldown = true;

        Vector3 pressedPos = initialLocalPos + Vector3.down * pressDepth;

        if (runtimeMaterial != null)
            runtimeMaterial.color = pressedColor;

        float t = 0f;
        while (t < pressDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / pressDuration);
            k = 1f - Mathf.Pow(1f - k, 3f); // ease out
            if (visual != null)
                visual.localPosition = Vector3.Lerp(initialLocalPos, pressedPos, k);
            yield return null;
        }

        t = 0f;
        while (t < releaseDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / releaseDuration);
            if (visual != null)
                visual.localPosition = Vector3.Lerp(pressedPos, initialLocalPos, k);
            yield return null;
        }

        if (visual != null)
            visual.localPosition = initialLocalPos;

        if (runtimeMaterial != null)
            runtimeMaterial.color = normalColor;

        isAnimating = false;

        yield return new WaitForSeconds(pressCooldown);
        isCooldown = false;
    }
}