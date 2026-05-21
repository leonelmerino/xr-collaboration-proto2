/// <summary>
/// Registro de un host descubierto en LAN via UDP broadcast.
/// Inmutable desde el punto de vista del consumidor; rellenado por LanDiscoveryService.
/// </summary>
public class DiscoveryRecord
{
    public string ip;
    public int gamePort;
    public string hostName;
    public string sessionId;
    public float lastSeenLocalTime;
}
