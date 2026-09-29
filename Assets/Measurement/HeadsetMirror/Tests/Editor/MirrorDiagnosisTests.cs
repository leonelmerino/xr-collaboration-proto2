using NUnit.Framework;

namespace XRCollab.Measurement.Mirroring.Tests
{
    public class MirrorDiagnosisTests
    {
        private const string Serial = "FA5AR3N00187";

        private static MirrorNotice Diagnose(MirrorState state, MirrorIssue issue = MirrorIssue.None, string detail = "",
            bool visible = true, double sinceFrame = 0.03, double dark = 0, XrVisibility xr = XrVisibility.Focused, string passthroughProblem = null,
            HeadsetWear wear = HeadsetWear.Unknown)
        {
            var status = new MirrorStatus(state, issue, detail, Serial);
            return MirrorDiagnosis.Evaluate(new DiagnosisInput(status, visible, sinceFrame, dark, new AppXrState(xr, passthroughProblem), wear));
        }

        [Test]
        public void ScreenCapture2026_09_29_WaitingScreenWhileSessionFocused_OffFace_IsNotWorn()
        {
            // Visor en la frente: pantalla de espera de VIVE (cuadros negros) con la sesión XR en FOCUSED a 90 fps.
            MirrorNotice n = Diagnose(MirrorState.Streaming, dark: 3, xr: XrVisibility.Focused, wear: HeadsetWear.OffFace);
            StringAssert.Contains("no está puesto", n.Title);
            StringAssert.Contains("frente", n.Hint);
        }

        [Test]
        public void OffFace_WinsOverPassthroughProblem()
        {
            MirrorNotice n = Diagnose(MirrorState.Streaming, dark: 3, passthroughProblem: "xrCreatePassthroughHTC falló", wear: HeadsetWear.OffFace);
            StringAssert.Contains("no está puesto", n.Title);
        }

        [Test]
        public void OffFace_Stalled_IsNotWorn()
        {
            StringAssert.Contains("no está puesto", Diagnose(MirrorState.Streaming, sinceFrame: 3, wear: HeadsetWear.OffFace).Title);
        }

        [Test]
        public void OnFace_Dark_PointsToStreamingSettings()
        {
            MirrorNotice n = Diagnose(MirrorState.Streaming, dark: 3, wear: HeadsetWear.OnFace);
            StringAssert.Contains("negro", n.Title);
            StringAssert.Contains("está puesto", n.Hint);
            StringAssert.Contains("DisplayPort", n.Hint);
        }

        [Test]
        public void OffFace_WithLiveImage_NoNotice()
        {
            // Solo importa cuando la imagen se ve mal.
            Assert.IsNull(Diagnose(MirrorState.Streaming, wear: HeadsetWear.OffFace));
        }

        [Test]
        public void Texts_UseOnlyCharactersTheLegacyFontHas()
        {
            // LegacyRuntime.ttf no tiene emoji ni símbolos como ⚡ (salían como un hueco en pantalla).
            foreach (MirrorState state in System.Enum.GetValues(typeof(MirrorState)))
            foreach (MirrorIssue issue in System.Enum.GetValues(typeof(MirrorIssue)))
            foreach (HeadsetWear wear in System.Enum.GetValues(typeof(HeadsetWear)))
            {
                MirrorNotice n = Diagnose(state, issue, "detalle", sinceFrame: 5, dark: 5, wear: wear);
                if (n == null) continue;
                foreach (char c in n.Title + n.Hint)
                    Assert.IsTrue(c < 0x2000 || c == '…' || c == '—', $"'{c}' (U+{(int)c:X4}) en {state}/{issue}/{wear}: {n}");
            }
        }

        [Test]
        public void LiveImage_NoNotice()
        {
            Assert.IsNull(Diagnose(MirrorState.Streaming));
        }

        [Test]
        public void LiveImage_WithMenuOverTheApp_NoNotice()
        {
            Assert.IsNull(Diagnose(MirrorState.Streaming, xr: XrVisibility.Visible));
        }

        [Test]
        public void Hidden_SaysHowToShow()
        {
            MirrorNotice n = Diagnose(MirrorState.Streaming, visible: false);
            StringAssert.Contains("oculto", n.Title);
            StringAssert.Contains("F5", n.Hint);
        }

        [TestCase(MirrorIssue.AdbMissing, "adb")]
        [TestCase(MirrorIssue.FfmpegMissing, "ffmpeg")]
        [TestCase(MirrorIssue.ServerAssetMissing, "build")]
        public void MissingTools_NameTheTool(MirrorIssue issue, string word)
        {
            MirrorNotice n = Diagnose(MirrorState.MissingTools, issue);
            StringAssert.Contains(word, n.Title);
        }

        [Test]
        public void NoDevice_AsksForUsbAndWarnsAboutDisplayPortPorts()
        {
            MirrorNotice n = Diagnose(MirrorState.WaitingForDevice, MirrorIssue.NoDevice);
            StringAssert.Contains("no está conectado por USB", n.Title);
            StringAssert.Contains("Thunderbolt", n.Hint);
        }

        [Test]
        public void Unauthorized_AsksToAcceptUsbDebugging()
        {
            StringAssert.Contains("depuración USB", Diagnose(MirrorState.WaitingForDevice, MirrorIssue.DeviceUnauthorized).Title);
        }

        [Test]
        public void Asleep_ExplainsNobodyIsWearingIt()
        {
            MirrorNotice n = Diagnose(MirrorState.DeviceAsleep, MirrorIssue.DeviceAsleep);
            StringAssert.Contains("reposo", n.Title);
            StringAssert.Contains("Ponérselo", n.Hint);
        }

        [Test]
        public void Screenshot2026_09_29_StreamingWithoutFramesAndSessionSynchronized_IsPaused()
        {
            // Visor en la frente: la app bajó a ~20 fps (SYNCHRONIZED) y el espejo decía "Streaming · 0 fps" en negro.
            MirrorNotice n = Diagnose(MirrorState.Streaming, sinceFrame: 4, xr: XrVisibility.NotVisible);
            StringAssert.Contains("pausó", n.Title);
            StringAssert.Contains("frente", n.Hint);
        }

        [Test]
        public void Streaming_DarkFramesAndSessionSynchronized_IsPaused()
        {
            // La pantalla de espera de VIVE (círculo de carga) llega como cuadros negros.
            StringAssert.Contains("pausó", Diagnose(MirrorState.Streaming, dark: 3, xr: XrVisibility.NotVisible).Title);
        }

        [Test]
        public void Streaming_StalledWhileAppFocused_WaitsForRecovery()
        {
            MirrorNotice n = Diagnose(MirrorState.Streaming, sinceFrame: 3);
            StringAssert.Contains("dejó de enviar", n.Title);
        }

        [Test]
        public void Streaming_ShortGap_NoNotice()
        {
            Assert.IsNull(Diagnose(MirrorState.Streaming, sinceFrame: MirrorDiagnosis.StalledAfterSeconds - 0.5));
            Assert.IsNull(Diagnose(MirrorState.Streaming, dark: MirrorDiagnosis.DarkAfterSeconds - 0.5));
        }

        [Test]
        public void Streaming_DarkWithPassthroughProblem_BlamesPassthrough()
        {
            MirrorNotice n = Diagnose(MirrorState.Streaming, dark: 3, passthroughProblem: "xrCreatePassthroughHTC falló");
            StringAssert.Contains("passthrough", n.Title);
            StringAssert.Contains("xrCreatePassthroughHTC falló", n.Hint);
            StringAssert.Contains("DisplayPort", n.Hint);
        }

        [Test]
        public void Streaming_DarkWithoutKnownCause_ListsTheUsualCauses()
        {
            MirrorNotice n = Diagnose(MirrorState.Streaming, dark: 3, xr: XrVisibility.Unknown);
            StringAssert.Contains("negro", n.Title);
            StringAssert.Contains("DisplayPort", n.Hint);
            StringAssert.Contains("MR with passthrough", n.Hint);
        }

        [Test]
        public void Reconnecting_ExplainsTheCut()
        {
            StringAssert.Contains("Se cortó", Diagnose(MirrorState.Reconnecting, MirrorIssue.StreamCut).Title);
            StringAssert.Contains("no envía", Diagnose(MirrorState.Reconnecting, MirrorIssue.NoImage).Title);
            StringAssert.Contains("pausó", Diagnose(MirrorState.Reconnecting, MirrorIssue.NoImage, xr: XrVisibility.NotVisible).Title);
            MirrorNotice failed = Diagnose(MirrorState.Reconnecting, MirrorIssue.SessionFailed, "adb push falló: x");
            StringAssert.Contains("adb push falló", failed.Hint);
        }

        [Test]
        public void Connecting_ShowsProgress()
        {
            StringAssert.Contains("Conectando", Diagnose(MirrorState.Connecting).Title);
        }

        [Test]
        public void NullStatus_IsTreatedAsStopped()
        {
            var input = new DiagnosisInput(null, true, double.PositiveInfinity, 0, AppXrState.Unknown);
            StringAssert.Contains("detenido", MirrorDiagnosis.Evaluate(input).Title);
        }

        [Test]
        public void EveryIssueInEveryState_ProducesATitle()
        {
            // Ninguna combinación debe dejar la pantalla negra sin explicación (salvo imagen en vivo).
            foreach (MirrorState state in System.Enum.GetValues(typeof(MirrorState)))
            foreach (MirrorIssue issue in System.Enum.GetValues(typeof(MirrorIssue)))
            {
                if (state == MirrorState.Streaming && issue == MirrorIssue.None) continue;
                MirrorNotice n = Diagnose(state, issue, "detalle", sinceFrame: double.PositiveInfinity);
                Assert.IsNotNull(n, $"{state}/{issue}");
                Assert.IsNotEmpty(n.Title, $"{state}/{issue}");
            }
        }

        [TestCase(5, XrVisibility.Focused)]
        [TestCase(4, XrVisibility.Visible)]
        [TestCase(3, XrVisibility.NotVisible)]
        [TestCase(1, XrVisibility.NotVisible)]
        [TestCase(6, XrVisibility.NotVisible)]
        [TestCase(0, XrVisibility.Unknown)]
        [TestCase(8, XrVisibility.Unknown)]
        public void SessionStateMapping(int xrState, XrVisibility expected)
        {
            Assert.AreEqual(expected, AppXrState.FromSessionState(xrState));
        }

        [Test]
        public void AppXrState_BlankProblemIsNull()
        {
            Assert.IsNull(new AppXrState(XrVisibility.Focused, "  ").PassthroughProblem);
        }
    }
}
