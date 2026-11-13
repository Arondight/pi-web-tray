// Pi Web Tray — dependency installation and progress UI
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
        // thenStart/thenOpen describe what to do after a successful install. A menu-driven
        // dependency update must not start a service the user had stopped; a startup install
        // or an icon click passes its own intent instead of reusing _autoStartService.
        private void BeginInstall(bool force, bool thenStart, bool thenOpen)
        {
            if (_exiting) return;
            if (ElevatedInstallUnsafe())
            {
                NotifyWarningOnce("elevated-writable-install",
                    L.T("notify.elevatedWritableTitle"),
                    L.T("notify.elevatedWritableBody"));
                _phase = Phase.Failed;
                UpdateUi();
                return;
            }
            // npm and the update helper both touch the global package tree. The
            // update helper is the only operation allowed to stop/replace files;
            // do not start an npm install while a check/download/apply is active.
            if (UpdateBusy())
            {
                if (_phase == Phase.Installing) return;
                // A probe may have completed while an update check/download was
                // active. Do not leave the tray stuck in Checking after declining
                // the install; expose the stable dependency state instead.
                if (_phase == Phase.Checking) _phase = Phase.NeedsDeps;
                bool restore = _installRestartService;
                _installRestartService = false;
                _installStartAfter = false;
                _installOpenAfter = false;
                ReleaseInstallUpdateLock();
                UpdateUi();
                NotifyWarning(L.T("notify.updateBusyTitle"), L.T("notify.updateBusyBody"));
                if (restore && _updateStage != UpdateStage.Applying)
                    RestoreServiceAfterInstall();
                return;
            }
            // A newer explicit action owns the phase now; do not let a stale phase-resolve
            // or start continuation overwrite it later.
            ClearPendingOwnerActions(false);

            // A previous npm run may have been interrupted. Force a clean reinstall so a
            // partial package directory is not mistaken for a working dependency.
            if (Installer.HasPending(_config)) force = true;

            if (_installAfterStop)
            {
                _installAfterStopForce = _installAfterStopForce || force;
                _installStartAfter = _installStartAfter || thenStart;
                _installOpenAfter = _installOpenAfter || thenOpen;
                return;
            }
            if (_phase == Phase.Installing) return;

            // The first probe can still be in flight (user clicked the menu immediately after
            // launch). Never run node/npm synchronously on the UI thread; queue the install and
            // let PollEnvironmentCheck start it when the probe result arrives.
            if (_probe == null)
            {
                ReleaseInstallUpdateLock();
                lock (_checkLock)
                {
                    _installAfterCheck = true;
                    _installAfterCheckForce = _installAfterCheckForce || force;
                    _installAfterCheckStart = _installAfterCheckStart || thenStart;
                    _installAfterCheckOpen = _installAfterCheckOpen || thenOpen;
                }
                BeginEnvironmentCheck(false, false);
                return;
            }

            ProbeResult probe = _probe;

            if (probe.NeedsNode)
            {
                ReleaseInstallUpdateLock();
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
                ReleaseInstallUpdateLock();
                // This can be reached after a stop-for-install if another process
                // installed the package in the meantime. Do not leave the install
                // restart intent behind for a later, unrelated failure/cancel.
                bool restore = _installRestartService;
                _installRestartService = false;
                _installStartAfter = false;
                _installOpenAfter = false;
                BeginEnvironmentCheck(thenStart || restore, thenOpen);
                return;
            }

            // The stop decision needs the port owner; probing it can take seconds, so it
            // runs in the background and the install continues when the result arrives.
            // Probe even in the Running phase: the port may have been taken over by an
            // unrelated process while the tray still believes it owns the service.
            if (Net.IsPortOpen(_port, 200))
            {
                ServiceProbe.Invalidate();
                InvalidateOwnerCheck();
                PortOwnerInfo owner = TakeOwnerCheck();
                if (owner == null)
                {
                    _installAfterOwnerCheck = true;
                    _installAfterOwnerCheckForce = force;
                    _installAfterOwnerCheckStart = thenStart;
                    _installAfterOwnerCheckOpen = thenOpen;
                    BeginOwnerCheck();
                    return;
                }
                ContinueBeginInstall(force, thenStart, thenOpen, owner);
                return;
            }
            ContinueBeginInstall(force, thenStart, thenOpen, null);
        }

        private void ContinueBeginInstall(bool force, bool thenStart, bool thenOpen, PortOwnerInfo owner)
        {
            if (ElevatedInstallUnsafe())
            {
                ReleaseInstallUpdateLock();
                NotifyWarningOnce("elevated-writable-install",
                    L.T("notify.elevatedWritableTitle"),
                    L.T("notify.elevatedWritableBody"));
                _phase = Phase.Failed;
                UpdateUi();
                return;
            }
            // The phase may have changed while the owner check ran. A dropped install
            // must not leave its cross-process update lock held.
            if (_phase == Phase.Starting || _phase == Phase.Stopping)
            {
                ReleaseInstallUpdateLock();
                return;
            }
            if (_installAfterStop)
            {
                _installAfterStopForce = _installAfterStopForce || force;
                _installStartAfter = _installStartAfter || thenStart;
                _installOpenAfter = _installOpenAfter || thenOpen;
                return;
            }
            if (_phase == Phase.Installing) return;

            // An update check/download can start while the owner probe is in flight
            // because the phase is still stable. Re-check before stopping the service
            // or starting npm; otherwise npm and the updater could overlap.
            if (UpdateBusy())
            {
                bool restore = _installRestartService;
                _installRestartService = false;
                _installStartAfter = false;
                _installOpenAfter = false;
                ReleaseInstallUpdateLock();
                NotifyWarning(L.T("notify.updateBusyTitle"), L.T("notify.updateBusyBody"));
                if (restore && _updateStage != UpdateStage.Applying)
                    RestoreServiceAfterInstall();
                return;
            }

            ProbeResult probe = _probe;
            if (probe == null)
            {
                ReleaseInstallUpdateLock();
                BeginEnvironmentCheck(thenStart, thenOpen);
                return;
            }
            if (probe.NeedsNode)
            {
                // The environment check can finish while the owner check runs; re-guard
                // here so an install queued before that result is not started anyway.
                ReleaseInstallUpdateLock();
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
                ReleaseInstallUpdateLock();
                bool restore = _installRestartService;
                _installRestartService = false;
                _installStartAfter = false;
                _installOpenAfter = false;
                BeginEnvironmentCheck(thenStart || restore, thenOpen);
                return;
            }

            // Serialize with the CLI install path and the update helper before stopping
            // the service. The lock is held through the stop and the npm run.
            if (!EnsureInstallUpdateLock())
            {
                bool restore = _installRestartService;
                _installRestartService = false;
                _installStartAfter = false;
                _installOpenAfter = false;
                if (restore && _updateStage != UpdateStage.Applying)
                    RestoreServiceAfterInstall();
                return;
            }

            // A failed owner probe is not proof that the port is ours (or foreign).
            // Install without stopping anything; killing an unidentifiable listener
            // would be unsafe, and aborting here would make the menu action look
            // like a port conflict.
            bool ownerCheckFailed = owner != null && owner.CheckFailed;
            if (ownerCheckFailed)
            {
                HandleOwnerCheckFailure(owner);
                owner = null;
            }

            // Remember the requested intent before any stop: a service that was stopped on
            // purpose stays stopped after a menu-driven update, while a running service (or
            // an explicit start request) is started again. Only stop the listener when the
            // owner probe proves it is this installation (or the wrapper this tray owns).
            bool needsStop = owner != null
                ? ShouldStopServiceForInstall(_phase, true,
                    IsOurPiWeb(owner), TrayOwnsPort(owner))
                : (_phase == Phase.Running && !ownerCheckFailed);
            // A stop requested while the owner check was in flight is the latest
            // user intent; do not let the install overwrite it by starting the
            // service afterwards.
            bool userWantsStopped = _serviceStoppedByUser;
            _installStartAfter = (needsStop || thenStart) && !userWantsStopped;
            _installOpenAfter = thenOpen && !userWantsStopped;

            // npm cannot replace a globally installed package while its node process is
            // running. Stop pi-web first, then continue with the same requested mode.
            if (needsStop)
            {
                _installAfterStop = true;
                _installAfterStopForce = force;
                _installRestartService = !_serviceStoppedByUser;
                _pendingRestart = false;
                _pendingOpenBrowser = false;

                // The stop may have to wait for the background owner check; in that case
                // _phase is still unchanged here. Do not abort: the stop continuation
                // (ContinueStopServer -> Stopping -> PollStopping -> ContinueInstallAfterStop)
                // completes the hand-off, and a foreign owner aborts the install there.
                StopServer();
                if (_phase == Phase.Stopped) ContinueInstallAfterStop();
                else if (_phase != Phase.Stopping && !_stopAfterOwnerCheck) AbortInstallAfterStop(false);
                return;
            }

            Installer.ResetAbort();
            Installer.ResetProgress();
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
            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                lock (_installLock)
                {
                    InstallResult failed = new InstallResult();
                    failed.Success = false;
                    failed.Message = L.T("install.crashBody", ex.Message);
                    _installResult = failed;
                    _installDone = true;
                }
            }
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
            bool restartService = _installRestartService;
            bool startAfter = _installStartAfter;
            bool openAfter = _installOpenAfter;
            bool pendingStart = _pendingStart;
            bool pendingStartOpen = _pendingStartOpen;
            _installRestartService = false;
            _installStartAfter = false;
            _installOpenAfter = false;
            _pendingStart = false;
            _pendingStartOpen = false;
            // npm has finished; the follow-up probe only reads state and may start the
            // service, so the installation lock can be released now.
            ReleaseInstallUpdateLock();
            CloseInstallForm();

            if (result == null)
            {
                bool depsMissing = _probe == null || !_probe.Ready;
                _phase = cancelled
                    ? (depsMissing ? Phase.NeedsDeps : Phase.Stopped)
                    : Phase.Failed;
                UpdateUi();
                if (cancelled)
                    NotifyWarning(L.T("install.cancelledTitle"), L.T("install.cancelledBody"));
                if (restartService || pendingStart)
                    RestoreServiceAfterInstall(pendingStart && pendingStartOpen);
                return;
            }

            if (result.Success)
            {
                Notify(L.T("install.doneTitle"), result.Message);
                // The install process leaves the service stopped. Move out of Installing
                // before starting the follow-up probe; BeginEnvironmentCheck never
                // overwrites a service action.
                _phase = Phase.Stopped;
                // Re-probe before starting so paths and versions are current. Never
                // auto-install again from this follow-up: npm reported success; if a
                // package is still missing the user needs the explicit menu action, not
                // an endless install loop.
                BeginEnvironmentCheck(startAfter || pendingStart,
                    openAfter || pendingStartOpen, 0, true);
            }
            else if (cancelled)
            {
                bool depsMissing = _probe == null || !_probe.Ready;
                _phase = depsMissing ? Phase.NeedsDeps : Phase.Stopped;
                UpdateUi();
                NotifyWarning(L.T("install.cancelledTitle"), L.T("install.cancelledBody"));
                if (restartService || pendingStart)
                    RestoreServiceAfterInstall(pendingStart && pendingStartOpen);
            }
            else
            {
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("install.failedTitle"), result.Message,
                    delegate { ShowLog(_config.InstallLogFile, L.T("log.installTitle")); });
                if (restartService || pendingStart)
                    RestoreServiceAfterInstall(pendingStart && pendingStartOpen);
            }
        }

        // Kept as a pure function so the install-stop decision stays testable; the owner
        // result now comes from the background owner check (ContinueBeginInstall).
        internal static bool ShouldStopServiceForInstall(Phase phase, bool portOpen,
            bool ownerIsPiWeb, bool ownedByTray)
        {
            // A closed port with a Running/Starting phase can still leave a tracked
            // wrapper behind; release it. When a listener exists, stop only the one
            // the owner probe identified as this installation (or this tray's child).
            if (!portOpen) return phase == Phase.Running || phase == Phase.Starting;
            return ownerIsPiWeb || ownedByTray;
        }

        private void ContinueInstallAfterStop()
        {
            if (!_installAfterStop) return;
            bool force = _installAfterStopForce;
            bool start = _installStartAfter;
            bool open = _installOpenAfter;
            _installAfterStop = false;
            _installAfterStopForce = false;
            BeginInstall(force, start, open);
        }

        private void AbortInstallAfterStop(bool notify)
        {
            if (!_installAfterStop) return;
            _installAfterStop = false;
            _installAfterStopForce = false;
            _installRestartService = false;
            _installStartAfter = false;
            _installOpenAfter = false;
            ReleaseInstallUpdateLock();
            Updater.AppendLog(_config.UpdateLogFile, "dependency update cancelled: Pi Web is still running");
            if (notify && _phase != Phase.PortConflict)
            {
                NotifyWarning(L.T("notify.stopFailedTitle"), L.T("notify.stopFailedBody", _port));
            }
        }

        private bool EnsureInstallUpdateLock()
        {
            if (_installUpdateLock != null) return true;
            _installUpdateLock = OperationLock.TryAcquire(
                OperationLock.UpdateName(_config.BaseDir));
            if (_installUpdateLock != null) return true;
            NotifyWarning(L.T("notify.updateBusyTitle"), L.T("update.error.busy"));
            return false;
        }

        private void ReleaseInstallUpdateLock()
        {
            OperationLock operationLock = _installUpdateLock;
            _installUpdateLock = null;
            if (operationLock == null) return;
            try { operationLock.Dispose(); } catch (Exception) { }
        }

        // A service can run from the installed pi-web script even when the separate
        // Pi package is missing or its installation is disabled. Readiness for
        // starting the service must not be tied to ProbeResult.Ready.
        internal static bool CanStartServiceFromProbe(ProbeResult probe, Func<string, bool> fileExists)
        {
            if (probe == null || probe.NeedsNode || !probe.PiWebFound
                || string.IsNullOrEmpty(probe.PiWebScript) || fileExists == null)
            {
                return false;
            }
            try { return fileExists(probe.PiWebScript); }
            catch (Exception) { return false; }
        }

        private void RestoreServiceAfterInstall(bool openBrowser = false)
        {
            // The probe predates the install. A failed or cancelled npm run can have
            // left a half-written package, so never use that stale result to start the
            // service. Re-probe first; suppress auto-install so this recovery cannot
            // turn into an endless install loop.
            BeginEnvironmentCheck(true, openBrowser, 0, true);
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

        private void ConfirmInstall()
        {
            // A force install reinstalls every enabled package. Show the warning whenever
            // at least one of those packages already exists, not only when the whole
            // environment is ready (for example when only Pi is missing).
            bool existingPackage = _probe != null
                && (_probe.PiWebFound || (_config.InstallPi && _probe.PiCliFound));
            if (_probe != null && !_probe.NeedsNode && (_probe.Ready || existingPackage))
            {
                string title = L.T("install.confirmTitle");
                string body = L.T("install.confirmBody");
                DialogResult answer;
                BeginModal();
                try
                {
                    answer = MessageBox.Show(
                        body,
                        title,
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
            BeginInstall(true, false, false);
        }
    }
}
