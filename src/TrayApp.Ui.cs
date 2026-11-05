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

            _statusItem.Text = (_phase == Phase.Running ? L.T("status.bulletOn") : L.T("status.bulletOff")) + label + (_elevated ? L.T("status.adminLong") : "");

            bool busy = _phase == Phase.Installing || _phase == Phase.Checking || _phase == Phase.Starting || _phase == Phase.Stopping || updateBusy;
            bool running = _phase == Phase.Running;
            bool portConflict = _phase == Phase.PortConflict;
            bool ready = _probe != null && _probe.Ready;
            bool needsSetup = _phase == Phase.NeedsNode || _phase == Phase.NeedsDeps;

            _openItem.Text = running ? L.T("menu.open") : L.T("menu.startAndOpen");
            _openItem.Enabled = !busy && !portConflict;
            _startItem.Enabled = !busy && !running && ready && !portConflict;
            _stopItem.Enabled = running && !updateBusy;
            _restartItem.Enabled = !busy && running;
            _installItem.Enabled = !busy;
            _installItem.Text = ready
                ? (_config.InstallPiCli ? L.T("menu.installDeps") : L.T("menu.installDepsPiOnly"))
                : L.T("menu.installAuto");
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
            // The Node.js item is only a link to the official download page; unlike
            // service/update actions it is safe to use at any time.
            _nodeItem.Enabled = true;
            _checkItem.Enabled = !busy;
            _settingsItem.Enabled = !busy;
            _installLogItem.Enabled = File.Exists(_config.InstallLogFile);
            _updateLogItem.Enabled = File.Exists(_config.UpdateLogFile);
            _copyUrlItem.Enabled = running;
            _restartAdminItem.Enabled = !_elevated && !busy;

            if (needsSetup) _openItem.Text = L.T("menu.setupFirst");
            if (portConflict) _openItem.Text = L.T("menu.portConflict");
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
                else BeginInstall(false, true, true);
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

        private void OpenNodeDownload()
        {
            Version version = WindowsVersion.Current;
            if (!NodeDownloads.UsesCommunityBuild(version))
            {
                OpenUrl(NodeDownloads.UrlFor(version));
                return;
            }

            // The community build exists only for 64-bit Windows; a 32-bit system has
            // no Node.js 22 at all, so do not send the user to a page without a download.
            if (!Environment.Is64BitOperatingSystem)
            {
                MessageBox.Show(
                    L.T("node.unsupportedBody"),
                    L.T("node.unsupportedTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            DialogResult answer = MessageBox.Show(
                L.T("node.communityBody"),
                L.T("node.communityTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            OpenUrl(NodeDownloads.UrlFor(version));
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
            using (AboutForm form = new AboutForm())
            {
                form.ShowDialog();
            }
        }

        private void ShowSettings()
        {
            int previousPort = _port;
            bool restartRequested = false;
            // A running/starting/stopping service owns the old port. Changing the
            // configured port without restarting the whole tray would make CLI
            // commands and the service state disagree, so require a stopped service.
            bool allowPortChange = _phase != Phase.Running && _phase != Phase.Starting
                && _phase != Phase.Stopping && _phase != Phase.Installing
                && _phase != Phase.Checking;
            using (SettingsForm form = new SettingsForm(_config, allowPortChange))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    restartRequested = form.RestartRequested;
                    bool portRejected = false;
                    if (_config.Port != previousPort)
                    {
                        // The state can change while the modal dialog is open (for example
                        // an auto-restart or a start signal). Never adopt a new port while
                        // a service may still own the old one.
                        bool canApplyPort = _phase != Phase.Running && _phase != Phase.Starting
                            && _phase != Phase.Stopping && _phase != Phase.Installing
                            && _phase != Phase.Checking
                            && (_phase == Phase.PortConflict || !Net.IsPortOpen(previousPort, 200));
                        if (!canApplyPort)
                        {
                            _config.Port = previousPort;
                            _config.SetValue("Port", previousPort.ToString());
                            portRejected = true;
                            MessageBox.Show(L.T("settings.portRequiresStopped"),
                                L.T("settings.title"),
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                        else
                        {
                            _port = _config.Port;
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
                        Notify(L.T("settings.savedTitle"), L.T("settings.savedBody"));
                }
            }
        }

        // The save-and-restart button closes this tray after starting a new
        // process. The new process waits for the single-instance mutex, so the
        // old service is fully stopped before the replacement starts.
        private void RestartTray()
        {
            string arguments = "--restart";
            if (!_autoStartService && _phase != Phase.Running)
                arguments += " --tray-only";
            else if (!_autoStartService || !_initialOpenBrowser)
                arguments += " --start-no-open";

            try
            {
                using (Process child = Sys.StartHidden(Application.ExecutablePath,
                    arguments, Application.StartupPath))
                {
                    if (child == null)
                    {
                        NotifyError(L.T("notify.restartFailed"), Application.ExecutablePath);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                NotifyError(L.T("notify.restartFailed"), ex.Message);
                return;
            }
            ExitApp();
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

            DialogResult answer = MessageBox.Show(
                L.T("notify.adminRestartBody"),
                L.T("notify.adminRestartTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            string arguments = !_autoStartService && _phase != Phase.Running
                ? "--tray-only"
                : (!_initialOpenBrowser ? "--start-no-open" : "");
            if (!Sys.RunElevated(Application.ExecutablePath, arguments))
                NotifyError(L.T("notify.adminRestartFailed"), L.T("notify.adminRestartFailedBody"));
        }

        private void CreateDesktopShortcut()
        {
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
                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                if (shortcut == null) throw new InvalidOperationException("CreateShortcut returned null");

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
