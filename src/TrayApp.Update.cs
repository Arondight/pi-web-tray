// Pi Web Tray — GitHub update state machine
//
// Partial of TrayApp. Kept separate so the tray state machine stays readable.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

        // The helper can only wait for a port quickly, not inspect its owner. Pass the
        // port only when the update expects our service to occupy it. Otherwise a
        // foreign listener on the configured port would block an otherwise safe update.
        internal static bool ShouldWaitForServicePortBeforeUpdate(bool serviceSilent,
            bool startedWithServiceRunning, bool listenerIsOurs,
            bool ownerCheckFailed = false)
        {
            // A failed owner probe is not evidence that the port is safe to replace.
            // Wait for it to close (or let the helper abort) rather than risking a
            // file replacement while an unidentifiable service is still running.
            if (ownerCheckFailed) return true;

            // A known foreign listener is never our service and must not block the update.
            return listenerIsOurs && (serviceSilent || startedWithServiceRunning);
        }

        private void BeginUpdateCheck(bool silent)
        {
            if (_exiting || AutostartBusy() || _phase == Phase.Checking
                || IsServiceActionInProgress()) return;

            if (!silent) _autoUpdateCheckAt = DateTime.MinValue;

            bool serviceRunning = _phase == Phase.Running
                || _phase == Phase.RunningUnverified;
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

            try
            {
                if (!silent) ShowUpdateForm();
                _updatePercent = -1;
                _updateTicks = 0;
                UpdateUi();
                Updater.AppendLog(_config.UpdateLogFile,
                    "check start (current " + Program.Version + (silent ? ", automatic" : "") + ")");
                if (!silent)
                    Notify(L.T("update.checkingTitle"), L.T("update.checkingBody", Program.Version));
            }
            catch (Exception ex)
            {
                AbortUpdateStartup(ex, !silent,
                    L.T("update.checkFailedTitle"), L.T("update.checkFailedBody", ex.Message));
                return;
            }

            Thread worker = new Thread(delegate ()
            {
                string error = null;
                ReleaseInfo release = null;
                try
                {
                    release = Updater.FindLatest(_config, cancellation, out error);
                }
                catch (Exception ex)
                {
                    // A network/configuration exception must still release the UI
                    // busy gate. Without this finally, a single unexpected failure
                    // leaves _updateBusy true until the tray is restarted.
                    release = null;
                    error = ex.Message;
                }
                finally
                {
                    lock (_updateLock)
                    {
                        if (generation == _updateGeneration)
                        {
                            _updateRelease = release;
                            _updateError = error;
                            _updateDone = true;
                        }
                    }
                }
            });
            worker.IsBackground = true;
            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                lock (_updateLock)
                {
                    if (generation == _updateGeneration)
                    {
                        _updateRelease = null;
                        _updateError = ex.Message;
                        _updateDone = true;
                    }
                }
            }
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
                    BeginModal();
                    try
                    {
                        MessageBox.Show(
                            L.T("update.upToDateBody", Program.Version),
                            L.T("update.upToDateTitle"),
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    finally
                    {
                        EndModal();
                    }
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
                            && !AutostartBusy() && _phase != Phase.Checking)
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
            if (release == null || _exiting || AutostartBusy()) return;

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

            DialogResult answer;
            BeginModal();
            try
            {
                answer = MessageBox.Show(
                    body,
                    L.T("update.confirmTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
            }
            finally
            {
                EndModal();
            }
            if (answer != DialogResult.Yes)
            {
                Updater.AppendLog(_config.UpdateLogFile, "cancelled by user");
                // Allow a fresh check next time instead of making the menu offer the
                // same declined release forever. Only clear the exact release that was
                // offered; a newer check must not be discarded here.
                if (ReferenceEquals(_availableRelease, release)) _availableRelease = null;
                if (ownsCheck && UpdateBusy()) EndUpdate();
                // A deferred notification has no check operation left to call
                // EndUpdate. Do not keep the old service intent forever after the
                // user declines that offer. If a newer release is still pending,
                // its own intent must be preserved.
                else if (!UpdateBusy() && _availableRelease == null)
                    _updateStartedWithServiceRunning = false;
                return;
            }

            if (!ownsCheck && UpdateBusy())
            {
                Updater.AppendLog(_config.UpdateLogFile,
                    "update offer ignored: another update operation is active");
                return;
            }

            if (AutostartBusy())
            {
                // This path is only reachable when an autostart operation became
                // busy while the confirmation dialog was open. Finish the check it
                // belongs to instead of leaving the update form in Checking forever.
                Updater.AppendLog(_config.UpdateLogFile,
                    "update offer ignored: autostart operation is active");
                if (ownsCheck && UpdateBusy()) EndUpdate();
                return;
            }

            BeginUpdateDownload(release);
        }

        private void BeginUpdateDownload(ReleaseInfo release)
        {
            // Downloading does not stop or start the service. Keep it available
            // while a service action is running; only the autostart task operation
            // owns a conflicting update lock. An offer declined here leaves the
            // caller's update state intact, so do not add a busy return without
            // making the caller finish that state.
            if (AutostartBusy()) return;
            // Keep the state from the check as well as the state at the start of the
            // download. If the service stops before the helper is launched, this still
            // preserves a service that was running when the update began.
            _updateStartedWithServiceRunning = _updateStartedWithServiceRunning
                || _phase == Phase.Running || _phase == Phase.RunningUnverified
                || _phase == Phase.Starting
                || _restartAfterUpdate;
            _availableRelease = null;

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

            try
            {
                ShowUpdateForm();
                _updatePercent = -1;
                _updateTicks = 0;
                UpdateUi();
                Notify(L.T("update.downloadingTitle"),
                    L.T("update.downloadingBody", release.Version));
            }
            catch (Exception ex)
            {
                AbortUpdateStartup(ex, true, L.T("update.downloadFailedTitle"),
                    L.T("update.downloadFailedBody", ex.Message, _config.UpdateLogFile));
                return;
            }

            Thread worker = new Thread(delegate ()
            {
                string error = null;
                string staging = null;
                try
                {
                    staging = Updater.Prepare(
                        _config,
                        release,
                        delegate (int percent) { _updatePercent = percent; },
                        cancellation,
                        out error);
                }
                catch (Exception ex)
                {
                    // Keep the same invariant as the check worker: an unexpected
                    // exception must not leave _updateBusy set until process exit.
                    staging = null;
                    error = ex.Message;
                }
                finally
                {
                    lock (_updateLock)
                    {
                        if (generation == _updateGeneration)
                        {
                            _updateStaging = staging;
                            _updateError = error;
                            _updateDone = true;
                        }
                    }
                }
            });
            worker.IsBackground = true;
            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                lock (_updateLock)
                {
                    if (generation == _updateGeneration)
                    {
                        _updateStaging = null;
                        _updateError = ex.Message;
                        _updateDone = true;
                    }
                }
            }
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

        private void ReleaseUpdateHelperGate()
        {
            string path = _updateHelperGatePath;
            bool signaled = _updateHelperGateSignaled;
            _updateHelperGatePath = null;
            _updateHelperGateSignaled = false;
            if (string.IsNullOrEmpty(path) || signaled) return;
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        private bool SignalUpdateHelperGate()
        {
            string path = _updateHelperGatePath;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                // Remove a gate planted before the parent confirmation. If an attacker
                // recreates it between this delete and CreateNew, CreateNew fails and
                // the helper will reject the confirmation instead of applying the update.
                if (File.Exists(path)) File.Delete(path);
                if (PathSafety.ContainsReparsePoint(path)) return false;
                // The file is created only after every ExitApp guard has passed.
                // It persists even if this process exits before the helper opens it.
                // CreateNew never follows a pre-created reparse point at the gate name.
                using (FileStream stream = new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    byte[] data = new UTF8Encoding(false).GetBytes("confirmed");
                    stream.Write(data, 0, data.Length);
                }
                _updateHelperGateSignaled = true;
                return true;
            }
            catch (Exception ex)
            {
                // Do not leave a pre-created or partial gate for the helper to trust.
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
                Updater.AppendLog(_config.UpdateLogFile, "could not confirm update gate: " + ex.Message);
                return false;
            }
        }

        private void BeginUpdateApply(ReleaseInfo release, string staging)
        {
            // The executable is about to be replaced from this directory. Re-check
            // the ACL at action time: an elevated tray must not launch a helper from
            // a directory that standard users can modify, even if it was protected
            // when the tray started.
            if (ElevatedInstallUnsafe())
            {
                Updater.AppendLog(_config.UpdateLogFile,
                    "update refused: installation directory became user-writable");
                EndUpdate();
                ShowUpdateError(L.T("notify.elevatedWritableTitle"),
                    L.T("notify.elevatedWritableBody"));
                return;
            }

            ReleaseUpdateHelperGate();
            string gatePath = "";
            try
            {
                string work = Path.GetDirectoryName(staging);
                if (!string.IsNullOrEmpty(work))
                    gatePath = Path.Combine(work, "gate-" + Guid.NewGuid().ToString("N"));
            }
            catch (Exception)
            {
            }
            if (string.IsNullOrEmpty(gatePath))
            {
                Updater.AppendLog(_config.UpdateLogFile, "could not prepare update gate");
                EndUpdate();
                ShowUpdateError(L.T("update.error.notConfirmed"),
                    L.T("update.applyFailedBody", L.T("update.error.unsafePath"),
                        _config.UpdateLogFile));
                return;
            }
            _updateHelperGatePath = gatePath;

            int generation;
            lock (_updateLock)
            {
                _updateStage = UpdateStage.Applying;
                _updateDone = false;
                _updateError = null;
                _updateGeneration++;
                generation = _updateGeneration;
            }

            try
            {
                ShowUpdateForm();
                _updatePercent = -1;
                _updateTicks = 0;
                UpdateUi();
                Notify(L.T("update.applyingTitle"),
                    L.T("update.applyingBody", release.Version));
            }
            catch (Exception ex)
            {
                AbortUpdateStartup(ex, true, L.T("update.applyFailedTitle"),
                    L.T("update.applyFailedBody", ex.Message, _config.UpdateLogFile));
                return;
            }

            bool elevated = _elevated;
            bool stoppedByUser = _serviceStoppedByUser;
            bool startedRunning = _updateStartedWithServiceRunning;
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
                    bool listenerIsOurs = IsOurPiWeb(owner) || TrayOwnsPort(owner);
                    bool serviceSilent = (!stoppedByUser && listenerIsOurs)
                        || ShouldRestartServiceAfterUpdate(startedRunning,
                            stoppedByUser, _restartAfterUpdate);
                    ApplyStartMode mode = serviceSilent
                        ? ApplyStartMode.ServiceSilent
                        : ApplyStartMode.TrayOnly;
                    // Only wait for the port when the update itself is meant to keep
                    // or restore our service. A known foreign listener is not a reason
                    // to abort. If the owner check failed, wait rather than risk
                    // replacing files while an unidentifiable listener still runs.
                    bool ownerCheckFailed = owner != null
                        && (owner.CheckFailed || owner.ProbeIncomplete);
                    bool waitForListener = listenerIsOurs || ownerCheckFailed;
                    bool waitForService = ShouldWaitForServicePortBeforeUpdate(
                        mode == ApplyStartMode.ServiceSilent,
                        startedRunning, waitForListener, ownerCheckFailed);
                    string work = Path.GetDirectoryName(staging);
                    string zipPath = string.IsNullOrEmpty(work)
                        ? ""
                        : Path.Combine(work, release.ZipName);
                    ok = Updater.StartHelper(_config, staging, zipPath, release.ZipChecksum,
                        mode, elevated, waitForService, gatePath, out error);
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
            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                lock (_updateLock)
                {
                    if (generation == _updateGeneration)
                    {
                        _updateError = ex.Message;
                        _updateDone = true;
                    }
                }
            }
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
            // let it replace the files and start the new tray. This is the only ExitApp
            // call allowed while the update is in the Applying stage.
            Updater.AppendLog(_config.UpdateLogFile, "helper started; stopping the tray");
            _exitForUpdateApply = true;
            bool exited = ExitApp(false);
            if (!exited) _exitForUpdateApply = false;
            if (!exited)
            {
                // An install confirmation or another exit guard can leave the tray
                // running while the helper is still waiting. Do not leave the update
                // form in Applying with its Cancel button disabled indefinitely.
                Updater.AppendLog(_config.UpdateLogFile,
                    "tray exit was blocked; clearing the update state");
                EndUpdate();
                BeginModal();
                try
                {
                    MessageBox.Show(L.T("update.error.exitBlocked"),
                        L.T("update.applyFailedTitle"), MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                finally
                {
                    EndModal();
                }
            }
        }

        private void EndUpdate()
        {
            UpdateCancellation cancellation;
            ReleaseUpdateHelperGate();
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
                _updateStartedWithServiceRunning, _serviceStoppedByUser,
                _restartAfterUpdate);
            _restartAfterUpdate = false;
            // A silent check can end with an available release waiting for the
            // notification click. Keep the service intent until that offer is
            // accepted or declined; otherwise a service that was running during
            // the check is not preserved when the deferred download begins.
            if (_availableRelease == null) _updateStartedWithServiceRunning = false;
            if (restartAfterUpdate && _config.AutoRestart
                && CanStartServiceFromProbe(_probe, File.Exists)
                && (_phase == Phase.Stopped || _phase == Phase.Failed))
            {
                ScheduleAutoRestart();
            }
        }

        private void AbortUpdateStartup(Exception ex, bool showError,
            string title, string body)
        {
            try
            {
                Updater.AppendLog(_config.UpdateLogFile,
                    "update UI startup failed: " + ex);
            }
            catch (Exception)
            {
            }
            try { EndUpdate(); } catch (Exception) { }
            if (!showError) return;
            try { ShowUpdateError(title, body); } catch (Exception) { }
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
            // Once the helper has been started it cannot be cancelled safely: the
            // helper may already be waiting for this process to exit and replacing
            // files. The UI disables the button, but protect the code path too.
            if (_updateStage == UpdateStage.Applying) return;
            Updater.AppendLog(_config.UpdateLogFile, "cancelled by user");
            UpdateCancellation cancellation;
            lock (_updateLock) cancellation = _updateCancellation;
            if (cancellation != null) cancellation.Cancel();
            EndUpdate();
            NotifyWarning(L.T("update.cancelledTitle"), L.T("update.cancelledBody"));
        }

        private void ShowUpdateError(string title, string body)
        {
            BeginModal();
            try
            {
                MessageBox.Show(body, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                EndModal();
            }
        }
    }
}
