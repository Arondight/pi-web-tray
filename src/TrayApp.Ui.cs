// Pi Web Tray — menu actions and notifications
//
// Partial of TrayApp. Kept separate so the tray state machine stays readable.

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
    internal sealed partial class TrayApp
    {
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
                case Phase.RunningUnverified:
                    label = L.T("status.unverified", _port);
                    color = Icons.BusyColor;
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

            if (_phase == Phase.Running && _nodePresenceKnown && !_nodePresent)
                label += L.T("status.nodeMissing");

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

                // Checking and downloading do not stop Pi Web. Keep the phase colour
                // (green while running) and put update progress in the tooltip/menu.
                // Only the apply stage stops and restarts the tray.
                if (_updateStage == UpdateStage.Applying) color = Icons.BusyColor;
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

            bool phaseRunning = _phase == Phase.Running || _phase == Phase.RunningUnverified;
            string statusText = (phaseRunning ? L.T("status.bulletOn") : L.T("status.bulletOff"))
                + label + (_elevated ? L.T("status.adminLong") : "");
            // The status line can contain the last npm output line. Keep the menu
            // readable for long localized strings and expose the full text as a tooltip.
            _statusItem.ToolTipText = statusText;
            // ToolStrip text treats '&' as a mnemonic marker. npm output and paths
            // can contain it; double it so the status remains verbatim.
            _statusItem.Text = Shell.Clip(statusText, 100).Replace("&", "&&");

            bool applying = _updateStage == UpdateStage.Applying;
            bool serviceBusy = _phase == Phase.Installing || _phase == Phase.Checking
                || _phase == Phase.Starting || _phase == Phase.Stopping;
            bool busy = serviceBusy || updateBusy;
            bool running = _phase == Phase.Running || _phase == Phase.RunningUnverified;
            bool portConflict = _phase == Phase.PortConflict;
            bool ready = _probe != null && _probe.Ready;
            // Pi and npm are install-time requirements; a runnable Pi Web can be
            // started without them. Pi installation itself can be disabled in
            // Settings. Keep the menu consistent with CanStartServiceFromProbe and
            // with the thenStart path in ApplyCheckResult.
            bool canRun = CanStartServiceFromProbe(_probe, File.Exists);
            bool needsSetup = _phase == Phase.NeedsNode || _phase == Phase.NeedsDeps;
            bool trackedAlive = TrackedServerAlive();
            bool trackedStopAvailable = trackedAlive
                && (_phase == Phase.Stopped || _phase == Phase.Failed
                    || _phase == Phase.NeedsNode || _phase == Phase.NeedsDeps);

            _openItem.Text = running ? L.T("menu.open") : L.T("menu.startAndOpen");
            // Checking or downloading an update does not affect the running service.
            // Keep normal open/stop/restart actions available; only Applying is exclusive.
            _openItem.Enabled = !serviceBusy && !portConflict && !applying;
            _startItem.Enabled = !serviceBusy && !running && canRun && !portConflict && !applying;
            _stopItem.Enabled = (running || _phase == Phase.Starting || trackedStopAvailable)
                && !applying;
            _restartItem.Enabled = !serviceBusy && running && !applying;
            _installItem.Enabled = !busy;
            // When the service is installed the action updates/reinstalls it; when
            // it is missing the same action also has to install it. Keep the two
            // labels accurate instead of showing "update" for a clean install.
            _installItem.Text = ready
                ? L.T("menu.installAuto")
                : L.T("menu.installDeps");
            _updateItem.Enabled = !busy;
            _updateItem.Text = updateBusy
                ? (_updateStage == UpdateStage.Checking
                    ? L.T("update.status.checking")
                    : L.T("menu.checkUpdatesBusy"))
                : (_availableRelease != null
                    ? L.T("menu.updateAvailable", _availableRelease.Version)
                    : L.T("menu.checkUpdates"));

            if (_updateForm != null && updateBusy)
            {
                int formPercent = _updateStage == UpdateStage.Checking ? 0
                    : _updateStage == UpdateStage.Downloading ? _updatePercent : 100;
                bool canCancel = _updateStage == UpdateStage.Checking || _updateStage == UpdateStage.Downloading;
                _updateForm.SetState(label, formPercent, canCancel);
            }
            // The Node.js item is only a link to the official download page; unlike
            // service/update actions it is safe to use at any time.
            _nodeItem.Enabled = true;
            _checkItem.Enabled = !busy && !_selfCheckRunning;
            _settingsItem.Enabled = !busy;
            _installLogItem.Enabled = File.Exists(_config.InstallLogFile);
            _updateLogItem.Enabled = File.Exists(_config.UpdateLogFile);
            _copyUrlItem.Enabled = running;
            _restartAdminItem.Enabled = !_elevated && !busy;
            // Applying replaces this executable and starts a helper that cannot be
            // cancelled safely. Exiting at that point would leave the update and the
            // service in an unpredictable state, so keep the action unavailable until
            // the updater itself calls ExitApp.
            _exitItem.Enabled = !applying;

            if (needsSetup) _openItem.Text = L.T("menu.setupFirst");
            if (portConflict) _openItem.Text = L.T("menu.portConflict");

            RefreshNodeHealth();
        }

        private bool TrackedServerAlive()
        {
            try { return _server != null && !_server.HasExited; }
            catch (Exception) { return false; }
        }

        private void OpenOrStart()
        {
            if (_exiting) return;
            // Applying replaces the executable and restarts the tray; no service action
            // should begin during that short window. Checking/downloading is not exclusive.
            if (_updateStage == UpdateStage.Applying) return;
            if (UpdateBusy() && (_phase == Phase.NeedsDeps || _phase == Phase.PortConflict))
            {
                NotifyWarning(L.T("notify.updateBusyTitle"), L.T("notify.updateBusyBody"));
                return;
            }
            if (_phase == Phase.PortConflict)
            {
                BeginModal();
                try
                {
                    MessageBox.Show(
                        L.T("notify.portConflictBody", _port),
                        L.T("notify.portConflictTitle"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                finally
                {
                    EndModal();
                }
                ShowSettings();
                return;
            }
            if (_phase == Phase.Running)
            {
                // This click is an explicit open request. Do not leave an older
                // "user stopped" marker behind for the next update decision.
                _serviceStoppedByUser = false;
                OpenBrowser();
                return;
            }
            if (_phase == Phase.RunningUnverified)
            {
                // The port is open but the owner could not be verified. Opening the
                // local URL is still useful; do not start a second service or claim
                // that the listener is ours.
                _serviceStoppedByUser = false;
                NotifyWarning(L.T("notify.ownerCheckFailedTitle"),
                    L.T("notify.ownerCheckFailedBody", _port));
                OpenBrowser();
                return;
            }
            if (_phase == Phase.Stopped && _probe != null && _probe.NeedsPiWeb)
            {
                // The service may have been running when a self-check found the
                // package missing. Once it stops, a click on Open should repair the
                // dependency instead of only showing a warning.
                if (UpdateBusy())
                {
                    NotifyWarning(L.T("notify.updateBusyTitle"), L.T("notify.updateBusyBody"));
                    return;
                }
                // Explicit start/open consent for the install that follows.
                _serviceStoppedByUser = false;
                BeginInstall(false, true, true);
                return;
            }
            if (_phase == Phase.Installing) return;
            if (_phase == Phase.Checking)
            {
                // A click while the first probe is running should not be lost. Queue
                // an explicit start/open request behind the in-flight check instead.
                _serviceStoppedByUser = false;
                BeginEnvironmentCheck(true, true);
                return;
            }
            if (_phase == Phase.Starting)
            {
                // A silent start (autostart or post-update) may not have asked for a
                // browser. A click during Starting is an explicit request to open it
                // once the service is ready.
                _serviceStoppedByUser = false;
                _openWhenReady = true;
                return;
            }
            if (_phase == Phase.Stopping)
            {
                NotifyWarning(L.T("notify.stoppingTitle"), L.T("notify.stoppingBody"));
                return;
            }
            if (_phase == Phase.NeedsNode)
            {
                // The background watcher normally continues after Node.js is installed;
                // clicking the tray is also an explicit retry if that was missed.
                _serviceStoppedByUser = false;
                BeginEnvironmentCheck(true, true);
                return;
            }
            if (_phase == Phase.Failed)
            {
                // Re-probe first: the failure may have been a start timeout, not a broken
                // installation. ApplyCheckResult decides whether to retry install or start.
                _serviceStoppedByUser = false;
                BeginEnvironmentCheck(true, true);
                return;
            }
            if (_phase == Phase.NeedsDeps)
            {
                // The user clicked the icon, which counts as consent to install or repair
                _serviceStoppedByUser = false;
                if (_probe == null || _probe.NeedsNode) BeginEnvironmentCheck(true, true);
                else BeginInstall(false, true, true);
                return;
            }
            _serviceStoppedByUser = false;
            StartServer(true);
        }

        private void OpenBrowser()
        {
            try
            {
                Sys.OpenExternal("http://127.0.0.1:" + _port);
            }
            catch (Exception ex) { NotifyError(L.T("notify.noBrowser"), ex.Message); }
        }

        private void OpenNodeDownload()
        {
            Version version = WindowsVersion.Current;
            if (!NodeDownloads.UsesCommunityBuild(version))
            {
                OpenUrl(NodeDownloads.UrlFor(version));
                return;
            }

            using (NodePromptForm prompt = new NodePromptForm(
                L.T("node.communityTitle"), L.T("node.communityBody"),
                true, false, L.T("node.cancel")))
            {
                DialogResult answer;
                BeginModal();
                try { answer = prompt.ShowDialog(); }
                finally { EndModal(); }
                if (answer == DialogResult.Yes)
                    OpenUrl(NodeDownloads.UrlFor(version));
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                Sys.OpenExternal(url);
            }
            catch (Exception ex) { NotifyError(L.T("notify.noLink"), ex.Message + "\r\n" + url); }
        }

        private void ShowLog(string path, string title)
        {
            try
            {
                using (LogForm form = new LogForm(path, title))
                {
                    BeginModal();
                    try { form.ShowDialog(); }
                    finally { EndModal(); }
                }
            }
            catch (Exception ex)
            {
                NotifyError(L.T("notify.openLogFailed"), ex.Message);
            }
        }

        private void ShowAbout()
        {
            using (AboutForm form = new AboutForm())
            {
                BeginModal();
                try { form.ShowDialog(); }
                finally { EndModal(); }
            }
        }

        // A foreign listener on the old port must not prevent the user from choosing
        // a free port. Only a service phase that may still manage the old listener (or
        // a tracked wrapper that actually owns it) blocks the change; PortConflict is
        // explicitly allowed because changing the port is the resolution path.
        internal static bool CanApplyPortChange(Phase phase, bool oldPortListening,
            bool trackedServiceAlive)
        {
            if (phase == Phase.Running || phase == Phase.RunningUnverified
                || phase == Phase.Starting || phase == Phase.Stopping
                || phase == Phase.Installing || phase == Phase.Checking)
            {
                return false;
            }
            bool trackedServiceOwnsPort = oldPortListening && trackedServiceAlive;
            return !trackedServiceOwnsPort || phase == Phase.PortConflict;
        }

        private void ShowSettings()
        {
            if (_exiting) return;
            int previousPort = _port;
            bool installPiBefore = _config.InstallPi;
            bool restartRequested = false;
            // A running/starting/stopping service owns the old port. Changing the
            // configured port without restarting the whole tray would make CLI
            // commands and the service state disagree, so require a stopped service.
            bool allowPortChange = _phase != Phase.Running
                && _phase != Phase.RunningUnverified && _phase != Phase.Starting
                && _phase != Phase.Stopping && _phase != Phase.Installing
                && _phase != Phase.Checking;
            using (SettingsForm form = new SettingsForm(_config, allowPortChange))
            {
                DialogResult result;
                BeginModal();
                try
                {
                    result = form.ShowDialog();
                }
                finally
                {
                    EndModal();
                }
                if (result == DialogResult.OK)
                {
                    restartRequested = form.RestartRequested;
                    bool portRejected = false;
                    if (_config.Port != previousPort)
                    {
                        // The state can change while the modal dialog is open (for example
                        // an auto-restart or a start signal). Never adopt a new port while
                        // the service this tray started may still own the old one. A foreign
                        // program on the old port must not block the configuration change:
                        // changing the port is exactly how the user resolves a conflict.
                        bool oldPortListening = Net.IsPortOpen(previousPort, 200);
                        bool canApplyPort = CanApplyPortChange(_phase, oldPortListening,
                            _server != null && !_server.HasExited);
                        if (!canApplyPort)
                        {
                            _config.Port = previousPort;
                            _config.ConfiguredPort = previousPort;
                            if (!_config.SetValue("Port", previousPort.ToString()))
                                NotifyError(L.T("settings.title"), L.T("settings.saveFailed"));
                            portRejected = true;
                            BeginModal();
                            try
                            {
                                MessageBox.Show(L.T("settings.portRequiresStopped"),
                                    L.T("settings.title"),
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);
                            }
                            finally
                            {
                                EndModal();
                            }
                        }
                        else
                        {
                            // No usable service owns the old port. If a tracked
                            // wrapper survived a failed start, release it before the
                            // tray starts using a different port.
                            KillTrackedServer();
                            _port = _config.Port;
                            // A probe for the old port may still be in flight. Drop it
                            // and every action that was waiting for it; otherwise a
                            // stale result could make the new port look running or
                            // conflicted.
                            ClearPendingOwnerActions(false);
                            _stopAfterOwnerCheck = false;
                            InvalidateOwnerCheck();
                            ServiceProbe.Invalidate();
                            _lastOwnerCheck = DateTime.MinValue;
                            _lastNotified = "";
                            if (_phase == Phase.PortConflict) _phase = Phase.Stopped;
                            TrayState.Save(Process.GetCurrentProcess().Id, _elevated, _port);
                            UpdateUi();
                        }
                    }
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
                    if (restartRequested)
                    {
                        RestartTray();
                        return;
                    }
                    if (!portRejected)
                    {
                        // Turning Pi installation on/off changes readiness without
                        // changing the installed packages. Refresh the cached probe so
                        // the tray icon and menu stop/start offering the wrong action.
                        if (_config.InstallPi != installPiBefore && !IsServiceActionInProgress())
                        {
                            if (_probe == null)
                            {
                                BeginEnvironmentCheck(false, false);
                            }
                            else
                            {
                                _probe.PiCliRequired = _config.InstallPi;
                                ApplyProbeResult(_probe);
                            }
                        }
                        Notify(L.T("settings.savedTitle"), L.T("settings.savedBody"));
                    }
                }
            }
        }

        // Save and restart asks ExitApp to start the replacement after any
        // update/install confirmation has succeeded.
        private void RestartTray()
        {
            // The update helper is already replacing this executable. Starting a
            // second tray now would launch the old binary while files are changing.
            if (_updateStage == UpdateStage.Applying)
            {
                Notify(L.T("notify.restartCanceled"), L.T("notify.restartBusyBody"));
                return;
            }
            if (ElevatedInstallUnsafe())
            {
                NotifyWarning(L.T("notify.elevatedWritableTitle"),
                    L.T("notify.elevatedWritableBody"));
                return;
            }
            ExitApp(true);
        }

        // A settings restart must preserve the current service state. In particular,
        // an intentionally stopped service must not start again just because
        // AutoStart is enabled. Running: start the replacement service silently.
        // Stopped: keep the replacement tray-only.
        internal static string RestartArguments(bool serviceRunning)
        {
            return "--restart " + (serviceRunning ? "--start-no-open" : "--tray-only");
        }

        // The phase can temporarily be Checking/NeedsDeps/NeedsNode while a service
        // started by this tray is still alive. Preserve that service on restart too.
        private bool ServiceRunningForRestart()
        {
            if (_phase == Phase.Running || _phase == Phase.RunningUnverified) return true;
            try { if (_server != null && !_server.HasExited) return true; }
            catch (Exception) { }

            // A tray started while Pi Web is already listening can still be in the
            // asynchronous Checking/Starting phase with no _server handle. Do not
            // choose --tray-only for this case: ExitApp will stop the existing
            // listener, and the replacement would then leave the service down.
            if (_phase == Phase.Checking || _phase == Phase.Starting)
            {
                try { return Net.IsPortOpen(_port, 200); }
                catch (Exception) { }
            }
            return false;
        }

        private bool StartRestartProcess()
        {
            string arguments = RestartArguments(ServiceRunningForRestart());

            if (_replaceWithElevated)
            {
                if (!Sys.RunElevated(Application.ExecutablePath, arguments))
                {
                    NotifyError(L.T("notify.adminRestartFailed"),
                        L.T("notify.adminRestartFailedBody"));
                    return false;
                }
                return true;
            }

            try
            {
                using (Process child = Sys.StartHidden(Application.ExecutablePath,
                    arguments, Application.StartupPath))
                {
                    if (child == null)
                    {
                        NotifyError(L.T("notify.restartFailed"), Application.ExecutablePath);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                NotifyError(L.T("notify.restartFailed"), ex.Message);
                return false;
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Sys.OpenExternal(path);
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
                if (PathSafety.ContainsReparsePoint(_config.IniPath))
                {
                    NotifyError(L.T("notify.openConfigFailed"), L.T("update.error.unsafePath"));
                    return;
                }
                if (!File.Exists(_config.IniPath)) File.WriteAllText(_config.IniPath, "");
                // Resolve from the system directory too; the tray may be elevated and
                // must not pick up a notepad.exe from the current directory or PATH.
                using (Process notepad = Process.Start(Sys.SystemExecutable("notepad.exe"),
                    Shell.Quote(_config.IniPath)))
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
            // The update helper is already replacing this executable. A second
            // replacement would race the helper and could leave two trays fighting
            // over the mutex and port; the settings restart has the same guard.
            if (_updateStage == UpdateStage.Applying)
            {
                Notify(L.T("notify.restartCanceled"), L.T("notify.restartBusyBody"));
                return;
            }
            // Running the whole tray elevated from a user-writable directory would
            // let a same-user process replace the executable or a loaded resource. Also
            // refuse over-the-shoulder elevation from a standard-user account.
            if (!Autostart.IsAdministratorAccount())
            {
                NotifyWarning(L.T("notify.adminRestartTitle"), L.T("notify.adminAccountBody"));
                return;
            }
            if (Autostart.IsUserWritableInstall(Application.ExecutablePath))
            {
                NotifyWarning(L.T("notify.adminRestartTitle"), L.T("notify.adminRestartWritableBody"));
                return;
            }

            DialogResult answer;
            BeginModal();
            try
            {
                answer = MessageBox.Show(
                    L.T("notify.adminRestartBody"),
                    L.T("notify.adminRestartTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
            }
            finally
            {
                EndModal();
            }
            if (answer != DialogResult.Yes) return;

            // Start the elevated child only after ExitApp's update/install prompts
            // have been accepted. The child uses --restart and waits for this tray
            // to release the mutex, so it cannot race the old service still closing.
            _replaceWithElevated = true;
            if (!ExitApp(true))
            {
                _replaceWithElevated = false;
            }
        }

        private void CreateDesktopShortcut()
        {
            if (_elevated)
            {
                // WScript.Shell is resolved through COM; do not load a user-controlled
                // COM registration with the tray's elevated token.
                NotifyWarning(L.T("notify.shortcutAdminTitle"),
                    L.T("notify.shortcutAdminBody"));
                return;
            }

            object shell = null;
            object shortcut = null;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                // Match the product name shown in the executable and About window.
                string path = Path.Combine(desktop, Program.AppName + ".lnk");

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    NotifyError(L.T("notify.createFailed"), L.T("notify.noShell"));
                    return;
                }
                shell = Activator.CreateInstance(shellType);

                // Do not silently overwrite an unrelated shortcut that happens to
                // have our product name. Only replace a link that points at this
                // executable, or after an explicit confirmation.
                if (File.Exists(path)
                    && !ShortcutTargetsExecutable(shellType, shell, path))
                {
                    DialogResult answer;
                    BeginModal();
                    try
                    {
                        answer = MessageBox.Show(
                            L.T("notify.shortcutExistsBody", path),
                            L.T("notify.shortcutExistsTitle"),
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button2);
                    }
                    finally
                    {
                        EndModal();
                    }
                    if (answer != DialogResult.Yes) return;
                }

                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                if (shortcut == null)
                    throw new InvalidOperationException(L.T("notify.shortcutCreateFailed"));

                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
                shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { _config.BaseDir });
                shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath + ",0" });
                shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Program.AppName });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

                // Remove the shortcut created by older releases, but only when it still
                // points at this executable; other shortcuts on the desktop are not ours.
                RemoveOwnedShortcut(shellType, shell, Path.Combine(desktop, "Pi Web.lnk"));

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

        private static bool ShortcutTargetsExecutable(Type shellType, object shell, string path)
        {
            object link = null;
            try
            {
                link = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                if (link == null) return false;
                object target = link.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, link, null);
                return string.Equals(Convert.ToString(target),
                    Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                ReleaseComObject(link);
            }
        }

        private static void RemoveOwnedShortcut(Type shellType, object shell, string path)
        {
            object legacy = null;
            try
            {
                if (!File.Exists(path)) return;
                legacy = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                if (legacy == null) return;

                object target = legacy.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, legacy, null);
                if (!string.Equals(Convert.ToString(target),
                        Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }
            finally
            {
                ReleaseComObject(legacy);
            }

            try { File.Delete(path); } catch (Exception) { }
        }

        private static void ReleaseComObject(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.ReleaseComObject(value); } catch (Exception) { }
        }

        private void Notify(string title, string body)
        {
            EnqueueNotification(title, body, ToolTipIcon.Info, 5000, null);
        }

        private void Notify(string title, string body, Action onClick)
        {
            EnqueueNotification(title, body, ToolTipIcon.Info, 5000, onClick);
        }

        private void NotifyWarning(string title, string body)
        {
            EnqueueNotification(title, body, ToolTipIcon.Warning, 8000, null);
        }

        private void NotifyWarning(string title, string body, Action onClick)
        {
            EnqueueNotification(title, body, ToolTipIcon.Warning, 8000, onClick);
        }

        private void NotifyError(string title, string body)
        {
            EnqueueNotification(title, body, ToolTipIcon.Error, 8000, null);
        }

        private void NotifyError(string title, string body, Action onClick)
        {
            EnqueueNotification(title, body, ToolTipIcon.Error, 8000, onClick);
        }

        private void EnqueueNotification(string title, string body, ToolTipIcon icon,
            int timeout, Action onClick)
        {
            if (_exiting) return;
            TrayNotification notification = new TrayNotification();
            notification.Title = title;
            notification.Body = body;
            notification.Icon = icon;
            notification.Timeout = timeout;
            notification.OnClick = onClick;
            _notifications.Enqueue(notification);
            ShowNextNotification();
        }

        private void ShowNextNotification()
        {
            if (_currentNotification != null || _exiting) return;
            while (_notifications.Count > 0)
            {
                TrayNotification notification = _notifications.Dequeue();
                if (!Notifications.Show(_tray, notification.Title, notification.Body,
                        notification.Icon, notification.Timeout))
                {
                    continue;
                }
                _currentNotification = notification;
                _currentNotificationAt = DateTime.Now;
                return;
            }
        }

        private void CompleteNotification()
        {
            _currentNotification = null;
            ShowNextNotification();
        }

        // WinForms does not always raise BalloonTipClosed on every Windows release.
        // Give the shell time to hide the balloon, then advance the queue.
        private void PollNotifications()
        {
            if (_currentNotification == null) return;
            int timeout = _currentNotification.Timeout + 5000;
            if (DateTime.Now - _currentNotificationAt >
                TimeSpan.FromMilliseconds(timeout))
            {
                CompleteNotification();
            }
        }

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
