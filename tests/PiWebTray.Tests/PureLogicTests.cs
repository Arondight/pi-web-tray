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
            PathSplitTests();
            NodeDownloadTests();
            QuoteTests();
            IniReadTests();
            InstanceSuffixTests();
            LocalizationTests();
            IconEncodingTests();
            InstallArgumentsTests();
            SafeTagTests();
            UpdateTests();
            InstallServiceStopTests();
            UpdateCancellationTests();
            UpdateCopyTests();
            UpdateTargetTests();
            ServiceProbeTests();
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

        private static void PathSplitTests()
        {
            List<string> dirs = EnvironmentProbe.SplitPath(" C:\\a ;\"C:\\b\";;c:\\A ");
            CheckEqual("path split count", 2, dirs.Count);
            CheckEqual("path split first", "C:\\a", dirs[0]);
            CheckEqual("path split second", "C:\\b", dirs[1]);
            CheckEqual("path split empty", 0, EnvironmentProbe.SplitPath("").Count);
        }

        private static void NodeDownloadTests()
        {
            CheckEqual("Windows 7 Node.js download", NodeDownloads.Windows7Or8Url,
                NodeDownloads.UrlFor(new Version(6, 1, 7601)));
            CheckEqual("Windows 8 Node.js download", NodeDownloads.Windows7Or8Url,
                NodeDownloads.UrlFor(new Version(6, 2, 9200)));
            CheckEqual("Windows 8.1 Node.js download", NodeDownloads.Windows81Url,
                NodeDownloads.UrlFor(new Version(6, 3, 9600)));
            CheckEqual("newer Windows Node.js download", NodeDownloads.DefaultUrl,
                NodeDownloads.UrlFor(new Version(10, 0, 19045)));
            CheckEqual("unknown Windows Node.js download", NodeDownloads.DefaultUrl,
                NodeDownloads.UrlFor(null));
            Check("current Windows version is readable", WindowsVersion.Current.Major >= 6);
            Check("legacy download uses the pinned version",
                NodeDownloads.Windows7Or8Url.Contains(NodeDownloads.PinnedVersion));
            Check("8.1 download uses the pinned version",
                NodeDownloads.Windows81Url.Contains(NodeDownloads.PinnedVersion));
            Check("Windows 7 uses the community build",
                NodeDownloads.UsesCommunityBuild(new Version(6, 1, 7601)));
            Check("Windows 8 uses the community build",
                NodeDownloads.UsesCommunityBuild(new Version(6, 2, 9200)));
            Check("Windows 8.1 does not use the community build",
                !NodeDownloads.UsesCommunityBuild(new Version(6, 3, 9600)));
            Check("Windows 10 does not use the community build",
                !NodeDownloads.UsesCommunityBuild(new Version(10, 0, 19045)));
            Check("no community build without a version",
                !NodeDownloads.UsesCommunityBuild(null));

            // The console hint shown by --install must carry the same link as the tray menu.
            L.Use("en");
            Check("console hint carries the download url",
                L.T("install.installNode", ProbeResult.NodeMinimum, NodeDownloads.Windows7Or8Url)
                    .Contains(NodeDownloads.Windows7Or8Url));

            string missingBody = L.T("node.missingBody", "Node.js was not found",
                ProbeResult.NodeMinimum, NodeDownloads.DefaultUrl);
            Check("missing Node prompt carries the minimum version",
                missingBody.Contains(ProbeResult.NodeMinimum));
            Check("missing Node prompt carries the download url",
                missingBody.Contains(NodeDownloads.DefaultUrl));
            Check("missing Node prompt keeps the reason", missingBody.Contains("was not found"));
        }

        private static void QuoteTests()
        {
            CheckEqual("quote plain", "\"C:\\Program Files\\node.exe\"", Shell.Quote("C:\\Program Files\\node.exe"));
            CheckEqual("cmd line", "/d /s /c \"cmd\"", Shell.CmdLine("cmd"));
            CheckEqual("quote arg plain", "\"abc\"", Shell.QuoteArg("abc"));
            CheckEqual("quote arg spaces", "\"a b\"", Shell.QuoteArg("a b"));
            CheckEqual("quote arg trailing slash", "\"C:\\dir\\\\\"", Shell.QuoteArg("C:\\dir\\"));
            CheckEqual("quote arg embedded quote", "\"a\\\"b\"", Shell.QuoteArg("a\"b"));
            CheckEqual("quote arg null", "\"\"", Shell.QuoteArg(null));
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

                string writePath = path + ".write";
                try
                {
                    Config writeConfig = new Config();
                    writeConfig.IniPath = writePath;
                    List<KeyValuePair<string, string>> values = new List<KeyValuePair<string, string>>();
                    values.Add(new KeyValuePair<string, string>("Port", "30200"));
                    values.Add(new KeyValuePair<string, string>("WorkingDir", "C:/work;old"));
                    values.Add(new KeyValuePair<string, string>("NpmRegistry", "https://example.test/#frag"));
                    values.Add(new KeyValuePair<string, string>("Language", "zh-Hant"));
                    Check("ini values written", writeConfig.SetValues(values));
                    string parsedKey;
                    string parsedValue;
                    CheckEqual("ini quoted semicolon parses", "C:/work;old",
                        Config.TryParseLine("WorkingDir=\"C:/work;old\"", out parsedKey, out parsedValue)
                            ? parsedValue : "parse-failed");
                    CheckEqual("ini written port", "30200", Config.ReadIniValue(writePath, "port"));
                    CheckEqual("ini written semicolon path", "C:/work;old", Config.ReadIniValue(writePath, "WorkingDir"));
                    CheckEqual("ini written hash URL", "https://example.test/#frag", Config.ReadIniValue(writePath, "npmregistry"));
                    CheckEqual("ini written language", "zh-Hant", Config.ReadIniValue(writePath, "language"));
                }
                finally
                {
                    try { File.Delete(writePath); } catch (Exception) { }
                }
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

            List<string> web = new List<string>(new string[] { "@agegr/pi-web@latest" });
            List<string> cli = new List<string>(new string[] { "@earendil-works/pi-coding-agent@latest" });
            string path = "C:\\Program Files\\nodejs\\npm-cli.js";

            string webArgs = Installer.BuildArguments(config, path, web, "12.2.1", false, Installer.PiWebScriptPackages);
            Check("npm args quote the cli path", webArgs.StartsWith("\"C:\\Program Files\\nodejs\\npm-cli.js\"", StringComparison.Ordinal));
            Check("npm args install globally", webArgs.Contains(" install -g "));
            Check("npm args keep package", webArgs.Contains("@agegr/pi-web@latest"));
            Check("npm args quiet audit/fund", webArgs.Contains("--no-fund --no-audit"));
            Check("npm args without registry", !webArgs.Contains("--registry"));

            config.NpmRegistry = "https://registry.npmmirror.com";
            string withRegistry = Installer.BuildArguments(config, "npm-cli.js", web, "12.2.1", false, Installer.PiWebScriptPackages);
            Check("npm args include quoted registry", withRegistry.Contains("--registry \"https://registry.npmmirror.com\""));

            Check("pi-web never ignores scripts", !webArgs.Contains("--ignore-scripts"));
            Check("npm 12 allows pi-web scripts", webArgs.Contains("--allow-scripts \"@agegr/pi-web,node-pty\""));
            string web11 = Installer.BuildArguments(config, path, web, "11.6.0", false, Installer.PiWebScriptPackages);
            Check("npm 11 has no allow-scripts flag", !web11.Contains("--allow-scripts"));

            string cliArgs = Installer.BuildArguments(config, path, cli, "12.2.1", true, "");
            Check("pi cli installs with --ignore-scripts", cliArgs.Contains(" --ignore-scripts"));
            Check("pi cli never allows scripts", !cliArgs.Contains("--allow-scripts"));

            CheckEqual("npm major 12", 12, Installer.NpmMajor("12.2.1"));
            CheckEqual("npm major 11", 11, Installer.NpmMajor("11.6.0"));
            CheckEqual("npm major unknown", 0, Installer.NpmMajor(""));
            CheckEqual("allowlist keeps user packages",
                "@google/genai,protobufjs,@agegr/pi-web,node-pty",
                Installer.MergeAllowScripts("@google/genai, protobufjs", Installer.PiWebScriptPackages));
            CheckEqual("allowlist deduplicates",
                "@agegr/pi-web,node-pty",
                Installer.MergeAllowScripts("@agegr/pi-web", Installer.PiWebScriptPackages));
            CheckEqual("allowlist drops unsafe entries",
                "@agegr/pi-web,node-pty",
                Installer.MergeAllowScripts("evil --registry x,\"bad\",@agegr/pi-web,node-pty", Installer.PiWebScriptPackages));

            ProbeResult missing = new ProbeResult();
            config.InstallPiCli = true;
            List<InstallStep> steps = Installer.StepsToInstall(missing, config, false);
            CheckEqual("missing deps need two npm runs", 2, steps.Count);
            Check("pi-web runs first", steps[0].Packages.Contains("@agegr/pi-web@latest"));
            Check("pi-web step allows scripts", steps[0].AllowScripts && !steps[0].IgnoreScripts);
            Check("pi cli runs second", steps[1].Packages.Contains("@earendil-works/pi-coding-agent@latest"));
            Check("pi cli step ignores scripts", steps[1].IgnoreScripts && !steps[1].AllowScripts);
            Check("pi-web step is not the pi cli step", !Installer.IsPiCliStep(steps[0], config));
            Check("pi cli step is recognised", Installer.IsPiCliStep(steps[1], config));

            ProbeResult webMissingCliPresent = new ProbeResult();
            webMissingCliPresent.PiCliFound = true;
            webMissingCliPresent.PiCliVersion = "1.0.0";
            List<InstallStep> firstRun = Installer.StepsToInstall(webMissingCliPresent, config, false);
            CheckEqual("first-run install updates an existing pi cli", 2, firstRun.Count);
            Check("first-run install includes the pi cli",
                firstRun[1].Packages.Contains("@earendil-works/pi-coding-agent@latest"));

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

        private static void UpdateTests()
        {
            Check("newer patch", Updater.IsNewer("v0.1.1", "0.1.0"));
            Check("same version", !Updater.IsNewer("v0.1.0", "0.1.0"));
            Check("older version", !Updater.IsNewer("0.0.9", "0.1.0"));
            Check("dev always updates", Updater.IsNewer("v0.1.0", "dev"));
            CheckEqual("normalize tag", "1.2.3", Updater.NormalizeVersion("v1.2.3"));
            CheckEqual("normalize empty", "", Updater.NormalizeVersion(""));
            Check("windows archive", Updater.IsWindowsArchive("pi-web-tray-0.2.0-win.zip"));
            Check("checksum is not an archive", !Updater.IsWindowsArchive("pi-web-tray-0.2.0-win.zip.sha256"));
            Check("other archive rejected", !Updater.IsWindowsArchive("source.tar.gz"));
            Check("trusted github url", Updater.IsTrustedUrl(
                "https://github.com/Arondight/pi-web-tray/releases/download/v0.2.0/x.zip"));
            Check("trusted objects url", Updater.IsTrustedUrl("https://objects.githubusercontent.com/x"));
            Check("plain http rejected", !Updater.IsTrustedUrl("http://github.com/x"));
            Check("other host rejected", !Updater.IsTrustedUrl("https://example.com/x"));

            string hash = new string('a', 64);
            CheckEqual("checksum parsed", hash, Updater.ParseChecksum(hash + "  pi-web-tray-0.2.0-win.zip\r\n"));
            CheckEqual("checksum star format", hash, Updater.ParseChecksum(hash + " *file.zip"));
            CheckEqual("checksum rejects short", "", Updater.ParseChecksum("abc  file.zip"));
            CheckEqual("checksum rejects junk", "",
                Updater.ParseChecksum(new string('z', 64) + "  file.zip"));

            string staging;
            string target;
            string logPath;
            string language;
            string dataDir;
            string stagingHash;
            int parentPid;
            ApplyStartMode mode;
            bool originalElevated;
            string stagingHashArg = new string('a', 64);
            bool parsed = Updater.TryParseApplyArgs(
                new string[]
                {
                    "--apply-update", "C:\\stage", "C:\\target", "1234",
                    "tray-only", "0", "C:\\update.log", "zh", "C:\\data", stagingHashArg
                },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash);
            Check("helper args parse", parsed);
            CheckEqual("helper staging", "C:\\stage", staging);
            CheckEqual("helper target", "C:\\target", target);
            CheckEqual("helper parent pid", 1234, parentPid);
            CheckEqual("helper mode", ApplyStartMode.TrayOnly, mode);
            CheckEqual("helper elevated", false, originalElevated);
            CheckEqual("helper log", "C:\\update.log", logPath);
            CheckEqual("helper language", "zh", language);
            CheckEqual("helper data dir", "C:\\data", dataDir);
            CheckEqual("helper staging hash", stagingHashArg, stagingHash);

            bool legacyParsed = Updater.TryParseApplyArgs(
                new string[]
                {
                    "--apply-update", "C:\\stage", "C:\\target", "1234",
                    "tray-only", "0", "C:\\update.log", "zh", "C:\\data"
                },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash);
            Check("legacy helper args parse", legacyParsed);
            CheckEqual("legacy helper has no hash", "", stagingHash);

            bool silentParsed = Updater.TryParseApplyArgs(
                new string[]
                {
                    "--apply-update", "C:/stage", "C:/target", "1234",
                    "service-silent", "0", "C:/update.log", "en", "C:/data", stagingHashArg
                },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash);
            Check("service-silent mode parsed", silentParsed && mode == ApplyStartMode.ServiceSilent);

            Check("helper args reject missing values", !Updater.TryParseApplyArgs(
                new string[] { "--apply-update", "C:\\stage" },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash));

            string error;
            string json = @"{
  ""tag_name"": ""v0.2.0"",
  ""assets"": [
    { ""name"": ""pi-web-tray-0.2.0-win.zip"",
      ""browser_download_url"": ""https://github.com/Arondight/pi-web-tray/releases/download/v0.2.0/pi-web-tray-0.2.0-win.zip"",
      ""size"": 12345 },
    { ""name"": ""pi-web-tray-0.2.0-win.zip.sha256"",
      ""browser_download_url"": ""https://github.com/Arondight/pi-web-tray/releases/download/v0.2.0/pi-web-tray-0.2.0-win.zip.sha256"",
      ""size"": 100 }
  ]
}";
            ReleaseInfo parsedRelease = Updater.ParseRelease(json, out error);
            Check("release parsed", parsedRelease != null && error.Length == 0);
            if (parsedRelease != null)
            {
                CheckEqual("release tag", "v0.2.0", parsedRelease.Tag);
                CheckEqual("release version", "0.2.0", parsedRelease.Version);
                CheckEqual("release archive", "pi-web-tray-0.2.0-win.zip", parsedRelease.ZipName);
                CheckEqual("release checksum", "pi-web-tray-0.2.0-win.zip.sha256", parsedRelease.ChecksumName);
            }

            ReleaseInfo noAssets = Updater.ParseRelease("{\"tag_name\":\"v0.2.0\",\"assets\":[]}", out error);
            Check("release without assets is rejected", noAssets == null && error.Length > 0);

            string updateLogPath = Path.Combine(Path.GetTempPath(), "pi-web-tray-log-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                Updater.AppendLog(updateLogPath, "first line");
                Updater.AppendLog(updateLogPath, "second line");

                byte[] bytes = File.ReadAllBytes(updateLogPath);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string logText = Encoding.UTF8.GetString(bytes);

                Check("update log has no BOM", !bom);
                Check("update log keeps first line", logText.Contains("first line"));
                Check("update log keeps second line", logText.Contains("second line"));
                Check("update log ends with a newline", logText.EndsWith(Environment.NewLine, StringComparison.Ordinal));
            }
            finally
            {
                try { File.Delete(updateLogPath); } catch (Exception) { }
            }
        }

        private static void InstallServiceStopTests()
        {
            Check("install stops a running service",
                TrayApp.ShouldStopServiceForInstall(Phase.Running, false, false, false));
            Check("install stops a pi-web listener in a stopped state",
                TrayApp.ShouldStopServiceForInstall(Phase.Stopped, true, true, false));
            Check("install stops a tray-owned listener",
                TrayApp.ShouldStopServiceForInstall(Phase.PortConflict, true, false, true));
            Check("install ignores a closed port",
                !TrayApp.ShouldStopServiceForInstall(Phase.Stopped, false, true, true));
            Check("install ignores an unrelated listener",
                !TrayApp.ShouldStopServiceForInstall(Phase.PortConflict, true, false, false));
        }

        private static void UpdateCancellationTests()
        {
            UpdateCancellation cancellation = new UpdateCancellation();
            Check("update cancellation starts active", !cancellation.IsCancelled);
            cancellation.Release(null);
            cancellation.Cancel();
            Check("update cancellation sets flag", cancellation.IsCancelled);
            cancellation.Cancel();
            Check("update cancellation is idempotent", cancellation.IsCancelled);
        }

        private static void UpdateCopyTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "pi-web-tray-update-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(root, "target");
            string staging = Path.Combine(root, "staging");
            string logPath = Path.Combine(root, "update.log");

            try
            {
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(Path.Combine(staging, "zh-Hans"));
                Directory.CreateDirectory(Path.Combine(staging, "config"));

                File.WriteAllText(Path.Combine(target, "pi-web-tray.exe"), "old");
                File.WriteAllText(Path.Combine(target, "pi-web-tray.ini"), "user");
                File.WriteAllText(Path.Combine(target, "server.log"), "log");
                Directory.CreateDirectory(Path.Combine(target, "zh-Hant"));
                File.WriteAllText(Path.Combine(target, "zh-Hant", "old.resources.dll"), "old");
                File.WriteAllText(Path.Combine(target, "old.txt"), "old");
                File.WriteAllText(Path.Combine(root, "outside.txt"), "keep");
                File.WriteAllText(Path.Combine(target, Updater.ManifestName),
                    "pi-web-tray.exe" + Environment.NewLine
                    + "./old.txt" + Environment.NewLine
                    + "zh-Hant/old.resources.dll" + Environment.NewLine
                    + "../outside.txt" + Environment.NewLine
                    + "sub/../pi-web-tray.exe" + Environment.NewLine);

                File.WriteAllText(Path.Combine(staging, "pi-web-tray.exe"), "new");
                File.WriteAllText(Path.Combine(staging, "pi-web-tray.exe.config"), "config");
                File.WriteAllText(Path.Combine(staging, "zh-Hans", "pi-web-tray.resources.dll"), "dll");
                File.WriteAllText(Path.Combine(staging, "config", "pi-web-tray.ini"), "sample");
                File.WriteAllText(Path.Combine(staging, "pi-web-tray.ini"), "should-not-copy");
                File.WriteAllText(Path.Combine(staging, "server.log"), "should-not-copy");

                string stagingHash1 = Updater.ComputeStagingHash(staging);
                string stagingHash2 = Updater.ComputeStagingHash(staging);
                CheckEqual("staging hash stable", stagingHash1, stagingHash2);
                Check("staging hash length", stagingHash1.Length == 64);

                File.WriteAllText(Path.Combine(staging, Updater.ManifestName),
                    "attacker.txt" + Environment.NewLine);
                CheckEqual("staging hash ignores the release manifest",
                    stagingHash1, Updater.ComputeStagingHash(staging));

                File.WriteAllText(Path.Combine(staging, "server.log"), "changed-skip");
                File.WriteAllText(Path.Combine(staging, "pi-web-tray.ini"), "changed-skip");
                CheckEqual("staging hash ignores files that are not copied",
                    stagingHash1, Updater.ComputeStagingHash(staging));

                File.WriteAllText(Path.Combine(staging, "pi-web-tray.exe"), "tampered");
                Check("staging hash detects file changes",
                    stagingHash1 != Updater.ComputeStagingHash(staging));
                File.WriteAllText(Path.Combine(staging, "pi-web-tray.exe"), "new");

                File.WriteAllText(Path.Combine(staging, "extra.txt"), "extra");
                Check("staging hash detects added files",
                    stagingHash1 != Updater.ComputeStagingHash(staging));
                File.Delete(Path.Combine(staging, "extra.txt"));

                Updater.CopyFiles(staging, target, logPath);

                CheckEqual("update replaces exe", "new", File.ReadAllText(Path.Combine(target, "pi-web-tray.exe")));
                CheckEqual("update copies config", "config", File.ReadAllText(Path.Combine(target, "pi-web-tray.exe.config")));
                CheckEqual("update keeps user ini", "user", File.ReadAllText(Path.Combine(target, "pi-web-tray.ini")));
                CheckEqual("update keeps log", "log", File.ReadAllText(Path.Combine(target, "server.log")));
                CheckEqual("update copies culture", "dll",
                    File.ReadAllText(Path.Combine(target, "zh-Hans", "pi-web-tray.resources.dll")));
                CheckEqual("update copies the sample config", "sample",
                    File.ReadAllText(Path.Combine(target, "config", "pi-web-tray.ini")));
                Check("update removes stale file", !File.Exists(Path.Combine(target, "old.txt")));
                Check("update removes empty stale directory",
                    !Directory.Exists(Path.Combine(target, "zh-Hant")));
                Check("manifest never deletes outside the target",
                    File.Exists(Path.Combine(root, "outside.txt")));
                string manifest = File.ReadAllText(Path.Combine(target, Updater.ManifestName));
                Check("manifest lists new executable", manifest.Contains("pi-web-tray.exe"));
                Check("manifest lists copied config", manifest.Contains("config/pi-web-tray.ini"));
                Check("manifest drops stale file", !manifest.Contains("old.txt"));
                Check("update leaves no backup directory",
                    Directory.GetDirectories(target, ".pi-web-tray-backup-*").Length == 0);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void UpdateTargetTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "pi-web-tray-target-" + Guid.NewGuid().ToString("N"));
            string valid = Path.Combine(root, "valid");
            string invalid = Path.Combine(root, "invalid");
            string missing = Path.Combine(root, "missing");

            try
            {
                Directory.CreateDirectory(valid);
                Directory.CreateDirectory(invalid);
                File.Copy(typeof(Updater).Assembly.Location, Path.Combine(valid, "pi-web-tray.exe"));
                File.WriteAllText(Path.Combine(invalid, "pi-web-tray.exe"), "not an executable");

                string reason;
                Check("recognises a Pi Web Tray installation",
                    Updater.IsPiWebTrayInstallation(valid, out reason));
                Check("rejects a directory without pi-web-tray.exe",
                    !Updater.IsPiWebTrayInstallation(missing, out reason));
                Check("rejects a non-Pi-Web-Tray executable",
                    !Updater.IsPiWebTrayInstallation(invalid, out reason));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void ServiceProbeTests()
        {
            string netstat = string.Join(Environment.NewLine, new string[]
            {
                "  TCP    127.0.0.1:30141        0.0.0.0:0              LISTENING       1234",
                "  TCP    [::1]:30141            [::]:0                 LISTENING       5678",
                "  TCP    127.0.0.1:30142        0.0.0.0:0              LISTENING       9999"
            });
            CheckEqual("port owner pid IPv4", 1234, ServiceProbe.ParseListenerPid(netstat, 30141));
            string ipv6 = string.Join(Environment.NewLine, new string[]
            {
                "  TCP    [::]:30141             [::]:0                 LISTENING       2468",
                "  UDP    0.0.0.0:30141          *:*                                    1357"
            });
            CheckEqual("port owner pid IPv6", 2468, ServiceProbe.ParseListenerPid(ipv6, 30141));
            CheckEqual("port owner pid no match", 0, ServiceProbe.ParseListenerPid(netstat, 30143));
            CheckEqual("port owner pid empty", 0, ServiceProbe.ParseListenerPid("", 30141));

            Check("pi-web command line js", ServiceProbe.IsPiWebCommandLine(
                @"""C:\Program Files\nodejs\node.exe"" ""C:\Users\u\AppData\Roaming\npm\node_modules\@agegr\pi-web\bin\pi-web.js"" --no-open -p 30141"));
            Check("pi-web command line package", ServiceProbe.IsPiWebCommandLine(
                "node /usr/lib/node_modules/@agegr/pi-web/bin/pi-web.js"));
            Check("other node command rejected", !ServiceProbe.IsPiWebCommandLine(
                @"node C:\work\other.js"));
            Check("empty command rejected", !ServiceProbe.IsPiWebCommandLine(""));

            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            Check("native parent pid", NativeProcess.ParentPid(self) > 0);
            Check("native image path", NativeProcess.ImagePath(self).Length > 0);
            Check("native command line", NativeProcess.CommandLine(self).Length > 0);

            if (Environment.Is64BitOperatingSystem)
            {
                string wow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "SysWOW64", "cmd.exe");
                if (File.Exists(wow64))
                {
                    try
                    {
                        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                        psi.FileName = wow64;
                        psi.Arguments = "/c ping -n 4 127.0.0.1 >nul";
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        using (System.Diagnostics.Process child = System.Diagnostics.Process.Start(psi))
                        {
                            try
                            {
                                if (!child.HasExited)
                                {
                                    string wowLine = NativeProcess.CommandLine(child.Id);
                                    Check("wow64 command line", wowLine.IndexOf(
                                        "cmd.exe", StringComparison.OrdinalIgnoreCase) >= 0);
                                }
                            }
                            finally
                            {
                                try { Sys.KillTree(child.Id); } catch (Exception) { }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // SysWOW64 is optional in constrained test environments.
                    }
                }
            }
            Check("system tool path is absolute", Path.IsPathRooted(Sys.SystemExecutable("netstat.exe")));
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
