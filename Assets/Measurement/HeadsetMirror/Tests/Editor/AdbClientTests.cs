using System.Collections.Generic;
using NUnit.Framework;

namespace XRCollab.Measurement.Mirroring.Tests
{
    public class AdbClientTests
    {
        [Test]
        public void ParseDevices_ReadsSerialAndState()
        {
            IReadOnlyList<AdbDevice> devices = AdbClient.ParseDevices(
                "List of devices attached\r\nFA5AR3N00187\tdevice\r\nFA5AR3N00999\tunauthorized\r\nemulator-5554\toffline\r\n\r\n");
            Assert.AreEqual(3, devices.Count);
            Assert.AreEqual("FA5AR3N00187", devices[0].Serial);
            Assert.IsTrue(devices[0].IsReady);
            Assert.IsTrue(devices[1].IsUnauthorized);
            Assert.IsFalse(devices[2].IsReady);
        }

        [Test]
        public void ParseDevices_LongFormat()
        {
            IReadOnlyList<AdbDevice> devices = AdbClient.ParseDevices(
                "List of devices attached\nFA5AR3N00187           device product:kona model:VIVE_Focus_Vision device:kona transport_id:3\n");
            Assert.AreEqual(1, devices.Count);
            Assert.AreEqual("FA5AR3N00187", devices[0].Serial);
            Assert.IsTrue(devices[0].IsReady);
        }

        [Test]
        public void ParseDevices_SkipsDaemonMessages()
        {
            IReadOnlyList<AdbDevice> devices = AdbClient.ParseDevices(
                "* daemon not running; starting now at tcp:5037\n* daemon started successfully\nList of devices attached\n");
            Assert.AreEqual(0, devices.Count);
        }

        [Test]
        public void ParseDevices_EmptyOrNull()
        {
            Assert.AreEqual(0, AdbClient.ParseDevices("").Count);
            Assert.AreEqual(0, AdbClient.ParseDevices(null).Count);
        }

        [Test]
        public void ParseDisplaySize_Physical()
        {
            Assert.IsTrue(AdbClient.ParseDisplaySize("Physical size: 4896x2448\n", out int w, out int h));
            Assert.AreEqual(4896, w);
            Assert.AreEqual(2448, h);
        }

        [TestCase("Physical size: 4896x2448\nOverride size: 2448x1224\n")]
        [TestCase("Override size: 2448x1224\nPhysical size: 4896x2448\n")]
        public void ParseDisplaySize_OverrideWins(string output)
        {
            Assert.IsTrue(AdbClient.ParseDisplaySize(output, out int w, out int h));
            Assert.AreEqual(2448, w);
            Assert.AreEqual(1224, h);
        }

        [Test]
        public void ParseForwards_ReadsSerialLocalAndRemote()
        {
            IReadOnlyList<AdbForward> forwards = AdbClient.ParseForwards(
                "FA5AR3N00187 tcp:63852 localabstract:scrcpy_1660516f\r\nFA5AR3N00999 tcp:5555 tcp:5555\r\n\r\n");
            Assert.AreEqual(2, forwards.Count);
            Assert.AreEqual("FA5AR3N00187", forwards[0].Serial);
            Assert.AreEqual(63852, forwards[0].LocalTcpPort);
            Assert.AreEqual("localabstract:scrcpy_1660516f", forwards[0].Remote);
            StringAssert.StartsWith("localabstract:" + ScrcpyProtocol.SocketName(""), forwards[0].Remote);
        }

        [TestCase("localabstract:foo")]
        [TestCase("tcp:abc")]
        public void Forward_NonTcpLocal_HasNoPort(string local)
        {
            Assert.AreEqual(0, new AdbForward("S", local, "localabstract:scrcpy_1").LocalTcpPort);
        }

        [Test]
        public void ParseForwards_EmptyOrNull()
        {
            Assert.AreEqual(0, AdbClient.ParseForwards("").Count);
            Assert.AreEqual(0, AdbClient.ParseForwards(null).Count);
        }

        [TestCase("  mWakefulness=Awake\n", "Awake")]
        [TestCase("  mWakefulness=Asleep\n  mWakefulnessChanging=false\n", "Asleep")]
        [TestCase("  mWakefulnessChanging=true\n  mWakefulness=Dozing\n", "Dozing")]
        public void ParseWakefulness_ReadsState(string output, string expected)
        {
            Assert.IsTrue(AdbClient.ParseWakefulness(output, out string state));
            Assert.AreEqual(expected, state);
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("  mWakefulnessChanging=false\n")]
        public void ParseWakefulness_Unrecognized(string output)
        {
            Assert.IsFalse(AdbClient.ParseWakefulness(output, out string state));
            Assert.IsNull(state);
        }

        [TestCase("")]
        [TestCase("error: no devices/emulators found")]
        [TestCase(null)]
        public void ParseDisplaySize_Unrecognized(string output)
        {
            Assert.IsFalse(AdbClient.ParseDisplaySize(output, out _, out _));
        }
    }
}
