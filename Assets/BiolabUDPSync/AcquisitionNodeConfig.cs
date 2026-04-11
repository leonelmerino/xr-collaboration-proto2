using UnityEngine;

public enum AcquisitionNodeRole
{
    Host,
    Client,
    Helper,
    AcquisitionMockOnly
}

public class AcquisitionNodeConfig : MonoBehaviour
{
    [Header("Role")]
    public AcquisitionNodeRole role = AcquisitionNodeRole.Host;
    public string nodeId = "VR_HOST";

    [Header("Acquisition Endpoint")]
    public string acquisitionIp = "127.0.0.1";
    public int acquisitionPort = 1776;
    public int responseTimeoutMs = 1000;

    [Header("Behavior")]
    public bool requireAcquisitionForSessionStart = false;
    public bool useEmbeddedAcquisitionMock = true;

    public bool IsHost => role == AcquisitionNodeRole.Host;
    public bool IsClient => role == AcquisitionNodeRole.Client;
    public bool IsHelper => role == AcquisitionNodeRole.Helper;
    public bool IsMockOnly => role == AcquisitionNodeRole.AcquisitionMockOnly;
}