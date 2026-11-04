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
                BeginEnvironmentCheck(thenStart, thenOpen);
                return;
            }

            // The stop decision needs the port owner; probing it can take seconds, so it
            // runs in the background and the install continues when the result arrives.
            if (_phase != Phase.Running && Net.IsPortOpen(_port, 200))
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
            // The phase may have changed while the owner check ran
            if (_phase == Phase.Starting || _phase == Phase.Stopping) return;
            if (_installAfterStop)
            {
                _installAfterStopForce = _installAfterStopForce || force;
                _installStartAfter = _installStartAfter || thenStart;
                _installOpenAfter = _installOpenAfter || thenOpen;
                return;
            }
            if (_phase == Phase.Installing) return;

            ProbeResult probe = _probe;
            if (probe == null)
            {
                BeginEnvironmentCheck(thenStart, thenOpen);
                return;
            }
            if (probe.NeedsNode)
            {
                // The environment check can finish while the owner check runs; re-guard
                // here so an install queued before that result is not started anyway.
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
                BeginEnvironmentCheck(thenStart, thenOpen);
                return;
            }

            // Remember the requested intent before any stop: a service that was stopped on
            // purpose stays stopped after a menu-driven update, while a running service (or
            // an explicit start request) is started again. Running implies the port is ours
            // (this tray started it), so no owner result is needed in that case.
            bool needsStop = _phase == Phase.Running
                || (owner != null && ShouldStopServiceForInstall(_phase, true,
                    IsOurPiWeb(owner), TrayOwnsPort(owner)));
            _installStartAfter = needsStop || thenStart;
            _installOpenAfter = needsStop ? false : thenOpen;

            // npm cannot replace a globally installed package while its node process is
            // running. Stop pi-web first, then continue with the same requested mode.
            if (needsStop)
            {
                _installAfterStop = true;
                _installAfterStopForce = force;
                _installRestartService = true;
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
            bool restartService = _installRestartService;
            bool startAfter = _installStartAfter;
            bool openAfter = _installOpenAfter;
            _installRestartService = false;
            _installStartAfter = false;
            _installOpenAfter = false;
            CloseInstallForm();

            if (result == null)
            {
                _phase = cancelled ? Phase.NeedsDeps : Phase.Failed;
                UpdateUi();
                if (cancelled)
                    NotifyWarning(L.T("install.cancelledTitle"), L.T("install.cancelledBody"));
                if (restartService) RestoreServiceAfterInstall();
                return;
            }

            if (result.Success)
            {
                Notify(L.T("install.doneTitle"), result.Message);
                // Re-probe before starting so paths and versions are current. In autostart
                // mode this must not turn into "start the service": login shows the tray only.
                BeginEnvironmentCheck(startAfter, openAfter);
            }
            else if (cancelled)
            {
                _phase = Phase.NeedsDeps;
                UpdateUi();
                NotifyWarning(L.T("install.cancelledTitle"), L.T("install.cancelledBody"));
                if (restartService) RestoreServiceAfterInstall();
            }
            else
            {
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("install.failedTitle"), result.Message,
                    delegate { ShowLog(_config.InstallLogFile, L.T("menu.installLog")); });
                if (restartService) RestoreServiceAfterInstall();
            }
        }

        // Kept as a pure function so the install-stop decision stays testable; the owner
        // result now comes from the background owner check (ContinueBeginInstall).
        internal static bool ShouldStopServiceForInstall(Phase phase, bool portOpen,
            bool ownerIsPiWeb, bool ownedByTray)
        {
            if (phase == Phase.Running) return true;
            if (!portOpen) return false;
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
            Updater.AppendLog(_config.UpdateLogFile, "dependency update cancelled: pi-web is still running");
            if (notify && _phase != Phase.PortConflict)
            {
                NotifyWarning(L.T("notify.stopFailedTitle"), L.T("notify.stopFailedBody", _port));
            }
        }

        private void RestoreServiceAfterInstall()
        {
            if (_probe != null && _probe.Ready)
            {
                _phase = Phase.Stopped;
                StartServer(false);
            }
            else
            {
                BeginEnvironmentCheck(true, false);
            }
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
            if (_probe != null && _probe.Ready)
            {
                string title = _config.InstallPiCli
                    ? L.T("install.confirmTitle") : L.T("install.confirmTitlePiOnly");
                string body = _config.InstallPiCli
                    ? L.T("install.confirmBody") : L.T("install.confirmBodyPiOnly");
                DialogResult answer = MessageBox.Show(
                    body,
                    title,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }
            BeginInstall(true, false, false);
        }
    }
}
