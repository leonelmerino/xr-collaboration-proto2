using UnityEngine;

[CreateAssetMenu(fileName = "AcquisitionConfig", menuName = "XR/Acquisition Config")]
public class AcquisitionConfig : ScriptableObject
{
    [Header("Mode")]
    public bool useMockInsteadOfBioLab = false;

    [Header("Acquisition Endpoint")]
    public string acquisitionIp = "192.168.0.100";
    public int acquisitionPort = 1776;
    public int receiveTimeoutMs = 1000;

    [Header("Node Identity")]
    public string nodeId = "VR_HOST";

    [Header("Behavior")]
    public bool requireAcquisitionForSessionStart = false;
    public bool sendSyncMarkers = true;
}