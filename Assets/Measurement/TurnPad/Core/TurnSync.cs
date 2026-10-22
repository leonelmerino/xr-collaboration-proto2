using System;

namespace XRCollab.Measurement.Turns
{
    /// <summary>
    /// Reglas del turno único entre PCs, separadas del transporte (TurnCoordinator pone la red de Netcode y el panel; los
    /// tests de EditMode ponen una red simulada con cortes, reinicios y relojes corridos).
    ///  - Cliente: cuenta su turno más nuevo conocido al host al iniciar un turno, al (re)conectarse y cuando el host
    ///    sabe algo más viejo que él.
    ///  - Host: fusiona, y si cambió se lo cuenta a todos; además lo repite cada pocos segundos (latido), así un aviso
    ///    perdido o un cliente que recién se conecta se ponen al día solos.
    ///  - Cualquiera que esté en turno y se entere de un turno más nuevo de otro PC termina el suyo.
    /// </summary>
    public sealed class TurnSync
    {
        public readonly TurnLedger Ledger = new TurnLedger();
        public readonly string Me;
        public bool IsHost;

        public Func<bool> InTurn = () => false;
        /// <summary>Terminar el turno de este PC porque (pc, clave) lo tomó.</summary>
        public Action<string, long> Yield;
        /// <summary>Cambió el turno más nuevo conocido (guardar en disco).</summary>
        public Action Changed;
        public Action<string, long> SendToHost;
        public Action<string, long> Broadcast;
        /// <summary>Host → un solo cliente (que mandó algo más viejo de lo que sabe el host).</summary>
        public Action<ulong, string, long> Reply;

        public TurnSync(string me) { Me = me ?? ""; }

        /// <summary>Este PC inicia un turno. Devuelve su clave.</summary>
        public long StartTurn(long utcTicks)
        {
            long key = Ledger.Start(Me, utcTicks);
            Changed?.Invoke();
            Publish();
            return key;
        }

        /// <summary>Estado guardado al arrancar la app: lo retoma y, si ya había uno más nuevo de otro, cede.</summary>
        public void Restore(string pc, long key)
        {
            Ledger.Merge(pc, key);
            Reconcile();
        }

        public void Publish()
        {
            if (Ledger.Key <= 0) return;
            if (IsHost) Broadcast?.Invoke(Ledger.Pc, Ledger.Key);
            else SendToHost?.Invoke(Ledger.Pc, Ledger.Key);
        }

        /// <summary>Cliente recién conectado (o reconectado) al host.</summary>
        public void OnConnected() { if (!IsHost) Publish(); }

        /// <summary>Latido del host.</summary>
        public void OnHeartbeat() { if (IsHost) Publish(); }

        public void OnReceive(ulong sender, string pc, long key)
        {
            bool changed = Absorb(pc, key);
            if (IsHost)
            {
                if (changed) Publish();
                else if (Ledger.NewerThan(pc, key)) Reply?.Invoke(sender, Ledger.Pc, Ledger.Key);
            }
            else if (Ledger.NewerThan(pc, key)) Publish();
        }

        /// <summary>El panel de sesión vio un turno más nuevo en otro PC (camino de respaldo, sin la red del host).</summary>
        public void OnPanel(string pc, long key)
        {
            if (Absorb(pc, key)) Publish();
        }

        public void Reconcile()
        {
            if (Ledger.ShouldYield(InTurn(), Me)) Yield?.Invoke(Ledger.Pc, Ledger.Key);
        }

        private bool Absorb(string pc, long key)
        {
            if (!Ledger.Merge(pc, key)) return false;
            Changed?.Invoke();
            Reconcile();
            return true;
        }
    }
}
