using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// Bootstrap de NGO via teclado.
///
/// - H: arranca el host y empieza a broadcastear discovery en LAN.
/// - C: arranca como cliente. Espera hasta discoveryWaitSec a que LAN Discovery
///      encuentre un host. Si lo encuentra, usa esa IP automaticamente (sin tocar
///      el Inspector). Si no encuentra ninguno en el tiempo de espera y
///      fallbackToStaticIp=true, cae a la IP configurada en UnityTransport (util
///      para testing local con host y cliente en la misma maquina).
///
/// La IP del UnityTransport en el Inspector NO necesita ser la del host real.
/// Solo se usa como ultimo recurso si el discovery falla y fallbackToStaticIp=true.
/// </summary>
public class NetworkLauncher : MonoBehaviour
{
    [Header("Discovery")]
    [Tooltip("Segundos que el cliente espera a que LAN Discovery encuentre un host antes de " +
             "abandonar. Aumentar si la red es lenta o el host tarda en arrancar. " +
             "El broadcast del host llega cada 1.5 s, asi que con 6 s hay margen para 4 intentos.")]
    [SerializeField] private float discoveryWaitSec = 6f;

    [Tooltip("Si es true y el discovery no encuentra host en el timeout, el cliente conecta " +
             "igualmente a la IP estatica del UnityTransport (Inspector). " +
             "Util para testing en la misma maquina (127.0.0.1). " +
             "En produccion dejar en false para que el fallo sea visible.")]
    [SerializeField] private bool fallbackToStaticIp = false;

    [Header("HUD")]
    [Tooltip("Muestra un overlay en pantalla con el estado de discovery / conexion.")]
    [SerializeField] private bool showStatusHud = true;
    [SerializeField] private int hudFontSize = 15;

    // Estado interno para el HUD.
    private enum Status { Idle, Hosting, WaitingForHost, Connecting, Connected, Failed }
    private Status _status = Status.Idle;
    private string _statusDetail = "";

    private GUIStyle _boxStyle;
    private Coroutine _connectCoroutine;

    // ─────────────────────────────────────────────────────────────
    // Input
    // ─────────────────────────────────────────────────────────────

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.H)) StartHost();
        if (Input.GetKeyDown(KeyCode.C)) TryStartClient();
    }

    // Argumento de lanzamiento "-role host|client": equivale a presionar H o C, sin depender de que
    // la ventana tenga el foco (Start-LabSession lanza las apps en los PCs del laboratorio por SSH).
    private IEnumerator Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Build dentro del visor: sin teclado ni argumentos. Se une solo como cliente si hay un host en la red
        // (opcional); si no lo hay, la app sigue sola y registra todo localmente igual.
        StartCoroutine(HeadsetAutoJoin());
        yield break;
#else
        string[] args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-role");
        if (i < 0 || i + 1 >= args.Length) yield break;
        string role = args[i + 1].ToLowerInvariant();

        yield return new WaitForSeconds(1f);   // NetworkManager y LanDiscoveryService ya inicializados
        Debug.Log($"[NetworkLauncher] -role {role} desde la línea de comandos.");
        if (role == "host") StartHost();
        else if (role == "client") TryStartClient();
        else Debug.LogWarning($"[NetworkLauncher] -role desconocido: {role} (usar host o client)");
#endif
    }

    // ─────────────────────────────────────────────────────────────
    // Visor (build Android): unión opcional al host
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Archivo opcional con la IP del host ("192.168.1.10" o "192.168.1.10:7777"), para cuando el broadcast de
    /// LAN discovery no llega al visor. Se deja con adb en
    /// /sdcard/Android/data/&lt;paquete&gt;/files/host.txt (Application.persistentDataPath).
    /// </summary>
    public const string HostFileName = "host.txt";
    private const float HeadsetRetrySec = 10f;
    private const float HeadsetConnectTimeoutSec = 15f;

    private IEnumerator HeadsetAutoJoin()
    {
        yield return new WaitForSeconds(2f);   // NetworkManager y LanDiscoveryService ya inicializados
        var nm = NetworkManager.Singleton;
        if (nm == null) yield break;
        var ut = nm.GetComponent<UnityTransport>();
        Debug.Log("[NetworkLauncher] Visor: buscando un host en la red (opcional; los datos se registran igual).");

        while (true)
        {
            if (nm.IsListening) { yield return new WaitForSeconds(2f); continue; }   // conectado o intentando

            string ip = null;
            ushort port = ut != null ? ut.ConnectionData.Port : (ushort)7777;
            if (TryReadHostFile(out string fileIp, out ushort filePort))
            {
                ip = fileIp;
                if (filePort != 0) port = filePort;
            }
            else
            {
                var disc = LanDiscoveryService.Instance;
                float elapsed = 0f;
                while (disc != null && disc.UseLanDiscovery && elapsed < discoveryWaitSec)
                {
                    var host = disc.PickPreferredHost();
                    if (host != null) { ip = host.ip; port = (ushort)host.gamePort; break; }
                    SetStatus(Status.WaitingForHost, $"Buscando host en la red... ({elapsed:F0} / {discoveryWaitSec:F0} s)");
                    yield return new WaitForSeconds(0.5f);
                    elapsed += 0.5f;
                }
            }

            if (ip == null)
            {
                SetStatus(Status.Idle, "Sin host en la red: registrando solo en el visor");
                yield return new WaitForSeconds(HeadsetRetrySec);
                continue;
            }

            if (ut != null)
            {
                ut.ConnectionData.Address = ip;
                ut.ConnectionData.Port = port;
            }
            Debug.Log($"[NetworkLauncher] Visor: conectando a {ip}:{port}");
            SetStatus(Status.Connecting, $"Conectando a {ip}:{port}...");
            nm.StartClient();

            float wait = 0f;
            while (wait < HeadsetConnectTimeoutSec && nm.IsListening && !nm.IsConnectedClient)
            {
                yield return new WaitForSeconds(0.5f);
                wait += 0.5f;
            }
            if (nm.IsConnectedClient)
            {
                SetStatus(Status.Connected, $"Conectado a {ip}:{port}");
                while (nm.IsListening) yield return new WaitForSeconds(2f);   // hasta que se corte
                Debug.LogWarning("[NetworkLauncher] Visor: se cortó la conexión con el host; reintentando.");
            }
            else
            {
                Debug.LogWarning($"[NetworkLauncher] Visor: no se pudo conectar a {ip}:{port}; reintentando en {HeadsetRetrySec} s.");
                if (nm.IsListening) nm.Shutdown();
                SetStatus(Status.Failed, $"No se pudo conectar a {ip}:{port}; reintentando");
            }
            yield return new WaitForSeconds(HeadsetRetrySec);
        }
    }

    private static bool TryReadHostFile(out string ip, out ushort port)
    {
        ip = null;
        port = 0;
        try
        {
            string path = System.IO.Path.Combine(Application.persistentDataPath, HostFileName);
            if (!System.IO.File.Exists(path)) return false;
            string text = System.IO.File.ReadAllText(path).Trim();
            if (text.Length == 0) return false;
            int colon = text.LastIndexOf(':');
            if (colon > 0 && ushort.TryParse(text.Substring(colon + 1), out ushort p)) { port = p; text = text.Substring(0, colon); }
            if (!System.Net.IPAddress.TryParse(text, out _)) return false;
            ip = text;
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[NetworkLauncher] no se pudo leer {HostFileName}: {e.Message}");
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Host
    // ─────────────────────────────────────────────────────────────

    private void StartHost()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.IsListening) return;

        nm.StartHost();

        var disc = LanDiscoveryService.Instance;
        if (disc != null && disc.UseLanDiscovery)
        {
            int gamePort = GetGamePort(nm);
            disc.StartServer(gamePort);
            SetStatus(Status.Hosting, $"Host activo — anunciando en LAN puerto {gamePort}");
        }
        else
        {
            SetStatus(Status.Hosting, "Host activo — LAN discovery deshabilitado");
            Debug.Log("[NetworkLauncher] LAN discovery deshabilitado. El host no anuncia en la red.");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Cliente
    // ─────────────────────────────────────────────────────────────

    private void TryStartClient()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.IsListening) return;

        if (_connectCoroutine != null) StopCoroutine(_connectCoroutine);
        _connectCoroutine = StartCoroutine(ConnectClientCoroutine());
    }

    private IEnumerator ConnectClientCoroutine()
    {
        var nm  = NetworkManager.Singleton;
        var ut  = nm.GetComponent<UnityTransport>();
        var disc = LanDiscoveryService.Instance;

        // ── Con discovery ─────────────────────────────────────────
        if (disc != null && disc.UseLanDiscovery)
        {
            float elapsed = 0f;
            SetStatus(Status.WaitingForHost, $"Buscando host en LAN... (0.0 / {discoveryWaitSec:F0} s)");

            while (elapsed < discoveryWaitSec)
            {
                var host = disc.PickPreferredHost();
                if (host != null)
                {
                    // Host encontrado: aplicar IP/puerto y conectar.
                    if (ut != null)
                    {
                        ut.ConnectionData.Address = host.ip;
                        ut.ConnectionData.Port    = (ushort)host.gamePort;
                    }
                    Debug.Log($"[NetworkLauncher] Host descubierto: {host.hostName} @ {host.ip}:{host.gamePort} — conectando.");
                    SetStatus(Status.Connecting, $"Conectando a {host.hostName} ({host.ip}:{host.gamePort})...");
                    nm.StartClient();
                    yield break;
                }

                yield return new WaitForSeconds(0.25f);
                elapsed += 0.25f;
                SetStatus(Status.WaitingForHost,
                    $"Buscando host en LAN... ({elapsed:F1} / {discoveryWaitSec:F0} s)");
            }

            // ── Timeout ──────────────────────────────────────────
            if (fallbackToStaticIp && ut != null)
            {
                string staticAddr = $"{ut.ConnectionData.Address}:{ut.ConnectionData.Port}";
                Debug.LogWarning($"[NetworkLauncher] Discovery timeout ({discoveryWaitSec} s). " +
                                 $"Fallback a IP estatica: {staticAddr}");
                SetStatus(Status.Connecting, $"Sin host en LAN — fallback a IP estatica {staticAddr}");
                nm.StartClient();
            }
            else
            {
                string msg = $"No se encontro ningun host en LAN en {discoveryWaitSec:F0} s. " +
                              "Verificar que el host este activo y que el firewall permita UDP 7778.";
                Debug.LogError($"[NetworkLauncher] {msg}");
                SetStatus(Status.Failed, msg);
            }
        }
        // ── Sin discovery (modo manual) ───────────────────────────
        else
        {
            string addr = ut != null
                ? $"{ut.ConnectionData.Address}:{ut.ConnectionData.Port}"
                : "desconocida";
            Debug.Log($"[NetworkLauncher] LAN discovery deshabilitado. Conectando a IP estatica: {addr}");
            SetStatus(Status.Connecting, $"Conectando a {addr} (modo manual)...");
            nm.StartClient();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // HUD
    // ─────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!showStatusHud || _status == Status.Idle) return;

        EnsureStyle();

        Color bg = _status switch
        {
            Status.Hosting        => new Color(0.10f, 0.50f, 0.15f, 0.88f),
            Status.WaitingForHost => new Color(0.15f, 0.35f, 0.70f, 0.88f),
            Status.Connecting     => new Color(0.15f, 0.35f, 0.70f, 0.88f),
            Status.Connected      => new Color(0.10f, 0.55f, 0.15f, 0.88f),
            Status.Failed         => new Color(0.65f, 0.10f, 0.10f, 0.88f),
            _                     => new Color(0.20f, 0.20f, 0.20f, 0.88f),
        };

        string prefix = _status switch
        {
            Status.Hosting        => "[HOST]",
            Status.WaitingForHost => "[BUSCANDO]",
            Status.Connecting     => "[CONECTANDO]",
            Status.Connected      => "[CONECTADO]",
            Status.Failed         => "[ERROR]",
            _                     => "",
        };

        float w = 500f, h = 56f;
        float x = (Screen.width  - w) * 0.5f;
        float y =  Screen.height - h  - 24f;

        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = bg;
        GUI.Box(new Rect(x, y, w, h), $"{prefix}  {_statusDetail}", _boxStyle);
        GUI.backgroundColor = prevBg;
    }

    private void EnsureStyle()
    {
        if (_boxStyle != null) return;
        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize  = hudFontSize,
            alignment = TextAnchor.MiddleCenter,
            wordWrap  = true,
        };
        _boxStyle.normal.textColor = Color.white;
        _boxStyle.padding = new RectOffset(12, 12, 8, 8);
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private void SetStatus(Status s, string detail)
    {
        _status       = s;
        _statusDetail = detail;
        Debug.Log($"[NetworkLauncher] {s}: {detail}");
    }

    private static int GetGamePort(NetworkManager nm)
    {
        var ut = nm.GetComponent<UnityTransport>();
        return ut != null ? ut.ConnectionData.Port : 7777;
    }
}
