using TMPro;
using UnityEngine;

/// <summary>
/// Panel VR world-space que muestra el turno actual del juego Jenga.
///
/// SETUP:
/// 1. Crear un Canvas (Render Mode = World Space) en la escena.
/// 2. Posicionarlo cerca de la mesa Jenga donde ambos jugadores puedan verlo.
/// 3. Agregar un TMP_Text dentro del Canvas.
/// 4. Agregar este componente al mismo Canvas o a cualquier GameObject.
/// 5. Asignar turnText y, opcionalmente, turnManager (se auto-busca si queda vacío).
///
/// Modos de posicionamiento:
///   Fixed    — el panel está fijo en el mundo; rota para mirar a la cámara (billboard).
///   FollowCamera — el panel se mueve delante del HMD en cada frame (más intrusivo).
/// </summary>
public class TurnIndicatorUI : MonoBehaviour
{
    public enum DisplayMode { Fixed, FollowCamera }

    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Referencias")]
    [Tooltip("TMP_Text donde se muestra el turno. Puede estar en otro GameObject.")]
    [SerializeField] private TMP_Text turnText;
    [SerializeField] private JengaTurnManager turnManager;
    [Tooltip("Cámara a seguir (o billboard hacia). Default: Camera.main.")]
    [SerializeField] private Camera targetCamera;

    [Header("Posicionamiento")]
    [SerializeField] private DisplayMode mode = DisplayMode.Fixed;

    [Header("Follow Camera (solo si mode=FollowCamera)")]
    [SerializeField] private float followDistance = 1.2f;
    [SerializeField] private float followVerticalOffset = -0.25f;

    [Header("Textos")]
    [Tooltip("Texto mostrado antes de que el host inicie la sesión (F3).")]
    [SerializeField] private string waitingText = "Esperando inicio...\n<size=70%>(Host: F3)</size>";
    [SerializeField] private string turnLabelPrefix = "Turno";
    [SerializeField] private string winLabel = "¡Ganó!";
    [SerializeField] private string collapseLabel = "Torre caída\nPartida terminada";

    // ─── Estado interno ───────────────────────────────────────────────────────

    private bool _sessionEnded;
    private bool _wasWin;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Start()
    {
        if (targetCamera == null) targetCamera = Camera.main;

        if (turnManager == null)
            turnManager = JengaTurnManager.Instance ?? FindObjectOfType<JengaTurnManager>();

        if (turnManager != null)
        {
            turnManager.OnTurnChanged += HandleTurnChanged;
            turnManager.OnSessionActiveChanged += HandleSessionActiveChanged;
        }
        else
        {
            Debug.LogWarning("[TurnIndicatorUI] JengaTurnManager no encontrado en escena.");
        }

        SetText(waitingText);
    }

    private void OnDestroy()
    {
        if (turnManager == null) return;
        turnManager.OnTurnChanged -= HandleTurnChanged;
        turnManager.OnSessionActiveChanged -= HandleSessionActiveChanged;
    }

    private void LateUpdate()
    {
        if (targetCamera == null) return;

        if (mode == DisplayMode.Fixed)
        {
            // Billboard: el +Z del canvas apunta hacia la cámara para que el texto sea legible.
            Vector3 toCamera = targetCamera.transform.position - transform.position;
            if (toCamera.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(toCamera.normalized);
        }
        else // FollowCamera
        {
            Vector3 fwd = targetCamera.transform.forward;
            Vector3 up  = targetCamera.transform.up;
            transform.position = targetCamera.transform.position
                + fwd * followDistance
                + up  * followVerticalOffset;
            transform.rotation = Quaternion.LookRotation(fwd, up);
        }
    }

    // ─── Callbacks ────────────────────────────────────────────────────────────

    private void HandleTurnChanged(ulong clientId, string nodeId, int turnNumber)
    {
        if (clientId == ulong.MaxValue)
        {
            // currentTurnClientId = ulong.MaxValue → colapso (ver TowerCollapseServerRpc).
            SetText(collapseLabel);
            return;
        }

        // Turno normal o WIN todavía no confirmado por HandleSessionActiveChanged.
        SetText($"{turnLabelPrefix} {turnNumber}\n<b>{nodeId}</b>");
    }

    private void HandleSessionActiveChanged(bool active)
    {
        if (active)
        {
            SetText("Iniciando...");
            _sessionEnded = false;
            return;
        }

        // Sesión terminada.
        _sessionEnded = true;

        // Si currentTurnClientId != ulong.MaxValue → la sesión terminó por WIN.
        // (ulong.MaxValue significa colapso, ya manejado por HandleTurnChanged.)
        ulong winner = turnManager.CurrentTurnClientId.Value;
        if (winner != ulong.MaxValue)
        {
            string nodeId = turnManager.GetNodeIdForClient(winner);
            SetText($"{winLabel}\n<b>{nodeId}</b>");
        }
        // else: el texto de colapso ya fue puesto por HandleTurnChanged.
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void SetText(string content)
    {
        if (turnText != null)
            turnText.text = content;
    }
}
