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
        private UpdateCancellation _updateCancellation;
        private bool _restartAfterUpdate;
        private bool _updateStartedWithServiceRunning;
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
    }
}
