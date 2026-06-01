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
