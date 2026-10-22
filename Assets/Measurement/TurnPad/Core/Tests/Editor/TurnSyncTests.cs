using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace XRCollab.Measurement.Turns.Tests
{
    public class TurnSyncTests
    {
        private const long Ms = TimeSpan.TicksPerMillisecond;
        private static readonly long T0 = new DateTime(2026, 10, 22, 18, 0, 0, DateTimeKind.Utc).Ticks;

        [Test]
        public void Ledger_LaterKeyWins_TieBrokenByPc_MergeIsIdempotent()
        {
            var l = new TurnLedger();
            Assert.IsTrue(l.Merge("STIMULUS2", T0));
            Assert.IsFalse(l.Merge("STIMULUS2", T0));
            Assert.IsFalse(l.Merge("STIMULUS1", T0 - 1));
            Assert.IsTrue(l.Merge("STIMULUS3", T0));        // empate: nombre mayor
            Assert.IsFalse(l.Merge("STIMULUS1", T0));
            Assert.IsFalse(l.Merge("", T0 + 5));
            Assert.IsFalse(l.Merge("STIMULUS1", 0));
            Assert.AreEqual("STIMULUS3", l.Pc);
            Assert.IsTrue(l.ShouldYield(true, "STIMULUS1"));
            Assert.IsFalse(l.ShouldYield(true, "STIMULUS3"));
            Assert.IsFalse(l.ShouldYield(false, "STIMULUS1"));
            Assert.IsFalse(new TurnLedger().ShouldYield(true, "STIMULUS1"));
        }

        [Test]
        public void Ledger_StartAfterAKnownTurn_WinsEvenWithAClockFarBehind()
        {
            var a = new TurnLedger();
            var b = new TurnLedger();
            a.Start("A", T0);
            b.Merge(a.Pc, a.Key);
            long kb = b.Start("B", T0 - TimeSpan.FromDays(400).Ticks);   // reloj de B en 2025
            Assert.Greater(kb, a.Key);
            a.Merge(b.Pc, b.Key);
            Assert.IsTrue(a.ShouldYield(true, "A"));
        }

        [Test]
        public void Snapshot_RoundTrip_AndResumeRules()
        {
            var s = new TurnSnapshot
            {
                Day = "2026-10-22", Number = 7, InTurn = true, StartUtcTicks = T0, LastDuration = 12.5,
                LastUtc = "2026-10-22T18:00:00.000Z", Source = "hand_right", LatestPc = "STIMULUS2", LatestKey = T0 + 3,
            };
            TurnSnapshot r = TurnSnapshot.Parse(s.Serialize().Replace("\n", "\r\n") + "garbage\nn=x\n");
            Assert.AreEqual(s.Serialize(), TurnSnapshot.Parse(s.Serialize()).Serialize());
            Assert.AreEqual(0, r.Number);                    // la línea dañada «n=x» deja el valor por defecto
            Assert.AreEqual("STIMULUS2", r.LatestPc);
            Assert.AreEqual(T0 + 3, r.LatestKey);
            Assert.AreEqual("hand_right", r.Source);

            Assert.IsTrue(s.CanResume("2026-10-22", T0 + TimeSpan.FromMinutes(5).Ticks));
            Assert.IsFalse(s.CanResume("2026-10-23", T0 + TimeSpan.FromMinutes(5).Ticks));   // otro día
            Assert.IsFalse(s.CanResume("2026-10-22", T0 + TimeSpan.FromMinutes(31).Ticks));  // demasiado viejo
            Assert.IsFalse(s.CanResume("2026-10-22", T0 - TimeSpan.FromMinutes(1).Ticks));   // inicio en el futuro
            s.InTurn = false;
            Assert.IsFalse(s.CanResume("2026-10-22", T0 + 1));
            Assert.IsTrue(TurnSnapshot.Parse("").Day == "" && !TurnSnapshot.Parse(null).InTurn);
        }

        [Test]
        public void TwoPresses_WhileOneClientIsCutOff_TheLaterPressWins([Values] bool isolatedPressesLast)
        {
            var net = new Net(0, panel: false);
            Node a = net.Nodes[1], b = net.Nodes[2];
            net.Cut(a);
            net.Now = T0;
            net.Press(isolatedPressesLast ? b : a);
            net.Now = T0 + 800 * Ms;
            net.Press(isolatedPressesLast ? a : b);
            net.DeliverAll();
            Assert.IsTrue(a.InTurn && b.InTurn);   // sin red, cada uno con su turno
            net.Heal();
            Node winner = isolatedPressesLast ? a : b, loser = isolatedPressesLast ? b : a;
            Assert.IsTrue(winner.InTurn);
            Assert.IsFalse(loser.InTurn);
            Assert.AreEqual(1, loser.Yields);
        }

        [Test]
        public void AppRestart_ResumesTheTurn_AndYieldsIfSomeoneTookItMeanwhile()
        {
            var net = new Net(1, panel: false);
            Node host = net.Nodes[0], a = net.Nodes[1], b = net.Nodes[2];
            net.Press(a);
            net.DeliverAll();
            net.Down(a);
            net.Up(a);
            net.Heal();
            Assert.IsTrue(a.InTurn, "retoma su turno tras reiniciar la app");
            net.Down(a);
            net.Now += 2000 * Ms;
            net.Press(b);
            net.DeliverAll();
            net.Up(a);
            net.Heal();
            Assert.IsFalse(a.InTurn);
            Assert.IsTrue(b.InTurn);

            net.Down(host);           // el host se reinicia y pierde su memoria (sin archivo)
            net.Up(host, lost: true);
            net.Heal();
            Assert.IsTrue(b.InTurn);
            Assert.AreEqual("B", host.Sync.Ledger.Pc, "el host lo vuelve a saber por el cliente");
        }

        // Muchas historias al azar: presiones, cortes de red, reinicios de apps (con y sin archivo de estado), caídas del
        // host, mensajes desordenados, relojes corridos (±50 ms, y a veces uno ±10 min). Al volver la red (o solo con
        // el panel), todos quedan de acuerdo, hay a lo sumo un turno, y una presión nueva siempre se queda con el turno.
        [Test]
        public void RandomHistories_ConvergeToASingleTurn([Values] bool panelOnly)
        {
            int conflicts = 0;   // turnos cedidos: prueba que las historias sí producen choques (no solo casos fáciles)
            for (int seed = 0; seed < 400; seed++)
            {
                var net = new Net(seed, panel: true);
                Random rng = net.Rng;
                for (int step = 0; step < 300; step++)
                {
                    net.Now += rng.Next(1, 800) * Ms;
                    Node n = net.Nodes[rng.Next(net.Nodes.Count)];
                    switch (rng.Next(12))
                    {
                        case 0: case 1: case 2: net.Press(n); break;
                        case 3: case 4: case 5: net.DeliverOne(); break;
                        case 6: net.Heartbeat(); break;
                        case 7: if (n.Linked) net.Cut(n); else net.Link(n); break;
                        case 8: if (n.Up) net.Down(n); else net.Up(n, lost: rng.Next(6) == 0); break;
                        case 9: if (rng.Next(3) == 0) net.PanelTick(); break;
                        default: net.DeliverOne(); break;
                    }
                }
                if (panelOnly) net.HealByPanelOnly(); else net.Heal();
                net.AssertConsistent($"seed {seed} (panel only: {panelOnly})");

                Node p = net.Nodes[rng.Next(net.Nodes.Count)];
                if (p.InTurn) net.Press(p);
                net.Now += 50 * Ms;
                net.Press(p);
                if (panelOnly) net.HealByPanelOnly(); else net.Heal();
                net.AssertConsistent($"seed {seed} after a new press");
                Assert.IsTrue(p.InTurn, $"seed {seed}: la presión nueva de {p.Name} se queda con el turno");
                conflicts += net.Nodes.Sum(n => n.Yields);
            }
            Assert.Greater(conflicts, 1000);
        }

        // ---------------- red simulada ----------------

        private sealed class Node
        {
            public string Name;
            public ulong Id;
            public bool IsHost, InTurn, Up = true, Linked = true;
            public long ClockOffset;
            public int Yields;
            public TurnSync Sync;
        }

        private sealed class Msg { public Node From, To; public string Pc; public long Key; }

        private sealed class Net
        {
            public readonly List<Node> Nodes = new List<Node>();
            public readonly Random Rng;
            public long Now = T0;
            private readonly List<Msg> _inFlight = new List<Msg>();
            private readonly bool _panel;
            private Node Host => Nodes[0];

            public Net(int seed, bool panel)
            {
                Rng = new Random(seed);
                _panel = panel;
                foreach (string name in new[] { "H", "A", "B" })
                {
                    var n = new Node { Name = name, Id = (ulong)Nodes.Count, IsHost = name == "H", ClockOffset = Rng.Next(-50, 51) * Ms };
                    Nodes.Add(n);
                    Wire(n);
                }
                if (seed % 4 == 3) Nodes[Rng.Next(3)].ClockOffset += (Rng.Next(2) == 0 ? -1 : 1) * TimeSpan.FromMinutes(10).Ticks;
            }

            private void Wire(Node n, TurnLedger restoreFrom = null)
            {
                n.Sync = new TurnSync(n.Name)
                {
                    IsHost = n.IsHost,
                    InTurn = () => n.InTurn,
                    Yield = (pc, key) => { n.InTurn = false; n.Yields++; },
                    SendToHost = (pc, key) => Send(n, Host, pc, key),
                    Broadcast = (pc, key) => { foreach (Node c in Nodes.Where(c => !c.IsHost)) Send(Host, c, pc, key); },
                    Reply = (id, pc, key) => Send(Host, Nodes[(int)id], pc, key),
                };
                if (restoreFrom != null) n.Sync.Restore(restoreFrom.Pc, restoreFrom.Key);
            }

            private bool Connected(Node c) => c.IsHost ? c.Up : c.Up && c.Linked && Host.Up;

            private void Send(Node from, Node to, string pc, long key)
            {
                Node client = from.IsHost ? to : from;
                if (Connected(client) && Host.Up) _inFlight.Add(new Msg { From = from, To = to, Pc = pc, Key = key });
            }

            // Se corta la conexión de n (si es el host, la de todos): lo que iba por ella se pierde.
            private void Drop(Node n) => _inFlight.RemoveAll(m => n.IsHost || m.From == n || m.To == n);

            public void Press(Node n)
            {
                if (!n.Up) return;
                if (n.InTurn) { n.InTurn = false; return; }
                n.InTurn = true;
                n.Sync.StartTurn(Now + n.ClockOffset);
            }

            public void DeliverOne()
            {
                if (_inFlight.Count == 0) return;
                int i = Rng.Next(_inFlight.Count);   // en cualquier orden
                Msg m = _inFlight[i];
                _inFlight.RemoveAt(i);
                m.To.Sync.OnReceive(m.From.Id, m.Pc, m.Key);
            }

            public void DeliverAll()
            {
                for (int guard = 0; _inFlight.Count > 0; guard++)
                {
                    Assert.Less(guard, 10000, "mensajes sin fin");
                    DeliverOne();
                }
            }

            public void Heartbeat() { if (Host.Up) Host.Sync.OnHeartbeat(); }

            public void Cut(Node n)
            {
                if (n.IsHost) return;
                n.Linked = false;
                Drop(n);
            }

            public void Link(Node n)
            {
                if (n.IsHost || n.Linked) return;
                n.Linked = true;
                if (Connected(n)) n.Sync.OnConnected();
            }

            public void Down(Node n)
            {
                n.Up = false;
                Drop(n);
            }

            /// <summary>La app vuelve a abrir: retoma lo guardado (o nada, si se perdió el archivo).</summary>
            public void Up(Node n, bool lost = false)
            {
                if (n.Up) return;
                TurnLedger saved = n.Sync.Ledger;
                if (lost) n.InTurn = false;
                Wire(n, lost ? null : saved);
                n.Up = true;
                if (n.IsHost) { foreach (Node c in Nodes.Where(c => !c.IsHost && Connected(c))) c.Sync.OnConnected(); }
                else if (Connected(n)) n.Sync.OnConnected();
            }

            /// <summary>Panel de sesión: lee el turno más nuevo de cada app que responde y se lo pasa a las atrasadas.</summary>
            public void PanelTick()
            {
                if (!_panel) return;
                List<Node> up = Nodes.Where(n => n.Up && n.Sync.Ledger.Key > 0).ToList();
                if (up.Count == 0) return;
                Node best = up[0];
                foreach (Node n in up)
                    if (TurnLedger.IsNewer(n.Sync.Ledger.Pc, n.Sync.Ledger.Key, best.Sync.Ledger.Pc, best.Sync.Ledger.Key)) best = n;
                string pc = best.Sync.Ledger.Pc;
                long key = best.Sync.Ledger.Key;
                foreach (Node n in Nodes.Where(n => n.Up))
                    if (TurnLedger.IsNewer(pc, key, n.Sync.Ledger.Pc, n.Sync.Ledger.Key)) n.Sync.OnPanel(pc, key);
            }

            public void Heal()
            {
                foreach (Node n in Nodes.Where(n => n.IsHost)) Up(n);
                foreach (Node n in Nodes.Where(n => !n.IsHost)) { Up(n); Link(n); }
                for (int round = 0; round < 3; round++)
                {
                    DeliverAll();
                    Heartbeat();
                    DeliverAll();
                }
            }

            public void HealByPanelOnly()
            {
                foreach (Node n in Nodes.Where(n => !n.IsHost)) Cut(n);   // sin red del host: solo el panel
                foreach (Node n in Nodes) Up(n);
                _inFlight.Clear();
                PanelTick();
                PanelTick();
            }

            public void AssertConsistent(string what)
            {
                var keys = Nodes.Select(n => (n.Sync.Ledger.Pc, n.Sync.Ledger.Key)).Distinct().ToList();
                Assert.AreEqual(1, keys.Count, $"{what}: no todos saben lo mismo: {string.Join(", ", keys)}");
                List<Node> inTurn = Nodes.Where(n => n.InTurn).ToList();
                Assert.LessOrEqual(inTurn.Count, 1, $"{what}: {inTurn.Count} PCs en turno a la vez");
                if (inTurn.Count == 1) Assert.AreEqual(keys[0].Pc, inTurn[0].Name, $"{what}: el que está en turno no es el más nuevo");
            }
        }
    }
}
