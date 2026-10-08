using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

public class BioLabUdpClient
{
    private readonly string ipAddress;
    private readonly int port;
    private readonly int timeoutMs;

    public BioLabUdpClient(string ipAddress, int port, int timeoutMs = 1000)
    {
        this.ipAddress = ipAddress;
        this.port = port;
        this.timeoutMs = timeoutMs;
    }

    public Task<string> PingAsync()
    {
        return SendCommandAsync("PING");
    }

    public Task<string> StartAcquisitionAsync()
    {
        return SendCommandAsync("START");
    }

    public Task<string> StopAcquisitionAsync()
    {
        return SendCommandAsync("STOP");
    }

    public Task<string> SendEventAsync(string eventSource, string value)
    {
        string safeSource = SanitizeField(eventSource);
        string safeValue = SanitizeField(value);
        return SendCommandAsync($"E:{safeSource},{safeValue},0");
    }

    public async Task<string> SendCommandAsync(string command)
    {
        using UdpClient udp = new UdpClient();
        udp.Client.ReceiveTimeout = timeoutMs;
        udp.Connect(ipAddress, port);

        byte[] bytes = Encoding.ASCII.GetBytes(command);
        await udp.SendAsync(bytes, bytes.Length);

        try
        {
            // ReceiveAsync ignora ReceiveTimeout: sin esto, si BioLab no responde el PING nunca termina.
            Task<UdpReceiveResult> receive = udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(timeoutMs)) != receive)
            {
                _ = receive.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                return "TIMEOUT_OR_SOCKET_ERROR: no reply";
            }
            UdpReceiveResult result = await receive;
            return Encoding.ASCII.GetString(result.Buffer).Trim();
        }
        catch (SocketException ex)
        {
            return $"TIMEOUT_OR_SOCKET_ERROR: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private string SanitizeField(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "EMPTY";

        return input.Replace(",", "_").Replace(":", "_").Replace(" ", "_");
    }
}