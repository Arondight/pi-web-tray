// Pi Web Tray — start-at-login operations
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
        private int _adminAutostartRefusals;
        private DateTime _lastAdminAutostartRefusal = DateTime.MinValue;
        private bool _existingAdminTaskAuditDone;
        private bool _legacyTaskCache;
        private bool _legacyTaskEnabledCache;
        private bool _taskEnabledCache;
        private DateTime _legacyTaskCacheAt = DateTime.MinValue;

        private bool IsRunKeyEnabled()
        {
            return Autostart.RunValueExists();
        }

        private void RefreshAutostartItems()
        {
            BeginTaskRefresh();
            UpdateAutostartItems();
        }

        private void UpdateAutostartItems()
        {
            bool task = TaskExists(true);
            bool taskEnabled = task && TaskEnabled(true);
            bool legacyTask = LegacyTaskEnabled(true);
            bool run = IsRunKeyEnabled()
                && Autostart.RunValueTargetsExecutable(Application.ExecutablePath)
                && Autostart.RunValueApproved();
            bool busy = AutostartBusy() || UpdateBusy();

            // A task that exists but is disabled or points at an older installation
            // does not make the current installation start at login.
            taskEnabled = taskEnabled || legacyTask;
            _autostartItem.Text = busy
                ? L.T("menu.autostartBusy")
                : L.T("menu.autostart", Autostart.Describe(taskEnabled, run));
            _autostartItem.Checked = taskEnabled || run;
            _autostartItem.Enabled = !busy;

            _adminAutostartItem.Text = L.T("menu.adminAutostart", _config.AutostartAdmin ? L.T("bool.yes") : L.T("bool.no"));
            _adminAutostartItem.Checked = _config.AutostartAdmin;
            _adminAutostartItem.Enabled = !busy;
        }

        private void ToggleAutostart()
        {
            bool enable = !_autostartItem.Checked;
            if (enable && _config.AutostartAdmin && RefuseAdminAutostart())
            {
                // The administrator preference is not usable (folder or account).
                // Fall back to standard start-at-login instead of blocking the
                // user's obvious intent; the refusal notification already explained
                // why administrator mode was not used.
                _config.AutostartAdmin = false;
                if (!_config.SetValue("AutostartAdmin", "0"))
                    NotifyError(L.T("settings.title"), L.T("settings.saveFailed"));
                UpdateAutostartItems();
            }
            BeginAutostartOp(enable, _config.AutostartAdmin);
        }

        private void ToggleAdminAutostart()
        {
            if (AutostartBusy()) return;

            bool enable = !_config.AutostartAdmin;
            if (enable && RefuseAdminAutostart()) return;

            bool previous = _config.AutostartAdmin;
            _config.AutostartAdmin = enable;
            if (!_config.SetValue("AutostartAdmin", _config.AutostartAdmin ? "1" : "0"))
            {
                _config.AutostartAdmin = previous;
                NotifyError(L.T("settings.title"), L.T("settings.saveFailed"));
                return;
            }
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

        // Keep the menu text unchanged when the administrator mode is unavailable.
        // The click is rejected without mutating the preference; a detailed clickable
        // balloon explains why, and a second attempt in the same short window falls
        // back to a modal dialog in case notifications are suppressed.
        private bool RefuseAdminAutostart()
        {
            if (!Autostart.IsUserWritableInstall(Application.ExecutablePath))
            {
                if (Autostart.IsAdministratorAccount()) return false;
                try
                {
                    Updater.AppendLog(_config.UpdateLogFile,
                        "administrator start-at-login refused: current account is not an administrator");
                }
                catch (Exception)
                {
                }
                NotifyWarning(L.T("auto.adminAccountTitle"), L.T("auto.adminAccountRequired"));
                return true;
            }

            string directory = Path.GetDirectoryName(Application.ExecutablePath)
                ?? Application.ExecutablePath;
            try
            {
                Updater.AppendLog(_config.UpdateLogFile,
                    "administrator start-at-login refused: user-writable install " + directory);
            }
            catch (Exception)
            {
            }

            DateTime now = DateTime.Now;
            if (_lastAdminAutostartRefusal == DateTime.MinValue
                || now - _lastAdminAutostartRefusal > TimeSpan.FromMinutes(2))
            {
                _adminAutostartRefusals = 0;
            }
            _lastAdminAutostartRefusal = now;
            _adminAutostartRefusals++;

            if (_adminAutostartRefusals >= 2)
            {
                _adminAutostartRefusals = 0;
                ShowAdminAutostartBlockedDetails();
            }
            else
            {
                NotifyWarning(
                    L.T("auto.adminWritableTitle"),
                    L.T("auto.adminWritableBalloon"),
                    delegate { ShowAdminAutostartBlockedDetails(); });
            }
            return true;
        }

        private void ShowAdminAutostartBlockedDetails()
        {
            string directory = Path.GetDirectoryName(Application.ExecutablePath)
                ?? Application.ExecutablePath;
            BeginModal();
            try
            {
                MessageBox.Show(
                    L.T("auto.adminWritableDetails", directory),
                    L.T("auto.adminWritableTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                EndModal();
            }
        }

        // Existing tasks created by older releases are not covered by the toggle-time
        // check. Warn once per session if such a task points at this user-writable
        // installation; disabling it remains possible through the normal menu action.
        private void AuditExistingAdminTask()
        {
            if (_existingAdminTaskAuditDone || AutostartBusy()) return;
            if (!TaskExists(false) && !LegacyTaskExists(false)) return;
            if (!Autostart.IsUserWritableInstall(Application.ExecutablePath)) return;

            _existingAdminTaskAuditDone = true;
            string directory = Path.GetDirectoryName(Application.ExecutablePath)
                ?? Application.ExecutablePath;
            try
            {
                Updater.AppendLog(_config.UpdateLogFile,
                    "existing administrator start-at-login uses user-writable install " + directory);
            }
            catch (Exception)
            {
            }
            NotifyWarning(
                L.T("auto.adminWritableExistingTitle"),
                L.T("auto.adminWritableExistingBalloon"),
                delegate { ShowExistingAdminTaskDetails(); });
        }

        private void ShowExistingAdminTaskDetails()
        {
            string directory = Path.GetDirectoryName(Application.ExecutablePath)
                ?? Application.ExecutablePath;
            string taskName = LegacyTaskExists(false)
                ? Autostart.LegacyTaskName
                : Autostart.TaskName;
            BeginModal();
            try
            {
                MessageBox.Show(
                    L.T("auto.adminWritableExistingDetails", taskName, directory),
                    L.T("auto.adminWritableExistingTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                EndModal();
            }
        }

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
            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                lock (_autoLock)
                {
                    _autoMessage = L.T("auto.failed") + "\r\n" + ex.Message;
                    _autoError = true;
                    _autoBusy = false;
                    _autoDone = true;
                }
            }
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

        private bool TaskEnabled(bool kickRefresh)
        {
            bool value;
            DateTime at;
            lock (_taskCacheLock)
            {
                value = _taskEnabledCache;
                at = _taskCacheAt;
            }

            bool stale = at == DateTime.MinValue || DateTime.Now - at > TimeSpan.FromSeconds(10);
            if (kickRefresh && stale) BeginTaskRefresh();
            return value;
        }

        private bool LegacyTaskExists(bool kickRefresh)
        {
            bool value;
            DateTime at;
            lock (_taskCacheLock)
            {
                value = _legacyTaskCache;
                at = _legacyTaskCacheAt;
            }

            bool stale = at == DateTime.MinValue || DateTime.Now - at > TimeSpan.FromSeconds(10);
            if (kickRefresh && stale) BeginTaskRefresh();
            return value;
        }

        private bool LegacyTaskEnabled(bool kickRefresh)
        {
            bool value;
            DateTime at;
            lock (_taskCacheLock)
            {
                value = _legacyTaskEnabledCache;
                at = _legacyTaskCacheAt;
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
                bool enabled = false;
                bool matchesExecutable = true;
                bool legacy = false;
                bool legacyEnabled = false;
                bool legacyMatches = false;
                try
                {
                    task = Autostart.QueryTaskState(out enabled, out matchesExecutable);
                }
                catch (Exception)
                {
                }
                try
                {
                    legacy = Autostart.QueryLegacyTaskState(out legacyEnabled, out legacyMatches);
                }
                catch (Exception) { }
                lock (_taskCacheLock)
                {
                    _taskCache = task;
                    _taskEnabledCache = task && enabled && matchesExecutable;
                    _legacyTaskCache = legacy;
                    _legacyTaskEnabledCache = legacy && legacyEnabled && legacyMatches;
                    _taskCacheAt = DateTime.Now;
                    _legacyTaskCacheAt = DateTime.Now;
                    _taskRefreshRunning = false;
                    _taskRefreshDone = true;
                }
            });
            worker.IsBackground = true;
            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                try
                {
                    Updater.AppendLog(_config.UpdateLogFile,
                        "could not start autostart state worker: " + ex.Message);
                }
                catch (Exception) { }
                lock (_taskCacheLock)
                {
                    _taskRefreshRunning = false;
                    _taskRefreshDone = true;
                }
            }
        }

        private void PollTaskRefresh()
        {
            bool done;
            lock (_taskCacheLock)
            {
                done = _taskRefreshDone;
                _taskRefreshDone = false;
            }
            if (done)
            {
                // The two autostart modes are mutually exclusive. A stale Run value
                // (for example left by an older release) would start a second tray at
                // login. Remove it once the task state is known instead of letting
                // RepairRunValue preserve both modes indefinitely.
                // Only replace the Run value when the scheduled task is actually
                // usable. A disabled task plus a working Run value is still the
                // user's only working startup path.
                if ((TaskEnabled(false) || LegacyTaskEnabled(false))
                    && IsRunKeyEnabled() && !AutostartBusy())
                {
                    Autostart.ClearRunValue();
                }
                AuditExistingAdminTask();
                UpdateAutostartItems();
            }

            // Keep the cached value from going stale while the tray sits idle
            bool stale;
            DateTime at;
            DateTime legacyAt;
            lock (_taskCacheLock)
            {
                at = _taskCacheAt;
                legacyAt = _legacyTaskCacheAt;
            }
            stale = at == DateTime.MinValue || DateTime.Now - at > TimeSpan.FromSeconds(60)
                || legacyAt == DateTime.MinValue
                || DateTime.Now - legacyAt > TimeSpan.FromSeconds(60);
            if (stale) BeginTaskRefresh();
        }

        private bool AutostartBusy()
        {
            lock (_autoLock) return _autoBusy;
        }
    }
}
