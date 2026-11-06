// Pi Web Tray — process lifecycle integration tests
//
// These tests launch an isolated tray process with a temporary data directory,
// a free port and the missing-Node test hook. They cover the single-instance,
// restart handoff and quit paths without touching the user's real tray or
// starting Node/npm.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWebTray
{
    [TestClass]
    public sealed class LifecycleTests
    {
        [TestMethod]
        public void TrayLifecycleHandlesDuplicateRestartAndQuit()
        {
            string app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pi-web-tray.exe");
            Assert.IsTrue(File.Exists(app), "The test output must contain pi-web-tray.exe.");

            string home = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-lifecycle-" + Guid.NewGuid().ToString("N"));
            string instance = "Lifecycle" + Guid.NewGuid().ToString("N").Substring(0, 12);
            int port = FreePort();
            List<Process> children = new List<Process>();
            Directory.CreateDirectory(home);

            try
            {
                Process oldTray = Start(app, home, instance, port, "--tray-only", children);
                string stateHome = Path.Combine(home, "pi-web-tray." + instance);
                int oldPid = WaitForState(stateHome, 0, 10000);
                Assert.IsTrue(oldPid > 0, "The first tray did not write its state file.");
                Assert.IsFalse(oldTray.HasExited);

                Process duplicate = Start(app, home, instance, port, "--tray-only", children);
                Assert.IsTrue(duplicate.WaitForExit(5000), "A duplicate tray did not exit.");
                Assert.AreEqual(0, duplicate.ExitCode, "A duplicate tray should be a no-op.");
                Assert.IsFalse(oldTray.HasExited);

                Process restart = Start(app, home, instance, port,
                    "--restart --tray-only", children);
                Assert.IsFalse(restart.HasExited, "The restart helper exited before the old tray.");
                Process quitOld = Start(app, home, instance, port, "--quit", children);
                Assert.IsTrue(oldTray.WaitForExit(15000), "The old tray did not exit.");
                int newPid = WaitForState(stateHome, oldPid, 15000);
                Assert.IsTrue(newPid > 0, "The restarted tray did not write its state file.");
                Assert.AreNotEqual(oldPid, newPid);

                using (Process newTray = Process.GetProcessById(newPid))
                {
                    children.Add(newTray);
                    Assert.IsFalse(newTray.HasExited, "The restarted tray is not running.");
                    StopQuietly(quitOld);

                    Process quitNew = Start(app, home, instance, port, "--quit", children);
                    Assert.IsTrue(newTray.WaitForExit(15000), "The restarted tray did not quit.");
                    Assert.IsTrue(quitNew.WaitForExit(15000), "The second quit command did not finish.");
                }
            }
            finally
            {
                foreach (Process child in children)
                {
                    try { if (!child.HasExited) child.Kill(); } catch (Exception) { }
                    try { child.Dispose(); } catch (Exception) { }
                }
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(home)) Directory.Delete(home, true);
                        break;
                    }
                    catch (Exception)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
        }

        [TestMethod]
        public void UpdateApplyReplacesFilesAndRestarts()
        {
            string source = AppDomain.CurrentDomain.BaseDirectory;
            string sourceExe = Path.Combine(source, "pi-web-tray.exe");
            Assert.IsTrue(File.Exists(sourceExe), "The test output must contain pi-web-tray.exe.");

            string root = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-update-lifecycle-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(root, "target");
            string home = Path.Combine(root, "data");
            string instance = "Update" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string dataDir = Path.Combine(home, "pi-web-tray." + instance);
            string staging = Path.Combine(dataDir, "staging");
            string log = Path.Combine(dataDir, "update.log");
            int port = FreePort();
            Process helper = null;
            Process updatedTray = null;
            int updatedPid = 0;

            try
            {
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(staging);
                CopyReleaseFiles(source, target);
                CopyReleaseFiles(source, staging);
                File.WriteAllText(Path.Combine(staging, "update-marker.txt"), "updated",
                    new UTF8Encoding(false));

                string hash = Updater.ComputeStagingHash(staging);
                string arguments = Updater.BuildApplyArguments(staging, target, 0,
                    ApplyStartMode.TrayOnly, false, log, "en", dataDir, hash, 0);

                helper = StartIsolated(Path.Combine(target, "pi-web-tray.exe"), arguments,
                    home, instance, port);
                Assert.IsTrue(helper.WaitForExit(90000), "The update helper did not exit.");
                Assert.AreEqual(0, helper.ExitCode, "The update helper failed.");
                Assert.IsTrue(File.Exists(Path.Combine(target, "update-marker.txt")),
                    "The staged file was not copied.");

                updatedPid = WaitForState(dataDir, 0, 15000);
                Assert.IsTrue(updatedPid > 0, "The updated tray did not write its state file.");
                updatedTray = Process.GetProcessById(updatedPid);
                Assert.IsFalse(updatedTray.HasExited, "The updated tray is not running.");

                using (Process quit = StartIsolated(Path.Combine(target, "pi-web-tray.exe"),
                    "--quit", home, instance, port))
                {
                    Assert.IsTrue(quit.WaitForExit(30000), "The quit command did not finish.");
                    Assert.AreEqual(0, quit.ExitCode, "The quit command failed.");
                }
                Assert.IsTrue(updatedTray.WaitForExit(15000), "The updated tray did not quit.");
            }
            finally
            {
                if (updatedTray != null)
                {
                    try { if (!updatedTray.HasExited) updatedTray.Kill(); } catch (Exception) { }
                    try { updatedTray.Dispose(); } catch (Exception) { }
                }
                if (updatedPid > 0 && updatedTray == null)
                {
                    try
                    {
                        using (Process tray = Process.GetProcessById(updatedPid))
                            tray.Kill();
                    }
                    catch (Exception) { }
                }
                if (helper != null)
                {
                    try { if (!helper.HasExited) helper.Kill(); } catch (Exception) { }
                    try { helper.Dispose(); } catch (Exception) { }
                }
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void CopyReleaseFiles(string source, string destination)
        {
            foreach (string name in new string[] { "pi-web-tray.exe", "pi-web-tray.exe.config" })
            {
                string file = Path.Combine(source, name);
                if (File.Exists(file)) File.Copy(file, Path.Combine(destination, name), true);
            }

            foreach (string culture in new string[] { "zh-Hans", "zh-Hant" })
            {
                string directory = Path.Combine(source, culture);
                if (!Directory.Exists(directory)) continue;
                string copy = Path.Combine(destination, culture);
                Directory.CreateDirectory(copy);
                foreach (string file in Directory.GetFiles(directory))
                    File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), true);
            }
        }

        private static Process StartIsolated(string app, string arguments, string home,
            string instance, int port)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = app;
            psi.Arguments = arguments;
            psi.WorkingDirectory = Path.GetDirectoryName(app);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["PI_WEB_TRAY_HOME"] = home;
            psi.EnvironmentVariables["PI_WEB_TRAY_INSTANCE"] = instance;
            psi.EnvironmentVariables["PI_WEB_TRAY_PORT"] =
                port.ToString(CultureInfo.InvariantCulture);
            psi.EnvironmentVariables["PI_WEB_TRAY_LANG"] = "en";
            psi.EnvironmentVariables["PI_WEB_TRAY_NO_NODE"] = "1";
            Process process = Process.Start(psi);
            if (process == null) throw new InvalidOperationException("Could not start " + app);
            return process;
        }

        [TestMethod]
        public void StopWithoutTrayReturnsZeroForAClosedPort()
        {
            string app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pi-web-tray.exe");
            Assert.IsTrue(File.Exists(app), "The test output must contain pi-web-tray.exe.");

            string home = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-stop-" + Guid.NewGuid().ToString("N"));
            string instance = "Stop" + Guid.NewGuid().ToString("N").Substring(0, 12);
            int port = FreePort();
            Directory.CreateDirectory(home);
            Process command = null;
            try
            {
                command = Start(app, home, instance, port, "--stop", null);
                Assert.IsTrue(command.WaitForExit(10000), "--stop did not finish.");
                Assert.AreEqual(0, command.ExitCode,
                    "--stop should succeed when no listener is present.");
            }
            finally
            {
                if (command != null)
                {
                    try { if (!command.HasExited) command.Kill(); } catch (Exception) { }
                    try { command.Dispose(); } catch (Exception) { }
                }
                try { Directory.Delete(home, true); } catch (Exception) { }
            }
        }

        private static Process Start(string app, string home, string instance, int port,
            string arguments, List<Process> children)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = app;
            psi.Arguments = arguments;
            psi.WorkingDirectory = Path.GetDirectoryName(app);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["PI_WEB_TRAY_HOME"] = home;
            psi.EnvironmentVariables["PI_WEB_TRAY_INSTANCE"] = instance;
            psi.EnvironmentVariables["PI_WEB_TRAY_PORT"] =
                port.ToString(CultureInfo.InvariantCulture);
            psi.EnvironmentVariables["PI_WEB_TRAY_LANG"] = "en";
            // Keep the lifecycle test offline and avoid starting or installing Node.
            psi.EnvironmentVariables["PI_WEB_TRAY_NO_NODE"] = "1";
            Process process = Process.Start(psi);
            if (process == null) throw new InvalidOperationException("Could not start the tray.");
            if (children != null) children.Add(process);
            return process;
        }

        private static int FreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static int WaitForState(string stateDir, int previousPid, int timeoutMs)
        {
            string path = Path.Combine(stateDir, "state.ini");
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        foreach (string rawLine in File.ReadAllLines(path))
                        {
                            string line = rawLine.Trim();
                            if (!line.StartsWith("Pid=", StringComparison.OrdinalIgnoreCase)) continue;
                            int pid;
                            if (int.TryParse(line.Substring(4).Trim(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out pid)
                                && pid > 0 && pid != previousPid)
                            {
                                try
                                {
                                    using (Process process = Process.GetProcessById(pid))
                                    {
                                        if (!process.HasExited) return pid;
                                    }
                                }
                                catch (Exception)
                                {
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
                Thread.Sleep(100);
            }
            return 0;
        }

        private static void StopQuietly(Process process)
        {
            try
            {
                if (process == null || process.HasExited) return;
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
            }
        }
    }
}
