using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class TrackerDebug : MonoBehaviour
{
    void Start() => ScanDevices();

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.T)) ScanDevices();
    }

    void ScanDevices()
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevices(devices);

        int tracked = 0;
        foreach (var d in devices)
        {
            bool isTracked = false;
            d.TryGetFeatureValue(CommonUsages.isTracked, out isTracked);

            if (d.name.Contains("Ultimate Tracker") || d.name.Contains("Tracker"))
            {
                Vector3 pos = Vector3.zero;
                d.TryGetFeatureValue(CommonUsages.devicePosition, out pos);

                Debug.Log($"[Tracker] '{d.name}' | tracked={isTracked} | pos={pos.ToString("F2")}");
                if (isTracked) tracked++;
            }
        }
        Debug.Log($"[Tracker] Total tracked: {tracked}");
    }
}