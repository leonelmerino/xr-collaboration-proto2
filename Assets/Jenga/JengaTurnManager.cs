using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gestor de turnos secuenciales para Jenga con integración BioLab y eye tracking.
///
/// SETUP:
/// - Colocar en un GameObject de la escena junto con NetworkObject.
/// - El host arranca los turnos presionando F3 (o llamando StartSession() desde código).
/// - JengaPokeInteractor y JengaRayGrabInteractor llaman NotifyBlockInteraction() y
///   NotifyBlockReleased() automáticamente.
///
/// FLUJO DE EVENTOS BIOLAB:
///   SESSION_START (automático vía AcquisitionEventManager.autoStartSession)
///   ↓
///   TASK_START (task="jenga", trial="T01") — al primer toque del jugador activo
///     BLOCK_TOUCH — por cada bloque adicional tocado en el mismo turno
///   TASK_END
///     con BLOCK_REMOVED — bloque soltado fuera de huella de la torre
///     con TOWER_COLLAPSE — bloque sin grab sale de huella
///     con WIN — BLOCK_REMOVED que vacía la torre completamente
///   ↓ (siguiente turno del siguiente jugador)
///   ...
///
/// CONSISTENCIA DE IDS:
///   - AcquisitionNodeConfig.nodeId debe coincidir con EyeTrackingSessionLogger.participantId
///     en cada máquina. Ambos loggers usan taskId="jenga" fijo y trialId="T01", "T02"...
///     actualizado dinámicamente al inicio de cada turno.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class JengaTurnManager : NetworkBehaviour
{
    public static JengaTurnManager Instance { get; private set; }

    // ─── Inspector ───────────────────────────────────────────────────────────

    [Header("References (auto-buscadas si quedan vacías)")]
    [SerializeField] private AcquisitionEventManager acquisitionManager;
    [SerializeField] private ExperimentEventLogger eventLogger;
    [SerializeField] private EyeTrackingSessionLogger eyeLogger;

    [Header("Identificación de tarea")]
    [Tooltip("Valor fijo de taskId en todos los eventos BioLab y en el eye tracker.")]
    [SerializeField] private string fixedTaskId = "jenga";

    [Header("Control de sesión (solo host)")]
    [Tooltip("El host presiona esta tecla para iniciar la ronda de turnos.")]
    [SerializeField] private KeyCode startSessionKey = KeyCode.F3;

    // ─── Network Variables (server-authoritative) ─────────────────────────────

    private NetworkVariable<ulong> _currentTurnClientId = new(
        ulong.MaxValue,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private NetworkVariable<int> _turnNumber = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private NetworkVariable<bool> _sessionActive = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private NetworkVariable<int> _initialBlockCount = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>Nombres de bloques removidos legítimamente (BLOCK_REMOVED). Excluidos del chequeo de colapso.</summary>
    private NetworkList<FixedString64Bytes> _removedBlocks;

    /// <summary>Registro ClientId → nodeId para que todos los clientes puedan mostrar el nombre del jugador activo.</summary>
    private NetworkList<ParticipantInfo> _participants;

    // ─── Estado local (no networked, por cliente) ─────────────────────────────

    private bool _turnHasStarted;  // TASK_START ya fue enviado para este turno
    private bool _turnEnded;       // TASK_END ya fue enviado; ignorar más eventos este turno
    private string _lastTouchedBlock = "";

    // Orden de turnos (host lo construye, no necesita replicarse; cada cliente lo infiere de CurrentTurnClientId)
    private List<ulong> _turnOrder = new();
    private int _turnOrderIndex;

    // ─── Eventos públicos (para UI y otros componentes) ──────────────────────

    /// <summary>Fired al cambiar el turno. (clientId, nodeId del nuevo turno, número de turno)</summary>
    public event System.Action<ulong, string, int> OnTurnChanged;

    /// <summary>Fired al iniciar (true) o finalizar (false) la sesión.</summary>
    public event System.Action<bool> OnSessionActiveChanged;

    // ─── Accessors públicos ──────────────────────────────────────────────────

    public NetworkVariable<ulong> CurrentTurnClientId => _currentTurnClientId;
    public NetworkVariable<int> TurnNumber => _turnNumber;
    public NetworkVariable<bool> SessionActive => _sessionActive;

    /// <summary>True solo en el cliente que tiene el turno actualmente.</summary>
    public bool IsMyTurn => IsSpawned && _currentTurnClientId.Value == NetworkManager.LocalClientId;

    // ─── Lifecycle ───────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[JengaTurnManager] Duplicate instance destroyed.");
            Destroy(gameObject);
            return;
        }
        Instance = this;

        _removedBlocks = new NetworkList<FixedString64Bytes>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        _participants = new NetworkList<ParticipantInfo>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public override void OnNetworkSpawn()
    {
        // Auto-búsqueda de referencias.
        if (acquisitionManager == null)
            acquisitionManager = AcquisitionEventManager.Instance
                ?? FindObjectOfType<AcquisitionEventManager>();
        if (eventLogger == null)
            eventLogger = FindObjectOfType<ExperimentEventLogger>();
        if (eyeLogger == null)
            eyeLogger = FindObjectOfType<EyeTrackingSessionLogger>();

        // Forzar taskId fijo en ambos loggers para consistencia con eye tracking.
        if (eventLogger != null) eventLogger.taskId = fixedTaskId;
        if (eyeLogger != null) eyeLogger.taskId = fixedTaskId;

        // Suscribirse a cambios de NetworkVariables.
        _currentTurnClientId.OnValueChanged += HandleTurnChanged;
        _sessionActive.OnValueChanged += (_, v) => OnSessionActiveChanged?.Invoke(v);
        _participants.OnListChanged += _ =>
        {
            // Refrescar UI si el registro llega tarde.
            string nodeId = GetNodeIdForClient(_currentTurnClientId.Value);
            OnTurnChanged?.Invoke(_currentTurnClientId.Value, nodeId, _turnNumber.Value);
        };

        // Registrar este cliente en el servidor.
        RegisterParticipantServerRpc(GetLocalNodeId());
    }

    public override void OnNetworkDespawn()
    {
        _currentTurnClientId.OnValueChanged -= HandleTurnChanged;
    }

    private void Update()
    {
        // Solo el host puede arrancar la sesión con la tecla.
        if (IsServer && Input.GetKeyDown(startSessionKey))
            StartSession();
    }

    // ─── API pública: control de sesión (host) ────────────────────────────────

    /// <summary>
    /// Inicia la ronda de turnos. Solo el host puede llamarlo.
    /// Requiere que los clientes ya estén conectados y spawneados.
    /// </summary>
    public void StartSession()
    {
        if (!IsServer) return;
        if (_sessionActive.Value)
        {
            Debug.LogWarning("[JengaTurnManager] StartSession: ya hay una sesión activa.");
            return;
        }

        // Contar bloques iniciales para detección de WIN.
        _initialBlockCount.Value = FindObjectsOfType<JengaBlockTag>().Length;

        // Construir orden de turnos: todos los clientes conectados en orden de conexión.
        _turnOrder.Clear();
        foreach (var id in NetworkManager.ConnectedClientsIds)
            _turnOrder.Add(id);

        _turnOrderIndex = 0;
        _sessionActive.Value = true;

        Debug.Log($"[JengaTurnManager] Sesión iniciada. Jugadores: {_turnOrder.Count}, Bloques: {_initialBlockCount.Value}");

        if (_turnOrder.Count > 0)
            AdvanceTurn_Server();
        else
            Debug.LogWarning("[JengaTurnManager] StartSession: ningún cliente conectado.");
    }

    // ─── API pública: llamada por los interactores ─────────────────────────────

    /// <summary>
    /// Llamar cuando el jugador local toca cualquier bloque (poke o grab).
    /// Solo actúa si es el turno del jugador local.
    /// </summary>
    public void NotifyBlockInteraction(string blockTag)
    {
        if (!IsSpawned || !IsMyTurn || _turnEnded) return;

        _lastTouchedBlock = blockTag;
        JengaTowerMonitor.Instance?.SetLastTouchedBlock(blockTag);

        if (!_turnHasStarted)
        {
            // Primera interacción del turno → TASK_START.
            _turnHasStarted = true;
            SyncTrialContext();
            acquisitionManager?.BeginTask(fixedTaskId);
            Debug.Log($"[JengaTurnManager] TASK_START turno={_turnNumber.Value} bloque={blockTag}");
        }
        else
        {
            // Interacción posterior → BLOCK_TOUCH.
            acquisitionManager?.EmitInteractionEvent("BLOCK_TOUCH", "block", blockTag);
            Debug.Log($"[JengaTurnManager] BLOCK_TOUCH bloque={blockTag}");
        }
    }

    /// <summary>
    /// Llamar cuando el jugador local suelta un bloque (solo ray grab tiene release).
    /// isOutsideFootprint: resultado de JengaTowerMonitor.IsOutsideFootprint() en el momento del release.
    /// </summary>
    public void NotifyBlockReleased(string blockTag, bool isOutsideFootprint)
    {
        if (!IsSpawned || !IsMyTurn || !_turnHasStarted || _turnEnded) return;

        if (!isOutsideFootprint)
        {
            // Bloque soltado dentro de la huella → sigue el juego; el turno continúa.
            return;
        }

        _turnEnded = true;

        // Verificar WIN: este bloque es el último de la torre.
        bool isWin = _initialBlockCount.Value > 0 &&
                     (_removedBlocks.Count + 1) >= _initialBlockCount.Value;

        if (isWin)
        {
            acquisitionManager?.EmitInteractionEvent("WIN", "block", blockTag);
            Debug.Log($"[JengaTurnManager] WIN! Último bloque removido: {blockTag}");
        }

        acquisitionManager?.EmitInteractionEvent("BLOCK_REMOVED", "block", blockTag);
        acquisitionManager?.EndTask();
        Debug.Log($"[JengaTurnManager] BLOCK_REMOVED bloque={blockTag} win={isWin}");

        // Avisar al servidor para que avance el turno (o cierre la sesión si WIN).
        BlockRemovedServerRpc(blockTag, isWin);
    }

    /// <summary>
    /// Llamar cuando JengaTowerMonitor detecta un bloque sin grab fuera de la huella.
    /// Solo actúa en el cliente del jugador con el turno activo.
    /// </summary>
    public void NotifyTowerCollapse(string collapsingBlockTag)
    {
        if (!IsSpawned || !IsMyTurn || !_turnHasStarted || _turnEnded) return;

        // Ignorar si el bloque ya fue removido legítimamente (podría estar fuera de huella desde antes).
        if (IsBlockRemoved(collapsingBlockTag)) return;

        _turnEnded = true;

        // Reportar el último bloque tocado en este turno como referencia del colapso.
        string refBlock = string.IsNullOrEmpty(_lastTouchedBlock) ? collapsingBlockTag : _lastTouchedBlock;
        acquisitionManager?.EmitInteractionEvent("TOWER_COLLAPSE", "block", refBlock);
        acquisitionManager?.EndTask();
        Debug.Log($"[JengaTurnManager] TOWER_COLLAPSE: colapsado={collapsingBlockTag} últimoTocado={_lastTouchedBlock}");

        TowerCollapseServerRpc();
    }

    // ─── ServerRpcs ──────────────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    private void RegisterParticipantServerRpc(string nodeId, ServerRpcParams rpc = default)
    {
        ulong senderId = rpc.Receive.SenderClientId;

        // Actualizar si ya existe, agregar si no.
        for (int i = 0; i < _participants.Count; i++)
        {
            if (_participants[i].ClientId == senderId)
            {
                var updated = _participants[i];
                updated.NodeId = nodeId;
                _participants[i] = updated;
                Debug.Log($"[JengaTurnManager] Participante actualizado: {nodeId} (id={senderId})");
                return;
            }
        }

        _participants.Add(new ParticipantInfo { ClientId = senderId, NodeId = nodeId });
        Debug.Log($"[JengaTurnManager] Participante registrado: {nodeId} (id={senderId})");
    }

    [ServerRpc(RequireOwnership = false)]
    private void BlockRemovedServerRpc(string blockTag, bool isWin, ServerRpcParams rpc = default)
    {
        ulong senderId = rpc.Receive.SenderClientId;
        if (senderId != _currentTurnClientId.Value)
        {
            Debug.LogWarning($"[JengaTurnManager] BlockRemovedServerRpc ignorado: sender={senderId} != turn={_currentTurnClientId.Value}");
            return;
        }

        // Registrar el bloque como removido para que el monitor de colapso lo ignore.
        _removedBlocks.Add(new FixedString64Bytes(blockTag));

        if (isWin)
        {
            // WIN: la sesión termina. Mantener CurrentTurnClientId para que la UI muestre al ganador.
            _sessionActive.Value = false;
            Debug.Log($"[JengaTurnManager] Servidor: WIN del cliente {senderId}. Sesión finalizada.");
        }
        else
        {
            // Turno normal terminado: pasar al siguiente jugador.
            AdvanceTurn_Server();
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void TowerCollapseServerRpc(ServerRpcParams rpc = default)
    {
        ulong senderId = rpc.Receive.SenderClientId;
        if (senderId != _currentTurnClientId.Value)
        {
            Debug.LogWarning($"[JengaTurnManager] TowerCollapseServerRpc ignorado: sender={senderId} != turn={_currentTurnClientId.Value}");
            return;
        }

        // Colapso: sesión termina. CurrentTurnClientId = ulong.MaxValue señala "fin por colapso" a la UI.
        _sessionActive.Value = false;
        _currentTurnClientId.Value = ulong.MaxValue;
        Debug.Log("[JengaTurnManager] Servidor: Torre colapsada. Sesión finalizada.");
    }

    // ─── Helpers internos ─────────────────────────────────────────────────────

    private void AdvanceTurn_Server()
    {
        if (!IsServer || _turnOrder.Count == 0) return;

        _turnOrderIndex %= _turnOrder.Count;
        _currentTurnClientId.Value = _turnOrder[_turnOrderIndex];
        _turnNumber.Value++;
        _turnOrderIndex++;

        Debug.Log($"[JengaTurnManager] Turno avanzado → cliente {_currentTurnClientId.Value} (turno {_turnNumber.Value})");
    }

    private void HandleTurnChanged(ulong _, ulong newClientId)
    {
        // Resetear estado local del turno anterior.
        _turnHasStarted = false;
        _turnEnded = false;
        _lastTouchedBlock = "";

        // Sincronizar trialId en los loggers locales.
        SyncTrialContext();

        string nodeId = GetNodeIdForClient(newClientId);
        OnTurnChanged?.Invoke(newClientId, nodeId, _turnNumber.Value);

        Debug.Log($"[JengaTurnManager] Turno local cambiado → {nodeId} (turno {_turnNumber.Value})");
    }

    /// <summary>Actualiza trialId en ExperimentEventLogger y EyeTrackingSessionLogger.</summary>
    private void SyncTrialContext()
    {
        string trialId = $"T{_turnNumber.Value:D2}";
        if (eventLogger != null) eventLogger.trialId = trialId;
        if (eyeLogger != null) eyeLogger.trialId = trialId;
    }

    /// <summary>Devuelve true si el bloque fue removido legítimamente en algún turno anterior.</summary>
    public bool IsBlockRemoved(string blockTag)
    {
        for (int i = 0; i < _removedBlocks.Count; i++)
            if (_removedBlocks[i] == blockTag) return true;
        return false;
    }

    /// <summary>Devuelve el nodeId del participante con ese ClientId, o un fallback legible.</summary>
    public string GetNodeIdForClient(ulong clientId)
    {
        for (int i = 0; i < _participants.Count; i++)
            if (_participants[i].ClientId == clientId)
                return _participants[i].NodeId.ToString();

        return clientId == ulong.MaxValue ? "—" : $"CLIENT_{clientId}";
    }

    private string GetLocalNodeId()
    {
        var cfg = FindObjectOfType<AcquisitionNodeConfig>();
        if (cfg != null) return cfg.nodeId;
        return $"CLIENT_{NetworkManager.LocalClientId}";
    }
}
