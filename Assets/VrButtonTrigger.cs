using UnityEngine;

public class VrButtonTrigger : MonoBehaviour
{
    [SerializeField] private VrTaskButton taskButton;

    private bool isFingerInside = false;

    private void Reset()
    {
        taskButton = GetComponent<VrTaskButton>();
    }

    private void Awake()
    {
        if (taskButton == null)
            taskButton = GetComponent<VrTaskButton>();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (taskButton == null) return;

        if (other.GetComponent<FingerButtonActivator>() == null) return;

        if (isFingerInside) return;

        isFingerInside = true;
        taskButton.Press();
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.GetComponent<FingerButtonActivator>() == null) return;

        isFingerInside = false;
    }
}