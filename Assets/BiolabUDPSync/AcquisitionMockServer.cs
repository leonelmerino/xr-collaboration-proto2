using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class AcquisitionMockServer : MonoBehaviour
{
    public int listenPort = 1776;
    public bool autoStart = false;
    public string mockLogFileName = "acquisition_mock_log.txt";

    private UdpClient udpServer;
    private Thread serverThread;
    private volatile bool isRunning;
    private bool acquisitionStarted;
    private string logPath;

    private void Start()
    {
        logPath = Path.Combine(Application.persistentDataPath, mockLogFileName);

        if (autoStart)
            StartServer();
    }

    public void StartServer()
    {
        if (isRunning) return;

        isRunning = true;
        serverThread = new Thread(ServerLoop);
        serverThread.IsBackground = true;
        serverThread.Start();

        Debug.Log($"[AcquisitionMockServer] Started on UDP {listenPort}");
    }

    public void StopServer()
    {
        isRunning = false;

        try { udpServer?.Close(); } catch { }
        udpServer = null;

        if (serverThread != null && serverThread.IsAlive)
            serverThread.Join(500);

        Debug.Log("[AcquisitionMockServer] Stopped");
    }

    private void OnApplicationQuit()
    {
        StopServer();
    }

    private void ServerLoop()
    {
        try
        {
            udpServer = new UdpClient(listenPort);
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (isRunning)
            {
                byte[] data = udpServer.Receive(ref remoteEP);
                string message = Encoding.ASCII.GetString(data).Trim();

                string response = HandleMessage(message);

                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                udpServer.Send(responseBytes, responseBytes.Length, remoteEP);

                AppendLog($"{DateTime.UtcNow:o} | FROM {remoteEP} | RX: {message} | TX: {response}");
            }
        }
        catch (SocketException)
        {
            if (isRunning)
                Debug.LogWarning("[AcquisitionMockServer] Socket closed or interrupted.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[AcquisitionMockServer] {ex}");
        }
    }

    private string HandleMessage(string message)
    {
        if (message == "PING")
            return "PONG";

        if (message == "START")
        {
            if (acquisitionStarted)
                return "ERROR Acquisition already started";

            acquisitionStarted = true;
            return "OK";
        }

        if (message == "STOP")
        {
            if (!acquisitionStarted)
                return "ERROR Acquisition not started";

            acquisitionStarted = false;
            return "OK";
        }

        if (message.StartsWith("E:"))
            return "OK";

        return "ERROR Unknown command";
    }

    private void AppendLog(string line)
    {
        try
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AcquisitionMockServer] Failed to write log: {ex.Message}");
        }
    }
}