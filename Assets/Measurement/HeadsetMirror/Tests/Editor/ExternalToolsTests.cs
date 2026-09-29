using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace XRCollab.Measurement.Mirroring.Tests
{
    public class ExternalToolsTests
    {
        private static Func<string, bool> Existing(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        [Test]
        public void Adb_PrefersViveHubInstallInProgramFiles()
        {
            string found = ExternalTools.FindAdb("", Existing(ExternalTools.ViveHubAdbPaths[0], ExternalTools.ViveHubAdbPaths[1]));
            Assert.AreEqual(ExternalTools.ViveHubAdbPaths[0], found);
        }

        [Test]
        public void Adb_FallsBackToSteamInstall()
        {
            Assert.AreEqual(ExternalTools.ViveHubAdbPaths[1], ExternalTools.FindAdb(null, Existing(ExternalTools.ViveHubAdbPaths[1])));
        }

        [Test]
        public void Adb_NeverUsesPathAdb()
        {
            // Un adb del PATH (Platform-Tools) es otra versión y corta el enlace de VIVE Hub: no se busca ahí.
            Assert.IsNull(ExternalTools.FindAdb("", Existing(@"C:\platform-tools\adb.exe")));
        }

        [Test]
        public void Adb_OverrideMustExist()
        {
            Assert.AreEqual(@"D:\x\adb.exe", ExternalTools.FindAdb(@"D:\x\adb.exe", Existing(@"D:\x\adb.exe")));
            Assert.IsNull(ExternalTools.FindAdb(@"D:\x\adb.exe", Existing(ExternalTools.ViveHubAdbPaths[0])));
        }

        [Test]
        public void Ffmpeg_SearchesPathFirst()
        {
            string found = ExternalTools.FindFfmpeg("", @"C:\a;C:\tools\ffmpeg\bin", @"C:\Users\u\AppData\Local",
                Existing(@"C:\tools\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\WinGet\Links\ffmpeg.exe"));
            Assert.AreEqual(@"C:\tools\ffmpeg\bin\ffmpeg.exe", found);
        }

        [Test]
        public void Ffmpeg_FallsBackToWinGetMachineThenUser()
        {
            string user = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe";
            Assert.AreEqual(@"C:\Program Files\WinGet\Links\ffmpeg.exe",
                ExternalTools.FindFfmpeg("", "", @"C:\Users\u\AppData\Local", Existing(@"C:\Program Files\WinGet\Links\ffmpeg.exe", user)));
            Assert.AreEqual(user, ExternalTools.FindFfmpeg("", "", @"C:\Users\u\AppData\Local", Existing(user)));
        }

        [Test]
        public void Ffmpeg_IgnoresBrokenAndQuotedPathEntries()
        {
            string found = ExternalTools.FindFfmpeg("", "C:\\bad|<dir>;;\"C:\\quoted dir\"", null, Existing(@"C:\quoted dir\ffmpeg.exe"));
            Assert.AreEqual(@"C:\quoted dir\ffmpeg.exe", found);
        }

        [Test]
        public void Ffmpeg_NotFound_ReturnsNull()
        {
            Assert.IsNull(ExternalTools.FindFfmpeg("", @"C:\a", null, Existing()));
        }

        [Test]
        public void ExistsThatThrows_IsTreatedAsMissing()
        {
            Assert.IsNull(ExternalTools.FindAdb("", _ => throw new UnauthorizedAccessException()));
        }
    }
}
