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
// The partial files (TrayApp.Service.cs, TrayApp.Update.cs, ...) split the state-machine
// sections; this file keeps the fields, constructor and poll loop.

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

    internal sealed partial class TrayApp : ApplicationContext
    {
        private const int StartTimeoutSeconds = 180;
        private const long MaxLogBytes = 4 * 1024 * 1024;

        // A service must stay up this long before its restart counter is reset; shorter
        // runs are treated as the same crash loop (see ScheduleAutoRestart).
        private const int StableRunSeconds = 60;

        private readonly Config _config;
        private readonly bool _elevated;
        private readonly bool _autoStartService;
        private readonly bool _initialOpenBrowser;
        private readonly Mutex _mutex;
        private int _port;

        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly EventWaitHandle _openSignal;
        private readonly EventWaitHandle _startSignal;
        private readonly EventWaitHandle _stopSignal;
        private readonly EventWaitHandle _quitSignal;
        private readonly EventWaitHandle _quitKeepServiceSignal;

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
        private bool _firstCheck = true;
        private int _firstCheckRetries;
        private Color _iconColor = Color.Empty;
        private Icon _currentIcon;
        private string _lastNotified = "";

        // Balloons are serialized so a click always belongs to the balloon that is
        // currently visible. NotifyIcon has no per-balloon callback identity, and a
        // single action field is overwritten by every later notification.
        private sealed class TrayNotification
        {
            internal string Title;
            internal string Body;
            internal ToolTipIcon Icon;
            internal int Timeout;
            internal Action OnClick;
        }

        private readonly Queue<TrayNotification> _notifications = new Queue<TrayNotification>();
        private TrayNotification _currentNotification;
        private DateTime _currentNotificationAt;

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
        private bool _checkSuppressAutoInstall;
        private bool _checkPendingSuppressAutoInstall;
        private bool _installAfterCheck;
        private bool _installAfterCheckForce;
        private bool _installAfterCheckStart;
        private bool _installAfterCheckOpen;
        private bool _installAfterStop;
        private bool _installAfterStopForce;
        private bool _installRestartService;   // a running service was stopped and must come back
        private bool _installStartAfter;       // start the service after a successful install
        private bool _installOpenAfter;        // open the browser once that service is ready

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
        private bool _serviceStoppedByUser;
        private bool _exiting;
        // A timer tick can be delivered while OnTick is showing a modal dialog
        // (MessageBox or the missing-Node prompt). Never run a second state-machine
        // pass inside that nested message loop.
        private bool _tickRunning;
        // Depth of application-modal dialogs. Automatic environment/install/update
        // actions are paused while a dialog is open; explicit stop/start signals and
        // the service phase polls still run.
        private int _modalDepth;
        private bool _cleaned;
        private DateTime _startDeadline;
        private DateTime _stopDeadline;
        private DateTime _lastSignalHandled = DateTime.MinValue;
        private DateTime _stopEscalatedAt = DateTime.MinValue;
        private volatile bool _stopWorkerRunning;
        private DateTime _autoRestartAt = DateTime.MinValue;
        private int _autoRestartAttempts;
        private DateTime _runningSince = DateTime.MinValue;
        private DateTime _lastOwnerCheck = DateTime.MinValue;
        private DateTime _lastNodeReprobeAt = DateTime.MinValue;
        // A present Node signature changed while the service was running. Do not
        // put the live service into the Checking phase; re-probe before the next
        // start instead.
        private bool _nodeProbeStale;

        // The report window performs its own background refresh; this flag only
        // prevents opening a second report or re-entering the menu action.
        private bool _selfCheckRunning;
        // The first-run missing-Node prompt is modal; auto actions must not run
        // behind it. The flag is cleared after ShowDialog returns.
        private bool _nodePromptOpen;

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
        private DateTime _updateBackupCleanupAt = DateTime.MinValue;
        private string _updateStaging;
        private string _updateError;
        private UpdateCancellation _updateCancellation;
        private bool _restartAfterUpdate;
        private bool _updateStartedWithServiceRunning;
        private volatile int _updatePercent = -1;
        private int _updateTicks;
        private int _portPollTicks;

        // Node.js presence watcher (background thread -> UI thread). The worker only reads
        // local files and the registry PATH; the UI thread consumes a single flag and never
        // performs the fingerprint itself.
        private int _nodeWatchGeneration;
        private bool _nodeWatching;
        private bool _nodePresenceNotified;
        private bool _nodeMissingNotified;
        private int _nodePresenceChanged;
        private readonly NodePresenceState _nodePresenceState = new NodePresenceState();
        private volatile bool _nodePresent;
        private volatile bool _nodePresenceKnown;

        private readonly object _taskCacheLock = new object();
        private bool _taskCache;
        private DateTime _taskCacheAt = DateTime.MinValue;
        private bool _taskRefreshRunning;
        private bool _taskRefreshDone;

        // port ownership probe (background -> UI thread): ServiceProbe.Inspect can block
        // for seconds (a netstat child process plus an HTTP fallback that fires exactly
        // when a privileged or foreign process holds the port). The UI thread only
        // requests checks and consumes finished results; it never waits for one.
        private readonly object _ownerLock = new object();
        private bool _ownerCheckRunning;
        private bool _ownerCheckDone;
        private PortOwnerInfo _ownerCheckResult;
        private int _ownerCheckGeneration;

        // Actions that wait for an owner-check result; recorded on the UI thread and
        // delivered by DispatchOwnerCheck once the background check has finished.
        private bool _stopAfterOwnerCheck;
        private bool _startAfterOwnerCheck;
        private bool _startAfterOwnerCheckOpen;
        private bool _resolveAfterOwnerCheck;
        private bool _installAfterOwnerCheck;
        private bool _installAfterOwnerCheckForce;
        private bool _installAfterOwnerCheckStart;
        private bool _installAfterOwnerCheckOpen;

        // log-host availability: probed once in the background together with the
        // environment check, because the 3 s self-probe must not run on the UI thread
        // on every service start
        private volatile int _logHostState = -1;   // -1 unknown, 0 unavailable, 1 available
        private int _checkLogHostState = -1;       // handed over by the check worker

        // readiness: "port accepts connections" is not the same as "the app answers HTTP"
        private readonly object _readyLock = new object();
        private int _readyResult;
        private bool _readyCheckRunning;
        // Incremented whenever a new service start supersedes an in-flight
        // HTTP readiness probe. A stale worker must not mark the new service
        // ready before it answers HTTP.
        private int _readyGeneration;

        internal TrayApp(Config config, bool elevated, Mutex mutex, bool startServer,
            bool openBrowserOnStart)
        {
            _config = config;
            _elevated = elevated;
            _autoStartService = startServer;
            _initialOpenBrowser = openBrowserOnStart;
            _mutex = mutex;
            _port = config.Port;

            bool created;
            _openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.OpenSignalName, out created);
            _startSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.StartSignalName, out created);
            _stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.StopSignalName, out created);
            _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.QuitSignalName, out created);
            _quitKeepServiceSignal = new EventWaitHandle(false, EventResetMode.AutoReset,
                Instance.QuitKeepServiceSignalName, out created);

            _statusItem = new ToolStripMenuItem(L.T("status.checkingShort"));
            _statusItem.Enabled = false;

            _openItem = new ToolStripMenuItem(L.T("menu.open"));
            _openItem.Font = new Font(_menu.Font, FontStyle.Bold);
            _openItem.Click += delegate { OpenOrStart(); };

            _startItem = new ToolStripMenuItem(L.T("menu.start"));
            _startItem.Click += delegate { StartServer(false); };

            _stopItem = new ToolStripMenuItem(L.T("menu.stop"));
            _stopItem.Click += delegate { StopServerFromUser(); };

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
            _nodeItem.Click += delegate { OpenNodeDownload(); };

            _checkItem = new ToolStripMenuItem(L.T("menu.selfCheck"));
            _checkItem.Click += delegate { RunSelfCheck(); };

            _settingsItem = new ToolStripMenuItem(L.T("menu.settings"));
            _settingsItem.Click += delegate { ShowSettings(); };

            ToolStripMenuItem logItem = new ToolStripMenuItem(L.T("menu.serverLog"));
            logItem.Click += delegate { ShowLog(_config.LogFile, L.T("log.serverTitle")); };

            _installLogItem = new ToolStripMenuItem(L.T("menu.installLog"));
            _installLogItem.Click += delegate { ShowLog(_config.InstallLogFile, L.T("log.installTitle")); };

            _updateLogItem = new ToolStripMenuItem(L.T("menu.updateLog"));
            _updateLogItem.Click += delegate { ShowLog(_config.UpdateLogFile, L.T("log.updateTitle")); };

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
            // Disable immediately in an elevated process; do not wait for the first
            // environment-check UpdateUi(), because the menu may be opened before it.
            _restartAdminItem.Enabled = !_elevated;
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
            _menu.Items.Add(_updateItem);
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
            _tray.Text = Program.AppName;
            _tray.MouseClick += delegate (object sender, MouseEventArgs e)
            {
                // A double-click raises two MouseClick events (1 then 2).
                // Opening the browser twice is surprising; only act on the first
                // click and leave the second event alone.
                if (e.Button == MouseButtons.Left && e.Clicks == 1) OpenOrStart();
            };
            _tray.BalloonTipClicked += delegate
            {
                TrayNotification current = _currentNotification;
                if (current == null) return;
                Action action = current.OnClick;
                CompleteNotification();
                if (action != null)
                {
                    try { action(); } catch (Exception) { }
                }
            };

            RefreshAutostartItems();
            if (!_elevated) Autostart.RepairRunValue(Application.ExecutablePath);

            if (_config.WorkingDirFallback)
            {
                try
                {
                    Updater.AppendLog(_config.UpdateLogFile,
                        "working directory not found: " + _config.ConfiguredWorkingDir
                        + "; using " + _config.WorkingDir);
                }
                catch (Exception) { }
                NotifyWarningOnce("config-workingdir",
                    L.T("notify.workingDirFallbackTitle"),
                    L.T("notify.workingDirFallbackBody",
                        _config.ConfiguredWorkingDir, _config.WorkingDir));
            }
            if (_config.NodePathInvalid)
            {
                try
                {
                    Updater.AppendLog(_config.UpdateLogFile,
                        "configured Node.js path not found: " + _config.NodePath);
                }
                catch (Exception) { }
                NotifyWarningOnce("config-nodepath",
                    L.T("notify.nodePathInvalidTitle"),
                    L.T("notify.nodePathInvalidBody", _config.NodePath));
            }

            _timer.Interval = 500;
            _timer.Tick += OnTick;
            _timer.Start();

            // Keep the lightweight fingerprint watcher for the whole process lifetime.
            // It is used both to resume after Node.js is installed and to warn if the
            // executable disappears while Pi Web is still running.
            StartNodePresenceMonitor();

            BeginEnvironmentCheck(_autoStartService, _initialOpenBrowser);
            if (_config.AutoCheckUpdates) _autoUpdateCheckAt = DateTime.Now.AddSeconds(10);

            // Write the state file only after the tray, menu and timer are initialized.
            // The update helper uses this file as its first sign that the new process
            // reached a usable state. It is not a full health check, so the helper also
            // waits for a short stability window before deleting its backup.
            TrayState.Save(Process.GetCurrentProcess().Id, elevated, _port);
            _updateBackupCleanupAt = DateTime.Now.AddSeconds(90);
        }

        // ---------------- poll loop ----------------

        private void BeginModal()
        {
            _modalDepth++;
        }

        private void EndModal()
        {
            if (_modalDepth > 0) _modalDepth--;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_exiting || _tickRunning) return;
            _tickRunning = true;
            try
            {
                OnTickCore(sender, e);
            }
            finally
            {
                _tickRunning = false;
            }
        }

        private void OnTickCore(object sender, EventArgs e)
        {
            if (_exiting) return;

            bool openSignal = false;
            bool startSignal = false;
            bool stopSignal = false;
            bool quitSignal = false;
            bool quitKeepServiceSignal = false;
            try { openSignal = _openSignal.WaitOne(0); } catch (Exception) { }
            try { startSignal = _startSignal.WaitOne(0); } catch (Exception) { }
            try { stopSignal = _stopSignal.WaitOne(0); } catch (Exception) { }
            try { quitSignal = _quitSignal.WaitOne(0); } catch (Exception) { }
            try { quitKeepServiceSignal = _quitKeepServiceSignal.WaitOne(0); } catch (Exception) { }

            // Elevated takeover with --tray-only must leave a running service alone.
            if (quitKeepServiceSignal)
            {
                ExitWithoutStoppingService(false);
                return;
            }
            if (quitSignal)
            {
                ExitApp();
                return;
            }
            if (stopSignal) StopServerFromUser();
            if (startSignal) StartServer(false);

            PollAutostartOp();
            DispatchOwnerCheck();
            if (_modalDepth == 0)
            {
                PollEnvironmentCheck();
                PollInstall();
                PollUpdate();
                PollNodePresence();
            }
            PollTaskRefresh();
            if (_exiting) return;

            if (_updateBackupCleanupAt != DateTime.MinValue && DateTime.Now >= _updateBackupCleanupAt
                && !_exiting)
            {
                _updateBackupCleanupAt = DateTime.MinValue;
                CleanupStaleUpdateBackups();
            }

            if (_autoUpdateCheckAt != DateTime.MinValue && DateTime.Now >= _autoUpdateCheckAt
                && !UpdateBusy() && !_exiting && !_nodePromptOpen && _modalDepth == 0
                && _phase != Phase.Installing && _phase != Phase.Checking
                && _phase != Phase.Starting && _phase != Phase.Stopping)
            {
                _autoUpdateCheckAt = DateTime.MinValue;
                BeginUpdateCheck(true);
            }

            if (_config.AutoRestart && _autoRestartAt != DateTime.MinValue
                && DateTime.Now >= _autoRestartAt
                && !UpdateBusy() && !_exiting && _modalDepth == 0
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

            PollNotifications();
        }
    }
}
