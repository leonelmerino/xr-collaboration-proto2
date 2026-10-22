using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

/// <summary>
/// Cliente del puente BioLab (tools/biolab-bridge/biolab_bridge.py, corre en ACQUISITION). Desde 2026-10-22 los
/// eventos de la app llegan a BioLab por aquí y no directo por UDP: directo nunca llegó ninguno (BioLab rechazaba el
/// payload con «invalid characters», y STIMULUS1 a veces no tiene ruta al router), y nadie se enteraba.
///  - <see cref="Send"/> no bloquea: deja el evento en una cola; un hilo propio lo entrega por TCP y espera la respuesta
///    (lo que dijo BioLab). Si se corta, reconecta y reintenta con el mismo id: el puente no lo repite en BioLab.
///  - Rutas en orden (cable, ZeroTier, Tailscale): usa la primera que conecta y vuelve a probar la primera cada 30 s.
///  - Cada 3 s pide el estado del puente: si BioLab escucha (archivo elegido) se ve en el panel.
///  - Un evento que no se pudo entregar en <see cref="MaxAgeSeconds"/> s queda solo en el events.csv local (EXPIRED).
/// Sin API de Unity en el hilo: los resultados se leen desde el hilo principal con <see cref="TryTakeResult"/>.
/// </summary>
public sealed class BioLabBridgeClient : IDisposable
{
    public const int DefaultPort = 47820;
    private const int ConnectTimeoutMs = 1500;
    private const int ReplyTimeoutMs = 3000;
    private const int StatusEverySeconds = 3;
    private const int RetryPrimaryEverySeconds = 30;
    public const int MaxAgeSeconds = 60;

    private sealed class Item { public string Id, Label, Detail, Utc; public DateTime Enqueued; }

    private readonly string[] _hosts;
    private readonly int[] _ports;
    private readonly string _pc;
    private readonly BlockingCollection<Item> _queue = new BlockingCollection<Item>(new ConcurrentQueue<Item>(), 5000);
    private readonly ConcurrentQueue<string[]> _results = new ConcurrentQueue<string[]>();
    private readonly Thread _thread;
    private volatile bool _stop;
    private TcpClient _tcp;
    private StreamReader _reader;
    private StreamWriter _writer;
    private int _hostIndex = -1;
    private DateTime _connectedAt;

    // Estado (lo lee AppHealth para el panel).
    public volatile bool Connected;
    public volatile bool BioLabListening;
    public volatile string Host = "";
    public volatile string LastReply = "";
    public volatile string LastError = "";
    public volatile string LastOkUtc = "";
    private int _ok, _failed;
    public int Ok => _ok;
    public int Failed => _failed;
    public int Pending => _queue.Count;

    /// <param name="hosts">«ip[:puerto];ip;…» en orden de preferencia</param>
    public BioLabBridgeClient(string hosts, string pc)
    {
        string[] parts = (hosts ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        _hosts = new string[parts.Length];
        _ports = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            int colon = p.LastIndexOf(':');
            _ports[i] = colon > 0 && int.TryParse(p.Substring(colon + 1), out int port) ? port : DefaultPort;
            _hosts[i] = colon > 0 ? p.Substring(0, colon) : p;
        }
        _pc = (pc ?? "").ToUpperInvariant();
        _thread = new Thread(Run) { IsBackground = true, Name = "BioLabBridge" };
        _thread.Start();
    }

    public string Targets => string.Join(";", _hosts);

    /// <summary>Hilo principal: deja el evento en la cola (no espera la red).</summary>
    public void Send(string label, string detail, string utc)
    {
        var item = new Item { Id = Guid.NewGuid().ToString("N"), Label = label ?? "", Detail = detail ?? "", Utc = utc ?? "", Enqueued = DateTime.UtcNow };
        if (!_queue.TryAdd(item)) LastError = "cola llena";
    }

    /// <summary>Resultado de un evento entregado (o vencido): etiqueta, detalle, respuesta de BioLab.</summary>
    public bool TryTakeResult(out string[] result) => _results.TryDequeue(out result);

    /// <summary>Al cerrar la app: hasta <paramref name="ms"/> para entregar lo pendiente.</summary>
    public void Flush(int ms)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(ms);
        while (_queue.Count > 0 && DateTime.UtcNow < until) Thread.Sleep(20);
    }

    public void Dispose()
    {
        _stop = true;
        _queue.CompleteAdding();
        _thread.Join(1500);
        Close();
    }

    // ---------------- hilo del puente ----------------

    private void Run()
    {
        DateTime nextStatus = DateTime.MinValue;
        while (!_stop)
        {
            Item item;
            try { if (!_queue.TryTake(out item, 300)) item = null; }
            catch (InvalidOperationException) { return; }   // CompleteAdding
            if (item != null) { Deliver(item); continue; }
            if (DateTime.UtcNow < nextStatus) continue;
            nextStatus = DateTime.UtcNow.AddSeconds(StatusEverySeconds);
            // Volver a la ruta preferida (p. ej. se enchufó el cable) cada tanto.
            if (_hostIndex > 0 && (DateTime.UtcNow - _connectedAt).TotalSeconds > RetryPrimaryEverySeconds) Close();
            string st = Request("{\"op\":\"status\"}");
            if (st != null) BioLabListening = Regex.IsMatch(st, "\"listening\":\\s*true");
        }
    }

    private void Deliver(Item it)
    {
        string json = "{\"op\":\"event\",\"id\":" + Q(it.Id) + ",\"pc\":" + Q(_pc) + ",\"label\":" + Q(it.Label) +
                      ",\"utc\":" + Q(it.Utc) + ",\"detail\":" + Q(it.Detail) + "}";
        int attempt = 0;
        while (!_stop)
        {
            if ((DateTime.UtcNow - it.Enqueued).TotalSeconds > MaxAgeSeconds)
            {
                Interlocked.Increment(ref _failed);
                _results.Enqueue(new[] { it.Label, it.Detail, "EXPIRED (puente sin respuesta: " + LastError + ")" });
                return;
            }
            string reply = Request(json);
            if (reply != null)
            {
                bool ok = Regex.IsMatch(reply, "\"ok\":\\s*true");
                string biolab = Field(reply, "biolab") ?? Field(reply, "error") ?? reply;
                // Solo cuenta lo que va a BioLab (los demás eventos el puente solo los registra: NOT_FORWARDED).
                if (biolab != "NOT_FORWARDED")
                {
                    LastReply = biolab;
                    if (ok && biolab.StartsWith("OK", StringComparison.Ordinal))
                    {
                        BioLabListening = true;
                        Interlocked.Increment(ref _ok);
                        LastOkUtc = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    }
                    else Interlocked.Increment(ref _failed);
                }
                _results.Enqueue(new[] { it.Label, it.Detail, biolab });
                return;
            }
            attempt++;
            Thread.Sleep(Math.Min(2000, 150 * attempt));
        }
    }

    /// <summary>Una línea al puente y su respuesta; null si no se pudo (la conexión se cierra y se reintenta).</summary>
    private string Request(string json)
    {
        try
        {
            if (!EnsureConnected()) return null;
            _writer.Write(json + "\n");
            _writer.Flush();
            string line = _reader.ReadLine();
            if (line == null) throw new IOException("el puente cerró la conexión");
            return line;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Close();
            return null;
        }
    }

    private bool EnsureConnected()
    {
        if (_tcp != null && _tcp.Connected) return true;
        Close();
        for (int i = 0; i < _hosts.Length && !_stop; i++)
        {
            var c = new TcpClient();
            try
            {
                IAsyncResult ar = c.BeginConnect(_hosts[i], _ports[i], null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs) || !c.Connected) { c.Close(); continue; }
                c.EndConnect(ar);
                c.NoDelay = true;
                NetworkStream s = c.GetStream();
                s.ReadTimeout = ReplyTimeoutMs;
                s.WriteTimeout = ReplyTimeoutMs;
                _tcp = c;
                _reader = new StreamReader(s, new UTF8Encoding(false));
                _writer = new StreamWriter(s, new UTF8Encoding(false));
                _hostIndex = i;
                _connectedAt = DateTime.UtcNow;
                Host = _hosts[i];
                Connected = true;
                return true;
            }
            catch (Exception e)
            {
                LastError = $"{_hosts[i]}: {e.Message}";
                c.Close();
            }
        }
        Connected = false;
        Host = "";
        return false;
    }

    private void Close()
    {
        try { _tcp?.Close(); } catch { /* ya cerrado */ }
        _tcp = null;
        _reader = null;
        _writer = null;
        _hostIndex = -1;
        Connected = false;
    }

    /// <summary>Objeto JSON para "biolab" del estado de la app (panel).</summary>
    public string HealthJson()
    {
        var c = CultureInfo.InvariantCulture;
        return "{\"bridge\":" + Q(Connected ? Host : "") + ",\"listening\":" + (Connected && BioLabListening ? "true" : "false") +
               ",\"ok\":" + _ok.ToString(c) + ",\"failed\":" + _failed.ToString(c) + ",\"pending\":" + Pending.ToString(c) +
               ",\"last\":" + Q(LastReply) + ",\"lastOk\":" + Q(LastOkUtc) + ",\"error\":" + Q(Connected ? "" : LastError) + "}";
    }

    private static string Field(string json, string name)
    {
        Match m = Regex.Match(json, "\"" + name + "\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
    }

    private static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s ?? "")
        {
            if (ch == '"') sb.Append("\\\"");
            else if (ch == '\\') sb.Append("\\\\");
            else if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }
}
