// Pi Web Tray — restart the new tray
//
// Partial of Updater. Kept separate so the update workflow stays readable.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PiWebTray
{
    internal static partial class Updater
    {
        private static bool Restart(string target, ApplyStartMode mode, bool originalElevated,
            int parentPid, string logPath, string dataDir)
        {
            string exe = Path.Combine(target, "pi-web-tray.exe");
            string arguments = mode == ApplyStartMode.TrayOnly ? "--tray-only"
                : mode == ApplyStartMode.ServiceSilent ? "--start-no-open" : "";
            bool helperElevated = Elevation.IsElevated();

            // An elevated helper would normally create an elevated child. In the common
            // case (no extra arguments) let Explorer launch it with the logged-on user's
            // medium token, then confirm that the new tray wrote its state file. If that
            // is not observable, fall back to a direct start.
            //
            // Explorer does not preserve PI_WEB_TRAY_* overrides, so skip this path when
            // the current instance uses a custom data directory, port or language.
            if (!originalElevated && helperElevated && !HasCustomEnvironment())
            {
                string cmdPath = null;
                try
                {
                    if (arguments.Length == 0)
                    {
                        AppendLog(logPath, "restart via explorer (de-elevate)");
                        ProcessStartInfo viaExplorer = new ProcessStartInfo();
                        viaExplorer.FileName = ExplorerExe();
                        viaExplorer.Arguments = Shell.QuoteArg(exe);
                        viaExplorer.WorkingDirectory = target;
                        viaExplorer.UseShellExecute = true;
                        using (Process explorer = Process.Start(viaExplorer))
                        {
                        }
                    }
                    else
                    {
                        cmdPath = LaunchViaExplorerCommand(dataDir, target, arguments, logPath);
                    }

                    if (arguments.Length == 0 || cmdPath != null)
                    {
                        if (WaitForNewTray(dataDir, parentPid, 10000))
                        {
                            AppendLog(logPath, "new tray observed after explorer restart");
                            return true;
                        }
                        AppendLog(logPath, "explorer restart not observed; falling back");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog(logPath, "explorer restart failed: " + ex.Message);
                }
                finally
                {
                    if (cmdPath != null)
                    {
                        try { File.Delete(cmdPath); } catch (Exception) { }
                    }
                }
            }

            try
            {
                AppendLog(logPath, "restart: " + exe + " " + arguments);
                ProcessStartInfo direct = new ProcessStartInfo();
                direct.FileName = exe;
                direct.Arguments = arguments;
                direct.WorkingDirectory = target;
                direct.UseShellExecute = true;
                using (Process tray = Process.Start(direct))
                {
                    if (tray == null) return false;
                    if (WaitForTrayProcess(tray, dataDir, parentPid, 15000)) return true;
                }
                AppendLog(logPath, "new tray did not start");
                return false;
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "restart failed: " + ex);
                return false;
            }
        }

        private static bool WaitForTrayProcess(Process tray, string dataDir, int oldPid, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (WaitForNewTray(dataDir, oldPid, 250)) return true;
                try { if (tray.HasExited) return false; }
                catch (Exception) { return false; }
            }
            // Still alive but no state file (for example an unwritable data directory):
            // keep it rather than rolling back a working process.
            try { return !tray.HasExited; } catch (Exception) { return false; }
        }

        private static string LaunchViaExplorerCommand(string dataDir, string target,
            string arguments, string logPath)
        {
            try
            {
                string exe = Path.Combine(target, "pi-web-tray.exe");
                string safeTarget = target.Replace("%", "%%");
                string safeExe = exe.Replace("%", "%%");
                string cmdPath = Path.Combine(dataDir,
                    ".pi-web-tray-restart-" + Guid.NewGuid().ToString("N") + ".cmd");

                string content = "@echo off\r\n"
                    + "cd /d " + Shell.QuoteArg(safeTarget) + "\r\n"
                    + "start \"\" " + Shell.QuoteArg(safeExe) + " " + arguments + "\r\n"
                    + "del /f /q \"%~f0\" >nul 2>&1\r\n";

                Directory.CreateDirectory(dataDir);
                File.WriteAllText(cmdPath, content, Encoding.Default);
                AppendLog(logPath, "restart command: " + cmdPath);

                ProcessStartInfo viaExplorer = new ProcessStartInfo();
                viaExplorer.FileName = ExplorerExe();
                viaExplorer.Arguments = Shell.QuoteArg(cmdPath);
                viaExplorer.WorkingDirectory = target;
                viaExplorer.UseShellExecute = true;
                using (Process explorer = Process.Start(viaExplorer))
                {
                }
                return cmdPath;
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "explorer command failed: " + ex.Message);
                return null;
            }
        }

        // explorer.exe lives in %windir%, not in System32, and it must never be started
        // by its bare name: ShellExecute resolves a bare name through the caller's current
        // directory and PATH, so an imposter explorer.exe in a writable directory would run
        // elevated here. Resolve the absolute path instead, mirroring Sys.SystemExecutable.
        private static string ExplorerExe()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");
        }

        private static bool HasCustomEnvironment()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_INSTANCE"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORT"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_LANG"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORTABLE"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_NO_NODE"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_NO_PIWEB"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_NO_PICLI"));
        }

        private static bool WaitForNewTray(string dataDir, int oldPid, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            string path = Path.Combine(dataDir, "state.ini");

            while (DateTime.Now < deadline)
            {
                Thread.Sleep(250);
                try
                {
                    if (!File.Exists(path)) continue;
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine.Trim();
                        if (!line.StartsWith("Pid=", StringComparison.OrdinalIgnoreCase)) continue;

                        int pid;
                        if (!int.TryParse(line.Substring(4).Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out pid))
                        {
                            continue;
                        }
                        if (pid > 0 && pid != oldPid && Sys.ProcessAlive(pid)) return true;
                    }
                }
                catch (Exception)
                {
                }
            }
            return false;
        }
    }
}
