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
                return SendSignal(Instance.StopSignalName) ? 0 : 1;
            }

            if (isQuit)
            {
                return SendSignal(Instance.QuitSignalName) ? 0 : 1;
            }

            bool portOpen = Net.IsPortOpen(config.Port, 300);

            // Cannot open the other instance's mutex: it runs at a higher integrity level (admin)
            if (accessDenied)
            {
                if (portOpen)
                {
                    PortOwnerInfo owner = ServiceProbe.Inspect(config.Port);
                    if (owner.IsPiWeb) OpenBrowser(config.Port);
                    else Inform(L.T("notify.portConflictTitle"), L.T("notify.portConflictBody", config.Port));
                }
                else
                {
                    Inform(L.T("prog.adminAlreadyTitle"), L.T("prog.adminAlreadyBody"));
                }
                return 0;
            }

            if (!elevated)
            {
                // Normal double-click: let the running tray start/open it
                SendSignal(Instance.OpenSignalName);
                return 0;
            }

            // Elevated start: if the other instance is already elevated and the service runs, just open the browser
            if (portOpen && TrayState.InstanceIsElevated())
            {
                PortOwnerInfo owner = ServiceProbe.Inspect(config.Port);
                if (owner.IsPiWeb) OpenBrowser(config.Port);
                else Inform(L.T("notify.portConflictTitle"), L.T("notify.portConflictBody", config.Port));
                return 0;
            }

            // Elevated takeover: tell the old instance to stop and exit, then take over as administrator
            SendSignal(Instance.QuitSignalName);
            Mutex taken;
            bool denied;
            if (TryAcquireMutex(15000, out taken, out denied))
            {
                if (Net.IsPortOpen(config.Port, 300))
                {
                    ServiceProbe.Invalidate();
                    PortOwnerInfo owner = ServiceProbe.Inspect(config.Port);
                    if (owner.IsPiWeb)
                    {
                        Sys.KillByPort(config.Port);
                        Net.WaitPortClosed(config.Port, 8000);
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

        // No tray is running, so stop the service through the CLI. A listener that
        // cannot be identified as pi-web is never killed.
        private static bool StopServiceWithoutTray(Config config)
        {
            ServiceProbe.Invalidate();
            PortOwnerInfo owner = ServiceProbe.Inspect(config.Port);
            if (owner.IsUnknown)
            {
                ConsoleHost.TryAttach();
                ConsoleHost.WriteError(L.T("prog.portConflictStop", config.Port));
                return false;
            }
            if (owner.Owner == PortOwner.None) return true;

            ProbeResult probe = EnvironmentProbe.Detect(config);
            string script = config.ServerScript;
            if (string.IsNullOrEmpty(script) && probe.PiWebFound) script = probe.PiWebScript;

            if (!string.IsNullOrEmpty(script) && File.Exists(script))
            {
                string node = string.IsNullOrEmpty(probe.NodePath) ? "node.exe" : probe.NodePath;
                ProcessRunner.Run(node, Shell.Quote(script) + " stop --port " + config.Port,
                    config.WorkingDir, 20000, null, null);
            }

            if (Net.IsPortOpen(config.Port, 300))
            {
                ServiceProbe.Invalidate();
                owner = ServiceProbe.Inspect(config.Port);
                if (!owner.IsPiWeb)
                {
                    ConsoleHost.TryAttach();
                    ConsoleHost.WriteError(L.T("prog.portConflictStop", config.Port));
                    return false;
                }
                Sys.KillByPort(config.Port);
                Net.WaitPortClosed(config.Port, 5000);
            }
            return true;
        }

        private static void OpenBrowser(int port)
        {
            try
            {
                using (Process browser = Process.Start("http://127.0.0.1:" + port))
                {
                    // The shell launches the browser; only our Process handle is released.
                }
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
            ProbeResult probe = EnvironmentProbe.Detect(config);
            PortOwnerInfo owner = ServiceProbe.Inspect(config.Port);

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

            if (console)
            {
                ConsoleHost.WriteLine(sb.ToString());
            }
            else
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                CheckForm.ShowReport(config, probe, elevated);
            }

            return probe.Ready ? 0 : 1;
        }

        private static int RunInstall(Config config, bool dryRun)
        {
            ConsoleHost.TryAttach();
            return Installer.RunInConsole(config, dryRun);
        }

        // --diag: write runtime state, real autostart state and the menu texts to diag.txt
        private static int RunDiag(Config config, bool elevated)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppName + " " + Version);
            sb.AppendLine(L.T("diag.time") + " : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(L.T("diag.exe") + " : " + Application.ExecutablePath);
            sb.AppendLine(L.T("sc.admin") + " : " + (elevated ? L.T("bool.yes") : L.T("bool.no")));
            PortOwnerInfo owner = ServiceProbe.Inspect(config.Port);
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
            bool run = Autostart.RunValueExists();
            sb.AppendLine();
            sb.AppendLine(L.T("diag.autostartSection"));
            sb.AppendLine(L.T("diag.task") + " : " + (task ? L.T("diag.exists") : L.T("diag.absent")));
            sb.AppendLine(L.T("diag.runKey") + " : " + (run ? L.T("diag.exists") : L.T("diag.absent")));
            sb.AppendLine(L.T("diag.menuAutostart") + " : " + L.T("menu.autostart", Autostart.Describe(task, run)));
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
            sb.AppendLine("  PI_WEB_TRAY_NO_NODE=1      " + L.T("help.envTest"));

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
                        + (exception == null ? "(unknown)" : exception.ToString()) + Environment.NewLine + Environment.NewLine,
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
