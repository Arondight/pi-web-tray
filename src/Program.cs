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
            // This check must run before Config.Load/resource lookup. A user-writable
            // installation can be replaced by a same-user process, including its
            // satellite resource DLLs. Refuse an elevated start instead of loading
            // untrusted code into the administrator token.
            if (Elevation.IsElevated()
                && Autostart.IsUserWritableInstall(Application.ExecutablePath))
            {
                const string unsafeElevated =
                    "Pi Web Tray refuses to run as administrator from a directory that "
                    + "standard users can modify. Move the program to a protected location "
                    + "such as Program Files and try again.";
                if (ConsoleHost.TryAttach()) ConsoleHost.WriteError(unsafeElevated);
                else MessageBox.Show(unsafeElevated, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

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

            // The update helper is the already-installed pi-web-tray.exe, started after
            // UAC when needed. It must be handled before argument validation because its
            // paths are positional.
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
                        if (i + 1 >= args.Length) return 2;
                        if (!LogHost.IsExpectedLogPath(args[i + 1])) return 2;
                        // A syntactically valid path can still be a directory, be
                        // read-only, or be locked by another process. The check must
                        // prove the log host can actually open its output, otherwise
                        // the service would run with silently discarded output.
                        if (!LogHost.CanOpenForAppend(args[i + 1])) return 2;
                        break;
                    }
                }
                return 0;
            }
            if (Has(args, "--log-host"))
            {
                return LogHost.Run(args);
            }

            // Read configuration for language/validation without creating files.
            // Unknown arguments and an invalid --dry-run must not leave a default ini
            // behind; only a valid interactive/CLI command materialises defaults.
            config = Config.Load(false);

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

            config = Config.Load(true);

            if (Has(args, "--make-ico"))
            {
                return ExportIconFile(config.BaseDir) ? 0 : 1;
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
            // Autostart must never open a browser, including when another tray
            // already owns the mutex and this process only forwards a signal.
            bool noBrowser = SuppressBrowserOnStart(isAutostart, isStartNoOpen);

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
                if (restartDenied)
                {
                    MessageBox.Show(L.T("prog.adminAlreadyBody"),
                        L.T("prog.adminAlreadyTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(
                        L.T("notify.restartFailedBody"),
                        L.T("notify.restartFailed"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
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
                    return 0;
                }
                // A tray can already be running (for example a user-started
                // --tray-only instance). Login autostart must still start the
                // service without opening a browser.
                if (isTrayOnly) return 0;
                return SendSignal(Instance.StartSignalName) ? 0 : 1;
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
                RunTray(config, elevated, !isTrayOnly, mutex, !isTrayOnly && !noBrowser);
                return 0;
            }

            // The running tray may have been started with PI_WEB_TRAY_PORT in a
            // different shell. Prefer the port it recorded in state.ini so CLI
            // install/stop/check commands target the service that actually exists.
            int servicePort = EffectivePort(config);

            if (isStop)
            {
                // The signal only asks the tray to stop the service. A privileged tray can
                // fail to kill it from this shell; verify the port actually closed so the
                // exit code reflects reality instead of the signal delivery.
                if (!SendSignal(Instance.StopSignalName)) return 1;
                if (!Net.WaitPortClosed(servicePort, 30000)) return 1;
                // A closed port does not mean the tray's `pi-web stop` child has
                // released the package directory. Wait so `--stop && --install` does
                // not start npm too early.
                return WaitForStopWorker(25000) ? 0 : 1;
            }

            if (isQuit)
            {
                if (!SendSignal(Instance.QuitSignalName)) return 1;
                // A closed port only proves that the service stopped, not that the
                // tray itself exited. Wait for the single-instance mutex so a
                // following command sees a genuinely free instance.
                // ExitApp may spend up to 8 seconds stopping the listener and
                // another 25 seconds waiting for a `pi-web stop` worker.
                if (!WaitForTrayExit(45000)) return 1;
                // The tray may have exited while a privileged or foreign listener
                // survived the stop. Do not report success in that case.
                return Net.WaitPortClosed(servicePort, 5000) ? 0 : 1;
            }

            bool portOpen = Net.IsPortOpen(servicePort, 300);

            // Cannot open the other instance's mutex: it runs at a higher integrity level (admin)
            if (accessDenied)
            {
                // The flags still have to win over the generic "open the running tray"
                // path. Otherwise --tray-only opens a browser and --start-no-open starts
                // nothing when the service is stopped.
                if (isTrayOnly) return 0;
                if (noBrowser)
                {
                    if (SendSignal(Instance.StartSignalName)) return 0;
                    Inform(L.T("prog.adminAlreadyTitle"), L.T("prog.adminAlreadyBody"));
                    return 1;
                }

                // Let the elevated tray start/open the service and show its own
                // conflict/update state. Directly opening the URL only works while
                // the service is already running; when it is stopped the old code
                // told the user "already running as admin" and did nothing.
                if (SendSignal(Instance.OpenSignalName)) return 0;
                if (portOpen)
                {
                    PortOwnerInfo owner = ServiceProbe.Inspect(servicePort, ExpectedPiWebScript(config));
                    if (owner.IsPiWeb && ServiceProbe.MatchesExpectedScript(owner, ExpectedPiWebScript(config)))
                        OpenBrowser(servicePort);
                    else Inform(L.T("notify.portConflictTitle"), L.T("notify.portConflictBody", servicePort));
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
                if (noBrowser)
                    return SendSignal(Instance.StartSignalName) ? 0 : 1;

                // Normal double-click: let the running tray start/open it.
                // Do not report success if the named event could not be opened:
                // an elevated or differently-scoped tray may be unreachable.
                if (!SendSignal(Instance.OpenSignalName))
                {
                    Inform(L.T("prog.adminAlreadyTitle"), L.T("prog.adminAlreadyBody"));
                    return 1;
                }
                return 0;
            }

            // An already-elevated tray keeps handling the flags itself. Do not replace
            // it just because the service is currently stopped: a plain launch should
            // ask the existing tray to start/open, not tear it down. This also avoids
            // aborting an install/update that happens to have closed the port.
            if (existingElevated)
            {
                if (isTrayOnly) return 0;
                if (noBrowser)
                    return SendSignal(Instance.StartSignalName) ? 0 : 1;
                if (SendSignal(Instance.OpenSignalName)) return 0;
                // Fall back to opening the verified listener directly when the
                // notification event is unavailable; keep the old behavior for a
                // running service, but do not pretend to start a stopped one.
                if (portOpen)
                {
                    PortOwnerInfo owner = ServiceProbe.Inspect(servicePort, ExpectedPiWebScript(config));
                    if (owner.IsPiWeb && ServiceProbe.MatchesExpectedScript(owner, ExpectedPiWebScript(config)))
                        OpenBrowser(servicePort);
                    else Inform(L.T("notify.portConflictTitle"), L.T("notify.portConflictBody", servicePort));
                    return 0;
                }
                Inform(L.T("prog.adminAlreadyTitle"), L.T("prog.adminAlreadyBody"));
                return 1;
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
            // Leave enough time for the old tray to finish a `pi-web stop` worker
            // before starting the replacement (ExitWithout waits up to 25 seconds).
            if (TryAcquireMutex(40000, out taken, out denied))
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
                RunTray(config, elevated, !isTrayOnly, taken, !isTrayOnly && !noBrowser);
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

        internal static bool SuppressBrowserOnStart(bool autostart, bool startNoOpen)
        {
            return autostart || startNoOpen;
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
                candidate = new Mutex(false, Instance.MutexName, out _);
                // Even the process that created the named mutex must actually acquire
                // it. Another instance can open and acquire it between CreateMutex and
                // WaitOne; ignoring the WaitOne result would let both processes believe
                // they own the single instance.
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

            if (!string.IsNullOrEmpty(script) && File.Exists(script)
                && !string.IsNullOrEmpty(probe.NodePath) && File.Exists(probe.NodePath))
            {
                // A bare "node.exe" fallback would let CreateProcess resolve a binary
                // from the current directory or PATH. If Node.js is gone, use the
                // identified listener PID below instead of attempting the CLI stop.
                ProcessRunner.Run(probe.NodePath, Shell.Quote(script) + " stop --port " + config.Port,
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
                    // CheckForm reads config.Port; use the running tray's port when
                    // this process was started without PI_WEB_TRAY_PORT.
                    config.Port = EffectivePort(config);
                    ProbeResult latest = CheckForm.ShowReport(config, new ProbeResult(), elevated, true);
                    if (latest == null)
                    {
                        // The user closed the report before its background probe finished.
                        // Do not report the placeholder result as "not ready"; run one
                        // synchronous probe so the CLI exit code still reflects reality.
                        latest = EnvironmentProbe.Detect(config);
                    }
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
                int port = EffectivePort(config);
                ProbeResult probe = EnvironmentProbe.Detect(config);
                PortOwnerInfo owner = ServiceProbe.Inspect(port, ExpectedPiWebScript(config, probe));

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(AppName + " " + Version);
                sb.AppendLine(L.T("sc.config") + " : " + config.IniPath);
                sb.AppendLine(L.T("sc.dataDir") + " : " + config.OutputDir);
                sb.AppendLine(L.T("sc.port", port, Net.IsPortOpen(port, 300) ? L.T("bool.yes") : L.T("bool.no")));
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

            // Do not run npm with an elevated token from a user-writable installation.
            // The dry run is read-only and remains usable for diagnostics.
            if (!dryRun && Elevation.IsElevated()
                && Autostart.IsUserWritableInstall(Application.ExecutablePath))
            {
                ConsoleHost.WriteError(L.T("update.error.writableInstall"));
                return 1;
            }

            // npm cannot replace a globally installed package while its node process runs.
            // The tray's install path stops pi-web first (TrayApp.BeginInstall); the CLI
            // path must do the same, or the install fails with a locked package directory.
            bool hasWork = dryRun || Installer.HasInstallWork(config);
            bool stopped = false;
            // The CLI and an elevated update helper can otherwise touch the same
            // installation at the same time: an update could replace the executable
            // while npm is rewriting the global package tree. An update helper holds
            // this lock for the whole apply phase.
            OperationLock updateLock = null;
            try
            {
                if (!dryRun)
                {
                    updateLock = OperationLock.TryAcquire(
                        OperationLock.UpdateName(config.BaseDir));
                    if (updateLock == null)
                    {
                        ConsoleHost.WriteError(L.T("update.error.busy"));
                        return 1;
                    }
                }

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
                // install; the CLI path asks a running tray to start it again. The tray's
                // StartServer deliberately refuses to run while UpdateName is held, so the
                // CLI must release it before sending the start signal. Otherwise the tray
                // consumes the signal, the restart times out, and a successfully installed
                // service is left stopped.
                bool restored = true;
                if (stopped && !dryRun)
                {
                    if (updateLock != null)
                    {
                        updateLock.Dispose();
                        updateLock = null;
                    }
                    restored = RestartServiceAfterConsoleInstall(config);
                }
                return code == 0 && !restored ? 1 : code;
            }
            finally
            {
                if (updateLock != null) updateLock.Dispose();
            }
        }

        // Mirror TrayApp.ShouldStopServiceForInstall: only stop a listener that is really
        // ours (the expected script on its command line). Another user's pi-web is left alone.
        private static bool StopServiceForConsoleInstall(Config config, bool dryRun, out bool stopped)
        {
            stopped = false;
            int port = EffectivePort(config);
            bool trayRunning = TrayRunning();
            // Capture this before sending the stop signal: a service that was open
            // and is now closed was stopped for the install and must be restored.
            bool serviceWasRunning = Net.IsPortOpen(port, 300);

            // Always tell a running tray to stop, even when the port is currently
            // closed. The tray may be Starting, or may have an AutoRestart timer
            // armed; either could otherwise bring the service back while npm runs.
            // RunInstall already holds UpdateName, which StartServer now honours.
            if (!dryRun && trayRunning)
            {
                if (!SendSignal(Instance.StopSignalName))
                {
                    ConsoleHost.WriteError(L.T("notify.stopFailedBody", port));
                    return false;
                }
            }

            if (!Net.IsPortOpen(port, 300))
            {
                // A tray may have closed the port for a prior stop while its
                // `pi-web stop` child is still releasing the package directory.
                if (!dryRun && trayRunning && !WaitForStopWorker(25000))
                {
                    ConsoleHost.WriteError(L.T("notify.stopFailedBody", port));
                    return false;
                }
                // If the port was open before the stop signal, this install stopped
                // a live service even though the port is now closed. Restore it after
                // npm finishes; a service that was already stopped stays stopped.
                if (!dryRun && trayRunning && serviceWasRunning) stopped = true;
                return true;
            }

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
            PortOwnerInfo owner = ServiceProbe.Inspect(port, script);
            if (!owner.IsPiWeb) return true;
            if (!ServiceProbe.MatchesExpectedScript(owner, script)) return true;

            if (dryRun) return true;

            ConsoleHost.WriteLine(L.T("install.stopFirst"));
            if (trayRunning)
            {
                // The stop signal was sent above. The tray owns the process tree and
                // keeps the service down (auto-restart only covers unexpected stops).
                if (!Net.WaitPortClosed(port, 30000))
                {
                    ConsoleHost.WriteError(L.T("notify.stopFailedBody", port));
                    return false;
                }
                // The port can close while the tray's `pi-web stop` child is still
                // finishing. Starting npm now can hit a locked package directory; wait
                // for the same lock the tray holds until that child exits.
                if (!WaitForStopWorker(25000))
                {
                    ConsoleHost.WriteError(L.T("notify.stopFailedBody", port));
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
            int port = EffectivePort(config);
            if (!TrayRunning())
            {
                ConsoleHost.WriteLine(L.T("install.stoppedNoTray"));
                return true;
            }

            // The stop worker releases StopName just before the tray timer observes the
            // Stopped phase. A short settle window avoids sending the one-shot start
            // signal while StartServer still sees Stopping; if it is still stopping,
            // StartServer now queues the request instead of discarding it.
            Thread.Sleep(750);
            if (!TrayRunning())
            {
                ConsoleHost.WriteLine(L.T("install.stoppedNoTray"));
                return true;
            }

            // Start without opening a browser: --install must not turn into "open pi-web".
            if (!SendSignal(Instance.StartSignalName))
            {
                ConsoleHost.WriteError(L.T("notify.stopFailedBody", port));
                return false;
            }
            // Match the tray's own start timeout: a cold Next.js start can take longer
            // than the old 30 second CLI window.
            if (!Net.WaitPortOpen(port, 180000))
            {
                ConsoleHost.WriteError(L.T("install.restartFailed", port));
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
                PortOwnerInfo owner = ServiceProbe.Inspect(port, script);
                if (owner.IsPiWeb && ServiceProbe.MatchesExpectedScript(owner, script)
                    && Net.IsHttpOk("http://127.0.0.1:" + port + "/", 1500))
                {
                    return true;
                }
                Thread.Sleep(500);
            }

            ConsoleHost.WriteError(L.T("install.restartFailed", port));
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

        // The tray holds OperationLock.StopName while its `pi-web stop` child is
        // running. Acquiring the lock after the port closes means that child has
        // finished; if the lock is free already, there was no stop worker to wait for.
        private static bool WaitForStopWorker(int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (OperationLock.IsAvailable(OperationLock.StopName)) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        // A live tray in this session records its actual port (including a
        // PI_WEB_TRAY_PORT override) in state.ini. CLI commands started from a
        // different shell must use that value, not their own config.
        private static int EffectivePort(Config config)
        {
            int port;
            if (TrayRunning() && TrayState.TryGetRunningPort(out port)) return port;
            return config.Port;
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
            int port = EffectivePort(config);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppName + " " + Version);
            sb.AppendLine(L.T("diag.time") + " : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(L.T("diag.exe") + " : " + Application.ExecutablePath);
            sb.AppendLine(L.T("sc.admin") + " : " + (elevated ? L.T("bool.yes") : L.T("bool.no")));
            PortOwnerInfo owner = ServiceProbe.Inspect(port, ExpectedPiWebScript(config));
            sb.AppendLine(L.T("sc.port", port, Net.IsPortOpen(port, 300) ? L.T("bool.yes") : L.T("bool.no")));
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

            bool taskEnabled;
            bool taskMatches;
            bool legacyTaskEnabled;
            bool legacyTaskMatches;
            bool task = Autostart.QueryTaskState(out taskEnabled, out taskMatches);
            bool legacyTask = Autostart.QueryLegacyTaskState(
                out legacyTaskEnabled, out legacyTaskMatches);
            bool run = Autostart.RunValueExists();
            bool taskStartsAtLogin = (task && taskEnabled && taskMatches)
                || (legacyTask && legacyTaskEnabled && legacyTaskMatches);
            sb.AppendLine();
            sb.AppendLine(L.T("diag.autostartSection"));
            sb.AppendLine(L.T("diag.task") + " : " + (task ? L.T("diag.exists") : L.T("diag.absent")));
            sb.AppendLine(L.T("diag.legacyTask") + " : " + (legacyTask ? L.T("diag.exists") : L.T("diag.absent")));
            sb.AppendLine(L.T("diag.runKey") + " : " + (run ? L.T("diag.exists") : L.T("diag.absent")));
            sb.AppendLine(L.T("diag.menuAutostart") + " : " + L.T("menu.autostart", Autostart.Describe(taskStartsAtLogin, run)));
            sb.AppendLine(L.T("diag.menuAdmin") + " : " + L.T("menu.adminAutostart", config.AutostartAdmin ? L.T("bool.yes") : L.T("bool.no")));

            string state = L.T("diag.none");
            try
            {
                string statePath = TrayState.StatePath();
                if (File.Exists(statePath) && !PathSafety.ContainsReparsePoint(statePath))
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
                if (!PathSafety.ContainsReparsePoint(config.DiagFile))
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
                if (PathSafety.ContainsReparsePoint(path)) return;
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

        private static bool ExportIconFile(string baseDir)
        {
            try
            {
                File.WriteAllBytes(
                    Path.Combine(baseDir, "pi-web-tray.ico"),
                    Icons.BuildIco(Icons.RunningColor, new int[] { 16, 20, 24, 32, 40, 48, 64, 256 }));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
