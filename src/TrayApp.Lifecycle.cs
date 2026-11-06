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
            if (_exiting) return false;

            // The updater calls ExitApp only after its helper has been started. Any other
            // update stage can still be abandoned, but the user should confirm it.
            if (UpdateBusy() && _updateStage != UpdateStage.Applying)
            {
                DialogResult updateAnswer = MessageBox.Show(
                    L.T("update.busyBody"),
                    L.T("update.busyTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
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
                DialogResult answer = MessageBox.Show(
                    L.T("exit.installingBody"),
                    L.T("exit.installingTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
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

            TrayState.Delete();
            Cleanup();
            ExitThread();
            return true;
        }

        // First start without Node.js: leave any other pi-web instance alone and just
        // shut down the tray that never managed to start its own service.
        private void ExitWithoutStoppingService()
        {
            if (_exiting) return;
            _exiting = true;
            try { _timer.Stop(); } catch (Exception) { }

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

            TrayState.Delete();
            Cleanup();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cleanup();
            base.Dispose(disposing);
        }

        private void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            // The watcher is a background thread and is never joined here: joining could
            // block the UI while it is finishing a registry or filesystem read.
            StopNodePresenceMonitor();
            try { _timer.Stop(); _timer.Dispose(); } catch (Exception) { }
            CloseInstallForm();
            CloseUpdateForm();
            try { _tray.Visible = false; _tray.Dispose(); } catch (Exception) { }
            try { _menu.Dispose(); } catch (Exception) { }
            try { _openSignal.Close(); } catch (Exception) { }
            try { _startSignal.Close(); } catch (Exception) { }
            try { _stopSignal.Close(); } catch (Exception) { }
            try { _quitSignal.Close(); } catch (Exception) { }
            try { _quitKeepServiceSignal.Close(); } catch (Exception) { }
            try { Icons.DisposeCache(); } catch (Exception) { }
            try { if (_server != null) _server.Dispose(); } catch (Exception) { }
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch (Exception) { }
                try { _mutex.Close(); } catch (Exception) { }
            }
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
