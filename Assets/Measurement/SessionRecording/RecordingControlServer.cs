using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Canal de control de la grabación desde afuera de la app: TCP solo en 127.0.0.1 (nada de red, ni SSH, ni
    /// lógica del laboratorio dentro de la app). Una herramienta local (tools\lab-remote\Send-RecordingCommand.ps1)
    /// manda una línea (<see cref="RecordingCommand"/>) y recibe una línea JSON (<see cref="RecordingStatus"/>).
    /// La red entre PCs (SSH, horario de inicio común) queda en las herramientas del laboratorio.
    ///
    /// Las conexiones se atienden en un hilo propio; cada orden se ejecuta en el hilo principal en
    /// <see cref="Pump"/> (Unity no es thread-safe) y su respuesta vuelve al cliente.
    /// </summary>
    public sealed class RecordingControlServer : IDisposable
    {
        private const string LogPrefix = "[RecordingControl] ";
        private const int ResponseTimeoutMs = 3000;
        private const int MaxLineLength = 1024;

        private sealed class Request
        {
            public string Line;
            public string Response;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private readonly ConcurrentQueue<Request> _pending = new ConcurrentQueue<Request>();
        private readonly TcpListener _listener;
        private readonly Thread _acceptThread;
        private volatile bool _disposed;

        public int Port { get; }

        private RecordingControlServer(int port)
        {
            Port = port;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "RecordingControl accept" };
            _acceptThread.Start();
        }

        /// <returns>null si el puerto está ocupado (otra app abierta) u otro error: la grabación sigue sin control externo.</returns>
        public static RecordingControlServer TryStart(int port)
        {
            try
            {
                var server = new RecordingControlServer(port);
                Debug.Log($"{LogPrefix}escuchando en 127.0.0.1:{port}");
                return server;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogPrefix}no se pudo abrir 127.0.0.1:{port} ({e.Message}): la grabación no se podrá controlar desde afuera");
                return null;
            }
        }

        /// <summary>Hilo principal: ejecuta las órdenes pendientes con <paramref name="handler"/> (línea → JSON).</summary>
        public void Pump(Func<string, string> handler)
        {
            while (_pending.TryDequeue(out Request request))
            {
                try { request.Response = handler(request.Line); }
                catch (Exception e) { request.Response = ErrorJson("error interno: " + e.Message); }
                request.Done.Set();
            }
        }

        private void AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) { if (_disposed) return; Thread.Sleep(100); continue; }
                var t = new Thread(() => Serve(client)) { IsBackground = true, Name = "RecordingControl client" };
                t.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 10_000;
                    NetworkStream stream = client.GetStream();
                    var reader = new StreamReader(stream, new UTF8Encoding(false));
                    var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    string line;
                    while (!_disposed && (line = reader.ReadLine()) != null)
                    {
                        if (line.Length > MaxLineLength) { writer.WriteLine(ErrorJson("línea demasiado larga")); continue; }
                        if (line.Trim().Length == 0) continue;
                        var request = new Request { Line = line };
                        _pending.Enqueue(request);
                        writer.WriteLine(request.Done.Wait(ResponseTimeoutMs)
                            ? request.Response
                            : ErrorJson("la app no respondió a tiempo (¿cargando o pausada?)"));
                    }
                }
                catch (Exception) { /* cliente se fue o timeout de lectura: nada que hacer */ }
            }
        }

        public static string ErrorJson(string message) =>
            JsonUtility.ToJson(new RecordingStatus { ok = false, error = message, state = "", computer = Environment.MachineName });

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _listener.Stop(); }
            catch (Exception) { }
            while (_pending.TryDequeue(out Request request))
            {
                request.Response = ErrorJson("la app se está cerrando");
                request.Done.Set();
            }
        }
    }
}
