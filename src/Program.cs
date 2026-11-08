// Pi Web Tray — entry point
//
// Double-click: start pi-web (installing what is missing) and open the browser; if it runs, just open it
// "Run as administrator" on the shortcut: take over and run the whole of pi-web elevated
//
// Single instance: named mutex + port probe; clicking any number of times never starts a second pi-web
//
// All user-visible text goes through L.T("key"); see src/Localization.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PiWebTray
{
    internal static class Program
    {
        internal const string AppName = "Pi Web Tray";
        internal const string ProjectUrl = "https://github.com/Arondight/pi-web-tray";

        [STAThread]
        private static int Main(string[] args)
        {
            // The language must be known before any text appears: read ini / env first
            Config config = null;

            // Read-only commands do not accept positional arguments. Keep the
            // documented "unknown argument returns 2" behaviour consistent here too.
            if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?")
                || Has(args, "--version") || Has(args, "--lang-check"))
            {
                string earlyUnknown = FindUnknownArgument(args);
                if (earlyUnknown != null)
                {
                    // Resolve the configured language before printing the error, but
                    // keep the read-only command free of disk writes.
                    Config.Load(false);
                    ConsoleHost.TryAttach();
                    ConsoleHost.WriteError(L.T("prog.unknownArg", earlyUnknown));
                    return 2;
                }
            }

            if (Has(args, "--lang-check"))
            {
                ConsoleHost.TryAttach();
                Config.Load(false);     // also resolves the language from the config
                System.Collections.Generic.List<string> problems = L.Problems();
                if (problems.Count == 0)
                {
                    ConsoleHost.WriteLine(L.T("langcheck.ok", L.Count, L.LanguageCount));
                    return 0;
                }
                ConsoleHost.WriteLine(L.T("langcheck.fail", string.Join("; ", problems.ToArray())));
                return 1;
            }

            if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?"))
            {
                Config.Load(false);     // --help must not create files
                ShowHelp();
                return 0;
            }

            if (Has(args, "--version"))
            {
                string version = AppName + " " + Version;
                if (ConsoleHost.TryAttach()) ConsoleHost.WriteLine(version);
                else MessageBox.Show(version, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            // The update helper is a separate process started from the staged release. It must
            // be handled before the argument validation, because its paths are positional.
            if (Has(args, "--apply-update"))
            {
                Config.Load(false);
                return Updater.Apply(args);
            }

            // Internal mode: the service pipes its output here so the log can be rotated
            // while node still runs. It has no config, tray, mutex, or UI. The check mode
            // lets the tray verify that this child can be created before relying on it.
            if (Has(args, "--log-host-check"))
            {
                // The tray passes the configured log path so a path that the log
                // host would reject (reparse point, wrong name) makes the probe fail
                // and the service uses the safe fallback instead.
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--log-host-check",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length && !LogHost.IsExpectedLogPath(args[i + 1]))
                            return 2;
                        break;
                    }
                }
                return 0;
            }
            if (Has(args, "--log-host"))
            {
                return LogHost.Run(args);
            }

            config = Config.Load(true);

            string unknown = FindUnknownArgument(args);
            if (unknown != null)
            {
                ConsoleHost.TryAttach();
                ConsoleHost.WriteError(L.T("prog.unknownArg", unknown));
                return 2;
            }
            if (Has(args, "--dry-run") && !Has(args, "--install"))
            {
                ConsoleHost.TryAttach();
                ConsoleHost.WriteError(L.T("prog.dryRunOnly"));
                return 2;
            }

            if (Has(args, "--make-ico"))
            {
                ExportIconFile(config.BaseDir);
                return 0;
            }

            bool elevated = Elevation.IsElevated();

            if (Has(args, "--check"))
            {
                return RunCheck(config, elevated);
            }

            if (Has(args, "--install"))
            {
                return RunInstall(config, Has(args, "--dry-run"));
            }

            if (Has(args, "--diag"))
            {
                return RunDiag(config, elevated);
            }

            // The tray is a long-lived process: an unhandled exception must not kill it silently
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                LogCrash(e.ExceptionObject as Exception);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                LogCrash(e.Exception);
                MessageBox.Show(
                    L.T("prog.crashBody", e.Exception.Message),
                    AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            };

            bool isStop = Has(args, "--stop");
            bool isQuit = Has(args, "--quit");
            bool isAutostart = Has(args, "--autostart");
            bool isTrayOnly = Has(args, "--tray-only");
            bool isStartNoOpen = Has(args, "--start-no-open");
            bool isRestart = Has(args, "--restart");

            // Save-and-restart starts this process before the old tray exits.
            // Wait for the single-instance mutex so the old service is fully
            // stopped before the new tray starts its own listener.
            if (isRestart)
            {
                Mutex restartMutex;
                bool restartDenied;
                if (TryAcquireMutex(30000, out restartMutex, out restartDenied))
                {
                    RunTray(config, elevated, !isTrayOnly, restartMutex,
                        !isTrayOnly && !isStartNoOpen);
                    return 0;
                }
                MessageBox.Show(
                    L.T("notify.restartFailedBody"),
                    L.T("notify.restartFailed"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 1;
            }

            // Start at login: start the tray and pi-web without opening a browser.
            // --tray-only remains available for a tray without the service.
            if (isAutostart && !isStop && !isQuit)
            {
                Mutex autoMutex;
                bool autoDenied;
                if (TryAcquireMutex(0, out autoMutex, out autoDenied))
                {
                    RunTray(config, elevated, !isTrayOnly, autoMutex, false);
                }
                return 0;
            }

            Mutex mutex;
            bool accessDenied;
            bool acquired = TryAcquireMutex(0, out mutex, out accessDenied);

            if (acquired)
            {
                // No other tray is running: I become the tray
                if (isStop || isQuit)
                {
                    // There is not even a tray, so stop the service directly
                    bool stopped = StopServiceWithoutTray(config);
                    ReleaseMutex(mutex);
                    return stopped ? 0 : 1;
                }
                RunTray(config, elevated, !isTrayOnly, mutex, !isTrayOnly && !isStartNoOpen);
                return 0;
            }

            if (isStop)
            {
                // The signal only asks the tray to stop the service. A privileged tray can
                // fail to kill it from this shell; verify the port actually closed so the
                // exit code reflects reality instead of the signal delivery.
                if (!SendSignal(Instance.StopSignalName)) return 1;
                return Net.WaitPortClosed(config.Port, 30000) ? 0 : 1;
            }

            if (isQuit)
            {
                if (!SendSignal(Instance.QuitSignalName)) return 1;
                // A closed port only proves that the service stopped, not that the
                // tray itself exited. Wait for the single-instance mutex so a
                // following command sees a genuinely free instance.
                if (!WaitForTrayExit(30000)) return 1;
                // The tray may have exited while a privileged or foreign listener
                // survived the stop. Do not report success in that case.
                return Net.WaitPortClosed(config.Port, 5000) ? 0 : 1;
            }

            bool portOpen = Net.IsPortOpen(config.Port, 300);

            // Cannot open the other instance's mutex: it runs at a higher integrity level (admin)
            if (accessDenied)
            {
                // The flags still have to win over the generic "open the running tray"
                // path. Otherwise --tray-only opens a browser and --start-no-open starts
                // nothing when the service is stopped.
                if (isTrayOnly) return 0;
                if (isStartNoOpen)
                {
                    if (SendSignal(Instance.StartSignalName)) return 0;
                    Inform(L.T("prog.adminAlreadyTitle"), L.T("prog.adminAlreadyBody"));
                    return 1;
                }

                if (portOpen)
                {
                    PortOwnerInfo owner = ServiceProbe.Inspect(config.Port, ExpectedPiWebScript(config));
                    if (owner.IsPiWeb && ServiceProbe.MatchesExpectedScript(owner, ExpectedPiWebScript(config)))
                        OpenBrowser(config.Port);
                    else Inform(L.T("notify.portConflictTitle"), L.T("notify.portConflictBody", config.Port));
                }
                else
                {
                    Inform(L.T("prog.adminAlreadyTitle"), L.T("prog.adminAlreadyBody"));
                }
                return 0;
            }

            bool existingElevated = TrayState.InstanceIsElevated();

            if (!elevated)
            {
                // A tray-only start must not start the service; a no-browser start
                // must not open a browser. Send the specific signal to the tray.
                if (isTrayOnly) return 0;
                if (isStartNoOpen)
                    return SendSignal(Instance.StartSignalName) ? 0 : 1;

                // Normal double-click: let the running tray start/open it
                SendSignal(Instance.OpenSignalName);
                return 0;
            }

            // An already-elevated tray keeps handling the flags itself. Do not replace
            // it just because a no-browser start was requested.
            if (existingElevated)
            {
                if (isTrayOnly) return 0;
                if (isStartNoOpen)
                    return SendSignal(Instance.StartSignalName) ? 0 : 1;

                // A plain elevated start with the service running just opens the browser.
                if (portOpen)
                {
                    PortOwnerInfo owner = ServiceProbe.Inspect(config.Port, ExpectedPiWebScript(config));
                    if (owner.IsPiWeb && ServiceProbe.MatchesExpectedScript(owner, ExpectedPiWebScript(config)))
                        OpenBrowser(config.Port);
                    else Inform(L.T("notify.portConflictTitle"), L.T("notify.portConflictBody", config.Port));
                    return 0;
                }
            }

            // Elevated takeover: this must also run for --tray-only and --start-no-open
            // when the running tray is not elevated. Otherwise those flags return early
            // and the old tray keeps the instance alive. A tray-only handoff must not
            // stop the service, so use the signal that quits without service cleanup.
            if (isTrayOnly)
                SendSignal(Instance.QuitKeepServiceSignalName);
            else
                SendSignal(Instance.QuitSignalName);
            Mutex taken;
            bool denied;
            if (TryAcquireMutex(15000, out taken, out denied))
            {
                // --tray-only explicitly says "do not start or manage the service".
                // Killing the listener here would contradict that flag, especially
                // after the old tray was asked to keep its service running.
                if (!isTrayOnly && Net.IsPortOpen(config.Port, 300))
                {
                    ServiceProbe.Invalidate();
                    PortOwnerInfo owner = ServiceProbe.Inspect(config.Port, ExpectedPiWebScript(config));
                    if (owner.IsPiWeb)
                    {
                        if (!ServiceProbe.MatchesExpectedScript(owner, ExpectedPiWebScript(config)))
                        {
                            // Another Pi Web installation owns the port. The old tray has
                            // already exited, so returning here would leave the user without
                            // a tray. Start the new tray in the normal conflict state instead;
                            // it will report the conflict and offer Settings.
                            Updater.AppendLog(config.UpdateLogFile,
                                "elevated takeover: port " + config.Port
                                + " is owned by another Pi Web installation; keeping it running");
                        }
                        else
                        {
                            if (owner.Pid > 4) Sys.KillTree(owner.Pid);
                            Net.WaitPortClosed(config.Port, 8000);
                        }
                    }
                }
                RunTray(config, elevated, !isTrayOnly, taken, !isTrayOnly && !isStartNoOpen);
                return 0;
            }

            Inform(L.T("prog.takeoverTitle"), L.T("prog.takeoverBody"));
            return 0;
        }

        internal static string Version
        {
            get
            {
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    object[] attributes = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                    if (attributes.Length > 0)
                    {
                        string text = ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                    Version version = assembly.GetName().Version;
                    if (version != null) return version.ToString(3);
                }
                catch (Exception)
                {
                }
                return "dev";
            }
        }

        private static bool Has(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Reject typos instead of silently starting the tray for an unknown switch
        private static string FindUnknownArgument(string[] args)
        {
            string[] known = new string[]
            {
                "--autostart", "--tray-only", "--start-no-open", "--stop", "--quit",
                "--check", "--install", "--dry-run", "--diag", "--make-ico",
                "--restart",
                "--lang-check", "--version", "--apply-update", "--help", "-h", "/?"
            };

            foreach (string arg in args)
            {
                if (string.IsNullOrEmpty(arg)) continue;
                bool match = false;
                foreach (string name in known)
                {
                    if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                    {
                        match = true;
                        break;
                    }
                }
                if (!match) return arg;
            }
            return null;
        }

        private static void RunTray(Config config, bool elevated, bool startServer, Mutex mutex,
            bool openBrowserOnStart)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (TrayApp app = new TrayApp(config, elevated, mutex, startServer, openBrowserOnStart))
            {
                Application.Run(app);
            }
        }

        private static bool TryAcquireMutex(int waitMs, out Mutex mutex, out bool accessDenied)
        {
            mutex = null;
            accessDenied = false;
            Mutex candidate = null;
            try
            {
                bool created;
                candidate = new Mutex(false, Instance.MutexName, out created);
                if (created)
                {
                    candidate.WaitOne(0);
                    mutex = candidate;
                    return true;
                }
                try
                {
                    if (candidate.WaitOne(waitMs))
                    {
                        mutex = candidate;
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    mutex = candidate;
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
                accessDenied = true;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (mutex == null && candidate != null)
                {
                    try { candidate.Close(); } catch (Exception) { }
                }
            }
            return false;
        }

        private static void ReleaseMutex(Mutex mutex)
        {
            if (mutex == null) return;
            try { mutex.ReleaseMutex(); } catch (Exception) { }
            try { mutex.Close(); } catch (Exception) { }
        }

        internal static bool SendSignal(string name)
        {
            try
            {
                bool created;
                using (EventWaitHandle signal = new EventWaitHandle(false, EventResetMode.AutoReset, name, out created))
                {
                    signal.Set();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The CLI paths do not have the tray's cached probe result; detect the installed
        // script before treating a listener as ours. An unknown path must never match.
        private static string ExpectedPiWebScript(Config config)
        {
            ProbeResult probe = null;
            try { probe = EnvironmentProbe.Detect(config); }
            catch (Exception) { }
            return ExpectedPiWebScript(config, probe);
        }

        private static string ExpectedPiWebScript(Config config, ProbeResult probe)
        {
            if (probe != null && probe.PiWebFound && !string.IsNullOrEmpty(probe.PiWebScript))
                return probe.PiWebScript;
            return "";
        }

        // No tray is running, so stop the service through the CLI. A listener that
        // cannot be identified as pi-web is never killed.
        private static bool StopServiceWithoutTray(Config config)
        {
            ServiceProbe.Invalidate();
            ProbeResult probe;
            try
            {
                probe = EnvironmentProbe.Detect(config);
            }
            catch (Exception ex)
            {
                ConsoleHost.TryAttach();
                ConsoleHost.WriteError(L.T("notify.selfCheckError", ex.Message));
                return false;
            }
            string script = ExpectedPiWebScript(config, probe);
            PortOwnerInfo owner = ServiceProbe.Inspect(config.Port, script);
            if (owner.IsUnknown)
            {
                ConsoleHost.TryAttach();
                ConsoleHost.WriteError(L.T("prog.portConflictStop", config.Port));
                return false;
            }
            if (owner.Owner == PortOwner.None) return true;

            // A different pi-web installation on the same port is not ours; never stop it.
            if (owner.IsPiWeb && !ServiceProbe.MatchesExpectedScript(owner, script))
            {
                ConsoleHost.TryAttach();
                ConsoleHost.WriteError(L.T("prog.portConflictStop", config.Port));
                return false;
            }

            if (!string.IsNullOrEmpty(script) && File.Exists(script))
            {
                string node = string.IsNullOrEmpty(probe.NodePath) ? "node.exe" : probe.NodePath;
                ProcessRunner.Run(node, Shell.Quote(script) + " stop --port " + config.Port,
                    config.WorkingDir, 20000, null, null);
            }

            if (Net.IsPortOpen(config.Port, 300))
            {
                ServiceProbe.Invalidate();
                owner = ServiceProbe.Inspect(config.Port, script);
                if (!owner.IsPiWeb || !ServiceProbe.MatchesExpectedScript(owner, script))
                {
                    ConsoleHost.TryAttach();
                    ConsoleHost.WriteError(L.T("prog.portConflictStop", config.Port));
                    return false;
                }
                if (owner.Pid > 4) Sys.KillTree(owner.Pid);
                Net.WaitPortClosed(config.Port, 5000);
            }

            // The exit code must tell the truth: a privileged listener can survive a
            // non-elevated taskkill, and a chained `--stop && ...` script must not be
            // told the service stopped when it did not.
            return !Net.IsPortOpen(config.Port, 300);
        }

        private static void OpenBrowser(int port)
        {
            try
            {
                Sys.OpenExternal("http://127.0.0.1:" + port);
            }
            catch (Exception)
            {
            }
        }

        private static void Inform(string title, string body)
        {
            MessageBox.Show(body, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------- CLI: check / install / diag ----------------

        private static int RunCheck(Config config, bool elevated)
        {
            bool console = ConsoleHost.TryAttach();
            if (!console)
            {
                // Without a console, show the report before probing. The previous
                // implementation ran the full (possibly tens of seconds) probe before
                // any window appeared, so a double-click looked like a no-op.
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    ProbeResult latest = CheckForm.ShowReport(config, new ProbeResult(), elevated, true);
                    return latest.Ready ? 0 : 1;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(L.T("notify.selfCheckError", ex.Message), L.T("sc.title"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
            }

            try
            {
                ProbeResult probe = EnvironmentProbe.Detect(config);
                PortOwnerInfo owner = ServiceProbe.Inspect(config.Port, ExpectedPiWebScript(config, probe));

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(AppName + " " + Version);
                sb.AppendLine(L.T("sc.config") + " : " + config.IniPath);
                sb.AppendLine(L.T("sc.dataDir") + " : " + config.OutputDir);
                sb.AppendLine(L.T("sc.port", config.Port, Net.IsPortOpen(config.Port, 300) ? L.T("bool.yes") : L.T("bool.no")));
                sb.AppendLine(L.T("sc.portOwner") + " : " + ServiceProbe.OwnerText(owner));
                sb.AppendLine(L.T("sc.workDir") + " : " + config.WorkingDir);
                sb.AppendLine(L.T("sc.admin") + " : " + (elevated ? L.T("bool.yes") : L.T("bool.no")));
                sb.AppendLine(L.T("sc.language") + " : " + L.Current);
                sb.AppendLine();
                sb.Append(probe.Summary());

                ConsoleHost.WriteLine(sb.ToString());
                return probe.Ready ? 0 : 1;
            }
            catch (Exception ex)
            {
                ConsoleHost.WriteError(L.T("notify.selfCheckError", ex.Message));
                return 1;
            }
        }

        private static int RunInstall(Config config, bool dryRun)
        {
            ConsoleHost.TryAttach();

            // npm cannot replace a globally installed package while its node process runs.
            // The tray's install path stops pi-web first (TrayApp.BeginInstall); the CLI
            // path must do the same, or the install fails with a locked package directory.
            bool hasWork = dryRun || Installer.HasInstallWork(config);
            bool stopped = false;
            try
            {
                if (hasWork && !StopServiceForConsoleInstall(config, dryRun, out stopped)) return 1;
            }
            catch (Exception ex)
            {
                ConsoleHost.WriteError(L.T("notify.selfCheckError", ex.Message));
                return 1;
            }

            int code;
            try
            {
                code = Installer.RunInConsole(config, dryRun);
            }
            catch (Exception ex)
            {
                // A refused install may have thrown after the service was stopped.
                // Do not skip the restore path just because the install itself failed.
                ConsoleHost.WriteError(L.T("install.crashBody", ex.Message));
                code = 1;
            }

            // Restore what was stopped: a tray path restarts the service it stopped for an
            // install; the CLI path asks a running tray to start it again.
            bool restored = true;
            if (stopped && !dryRun) restored = RestartServiceAfterConsoleInstall(config);
            return code == 0 && !restored ? 1 : code;
        }

        // Mirror TrayApp.ShouldStopServiceForInstall: only stop a listener that is really
        // ours (the expected script on its command line). Another user's pi-web is left alone.
        private static bool StopServiceForConsoleInstall(Config config, bool dryRun, out bool stopped)
        {
            stopped = false;
            if (!Net.IsPortOpen(config.Port, 300)) return true;

            ServiceProbe.Invalidate();
            ProbeResult probe;
            try
            {
                probe = EnvironmentProbe.Detect(config);
            }
            catch (Exception ex)
            {
                ConsoleHost.WriteError(L.T("notify.selfCheckError", ex.Message));
                return false;
            }
            string script = ExpectedPiWebScript(config, probe);
            PortOwnerInfo owner = ServiceProbe.Inspect(config.Port, script);
            if (!owner.IsPiWeb) return true;
            if (!ServiceProbe.MatchesExpectedScript(owner, script)) return true;

            if (dryRun) return true;

            ConsoleHost.WriteLine(L.T("install.stopFirst"));
            if (TrayRunning())
            {
                // Ask the running tray: it owns the process tree, and its explicit stop
                // path keeps the service down (auto-restart only covers unexpected stops).
                if (!SendSignal(Instance.StopSignalName))
                {
                    ConsoleHost.WriteError(L.T("notify.stopFailedBody", config.Port));
                    return false;
                }
                if (!Net.WaitPortClosed(config.Port, 30000))
                {
                    ConsoleHost.WriteError(L.T("notify.stopFailedBody", config.Port));
                    return false;
                }
                stopped = true;
                return true;
            }

            // StopServiceWithoutTray prints its own conflict error
            stopped = StopServiceWithoutTray(config);
            return stopped;
        }

        private static bool RestartServiceAfterConsoleInstall(Config config)
        {
            if (!TrayRunning())
            {
                ConsoleHost.WriteLine(L.T("install.stoppedNoTray"));
                return true;
            }

            // Start without opening a browser: --install must not turn into "open pi-web".
            if (!SendSignal(Instance.StartSignalName))
            {
                ConsoleHost.WriteError(L.T("notify.stopFailedBody", config.Port));
                return false;
            }
            if (!Net.WaitPortOpen(config.Port, 30000))
            {
                ConsoleHost.WriteError(L.T("install.restartFailed", config.Port));
                return false;
            }

            // An open port alone can be another program. Confirm that the listener is
            // the Pi Web script this installation manages and that it answers HTTP.
            // Next.js may need a few seconds after opening the port.
            string script = ExpectedPiWebScript(config);
            DateTime deadline = DateTime.Now.AddSeconds(30);
            while (DateTime.Now < deadline)
            {
                ServiceProbe.Invalidate();
                PortOwnerInfo owner = ServiceProbe.Inspect(config.Port, script);
                if (owner.IsPiWeb && ServiceProbe.MatchesExpectedScript(owner, script)
                    && Net.IsHttpOk("http://127.0.0.1:" + config.Port + "/", 1500))
                {
                    return true;
                }
                Thread.Sleep(500);
            }

            ConsoleHost.WriteError(L.T("install.restartFailed", config.Port));
            return false;
        }

        private static bool WaitForTrayExit(int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                Mutex mutex;
                bool denied;
                if (TryAcquireMutex(0, out mutex, out denied))
                {
                    ReleaseMutex(mutex);
                    return true;
                }
                Thread.Sleep(150);
            }
            return false;
        }

        // True when another tray instance owns the single-instance mutex. A denied
        // acquire also counts: an elevated tray is running.
        private static bool TrayRunning()
        {
            Mutex mutex;
            bool denied;
            if (TryAcquireMutex(0, out mutex, out denied))
            {
                ReleaseMutex(mutex);
                return false;
            }
            return true;
        }

        // --diag: write runtime state, real autostart state and the menu texts to diag.txt
        private static int RunDiag(Config config, bool elevated)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppName + " " + Version);
            sb.AppendLine(L.T("diag.time") + " : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(L.T("diag.exe") + " : " + Application.ExecutablePath);
            sb.AppendLine(L.T("sc.admin") + " : " + (elevated ? L.T("bool.yes") : L.T("bool.no")));
            PortOwnerInfo owner = ServiceProbe.Inspect(config.Port, ExpectedPiWebScript(config));
            sb.AppendLine(L.T("sc.port", config.Port, Net.IsPortOpen(config.Port, 300) ? L.T("bool.yes") : L.T("bool.no")));
            sb.AppendLine(L.T("sc.portOwner") + " : " + ServiceProbe.OwnerText(owner));
            sb.AppendLine(L.T("sc.workDir") + " : " + config.WorkingDir);
            sb.AppendLine(L.T("sc.config") + " : " + config.IniPath);
            sb.AppendLine(L.T("sc.language") + " : " + L.Current);
            sb.AppendLine(L.T("sc.serverLog") + " : " + config.LogFile);
            sb.AppendLine(L.T("sc.installLog") + " : " + config.InstallLogFile);
            sb.AppendLine(L.T("sc.updateLog") + " : " + config.UpdateLogFile);
            sb.AppendLine(L.T("diag.instance") + " : " + (Instance.Suffix.Length > 0 ? Instance.Suffix : L.T("diag.none")));

            ProbeResult probe;
            try
            {
                probe = EnvironmentProbe.Detect(config);
            }
            catch (Exception ex)
            {
                probe = null;
                sb.AppendLine(L.T("diag.probeFailed", ex.Message));
            }
            if (probe != null)
            {
                sb.AppendLine();
                sb.AppendLine(L.T("diag.runtimeSection"));
                sb.Append(probe.Summary());
            }

            bool task = Autostart.QueryTask();
            bool legacyTask = Autostart.QueryLegacyTask();
            bool run = Autostart.RunValueExists();
            sb.AppendLine();
            sb.AppendLine(L.T("diag.autostartSection"));
            sb.AppendLine(L.T("diag.task") + " : " + (task ? L.T("diag.exists") : L.T("diag.absent")));
            sb.AppendLine(L.T("diag.legacyTask") + " : " + (legacyTask ? L.T("diag.exists") : L.T("diag.absent")));
            sb.AppendLine(L.T("diag.runKey") + " : " + (run ? L.T("diag.exists") : L.T("diag.absent")));
            sb.AppendLine(L.T("diag.menuAutostart") + " : " + L.T("menu.autostart", Autostart.Describe(task || legacyTask, run)));
            sb.AppendLine(L.T("diag.menuAdmin") + " : " + L.T("menu.adminAutostart", config.AutostartAdmin ? L.T("bool.yes") : L.T("bool.no")));

            string state = L.T("diag.none");
            try
            {
                string statePath = TrayState.StatePath();
                if (File.Exists(statePath))
                    state = File.ReadAllText(statePath).Replace("\r", "").Replace("\n", "  ");
            }
            catch (Exception)
            {
                // The state file can disappear while a tray exits; the snapshot stays useful.
            }
            sb.AppendLine(L.T("diag.stateFile") + " : " + state);
            sb.AppendLine(L.T("diag.mutex") + " : " + DescribeMutex());

            try
            {
                Directory.CreateDirectory(config.OutputDir);
                File.WriteAllText(config.DiagFile, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }

            if (ConsoleHost.TryAttach())
            {
                ConsoleHost.WriteLine(sb.ToString());
            }
            else
            {
                MessageBox.Show(sb.ToString(), L.T("diag.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }

        private static string DescribeMutex()
        {
            Mutex mutex;
            bool denied;
            if (TryAcquireMutex(0, out mutex, out denied))
            {
                ReleaseMutex(mutex);
                return L.T("diag.mutexFree");
            }
            return denied ? L.T("diag.mutexAdmin") : L.T("diag.mutexOther");
        }

        private static void ShowHelp()
        {
            ConsoleHost.TryAttach();
            const string pad = "                                ";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppName + " " + Version);
            sb.AppendLine();
            sb.AppendLine(L.T("help.title"));
            sb.AppendLine("  " + Line("pi-web-tray.exe", L.T("help.launch"), pad));
            sb.AppendLine("  " + Line("--autostart", L.T("help.autostart"), pad));
            sb.AppendLine("  " + Line("--tray-only", L.T("help.trayOnly"), pad));
            sb.AppendLine("  " + Line("--start-no-open", L.T("help.startNoOpen"), pad));
            sb.AppendLine("  " + Line("--stop", L.T("help.stop"), pad));
            sb.AppendLine("  " + Line("--quit", L.T("help.quit"), pad));
            sb.AppendLine("  " + Line("--check", L.T("help.check"), pad));
            sb.AppendLine("  " + Line("--install", L.T("help.install"), pad));
            sb.AppendLine("  " + Line("--dry-run", L.T("help.dryrun"), pad));
            sb.AppendLine("  " + Line("--diag", L.T("help.diag"), pad));
            sb.AppendLine("  " + Line("--lang-check", L.T("help.langcheck"), pad));
            sb.AppendLine("  " + Line("--make-ico", L.T("help.makeico"), pad));
            sb.AppendLine("  " + Line("--version", L.T("help.version"), pad));
            sb.AppendLine();
            sb.AppendLine(L.T("help.envTitle"));
            sb.AppendLine("  PI_WEB_TRAY_INSTANCE=dev   " + L.T("help.envInstance"));
            sb.AppendLine("  PI_WEB_TRAY_PORT=30198     " + L.T("help.envPort"));
            sb.AppendLine("  PI_WEB_TRAY_HOME=D:\\x      " + L.T("help.envHome"));
            sb.AppendLine("  PI_WEB_TRAY_LANG=zh|en     " + L.T("help.envLang"));
            sb.AppendLine("  PI_WEB_TRAY_PORTABLE=1     " + L.T("help.envPortable"));
            sb.AppendLine("  PI_WEB_TRAY_NO_NODE=1      " + L.T("help.envNoNode"));
            sb.AppendLine("  PI_WEB_TRAY_NO_PIWEB=1     " + L.T("help.envNoPiweb"));
            sb.AppendLine("  PI_WEB_TRAY_NO_PICLI=1     " + L.T("help.envNoPicli"));

            string text = sb.ToString();
            if (ConsoleHost.TryAttach()) ConsoleHost.WriteLine(text);
            else MessageBox.Show(text, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // CJK characters take two columns, so the help output stays aligned
        private static string Line(string flag, string description, string pad)
        {
            int width = VisualWidth(flag);
            int spaces = pad.Length - width;
            if (spaces < 2) spaces = 2;
            return flag + new string(' ', spaces) + description;
        }

        private static int VisualWidth(string text)
        {
            int width = 0;
            foreach (char c in text) width += c > 0x2E80 ? 2 : 1;
            return width;
        }

        private static void LogCrash(Exception exception)
        {
            try
            {
                Directory.CreateDirectory(TrayState.Folder);
                string path = Path.Combine(TrayState.Folder, "crash.log");
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > 4 * 1024 * 1024)
                {
                    string backup = path + ".1";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(path, backup);
                }
                File.AppendAllText(
                    path,
                    "==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====" + Environment.NewLine
                        + Shell.RedactSecrets(exception == null ? "(unknown)" : exception.ToString())
                        + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
        }

        private static void ExportIconFile(string baseDir)
        {
            try
            {
                File.WriteAllBytes(
                    Path.Combine(baseDir, "pi-web-tray.ico"),
                    Icons.BuildIco(Icons.RunningColor, new int[] { 16, 20, 24, 32, 40, 48, 64, 256 }));
            }
            catch (Exception)
            {
            }
        }
    }
}
