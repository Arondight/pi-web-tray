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
        Failed,
        PortConflict
    }

    internal enum UpdateStage
    {
        None,
        Checking,
        Downloading,
        Applying
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private const int StartTimeoutSeconds = 180;
        private const long MaxLogBytes = 4 * 1024 * 1024;

        private readonly Config _config;
        private readonly bool _elevated;
        private readonly bool _autoStartService;
        private readonly bool _initialOpenBrowser;
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
        private readonly ToolStripMenuItem _updateItem;
        private readonly ToolStripMenuItem _nodeItem;
        private readonly ToolStripMenuItem _checkItem;
        private readonly ToolStripMenuItem _settingsItem;
        private readonly ToolStripMenuItem _installLogItem;
        private readonly ToolStripMenuItem _updateLogItem;
        private readonly ToolStripMenuItem _copyUrlItem;
        private readonly ToolStripMenuItem _restartAdminItem;
        private readonly ToolStripMenuItem _autostartItem;
        private readonly ToolStripMenuItem _adminAutostartItem;
        private readonly ToolStripMenuItem _aboutItem;

        private Process _server;
        private InstallForm _installForm;

        private Phase _phase = Phase.Checking;
        private ProbeResult _probe;
        private Color _iconColor = Color.Empty;
        private Icon _currentIcon;
        private string _lastNotified = "";
        private Action _balloonAction;

        // environment probe (background -> UI thread)
        private readonly object _checkLock = new object();
        private bool _checkRunning;
        private bool _checkDone;
        private ProbeResult _checkResult;
        private bool _checkThenStart;
        private bool _checkOpenBrowser;
        private bool _checkPending;
        private bool _checkPendingThenStart;
        private bool _checkPendingOpenBrowser;
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
        private bool _pendingOpenBrowser;
        private bool _exiting;
        private bool _cleaned;
        private DateTime _startDeadline;
        private DateTime _stopDeadline;
        private DateTime _lastSignalHandled = DateTime.MinValue;
        private DateTime _stopEscalatedAt = DateTime.MinValue;
        private DateTime _autoRestartAt = DateTime.MinValue;
        private int _autoRestartAttempts;
        private DateTime _lastOwnerCheck = DateTime.MinValue;

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

        // update (background -> UI thread)
        private readonly object _updateLock = new object();
        private bool _updateBusy;
        private bool _updateDone;
        private UpdateStage _updateStage = UpdateStage.None;
        private ReleaseInfo _updateRelease;
        private ReleaseInfo _availableRelease;
        private bool _updateSilent;
        private int _updateGeneration;
        private UpdateForm _updateForm;
        private DateTime _autoUpdateCheckAt = DateTime.MinValue;
        private string _updateStaging;
        private string _updateError;
        private volatile int _updatePercent = -1;
        private int _updateTicks;
        private int _portPollTicks;

        private readonly object _taskCacheLock = new object();
        private bool _taskCache;
        private DateTime _taskCacheAt = DateTime.MinValue;
        private bool _taskRefreshRunning;
        private bool _taskRefreshDone;

        // readiness: "port accepts connections" is not the same as "the app answers HTTP"
        private readonly object _readyLock = new object();
        private int _readyResult;
        private bool _readyCheckRunning;

        internal TrayApp(Config config, bool elevated, Mutex mutex, bool startServer,
            bool openBrowserOnStart)
        {
            _config = config;
            _elevated = elevated;
            _autoStartService = startServer;
            _initialOpenBrowser = openBrowserOnStart;
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
            _startItem.Click += delegate { StartServer(false); };

            _stopItem = new ToolStripMenuItem(L.T("menu.stop"));
            _stopItem.Click += delegate { StopServer(); };

            _restartItem = new ToolStripMenuItem(L.T("menu.restart"));
            _restartItem.Click += delegate { RestartServer(false); };

            _installItem = new ToolStripMenuItem(L.T("menu.installDeps"));
            _installItem.Click += delegate { ConfirmInstall(); };

            _updateItem = new ToolStripMenuItem(L.T("menu.checkUpdates"));
            _updateItem.Click += delegate
            {
                if (_availableRelease != null && !UpdateBusy()) OfferUpdate(_availableRelease);
                else BeginUpdateCheck(false);
            };

            _nodeItem = new ToolStripMenuItem(L.T("menu.node"));
            _nodeItem.Click += delegate { OpenUrl("https://nodejs.org/en/download"); };

            _checkItem = new ToolStripMenuItem(L.T("menu.selfCheck"));
            _checkItem.Click += delegate { RunSelfCheck(); };

            _settingsItem = new ToolStripMenuItem(L.T("menu.settings"));
            _settingsItem.Click += delegate { ShowSettings(); };

            ToolStripMenuItem logItem = new ToolStripMenuItem(L.T("menu.serverLog"));
            logItem.Click += delegate { ShowLog(_config.LogFile, L.T("menu.serverLog")); };

            _installLogItem = new ToolStripMenuItem(L.T("menu.installLog"));
            _installLogItem.Click += delegate { ShowLog(_config.InstallLogFile, L.T("menu.installLog")); };

            _updateLogItem = new ToolStripMenuItem(L.T("menu.updateLog"));
            _updateLogItem.Click += delegate { ShowLog(_config.UpdateLogFile, L.T("menu.updateLog")); };

            ToolStripMenuItem logsItem = new ToolStripMenuItem(L.T("menu.logs"));
            logsItem.DropDownItems.Add(logItem);
            logsItem.DropDownItems.Add(_installLogItem);
            logsItem.DropDownItems.Add(_updateLogItem);

            ToolStripMenuItem folderItem = new ToolStripMenuItem(L.T("menu.folder"));
            folderItem.Click += delegate { OpenFolder(_config.BaseDir); };

            ToolStripMenuItem dataFolderItem = new ToolStripMenuItem(L.T("menu.dataFolder"));
            dataFolderItem.Click += delegate { OpenFolder(_config.OutputDir); };

            ToolStripMenuItem configItem = new ToolStripMenuItem(L.T("menu.config"));
            configItem.Click += delegate { OpenConfiguration(); };

            _copyUrlItem = new ToolStripMenuItem(L.T("menu.copyUrl"));
            _copyUrlItem.Click += delegate { CopyUrl(); };

            ToolStripMenuItem shortcutItem = new ToolStripMenuItem(L.T("menu.shortcut"));
            shortcutItem.Click += delegate { CreateDesktopShortcut(); };

            _autostartItem = new ToolStripMenuItem(L.T("menu.autostart", L.T("auto.off")));
            _autostartItem.Click += delegate { ToggleAutostart(); };

            _adminAutostartItem = new ToolStripMenuItem(L.T("menu.adminAutostart", L.T("bool.no")));
            _adminAutostartItem.Click += delegate { ToggleAdminAutostart(); };

            _restartAdminItem = new ToolStripMenuItem(L.T("menu.restartAdmin"));
            _restartAdminItem.Click += delegate { RestartAsAdministrator(); };

            _aboutItem = new ToolStripMenuItem(L.T("menu.about"));
            _aboutItem.Click += delegate { ShowAbout(); };

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
            _menu.Items.Add(_updateItem);
            _menu.Items.Add(_nodeItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_checkItem);
            _menu.Items.Add(_settingsItem);
            _menu.Items.Add(logsItem);
            _menu.Items.Add(dataFolderItem);
            _menu.Items.Add(folderItem);
            _menu.Items.Add(configItem);
            _menu.Items.Add(_copyUrlItem);
            _menu.Items.Add(shortcutItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_autostartItem);
            _menu.Items.Add(_adminAutostartItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_restartAdminItem);
            _menu.Items.Add(_aboutItem);
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
            _tray.BalloonTipClicked += delegate
            {
                Action action = _balloonAction;
                _balloonAction = null;
                if (action != null)
                {
                    try { action(); } catch (Exception) { }
                }
            };

            RefreshAutostartItems();
            if (!_elevated) Autostart.RepairRunValue(Application.ExecutablePath);

            _timer.Interval = 500;
            _timer.Tick += OnTick;
            _timer.Start();

            BeginEnvironmentCheck(_autoStartService, _initialOpenBrowser);
            if (_config.AutoCheckUpdates) _autoUpdateCheckAt = DateTime.Now.AddSeconds(10);
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
            PollUpdate();
            if (_exiting) return;

            if (_autoUpdateCheckAt != DateTime.MinValue && DateTime.Now >= _autoUpdateCheckAt
                && !UpdateBusy() && !_exiting
                && _phase != Phase.Installing && _phase != Phase.Checking
                && _phase != Phase.Starting && _phase != Phase.Stopping)
            {
                _autoUpdateCheckAt = DateTime.MinValue;
                BeginUpdateCheck(true);
            }

            if (_config.AutoRestart && _autoRestartAt != DateTime.MinValue
                && DateTime.Now >= _autoRestartAt
                && !UpdateBusy() && !_exiting
                && (_phase == Phase.Stopped || _phase == Phase.Failed))
            {
                _autoRestartAt = DateTime.MinValue;
                StartServer(false);
            }

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
                    _portPollTicks++;
                    if (_portPollTicks % 4 == 0) PollPortSync();
                    break;
            }

            if (UpdateBusy())
            {
                _updateTicks++;
                if (_updateTicks % 4 == 0) UpdateUi();
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
                _lastNotified = "";
                _lastOwnerCheck = DateTime.Now;
                _autoRestartAt = DateTime.MinValue;
                _autoRestartAttempts = 0;
                bool open = _openWhenReady;
                _openWhenReady = false;
                UpdateUi();
                if (open) OpenBrowser();
                Notify(L.T("notify.readyTitle"), "http://127.0.0.1:" + _port, delegate { OpenBrowser(); });
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
                NotifyError(L.T("notify.startFailedTitle"), L.T("notify.startFailedBody", reason),
                    delegate { ShowLog(_config.LogFile, L.T("menu.serverLog")); });
                ScheduleAutoRestart();
            }
        }

        private void PollStopping()
        {
            if (!Net.IsPortOpen(_port, 200))
            {
                ServiceProbe.Invalidate();
                _phase = Phase.Stopped;
                UpdateUi();
                if (_pendingRestart)
                {
                    _pendingRestart = false;
                    bool open = _pendingOpenBrowser;
                    _pendingOpenBrowser = false;
                    StartServer(open);
                }
                return;
            }

            if (DateTime.Now > _stopDeadline)
            {
                // The CLI had its chance and the service is still up. Only force-kill a
                // confirmed pi-web process (or the process this tray started); never kill
                // an unknown listener that took the port.
                if (_stopEscalatedAt == DateTime.MinValue)
                {
                    ServiceProbe.Invalidate();
                    PortOwnerInfo owner = ServiceProbe.Inspect(_port);
                    if (owner.IsPiWeb || TrayOwnsPort())
                    {
                        _stopEscalatedAt = DateTime.Now;
                        Sys.KillByPort(_port);
                        ServiceProbe.Invalidate();
                    }
                    else
                    {
                        _phase = Phase.PortConflict;
                        _pendingRestart = false;
                        _pendingOpenBrowser = false;
                        UpdateUi();
                        NotifyPortConflict();
                    }
                }
                else if (DateTime.Now > _stopEscalatedAt.AddSeconds(6))
                {
                    _phase = Phase.Running;
                    _pendingRestart = false;
                    _pendingOpenBrowser = false;
                    UpdateUi();
                    NotifyError(L.T("notify.stopFailedTitle"), L.T("notify.stopFailedBody", _port));
                }
            }
        }

        // If something else stops or starts the service, the icon must follow.
        // A listener is not automatically pi-web: resolve the owner before showing
        // Running, and never treat an unknown listener as our service.
        private void PollPortSync()
        {
            bool actuallyRunning = Net.IsPortOpen(_port, 200);

            if (!actuallyRunning)
            {
                ServiceProbe.Invalidate();
                if (_phase == Phase.Running)
                {
                    _phase = Phase.Stopped;
                    UpdateUi();
                    NotifyWarningOnce("service-stopped",
                        L.T("notify.serviceStoppedTitle"),
                        L.T("notify.serviceStoppedBody"),
                        delegate { StartServer(false); });
                    ScheduleAutoRestart();
                }
                else if (_phase == Phase.PortConflict)
                {
                    _phase = Phase.Stopped;
                    _lastNotified = "";
                    UpdateUi();
                }
                return;
            }

            if (_phase == Phase.Running)
            {
                // Re-verify occasionally so a different listener taking the port does not
                // stay green forever. Critical stop/exit paths also invalidate the cache.
                if (DateTime.Now - _lastOwnerCheck < TimeSpan.FromSeconds(30)) return;
                _lastOwnerCheck = DateTime.Now;
                ServiceProbe.Invalidate();
                PortOwnerInfo current = ServiceProbe.Inspect(_port);
                if (current.IsPiWeb) return;
                if (current.IsUnknown && TrayOwnsPort()) return;

                _phase = current.IsUnknown ? Phase.PortConflict : Phase.Stopped;
                UpdateUi();
                if (_phase == Phase.PortConflict) NotifyPortConflict();
                return;
            }
            if (_phase != Phase.Stopped && _phase != Phase.Failed && _phase != Phase.NeedsDeps
                && _phase != Phase.NeedsNode && _phase != Phase.PortConflict)
            {
                return;
            }

            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            if (owner.IsPiWeb)
            {
                _phase = Phase.Running;
                _lastNotified = "";
                _lastOwnerCheck = DateTime.Now;
                UpdateUi();
            }
            else if (owner.IsUnknown)
            {
                if (_phase != Phase.PortConflict)
                {
                    _phase = Phase.PortConflict;
                    UpdateUi();
                    NotifyPortConflict();
                }
            }
            else if (_phase == Phase.PortConflict)
            {
                _phase = Phase.Stopped;
                UpdateUi();
            }
        }

        // ---------------- environment probe ----------------

        private void BeginEnvironmentCheck(bool thenStart, bool openBrowser)
        {
            lock (_checkLock)
            {
                if (_checkRunning)
                {
                    // A check is already in flight: remember the request instead of dropping it,
                    // otherwise "install finished -> check -> start" can silently lose the start.
                    _checkPending = true;
                    _checkPendingThenStart = _checkPendingThenStart || thenStart;
                    _checkPendingOpenBrowser = _checkPendingOpenBrowser || openBrowser;
                    return;
                }
                _checkRunning = true;
                _checkDone = false;
                _checkResult = null;
                _checkThenStart = thenStart;
                _checkOpenBrowser = openBrowser;
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
            bool openBrowser;
            lock (_checkLock)
            {
                if (!_checkDone) return;
                _checkDone = false;
                result = _checkResult;
                thenStart = _checkThenStart;
                openBrowser = _checkOpenBrowser;
            }

            ApplyCheckResult(result, thenStart, openBrowser);

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
            bool pendingOpenBrowser;
            lock (_checkLock)
            {
                pending = _checkPending;
                pendingThenStart = _checkPendingThenStart;
                pendingOpenBrowser = _checkPendingOpenBrowser;
                _checkPending = false;
                _checkPendingThenStart = false;
                _checkPendingOpenBrowser = false;
            }
            if (pending && _phase != Phase.Installing)
                BeginEnvironmentCheck(pendingThenStart, pendingOpenBrowser);
        }

        private void ApplyCheckResult(ProbeResult result, bool thenStart, bool openBrowser)
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
                StartServer(openBrowser);
                return;
            }

            _lastNotified = "";
            _phase = ResolveServicePhase();
            UpdateUi();
            if (_phase == Phase.PortConflict) NotifyPortConflict();
        }

        private bool TrayOwnsPort()
        {
            if (_server == null) return false;
            try
            {
                if (_server.HasExited) return false;
            }
            catch (Exception)
            {
                return false;
            }

            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            if (owner.IsPiWeb) return true;
            if (owner.Pid <= 0) return false;
            if (owner.Pid == _server.Id) return true;
            return ServiceProbe.ParentPid(owner.Pid) == _server.Id;
        }

        private Phase ResolveServicePhase()
        {
            if (!Net.IsPortOpen(_port, 200)) return Phase.Stopped;

            ServiceProbe.Invalidate();
            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            if (owner.IsPiWeb) return Phase.Running;
            if (owner.IsUnknown) return Phase.PortConflict;
            return Phase.Stopped;
        }

        private void NotifyPortConflict()
        {
            NotifyWarningOnce("port-conflict",
                L.T("notify.portConflictTitle"),
                L.T("notify.portConflictBody", _port),
                delegate { ShowSettings(); });
        }

        private void ApplyProbeResult(ProbeResult result)
        {
            if (result == null) return;

            _probe = result;
            _config.NodePath = result.NodePath;
            if (result.PiWebFound) _config.ServerScript = result.PiWebScript;

            if (result.NeedsNode) _phase = Phase.NeedsNode;
            else if (result.NeedsPiWeb) _phase = Phase.NeedsDeps;
            else _phase = ResolveServicePhase();

            UpdateUi();
            if (_phase == Phase.PortConflict) NotifyPortConflict();
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
                BeginEnvironmentCheck(false, false);
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
                BeginEnvironmentCheck(_autoStartService, _initialOpenBrowser);
                return;
            }

            Installer.ResetAbort();
            _phase = Phase.Installing;
            _installStartedAt = DateTime.Now;
            _installTicks = 0;
            ShowInstallForm();
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

            bool cancelled = _installForm != null && _installForm.Cancelled;
            CloseInstallForm();

            if (result == null)
            {
                _phase = cancelled ? Phase.NeedsDeps : Phase.Failed;
                UpdateUi();
                if (cancelled)
                    NotifyWarning(L.T("install.cancelledTitle"), L.T("install.cancelledBody"));
                return;
            }

            if (result.Success)
            {
                Notify(L.T("install.doneTitle"), result.Message);
                // Re-probe before starting so paths and versions are current. In autostart
                // mode this must not turn into "start the service": login shows the tray only.
                BeginEnvironmentCheck(_autoStartService, _initialOpenBrowser);
            }
            else if (cancelled)
            {
                _phase = Phase.NeedsDeps;
                UpdateUi();
                NotifyWarning(L.T("install.cancelledTitle"), L.T("install.cancelledBody"));
            }
            else
            {
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("install.failedTitle"), result.Message,
                    delegate { ShowLog(_config.InstallLogFile, L.T("menu.installLog")); });
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
                case Phase.PortConflict:
                    label = L.T("status.portConflict", _port);
                    color = Icons.ErrorColor;
                    break;
                default:
                    label = L.T("status.stopped");
                    color = Icons.StoppedColor;
                    break;
            }

            bool updateBusy = UpdateBusy();
            if (updateBusy)
            {
                if (_updateStage == UpdateStage.Checking)
                {
                    label = L.T("update.status.checking");
                }
                else if (_updateStage == UpdateStage.Downloading)
                {
                    int percent = _updatePercent;
                    label = percent >= 0
                        ? L.T("update.status.downloadingPercent", percent)
                        : L.T("update.status.downloading");
                }
                else
                {
                    label = L.T("update.status.applying");
                }
                color = Icons.BusyColor;
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

            bool busy = _phase == Phase.Installing || _phase == Phase.Checking || _phase == Phase.Starting || _phase == Phase.Stopping || updateBusy;
            bool running = _phase == Phase.Running;
            bool portConflict = _phase == Phase.PortConflict;
            bool ready = _probe != null && _probe.Ready;
            bool needsNode = _phase == Phase.NeedsNode || (_probe != null && _probe.NeedsNode);
            bool needsSetup = _phase == Phase.NeedsNode || _phase == Phase.NeedsDeps;

            _openItem.Text = running ? L.T("menu.open") : L.T("menu.startAndOpen");
            _openItem.Enabled = !busy && !portConflict;
            _startItem.Enabled = !busy && !running && ready && !portConflict;
            _stopItem.Enabled = running && !updateBusy;
            _restartItem.Enabled = !busy && running;
            _installItem.Enabled = !busy;
            _installItem.Text = ready ? L.T("menu.installDeps") : L.T("menu.installAuto");
            _updateItem.Enabled = !busy;
            _updateItem.Text = updateBusy
                ? L.T("menu.checkUpdatesBusy")
                : (_availableRelease != null
                    ? L.T("menu.updateAvailable", _availableRelease.Version)
                    : L.T("menu.checkUpdates"));

            if (_updateForm != null && updateBusy)
            {
                int formPercent = _updateStage == UpdateStage.Downloading ? _updatePercent : -1;
                bool canCancel = _updateStage == UpdateStage.Checking || _updateStage == UpdateStage.Downloading;
                _updateForm.SetState(label, formPercent, canCancel);
            }
            _nodeItem.Enabled = needsNode && !updateBusy;
            _checkItem.Enabled = !busy;
            _settingsItem.Enabled = !busy;
            _installLogItem.Enabled = File.Exists(_config.InstallLogFile);
            _updateLogItem.Enabled = File.Exists(_config.UpdateLogFile);
            _copyUrlItem.Enabled = running;
            _restartAdminItem.Enabled = !_elevated && !busy;

            if (needsSetup) _openItem.Text = L.T("menu.setupFirst");
            if (portConflict) _openItem.Text = L.T("menu.portConflict");
        }

        private void ShowInstallForm()
        {
            if (_installForm != null) return;
            _installForm = new InstallForm();
            _installForm.FormClosed += delegate { _installForm = null; };
            _installForm.Show();
            _installForm.Activate();
        }

        private void CloseInstallForm()
        {
            InstallForm form = _installForm;
            _installForm = null;
            if (form == null) return;
            try { form.CloseFromOwner(); } catch (Exception) { }
            try { form.Dispose(); } catch (Exception) { }
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

            string text = progress.StepCount > 0
                ? L.T("status.installingStep", progress.Step, progress.StepCount, FormatElapsed(elapsed))
                : L.T("status.installing", FormatElapsed(elapsed));
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

        private void ConfirmInstall()
        {
            if (_probe != null && _probe.Ready)
            {
                DialogResult answer = MessageBox.Show(
                    L.T("install.confirmBody"),
                    L.T("install.confirmTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }
            BeginInstall(true);
        }

        private void OpenOrStart()
        {
            if (UpdateBusy()) return;
            if (_phase == Phase.PortConflict)
            {
                MessageBox.Show(
                    L.T("notify.portConflictBody", _port),
                    L.T("notify.portConflictTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                ShowSettings();
                return;
            }
            if (_phase == Phase.Running) { OpenBrowser(); return; }
            if (_phase == Phase.Installing || _phase == Phase.Checking) return;
            if (_phase == Phase.NeedsNode)
            {
                // Missing Node can only be fixed by the user; probe again (one click once it is installed)
                BeginEnvironmentCheck(true, true);
                return;
            }
            if (_phase == Phase.Failed)
            {
                // Re-probe first: the failure may have been a start timeout, not a broken
                // installation. ApplyCheckResult decides whether to retry install or start.
                BeginEnvironmentCheck(true, true);
                return;
            }
            if (_phase == Phase.NeedsDeps)
            {
                // The user clicked the icon, which counts as consent to install or repair
                if (_probe == null || _probe.NeedsNode) BeginEnvironmentCheck(true, true);
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

        private void ScheduleAutoRestart()
        {
            if (!_config.AutoRestart || _exiting || UpdateBusy()) return;
            if (_probe == null || !_probe.Ready) return;
            if (_phase == Phase.Installing || _phase == Phase.Starting || _phase == Phase.Stopping) return;
            if (_autoRestartAt != DateTime.MinValue) return;
            if (_autoRestartAttempts >= 3) return;

            _autoRestartAttempts++;
            int delay = _autoRestartAttempts == 1 ? 5 : (_autoRestartAttempts == 2 ? 15 : 30);
            _autoRestartAt = DateTime.Now.AddSeconds(delay);
            NotifyWarning(L.T("notify.autoRestartTitle"), L.T("notify.autoRestartBody", delay));
        }

        private void StartServer(bool openBrowser)
        {
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;

            ServiceProbe.Invalidate();
            if (Net.IsPortOpen(_port, 300))
            {
                PortOwnerInfo owner = ServiceProbe.Inspect(_port);
                if (owner.IsPiWeb)
                {
                    _phase = Phase.Running;
                    _lastNotified = "";
                    _lastOwnerCheck = DateTime.Now;
                    _autoRestartAt = DateTime.MinValue;
                    _autoRestartAttempts = 0;
                    UpdateUi();
                    if (openBrowser) OpenBrowser();
                    return;
                }
                if (owner.IsUnknown)
                {
                    if (TrayOwnsPort())
                    {
                        _phase = Phase.Running;
                        _lastNotified = "";
                        _lastOwnerCheck = DateTime.Now;
                        UpdateUi();
                        if (openBrowser) OpenBrowser();
                        return;
                    }
                    _phase = Phase.PortConflict;
                    UpdateUi();
                    NotifyPortConflict();
                    return;
                }
                // The listener disappeared between the check and the inspection:
                // continue and try to start the service.
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
                ServiceProbe.Invalidate();

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
            _autoRestartAt = DateTime.MinValue;
            _autoRestartAttempts = 0;

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
                _pendingOpenBrowser = false;
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
                if (_pendingRestart)
                {
                    _pendingRestart = false;
                    bool open = _pendingOpenBrowser;
                    _pendingOpenBrowser = false;
                    StartServer(open);
                }
                return;
            }

            // A listener that is not pi-web must never be stopped or killed. The only
            // exception is a service started by this tray (the tracked cmd child), whose
            // owner may be temporarily unreadable through WMI.
            ServiceProbe.Invalidate();
            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            bool ownedByTray = TrayOwnsPort();
            if (!owner.IsPiWeb && !ownedByTray)
            {
                _pendingRestart = false;
                _pendingOpenBrowser = false;
                _phase = owner.IsUnknown ? Phase.PortConflict : Phase.Stopped;
                UpdateUi();
                if (_phase == Phase.PortConflict) NotifyPortConflict();
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
                ServiceProbe.Invalidate();
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

        private void RestartServer(bool openBrowser)
        {
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;
            _pendingRestart = true;
            _pendingOpenBrowser = openBrowser;
            if (_phase != Phase.Running)
            {
                _pendingRestart = false;
                _pendingOpenBrowser = false;
                StartServer(openBrowser);
                return;
            }
            StopServer();
        }

        private void ExitApp()
        {
            if (_exiting) return;

            // The updater calls ExitApp only after its helper has been started. Any other
            // update stage can still be abandoned, but the user should confirm it.
            if (UpdateBusy() && _updateStage != UpdateStage.Applying)
            {
                DialogResult updateAnswer = MessageBox.Show(
                    L.T("update.busyBody"),
                    L.T("update.busyTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (updateAnswer != DialogResult.Yes) return;
            }

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
                CloseInstallForm();
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
                    ServiceProbe.Invalidate();
                    PortOwnerInfo owner = ServiceProbe.Inspect(_port);
                    bool ownedByTray = TrayOwnsPort();
                    if ((owner.IsUnknown || owner.Owner == PortOwner.None) && !ownedByTray)
                    {
                        // Another program owns the port. Leave it alone and keep the
                        // exit fast instead of waiting for a service that is not ours.
                        Updater.AppendLog(_config.UpdateLogFile,
                            "exit: port " + _port + " is used by another program; not stopped");
                    }
                    else
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
                        if (Net.IsPortOpen(_port, 200))
                        {
                            ServiceProbe.Invalidate();
                            owner = ServiceProbe.Inspect(_port);
                            if (owner.IsPiWeb || TrayOwnsPort())
                            {
                                Sys.KillByPort(_port);
                            }
                            else
                            {
                                Updater.AppendLog(_config.UpdateLogFile,
                                    "exit: port " + _port + " changed owner; not killed");
                            }
                        }
                        Net.WaitPortClosed(_port, 2000);
                    }
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
            CloseInstallForm();
            CloseUpdateForm();
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
                ProbeResult latest = CheckForm.ShowReport(_config, result, _elevated);
                ApplyProbeResult(latest);
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
            bool busy = AutostartBusy() || UpdateBusy();

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

        // ---------------- update ----------------

        private bool UpdateBusy()
        {
            lock (_updateLock) return _updateBusy;
        }

        private void BeginUpdateCheck(bool silent)
        {
            if (_exiting) return;
            if (_phase == Phase.Installing || _phase == Phase.Checking
                || _phase == Phase.Starting || _phase == Phase.Stopping)
            {
                return;
            }

            int generation;
            lock (_updateLock)
            {
                if (_updateBusy) return;
                _updateBusy = true;
                _updateDone = false;
                _updateStage = UpdateStage.Checking;
                _updateSilent = silent;
                _updateRelease = null;
                _updateStaging = null;
                _updateError = null;
                _updateGeneration++;
                generation = _updateGeneration;
            }

            if (!silent) ShowUpdateForm();
            _updatePercent = -1;
            _updateTicks = 0;
            UpdateUi();
            Updater.AppendLog(_config.UpdateLogFile,
                "check start (current " + Program.Version + (silent ? ", automatic" : "") + ")");
            if (!silent) Notify(L.T("update.checkingTitle"), L.T("update.checkingBody", Program.Version));

            Thread worker = new Thread(delegate ()
            {
                string error;
                ReleaseInfo release = Updater.FindLatest(out error);
                lock (_updateLock)
                {
                    if (generation != _updateGeneration) return;
                    _updateRelease = release;
                    _updateError = error;
                    _updateDone = true;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void PollUpdate()
        {
            UpdateStage stage;
            ReleaseInfo release;
            string staging;
            string error;
            lock (_updateLock)
            {
                if (!_updateDone) return;
                _updateDone = false;
                stage = _updateStage;
                release = _updateRelease;
                staging = _updateStaging;
                error = _updateError;
                _updateStaging = null;
                _updateError = null;
            }

            if (stage == UpdateStage.Checking) HandleUpdateCheck(release, error);
            else if (stage == UpdateStage.Downloading) HandleUpdateDownload(release, staging, error);
            else if (stage == UpdateStage.Applying) HandleUpdateApply(error);
        }

        private void HandleUpdateCheck(ReleaseInfo release, string error)
        {
            bool silent = _updateSilent;

            if (!string.IsNullOrEmpty(error))
            {
                Updater.AppendLog(_config.UpdateLogFile, "check failed: " + error);
                EndUpdate();
                if (!silent)
                    ShowUpdateError(L.T("update.checkFailedTitle"), L.T("update.checkFailedBody", error));
                return;
            }

            if (release == null || !Updater.IsNewer(release.Version, Program.Version))
            {
                Updater.AppendLog(_config.UpdateLogFile, "up to date");
                _availableRelease = null;
                EndUpdate();
                if (!silent)
                {
                    MessageBox.Show(
                        L.T("update.upToDateBody", Program.Version),
                        L.T("update.upToDateTitle"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return;
            }

            _availableRelease = release;
            if (silent)
            {
                Updater.AppendLog(_config.UpdateLogFile,
                    "update available: " + release.Tag + " (" + release.ZipName + ")");
                EndUpdate();
                Notify(L.T("update.availableTitle"), L.T("update.availableBody", release.Version),
                    delegate { OfferUpdate(release); });
                return;
            }

            OfferUpdate(release);
        }

        private void OfferUpdate(ReleaseInfo release)
        {
            if (release == null) return;

            Updater.AppendLog(_config.UpdateLogFile, "latest: " + release.Tag + " (" + release.ZipName + ")");
            string body = L.T("update.confirmBody", Program.Version, release.Version);
            if (!_elevated && Updater.RequiresElevation(_config.BaseDir))
                body += "\r\n\r\n" + L.T("update.adminNote");

            DialogResult answer = MessageBox.Show(
                body,
                L.T("update.confirmTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                Updater.AppendLog(_config.UpdateLogFile, "cancelled by user");
                if (UpdateBusy()) EndUpdate();
                return;
            }

            BeginUpdateDownload(release);
        }

        private void BeginUpdateDownload(ReleaseInfo release)
        {
            int generation;
            lock (_updateLock)
            {
                _updateStage = UpdateStage.Downloading;
                _updateDone = false;
                _updateRelease = release;
                _updateStaging = null;
                _updateError = null;
                _updateGeneration++;
                generation = _updateGeneration;
            }

            ShowUpdateForm();
            _updatePercent = -1;
            _updateTicks = 0;
            UpdateUi();
            Notify(L.T("update.downloadingTitle"), L.T("update.downloadingBody", release.Version));

            Thread worker = new Thread(delegate ()
            {
                string error;
                string staging = Updater.Prepare(
                    _config,
                    release,
                    delegate (int percent) { _updatePercent = percent; },
                    out error);
                lock (_updateLock)
                {
                    if (generation != _updateGeneration) return;
                    _updateStaging = staging;
                    _updateError = error;
                    _updateDone = true;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void HandleUpdateDownload(ReleaseInfo release, string staging, string error)
        {
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(staging))
            {
                Updater.AppendLog(_config.UpdateLogFile,
                    "download failed: " + (string.IsNullOrEmpty(error) ? L.T("update.error.unknown") : error));
                EndUpdate();
                ShowUpdateError(
                    L.T("update.downloadFailedTitle"),
                    L.T("update.downloadFailedBody",
                        string.IsNullOrEmpty(error) ? L.T("update.error.unknown") : error,
                        _config.UpdateLogFile));
                return;
            }

            if (release == null)
            {
                EndUpdate();
                return;
            }

            BeginUpdateApply(release, staging);
        }

        private void BeginUpdateApply(ReleaseInfo release, string staging)
        {
            int generation;
            lock (_updateLock)
            {
                _updateStage = UpdateStage.Applying;
                _updateDone = false;
                _updateError = null;
                _updateGeneration++;
                generation = _updateGeneration;
            }

            ShowUpdateForm();
            _updatePercent = -1;
            _updateTicks = 0;
            UpdateUi();
            Notify(L.T("update.applyingTitle"), L.T("update.applyingBody", release.Version));

            ServiceProbe.Invalidate();
            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            ApplyStartMode mode = owner.IsPiWeb || TrayOwnsPort()
                ? ApplyStartMode.ServiceSilent
                : ApplyStartMode.TrayOnly;
            bool elevated = _elevated;
            Thread worker = new Thread(delegate ()
            {
                string error;
                bool ok = Updater.StartHelper(_config, staging, mode, elevated, out error);
                lock (_updateLock)
                {
                    if (generation != _updateGeneration) return;
                    _updateError = ok ? "" : error;
                    _updateDone = true;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void HandleUpdateApply(string error)
        {
            if (!string.IsNullOrEmpty(error))
            {
                Updater.AppendLog(_config.UpdateLogFile, "helper start failed: " + error);
                EndUpdate();
                ShowUpdateError(
                    L.T("update.startFailedTitle"),
                    L.T("update.startFailedBody", error, _config.UpdateLogFile));
                return;
            }

            // The helper is already waiting for this process to exit. Stop pi-web and
            // let it replace the files and start the new tray.
            Updater.AppendLog(_config.UpdateLogFile, "helper started; stopping the tray");
            ExitApp();
        }

        private void EndUpdate()
        {
            _updateGeneration++;
            CloseUpdateForm();
            lock (_updateLock)
            {
                _updateBusy = false;
                _updateDone = false;
                _updateStage = UpdateStage.None;
                _updateRelease = null;
                _updateStaging = null;
                _updateError = null;
            }

            _updatePercent = -1;
            UpdateUi();
        }

        private void ShowUpdateForm()
        {
            if (_updateForm != null) return;
            _updateForm = new UpdateForm(CancelUpdate);
            _updateForm.FormClosed += delegate { _updateForm = null; };
            _updateForm.Show();
            _updateForm.Activate();
        }

        private void CloseUpdateForm()
        {
            UpdateForm form = _updateForm;
            _updateForm = null;
            if (form == null) return;
            try { form.CloseFromOwner(); } catch (Exception) { }
            try { form.Dispose(); } catch (Exception) { }
        }

        private void CancelUpdate()
        {
            if (!UpdateBusy()) return;
            Updater.AppendLog(_config.UpdateLogFile, "cancelled by user");
            _updateGeneration++;
            EndUpdate();
            NotifyWarning(L.T("update.cancelledTitle"), L.T("update.cancelledBody"));
        }

        private void ShowUpdateError(string title, string body)
        {
            MessageBox.Show(body, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowLog(string path, string title)
        {
            try
            {
                using (LogForm form = new LogForm(path, title))
                {
                    form.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                NotifyError(L.T("notify.openLogFailed"), ex.Message);
            }
        }

        private void ShowAbout()
        {
            using (AboutForm form = new AboutForm(delegate { BeginUpdateCheck(false); }))
            {
                form.ShowDialog();
            }
        }

        private void ShowSettings()
        {
            using (SettingsForm form = new SettingsForm(_config))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    if (_config.AutoCheckUpdates)
                    {
                        if (_autoUpdateCheckAt == DateTime.MinValue)
                            _autoUpdateCheckAt = DateTime.Now.AddSeconds(5);
                    }
                    else
                    {
                        _autoUpdateCheckAt = DateTime.MinValue;
                    }
                    if (!_config.AutoRestart)
                    {
                        _autoRestartAt = DateTime.MinValue;
                        _autoRestartAttempts = 0;
                    }
                    Notify(L.T("settings.savedTitle"), L.T("settings.savedBody"));
                }
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                using (Process explorer = Process.Start(path))
                {
                }
            }
            catch (Exception ex)
            {
                NotifyError(L.T("notify.openFolderFailed"), ex.Message + "\r\n" + path);
            }
        }

        private void OpenConfiguration()
        {
            try
            {
                if (!File.Exists(_config.IniPath)) File.WriteAllText(_config.IniPath, "");
                using (Process notepad = Process.Start("notepad.exe", Shell.Quote(_config.IniPath)))
                {
                }
            }
            catch (Exception ex)
            {
                NotifyError(L.T("notify.openConfigFailed"), ex.Message);
            }
        }

        private void CopyUrl()
        {
            try
            {
                string url = "http://127.0.0.1:" + _port;
                Clipboard.SetText(url);
                Notify(L.T("notify.urlCopiedTitle"), url);
            }
            catch (Exception ex)
            {
                NotifyError(L.T("notify.copyFailed"), ex.Message);
            }
        }

        private void RestartAsAdministrator()
        {
            if (_elevated) return;

            DialogResult answer = MessageBox.Show(
                L.T("notify.adminRestartBody"),
                L.T("notify.adminRestartTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            if (!Sys.RunElevated(Application.ExecutablePath, ""))
                NotifyError(L.T("notify.adminRestartFailed"), L.T("notify.adminRestartFailedBody"));
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
            _balloonAction = null;
            Notifications.Show(_tray, title, body);
        }

        private void Notify(string title, string body, Action onClick)
        {
            _balloonAction = onClick;
            Notifications.Show(_tray, title, body);
        }

        private void NotifyWarning(string title, string body)
        {
            _balloonAction = null;
            Notifications.ShowWarning(_tray, title, body);
        }

        private void NotifyWarning(string title, string body, Action onClick)
        {
            _balloonAction = onClick;
            Notifications.ShowWarning(_tray, title, body);
        }

        private void NotifyError(string title, string body)
        {
            _balloonAction = null;
            Notifications.ShowError(_tray, title, body);
        }

        private void NotifyError(string title, string body, Action onClick)
        {
            _balloonAction = onClick;
            Notifications.ShowError(_tray, title, body);
        }

        // Do not repeat the balloon when the same state is entered again
        private void NotifyWarningOnce(string key, string title, string body)
        {
            if (_lastNotified == key) return;
            _lastNotified = key;
            NotifyWarning(title, body);
        }

        private void NotifyWarningOnce(string key, string title, string body, Action onClick)
        {
            if (_lastNotified == key) return;
            _lastNotified = key;
            NotifyWarning(title, body, onClick);
        }
    }
}
