// Pi Web Tray — pure-logic tests
//
// Run with `dotnet test`. These tests cover parsing, comparison, quoting, ini reading,
// localisation completeness and icon encoding. They use only local loopback sockets and
// do not access external networks or change machine state.
//
// Output is English on purpose: it is developer-facing, not part of the localised UI.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
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

        [TestMethod]
        public void CmdQuoteSurvivesPercentAndSpaces()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-cmdquote-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "100% a b");
            string file = Path.Combine(directory, "payload.txt");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(file, "quoted-ok", new UTF8Encoding(false));
                string command = "type " + Shell.CmdQuote(file);
                ProcessResult run = ProcessRunner.Run(
                    Sys.SystemExecutable("cmd.exe"), Shell.CmdLine(command),
                    root, 10000, null, null);
                Assert.IsTrue(run.Started, "cmd.exe did not start.");
                Assert.IsFalse(run.TimedOut, "cmd.exe timed out.");
                Assert.AreEqual(0, run.ExitCode, "cmd.exe could not read the quoted path.");
                StringAssert.Contains(run.Output, "quoted-ok");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        [TestMethod]
        public void UpdateGateRequiresConfirmedContent()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-gate-" + Guid.NewGuid().ToString("N"));
            string gate = Path.Combine(root, "gate");
            try
            {
                Directory.CreateDirectory(root);
                Assert.IsFalse(Updater.GateConfirmed(gate), "A missing gate must not confirm.");
                File.WriteAllText(gate, "wrong", new UTF8Encoding(false));
                Assert.IsFalse(Updater.GateConfirmed(gate), "An arbitrary gate file must not confirm.");
                File.WriteAllText(gate, "confirmed", new UTF8Encoding(false));
                Assert.IsTrue(Updater.GateConfirmed(gate), "The parent confirmation was not recognized.");
                File.Delete(gate);
                Assert.IsFalse(Updater.GateConfirmed(gate), "A deleted gate must not confirm.");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        [TestMethod]
        public void TaskXmlOwnershipMatchesCurrentUser()
        {
            string sid;
            using (System.Security.Principal.WindowsIdentity identity =
                System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                Assert.IsNotNull(identity.User, "The test process has no user SID.");
                sid = identity.User.Value;
            }

            XmlDocument document = new XmlDocument();
            document.LoadXml(
                "<Task><Principals><Principal><UserId>" + sid + "</UserId></Principal></Principals>"
                + "<Triggers><LogonTrigger><UserId>" + sid + "</UserId></LogonTrigger></Triggers></Task>");
            bool explicitUser;
            Assert.IsTrue(Autostart.TaskXmlUserMatchesCurrentUser(document, out explicitUser),
                "A current-user task was treated as foreign.");
            Assert.IsTrue(explicitUser,
                "A task with an explicit current-user principal was not marked explicit.");

            document.LoadXml(
                "<Task><Principals><Principal><UserId>S-1-5-21-1-2-3-4</UserId></Principal></Principals>"
                + "<Triggers><LogonTrigger><UserId>S-1-5-21-1-2-3-4</UserId></LogonTrigger></Triggers></Task>");
            Assert.IsFalse(Autostart.TaskXmlUserMatchesCurrentUser(document),
                "Another user's task was treated as current.");

            document.LoadXml("<Task />");
            Assert.IsTrue(Autostart.TaskXmlUserMatchesCurrentUser(document, out explicitUser),
                "A legacy any-user task was rejected.");
            Assert.IsFalse(explicitUser,
                "A legacy any-user task was marked as explicitly owned.");
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
            ConfigSafetyTests();
            EnvironmentHardeningTests();
            PendingInstallTests();
            InstallerProgressTests();
            ProxySettingsTests();
            ProxyConnectionTests();
            SocksBridgeTests();
            ProxyResetTests();
            NodeDownloadTests();
            NodePresenceTests();
            NodePresenceStateTests();
            QuoteTests();
            IniReadTests();
            InstanceSuffixTests();
            InstanceHomeIsolationTests();
            OperationLockTests();
            AutostartPathTests();
            AutostartTaskXmlTests();
            TrayClickTests();
            UpdateRestartPolicyTests();
            ConfiguredValuesTests();
            NpmWorkingDirectoryTests();
            LocalizationTests();
            IconEncodingTests();
            InstallArgumentsTests();
            SafeTagTests();
            UpdateTests();
            InstallServiceStopTests();
            RestartArgumentsTests();
            StartSuppressionTests();
            PortChangeTests();
            ServiceRestoreTests();
            UpdateCancellationTests();
            UpdateCopyTests();
            UpdateCleanupTests();
            UpdateRollbackTests();
            UpdateTargetTests();
            StaleBackupTests();
            LogHostTests();
            LogViewTests();
            ServiceProbeTests();
            StateFileTests();
            HttpProbeTestsSkip();
            UpdaterExtractTests();
            ConfigDefaultTests();

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
            CheckEqual("stable is newer than the same pre-release", 1,
                VersionUtil.Compare("1.2.3", "1.2.3-beta"));
            CheckEqual("pre-release is older than stable", -1,
                VersionUtil.Compare("1.2.3-beta", "1.2.3"));
            CheckEqual("pre-release with a numeric suffix orders numerically", 1,
                VersionUtil.Compare("1.2.3-beta.10", "1.2.3-beta.2"));
            CheckEqual("identical pre-releases compare equal", 0,
                VersionUtil.Compare("1.2.3-beta.2", "1.2.3-beta.2"));
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

        private static void ConfigSafetyTests()
        {
            Check("data directory is the default", Config.ShouldUseDataDir(false, false, false));
            Check("system installs use the data directory", Config.ShouldUseDataDir(false, true, true));
            Check("portable mode is an explicit opt-in", !Config.ShouldUseDataDir(false, false, true));
            Check("isolated instances never share the ini", Config.ShouldUseDataDir(true, false, true));
            Check("the admin task is per user",
                Autostart.TaskName.StartsWith("PiWebTray-", StringComparison.Ordinal)
                && !string.Equals(Autostart.TaskName, Autostart.LegacyTaskName, StringComparison.Ordinal));
            CheckEqual("the default Run value is unchanged", "PiWebTray",
                Autostart.ScopedName(Autostart.BaseRunValueName, ""));
            CheckEqual("the Run value is scoped to this instance",
                Autostart.ScopedName(Autostart.BaseRunValueName, Instance.Suffix),
                Autostart.RunValueName);
            CheckEqual("a named instance gets a scoped Run value", "PiWebTray.PortableTest",
                Autostart.ScopedName(Autostart.BaseRunValueName, ".PortableTest"));
            CheckEqual("a named instance gets a scoped task name", "PiWebTray-S-1-5-21-1.PortableTest",
                Autostart.ScopedName("PiWebTray-S-1-5-21-1", ".PortableTest"));

            string currentSid;
            using (System.Security.Principal.WindowsIdentity identity =
                System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                currentSid = identity.User.Value;
            }
            CheckEqual("process user SID is readable", currentSid,
                NativeProcess.UserSid(System.Diagnostics.Process.GetCurrentProcess().Id));

            // Assert the full scoped name, not only a prefix: a machine-wide task
            // regression or a missing instance suffix would otherwise pass.
            CheckEqual("admin task name is scoped to this user and instance",
                Autostart.ScopedName("PiWebTray-" + currentSid, Instance.Suffix),
                Autostart.TaskName);

            CheckEqual("redacts URL user info",
                "https://***@example.test/path",
                Shell.RedactSecrets("https://user:pass@example.test/path"));
            CheckEqual("redacts SOCKS proxy user info",
                "socks5://***@127.0.0.1:1080",
                Shell.RedactSecrets("socks5://user:pass@127.0.0.1:1080"));
            CheckEqual("redacts legacy SOCKS proxy user info",
                "socks://***@127.0.0.1:1080",
                Shell.RedactSecrets("socks://user:pass@127.0.0.1:1080"));
            CheckEqual("redacts query tokens",
                "https://example.test/?token=***&x=1",
                Shell.RedactSecrets("https://example.test/?token=secret&x=1"));
            CheckEqual("keeps ordinary URLs",
                "https://registry.example.test/",
                Shell.RedactSecrets("https://registry.example.test/"));
            CheckEqual("redacts access tokens",
                "https://example.test/?access_token=***",
                Shell.RedactSecrets("https://example.test/?access_token=secret"));
            CheckEqual("redacts npm _authToken registry form",
                "//registry.example.test/:_authToken=***",
                Shell.RedactSecrets("//registry.example.test/:_authToken=npm-secret"));
            CheckEqual("redacts npm _password registry form",
                "//registry.example.test/:_password=***",
                Shell.RedactSecrets("//registry.example.test/:_password=hunter2"));
            CheckEqual("redacts npm registry path token form",
                "//registry.example.test/repository/npm/:_authToken=***",
                Shell.RedactSecrets("//registry.example.test/repository/npm/:_authToken=npm-secret"));
            CheckEqual("redacts bare npm api_key",
                "api_key=***",
                Shell.RedactSecrets("api_key=s3cr3t"));
            CheckEqual("redacts bare npm client_secret",
                "client_secret=***",
                Shell.RedactSecrets("client_secret=shh"));
            CheckEqual("redacts api_key",
                "https://example.test/?api_key=***",
                Shell.RedactSecrets("https://example.test/?api_key=s3cr3t"));
            CheckEqual("redacts private_token",
                "https://example.test/?private_token=***",
                Shell.RedactSecrets("https://example.test/?private_token=glpat-x"));
            CheckEqual("redacts client_secret",
                "https://example.test/?client_secret=***",
                Shell.RedactSecrets("https://example.test/?client_secret=shh"));

            PortOwnerInfo owner = new PortOwnerInfo
            {
                Owner = PortOwner.PiWeb,
                ScriptPath = @"C:\one\pi-web.js"
            };
            Check("accepts the same pi-web script",
                ServiceProbe.MatchesExpectedScript(owner, @"c:\ONE\pi-web.js"));
            Check("rejects another installation",
                !ServiceProbe.MatchesExpectedScript(owner, @"C:\two\pi-web.js"));
            Check("rejects a command line without a script path",
                !ServiceProbe.MatchesExpectedScript(new PortOwnerInfo { Owner = PortOwner.PiWeb }, @"C:\two\pi-web.js"));
            Check("rejects an unknown expected script",
                !ServiceProbe.MatchesExpectedScript(owner, ""));
        }

        private static void PathSplitTests()
        {
            List<string> dirs = EnvironmentProbe.SplitPath(" C:\\a ;\"C:\\b\";;c:\\A ");
            CheckEqual("path split count", 2, dirs.Count);
            CheckEqual("path split first", "C:\\a", dirs[0]);
            CheckEqual("path split second", "C:\\b", dirs[1]);
            CheckEqual("path split empty", 0, EnvironmentProbe.SplitPath("").Count);
        }

        private static void ProxySettingsTests()
        {
            ProxySettings proxy;
            string error;
            Check("empty proxy is valid", ProxySettings.TryParse("", out proxy, out error));
            CheckEqual("empty proxy kind", ProxyKind.None, proxy.Kind);
            Check("HTTP proxy parses",
                ProxySettings.TryParse("http://127.0.0.1:7890", out proxy, out error)
                && proxy.Kind == ProxyKind.Http && proxy.Host == "127.0.0.1" && proxy.Port == 7890);
            Check("SOCKS5 proxy parses",
                ProxySettings.TryParse("socks5://user:pass@127.0.0.1:1080", out proxy, out error)
                && proxy.Kind == ProxyKind.Socks5 && proxy.User == "user" && proxy.Password == "pass");
            CheckEqual("proxy display hides credentials", "socks5://127.0.0.1:1080", proxy.Display);
            Check("invalid proxy scheme rejected",
                !ProxySettings.TryParse("ftp://127.0.0.1:21", out proxy, out error));
            Check("proxy path rejected",
                !ProxySettings.TryParse("http://127.0.0.1:8080/path", out proxy, out error));
            Check("explicit proxy port zero is rejected",
                !ProxySettings.TryParse("http://127.0.0.1:0", out proxy, out error));
            Check("SOCKS5 proxy defaults to port 1080",
                ProxySettings.TryParse("socks5://127.0.0.1", out proxy, out error)
                && proxy.Port == 1080);
        }

        private static void ProxyConnectionTests()
        {
            int httpPort = StartHttpProxy();
            string error;
            Check("HTTP proxy test", ProxySettings.Test("http://127.0.0.1:" + httpPort, out error));
            int socksPort = StartSocksProxy();
            Check("SOCKS proxy test", ProxySettings.Test("socks5://127.0.0.1:" + socksPort, out error));
        }

        private static int StartHttpProxy()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] buffer = new byte[4096];
                        int total = 0;
                        while (total < buffer.Length)
                        {
                            int read = stream.Read(buffer, total, buffer.Length - total);
                            if (read <= 0) break;
                            total += read;
                            string text = Encoding.ASCII.GetString(buffer, 0, total);
                            if (text.Contains("\r\n\r\n")) break;
                        }
                        byte[] reply = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 Connection established\r\n\r\n");
                        stream.Write(reply, 0, reply.Length);
                    }
                }
                catch (Exception) { }
                finally { listener.Stop(); }
            });
            worker.IsBackground = true;
            worker.Start();
            return port;
        }

        private static int StartSocksProxy()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] greeting = new byte[3];
                        ReadExact(stream, greeting);
                        byte[] method = new byte[] { 5, 0 };
                        stream.Write(method, 0, method.Length);

                        byte[] request = new byte[4];
                        ReadExact(stream, request);
                        if (request[3] == 3)
                        {
                            int length = stream.ReadByte();
                            if (length < 0) return;
                            byte[] rest = new byte[length + 2];
                            ReadExact(stream, rest);
                        }
                        byte[] reply = new byte[]
                        {
                            5, 0, 0, 1, 127, 0, 0, 1, 0, 80
                        };
                        stream.Write(reply, 0, reply.Length);
                    }
                }
                catch (Exception) { }
                finally { listener.Stop(); }
            });
            worker.IsBackground = true;
            worker.Start();
            return port;
        }

        private static void SocksBridgeTests()
        {
            int echoPort = StartEchoServer();
            int socksPort = StartForwardingSocksProxy(echoPort);
            ProxySettings proxy;
            string error;
            Check("bridge SOCKS proxy parses",
                ProxySettings.TryParse("socks5://127.0.0.1:" + socksPort, out proxy, out error));

            using (SocksHttpBridge bridge = new SocksHttpBridge(proxy))
            using (TcpClient client = new TcpClient())
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                client.Connect(IPAddress.Loopback, bridge.Port);
                NetworkStream stream = client.GetStream();
                byte[] connect = Encoding.ASCII.GetBytes(
                    "CONNECT 127.0.0.1:" + echoPort + " HTTP/1.1\r\n"
                    + "Host: 127.0.0.1:" + echoPort + "\r\n\r\n");
                stream.Write(connect, 0, connect.Length);
                stream.Flush();

                string header = ReadHeader(stream);
                Check("bridge accepts the current process",
                    header != null && header.StartsWith("HTTP/1.1 200", StringComparison.Ordinal));
                if (header == null || !header.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
                    return;

                byte[] payload = new byte[] { 1, 2, 3 };
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
                byte[] reply = new byte[3];
                ReadExact(stream, reply);
                Check("bridge relays tunnel bytes",
                    reply[0] == 1 && reply[1] == 2 && reply[2] == 3);
            }
        }

        private static void ProxyResetTests()
        {
            ProxyTransport.Reset();
            string error;
            Check("proxy reset configures a bridge",
                ProxyTransport.Configure("socks5://127.0.0.1:1", out error));
            System.Reflection.FieldInfo field = typeof(ProxyTransport).GetField(
                "_bridge", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            SocksHttpBridge bridge = field == null ? null : field.GetValue(null) as SocksHttpBridge;
            Check("proxy bridge exists before reset", bridge != null);
            if (bridge == null)
            {
                ProxyTransport.Reset();
                return;
            }

            ProxyTransport.Reset();
            SocksHttpBridge after = field.GetValue(null) as SocksHttpBridge;
            Check("proxy reset releases the bridge", after == null);
            ProxyTransport.Reset();
        }

        private static int StartEchoServer()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] payload = new byte[3];
                        ReadExact(stream, payload);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                }
                catch (Exception) { }
                finally { listener.Stop(); }
            });
            worker.IsBackground = true;
            worker.Start();
            return port;
        }

        private static int StartForwardingSocksProxy(int echoPort)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] greeting = new byte[3];
                        ReadExact(stream, greeting);
                        stream.Write(new byte[] { 5, 0 }, 0, 2);

                        byte[] request = new byte[4];
                        ReadExact(stream, request);
                        if (request[3] == 1) ReadExact(stream, new byte[6]);
                        else if (request[3] == 4) ReadExact(stream, new byte[18]);
                        else
                        {
                            int length = stream.ReadByte();
                            if (length < 0) return;
                            ReadExact(stream, new byte[length + 2]);
                        }

                        using (TcpClient target = new TcpClient())
                        {
                            target.Connect(IPAddress.Loopback, echoPort);
                            stream.Write(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, 0, 10);
                            RelayRaw(client, target);
                        }
                    }
                }
                catch (Exception) { }
                finally { listener.Stop(); }
            });
            worker.IsBackground = true;
            worker.Start();
            return port;
        }

        private static void RelayRaw(TcpClient first, TcpClient second)
        {
            NetworkStream left = first.GetStream();
            NetworkStream right = second.GetStream();
            Thread upload = new Thread(delegate ()
            {
                try { CopyRaw(left, right); } catch (Exception) { }
            });
            Thread download = new Thread(delegate ()
            {
                try { CopyRaw(right, left); } catch (Exception) { }
            });
            upload.IsBackground = true;
            download.IsBackground = true;
            upload.Start();
            download.Start();
            upload.Join(5000);
            download.Join(5000);
        }

        private static void CopyRaw(Stream input, Stream output)
        {
            byte[] buffer = new byte[4096];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                output.Flush();
            }
        }

        private static string ReadHeader(Stream stream)
        {
            MemoryStream data = new MemoryStream();
            int state = 0;
            while (data.Length < 8192)
            {
                int value = stream.ReadByte();
                if (value < 0) break;
                data.WriteByte((byte)value);
                if ((state == 0 && value == '\r') || (state == 1 && value == '\n')
                    || (state == 2 && value == '\r') || (state == 3 && value == '\n'))
                {
                    state++;
                    if (state == 4) break;
                }
                else
                {
                    state = value == '\r' ? 1 : 0;
                }
            }
            return data.Length == 0 ? null : Encoding.ASCII.GetString(data.ToArray());
        }

        private static void ReadExact(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        private static void PendingInstallTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "pi-web-tray-pending-" + Guid.NewGuid().ToString("N"));
            Config config = new Config();
            config.OutputDir = root;
            try
            {
                Directory.CreateDirectory(root);
                Check("no pending install initially", !Installer.HasPending(config));
                File.WriteAllText(Installer.PendingMarker(config), "partial", Encoding.UTF8);
                Check("pending install is detected", Installer.HasPending(config));
                Installer.ClearPending(config);
                Check("pending install can be cleared", !Installer.HasPending(config));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void InstallerProgressTests()
        {
            Installer.ResetProgress();
            InstallProgress progress = Installer.Snapshot();
            Check("install progress resets step count", progress.StepCount == 0);
            Check("install progress resets step", progress.Step == 0);
            Check("install progress resets the last npm line",
                string.IsNullOrEmpty(progress.LastLine));
        }

        private static void NodeDownloadTests()
        {
            CheckEqual("Windows 7 Node.js download", NodeDownloads.Windows7Url,
                NodeDownloads.UrlFor(new Version(6, 1, 7601)));
            CheckEqual("Windows 8.1 Node.js download", NodeDownloads.Windows81Url,
                NodeDownloads.UrlFor(new Version(6, 3, 9600)));
            CheckEqual("newer Windows Node.js download", NodeDownloads.DefaultUrl,
                NodeDownloads.UrlFor(new Version(10, 0, 19045)));
            CheckEqual("unknown Windows Node.js download", NodeDownloads.DefaultUrl,
                NodeDownloads.UrlFor(null));
            Check("current Windows version is readable", WindowsVersion.Current.Major >= 6);
            Check("legacy download uses the pinned version",
                NodeDownloads.Windows7Url.Contains(NodeDownloads.PinnedVersion));
            Check("8.1 download uses the pinned version",
                NodeDownloads.Windows81Url.Contains(NodeDownloads.PinnedVersion));
            Check("Windows 7 uses the community build",
                NodeDownloads.UsesCommunityBuild(new Version(6, 1, 7601)));
            Check("Windows 8.1 does not use the community build",
                !NodeDownloads.UsesCommunityBuild(new Version(6, 3, 9600)));
            Check("Windows 10 does not use the community build",
                !NodeDownloads.UsesCommunityBuild(new Version(10, 0, 19045)));
            Check("no community build without a version",
                !NodeDownloads.UsesCommunityBuild(null));

            // The console hint shown by --install must carry the same link as the tray menu.
            L.Use("en");
            Check("console hint carries the download url",
                L.T("install.installNode", ProbeResult.NodeMinimum, NodeDownloads.Windows7Url)
                    .Contains(NodeDownloads.Windows7Url));

            string missingBody = L.T("node.missingBody", "Node.js was not found",
                ProbeResult.NodeMinimum, NodeDownloads.DefaultUrl);
            Check("missing Node prompt carries the minimum version",
                missingBody.Contains(ProbeResult.NodeMinimum));
            Check("missing Node prompt carries the download url",
                missingBody.Contains(NodeDownloads.DefaultUrl));
            Check("missing Node prompt keeps the reason", missingBody.Contains("was not found"));
        }

        private static void NodePresenceTests()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-node-presence-" + Guid.NewGuid().ToString("N"));
            string node = Path.Combine(root, "node.exe");
            Config config = new Config();
            config.NodePath = node;
            try
            {
                Check("configured Node candidate is listed",
                    EnvironmentProbe.NodeCandidates(config).Contains(node));
                Check("a missing configured Node path is not authoritative",
                    !EnvironmentProbe.HasUsableConfiguredNodePath(config));

                string missing = EnvironmentProbe.NodePresenceSignature(config);
                Check("missing configured Node is not in the signature",
                    missing.IndexOf(node, StringComparison.OrdinalIgnoreCase) < 0);

                Directory.CreateDirectory(root);
                File.WriteAllBytes(node, new byte[] { 0x4D, 0x5A });
                Check("an existing configured Node path is authoritative",
                    EnvironmentProbe.HasUsableConfiguredNodePath(config));
                string installed = EnvironmentProbe.NodePresenceSignature(config);
                Check("installed Node is in the signature",
                    installed.IndexOf(node, StringComparison.OrdinalIgnoreCase) >= 0);
                Check("Node signature changes when node.exe appears",
                    !string.Equals(missing, installed, StringComparison.Ordinal));

                File.Delete(node);
                Check("a removed configured Node path becomes non-authoritative",
                    !EnvironmentProbe.HasUsableConfiguredNodePath(config));
                string removed = EnvironmentProbe.NodePresenceSignature(config);
                Check("removed Node is no longer in the signature",
                    removed.IndexOf(node, StringComparison.OrdinalIgnoreCase) < 0);
                Check("Node signature changes when node.exe disappears",
                    !string.Equals(installed, removed, StringComparison.Ordinal));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void NodePresenceStateTests()
        {
            NodePresenceState state = new NodePresenceState();
            Check("node presence starts unknown", !state.Known && !state.Present);
            Check("first empty fingerprint is a silent baseline",
                !state.Update("") && state.Known && !state.Present);
            Check("still empty is not a second event", !state.Update(""));
            Check("appearing Node.js is an event",
                state.Update("node") && state.Present);
            Check("same Node.js fingerprint is quiet", !state.Update("node"));

            Check("first missing sample keeps the old state",
                !state.Update("") && state.Present);
            Check("confirmed missing Node.js is an event",
                state.Update("") && !state.Present);
            Check("missing Node.js stays quiet", !state.Update(""));
            Check("restored Node.js is an event",
                state.Update("node") && state.Present);

            NodePresenceState transient = new NodePresenceState();
            transient.Update("node");
            Check("one missing sample is ignored during an upgrade",
                !transient.Update("") && transient.Present);
            Check("Node.js returning before confirmation is quiet",
                !transient.Update("node") && transient.Present);
        }

        private static void QuoteTests()
        {
            CheckEqual("quote plain", "\"C:\\Program Files\\node.exe\"", Shell.Quote("C:\\Program Files\\node.exe"));
            CheckEqual("cmd line", "/d /v:off /s /c \"cmd\"", Shell.CmdLine("cmd"));
            CheckEqual("quote arg plain", "\"abc\"", Shell.QuoteArg("abc"));
            CheckEqual("quote arg spaces", "\"a b\"", Shell.QuoteArg("a b"));
            CheckEqual("quote arg trailing slash", "\"C:\\dir\\\\\"", Shell.QuoteArg("C:\\dir\\"));
            CheckEqual("quote arg embedded quote", "\"a\\\"b\"", Shell.QuoteArg("a\"b"));
            CheckEqual("quote arg null", "\"\"", Shell.QuoteArg(null));
            CheckEqual("cmd quote escapes percent", "\"C:\\x\"^%\"PATH\"^%\"\"",
                Shell.CmdQuote("C:\\x%PATH%"));
            bool controlRejected = false;
            try { Shell.CmdQuote("a\nb"); }
            catch (ArgumentException) { controlRejected = true; }
            Check("cmd quote rejects control characters", controlRejected);
            CheckEqual("path prefix trims trailing separators", @"C:\Dir",
                ProcessRunner.PathPrefix(@"C:\Dir\"));
            CheckEqual("path prefix preserves a drive root", @"C:\",
                ProcessRunner.PathPrefix(@"C:\"));
            CheckEqual("path prefix ignores an empty directory", "",
                ProcessRunner.PathPrefix(""));
            Check("empty path prefix does not create an empty PATH entry",
                !ProcessRunner.WithPathPrefix("").ContainsKey("PATH"));
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
                    values.Add(new KeyValuePair<string, string>("Proxy", "http://user:p\"ass;x@host:8080"));
                    values.Add(new KeyValuePair<string, string>("Language", "zh-Hant"));
                    Check("ini values written", writeConfig.SetValues(values));
                    string parsedKey;
                    string parsedValue;
                    CheckEqual("ini quoted semicolon parses", "C:/work;old",
                        Config.TryParseLine("WorkingDir=\"C:/work;old\"", out parsedKey, out parsedValue)
                            ? parsedValue : "parse-failed");
                    CheckEqual("ini doubled quote parses", "a\"b;c",
                        Config.TryParseLine("Proxy=\"a\"\"b;c\"", out parsedKey, out parsedValue)
                            ? parsedValue : "parse-failed");
                    CheckEqual("ini written port", "30200", Config.ReadIniValue(writePath, "port"));
                    CheckEqual("ini written semicolon path", "C:/work;old", Config.ReadIniValue(writePath, "WorkingDir"));
                    CheckEqual("ini written hash URL", "https://example.test/#frag", Config.ReadIniValue(writePath, "npmregistry"));
                    CheckEqual("ini written quoted value round-trips",
                        "http://user:p\"ass;x@host:8080", Config.ReadIniValue(writePath, "proxy"));
                    CheckEqual("ini written language", "zh-Hant", Config.ReadIniValue(writePath, "language"));

                    File.WriteAllText(writePath, "Port=1\nPort=2\n", new UTF8Encoding(false));
                    Check("duplicate ini key is rewritten", writeConfig.SetValue("Port", "30333"));
                    CheckEqual("duplicate ini key keeps the new value", "30333",
                        Config.ReadIniValue(writePath, "port"));
                    string duplicateText = File.ReadAllText(writePath);
                    Check("duplicate ini key leaves no stale copies",
                        !duplicateText.Contains("Port=1") && !duplicateText.Contains("Port=2"));
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
                string sanitisedSuffix = Instance.Suffix;
                Check("suffix sanitised has a stable prefix",
                    sanitisedSuffix.StartsWith(".abcd-", StringComparison.Ordinal));

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", "abcd");
                Check("sanitised names cannot collide with a literal name",
                    !string.Equals(sanitisedSuffix, Instance.Suffix, StringComparison.Ordinal));

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", "Dev");
                CheckEqual("suffix is case-normalized", ".dev", Instance.Suffix);

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", new string('x', 80));
                CheckEqual("suffix truncated with a collision-resistant hash", 42,
                    Instance.Suffix.Length);

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", "@@@");
                Check("an invalid instance does not become the default instance",
                    Instance.Suffix.Length > 1 && Instance.Suffix != ".i");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", original);
            }
        }

        private static void InstanceHomeIsolationTests()
        {
            string originalHome = Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME");
            string originalInstance = Environment.GetEnvironmentVariable("PI_WEB_TRAY_INSTANCE");
            string temp = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-home-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", temp);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", null);
                CheckEqual("HOME override is honored", Path.GetFullPath(temp), TrayState.Folder);

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", "dev");
                CheckEqual("HOME plus instance keeps the data isolated",
                    Path.Combine(Path.GetFullPath(temp), "pi-web-tray.dev"), TrayState.Folder);

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", null);
                Check("instance suffix remains in the default data path",
                    TrayState.Folder.EndsWith("pi-web-tray.dev", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", originalHome);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", originalInstance);
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception) { }
            }
        }

        private static void EnvironmentHardeningTests()
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
            psi.EnvironmentVariables["NODE_OPTIONS"] = "--require evil.js";
            psi.EnvironmentVariables["npm_config_script_shell"] = "evil.exe";
            psi.EnvironmentVariables["COR_PROFILER"] = "evil";
            psi.EnvironmentVariables["COMPlus_Test"] = "1";
            ProcessRunner.HardenEnvironment(psi, true);
            Check("elevated child drops NODE_OPTIONS", !psi.EnvironmentVariables.ContainsKey("NODE_OPTIONS"));
            Check("elevated child drops npm_config_*", !psi.EnvironmentVariables.ContainsKey("npm_config_script_shell"));
            Check("elevated child drops COR_PROFILER", !psi.EnvironmentVariables.ContainsKey("COR_PROFILER"));
            Check("elevated child drops COMPlus_*", !psi.EnvironmentVariables.ContainsKey("COMPlus_Test"));
            Check("elevated child has an absolute ComSpec",
                Path.IsPathRooted(psi.EnvironmentVariables["ComSpec"] ?? ""));

            System.Diagnostics.ProcessStartInfo normal = new System.Diagnostics.ProcessStartInfo();
            normal.EnvironmentVariables["NODE_OPTIONS"] = "--require user.js";
            ProcessRunner.HardenEnvironment(normal, false);
            Check("non-elevated child keeps user environment",
                normal.EnvironmentVariables.ContainsKey("NODE_OPTIONS"));
        }

        private static void OperationLockTests()
        {
            string name = @"Local\PiWebTray.SelfTest." + Guid.NewGuid().ToString("N");
            bool blocked = false;
            using (OperationLock first = OperationLock.TryAcquire(name))
            {
                Check("operation lock can be acquired", first != null);
                Check("a lock held by the current thread is not reported available",
                    !OperationLock.IsAvailable(name));
                if (first != null)
                {
                    Thread other = new Thread(delegate ()
                    {
                        using (OperationLock second = OperationLock.TryAcquire(name))
                            blocked = second == null;
                    });
                    other.IsBackground = true;
                    other.Start();
                    if (!other.Join(5000)) blocked = false;
                }
            }

            Check("operation lock is not re-entrant across threads", blocked);
            using (OperationLock after = OperationLock.TryAcquire(name))
                Check("operation lock is released", after != null);

            // The stop lock is acquired by the UI thread and released by the stop
            // worker. A Mutex would reject that release; the cross-thread lock must not.
            string crossName = @"Local\PiWebTray.SelfTestCross." + Guid.NewGuid().ToString("N");
            OperationLock cross = OperationLock.TryAcquireCrossThread(crossName);
            Check("cross-thread lock can be acquired", cross != null);
            if (cross != null)
            {
                Check("a held cross-thread lock is not reported available",
                    !OperationLock.IsAvailable(crossName));
                bool blockedCross = false;
                Thread probe = new Thread(delegate ()
                {
                    using (OperationLock second = OperationLock.TryAcquireCrossThread(crossName))
                        blockedCross = second == null;
                });
                probe.IsBackground = true;
                probe.Start();
                if (!probe.Join(5000)) blockedCross = false;
                Check("cross-thread lock is exclusive", blockedCross);

                bool released = false;
                Thread releaser = new Thread(delegate ()
                {
                    try { cross.Dispose(); released = true; } catch (Exception) { }
                });
                releaser.IsBackground = true;
                releaser.Start();
                if (!releaser.Join(5000)) released = false;
                Check("cross-thread lock releases from another thread", released);

                using (OperationLock after = OperationLock.TryAcquireCrossThread(crossName))
                    Check("cross-thread lock is released", after != null);
                if (!released) cross.Dispose();
                Check("a released cross-thread lock is reported available",
                    OperationLock.IsAvailable(crossName));
            }

            CheckEqual("update lock name is stable",
                OperationLock.UpdateName(@"C:\Apps\PiWebTray"),
                OperationLock.UpdateName(@"c:\apps\piwebtray\"));
            Check("update lock names differ by installation",
                !string.Equals(OperationLock.UpdateName(@"C:\Apps\One"),
                    OperationLock.UpdateName(@"C:\Apps\Two"), StringComparison.Ordinal));
            Check("operation locks use the global namespace",
                OperationLock.UpdateName(@"C:\Apps\One").StartsWith(@"Global\",
                    StringComparison.OrdinalIgnoreCase));
            CheckEqual("stop lock is scoped to the instance",
                @"Local\PiWebTray.StopLock" + Instance.Suffix, OperationLock.StopName);
            Check("stop lock does not collide with the stop event",
                !string.Equals(OperationLock.StopName, Instance.StopSignalName,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static void AutostartPathTests()
        {
            string temp = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-autostart-" + Guid.NewGuid().ToString("N"));
            string exe = Path.Combine(temp, "pi-web-tray.exe");
            try
            {
                Directory.CreateDirectory(temp);
                File.WriteAllText(exe, "test");
                Check("user-writable install is detected", Autostart.IsUserWritableInstall(exe));

                string message;
                Check("admin autostart is refused for a user-writable install",
                    !Autostart.Apply(true, true, false, exe, out message)
                    && !string.IsNullOrEmpty(message));

                string originalInstance = Environment.GetEnvironmentVariable(
                    "PI_WEB_TRAY_INSTANCE");
                try
                {
                    Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", "named-selftest");
                    Check("named instance autostart is refused instead of starting the default instance",
                        !Autostart.Apply(true, false, false, exe, out message)
                        && !string.IsNullOrEmpty(message));
                }
                finally
                {
                    Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE",
                        originalInstance);
                }

                CheckEqual("task user matches current SID", true,
                    Autostart.TaskUserIdMatchesCurrentUser(
                        System.Security.Principal.WindowsIdentity.GetCurrent().User.Value));
                Check("task user rejects another SID",
                    !Autostart.TaskUserIdMatchesCurrentUser("S-1-5-21-1-2-3-4"));
                Check("task without UserId is the legacy any-user task",
                    Autostart.TaskUserIdMatchesCurrentUser(""));

                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(programFiles))
                {
                    Check("Program Files is treated as protected",
                        !Autostart.IsUserWritableInstall(Path.Combine(programFiles, "pi-web-tray.exe")));
                }

                CheckEqual("quoted Run value target",
                    @"C:\Apps\Pi Web Tray\pi-web-tray.exe",
                    Autostart.RunValueTarget("\"C:\\Apps\\Pi Web Tray\\pi-web-tray.exe\" --autostart"));
                CheckEqual("unquoted Run value target",
                    @"C:\Apps\pi-web-tray.exe",
                    Autostart.RunValueTarget(@"C:\Apps\pi-web-tray.exe --autostart"));
                CheckEqual("empty Run value target", "", Autostart.RunValueTarget(""));

                string command = Autostart.BuildSecureDirectoryCommand(
                    @"C:\Program Files\Pi Web Tray");
                Check("repair uses icacls",
                    command.Contains(Shell.Quote(Sys.SystemExecutable("icacls.exe"))));
                Check("repair sets an Administrators owner",
                    command.Contains(" /setowner \"*S-1-5-32-544\" /t /l"));
                Check("repair resets the root ACL first",
                    command.Contains(" /reset && "));
                Check("repair removes inherited ACLs", command.Contains(" /inheritance:r"));
                Check("repair grants SYSTEM full control",
                    command.Contains("*S-1-5-18:(OI)(CI)F"));
                Check("repair grants Administrators full control",
                    command.Contains("*S-1-5-32-544:(OI)(CI)F"));
                Check("repair leaves Users read-only",
                    command.Contains("*S-1-5-32-545:(OI)(CI)RX"));
                Check("repair resets child file ACLs", command.Contains("\\*\" /reset /t /l"));
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception) { }
            }
        }

        private static void AutostartTaskXmlTests()
        {
            const string sid = "S-1-5-21-2119269988-3384628244-2020626179-1001";
            const string exe = @"C:\Program Files\Pi Web Tray\pi-web-tray.exe";
            byte[] bytes = Autostart.BuildTaskXml(exe, sid);

            XmlDocument document = new XmlDocument();
            using (MemoryStream stream = new MemoryStream(bytes))
            {
                document.Load(stream);
            }

            Check("task XML has a logon trigger",
                document.GetElementsByTagName("LogonTrigger", "*").Count == 1);
            Check("task XML has no any-user trigger",
                document.GetElementsByTagName("LogonTrigger", "*").Count == 1
                && document.GetElementsByTagName("LogonTrigger", "*")[0]
                    .SelectNodes("descendant::*[local-name()='UserId']") != null
                && document.GetElementsByTagName("LogonTrigger", "*")[0]
                    .SelectNodes("descendant::*[local-name()='UserId']").Count == 1);
            CheckEqual("task XML uses the user SID in both UserId fields", 2,
                document.GetElementsByTagName("UserId", "*").Count);
            CheckEqual("task XML trigger user", sid,
                ((XmlElement)document.GetElementsByTagName("LogonTrigger", "*")[0])
                    .GetElementsByTagName("UserId", "*")[0].InnerText);
            CheckEqual("task XML principal user", sid,
                ((XmlElement)document.GetElementsByTagName("Principal", "*")[0])
                    .GetElementsByTagName("UserId", "*")[0].InnerText);
            CheckEqual("task XML allows starting on batteries", "false",
                document.GetElementsByTagName("DisallowStartIfOnBatteries", "*")[0].InnerText);
            CheckEqual("task XML keeps running on batteries", "false",
                document.GetElementsByTagName("StopIfGoingOnBatteries", "*")[0].InnerText);
            CheckEqual("task XML has no execution time limit", "PT0S",
                document.GetElementsByTagName("ExecutionTimeLimit", "*")[0].InnerText);
            CheckEqual("task XML runs highest available", "HighestAvailable",
                document.GetElementsByTagName("RunLevel", "*")[0].InnerText);
            CheckEqual("task XML command", exe,
                document.GetElementsByTagName("Command", "*")[0].InnerText);
            CheckEqual("task XML arguments", "--autostart",
                document.GetElementsByTagName("Arguments", "*")[0].InnerText);
            Check("generated task XML is enabled", Autostart.IsTaskXmlEnabled(document));
            Check("empty task arguments still start the service",
                Autostart.TaskArgumentsStartService(""));
            Check("autostart arguments start the service",
                Autostart.TaskArgumentsStartService("--autostart"));
            Check("start-no-open arguments start the service",
                Autostart.TaskArgumentsStartService("--start-no-open"));
            Check("tray-only arguments do not satisfy start-at-login",
                !Autostart.TaskArgumentsStartService("--tray-only"));
            Check("mixed unsafe arguments are rejected",
                !Autostart.TaskArgumentsStartService("--autostart --tray-only"));

            // A task disabled in Task Scheduler has Settings/Enabled=false while the
            // logon trigger can still be true. The parser must not pick the trigger's
            // Enabled element (which is first in document order).
            const string disabledXml =
                "<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'>"
                + "<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>"
                + "<Settings><Enabled>false</Enabled></Settings></Task>";
            XmlDocument disabled = new XmlDocument();
            disabled.LoadXml(disabledXml);
            Check("disabled task settings override an enabled trigger",
                !Autostart.IsTaskXmlEnabled(disabled));

            XmlDocument noTrigger = new XmlDocument();
            noTrigger.LoadXml("<Task><Settings><Enabled>true</Enabled></Settings></Task>");
            Check("a task without a logon trigger is not treated as an enabled login task",
                !Autostart.IsTaskXmlEnabled(noTrigger));
        }

        private static void TrayClickTests()
        {
            DateTime start = new DateTime(2026, 1, 1, 12, 0, 0);
            Check("first tray click is handled",
                TrayApp.ShouldHandleTrayClick(DateTime.MinValue, start, 500));
            Check("rapid second tray click is ignored",
                !TrayApp.ShouldHandleTrayClick(start, start.AddMilliseconds(250), 500));
            Check("tray click after the debounce is handled",
                TrayApp.ShouldHandleTrayClick(start, start.AddMilliseconds(500), 500));
            Check("a slower system double-click interval is honored",
                !TrayApp.ShouldHandleTrayClick(start, start.AddMilliseconds(750), 900));
            Check("a click after a slower interval is handled",
                TrayApp.ShouldHandleTrayClick(start, start.AddMilliseconds(900), 900));
        }

        private static void UpdateRestartPolicyTests()
        {
            Check("update restarts after an unexpected stop",
                TrayApp.ShouldRestartServiceAfterUpdate(true, false, true));
            Check("update restarts a service that was running",
                TrayApp.ShouldRestartServiceAfterUpdate(true, false, false));
            Check("update does not restart a service that was stopped",
                !TrayApp.ShouldRestartServiceAfterUpdate(false, false, false));
            Check("update restores a running service with AutoRestart off",
                TrayApp.ShouldRestartServiceAfterUpdate(true, false, false));
            Check("an explicit stop wins over AutoRestart",
                !TrayApp.ShouldRestartServiceAfterUpdate(true, true, true));
            Check("a crash still restarts with AutoRestart off only when explicitly marked",
                TrayApp.ShouldRestartServiceAfterUpdate(false, false, true));

            Check("helper waits when the update starts a silent service",
                TrayApp.ShouldWaitForServicePortBeforeUpdate(true, false, false));
            Check("helper waits while our service is still listening",
                TrayApp.ShouldWaitForServicePortBeforeUpdate(false, true, true));
            Check("helper does not wait for a foreign listener after a user stop",
                !TrayApp.ShouldWaitForServicePortBeforeUpdate(false, true, false));
            Check("helper does not wait for a foreign listener when no service ran",
                !TrayApp.ShouldWaitForServicePortBeforeUpdate(false, false, false));
        }

        private static void NpmWorkingDirectoryTests()
        {
            Config config = new Config();
            config.DataDir = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-npm-data-" + Guid.NewGuid().ToString("N"));
            config.WorkingDir = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-npm-project-" + Guid.NewGuid().ToString("N"));

            string directory = EnvironmentProbe.NpmWorkingDirectory(config);
            Check("npm working directory is available",
                !string.IsNullOrEmpty(directory) && Directory.Exists(directory));
            Check("npm uses the user home instead of a data subfolder",
                string.Equals(Path.GetFullPath(directory), Path.GetFullPath(Shell.Home()),
                    StringComparison.OrdinalIgnoreCase));
            Check("npm does not recreate the old data npm folder",
                !string.Equals(Path.GetFullPath(directory),
                    Path.GetFullPath(Path.Combine(config.DataDir, "npm")),
                    StringComparison.OrdinalIgnoreCase));
        }

        private static void ConfiguredValuesTests()
        {
            string originalHome = Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME");
            string originalInstance = Environment.GetEnvironmentVariable("PI_WEB_TRAY_INSTANCE");
            string originalPort = Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORT");
            string originalLang = Environment.GetEnvironmentVariable("PI_WEB_TRAY_LANG");
            string originalPortable = Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORTABLE");
            string originalNode = Environment.GetEnvironmentVariable("NODE_TEST");
            string originalRegistry = Environment.GetEnvironmentVariable("REG_TEST");
            string originalProxy = Environment.GetEnvironmentVariable("PROXY_TEST");
            string originalTag = Environment.GetEnvironmentVariable("TAG_TEST");
            string temp = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-config-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                CheckEqual("relative config paths resolve against the installation directory",
                    Path.Combine(temp, "node.exe"), Config.ResolvePath(temp, "node.exe"));
                string iniPath = Path.Combine(temp, "pi-web-tray.ini");
                Environment.SetEnvironmentVariable("NODE_TEST", System.Windows.Forms.Application.ExecutablePath);
                Environment.SetEnvironmentVariable("REG_TEST", "https://registry.example.test");
                Environment.SetEnvironmentVariable("PROXY_TEST", "http://127.0.0.1:1");
                Environment.SetEnvironmentVariable("TAG_TEST", "latest");
                File.WriteAllText(iniPath,
                    "Port=30211\nWorkingDir=%USERPROFILE%\nLanguage=en\nInstallPi=0\n"
                    + "NodePath=%NODE_TEST%\nNpmRegistry=%REG_TEST%\n"
                    + "Proxy=%PROXY_TEST%\nInstallTag=%TAG_TEST%\n",
                    new UTF8Encoding(false));

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", temp);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", null);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORT", "30222");
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_LANG", "en");
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORTABLE", null);

                Config config = Config.Load(true);
                CheckEqual("ini port is preserved separately", 30211, config.ConfiguredPort);
                CheckEqual("environment port is effective", 30222, config.Port);
                Check("port override is recorded", config.PortFromEnvironment);
                CheckEqual("working directory keeps its raw ini value",
                    "%USERPROFILE%", config.ConfiguredWorkingDir);
                CheckEqual("node path keeps its raw ini value",
                    "%NODE_TEST%", config.ConfiguredNodePath);
                CheckEqual("node path is expanded for use",
                    System.Windows.Forms.Application.ExecutablePath, config.NodePath);
                CheckEqual("registry keeps its raw ini value",
                    "%REG_TEST%", config.ConfiguredNpmRegistry);
                CheckEqual("registry is expanded for use",
                    "https://registry.example.test", config.NpmRegistry);
                CheckEqual("proxy keeps its raw ini value",
                    "%PROXY_TEST%", config.ConfiguredProxy);
                CheckEqual("proxy is expanded for use",
                    "http://127.0.0.1:1", config.Proxy);
                CheckEqual("tag keeps its raw ini value",
                    "%TAG_TEST%", config.ConfiguredInstallTag);
                CheckEqual("tag is expanded for use", "latest", config.InstallTag);
                Check("install Pi can be disabled from the ini", !config.InstallPi);
                Check("working directory is expanded for use",
                    Directory.Exists(config.WorkingDir));

                using (SettingsForm form = new SettingsForm(config))
                {
                    System.Reflection.FieldInfo field = typeof(SettingsForm).GetField("_port",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    System.Windows.Forms.NumericUpDown port = field == null ? null : field.GetValue(form)
                        as System.Windows.Forms.NumericUpDown;
                    CheckEqual("settings shows the persisted port", 30211, port == null ? -1 : (int)port.Value);

                    System.Reflection.MethodInfo save = typeof(SettingsForm).GetMethod("SaveSettings",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    CheckEqual("settings save succeeds", true,
                        save == null ? null : save.Invoke(form, new object[] { false }));
                }

                string saved = File.ReadAllText(iniPath);
                Check("settings preserves the configured port", saved.Contains("Port=30211"));
                Check("settings preserves the disabled install Pi option",
                    saved.Contains("InstallPi=0"));
                Check("settings preserves the %USERPROFILE% template",
                    saved.Contains("WorkingDir=%USERPROFILE%"));
                Check("settings preserves the node path template",
                    saved.Contains("NodePath=%NODE_TEST%"));
                Check("settings preserves the registry template",
                    saved.Contains("NpmRegistry=%REG_TEST%"));
                Check("settings preserves the proxy template",
                    saved.Contains("Proxy=%PROXY_TEST%"));
                Check("settings preserves the tag template",
                    saved.Contains("InstallTag=%TAG_TEST%"));
                CheckEqual("environment port is still effective after save", 30222, config.Port);

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORTABLE", "1");
                Config portableHome = Config.Load(true);
                Check("HOME takes precedence over portable mode",
                    Path.GetFullPath(portableHome.IniPath).StartsWith(
                        Path.GetFullPath(temp) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase));
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORTABLE", null);

                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORT", null);
                Config withoutOverride = Config.Load(true);
                CheckEqual("configured port is effective without an override",
                    withoutOverride.ConfiguredPort, withoutOverride.Port);
                Check("no port override is recorded without the variable",
                    !withoutOverride.PortFromEnvironment);

                // A temporary UI-language override must not become the value that
                // Settings persists when the ini has no Language key.
                File.WriteAllText(iniPath, "Port=30211" + Environment.NewLine);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_LANG", "zh");
                Config languageOverride = Config.Load(false);
                CheckEqual("environment language is not persisted as configured",
                    L.Auto, languageOverride.ConfiguredLanguage);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", originalHome);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", originalInstance);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORT", originalPort);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_LANG", originalLang);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORTABLE", originalPortable);
                Environment.SetEnvironmentVariable("NODE_TEST", originalNode);
                Environment.SetEnvironmentVariable("REG_TEST", originalRegistry);
                Environment.SetEnvironmentVariable("PROXY_TEST", originalProxy);
                Environment.SetEnvironmentVariable("TAG_TEST", originalTag);
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception) { }
            }
        }

        private static void StaleBackupTests()
        {
            string target = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-backups-" + Guid.NewGuid().ToString("N"));
            string oldBackup = Path.Combine(target, ".pi-web-tray-backup-old");
            string newBackup = Path.Combine(target, ".pi-web-tray-backup-new");
            string protectedBackup = Path.Combine(target, ".pi-web-tray-backup-protected");
            string staleCopyBackup = Path.Combine(target, ".pi-web-tray-backup-stale-copy");
            string completeBackup = Path.Combine(target, ".pi-web-tray-backup-complete");
            string staleStage = Path.Combine(target, ".pi-web-tray-stage-old");
            try
            {
                Directory.CreateDirectory(oldBackup);
                Directory.CreateDirectory(newBackup);
                Directory.CreateDirectory(protectedBackup);
                Directory.CreateDirectory(staleCopyBackup);
                Directory.CreateDirectory(completeBackup);
                Directory.CreateDirectory(staleStage);
                // A failed rollback is protected by .keep; an interrupted backup has
                // only .incomplete and must be preserved; a finished backup has both
                // markers and can be retried after a failed directory delete.
                File.WriteAllText(protectedBackup + ".keep", "");
                File.WriteAllText(staleCopyBackup + ".incomplete", "");
                File.WriteAllText(completeBackup + ".incomplete", "");
                File.WriteAllText(completeBackup + ".complete", "");
                Directory.SetLastWriteTimeUtc(oldBackup, DateTime.UtcNow.AddHours(-2));
                Directory.SetLastWriteTimeUtc(newBackup, DateTime.UtcNow);
                Directory.SetLastWriteTimeUtc(protectedBackup, DateTime.UtcNow.AddHours(-2));
                Directory.SetLastWriteTimeUtc(staleCopyBackup, DateTime.UtcNow.AddHours(-2));
                Directory.SetLastWriteTimeUtc(completeBackup, DateTime.UtcNow.AddHours(-2));
                Directory.SetLastWriteTimeUtc(staleStage, DateTime.UtcNow.AddHours(-2));

                Updater.DeleteStaleBackups(target, TimeSpan.FromMinutes(30));
                Check("stale update backup is removed", !Directory.Exists(oldBackup));
                Check("recent update backup is kept", Directory.Exists(newBackup));
                Check("rollback-protected backup is never removed automatically",
                    Directory.Exists(protectedBackup));
                Check("interrupted backup without a complete marker is preserved",
                    Directory.Exists(staleCopyBackup));
                Check("finished backup is retried after a failed delete",
                    !Directory.Exists(completeBackup));

                Updater.DeleteStaleStagingSnapshots(target, TimeSpan.FromMinutes(30));
                Check("stale staging snapshot is removed", !Directory.Exists(staleStage));
            }
            finally
            {
                try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch (Exception) { }
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
            Check("npm args never include registry credentials", !withRegistry.Contains("--registry"));
            Dictionary<string, string> registryEnv = new Dictionary<string, string>();
            Installer.ApplyRegistryEnvironment(config, registryEnv);
            CheckEqual("registry uses the npm environment", "https://registry.npmmirror.com",
                registryEnv["npm_config_registry"]);

            Check("pi-web explicitly enables scripts", webArgs.Contains(" --ignore-scripts=false"));
            Check("pi-web never disables scripts", !webArgs.Contains(" --ignore-scripts "));
            Check("npm 12 allows pi-web scripts", webArgs.Contains("--allow-scripts \"@agegr/pi-web,node-pty\""));
            string web11 = Installer.BuildArguments(config, path, web, "11.6.0", false, Installer.PiWebScriptPackages);
            Check("npm 11 still enables scripts", web11.Contains(" --ignore-scripts=false"));
            Check("npm 11 has no allow-scripts flag", !web11.Contains("--allow-scripts"));

            string cliArgs = Installer.BuildArguments(config, path, cli, "12.2.1", true, "");
            Check("pi installs with --ignore-scripts", cliArgs.Contains(" --ignore-scripts"));
            Check("pi never allows scripts", !cliArgs.Contains("--allow-scripts"));

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
            List<InstallStep> steps = Installer.StepsToInstall(missing, config, false);
            CheckEqual("missing deps need two npm runs", 2, steps.Count);
            Check("pi-web runs first", steps[0].Packages.Contains("@agegr/pi-web@latest"));
            Check("pi-web step allows scripts", steps[0].AllowScripts && !steps[0].IgnoreScripts);
            Check("pi runs second", steps[1].Packages.Contains("@earendil-works/pi-coding-agent@latest"));
            Check("pi step ignores scripts", steps[1].IgnoreScripts && !steps[1].AllowScripts);
            Check("pi-web step is not the pi step", !Installer.IsPiCliStep(steps[0], config));
            Check("pi step is recognised", Installer.IsPiCliStep(steps[1], config));

            Config webOnlyConfig = new Config();
            webOnlyConfig.InstallPi = false;
            List<InstallStep> webOnly = Installer.StepsToInstall(new ProbeResult(), webOnlyConfig, false);
            CheckEqual("InstallPi=0 skips the pi step", 1, webOnly.Count);
            Check("InstallPi=0 still installs pi-web",
                webOnly[0].Packages.Contains("@agegr/pi-web@latest"));
            CheckEqual("InstallPi=0 force only installs pi-web", 1,
                Installer.PackagesToInstall(new ProbeResult(), webOnlyConfig, true).Count);
            ProbeResult optionalCli = new ProbeResult();
            optionalCli.NodeFound = true;
            optionalCli.NodeRunnable = true;
            optionalCli.NpmFound = true;
            optionalCli.NodeVersion = ProbeResult.NodeMinimum;
            optionalCli.PiWebFound = true;
            optionalCli.PiCliRequired = false;
            Check("missing Pi is optional when installation is disabled",
                !optionalCli.NeedsPiCli && optionalCli.Ready);
            Check("missing Pi still needs installation when enabled",
                new ProbeResult().NeedsPiCli);

            Check("exact version tag is recognised", Installer.IsExactVersionTag("1.2.3"));
            Check("leading v is an exact version tag", Installer.IsExactVersionTag("v1.2.3"));
            Check("a two-component version is a range, not exact",
                !Installer.IsExactVersionTag("1.2"));
            Check("a four-component value is not an npm exact version",
                !Installer.IsExactVersionTag("1.2.3.4"));
            Check("latest is not an exact version tag", !Installer.IsExactVersionTag("latest"));
            Check("range is not an exact version tag", !Installer.IsExactVersionTag("^1.2.3"));
            Check("exact version matches installed package",
                Installer.VersionMatchesTag("1.2.3", "1.2.3"));
            Check("leading v matches installed package",
                Installer.VersionMatchesTag("v1.2.3", "1.2.3"));
            Check("different exact version is rejected",
                !Installer.VersionMatchesTag("1.2.2", "1.2.3"));
            Check("pre-release is rejected for an exact stable tag",
                !Installer.VersionMatchesTag("1.2.3-beta", "1.2.3"));
            Check("dist tag cannot be version-verified",
                Installer.VersionMatchesTag("0.9.0", "latest"));
            Check("a two-component range falls back to presence verification",
                Installer.VersionMatchesTag("1.2.7", "1.2"));
            Check("missing installed version is a mismatch for an exact tag",
                !Installer.VersionMatchesTag("", "1.2.3"));
            Check("missing installed version is fine for a dist tag",
                Installer.VersionMatchesTag("", "latest"));

            Config exactConfig = new Config();
            exactConfig.InstallTag = "1.2.3";
            CheckEqual("exact pi-web tag keeps cli on latest", "latest",
                Installer.PiCliTag(exactConfig));
            List<InstallStep> exactSteps = Installer.StepsToInstall(new ProbeResult(), exactConfig, false);
            Check("exact tag is used for pi-web",
                exactSteps[0].Packages.Contains("@agegr/pi-web@1.2.3"));
            Check("exact tag is not used for pi",
                exactSteps[1].Packages.Contains("@earendil-works/pi-coding-agent@latest"));
            Check("exact tag pi step is recognised",
                Installer.IsPiCliStep(exactSteps[1], exactConfig));

            Config nextConfig = new Config();
            nextConfig.InstallTag = "next";
            CheckEqual("dist tag is shared with the pi", "next",
                Installer.PiCliTag(nextConfig));

            CheckEqual("npm warning is not the npm version", "12.2.1",
                EnvironmentProbe.LastMeaningfulLine("npm warn deprecated x\n12.2.1\n"));
            CheckEqual("npm errors alone do not provide a version", "",
                EnvironmentProbe.LastMeaningfulLine("npm error network failure\n"));

            ProbeResult webMissingCliPresent = new ProbeResult();
            webMissingCliPresent.PiCliFound = true;
            webMissingCliPresent.PiCliVersion = "1.0.0";
            List<InstallStep> firstRun = Installer.StepsToInstall(webMissingCliPresent, config, false);
            CheckEqual("first-run install updates an existing pi", 2, firstRun.Count);
            Check("first-run install includes the pi",
                firstRun[1].Packages.Contains("@earendil-works/pi-coding-agent@latest"));

            ProbeResult installed = new ProbeResult();
            installed.PiWebFound = true;
            installed.PiCliFound = true;
            CheckEqual("no packages when everything is installed", 0, Installer.PackagesToInstall(installed, config, false).Count);
            CheckEqual("force installs pi-web and pi", 2, Installer.PackagesToInstall(installed, config, true).Count);
            CheckEqual("force always installs both packages", 2, Installer.PackagesToInstall(installed, config, true).Count);
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
            CheckEqual("checksum selects the requested file", hash,
                Updater.ParseChecksum(hash + "  pi-web-tray-0.2.0-win.zip\r\n",
                    "pi-web-tray-0.2.0-win.zip"));
            CheckEqual("checksum rejects another file", "",
                Updater.ParseChecksum(hash + "  other.zip\r\n",
                    "pi-web-tray-0.2.0-win.zip"));
            CheckEqual("checksum requires a filename when one is requested", "",
                Updater.ParseChecksum(hash + "\r\n", "pi-web-tray-0.2.0-win.zip"));
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
            int parsedPort;
            ApplyStartMode mode;
            bool originalElevated;
            string parsedZip;
            string parsedChecksum;
            string parsedGate;
            string stagingHashArg = new string('a', 64);
            bool parsed = Updater.TryParseApplyArgs(
                new string[]
                {
                    "--apply-update", "C:\\data\\update\\stage", "C:\\target", "1234",
                    "tray-only", "0", "C:\\data\\update.log", "zh", "C:\\data", stagingHashArg
                },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash);
            Check("helper args parse", parsed);
            CheckEqual("helper staging", "C:\\data\\update\\stage", staging);
            CheckEqual("helper target", "C:\\target", target);
            CheckEqual("helper parent pid", 1234, parentPid);
            CheckEqual("helper mode", ApplyStartMode.TrayOnly, mode);
            CheckEqual("helper elevated", false, originalElevated);
            CheckEqual("helper log", "C:\\data\\update.log", logPath);
            CheckEqual("helper language", "zh", language);
            CheckEqual("helper data dir", "C:\\data", dataDir);
            CheckEqual("helper staging hash", stagingHashArg, stagingHash);

            bool legacyParsed = Updater.TryParseApplyArgs(
                new string[]
                {
                    "--apply-update", "C:\\data\\update\\stage", "C:\\target", "1234",
                    "tray-only", "0", "C:\\data\\update.log", "zh", "C:\\data"
                },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash);
            Check("legacy helper without hash is rejected", !legacyParsed);

            bool silentParsed = Updater.TryParseApplyArgs(
                new string[]
                {
                    "--apply-update", "C:/data/update/stage", "C:/target", "1234",
                    "service-silent", "0", "C:/data/update.log", "en", "C:/data", stagingHashArg
                },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash);
            Check("service-silent mode parsed", silentParsed && mode == ApplyStartMode.ServiceSilent);

            bool portParsed = Updater.TryParseApplyArgs(
                new string[]
                {
                    "--apply-update", "C:\\data\\update\\stage", "C:\\target", "1234",
                    "service-silent", "0", "C:\\data\\update.log", "en", "C:\\data",
                    stagingHashArg, "30141"
                },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash,
                out parsedPort);
            Check("helper args parse optional port", portParsed && parsedPort == 30141);

            bool noWaitParsed = Updater.TryParseApplyArgs(
                new string[]
                {
                    "--apply-update", "C:\\data\\update\\stage", "C:\\target", "1234",
                    "tray-only", "0", "C:\\data\\update.log", "en", "C:\\data",
                    stagingHashArg, "0"
                },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash,
                out parsedPort);
            Check("helper args accept zero wait port", noWaitParsed && parsedPort == 0);

            string gateName = @"C:\data\update\gate-test";
            bool gateParsed = Updater.TryParseApplyArgs(
                new string[]
                {
                    "--apply-update", "C:\\data\\update\\stage", "C:\\target", "1234",
                    "tray-only", "0", "C:\\data\\update.log", "en", "C:\\data",
                    stagingHashArg, "0", gateName
                },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash,
                out parsedPort, out parsedZip, out parsedChecksum, out parsedGate);
            Check("helper args parse the update gate",
                gateParsed && parsedGate == gateName && parsedZip.Length == 0);
            Check("helper argument builder carries the update gate",
                Updater.BuildApplyArguments("C:\\data\\update\\stage", "C:\\target", 1234,
                    ApplyStartMode.TrayOnly, false, "C:\\data\\update.log", "en",
                    "C:\\data", stagingHashArg, 0, "C:\\data\\update\\x.zip",
                    stagingHashArg, gateName).Contains(gateName));

            Check("helper args reject missing values", !Updater.TryParseApplyArgs(
                new string[] { "--apply-update", "C:\\stage" },
                out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash));

            Check("helper args reject a log path outside the install/data roots",
                !Updater.TryParseApplyArgs(
                    new string[]
                    {
                        "--apply-update", "C:\\data\\update\\stage", "C:\\target", "1234",
                        "tray-only", "0", "C:\\Windows\\evil.log", "en", "C:\\data", stagingHashArg
                    },
                    out staging, out target, out parentPid, out mode,
                    out originalElevated, out logPath, out language, out dataDir, out stagingHash));

            Check("helper args reject staging outside the install/data roots",
                !Updater.TryParseApplyArgs(
                    new string[]
                    {
                        "--apply-update", "C:\\Windows\\stage", "C:\\target", "1234",
                        "tray-only", "0", "C:\\data\\update.log", "en", "C:\\data", stagingHashArg
                    },
                    out staging, out target, out parentPid, out mode,
                    out originalElevated, out logPath, out language, out dataDir, out stagingHash));

            Check("helper args reject an arbitrary log file name",
                !Updater.TryParseApplyArgs(
                    new string[]
                    {
                        "--apply-update", "C:\\data\\update\\stage", "C:\\target", "1234",
                        "tray-only", "0", "C:\\data\\evil.cmd", "en", "C:\\data", stagingHashArg
                    },
                    out staging, out target, out parentPid, out mode,
                    out originalElevated, out logPath, out language, out dataDir, out stagingHash));

            Check("helper args reject staging outside the update folder",
                !Updater.TryParseApplyArgs(
                    new string[]
                    {
                        "--apply-update", "C:\\data\\staging", "C:\\target", "1234",
                        "tray-only", "0", "C:\\data\\update.log", "en", "C:\\data", stagingHashArg
                    },
                    out staging, out target, out parentPid, out mode,
                    out originalElevated, out logPath, out language, out dataDir, out stagingHash));

            Check("helper args reject a system data directory",
                !Updater.TryParseApplyArgs(
                    new string[]
                    {
                        "--apply-update", "C:\\Windows\\update\\stage", "C:\\target", "1234",
                        "tray-only", "0", "C:\\Windows\\update.log", "en", "C:\\Windows", stagingHashArg
                    },
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

                string firstLine = logText.Split(new string[] { Environment.NewLine }, 2,
                    StringSplitOptions.None)[0];
                DateTime stamp;
                Check("update log starts with a timestamp",
                    firstLine.Length > 19
                    && DateTime.TryParseExact(firstLine.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp));
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
            Check("install does not stop a foreign owner while the phase is Running",
                !TrayApp.ShouldStopServiceForInstall(Phase.Running, true, false, false));
            Check("install stops this installation while the phase is Running",
                TrayApp.ShouldStopServiceForInstall(Phase.Running, true, true, false));
        }

        private static void RestartArgumentsTests()
        {
            // Both a normal and an elevated replacement use --restart: it waits for
            // the old tray to release the mutex, so the elevated process cannot
            // observe the old service's port as a conflict.
            CheckEqual("restart while running keeps the service silent",
                "--restart --start-no-open", TrayApp.RestartArguments(true));
            CheckEqual("restart while stopped keeps the service stopped",
                "--restart --tray-only", TrayApp.RestartArguments(false));
        }

        private static void StartSuppressionTests()
        {
            Check("autostart never opens a browser",
                Program.SuppressBrowserOnStart(true, false));
            Check("silent start never opens a browser",
                Program.SuppressBrowserOnStart(false, true));
            Check("autostart plus silent start stays silent",
                Program.SuppressBrowserOnStart(true, true));
            Check("a normal start may open a browser",
                !Program.SuppressBrowserOnStart(false, false));
        }

        private static void PortChangeTests()
        {
            Check("port change allows a foreign listener while stopped",
                TrayApp.CanApplyPortChange(Phase.Stopped, true, false));
            Check("port change allows a foreign listener in a port conflict",
                TrayApp.CanApplyPortChange(Phase.PortConflict, true, false));
            Check("port change blocks a tracked service on the old port",
                !TrayApp.CanApplyPortChange(Phase.Stopped, true, true));
            Check("port change allows resolving a port conflict with a tracked wrapper",
                TrayApp.CanApplyPortChange(Phase.PortConflict, true, true));
            Check("port change allows a stopped service with a closed port",
                TrayApp.CanApplyPortChange(Phase.Stopped, false, true));
            Check("port change blocks a running service",
                !TrayApp.CanApplyPortChange(Phase.Running, false, false));
            Check("port change blocks an unverified listener",
                !TrayApp.CanApplyPortChange(Phase.RunningUnverified, false, false));
            Check("port change blocks a starting service",
                !TrayApp.CanApplyPortChange(Phase.Starting, false, false));
            Check("port change blocks an environment check",
                !TrayApp.CanApplyPortChange(Phase.Checking, false, false));
        }

        private static void ServiceRestoreTests()
        {
            ProbeResult probe = new ProbeResult();
            probe.NodeFound = true;
            probe.NodeRunnable = true;
            probe.NpmFound = true;
            probe.NodeVersion = ProbeResult.NodeMinimum;
            probe.PiWebFound = true;
            probe.PiWebScript = @"C:\pi-web\pi-web.js";
            // Pi is intentionally missing: pi-web itself can still run.
            probe.PiCliFound = false;

            Check("restore can start pi-web without Pi",
                TrayApp.CanStartServiceFromProbe(probe, delegate (string path) { return true; }));
            Check("restore rejects a missing script",
                !TrayApp.CanStartServiceFromProbe(probe, delegate (string path) { return false; }));
            Check("restore rejects a missing probe",
                !TrayApp.CanStartServiceFromProbe(null, delegate (string path) { return true; }));

            probe.NodeFound = false;
            Check("restore rejects missing Node.js",
                !TrayApp.CanStartServiceFromProbe(probe, delegate (string path) { return true; }));
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

                // The helper creates a protected snapshot and re-verifies it. Adding a
                // file after the tray computed the expected hash must abort the apply.
                MethodInfo snapshotMethod = typeof(Updater).GetMethod("SnapshotStaging",
                    BindingFlags.Static | BindingFlags.NonPublic);
                object[] snapshotArgs = new object[]
                {
                    staging, stagingHash1, target, logPath, null
                };
                object snapshotResult = snapshotMethod.Invoke(null, snapshotArgs);
                Check("helper snapshot rejects files added after the hash", snapshotResult == null);
                Check("helper snapshot leaves no staging snapshot",
                    Directory.GetDirectories(target, ".pi-web-tray-stage-*").Length == 0);
                File.Delete(Path.Combine(staging, "extra.txt"));

                UpdateCopyResult copy = Updater.CopyFiles(staging, target, logPath);

                CheckEqual("update replaces exe", "new", File.ReadAllText(Path.Combine(target, "pi-web-tray.exe")));
                CheckEqual("update copies config", "config", File.ReadAllText(Path.Combine(target, "pi-web-tray.exe.config")));
                CheckEqual("update keeps user ini", "user", File.ReadAllText(Path.Combine(target, "pi-web-tray.ini")));
                CheckEqual("update keeps log", "log", File.ReadAllText(Path.Combine(target, "server.log")));
                CheckEqual("update copies culture", "dll",
                    File.ReadAllText(Path.Combine(target, "zh-Hans", "pi-web-tray.resources.dll")));
                CheckEqual("update copies the sample config", "sample",
                    File.ReadAllText(Path.Combine(target, "config", "pi-web-tray.ini")));
                Check("update keeps a backup until restart", Directory.Exists(copy.Backup));
                Check("update marks the backup as incomplete until restart",
                    File.Exists(copy.Backup + ".incomplete"));
                Check("update marks a finished backup as complete",
                    File.Exists(copy.Backup + ".complete"));
                Check("update defers stale cleanup",
                    File.Exists(Path.Combine(target, "old.txt")));

                Updater.CleanupStaleFiles(target, copy.Managed, logPath);

                Check("update removes stale file", !File.Exists(Path.Combine(target, "old.txt")));
                Check("update removes empty stale directory",
                    !Directory.Exists(Path.Combine(target, "zh-Hant")));
                Check("manifest never deletes outside the target",
                    File.Exists(Path.Combine(root, "outside.txt")));
                string manifest = File.ReadAllText(Path.Combine(target, Updater.ManifestName));
                Check("manifest lists new executable", manifest.Contains("pi-web-tray.exe"));
                Check("manifest lists copied config", manifest.Contains("config/pi-web-tray.ini"));
                Check("manifest drops stale file", !manifest.Contains("old.txt"));

                Updater.DeleteBackup(copy.Backup);
                Check("update leaves no backup directory",
                    Directory.GetDirectories(target, ".pi-web-tray-backup-*").Length == 0);
                Check("update removes the incomplete marker",
                    !File.Exists(copy.Backup + ".incomplete"));
                Check("update removes the complete marker",
                    !File.Exists(copy.Backup + ".complete"));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void UpdateCleanupTests()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-cleanup-" + Guid.NewGuid().ToString("N"));
            string oldWork = Path.Combine(root, "old-work");
            string activeWork = Path.Combine(root, "active-work");
            try
            {
                Directory.CreateDirectory(oldWork);
                Directory.CreateDirectory(activeWork);
                File.WriteAllText(Path.Combine(activeWork, "partial.zip"), "partial");
                File.WriteAllText(Path.Combine(activeWork, "staging-marker"), "partial");
                Directory.SetLastWriteTimeUtc(oldWork, DateTime.UtcNow.AddHours(-2));
                Directory.SetLastWriteTimeUtc(activeWork, DateTime.UtcNow);

                MethodInfo cleanup = typeof(Updater).GetMethod("CleanupOld",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Check("cleanup method is available", cleanup != null);
                if (cleanup != null) cleanup.Invoke(null, new object[] { root });

                Check("old update work is removed", !Directory.Exists(oldWork));
                Check("active update work is kept", Directory.Exists(activeWork)
                    && File.Exists(Path.Combine(activeWork, "partial.zip")));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void UpdateRollbackTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "pi-web-tray-rollback-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(root, "target");
            string staging = Path.Combine(root, "staging");
            string logPath = Path.Combine(root, "update.log");
            try
            {
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(target, "pi-web-tray.exe"), "old");
                File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
                File.WriteAllText(Path.Combine(staging, "pi-web-tray.exe"), "new");
                File.WriteAllText(Path.Combine(staging, "new-only.txt"), "new");

                UpdateCopyResult copy = Updater.CopyFiles(staging, target, logPath);
                CheckEqual("rollback setup replaces exe", "new",
                    File.ReadAllText(Path.Combine(target, "pi-web-tray.exe")));
                Check("rollback setup copies new file",
                    File.Exists(Path.Combine(target, "new-only.txt")));

                Check("rollback completes", Updater.Rollback(copy.Backup, target, copy.Managed, logPath));

                CheckEqual("rollback restores the old executable", "old",
                    File.ReadAllText(Path.Combine(target, "pi-web-tray.exe")));
                Check("rollback removes files new to the release",
                    !File.Exists(Path.Combine(target, "new-only.txt")));
                Check("rollback keeps unmanaged files",
                    File.Exists(Path.Combine(target, "keep.txt")));
                Check("rollback removes the backup", !Directory.Exists(copy.Backup));
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

        private static void LogHostTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "pi-web-tray-loghost-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "server.log");
            string originalHome = Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME");
            string originalInstance = Environment.GetEnvironmentVariable("PI_WEB_TRAY_INSTANCE");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", root);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", null);
                Check("log host accepts the configured server.log",
                    LogHost.IsExpectedLogPath(path));
                Check("log host rejects an arbitrary file",
                    !LogHost.IsExpectedLogPath(Path.Combine(root, "evil.cmd")));
                Directory.CreateDirectory(path);
                Check("log host rejects a directory at the server.log path",
                    !LogHost.CanOpenForAppend(path));
                Directory.Delete(path);
                CheckEqual("log host mode rejects an arbitrary file", 2,
                    LogHost.Run(new string[] { "--log-host", Path.Combine(root, "evil.cmd") }));
                CheckEqual("log host mode rejects a missing path", 2,
                    LogHost.Run(new string[] { "--log-host" }));
                CheckEqual("log host mode rejects an invalid size", 2,
                    LogHost.Run(new string[] { "--log-host", path, "1" }));
                using (MemoryStream input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 200))))
                {
                    LogHost.Append(input, path, 100);
                }

                string first = File.ReadAllText(path);
                Check("log host writes the first chunk", first.Contains(new string('x', 200)));
                Check("log host timestamps service lines",
                    first.StartsWith(DateTime.Now.ToString("yyyy-MM-dd"), StringComparison.Ordinal));

                using (MemoryStream input = new MemoryStream(Encoding.UTF8.GetBytes("next")))
                {
                    LogHost.Append(input, path, 100);
                }
                Check("log host rotates before crossing the limit",
                    File.Exists(path + ".1") && File.ReadAllText(path + ".1").Contains(new string('x', 200)));
                string appended = File.ReadAllText(path);
                Check("log host appends after rotation",
                    appended.EndsWith(" next" + Environment.NewLine, StringComparison.Ordinal));

                File.Delete(path);
                File.Delete(path + ".1");
                using (MemoryStream input = new MemoryStream(Encoding.UTF8.GetBytes(
                    "https://user:pass@example.test/?token=secret")))
                {
                    LogHost.Append(input, path, 1024 * 1024);
                }
                string redacted = File.ReadAllText(path);
                Check("log host redacts registry credentials",
                    redacted.EndsWith(" https://***@example.test/?token=***" + Environment.NewLine,
                        StringComparison.Ordinal)
                    && !redacted.Contains("user:pass")
                    && !redacted.Contains("token=secret"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", originalHome);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", originalInstance);
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void LogViewTests()
        {
            string nl = Environment.NewLine;
            CheckEqual("log view keeps CRLF", "a" + nl + "b",
                LogForm.NormalizeLineEndings("a\r\nb"));
            CheckEqual("log view normalizes LF", "a" + nl + "b",
                LogForm.NormalizeLineEndings("a\nb"));
            CheckEqual("log view normalizes CR", "a" + nl + "b",
                LogForm.NormalizeLineEndings("a\rb"));
            CheckEqual("log view normalizes mixed endings",
                "a" + nl + "b" + nl + "c" + nl + "d",
                LogForm.NormalizeLineEndings("a\r\nb\nc\rd"));
            CheckEqual("log view leaves empty text", "", LogForm.NormalizeLineEndings(""));
            Check("log view recognizes the last line as visible",
                LogForm.IsLastLineVisible(999, 999));
            Check("log view does not treat an earlier visible line as the bottom",
                !LogForm.IsLastLineVisible(998, 999));
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
            CheckEqual("pi-web script path extraction",
                @"C:\Users\u\AppData\Roaming\npm\node_modules\@agegr\pi-web\bin\pi-web.js",
                ServiceProbe.ExtractScriptPath(
                    @"""C:\Program Files\nodejs\node.exe"" ""C:\Users\u\AppData\Roaming\npm\node_modules\@agegr\pi-web\bin\pi-web.js"" --no-open -p 30141"));
            string customScript = Path.Combine(Path.GetTempPath(), "Pi Web Scripts", "server.js");
            string customLine = "\"C:\\Program Files\\nodejs\\node.exe\" \"" +
                customScript + "\" --no-open -p 30141";
            CheckEqual("custom expected script path extraction",
                customScript, ServiceProbe.ExtractScriptPath(customLine, customScript));
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
                        // Long-lived on purpose: a short-lived child let the check silently
                        // skip before, and the child must still be alive while probed.
                        psi.Arguments = "/c ping -n 30 127.0.0.1 >nul";
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        using (System.Diagnostics.Process child = System.Diagnostics.Process.Start(psi))
                        {
                            try
                            {
                                Check("wow64 child alive during probe", !child.HasExited);
                                string wowLine = NativeProcess.CommandLine(child.Id);
                                Check("wow64 command line", wowLine.IndexOf(
                                    "cmd.exe", StringComparison.OrdinalIgnoreCase) >= 0);
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
                TrayState.ClearServiceStopRequest();
                Check("no service stop marker by default", !TrayState.ServiceStopRequested());
                TrayState.RequestServiceStop();
                Check("service stop marker is persisted", TrayState.ServiceStopRequested());
                CheckEqual("service stop marker content", "1",
                    File.ReadAllText(TrayState.ServiceStopRequestPath()).Trim());
                TrayState.ClearServiceStopRequest();
                Check("service stop marker can be cleared", !TrayState.ServiceStopRequested());
                string content = File.ReadAllText(TrayState.StatePath());
                Check("state has pid", content.Contains("Pid=0"));
                Check("state has port", content.Contains("Port=30141"));
                Check("state has session", content.Contains("Session="));

                // pid 0 is never a real process, so the stale-state guard must answer false
                Check("stale state is not trusted", !TrayState.InstanceIsElevated());

                // A state file owned by another process must not be deleted by this one.
                TrayState.Save(4, true, 30141);
                TrayState.Delete();
                Check("another process's state is kept", File.Exists(TrayState.StatePath()));
                File.Delete(TrayState.StatePath());

                // our own pid is alive -> trusted
                TrayState.Save(System.Diagnostics.Process.GetCurrentProcess().Id, true, 30141);
                Check("live state is trusted", TrayState.InstanceIsElevated());
                int runningPort;
                Check("live state reports the running port",
                    TrayState.TryGetRunningPort(out runningPort) && runningPort == 30141);

                TrayState.Save(System.Diagnostics.Process.GetCurrentProcess().Id, false, 30222);
                Check("state port can change",
                    TrayState.TryGetRunningPort(out runningPort) && runningPort == 30222);

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

            // Regression: some Windows builds report a newly created listener with a
            // bogus state (0 or FIN_WAIT_1). The listener table must still find it and
            // identify the owning process.
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                Check("open port reports open", Net.IsPortOpen(openPort, 500));
                CheckEqual("open port owner is current process",
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    TcpListenerTable.ListenerPid(openPort));
            }
            finally
            {
                listener.Stop();
            }

            // The listener table has a different row layout for IPv6; make sure the
            // PID lookup stays correct there as well. IPv6 may be disabled on old systems.
            TcpListener listener6 = null;
            try
            {
                listener6 = new TcpListener(IPAddress.IPv6Loopback, 0);
                listener6.Server.SetSocketOption(
                    SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                listener6.Start();
                int openPort6 = ((IPEndPoint)listener6.LocalEndpoint).Port;
                Check("IPv6 open port reports open", Net.IsPortOpen(openPort6, 500));
                CheckEqual("IPv6 open port owner is current process",
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    TcpListenerTable.ListenerPid(openPort6));
            }
            catch (SocketException)
            {
                // IPv6 is optional on the systems this tray supports.
            }
            finally
            {
                if (listener6 != null) listener6.Stop();
            }
        }

        // The updater's zip-slip defence had no test before; it is the only security
        // component on the extract path. A crafted archive must be rejected while its
        // harmless entries stay where they belong.
        private static void UpdaterExtractTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "pi-web-tray-extract-" + Guid.NewGuid().ToString("N"));
            string dir = Path.Combine(root, "extract");
            try
            {
                Directory.CreateDirectory(dir);

                // 1. A relative parent escape mixed with a normal entry
                string zip = Path.Combine(root, "update.zip");
                using (FileStream stream = File.Create(zip))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    WriteZipEntry(archive, "pi-web-tray.exe", "new exe");
                    WriteZipEntry(archive, "../evil.txt", "escaped");
                }

                bool threw = false;
                try { Updater.Extract(zip, dir); }
                catch (InvalidDataException) { threw = true; }
                Check("zip-slip entry rejected", threw);
                Check("zip-slip wrote nothing outside the target",
                    !File.Exists(Path.Combine(root, "evil.txt")));

                // 2. An absolute path entry
                zip = Path.Combine(root, "absolute.zip");
                using (FileStream stream = File.Create(zip))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    WriteZipEntry(archive, Path.GetPathRoot(dir).TrimEnd('\\', '/') + "\\pi-web-tray-evil.txt", "absolute");
                }

                threw = false;
                try { Updater.Extract(zip, dir); }
                catch (InvalidDataException) { threw = true; }
                Check("absolute path entry rejected", threw);

                // 3. A well-formed archive with nested entries extracts normally
                zip = Path.Combine(root, "clean.zip");
                using (FileStream stream = File.Create(zip))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    WriteZipEntry(archive, "pi-web-tray.exe", "new exe");
                    WriteZipEntry(archive, "zh-Hans/pi-web-tray.resources.dll", "dll");
                }

                Updater.Extract(zip, dir);
                CheckEqual("clean archive extracts the executable", "new exe",
                    File.ReadAllText(Path.Combine(dir, "pi-web-tray.exe")));
                CheckEqual("clean archive extracts nested entries", "dll",
                    File.ReadAllText(Path.Combine(dir, "zh-Hans", "pi-web-tray.resources.dll")));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void WriteZipEntry(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        // The documented defaults were never asserted against the code before; the ini is
        // the contract users read (README table) and Config.WriteDefaultIni writes.
        private static void ConfigDefaultTests()
        {
            string originalHome = Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME");
            string originalInstance = Environment.GetEnvironmentVariable("PI_WEB_TRAY_INSTANCE");
            string originalPort = Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORT");
            string originalLang = Environment.GetEnvironmentVariable("PI_WEB_TRAY_LANG");
            string originalPortable = Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORTABLE");
            string temp = Path.Combine(Path.GetTempPath(), "pi-web-tray-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", temp);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", null);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORT", null);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_LANG", null);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORTABLE", null);

                Config config = Config.Load(true);

                // Field defaults (used when a key is absent from the ini) are part of the
                // documented contract too; the ini value covering them is not enough.
                Config fresh = new Config();
                CheckEqual("field default port", 30141, fresh.Port);
                Check("field default auto install", fresh.AutoInstall);
                Check("field default install Pi is on", fresh.InstallPi);
                Check("field default auto restart is off", !fresh.AutoRestart);
                Check("field default auto update check is off", !fresh.AutoCheckUpdates);
                CheckEqual("field default install tag", "latest", fresh.InstallTag);
                CheckEqual("field default registry is empty", "", fresh.NpmRegistry);
                CheckEqual("field default proxy is empty", "", fresh.Proxy);
                CheckEqual("field default language", "auto", fresh.Language);
                Check("field default admin autostart is off", !fresh.AutostartAdmin);

                string ini = Path.Combine(temp, "pi-web-tray.ini");
                Check("default ini is written on first run", File.Exists(ini));

                CheckEqual("default port", 30141, config.Port);
                Check("default auto install is on", config.AutoInstall);
                Check("default install Pi is on", config.InstallPi);
                Check("default auto restart is off", !config.AutoRestart);
                Check("default auto update check is off", !config.AutoCheckUpdates);
                CheckEqual("default install tag", "latest", config.InstallTag);
                CheckEqual("default registry is empty", "", config.NpmRegistry);
                CheckEqual("default proxy is empty", "", config.Proxy);
                CheckEqual("default language", "auto", config.Language);
                Check("default admin autostart is off", !config.AutostartAdmin);
                Check("default working dir is the profile", Directory.Exists(config.WorkingDir));
                CheckEqual("default node path is empty", "", config.NodePath ?? "");

                string content = File.ReadAllText(ini);
                Check("ini documents the port", content.Contains("Port=30141"));
                Check("ini documents the working dir", content.Contains("WorkingDir=%USERPROFILE%"));
                Check("ini documents auto install", content.Contains("AutoInstall=1"));
                Check("ini documents install Pi", content.Contains("InstallPi=1"));
                Check("ini documents auto restart", content.Contains("AutoRestart=0"));
                Check("ini documents update checks", content.Contains("AutoCheckUpdates=0"));
                Check("ini documents the install tag", content.Contains("InstallTag=latest"));
                Check("ini documents npm registry", content.Contains("NpmRegistry="));
                Check("ini documents proxy", content.Contains("Proxy="));
                Check("ini documents node path", content.Contains("NodePath="));
                Check("ini documents the admin autostart", content.Contains("AutostartAdmin=0"));
                Check("ini documents the language", content.Contains("Language=auto"));
                CheckEqual("ini documents exactly the 12 documented keys", 12, CountIniKeys(content));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_HOME", originalHome);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_INSTANCE", originalInstance);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORT", originalPort);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_LANG", originalLang);
                Environment.SetEnvironmentVariable("PI_WEB_TRAY_PORTABLE", originalPortable);
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception) { }
            }
        }

        private static int CountIniKeys(string content)
        {
            int count = 0;
            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line.IndexOf('=') > 0) count++;
            }
            return count;
        }
    }
}
