// Pi Web Tray — the tray itself
//
// One state machine drives everything:
//
//   Checking --> NeedsNode   (only the user can help here: install Node.js)
//            --> NeedsDeps --> Installing ----+
//            --> Stopped   --> Starting      +--> Running --> Stopping --> Stopped
//                                            \--> Failed
//
// Every slow operation (env probe, npm install, schtasks, stopping the service) runs on a background
// thread; the UI thread polls for the result and updates the controls (WinForms controls are thread-affine).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PiWebTray
{
    internal enum Phase
    {
        Checking,
        NeedsNode,
        NeedsDeps,
        Installing,
        Starting,
        Running,
        Stopping,
        Stopped,
        Failed
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private const int StartTimeoutSeconds = 180;
        private const long MaxLogBytes = 4 * 1024 * 1024;

        private readonly Config _config;
        private readonly bool _elevated;
        private readonly bool _autoStartService;
        private readonly Mutex _mutex;
        private readonly int _port;

        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly EventWaitHandle _openSignal;
        private readonly EventWaitHandle _stopSignal;
        private readonly EventWaitHandle _quitSignal;

        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _openItem;
        private readonly ToolStripMenuItem _startItem;
        private readonly ToolStripMenuItem _stopItem;
        private readonly ToolStripMenuItem _restartItem;
        private readonly ToolStripMenuItem _installItem;
        private readonly ToolStripMenuItem _nodeItem;
        private readonly ToolStripMenuItem _checkItem;
        private readonly ToolStripMenuItem _installLogItem;
        private readonly ToolStripMenuItem _autostartItem;
        private readonly ToolStripMenuItem _adminAutostartItem;

        private Process _server;

        private Phase _phase = Phase.Checking;
        private ProbeResult _probe;
        private Color _iconColor = Color.Empty;
        private Icon _currentIcon;
        private string _lastNotified = "";

        // environment probe (background -> UI thread)
        private readonly object _checkLock = new object();
        private bool _checkRunning;
        private bool _checkDone;
        private ProbeResult _checkResult;
        private bool _checkThenStart;
        private bool _checkPending;
        private bool _checkPendingThenStart;
        private bool _installAfterCheck;
        private bool _installAfterCheckForce;

        // install (background -> UI thread)
        private readonly object _installLock = new object();
        private bool _installDone;
        private InstallResult _installResult;
        private DateTime _installStartedAt = DateTime.MinValue;
        private int _installTicks;

        // service control
        private bool _openWhenReady;
        private bool _pendingRestart;
        private bool _exiting;
        private bool _cleaned;
        private DateTime _startDeadline;
        private DateTime _stopDeadline;
        private DateTime _lastSignalHandled = DateTime.MinValue;
        private DateTime _stopEscalatedAt = DateTime.MinValue;

        // self check result: computed on a thread, shown from the UI thread
        private volatile ProbeResult _selfCheckResult;
        private volatile string _selfCheckError;

        // autostart operation (background -> UI thread)
        private readonly object _autoLock = new object();
        private bool _autoBusy;
        private bool _autoDone;
        private string _autoMessage;
        private bool _autoError;
        private int _revertAdminAutostart = -1; // -1 = no revert, 0/1 = previous value

        private readonly object _taskCacheLock = new object();
        private bool _taskCache;
        private DateTime _taskCacheAt = DateTime.MinValue;
        private bool _taskRefreshRunning;
        private bool _taskRefreshDone;

        // readiness: "port accepts connections" is not the same as "the app answers HTTP"
        private readonly object _readyLock = new object();
        private int _readyResult;
        private bool _readyCheckRunning;

        internal TrayApp(Config config, bool elevated, Mutex mutex, bool startServer)
        {
            _config = config;
            _elevated = elevated;
            _autoStartService = startServer;
            _mutex = mutex;
            _port = config.Port;

            TrayState.Save(Process.GetCurrentProcess().Id, elevated, _port);

            bool created;
            _openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.OpenSignalName, out created);
            _stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.StopSignalName, out created);
            _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.QuitSignalName, out created);

            _statusItem = new ToolStripMenuItem(L.T("status.checkingShort"));
            _statusItem.Enabled = false;

            _openItem = new ToolStripMenuItem(L.T("menu.open"));
            _openItem.Font = new Font(_menu.Font, FontStyle.Bold);
            _openItem.Click += delegate { OpenOrStart(); };

            _startItem = new ToolStripMenuItem(L.T("menu.start"));
            _startItem.Click += delegate { StartServer(true); };

            _stopItem = new ToolStripMenuItem(L.T("menu.stop"));
            _stopItem.Click += delegate { StopServer(); };

            _restartItem = new ToolStripMenuItem(L.T("menu.restart"));
            _restartItem.Click += delegate { RestartServer(); };

            _installItem = new ToolStripMenuItem(L.T("menu.installDeps"));
            _installItem.Click += delegate { BeginInstall(true); };

            _nodeItem = new ToolStripMenuItem(L.T("menu.node"));
            _nodeItem.Click += delegate { OpenUrl("https://nodejs.org/en/download"); };

            _checkItem = new ToolStripMenuItem(L.T("menu.selfCheck"));
            _checkItem.Click += delegate { RunSelfCheck(); };

            ToolStripMenuItem logItem = new ToolStripMenuItem(L.T("menu.serverLog"));
            logItem.Click += delegate { OpenLog(_config.LogFile, "server.log"); };

            _installLogItem = new ToolStripMenuItem(L.T("menu.installLog"));
            _installLogItem.Click += delegate { OpenLog(_config.InstallLogFile, "install.log"); };

            ToolStripMenuItem folderItem = new ToolStripMenuItem(L.T("menu.folder"));
            folderItem.Click += delegate
            {
                try
                {
                    using (Process explorer = Process.Start(_config.BaseDir))
                    {
                    }
                }
                catch (Exception)
                {
                }
            };

            ToolStripMenuItem shortcutItem = new ToolStripMenuItem(L.T("menu.shortcut"));
            shortcutItem.Click += delegate { CreateDesktopShortcut(); };

            _autostartItem = new ToolStripMenuItem(L.T("menu.autostart", L.T("auto.off")));
            _autostartItem.Click += delegate { ToggleAutostart(); };

            _adminAutostartItem = new ToolStripMenuItem(L.T("menu.adminAutostart", L.T("bool.no")));
            _adminAutostartItem.Click += delegate { ToggleAdminAutostart(); };

            ToolStripMenuItem exitItem = new ToolStripMenuItem(L.T("menu.exit"));
            exitItem.Click += delegate { ExitApp(); };

            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_openItem);
            _menu.Items.Add(_startItem);
            _menu.Items.Add(_stopItem);
            _menu.Items.Add(_restartItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_installItem);
            _menu.Items.Add(_nodeItem);
            _menu.Items.Add(_checkItem);
            _menu.Items.Add(logItem);
            _menu.Items.Add(_installLogItem);
            _menu.Items.Add(folderItem);
            _menu.Items.Add(shortcutItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_autostartItem);
            _menu.Items.Add(_adminAutostartItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            // The check margin must stay: turning off only ShowImageMargin clips the check box, which makes
            // Checked look like a no-op. The state is therefore written into the text as well.
            _menu.ShowImageMargin = false;
            _menu.ShowCheckMargin = true;

            // Re-read the real system state whenever the menu opens (cached, so the menu stays responsive)
            _menu.Opening += delegate { UpdateAutostartItems(); };

            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.Text = "Pi Web";
            _tray.MouseClick += delegate (object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) OpenOrStart();
            };

            RefreshAutostartItems();

            _timer.Interval = 500;
            _timer.Tick += OnTick;
            _timer.Start();

            BeginEnvironmentCheck(_autoStartService);
        }

        // ---------------- poll loop ----------------

        private void OnTick(object sender, EventArgs e)
        {
            if (_exiting) return;

            bool openSignal = false;
            bool stopSignal = false;
            bool quitSignal = false;
            try { openSignal = _openSignal.WaitOne(0); } catch (Exception) { }
            try { stopSignal = _stopSignal.WaitOne(0); } catch (Exception) { }
            try { quitSignal = _quitSignal.WaitOne(0); } catch (Exception) { }

            if (quitSignal)
            {
                ExitApp();
                return;
            }
            if (stopSignal) StopServer();

            PollAutostartOp();
            PollEnvironmentCheck();
            PollInstall();
            PollSelfCheck();
            PollTaskRefresh();

            if (openSignal && DateTime.Now - _lastSignalHandled > TimeSpan.FromSeconds(3))
            {
                _lastSignalHandled = DateTime.Now;
                OpenOrStart();
            }

            switch (_phase)
            {
                case Phase.Starting:
                    PollStarting();
                    break;
                case Phase.Stopping:
                    PollStopping();
                    break;
                case Phase.Installing:
                    _installTicks++;
                    if (_installTicks % 4 == 0) UpdateUi();
                    break;
                default:
                    PollPortSync();
                    break;
            }
        }

        private void PollStarting()
        {
            if (Net.IsPortOpen(_port, 200))
            {
                // The port answers, but the web app may not be ready yet: confirm with a real HTTP request
                int http;
                lock (_readyLock) http = _readyResult;

                if (http == 0)
                {
                    BeginReadyCheck();
                    return;
                }
                if (http < 0)
                {
                    // Not ready yet (or a transient failure): reset and try again until the deadline
                    lock (_readyLock) _readyResult = 0;
                    return;
                }

                _phase = Phase.Running;
                bool open = _openWhenReady;
                _openWhenReady = false;
                UpdateUi();
                if (open) OpenBrowser();
                Notify(L.T("notify.readyTitle"), "http://127.0.0.1:" + _port);
                return;
            }

            bool dead = false;
            try { dead = _server != null && _server.HasExited; } catch (Exception) { }

            if (dead || DateTime.Now > _startDeadline)
            {
                string reason = dead ? L.T("notify.startFailedExited") : L.T("notify.startFailedTimeout", _port);
                _phase = Phase.Failed;
                _openWhenReady = false;
                _pendingRestart = false;
                UpdateUi();
                NotifyError(L.T("notify.startFailedTitle"), L.T("notify.startFailedBody", reason));
            }
        }

        private void PollStopping()
        {
            if (!Net.IsPortOpen(_port, 200))
            {
                _phase = Phase.Stopped;
                UpdateUi();
                if (_pendingRestart)
                {
                    _pendingRestart = false;
                    StartServer(true);
                }
                return;
            }

            if (DateTime.Now > _stopDeadline)
            {
                // The CLI had its chance and the service is still up: force kill, then wait for the port
                if (_stopEscalatedAt == DateTime.MinValue)
                {
                    _stopEscalatedAt = DateTime.Now;
                    Sys.KillByPort(_port);
                }
                else if (DateTime.Now > _stopEscalatedAt.AddSeconds(6))
                {
                    _phase = Phase.Running;
                    _pendingRestart = false;
                    UpdateUi();
                    NotifyError(L.T("notify.stopFailedTitle"), L.T("notify.stopFailedBody", _port));
                }
            }
        }

        // If something else stops or starts the service, the icon must follow
        private void PollPortSync()
        {
            bool actuallyRunning = Net.IsPortOpen(_port, 200);

            if (actuallyRunning && (_phase == Phase.Stopped || _phase == Phase.Failed || _phase == Phase.NeedsDeps || _phase == Phase.NeedsNode))
            {
                _phase = Phase.Running;
                UpdateUi();
            }
            else if (!actuallyRunning && _phase == Phase.Running)
            {
                _phase = Phase.Stopped;
                UpdateUi();
            }
        }

        // ---------------- environment probe ----------------

        private void BeginEnvironmentCheck(bool thenStart)
        {
            lock (_checkLock)
            {
                if (_checkRunning)
                {
                    // A check is already in flight: remember the request instead of dropping it,
                    // otherwise "install finished -> check -> start" can silently lose the start.
                    _checkPending = true;
                    _checkPendingThenStart = _checkPendingThenStart || thenStart;
                    return;
                }
                _checkRunning = true;
                _checkDone = false;
                _checkResult = null;
                _checkThenStart = thenStart;
            }

            if (_phase != Phase.Installing) _phase = Phase.Checking;
            UpdateUi();

            Thread worker = new Thread(delegate ()
            {
                ProbeResult result = null;
                try
                {
                    result = EnvironmentProbe.Detect(_config);
                }
                catch (Exception)
                {
                }
                lock (_checkLock)
                {
                    _checkResult = result;
                    _checkRunning = false;
                    _checkDone = true;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void PollEnvironmentCheck()
        {
            ProbeResult result;
            bool thenStart;
            lock (_checkLock)
            {
                if (!_checkDone) return;
                _checkDone = false;
                result = _checkResult;
                thenStart = _checkThenStart;
            }

            ApplyCheckResult(result, thenStart);

            // A menu click asked to install before the first probe had finished. The probe has
            // just supplied the paths/versions the installer needs, so run it now.
            bool installAfterCheck;
            bool installAfterCheckForce;
            lock (_checkLock)
            {
                installAfterCheck = _installAfterCheck;
                installAfterCheckForce = _installAfterCheckForce;
                _installAfterCheck = false;
                _installAfterCheckForce = false;
            }
            if (installAfterCheck && _phase != Phase.Installing && _phase != Phase.NeedsNode && _probe != null)
            {
                BeginInstall(installAfterCheckForce);
            }

            // A request arrived while this check was running (install finished, icon clicked, ...)
            bool pending;
            bool pendingThenStart;
            lock (_checkLock)
            {
                pending = _checkPending;
                pendingThenStart = _checkPendingThenStart;
                _checkPending = false;
                _checkPendingThenStart = false;
            }
            if (pending && _phase != Phase.Installing) BeginEnvironmentCheck(pendingThenStart);
        }

        private void ApplyCheckResult(ProbeResult result, bool thenStart)
        {
            if (result == null)
            {
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("notify.envCheckFailed"), L.T("notify.envCheckFailedBody"));
                return;
            }

            _probe = result;
            _config.NodePath = result.NodePath;
            if (result.PiWebFound) _config.ServerScript = result.PiWebScript;

            if (result.NeedsNode)
            {
                _phase = Phase.NeedsNode;
                UpdateUi();
                NotifyWarningOnce("needs-node",
                    L.T("notify.needsNodeTitle", ProbeResult.NodeMinimum),
                    L.T("notify.needsNodeBody", result.NodeIssue()));
                return;
            }

            if (result.NeedsPiWeb)
            {
                if (_config.AutoInstall)
                {
                    BeginInstall(false);
                    return;
                }
                _phase = Phase.NeedsDeps;
                UpdateUi();
                NotifyWarningOnce("needs-piweb",
                    L.T("notify.needsPiwebTitle"),
                    L.T("notify.needsPiwebBody"));
                return;
            }

            if (thenStart)
            {
                _phase = Phase.Stopped;
                StartServer(true);
                return;
            }

            _lastNotified = "";
            _phase = Net.IsPortOpen(_port, 200) ? Phase.Running : Phase.Stopped;
            UpdateUi();
        }

        // Ask the endpoint once, on a thread, so the UI never waits for HTTP
        private void BeginReadyCheck()
        {
            lock (_readyLock)
            {
                if (_readyCheckRunning) return;
                _readyCheckRunning = true;
            }

            Thread worker = new Thread(delegate ()
            {
                bool ok = Net.IsHttpOk("http://127.0.0.1:" + _port + "/", 3000);
                lock (_readyLock)
                {
                    _readyResult = ok ? 1 : -1;
                    _readyCheckRunning = false;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        // ---------------- install ----------------

        private void BeginInstall(bool force)
        {
            if (_phase == Phase.Installing) return;

            // The first probe can still be in flight (user clicked the menu immediately after
            // launch). Never run node/npm synchronously on the UI thread; queue the install and
            // let PollEnvironmentCheck start it when the probe result arrives.
            if (_probe == null)
            {
                lock (_checkLock)
                {
                    _installAfterCheck = true;
                    _installAfterCheckForce = _installAfterCheckForce || force;
                }
                BeginEnvironmentCheck(false);
                return;
            }

            ProbeResult probe = _probe;

            if (probe.NeedsNode)
            {
                _phase = Phase.NeedsNode;
                UpdateUi();
                NotifyWarningOnce("needs-node-install",
                    L.T("notify.noNodeInstallTitle"),
                    L.T("notify.noNodeInstallBody", probe.NodeIssue()));
                return;
            }

            List<string> packages = Installer.PackagesToInstall(probe, _config, force);

            if (packages.Count == 0)
            {
                BeginEnvironmentCheck(_autoStartService);
                return;
            }

            _phase = Phase.Installing;
            _installStartedAt = DateTime.Now;
            _installTicks = 0;
            lock (_installLock)
            {
                _installDone = false;
                _installResult = null;
            }
            UpdateUi();

            Notify(L.T("install.startTitle"), L.T("install.startBody", string.Join(", ", packages.ToArray())));

            Thread worker = new Thread(delegate ()
            {
                InstallResult result;
                try
                {
                    result = Installer.Install(_config, probe, force, null);
                }
                catch (Exception ex)
                {
                    result = new InstallResult();
                    result.Success = false;
                    result.Message = L.T("install.crashBody", ex.Message);
                }
                lock (_installLock)
                {
                    _installResult = result;
                    _installDone = true;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void PollInstall()
        {
            InstallResult result;
            lock (_installLock)
            {
                if (!_installDone) return;
                _installDone = false;
                result = _installResult;
            }

            if (result == null)
            {
                _phase = Phase.Failed;
                UpdateUi();
                return;
            }

            if (result.Success)
            {
                Notify(L.T("install.doneTitle"), result.Message);
                // Re-probe before starting so paths and versions are current. In autostart
                // mode this must not turn into "start the service": login shows the tray only.
                BeginEnvironmentCheck(_autoStartService);
            }
            else
            {
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("install.failedTitle"), result.Message);
            }
        }

        // ---------------- UI refresh ----------------

        private void UpdateUi()
        {
            string label;
            Color color;

            switch (_phase)
            {
                case Phase.Checking:
                    label = L.T("status.checking");
                    color = Icons.BusyColor;
                    break;
                case Phase.NeedsNode:
                    label = L.T("status.needsNode");
                    color = Icons.SetupColor;
                    break;
                case Phase.NeedsDeps:
                    label = L.T("status.needsDeps");
                    color = Icons.SetupColor;
                    break;
                case Phase.Installing:
                    label = InstallingLabel();
                    color = Icons.BusyColor;
                    break;
                case Phase.Starting:
                    label = L.T("status.starting", _port);
                    color = Icons.BusyColor;
                    break;
                case Phase.Running:
                    label = L.T("status.running", _port);
                    color = Icons.RunningColor;
                    break;
                case Phase.Stopping:
                    label = L.T("status.stopping", _port);
                    color = Icons.BusyColor;
                    break;
                case Phase.Failed:
                    label = L.T("status.failed");
                    color = Icons.ErrorColor;
                    break;
                default:
                    label = L.T("status.stopped");
                    color = Icons.StoppedColor;
                    break;
            }

            if (_currentIcon == null || !_iconColor.Equals(color))
            {
                try
                {
                    // Icons are cached per colour by Icons.Create, so nothing is disposed here
                    _currentIcon = Icons.Create(color);
                    _iconColor = color;
                    _tray.Icon = _currentIcon;
                }
                catch (Exception)
                {
                    // GDI+ can fail on trimmed-down/server installs; never let a missing icon
                    // take down the tray state machine.
                    try { _tray.Icon = SystemIcons.Application; } catch (Exception) { }
                }
            }

            string suffix = _elevated ? L.T("status.admin") : "";
            try { _tray.Text = Shell.Clip(L.T("status.tooltip", label + suffix), 63); } catch (Exception) { }

            _statusItem.Text = (_phase == Phase.Running ? L.T("status.bulletOn") : L.T("status.bulletOff")) + label + (_elevated ? L.T("status.adminLong") : "");

            bool busy = _phase == Phase.Installing || _phase == Phase.Checking || _phase == Phase.Starting || _phase == Phase.Stopping;
            bool running = _phase == Phase.Running;
            bool ready = _probe != null && _probe.Ready;
            bool needsNode = _phase == Phase.NeedsNode || (_probe != null && _probe.NeedsNode);
            bool needsSetup = _phase == Phase.NeedsNode || _phase == Phase.NeedsDeps;

            _openItem.Text = running ? L.T("menu.open") : L.T("menu.startAndOpen");
            _openItem.Enabled = !busy;
            _startItem.Enabled = !busy && !running && ready;
            _stopItem.Enabled = running;
            _restartItem.Enabled = !busy && running;
            _installItem.Enabled = !busy;
            _installItem.Text = ready ? L.T("menu.installDeps") : L.T("menu.installAuto");
            _nodeItem.Enabled = needsNode;
            _installLogItem.Enabled = File.Exists(_config.InstallLogFile);

            if (needsSetup) _openItem.Text = L.T("menu.setupFirst");
        }

        private string InstallingLabel()
        {
            InstallProgress progress = Installer.Snapshot();
            TimeSpan elapsed = progress.Elapsed;
            if (_installStartedAt != DateTime.MinValue)
            {
                TimeSpan live = DateTime.Now - _installStartedAt;
                if (live > elapsed) elapsed = live;
            }

            string text = L.T("status.installing", FormatElapsed(elapsed));
            string last = progress.LastLine;
            if (!string.IsNullOrEmpty(last))
            {
                string trimmed = last.Trim();
                if (trimmed.Length > 0 && trimmed.Length < 40 && !trimmed.StartsWith("npm http", StringComparison.OrdinalIgnoreCase))
                    text = L.T("status.installingWith", FormatElapsed(elapsed), trimmed);
            }
            return text;
        }

        private static string FormatElapsed(TimeSpan span)
        {
            if (span.TotalSeconds < 60) return L.T("time.seconds", (int)span.TotalSeconds);
            return L.T("time.minutes", (int)span.TotalMinutes, span.Seconds);
        }

        // ---------------- service control ----------------

        private void OpenOrStart()
        {
            if (_phase == Phase.Running) { OpenBrowser(); return; }
            if (_phase == Phase.Installing || _phase == Phase.Checking) return;
            if (_phase == Phase.NeedsNode)
            {
                // Missing Node can only be fixed by the user; probe again (one click once it is installed)
                BeginEnvironmentCheck(true);
                return;
            }
            if (_phase == Phase.Failed)
            {
                // Re-probe first: the failure may have been a start timeout, not a broken
                // installation. ApplyCheckResult decides whether to retry install or start.
                BeginEnvironmentCheck(true);
                return;
            }
            if (_phase == Phase.NeedsDeps)
            {
                // The user clicked the icon, which counts as consent to install or repair
                if (_probe == null || _probe.NeedsNode) BeginEnvironmentCheck(true);
                else BeginInstall(false);
                return;
            }
            StartServer(true);
        }

        private void OpenBrowser()
        {
            try
            {
                using (Process browser = Process.Start("http://127.0.0.1:" + _port))
                {
                }
            }
            catch (Exception ex) { NotifyError(L.T("notify.noBrowser"), ex.Message); }
        }

        private void OpenUrl(string url)
        {
            try
            {
                using (Process browser = Process.Start(url))
                {
                }
            }
            catch (Exception ex) { NotifyError(L.T("notify.noLink"), ex.Message + "\r\n" + url); }
        }

        private void StartServer(bool openBrowser)
        {
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;

            if (Net.IsPortOpen(_port, 300))
            {
                _phase = Phase.Running;
                UpdateUi();
                if (openBrowser) OpenBrowser();
                return;
            }

            string script = _config.ServerScript;
            if (string.IsNullOrEmpty(script) || !File.Exists(script))
            {
                if (_probe != null && _probe.PiWebFound) script = _probe.PiWebScript;
            }

            if (string.IsNullOrEmpty(script) || !File.Exists(script))
            {
                _phase = Phase.NeedsDeps;
                UpdateUi();
                NotifyWarningOnce("no-script", L.T("notify.noPiwebTitle"), L.T("notify.noPiwebBody"));
                return;
            }

            string node = string.IsNullOrEmpty(_config.NodePath) ? "node.exe" : _config.NodePath;

            RotateLog();
            try
            {
                // Redirect output to the log through cmd: the tray is not in the pipe chain, so
                // the service does not die when the tray exits and the pipe closes.
                string command = Shell.Quote(node) + " " + Shell.Quote(script)
                    + " --no-open -p " + _port
                    + " 1>>" + Shell.Quote(_config.LogFile) + " 2>&1";

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Shell.ComSpec();
                psi.Arguments = Shell.CmdLine(command);
                psi.WorkingDirectory = _config.WorkingDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                // An elevated tray passes its token to the child, which is exactly what "run as admin" means here
                _server = Process.Start(psi);

                lock (_readyLock)
                {
                    _readyResult = 0;
                    _readyCheckRunning = false;
                }
                _phase = Phase.Starting;
                _openWhenReady = openBrowser;
                _startDeadline = DateTime.Now.AddSeconds(StartTimeoutSeconds);
                UpdateUi();
            }
            catch (Exception ex)
            {
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("notify.cannotStart"), ex.Message);
            }
        }

        private void StopServer()
        {
            if (_phase == Phase.Stopping) return;

            // Do not touch the state machine while no service state has been reached
            if (_phase == Phase.Checking || _phase == Phase.NeedsNode || _phase == Phase.NeedsDeps
                || _phase == Phase.Installing || _phase == Phase.Failed)
            {
                if (!Net.IsPortOpen(_port, 200)) return;
            }

            if (_phase == Phase.Starting)
            {
                // Stop immediately instead of waiting up to three minutes for the startup
                // deadline. The tree kill is asynchronous; PollStopping confirms the port is gone.
                _pendingRestart = false;
                try
                {
                    if (_server != null && !_server.HasExited) Sys.KillTree(_server.Id);
                }
                catch (Exception)
                {
                }
                _phase = Phase.Stopping;
                _stopDeadline = DateTime.Now.AddSeconds(10);
                _stopEscalatedAt = DateTime.MinValue;
                UpdateUi();
                return;
            }

            if (_phase == Phase.Installing)
            {
                NotifyWarningOnce("stop-during-install", L.T("notify.installBusyTitle"), L.T("notify.installBusyBody"));
                return;
            }

            if (!Net.IsPortOpen(_port, 300))
            {
                _phase = Phase.Stopped;
                UpdateUi();
                return;
            }

            _phase = Phase.Stopping;
            _stopDeadline = DateTime.Now.AddSeconds(20);
            _stopEscalatedAt = DateTime.MinValue;
            UpdateUi();

            // pi-web's own stop command starts node and takes a while, so run it in the background
            string script = _config.ServerScript;
            if (string.IsNullOrEmpty(script)) script = _probe != null ? _probe.PiWebScript : null;

            if (!string.IsNullOrEmpty(script) && File.Exists(script))
            {
                Thread worker = new Thread(delegate () { StopWithCli(script); });
                worker.IsBackground = true;
                worker.Start();
            }
            else
            {
                Sys.KillByPort(_port);
            }
        }

        private void StopWithCli(string script)
        {
            try
            {
                string node = string.IsNullOrEmpty(_config.NodePath) ? "node.exe" : _config.NodePath;
                ProcessRunner.Run(node, Shell.Quote(script) + " stop --port " + _port,
                    _config.WorkingDir, 20000, null, null);
            }
            catch (Exception)
            {
            }
        }

        private void RestartServer()
        {
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;
            _pendingRestart = true;
            if (_phase != Phase.Running)
            {
                _pendingRestart = false;
                StartServer(true);
                return;
            }
            StopServer();
        }

        private void ExitApp()
        {
            if (_exiting) return;

            // Aborting midway leaves a half-installed global package: ask first, and kill npm if confirmed
            if (_phase == Phase.Installing)
            {
                DialogResult answer = MessageBox.Show(
                    L.T("exit.installingBody"),
                    L.T("exit.installingTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
                Installer.Abort();
            }

            _exiting = true;
            _timer.Stop();

            try
            {
                // Exiting right after starting: kill the tree instead of waiting for it to come up
                if (_phase == Phase.Starting && _server != null)
                {
                    try { if (!_server.HasExited) Sys.KillTree(_server.Id); } catch (Exception) { }
                }

                if (Net.IsPortOpen(_port, 200))
                {
                    StopServer();
                    // Wait until it is really stopped, keeping the message loop alive meanwhile
                    int waited = 0;
                    while (waited < 8000 && Net.IsPortOpen(_port, 200))
                    {
                        Thread.Sleep(120);
                        Application.DoEvents();
                        waited += 120;
                    }
                    if (Net.IsPortOpen(_port, 200)) Sys.KillByPort(_port);
                    Net.WaitPortClosed(_port, 2000);
                }
            }
            catch (Exception)
            {
            }

            TrayState.Delete();
            Cleanup();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cleanup();
            base.Dispose(disposing);
        }

        private void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            try { _timer.Stop(); _timer.Dispose(); } catch (Exception) { }
            try { _tray.Visible = false; _tray.Dispose(); } catch (Exception) { }
            try { _menu.Dispose(); } catch (Exception) { }
            try { _openSignal.Close(); } catch (Exception) { }
            try { _stopSignal.Close(); } catch (Exception) { }
            try { _quitSignal.Close(); } catch (Exception) { }
            try { Icons.DisposeCache(); } catch (Exception) { }
            try { if (_server != null) _server.Dispose(); } catch (Exception) { }
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch (Exception) { }
                try { _mutex.Close(); } catch (Exception) { }
            }
        }

        // ---------------- self check ----------------

        private void RunSelfCheck()
        {
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    _selfCheckResult = EnvironmentProbe.Detect(_config);
                }
                catch (Exception ex)
                {
                    _selfCheckError = ex.Message;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void PollSelfCheck()
        {
            ProbeResult result = _selfCheckResult;
            string error = _selfCheckError;
            if (result == null && error == null) return;
            _selfCheckResult = null;
            _selfCheckError = null;

            if (result != null)
            {
                CheckForm.ShowReport(_config, result, _elevated);
            }
            else
            {
                MessageBox.Show(L.T("notify.selfCheckError", error), L.T("sc.title"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------------- logs ----------------

        private void RotateLog()
        {
            try
            {
                FileInfo info = new FileInfo(_config.LogFile);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    string backup = _config.LogFile + ".1";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(_config.LogFile, backup);
                }
            }
            catch (Exception)
            {
            }
        }

        // Returns false when the file is locked and cannot be rewritten
        private bool EnsureUtf8Bom(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return true;
                byte[] withBom = new byte[bytes.Length + 3];
                withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
                Buffer.BlockCopy(bytes, 0, withBom, 3, bytes.Length);
                File.WriteAllBytes(path, withBom);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void OpenLog(string path, string name)
        {
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, "");

                string target = path;
                if (!EnsureUtf8Bom(path))
                {
                    // The file is locked (service or install still writing), so write a BOM-prefixed snapshot instead
                    byte[] bytes = File.ReadAllBytes(path);
                    byte[] withBom = new byte[bytes.Length + 3];
                    withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
                    Buffer.BlockCopy(bytes, 0, withBom, 3, bytes.Length);
                    target = Path.Combine(Path.GetTempPath(), name);
                    File.WriteAllBytes(target, withBom);
                }

                using (Process notepad = Process.Start("notepad.exe", Shell.Quote(target)))
                {
                }
            }
            catch (Exception ex)
            {
                NotifyError(L.T("notify.openLogFailed"), ex.Message);
            }
        }

        // ---------------- autostart ----------------

        private bool IsRunKeyEnabled()
        {
            return Autostart.RunValueExists();
        }

        private bool IsTaskEnabled()
        {
            return Autostart.QueryTask();
        }

        private void RefreshAutostartItems()
        {
            BeginTaskRefresh();
            UpdateAutostartItems();
        }

        // Write the real state into the menu text and the check box (double safety: the box can be clipped)
        private void UpdateAutostartItems()
        {
            bool task = TaskExists(true);
            bool run = IsRunKeyEnabled();
            bool busy = AutostartBusy();

            _autostartItem.Text = busy
                ? L.T("menu.autostartBusy")
                : L.T("menu.autostart", Autostart.Describe(task, run));
            _autostartItem.Checked = task || run;
            _autostartItem.Enabled = !busy;

            _adminAutostartItem.Text = L.T("menu.adminAutostart", _config.AutostartAdmin ? L.T("bool.yes") : L.T("bool.no"));
            _adminAutostartItem.Checked = _config.AutostartAdmin;
            _adminAutostartItem.Enabled = !busy;
        }

        private void ToggleAutostart()
        {
            BeginAutostartOp(!_autostartItem.Checked, _config.AutostartAdmin);
        }

        private void ToggleAdminAutostart()
        {
            if (AutostartBusy()) return;

            bool previous = _config.AutostartAdmin;
            _config.AutostartAdmin = !previous;
            _config.SetValue("AutostartAdmin", _config.AutostartAdmin ? "1" : "0");
            UpdateAutostartItems();

            // If autostart is already on, re-apply it in the new mode. If that fails (for
            // example the UAC prompt is cancelled), put the preference back so the menu does
            // not claim a mode that is not actually installed.
            if (_autostartItem.Checked)
            {
                lock (_autoLock) _revertAdminAutostart = previous ? 1 : 0;
                BeginAutostartOp(true, _config.AutostartAdmin);
            }
        }

        // schtasks and UAC can take seconds, so always run them in the background and poll the result
        private void BeginAutostartOp(bool enable, bool admin)
        {
            lock (_autoLock)
            {
                if (_autoBusy) return;
                _autoBusy = true;
                _autoDone = false;
                _autoMessage = null;
            }
            UpdateAutostartItems();

            Thread worker = new Thread(delegate ()
            {
                string message;
                bool error;
                try
                {
                    error = !Autostart.Apply(enable, admin, _elevated, Application.ExecutablePath, out message);
                }
                catch (Exception ex)
                {
                    error = true;
                    message = L.T("auto.failed") + "\r\n" + ex.Message;
                }
                lock (_autoLock)
                {
                    _autoMessage = message;
                    _autoError = error;
                    _autoBusy = false;
                    _autoDone = true;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void PollAutostartOp()
        {
            string message;
            bool error;
            int revertAdmin;
            lock (_autoLock)
            {
                if (!_autoDone) return;
                _autoDone = false;
                message = _autoMessage;
                error = _autoError;
                revertAdmin = _revertAdminAutostart;
                _revertAdminAutostart = -1;
                _autoMessage = null;
            }

            if (error && revertAdmin >= 0)
            {
                _config.AutostartAdmin = revertAdmin == 1;
                _config.SetValue("AutostartAdmin", _config.AutostartAdmin ? "1" : "0");
            }

            RefreshAutostartItems();
            if (!string.IsNullOrEmpty(message))
            {
                if (error) NotifyError(L.T("notify.autostartFailed"), message);
                else Notify(L.T("notify.autostartTitle"), message);
            }
        }

        // Never block the UI on schtasks: refresh in the background and report the last known value
        private bool TaskExists(bool kickRefresh)
        {
            bool value;
            DateTime at;
            lock (_taskCacheLock)
            {
                value = _taskCache;
                at = _taskCacheAt;
            }

            bool stale = at == DateTime.MinValue || DateTime.Now - at > TimeSpan.FromSeconds(10);
            if (kickRefresh && stale) BeginTaskRefresh();
            return value;
        }

        private void BeginTaskRefresh()
        {
            lock (_taskCacheLock)
            {
                if (_taskRefreshRunning) return;
                _taskRefreshRunning = true;
            }

            Thread worker = new Thread(delegate ()
            {
                bool task = false;
                try { task = IsTaskEnabled(); } catch (Exception) { }
                lock (_taskCacheLock)
                {
                    _taskCache = task;
                    _taskCacheAt = DateTime.Now;
                    _taskRefreshRunning = false;
                    _taskRefreshDone = true;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void PollTaskRefresh()
        {
            bool done;
            lock (_taskCacheLock)
            {
                done = _taskRefreshDone;
                _taskRefreshDone = false;
            }
            if (done) UpdateAutostartItems();

            // Keep the cached value from going stale while the tray sits idle
            bool stale;
            DateTime at;
            lock (_taskCacheLock) at = _taskCacheAt;
            stale = at == DateTime.MinValue || DateTime.Now - at > TimeSpan.FromSeconds(60);
            if (stale) BeginTaskRefresh();
        }

        private bool AutostartBusy()
        {
            lock (_autoLock) return _autoBusy;
        }

        // ---------------- helpers ----------------

        private void CreateDesktopShortcut()
        {
            object shell = null;
            object shortcut = null;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string path = Path.Combine(desktop, "Pi Web.lnk");

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    NotifyError(L.T("notify.createFailed"), L.T("notify.noShell"));
                    return;
                }
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                if (shortcut == null) throw new InvalidOperationException("CreateShortcut returned null");

                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
                shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { _config.BaseDir });
                shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath + ",0" });
                shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { L.T("menu.open") });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                Notify(L.T("notify.shortcutCreated"), path);
            }
            catch (Exception ex)
            {
                NotifyError(L.T("notify.createFailed"), ex.Message);
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static void ReleaseComObject(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.ReleaseComObject(value); } catch (Exception) { }
        }

        // ---------------- notifications (one balloon per state, no spam) ----------------

        private void Notify(string title, string body)
        {
            Notifications.Show(_tray, title, body);
        }

        private void NotifyWarning(string title, string body)
        {
            Notifications.ShowWarning(_tray, title, body);
        }

        private void NotifyError(string title, string body)
        {
            Notifications.ShowError(_tray, title, body);
        }

        // Do not repeat the balloon when the same state is entered again
        private void NotifyWarningOnce(string key, string title, string body)
        {
            if (_lastNotified == key) return;
            _lastNotified = key;
            NotifyWarning(title, body);
        }
    }
}
