using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace XRCollab.Measurement.Turns
{
    /// <summary>
    /// Un solo turno a la vez entre los PCs: si otro participante inicia su turno, el turno en curso de este PC termina
    /// solo (TURN_END con <c>source=handover|next=&lt;PC&gt;</c>). Las reglas están en <see cref="TurnSync"/> (Core, con
    /// tests); esto solo pone el transporte. README en esta carpeta.
    ///
    /// Transporte: la red de Netcode que ya une host y clientes, con un mensaje con nombre (sin NetworkObject):
    /// «turno más nuevo conocido» (PC, clave). El host lo repite cada <see cref="HeartbeatSeconds"/> s, así que un aviso
    /// perdido, un cliente que se reconecta o una app que se reinició se ponen al día solos. El panel de sesión es un
    /// segundo camino (<c>turn latest PC CLAVE</c>) que funciona aunque el host esté caído.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TurnCoordinator : MonoBehaviour
    {
        private const string MsgLatest = "XRC.TurnLatest";
        private const float HeartbeatSeconds = 2f;
        private const string LogPrefix = "[TurnCoordinator] ";

        public TurnSync Sync { get; private set; }

        private TurnPad _pad;
        private CustomMessagingManager _registeredOn;
        private bool _wasConnected;
        private float _nextBeat;

        /// <summary>Nombre de este PC (el visor nativo no tiene uno propio: su id de dispositivo).</summary>
        public static string LocalPc =>
            (Application.platform == RuntimePlatform.Android ? SystemInfo.deviceUniqueIdentifier : Environment.MachineName).ToUpperInvariant();

        private void Awake()
        {
            _pad = GetComponent<TurnPad>();
            Sync = new TurnSync(LocalPc)
            {
                InTurn = () => _pad != null && _pad.InTurn,
                Yield = (pc, key) =>
                {
                    Debug.Log($"{LogPrefix}{pc} tomó el turno: termina el de este PC");
                    _pad.EndForHandover(pc, key);
                },
                Changed = () => _pad.SaveState(),
                SendToHost = (pc, key) => Send(NetworkManager.ServerClientId, pc, key),
                Broadcast = (pc, key) =>
                {
                    NetworkManager nm = NetworkManager.Singleton;
                    if (!Ready(nm) || !nm.IsServer) return;
                    foreach (ulong id in nm.ConnectedClientsIds)
                        if (id != NetworkManager.ServerClientId) Send(id, pc, key);
                },
                Reply = (id, pc, key) => Send(id, pc, key),
            };
        }

        private void Update()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (!Ready(nm)) { _registeredOn = null; _wasConnected = false; return; }

            // CustomMessagingManager se crea de nuevo en cada StartHost/StartClient: registrar en cada uno.
            if (nm.CustomMessagingManager != _registeredOn)
            {
                _registeredOn = nm.CustomMessagingManager;
                _registeredOn.RegisterNamedMessageHandler(MsgLatest, OnMessage);
                _wasConnected = false;
            }
            Sync.IsHost = nm.IsServer;

            if (nm.IsServer)
            {
                if (Time.unscaledTime >= _nextBeat) { _nextBeat = Time.unscaledTime + HeartbeatSeconds; Sync.OnHeartbeat(); }
                return;
            }
            bool connected = nm.IsConnectedClient;
            if (connected && !_wasConnected)
            {
                Debug.Log($"{LogPrefix}conectado al host: le cuento el turno más nuevo que conozco ({Describe()})");
                Sync.OnConnected();
            }
            _wasConnected = connected;
        }

        private static bool Ready(NetworkManager nm) => nm != null && nm.IsListening && nm.CustomMessagingManager != null;

        private void Send(ulong clientId, string pc, long key)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (!Ready(nm) || (!nm.IsServer && !nm.IsConnectedClient)) return;
            try
            {
                using (var w = new FastBufferWriter(128, Allocator.Temp))
                {
                    w.WriteValueSafe(pc);
                    w.WriteValueSafe(key);
                    nm.CustomMessagingManager.SendNamedMessage(MsgLatest, clientId, w);
                }
            }
            catch (Exception e)
            {
                // Un cliente que se desconectaba justo: lo arregla el próximo latido o la reconexión.
                Debug.LogWarning($"{LogPrefix}no se pudo mandar a {clientId}: {e.Message}");
            }
        }

        private void OnMessage(ulong sender, FastBufferReader r)
        {
            try
            {
                r.ReadValueSafe(out string pc);
                r.ReadValueSafe(out long key);
                Sync.OnReceive(sender, pc, key);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogPrefix}mensaje de {sender} ilegible: {e.Message}");
            }
        }

        public string Describe() => Sync.Ledger.Key > 0 ? $"{Sync.Ledger.Pc} {Sync.Ledger.Key}" : "ninguno";
    }
}
