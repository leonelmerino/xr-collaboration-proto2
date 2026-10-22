using System;
using NUnit.Framework;

namespace XRCollab.Measurement.Turns.Tests
{
    public class TurnFollowTests
    {
        private const float Dt = 1f / 90f;

        private static TurnFollow At(float x, float z, float yaw)
        {
            var f = new TurnFollow();
            f.Snap(x, z, yaw);
            return f;
        }

        private static void Run(TurnFollow f, float seconds, Func<float, (float x, float z, float yaw)> head, bool frozen = false)
        {
            for (float t = 0f; t < seconds; t += Dt)
            {
                var h = head(t);
                f.Update(h.x, h.z, h.yaw, Dt, frozen);
            }
        }

        [Test]
        public void SmallLeansAndGlances_DoNotMoveIt()
        {
            TurnFollow f = At(0f, 0f, 0f);
            Run(f, 5f, t => (0.15f * (float)Math.Sin(t * 2f), 0.12f, 35f * (float)Math.Sin(t)));   // inclinarse, mirar de reojo
            Assert.AreEqual(0f, f.X, 1e-5f);
            Assert.AreEqual(0f, f.Z, 1e-5f);
            Assert.AreEqual(0f, f.Yaw, 1e-5f);
        }

        [Test]
        public void ABriefBigMove_DoesNotMoveIt()
        {
            TurnFollow f = At(0f, 0f, 0f);
            Run(f, 0.25f, t => (0.4f, 0f, 90f));   // menos que la espera
            Run(f, 2f, t => (0f, 0f, 0f));
            Assert.AreEqual(0f, f.X, 1e-5f);
            Assert.AreEqual(0f, f.Yaw, 1e-5f);
        }

        [Test]
        public void WalkingAway_ThenStopping_SettlesAtTheHead_Smoothly()
        {
            TurnFollow f = At(0f, 0f, 0f);
            float lastX = 0f, maxStep = 0f;
            for (float t = 0f; t < 2f; t += Dt)   // camina 1 m en 1 s y se detiene
            {
                f.Update(Math.Min(t, 1f), 0f, 0f, Dt, false);
                maxStep = Math.Max(maxStep, Math.Abs(f.X - lastX));
                lastX = f.X;
            }
            for (float t = 0f; t < 3f; t += Dt)
            {
                f.Update(1f, 0f, 0f, Dt, false);
                maxStep = Math.Max(maxStep, Math.Abs(f.X - lastX));
                lastX = f.X;
            }
            Assert.AreEqual(1f, f.X, 0.031f);
            Assert.IsFalse(f.Following, "ya se detuvo");
            Assert.Less(maxStep, 0.03f, "sin saltos: < 3 cm por cuadro");
            Assert.LessOrEqual(f.X, 1.0001f, "sin pasarse");
        }

        [Test]
        public void TurningAround_FollowsTheShortWay()
        {
            TurnFollow f = At(0f, 0f, 170f);
            Run(f, 4f, t => (0f, 0f, -100f));   // = 260°: 90° por el lado corto (cruza 180)
            Assert.AreEqual(0f, TurnFollow.Delta(f.Yaw, -100f), 3.01f);
        }

        [Test]
        public void Frozen_NeverMoves()
        {
            TurnFollow f = At(0f, 0f, 0f);
            Run(f, 3f, t => (0.8f, 0.5f, 120f), frozen: true);
            Assert.AreEqual(0f, f.X, 1e-6f);
            Assert.AreEqual(0f, f.Yaw, 1e-6f);
        }

        [Test]
        public void PolarRoundTrip()
        {
            TurnFollow f = At(1f, 2f, 30f);
            f.PointAt(40f, 0.45f, out float x, out float z);
            f.ToPolar(x, z, out float angle, out float radius);
            Assert.AreEqual(40f, angle, 1e-3f);
            Assert.AreEqual(0.45f, radius, 1e-4f);
            f.PointAt(0f, 1f, out x, out z);   // frente del cuerpo a 30°
            Assert.AreEqual(1f + (float)Math.Sin(Math.PI / 6), x, 1e-4f);
            Assert.AreEqual(2f + (float)Math.Cos(Math.PI / 6), z, 1e-4f);
        }
    }
}
