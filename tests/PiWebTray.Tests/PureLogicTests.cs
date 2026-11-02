// Pi Web Tray — pure-logic tests
//
// Run with `dotnet test`. These tests cover parsing, comparison, quoting, ini reading,
// localisation completeness and icon encoding. They do not access the network or change
// machine state.
//
// Output is English on purpose: it is developer-facing, not part of the localised UI.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWebTray
{
    [TestClass]
    public sealed class PureLogicTests
    {
        [TestMethod]
        public void AllPureLogicChecksPass()
        {
            Assert.AreEqual(0, SelfTest.Run(), "Pure-logic checks failed. See test output.");
        }
    }

    internal static class SelfTest
    {
        private static int _passed;
        private static readonly List<string> Failures = new List<string>();

        internal static int Run()
        {
            _passed = 0;
            Failures.Clear();

            VersionCompareTests();
            VersionParseTests();
            ClipTests();
            QuoteTests();
            IniReadTests();
            InstanceSuffixTests();
            LocalizationTests();
            IconEncodingTests();
            InstallArgumentsTests();
            SafeTagTests();
            StateFileTests();
            HttpProbeTestsSkip();

            Console.WriteLine();
            if (Failures.Count == 0)
            {
                Console.WriteLine("[ok] " + _passed + " checks passed");
                return 0;
            }

            Console.WriteLine("[failed] " + Failures.Count + " of " + (_passed + Failures.Count) + " checks failed:");
            foreach (string failure in Failures) Console.WriteLine("  - " + failure);
            return 1;
        }

        // ---------------- helpers ----------------

        private static void Check(string name, bool condition)
        {
            if (condition) _passed++;
            else Failures.Add(name);
        }

        private static void CheckEqual(string name, object expected, object actual)
        {
            bool same = expected == null ? actual == null : expected.Equals(actual);
            if (same) _passed++;
            else Failures.Add(name + ": expected <" + expected + "> but was <" + actual + ">");
        }

        // ---------------- tests ----------------

        private static void VersionCompareTests()
        {
            CheckEqual("compare 22.19.0 == 22.19", 0, VersionUtil.Compare("22.19.0", "22.19.0"));
            CheckEqual("compare 22.19.1 > 22.19.0", 1, VersionUtil.Compare("22.19.1", "22.19.0"));
            CheckEqual("compare 22.18.9 < 22.19.0", -1, VersionUtil.Compare("22.18.9", "22.19.0"));
            CheckEqual("compare v22.23.1 > 22.19.0", 1, VersionUtil.Compare("v22.23.1", "22.19.0"));
            CheckEqual("compare 23.0.0 > 22.19.0", 1, VersionUtil.Compare("23.0.0", "22.19.0"));
            CheckEqual("compare 22.19 == 22.19.0", 0, VersionUtil.Compare("22.19", "22.19.0"));
            Check("at least 22.19.0", VersionUtil.AtLeast("22.19.0", "22.19.0"));
            Check("at least v24.1.0", VersionUtil.AtLeast("v24.1.0", "22.19.0"));
            Check("not at least 22.18.0", !VersionUtil.AtLeast("22.18.0", "22.19.0"));
        }

        private static void VersionParseTests()
        {
            int[] parts = VersionUtil.Parse("v22.23.1-pre.2");
            Check("parse length 3", parts.Length == 3);
            Check("parse major 22", parts.Length > 0 && parts[0] == 22);
            Check("parse minor 23", parts.Length > 1 && parts[1] == 23);
            Check("parse patch 1", parts.Length > 2 && parts[2] == 1);
            CheckEqual("parse empty", 0, VersionUtil.Parse("").Length);
            CheckEqual("parse junk", 0, VersionUtil.Parse("abc").Length);
        }

        private static void ClipTests()
        {
            CheckEqual("clip short", "abc", Shell.Clip("abc", 10));
            CheckEqual("clip null", "", Shell.Clip(null, 10));
            string clipped = Shell.Clip(new string('x', 100), 63);
            CheckEqual("clip length", 63, clipped.Length);
            Check("clip keeps tail marker", clipped.EndsWith("...", StringComparison.Ordinal));
            CheckEqual("clip tiny budget", 3, Shell.Clip(new string('x', 10), 3).Length);
        }

        private static void QuoteTests()
        {
            CheckEqual("quote plain", "\"C:\\Program Files\\node.exe\"", Shell.Quote("C:\\Program Files\\node.exe"));
            CheckEqual("cmd line", "/d /s /c \"cmd\"", Shell.CmdLine("cmd"));
        }

        private static void IniReadTests()
        {
            string path = Path.Combine(Path.GetTempPath(), "pi-web-tray-selftest.ini");
            try
            {
                File.WriteAllText(path,
                    "; comment\r\nLanguage = en\r\nPort=30141   ; trailing note\r\nEmpty=\r\n"
                    + "WorkDir=C:\\work;old\r\nQuoted=\"C:\\Program Files\\x\"\r\nHash=https://example.test/#frag\r\n",
                    new UTF8Encoding(false));

                CheckEqual("ini read language", "en", Config.ReadIniValue(path, "language"));
                CheckEqual("ini read padded key", "en", Config.ReadIniValue(path, "  Language  "));
                CheckEqual("ini strips trailing comment", "30141", Config.ReadIniValue(path, "port"));
                CheckEqual("ini empty value", "", Config.ReadIniValue(path, "empty"));
                CheckEqual("ini missing key", "", Config.ReadIniValue(path, "nope"));
                CheckEqual("ini missing file", "", Config.ReadIniValue(path + ".missing", "port"));
                CheckEqual("ini keeps semicolon in value", "C:\\work;old", Config.ReadIniValue(path, "workdir"));
                CheckEqual("ini unquotes value", "C:\\Program Files\\x", Config.ReadIniValue(path, "quoted"));
                CheckEqual("ini keeps hash in value", "https://example.test/#frag", Config.ReadIniValue(path, "hash"));

                string key;
                string value;
                Check("ini rejects comment line", !Config.TryParseLine("; Port=1", out key, out value));
                Check("ini rejects line without equals", !Config.TryParseLine("Port", out key, out value));
                Check("ini rejects empty key", !Config.TryParseLine("=1", out key, out value));
                Check("drive root uses the data directory", Config.IsSystemDirectory(Path.GetPathRoot(Path.GetTempPath())));
                Check("user data directory stays portable", !Config.IsSystemDirectory(Path.Combine(Shell.LocalAppData(), "pi-web-tray-selftest")));
            }
            finally
            {
                try { File.Delete(path); } catch (Exception) { }
            }
        }

        private static void InstanceSuffixTests()
        {
            string original = Environment.GetEnvironmentVariable("PI_WEB_TRAY_INSTANCE");
            try
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", null);
                CheckEqual("suffix none", "", Instance.Suffix);
                Check("mutex default", Instance.MutexName.EndsWith("C2B1B0", StringComparison.Ordinal));

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", "dev");
                CheckEqual("suffix dev", ".dev", Instance.Suffix);
                Check("mutex suffixed", Instance.MutexName.EndsWith(".dev", StringComparison.Ordinal));

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", "a b/c\\d");
                CheckEqual("suffix sanitised", ".abcd", Instance.Suffix);

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", new string('x', 80));
                CheckEqual("suffix truncated", 33, Instance.Suffix.Length);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", original);
            }
        }

        private static void LocalizationTests()
        {
            List<string> problems = L.Problems();
            Check("localisation rows complete (" + string.Join("; ", problems.ToArray()) + ")", problems.Count == 0);
            Check("localisation has rows", L.Count > 100);

            L.Use("en");
            string english = L.T("status.running", 1234);
            Check("english formatting", english.Contains("1234") && english.StartsWith("Running", StringComparison.Ordinal));

            L.Use("zh-CN");
            string chinese = L.T("status.running", 1234);
            Check("simplified chinese formatting", chinese.Contains("1234") && chinese != english);

            L.Use("zh-TW");
            string traditional = L.T("status.running", 1234);
            Check("traditional chinese formatting", traditional.Contains("1234")
                && traditional != english && traditional != chinese);
            Check("traditional resolves to zh-Hant", L.Current == "zh-Hant");

            L.Use("auto");
            Check("auto resolves to a known language", L.Current == "en" || L.Current == "zh" || L.Current == "zh-Hant");

            CheckEqual("unknown key is visible", "!no.such.key!", L.T("no.such.key"));
            // Too few arguments must fall back to the raw format instead of throwing
            string rawFormat = L.T("status.installingWith");
            CheckEqual("short format args fall back", rawFormat, L.T("status.installingWith", "only-one"));
            // A null argument array must return the format instead of throwing
            CheckEqual("null format args fall back", rawFormat, L.T("status.installingWith", (object[])null));
            // Extra arguments are harmless
            Check("extra format args are fine", L.T("status.starting", 1, 2).Contains("1"));
        }

        private static void IconEncodingTests()
        {
            int[] sizes = new int[] { 16, 32, 48 };
            byte[] ico = Icons.BuildIco(Icons.RunningColor, sizes);

            Check("ico header reserved", ico[0] == 0 && ico[1] == 0);
            Check("ico type is icon", ico[2] == 1 && ico[3] == 0);
            CheckEqual("ico frame count", (byte)sizes.Length, ico[4]);

            // Offsets in the directory must point at the PNG payloads inside the file
            bool offsetsOk = true;
            for (int i = 0; i < sizes.Length; i++)
            {
                int entry = 6 + i * 16;
                int length = BitConverter.ToInt32(ico, entry + 8);
                int offset = BitConverter.ToInt32(ico, entry + 12);
                if (offset + length > ico.Length) offsetsOk = false;
                if (ico[offset] != 0x89 || ico[offset + 1] != 0x50) offsetsOk = false;
            }
            Check("ico frames point at PNG data", offsetsOk);

            IconCacheTests();
        }

        private static void IconCacheTests()
        {
            // Same colour must return the same instance (icons are cached, never disposed by callers)
            IntPtr first = Icons.Create(Icons.RunningColor).Handle;
            IntPtr second = Icons.Create(Icons.RunningColor).Handle;
            Check("icon cache returns one instance", first == second);

            IntPtr other = Icons.Create(Icons.ErrorColor).Handle;
            Check("different colours differ", other != first);
        }

        private static void InstallArgumentsTests()
        {
            Config config = new Config();
            config.InstallTag = "latest";
            config.NpmRegistry = "";

            List<string> packages = new List<string>(new string[] { "@agegr/pi-web@latest" });
            string args = Installer.BuildArguments(config, "C:\\Program Files\\nodejs\\npm-cli.js", packages);

            Check("npm args quote the cli path", args.StartsWith("\"C:\\Program Files\\nodejs\\npm-cli.js\"", StringComparison.Ordinal));
            Check("npm args install globally", args.Contains(" install -g "));
            Check("npm args keep package", args.Contains("@agegr/pi-web@latest"));
            Check("npm args quiet audit/fund", args.Contains("--no-fund --no-audit"));
            Check("npm args without registry", !args.Contains("--registry"));

            config.NpmRegistry = "https://registry.npmmirror.com";
            string withRegistry = Installer.BuildArguments(config, "npm-cli.js", packages);
            Check("npm args include quoted registry", withRegistry.Contains("--registry \"https://registry.npmmirror.com\""));

            ProbeResult installed = new ProbeResult();
            installed.PiWebFound = true;
            installed.PiCliFound = true;
            config.InstallPiCli = true;
            CheckEqual("no packages when everything is installed", 0, Installer.PackagesToInstall(installed, config, false).Count);
            CheckEqual("force installs pi-web and pi", 2, Installer.PackagesToInstall(installed, config, true).Count);
            config.InstallPiCli = false;
            CheckEqual("force respects InstallPiCli=0", 1, Installer.PackagesToInstall(installed, config, true).Count);
        }

        private static void SafeTagTests()
        {
            CheckEqual("tag latest", "latest", Installer.SafeTag("latest"));
            CheckEqual("tag pinned", "0.13.0", Installer.SafeTag("0.13.0"));
            CheckEqual("tag trims", "1.2.3", Installer.SafeTag("  1.2.3  "));
            CheckEqual("tag empty", "latest", Installer.SafeTag(""));
            CheckEqual("tag null", "latest", Installer.SafeTag(null));
            CheckEqual("tag rejects spaces", "latest", Installer.SafeTag("latest --registry evil"));
            CheckEqual("tag rejects quotes", "latest", Installer.SafeTag("x\"y"));
            CheckEqual("tag rejects semicolons", "latest", Installer.SafeTag("latest;calc"));
            CheckEqual("tag rejects too long", "latest", Installer.SafeTag(new string('a', 65)));
            CheckEqual("tag allows caret", "1.2.3^", Installer.SafeTag("1.2.3^"));
        }

        private static void StateFileTests()
        {
            string original = Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME");
            string temp = Path.Combine(Path.GetTempPath(), "pi-web-tray-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", temp);
                TrayState.Save(0, true, 30141);

                Check("state file created", File.Exists(TrayState.StatePath()));
                string content = File.ReadAllText(TrayState.StatePath());
                Check("state has pid", content.Contains("Pid=0"));
                Check("state has port", content.Contains("Port=30141"));

                // pid 0 is never a real process, so the stale-state guard must answer false
                Check("stale state is not trusted", !TrayState.InstanceIsElevated());

                // our own pid is alive -> trusted
                TrayState.Save(System.Diagnostics.Process.GetCurrentProcess().Id, true, 30141);
                Check("live state is trusted", TrayState.InstanceIsElevated());

                TrayState.Delete();
                Check("state deleted", !File.Exists(TrayState.StatePath()));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", original);
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception) { }
            }
        }

        private static void HttpProbeTestsSkip()
        {
            // IsHttpOk needs a listening server, so only the cheap negative case is covered here;
            // the positive path is exercised by the manual checklist in README.md.
            Check("closed port reports closed", !Net.IsPortOpen(1, 300));
            Check("closed port has no HTTP", !Net.IsHttpOk("http://127.0.0.1:1/", 500));
        }
    }
}
