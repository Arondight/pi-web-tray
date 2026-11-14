// Pi Web Tray — exit, cleanup and log rotation
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
        private void ExitApp()
        {
            ExitApp(false);
        }

        private bool ExitApp(bool restart)
        {
            if (_exiting || _exitPromptOpen) return false;

            // The updater's own ExitApp call is the only exit allowed while the helper
            // is replacing files. A menu/CLI exit at that point would abandon the
            // helper or leave the service and the installation in an inconsistent state.
            if (UpdateBusy() && _updateStage == UpdateStage.Applying && !_exitForUpdateApply)
            {
                // Do not show a modal dialog here: --quit may be running without an
                // interactive user, and the menu exit action is disabled in this stage.
                NotifyWarning(L.T("update.busyTitle"), L.T("update.exitDuringApplyBody"));
                return false;
            }

            // The updater calls ExitApp only after its helper has been started. Any other
            // update stage can still be abandoned, but the user should confirm it.
            if (UpdateBusy() && _updateStage != UpdateStage.Applying)
            {
                DialogResult updateAnswer;
                _exitPromptOpen = true;
                BeginModal();
                try
                {
                    updateAnswer = MessageBox.Show(
                        L.T("update.busyBody"),
                        L.T("update.busyTitle"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                }
                finally
                {
                    EndModal();
                    _exitPromptOpen = false;
                }
                if (updateAnswer != DialogResult.Yes)
                {
                    if (restart)
                        Notify(L.T("notify.restartCanceled"), L.T("notify.restartCanceledBody"));
                    return false;
                }
            }

            // Aborting midway leaves a half-installed global package: ask first, and kill npm if confirmed
            if (_phase == Phase.Installing)
            {
                DialogResult answer;
                _exitPromptOpen = true;
                BeginModal();
                try
                {
                    answer = MessageBox.Show(
                        L.T("exit.installingBody"),
                        L.T("exit.installingTitle"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                }
                finally
                {
                    EndModal();
                    _exitPromptOpen = false;
                }
                if (answer != DialogResult.Yes)
                {
                    if (restart)
                        Notify(L.T("notify.restartCanceled"), L.T("notify.restartCanceledBody"));
                    return false;
                }
                Installer.Abort();
                CloseInstallForm();
            }

            // Start the replacement only after update/install confirmation, so a
            // cancelled prompt never leaves an invisible child waiting for a lock.
            if (restart && !StartRestartProcess()) return false;

            _exiting = true;
            _timer.Stop();
            // ExitApp pumps the message loop while it waits for the service. Hide the
            // tray and disable the menu first so a queued click cannot start a new
            // service while the old one is being stopped.
            try { _tray.Visible = false; } catch (Exception) { }
            try { _menu.Enabled = false; } catch (Exception) { }

            UpdateCancellation updateCancellation;
            lock (_updateLock) updateCancellation = _updateCancellation;
            if (updateCancellation != null) updateCancellation.Cancel();

            try
            {
                // Exiting right after starting: kill the tree instead of waiting for it to come up
                if (_phase == Phase.Starting && _server != null)
                {
                    try { if (!_server.HasExited) Sys.KillTree(_server.Id); } catch (Exception) { }
                }

                if (Net.IsPortOpen(_port, 200))
                {
                    // Synchronous on purpose: this is the exit path, correctness of the
                    // stop/kill decision beats responsiveness, and a frozen window that
                    // is already closing is not user-visible.
                    ServiceProbe.Invalidate();
                    PortOwnerInfo owner = ServiceProbe.Inspect(_port, ExpectedPiWebScript());
                    bool ownedByTray = TrayOwnsPort(owner);
                    bool ourPiWeb = IsOurPiWeb(owner);
                    if ((owner.IsUnknown || owner.Owner == PortOwner.None
                        || (owner.IsPiWeb && !ourPiWeb)) && !ownedByTray)
                    {
                        // Another program owns the port. Leave it alone and keep the
                        // exit fast instead of waiting for a service that is not ours.
                        Updater.AppendLog(_config.UpdateLogFile,
                            "exit: port " + _port + " is used by another program; not stopped");
                    }
                    else
                    {
                        // ExitApp stops the timer before this point, so StopServer's
                        // asynchronous owner-check continuation would never run. Use the
                        // synchronous owner result we already have and start the stop worker
                        // directly; otherwise the update helper could restart a new tray
                        // while the old pi-web still owns the port.
                        ContinueStopServer(owner);
                        // Wait until it is really stopped, keeping the message loop alive meanwhile
                        int waited = 0;
                        while (waited < 8000 && Net.IsPortOpen(_port, 200))
                        {
                            Thread.Sleep(120);
                            Application.DoEvents();
                            waited += 120;
                        }
                        if (Net.IsPortOpen(_port, 200))
                        {
                            ServiceProbe.Invalidate();
                            owner = ServiceProbe.Inspect(_port, ExpectedPiWebScript());
                            if (IsOurPiWeb(owner) || TrayOwnsPort(owner))
                            {
                                if (owner.Pid > 4) Sys.KillTree(owner.Pid);
                            }
                            else
                            {
                                Updater.AppendLog(_config.UpdateLogFile,
                                    "exit: port " + _port + " changed owner; not killed");
                            }
                        }
                        Net.WaitPortClosed(_port, 2000);
                    }
                }
            }
            catch (Exception)
            {
            }

            // Let a running `pi-web stop` worker release the package directory before
            // the process exits. ProcessRunner allows the stop child up to 20 seconds
            // plus its kill/flush grace, so the wait must cover the whole worker.
            WaitForStopWorker(30000);

            // Normal exit means "stop the service". If the wrapper is still alive
            // while the port is already closed (a hung start or a detached child),
            // kill it here so it cannot become an orphan holding the npm package
            // directory or a port. Wait only on the final exit path.
            KillTrackedServer(true);

            // The helper waits for this process to exit and, when a gate is present,
            // for this explicit confirmation. Signal it only after all exit guards
            // and the service stop have succeeded. A blocked ExitApp never reaches
            // this point, so a later normal exit cannot apply the cancelled update.
            if (_exitForUpdateApply) SignalUpdateHelperGate();

            TrayState.Delete();
            Cleanup();
            ExitThread();
            return true;
        }

        // First start without Node.js: leave any other pi-web instance alone and just
        // shut down the tray that never managed to start its own service.
        private void ExitWithoutStoppingService(bool stopTrackedService)
        {
            if (_exiting) return;
            // An elevated --tray-only takeover is not allowed to abandon a running
            // update helper. ExitApp has the same guard; this path previously bypassed
            // it and could leave the helper waiting for a gate that never arrives.
            if (UpdateBusy() && _updateStage == UpdateStage.Applying)
            {
                NotifyWarning(L.T("update.busyTitle"), L.T("update.exitDuringApplyBody"));
                return;
            }
            _exiting = true;
            try { _timer.Stop(); } catch (Exception) { }

            // Keep an external/older service alive, but never leave a service that
            // this tray started behind a prompt. The elevated --tray-only takeover
            // passes false so the previous tray's service is preserved.
            if (stopTrackedService) KillTrackedServer();

            // Keep the service alive, but do not orphan an npm install or an update
            // download when another process takes over the tray (for example an
            // elevated --tray-only instance).
            if (_phase == Phase.Installing || _installForm != null)
            {
                Installer.Abort();
                CloseInstallForm();
            }
            UpdateCancellation cancellation;
            lock (_updateLock) cancellation = _updateCancellation;
            if (cancellation != null) cancellation.Cancel();

            WaitForStopWorker(30000);
            TrayState.Delete();
            Cleanup();
            ExitThread();
        }

        private void WaitForStopWorker(int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (_stopWorkerRunning && DateTime.Now < deadline)
            {
                Thread.Sleep(100);
                try { Application.DoEvents(); } catch (Exception) { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cleanup();
                // Keep the single-instance mutex until the message loop has really
                // exited. Releasing it in Cleanup() let a --restart child acquire
                // the mutex while the old process was still finishing ExitThread.
                ReleaseMutexHandle();
            }
            base.Dispose(disposing);
        }

        private void ReleaseMutexHandle()
        {
            if (_mutex == null) return;
            try { _mutex.ReleaseMutex(); } catch (Exception) { }
            try { _mutex.Close(); } catch (Exception) { }
        }

        private void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            // The watcher is a background thread and is never joined here: joining could
            // block the UI while it is finishing a registry or filesystem read.
            StopNodePresenceMonitor();
            try { _timer.Stop(); _timer.Dispose(); } catch (Exception) { }
            _notifications.Clear();
            _currentNotification = null;
            CloseInstallForm();
            CloseUpdateForm();
            ReleaseUpdateHelperGate();
            ReleaseInstallUpdateLock();
            // A still-running stop worker owns StopName. Closing it here would let a
            // waiting CLI install start npm before the `pi-web stop` child has
            // released the package directory. The worker releases the lock itself.
            if (!_stopWorkerRunning) ReleaseStopWorkerLock();
            try { _tray.Visible = false; _tray.Dispose(); } catch (Exception) { }
            try { _menu.Dispose(); } catch (Exception) { }
            try { _openSignal.Close(); } catch (Exception) { }
            try { _startSignal.Close(); } catch (Exception) { }
            try { _stopSignal.Close(); } catch (Exception) { }
            try { _installStopSignal.Close(); } catch (Exception) { }
            try { _quitSignal.Close(); } catch (Exception) { }
            try { _quitKeepServiceSignal.Close(); } catch (Exception) { }
            try { Icons.DisposeCache(); } catch (Exception) { }
            try { if (_server != null) _server.Dispose(); } catch (Exception) { }
        }

        private void CleanupStaleUpdateBackups()
        {
            string target = _config.BaseDir;
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    // Never delete a backup while an update helper owns the installation.
                    using (OperationLock updateLock = OperationLock.TryAcquire(
                        OperationLock.UpdateName(target)))
                    {
                        if (updateLock == null) return;
                        Updater.DeleteStaleBackups(target, TimeSpan.FromSeconds(30));
                        Updater.DeleteStaleStagingSnapshots(target, TimeSpan.FromSeconds(30));
                    }
                }
                catch (Exception) { }
            });
            worker.IsBackground = true;
            worker.Name = "pi-web-tray backup cleanup";
            try { worker.Start(); } catch (Exception) { }
        }

        private void RotateLog()
        {
            try
            {
                if (PathSafety.ContainsReparsePoint(_config.LogFile)) return;
                FileInfo info = new FileInfo(_config.LogFile);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    string backup = _config.LogFile + ".1";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(_config.LogFile, backup);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
