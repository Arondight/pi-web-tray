// Pi Web Tray — GitHub update state machine
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

            if (!silent) _autoUpdateCheckAt = DateTime.MinValue;

            bool serviceRunning = _phase == Phase.Running;
            UpdateCancellation cancellation = new UpdateCancellation();
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
                _updateCancellation = cancellation;
                _updateStartedWithServiceRunning = serviceRunning;
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
                ReleaseInfo release = Updater.FindLatest(_config, cancellation, out error);
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
                    delegate
                    {
                        // A notification can outlive the state it was created in.
                        // Never start a download while an install or service action is
                        // in progress.
                        if (!_exiting && !UpdateBusy() && !IsServiceActionInProgress()
                            && _phase != Phase.Checking)
                        {
                            OfferUpdate(release);
                        }
                    });
                return;
            }

            OfferUpdate(release);
        }

        private void OfferUpdate(ReleaseInfo release)
        {
            if (release == null || _exiting) return;

            // HandleUpdateCheck calls this while its check is still the active
            // operation. A notification action calls it with no active operation.
            // The distinction matters if an automatic check starts while the
            // confirmation dialog is open.
            bool ownsCheck;
            lock (_updateLock) ownsCheck = _updateBusy && _updateStage == UpdateStage.Checking;

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
                if (ownsCheck && UpdateBusy()) EndUpdate();
                return;
            }

            if (!ownsCheck && UpdateBusy())
            {
                Updater.AppendLog(_config.UpdateLogFile,
                    "update offer ignored: another update operation is active");
                return;
            }

            BeginUpdateDownload(release);
        }

        private void BeginUpdateDownload(ReleaseInfo release)
        {
            // Keep the state from the check as well as the state at the start of the
            // download. If the service stops before the helper is launched, this still
            // preserves a service that was running when the update began.
            _updateStartedWithServiceRunning = _updateStartedWithServiceRunning
                || _phase == Phase.Running || _restartAfterUpdate;

            UpdateCancellation cancellation = new UpdateCancellation();
            int generation;
            lock (_updateLock)
            {
                // A deferred offer (menu or notification) starts here after EndUpdate()
                // cleared the flag, so the download must take the busy state again.
                _updateBusy = true;
                _updateStage = UpdateStage.Downloading;
                _updateDone = false;
                _updateRelease = release;
                _updateStaging = null;
                _updateError = null;
                _updateCancellation = cancellation;
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
                    cancellation,
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

            bool elevated = _elevated;
            bool stoppedByUser = _serviceStoppedByUser;
            bool startedRunning = _updateStartedWithServiceRunning;
            bool autoRestart = _config.AutoRestart;
            Thread worker = new Thread(delegate ()
            {
                // The apply mode depends on the port owner, and probing it (netstat child
                // process plus the HTTP fallback) can take seconds; doing it here keeps the
                // UI thread free. The stop/kill itself still happens in ExitApp.
                string error;
                bool ok = false;
                try
                {
                    ServiceProbe.Invalidate();
                    PortOwnerInfo owner = ServiceProbe.Inspect(_port, ExpectedPiWebScript());
                    bool serviceSilent = (!stoppedByUser
                            && (IsOurPiWeb(owner) || TrayOwnsPort(owner)))
                        || ShouldRestartServiceAfterUpdate(startedRunning, autoRestart,
                            stoppedByUser, _restartAfterUpdate);
                    ApplyStartMode mode = serviceSilent
                        ? ApplyStartMode.ServiceSilent
                        : ApplyStartMode.TrayOnly;
                    bool waitForService = mode == ApplyStartMode.ServiceSilent
                        || _updateStartedWithServiceRunning;
                    ok = Updater.StartHelper(_config, staging, mode, elevated,
                        waitForService, out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
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
            if (!ExitApp(false))
            {
                // An install confirmation or another exit guard can leave the tray
                // running while the helper is still waiting. Do not leave the update
                // form in Applying with its Cancel button disabled indefinitely.
                Updater.AppendLog(_config.UpdateLogFile,
                    "tray exit was blocked; clearing the update state");
                EndUpdate();
                MessageBox.Show(L.T("update.error.exitBlocked"),
                    L.T("update.applyFailedTitle"), MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void EndUpdate()
        {
            UpdateCancellation cancellation;
            CloseUpdateForm();
            lock (_updateLock)
            {
                _updateGeneration++;
                _updateBusy = false;
                _updateDone = false;
                _updateStage = UpdateStage.None;
                _updateRelease = null;
                _updateStaging = null;
                _updateError = null;
                cancellation = _updateCancellation;
                _updateCancellation = null;
            }
            if (cancellation != null) cancellation.Cancel();

            _updatePercent = -1;
            UpdateUi();

            bool restartAfterUpdate = ShouldRestartServiceAfterUpdate(
                _updateStartedWithServiceRunning, _config.AutoRestart,
                _serviceStoppedByUser, _restartAfterUpdate);
            _restartAfterUpdate = false;
            _updateStartedWithServiceRunning = false;
            if (restartAfterUpdate && _config.AutoRestart
                && CanStartServiceFromProbe(_probe, File.Exists)
                && (_phase == Phase.Stopped || _phase == Phase.Failed))
            {
                ScheduleAutoRestart();
            }
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
            UpdateCancellation cancellation;
            lock (_updateLock) cancellation = _updateCancellation;
            if (cancellation != null) cancellation.Cancel();
            EndUpdate();
            NotifyWarning(L.T("update.cancelledTitle"), L.T("update.cancelledBody"));
        }

        private void ShowUpdateError(string title, string body)
        {
            MessageBox.Show(body, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
