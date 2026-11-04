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

            // Remember the requested intent before any stop: a service that was stopped on
            // purpose stays stopped after a menu-driven update, while a running service (or
            // an explicit start request) is started again.
            bool needsStop = InstallNeedsStop();
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

                StopServer();
                if (_phase == Phase.Stopped) ContinueInstallAfterStop();
                else if (_phase != Phase.Stopping) AbortInstallAfterStop(false);
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

        private bool InstallNeedsStop()
        {
            if (_phase == Phase.Running) return true;
            if (!Net.IsPortOpen(_port, 200)) return false;

            ServiceProbe.Invalidate();
            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            return ShouldStopServiceForInstall(_phase, true, IsOurPiWeb(owner), TrayOwnsPort());
        }

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
