// Pi Web Tray — service lifecycle and port ownership
//
// Partial of TrayApp. Kept separate so the tray state machine stays readable.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed partial class TrayApp
    {
        // ---------------- port ownership probe (background -> UI thread) ----------------

        // ServiceProbe.Inspect can take seconds: a netstat child process, then a 1500 ms
        // HTTP fallback that fires exactly when a privileged or foreign process holds the
        // port. Running it on the UI thread froze the tray in the PortConflict steady
        // state, so the poll loop follows the same worker + lock + done pattern as
        // BeginTaskRefresh/PollTaskRefresh: request here, consume the finished result.
        private static PortOwnerInfo OwnerCheckFailed()
        {
            return new PortOwnerInfo
            {
                Owner = PortOwner.Unknown,
                CheckFailed = true,
                Detail = "port owner check failed"
            };
        }

        // A failed probe is not a port conflict. Notify once, keep the current
        // phase, and retry; killing a tracked service on an access/worker error
        // would turn a transient problem into a service outage.
        private bool HandleOwnerCheckFailure(PortOwnerInfo owner)
        {
            if (owner == null || !owner.CheckFailed) return false;
            NotifyWarningOnce("owner-check-failed",
                L.T("notify.ownerCheckFailedTitle"),
                L.T("notify.ownerCheckFailedBody", _port));
            return true;
        }

        private void BeginOwnerCheck()
        {
            int generation;
            lock (_ownerLock)
            {
                if (_ownerCheckRunning) return;
                _ownerCheckRunning = true;
                _ownerCheckDone = false;
                _ownerCheckResult = null;
                generation = ++_ownerCheckGeneration;
            }

            // A background owner check must never be satisfied by a cache entry that
            // was invalidated while it was in flight.
            ServiceProbe.Invalidate();
            int port = _port;
            string expectedScript = ExpectedPiWebScript();
            Thread worker = new Thread(delegate ()
            {
                PortOwnerInfo result = null;
                try { result = ServiceProbe.Inspect(port, expectedScript); }
                catch (Exception) { result = OwnerCheckFailed(); }
                if (result == null) result = OwnerCheckFailed();
                lock (_ownerLock)
                {
                    // An invalidate that arrived while this worker was running bumped
                    // the generation; a stale result would mislead a stop/kill decision,
                    // so drop it.
                    if (generation == _ownerCheckGeneration)
                    {
                        _ownerCheckResult = result;
                        _ownerCheckDone = true;
                    }
                    _ownerCheckRunning = false;
                }
            });
            worker.IsBackground = true;
            try { worker.Start(); }
            catch (Exception)
            {
                lock (_ownerLock)
                {
                    if (generation == _ownerCheckGeneration)
                    {
                        _ownerCheckResult = OwnerCheckFailed();
                        _ownerCheckDone = true;
                    }
                    _ownerCheckRunning = false;
                }
            }
        }

        // Returns the finished result, or null while a check has not finished yet. The
        // result slot is consumed exactly once.
        private PortOwnerInfo TakeOwnerCheck()
        {
            lock (_ownerLock)
            {
                if (!_ownerCheckDone) return null;
                _ownerCheckDone = false;
                PortOwnerInfo result = _ownerCheckResult;
                _ownerCheckResult = null;
                return result;
            }
        }

        // Drop a finished or in-flight result so the next check starts from scratch.
        // Used where a decision must be based on fresh data, mirroring Invalidate().
        private void InvalidateOwnerCheck()
        {
            lock (_ownerLock)
            {
                _ownerCheckGeneration++;
                _ownerCheckDone = false;
                _ownerCheckResult = null;
            }
        }

        // Deliver a finished owner check to the action that requested it. Runs before the
        // phase poll so a user action is never lost while a check is in flight. Continuation
        // entry points re-check the phase: it can change while the check runs.
        private void DispatchOwnerCheck()
        {
            if (_stopAfterOwnerCheck)
            {
                PortOwnerInfo owner = TakeOwnerCheck();
                if (owner == null) { BeginOwnerCheck(); return; }
                _stopAfterOwnerCheck = false;
                ContinueStopServer(owner);
                return;
            }
            if (_startAfterOwnerCheck)
            {
                PortOwnerInfo owner = TakeOwnerCheck();
                if (owner == null) { BeginOwnerCheck(); return; }
                bool open = _startAfterOwnerCheckOpen;
                _startAfterOwnerCheck = false;
                _startAfterOwnerCheckOpen = false;
                ContinueStartServer(owner, open);
                return;
            }
            if (_resolveAfterOwnerCheck)
            {
                PortOwnerInfo owner = TakeOwnerCheck();
                if (owner == null) { BeginOwnerCheck(); return; }
                _resolveAfterOwnerCheck = false;
                if (HandleOwnerCheckFailure(owner))
                {
                    UpdateUi();
                    return;
                }
                // A newer action may have started while this check was in flight.
                // Never overwrite the state machine for that action with stale phase data.
                if (_phase == Phase.Installing || _phase == Phase.Starting
                    || _phase == Phase.Stopping || _phase == Phase.Checking
                    || _phase == Phase.NeedsNode || _phase == Phase.NeedsDeps)
                {
                    _openWhenResolved = false;
                    return;
                }
                Phase previousPhase = _phase;
                _phase = PhaseFromOwner(owner);
                bool openResolved = _openWhenResolved;
                _openWhenResolved = false;
                if (_phase == Phase.Running)
                {
                    ClearUserStopMarkerOnObservedRun(previousPhase);
                    _runningSince = DateTime.Now;
                    _autoRestartAt = DateTime.MinValue;
                }
                else
                {
                    // A resolved Stopped/PortConflict state means the tracked wrapper
                    // is not serving this port. Do not leave it behind.
                    KillTrackedServer();
                }
                UpdateUi();
                if (_phase == Phase.Running && openResolved) OpenBrowser();
                if (_phase == Phase.PortConflict) NotifyPortConflict();
                return;
            }
            if (_installAfterOwnerCheck)
            {
                PortOwnerInfo owner = TakeOwnerCheck();
                if (owner == null) { BeginOwnerCheck(); return; }
                bool force = _installAfterOwnerCheckForce;
                bool start = _installAfterOwnerCheckStart;
                bool open = _installAfterOwnerCheckOpen;
                _installAfterOwnerCheck = false;
                _installAfterOwnerCheckForce = false;
                _installAfterOwnerCheckStart = false;
                _installAfterOwnerCheckOpen = false;
                ContinueBeginInstall(force, start, open, owner);
            }
        }

        // The phase implied by an owner result; shared by the poll loop and the resolve
        // continuation.
        private Phase PhaseFromOwner(PortOwnerInfo owner)
        {
            // A failed check carries no evidence about the listener. Keep the
            // current phase rather than turning it into PortConflict.
            if (owner.CheckFailed) return _phase;
            if (IsOurPiWeb(owner)) return Phase.Running;
            if ((owner.IsUnknown || owner.IsPiWeb) && TrayOwnsPort(owner)) return Phase.Running;
            if (owner.IsUnknown || owner.IsPiWeb) return Phase.PortConflict;
            return Phase.Stopped;
        }

        // ResolveServicePhase() without blocking the UI thread: the phase is applied when
        // the background owner check finishes (DispatchOwnerCheck).
        private void RequestResolveServicePhase(bool openWhenRunning = false)
        {
            // Preserve an earlier explicit open request. A routine background check
            // must not cancel the intent of a normal double-click while the owner
            // check is still in flight. Explicit stop/start/install actions clear it
            // through ClearPendingOwnerActions().
            if (openWhenRunning) _openWhenResolved = true;
            if (!Net.IsPortOpen(_port, 200))
            {
                _openWhenResolved = false;
                InvalidateOwnerCheck();
                _phase = Phase.Stopped;
                return;
            }

            // A listener exists, but its owner has not been verified yet. Do not claim
            // "Stopped" (which disables Open/Stop and can make a live service
            // unmanageable) and do not claim "Running" (which could be a foreign
            // listener). Use an explicit unverified state until the check completes.
            if (_phase == Phase.Checking || _phase == Phase.Stopped
                || _phase == Phase.Failed || _phase == Phase.NeedsNode
                || _phase == Phase.NeedsDeps)
            {
                _phase = Phase.RunningUnverified;
            }

            // Resolve is a one-shot phase decision; never reuse a completed result from
            // an earlier action. InvalidateOwnerCheck() clears the slot, so always
            // start a fresh background check instead of pretending to take a result.
            ServiceProbe.Invalidate();
            InvalidateOwnerCheck();
            _resolveAfterOwnerCheck = true;
            BeginOwnerCheck();
        }

        // Must mirror StartServer: a configured script wins only while it still exists;
        // otherwise the probe result is what the service will actually launch.
        private string ExpectedPiWebScript()
        {
            if (_probe != null && _probe.PiWebFound && !string.IsNullOrEmpty(_probe.PiWebScript))
                return _probe.PiWebScript;
            return "";
        }

        // The probe may use a configured path, a PATH lookup or a well-known install
        // location. Keep that effective path separate from Config.NodePath, which is
        // the user's persisted preference (usually empty for automatic detection).
        private string EffectiveNodePath()
        {
            if (_probe != null && !string.IsNullOrEmpty(_probe.NodePath)) return _probe.NodePath;
            return _config.NodePath ?? "";
        }

        // An elevated tray must not launch itself or user-installed packages from a
        // directory that standard users can replace. The manual elevation path and the
        // admin-autostart eligibility check normally prevent this, but an old task or a
        // directory that became writable after the task was created must fail closed too.
        private bool ElevatedInstallUnsafe()
        {
            if (!_elevated) return false;
            // Re-check at action time: the installation ACL can change after startup.
            try { return Autostart.IsUserWritableInstall(Application.ExecutablePath); }
            catch (Exception) { return true; }
        }

        // The CLI install path holds UpdateName for the whole stop/npm sequence.
        // Starting a service then would make npm race the package directory and
        // could also resurrect a service the CLI deliberately stopped.
        private bool ExternalUpdateBusy()
        {
            // This tray's own install holds the same lock on the UI thread. Do not
            // start a service while npm is still rewriting the global package tree.
            if (_installUpdateLock != null) return true;
            try
            {
                using (OperationLock probe = OperationLock.TryAcquire(
                    OperationLock.UpdateName(_config.BaseDir)))
                {
                    return probe == null;
                }
            }
            catch (Exception)
            {
                // An inaccessible lock is busy, not permission to start a competing process.
                return true;
            }
        }

        private bool RefuseStartForExternalUpdate()
        {
            if (!ExternalUpdateBusy()) return false;
            _openWhenReady = false;
            _openWhenResolved = false;
            _pendingRestart = false;
            _pendingOpenBrowser = false;
            NotifyWarningOnce("external-update", L.T("notify.installBusyTitle"),
                L.T("install.busyBody"));
            UpdateUi();
            return true;
        }

        // Drop requests that belong to an older user action. Stop-for-install preserves
        // the queued install continuation; every other explicit action owns the state now.
        private void ClearPendingOwnerActions(bool keepInstall)
        {
            _startAfterOwnerCheck = false;
            _startAfterOwnerCheckOpen = false;
            _stopAfterOwnerCheck = false;
            _resolveAfterOwnerCheck = false;
            _openWhenResolved = false;
            if (keepInstall) return;

            _installAfterOwnerCheck = false;
            _installAfterOwnerCheckForce = false;
            _installAfterOwnerCheckStart = false;
            _installAfterOwnerCheckOpen = false;
        }

        // A pi-web listener is ours only when its script path matches the installed one.
        // Another installation (or another user session) must be left alone.
        private bool IsOurPiWeb(PortOwnerInfo owner)
        {
            return ServiceProbe.MatchesExpectedScript(owner, ExpectedPiWebScript());
        }

        private void PollStarting()
        {
            bool dead = false;
            try { dead = _server != null && _server.HasExited; } catch (Exception) { }
            if (dead)
            {
                FailStart(L.T("notify.startFailedExited"));
                return;
            }

            if (Net.IsPortOpen(_port, 200))
            {
                // Make sure the listener is actually our pi-web process before accepting
                // the HTTP response. This prevents another program on the same port from
                // being marked as a successful start. The check runs in the background;
                // this tick only consumes a finished result.
                PortOwnerInfo owner = TakeOwnerCheck();
                if (owner == null)
                {
                    BeginOwnerCheck();
                    return;
                }
                if (HandleOwnerCheckFailure(owner))
                {
                    if (DateTime.Now > _startDeadline)
                    {
                        // We started this process tree and the port is accepting
                        // connections. A transient inability to read the owner is not
                        // proof that the service is broken. Keep it alive in an
                        // explicit unverified state instead of killing it.
                        bool trackedAlive = false;
                        try { trackedAlive = _server != null && !_server.HasExited; }
                        catch (Exception) { }
                        if (trackedAlive)
                        {
                            _phase = Phase.RunningUnverified;
                            _openWhenReady = false;
                            _pendingRestart = false;
                            _autoRestartAt = DateTime.MinValue;
                            UpdateUi();
                        }
                        else
                        {
                            FailStart(L.T("notify.startFailedTimeout", _port));
                        }
                    }
                    return;
                }
                if (!IsOurPiWeb(owner) && !TrayOwnsPort(owner))
                {
                    if (DateTime.Now > _startDeadline)
                        FailStart(L.T("notify.startFailedTimeout", _port));
                    else if (owner.IsUnknown || owner.IsPiWeb)
                    {
                        // A listener that is not ours took the port while we were
                        // starting. Do not keep the orange Starting state for three
                        // minutes; report the conflict immediately and release our
                        // process tree.
                        KillTrackedServer();
                        _phase = Phase.PortConflict;
                        _openWhenReady = false;
                        _pendingRestart = false;
                        _autoRestartAt = DateTime.MinValue;
                        UpdateUi();
                        NotifyPortConflict();
                    }
                    return;
                }

                // The port answers, but the web app may not be ready yet: confirm with a real HTTP request
                int http;
                lock (_readyLock) http = _readyResult;

                if (http == 0)
                {
                    BeginReadyCheck();
                    return;
                }
                if (http < 0)
                {
                    // Not ready yet (or a transient failure): reset and try again until the deadline
                    lock (_readyLock) _readyResult = 0;
                    return;
                }

                _phase = Phase.Running;
                _lastNotified = "";
                _lastOwnerCheck = DateTime.Now;
                _autoRestartAt = DateTime.MinValue;
                _runningSince = DateTime.Now;
                _restartAfterUpdate = false;
                bool open = _openWhenReady;
                _openWhenReady = false;
                UpdateUi();
                if (open) OpenBrowser();
                Notify(L.T("notify.readyTitle"), "http://127.0.0.1:" + _port, delegate { OpenBrowser(); });
                return;
            }

            if (DateTime.Now > _startDeadline)
                FailStart(L.T("notify.startFailedTimeout", _port));
        }

        // Kill and release the process tree this tray started. Used when the
        // wrapper is still alive even though the service no longer listens (a
        // failed startup, a hung node process or a detached child). Without this
        // a later Start would overwrite _server and leave a second process tree.
        // The process-exit wait is opt-in: the timer/UI paths must not block for
        // seconds waiting for taskkill, while the final exit path does need it.
        private void KillTrackedServer()
        {
            KillTrackedServer(false);
        }

        private void KillTrackedServer(bool waitForExit)
        {
            Process server = _server;
            _server = null;
            if (server == null) return;

            try
            {
                if (!server.HasExited)
                {
                    Sys.KillTree(server.Id);
                    if (waitForExit)
                    {
                        try { server.WaitForExit(3000); } catch (Exception) { }
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                try { server.Dispose(); } catch (Exception) { }
            }
        }

        private void FailStart(string reason)
        {
            ClearPendingOwnerActions(false);
            InvalidateOwnerCheck();
            _phase = Phase.Failed;
            _openWhenReady = false;
            _pendingRestart = false;
            KillTrackedServer();
            UpdateUi();
            NotifyError(L.T("notify.startFailedTitle"), L.T("notify.startFailedBody", reason),
                delegate { ShowLog(_config.LogFile, L.T("log.serverTitle")); });
            ScheduleAutoRestart();
        }

        private void PollStopping()
        {
            if (!Net.IsPortOpen(_port, 200))
            {
                // The stop command can still hold the package directory after the port
                // closes. Do not start npm or a restart until its worker has exited.
                if (_stopWorkerRunning) return;

                ServiceProbe.Invalidate();
                InvalidateOwnerCheck();
                _phase = Phase.Stopped;
                UpdateUi();
                if (_installAfterStop)
                {
                    ContinueInstallAfterStop();
                    return;
                }
                if (_pendingRestart)
                {
                    _pendingRestart = false;
                    bool open = _pendingOpenBrowser;
                    _pendingOpenBrowser = false;
                    StartServer(open);
                }
                return;
            }

            if (DateTime.Now > _stopDeadline)
            {
                // The CLI had its chance and the service is still up. Only force-kill a
                // confirmed pi-web process (or the process this tray started); never kill
                // an unknown listener that took the port.
                if (_stopEscalatedAt == DateTime.MinValue)
                {
                    PortOwnerInfo owner = TakeOwnerCheck();
                    if (owner == null)
                    {
                        // The decision to kill must be based on fresh data, but never on the
                        // UI thread; the result is consumed on a later tick.
                        BeginOwnerCheck();
                        return;
                    }
                    if (HandleOwnerCheckFailure(owner))
                    {
                        // No evidence that the listener may be killed. Stay in Stopping
                        // for a few retries instead of claiming a port conflict. If the
                        // process probe remains unavailable, leave the intermediate state:
                        // otherwise Stop/Restart stay disabled and the tray is stuck.
                        _stopOwnerCheckFailures++;
                        _pendingRestart = false;
                        _pendingOpenBrowser = false;
                        AbortInstallAfterStop(false);
                        if (_stopOwnerCheckFailures >= 3)
                        {
                            _stopOwnerCheckFailures = 0;
                            KillTrackedServer();
                            _phase = Phase.Failed;
                            UpdateUi();
                            NotifyError(L.T("notify.stopFailedTitle"),
                                L.T("notify.stopFailedBody", _port));
                        }
                        return;
                    }
                    _stopOwnerCheckFailures = 0;
                    if (IsOurPiWeb(owner) || TrayOwnsPort(owner))
                    {
                        _stopEscalatedAt = DateTime.Now;
                        if (owner.Pid > 4) Sys.KillTree(owner.Pid);
                        ServiceProbe.Invalidate();
                    }
                    else
                    {
                        _phase = Phase.PortConflict;
                        _pendingRestart = false;
                        _pendingOpenBrowser = false;
                        AbortInstallAfterStop(false);
                        UpdateUi();
                        NotifyPortConflict();
                    }
                }
                else if (DateTime.Now > _stopEscalatedAt.AddSeconds(6))
                {
                    // The kill may still not have closed the port (a privileged listener,
                    // or the pid was reused). Re-derive the phase from the real owner
                    // instead of hard-coding Running, which would show a green icon while
                    // someone else's process holds the port. Leave Stopping first so the
                    // asynchronous resolve continuation is allowed to replace the phase.
                    _phase = Phase.Stopped;
                    RequestResolveServicePhase();
                    _pendingRestart = false;
                    _pendingOpenBrowser = false;
                    AbortInstallAfterStop(false);
                    UpdateUi();
                    NotifyError(L.T("notify.stopFailedTitle"), L.T("notify.stopFailedBody", _port));
                }
            }
        }

        private void PollPortSync()
        {
            bool actuallyRunning = Net.IsPortOpen(_port, 200);

            if (!actuallyRunning)
            {
                ServiceProbe.Invalidate();
                InvalidateOwnerCheck();
                _openWhenResolved = false;
                if (_phase == Phase.Running)
                {
                    // The listener is gone. The wrapper (cmd) or a detached node
                    // child can still be alive; keep it from becoming an orphan.
                    KillTrackedServer();
                    _phase = Phase.Stopped;
                    UpdateUi();
                    NotifyWarningOnce("service-stopped",
                        L.T("notify.serviceStoppedTitle"),
                        L.T("notify.serviceStoppedBody"),
                        delegate { StartServer(false); });
                    if (UpdateBusy() && _config.AutoRestart
                        && CanStartServiceFromProbe(_probe, File.Exists))
                    {
                        // The update owns the busy flag; remember the crash and schedule
                        // the restart after the update check/download has finished.
                        _restartAfterUpdate = true;
                    }
                    ScheduleAutoRestart();
                }
                else if (_phase == Phase.RunningUnverified)
                {
                    // The unverified listener is gone. There is no proof that it was
                    // ours, so do not report a service crash or auto-restart it.
                    KillTrackedServer();
                    _phase = Phase.Stopped;
                    _lastNotified = "";
                    UpdateUi();
                }
                else if (_phase == Phase.PortConflict)
                {
                    KillTrackedServer();
                    _phase = Phase.Stopped;
                    _lastNotified = "";
                    UpdateUi();
                }
                else
                {
                    // In any stable phase a closed port means a still-live wrapper is
                    // stale (for example after a probe reclassified the service while
                    // a previous process was still finishing). Release it.
                    KillTrackedServer();
                }
                return;
            }

            if (_phase == Phase.RunningUnverified)
            {
                // Retry the owner check promptly, but not on every tick.
                if (DateTime.Now - _lastOwnerCheck < TimeSpan.FromSeconds(10)) return;
                PortOwnerInfo current = TakeOwnerCheck();
                if (current == null)
                {
                    BeginOwnerCheck();
                    return;
                }
                if (HandleOwnerCheckFailure(current))
                {
                    _lastOwnerCheck = DateTime.Now;
                    return;
                }
                _lastOwnerCheck = DateTime.Now;
                Phase previousPhase = _phase;
                _phase = PhaseFromOwner(current);
                bool openResolved = _openWhenResolved;
                _openWhenResolved = false;
                if (_phase == Phase.Running)
                {
                    ClearUserStopMarkerOnObservedRun(previousPhase);
                    _lastNotified = "";
                    _autoRestartAt = DateTime.MinValue;
                    _runningSince = DateTime.Now;
                    _restartAfterUpdate = false;
                }
                else
                {
                    KillTrackedServer();
                }
                UpdateUi();
                if (_phase == Phase.Running && openResolved) OpenBrowser();
                if (_phase == Phase.PortConflict) NotifyPortConflict();
                return;
            }
            if (_phase == Phase.Running)
            {
                // A service that is already listening does not need a pending restart;
                // clear a timer that was armed before the service recovered on its own.
                _autoRestartAt = DateTime.MinValue;
                // Re-verify occasionally so a different listener taking the port does not
                // stay green forever. Critical stop/exit paths also invalidate the cache.
                // _lastOwnerCheck is only advanced when a result is consumed, so an
                // in-flight check is retried on the next tick instead of being dropped.
                if (DateTime.Now - _lastOwnerCheck < TimeSpan.FromSeconds(30)) return;
                PortOwnerInfo current = TakeOwnerCheck();
                if (current == null)
                {
                    BeginOwnerCheck();
                    return;
                }
                if (HandleOwnerCheckFailure(current))
                {
                    // Retry after the normal interval instead of killing a
                    // service whose owner cannot be read right now.
                    _lastOwnerCheck = DateTime.Now;
                    return;
                }
                _lastOwnerCheck = DateTime.Now;
                if (IsOurPiWeb(current)) return;
                // The command line can be unreadable while cmd still owns the tree;
                // the tracked child is enough evidence in that case.
                if ((current.IsUnknown || current.IsPiWeb) && TrayOwnsPort(current)) return;

                // The listener is not ours. Release any stale wrapper before
                // switching to the conflict/stopped state.
                KillTrackedServer();
                _phase = (current.IsUnknown || current.IsPiWeb) ? Phase.PortConflict : Phase.Stopped;
                UpdateUi();
                if (_phase == Phase.PortConflict) NotifyPortConflict();
                return;
            }
            if (_phase != Phase.Stopped && _phase != Phase.Failed && _phase != Phase.NeedsDeps
                && _phase != Phase.NeedsNode && _phase != Phase.PortConflict)
            {
                return;
            }

            PortOwnerInfo owner = TakeOwnerCheck();
            if (owner == null)
            {
                BeginOwnerCheck();
                return;
            }
            if (HandleOwnerCheckFailure(owner)) return;
            if (IsOurPiWeb(owner)
                || ((owner.IsUnknown || owner.IsPiWeb) && TrayOwnsPort(owner)))
            {
                Phase previousPhase = _phase;
                _phase = Phase.Running;
                ClearUserStopMarkerOnObservedRun(previousPhase);
                bool openResolved = _openWhenResolved;
                _openWhenResolved = false;
                _lastNotified = "";
                _lastOwnerCheck = DateTime.Now;
                _autoRestartAt = DateTime.MinValue;
                _runningSince = DateTime.Now;
                _restartAfterUpdate = false;
                UpdateUi();
                if (openResolved) OpenBrowser();
            }
            else if (owner.IsUnknown || owner.IsPiWeb)
            {
                if (_phase != Phase.PortConflict)
                {
                    KillTrackedServer();
                    _phase = Phase.PortConflict;
                    UpdateUi();
                    NotifyPortConflict();
                }
            }
            else if (_phase == Phase.PortConflict)
            {
                _phase = Phase.Stopped;
                UpdateUi();
            }
        }

        private void ClearUserStopMarkerOnObservedRun(Phase previousPhase)
        {
            // A user stop that failed leaves the phase Failed/PortConflict; keep the
            // intent in that case. Once the state machine has observed a stopped or
            // dependency-missing state and a listener later appears, the old stop
            // marker is stale and must not make an update leave the running service down.
            if (previousPhase == Phase.Stopped || previousPhase == Phase.Checking
                || previousPhase == Phase.NeedsDeps || previousPhase == Phase.NeedsNode
                || previousPhase == Phase.PortConflict
                || previousPhase == Phase.RunningUnverified)
            {
                _serviceStoppedByUser = false;
            }
        }

        private bool TrayOwnsPort(PortOwnerInfo owner)
        {
            if (owner == null || _server == null) return false;
            try
            {
                if (_server.HasExited) return false;
            }
            catch (Exception)
            {
                return false;
            }

            if (IsOurPiWeb(owner)) return true;
            if (owner.Pid <= 0) return false;
            return IsDescendantOf(owner.Pid, _server.Id);
        }

        private static bool IsDescendantOf(int pid, int rootPid)
        {
            if (rootPid <= 0) return false;
            for (int depth = 0; depth < 12 && pid > 4; depth++)
            {
                if (pid == rootPid) return true;
                pid = ServiceProbe.ParentPid(pid);
            }
            return false;
        }

        private void NotifyPortConflict()
        {
            NotifyWarningOnce("port-conflict",
                L.T("notify.portConflictTitle"),
                L.T("notify.portConflictBody", _port),
                delegate { ShowSettings(); });
        }

        // A probe can report missing Pi (or even missing Node.js) while the
        // already loaded service keeps listening. Do not downgrade a live service
        // to NeedsDeps/NeedsNode: that would disable Stop and turn Open into an
        // install action. Keep Running and let the owner check decide whether the
        // listener is still ours.
        private bool KeepRunningWhenListening(bool openBrowser)
        {
            // A probe can report missing Node/Pi while a service started earlier is
            // still listening. Do not downgrade that live service to Failed/Needs*
            // just because this check did not run in the Running phase.
            if (!Net.IsPortOpen(_port, 200))
            {
                _openWhenResolved = false;
                return false;
            }

            // RequestResolveServicePhase owns the pending-open flag until the
            // background owner check proves that the listener is ours.
            RequestResolveServicePhase(openBrowser);
            UpdateUi();
            if (_phase == Phase.PortConflict) NotifyPortConflict();
            return true;
        }

        private void ApplyProbeResult(ProbeResult result)
        {
            if (result == null) return;
            if (ElevatedInstallUnsafe())
            {
                _phase = Phase.Failed;
                UpdateUi();
                NotifyWarningOnce("elevated-writable-install",
                    L.T("notify.elevatedWritableTitle"),
                    L.T("notify.elevatedWritableBody"));
                return;
            }
            // Closing an environment report can race a service action. Do not let the
            // report overwrite Starting/Stopping/Installing; the next probe will refresh it.
            if (IsServiceActionInProgress()) return;

            _probe = result;
            _nodePresenceKnown = true;
            _nodePresent = result.NodeFound;

            if (KeepRunningWhenListening(false)) return;

            if (result.NeedsNode)
            {
                _phase = Phase.NeedsNode;
            }
            else
            {
                _nodePresenceNotified = false;
                // Keep this in sync with ApplyCheckResult: a missing Pi is an
                // environment problem too, not only a missing Pi Web package.
                if (result.NeedsPiWeb || result.NeedsPiCli) _phase = Phase.NeedsDeps;
                else RequestResolveServicePhase();
            }

            UpdateUi();
            if (_phase == Phase.PortConflict) NotifyPortConflict();
        }

        private void BeginReadyCheck()
        {
            int generation;
            lock (_readyLock)
            {
                if (_readyCheckRunning) return;
                _readyCheckRunning = true;
                generation = _readyGeneration;
            }

            Thread worker = new Thread(delegate ()
            {
                bool ok = Net.IsHttpOk("http://127.0.0.1:" + _port + "/", 3000);
                lock (_readyLock)
                {
                    if (generation != _readyGeneration) return;
                    _readyResult = ok ? 1 : -1;
                    _readyCheckRunning = false;
                }
            });
            worker.IsBackground = true;
            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                // Leave _readyCheckRunning set: retrying a failing thread start in a
                // tight tick loop only spins. The start deadline will fail the start.
                try
                {
                    Updater.AppendLog(_config.UpdateLogFile,
                        "could not start readiness check: " + ex.Message);
                }
                catch (Exception) { }
            }
        }

        private void ScheduleAutoRestart()
        {
            if (!_config.AutoRestart || _exiting || UpdateBusy()) return;
            if (!CanStartServiceFromProbe(_probe, File.Exists)) return;
            if (_phase == Phase.Installing || _phase == Phase.Starting || _phase == Phase.Stopping) return;

            // A service that only stayed up briefly is the same failure, not a success:
            // only a run that lasted long enough earns a fresh retry budget. Otherwise a
            // run-then-die service would be restarted forever at the 5 s interval.
            if (_runningSince != DateTime.MinValue
                && DateTime.Now - _runningSince >= TimeSpan.FromSeconds(StableRunSeconds))
            {
                _autoRestartAttempts = 0;
            }
            _runningSince = DateTime.MinValue;

            if (_autoRestartAt != DateTime.MinValue) return;
            if (_autoRestartAttempts >= 3) return;

            _autoRestartAttempts++;
            int delay = _autoRestartAttempts == 1 ? 5 : (_autoRestartAttempts == 2 ? 15 : 30);
            _autoRestartAt = DateTime.Now.AddSeconds(delay);
            NotifyWarning(L.T("notify.autoRestartTitle"), L.T("notify.autoRestartBody", delay));
        }

        // If a child process cannot be created (security software, a broken image, or a
        // policy), fall back to cmd redirection instead of making the service depend on a
        // broken pipe. Log rotation is then limited to service start.
        private bool LogHostAvailable()
        {
            if (ElevatedInstallUnsafe()) return false;
            try
            {
                ProcessResult check = ProcessRunner.Run(
                    Application.ExecutablePath,
                    "--log-host-check " + Shell.Quote(_config.LogFile),
                    Sys.SystemDirectory,
                    3000,
                    null,
                    null);
                return check.Started && !check.TimedOut && check.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Probed once in the background together with the environment check: the 3 s
        // self-probe must not run on the UI thread on every service start. A failed
        // first probe is kept: a log-host child that could not be created once is
        // unlikely to become creatable, and the 1>> fallback keeps the service startable.
        private bool LogHostAvailableCached()
        {
            int state = _logHostState;
            if (state == 1) return true;
            // A missing result means the initial environment check has not completed yet.
            // Use the compatibility path for this start rather than running the 3 s
            // self-probe on the UI thread. The next service start can use the cached result.
            return false;
        }

        private void StartServer(bool openBrowser)
        {
            if (_exiting) return;
            // The update helper is replacing the executable and has already stopped
            // the service. An external --start-no-open signal or a queued action must
            // not launch the old binary again during that window.
            if (_updateStage == UpdateStage.Applying) return;
            if (RefuseStartForExternalUpdate()) return;
            if (ElevatedInstallUnsafe())
            {
                NotifyWarningOnce("elevated-writable-install",
                    L.T("notify.elevatedWritableTitle"),
                    L.T("notify.elevatedWritableBody"));
                _phase = Phase.Failed;
                _openWhenReady = false;
                _pendingRestart = false;
                UpdateUi();
                return;
            }
            // A Node.js upgrade changed the effective paths while the service was
            // running. Refresh the probe before launching a new process; the live
            // service was deliberately left in Running.
            if (_nodeProbeStale && _phase != Phase.Running && _phase != Phase.Starting
                && _phase != Phase.Stopping && _phase != Phase.Installing
                && _phase != Phase.Checking)
            {
                _nodeProbeStale = false;
                BeginEnvironmentCheck(true, openBrowser);
                return;
            }
            // A start request during an environment probe belongs to that probe.
            // Let the in-flight result decide (and start only once), instead of
            // racing it with a second node process.
            if (_phase == Phase.Checking)
            {
                // This is an explicit start request, even while the probe is in
                // flight. It must override a previous deliberate stop; otherwise a
                // later update could think the user still wants the service down.
                _serviceStoppedByUser = false;
                bool deferred = false;
                lock (_checkLock)
                {
                    if (_checkRunning || _checkDone)
                    {
                        _checkThenStart = true;
                        _checkOpenBrowser = _checkOpenBrowser || openBrowser;
                        deferred = true;
                    }
                }
                if (deferred) return;
                BeginEnvironmentCheck(true, openBrowser);
                return;
            }

            // A request that cannot be executed yet must not mutate the current user
            // intent or discard another pending action. In particular, a start signal
            // during Stopping used to clear _serviceStoppedByUser even though the start
            // itself was dropped, which later confused the update restart decision.
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;

            ClearPendingOwnerActions(false);
            InvalidateOwnerCheck();

            // A new explicit start overrides an earlier deliberate stop. This matters
            // when an update is still downloading: a user stop must not be undone by
            // the update's auto-restart logic, while a later explicit start should be.
            _serviceStoppedByUser = false;

            // The fingerprint watcher can see node.exe disappear while a running service
            // is still using the already-loaded image. Re-probe before creating the next
            // process instead of attempting a start that can only fail.
            if (_nodePresenceKnown && !_nodePresent)
            {
                BeginEnvironmentCheck(true, openBrowser);
                return;
            }

            // A start signal can arrive while the tray is sitting in NeedsNode. Do not
            // launch node with a version that failed the minimum-version check.
            if (_probe != null && _probe.NeedsNode)
            {
                BeginEnvironmentCheck(true, openBrowser);
                return;
            }

            if (Net.IsPortOpen(_port, 300))
            {
                ServiceProbe.Invalidate();
                InvalidateOwnerCheck();
                PortOwnerInfo owner = TakeOwnerCheck();
                if (owner == null)
                {
                    // The ownership check (netstat child process plus the HTTP fallback)
                    // can take seconds; run it in the background and continue when the
                    // result arrives instead of freezing the UI thread.
                    _startAfterOwnerCheck = true;
                    _startAfterOwnerCheckOpen = openBrowser;
                    BeginOwnerCheck();
                    return;
                }
                ContinueStartServer(owner, openBrowser);
                return;
            }
            ContinueStartServer(null, openBrowser);
        }

        private void ContinueStartServer(PortOwnerInfo owner, bool openBrowser)
        {
            // The phase may have changed while the owner check ran
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;
            // An install can acquire the lock while the owner probe is in flight.
            if (RefuseStartForExternalUpdate()) return;
            // Re-check at the last moment: the installation ACL can change while the
            // background owner probe is running.
            if (ElevatedInstallUnsafe())
            {
                NotifyWarningOnce("elevated-writable-install",
                    L.T("notify.elevatedWritableTitle"),
                    L.T("notify.elevatedWritableBody"));
                _phase = Phase.Failed;
                _openWhenReady = false;
                _pendingRestart = false;
                UpdateUi();
                return;
            }
            if (_probe != null && _probe.NeedsNode)
            {
                BeginEnvironmentCheck(true, openBrowser);
                return;
            }

            if (owner != null && owner.CheckFailed)
            {
                // We could not decide whether the listener is ours. Do not start
                // a second process and never kill the tracked wrapper here; a
                // retry will be driven by the next status poll.
                HandleOwnerCheckFailure(owner);
                return;
            }

            if (owner != null)
            {
                if (IsOurPiWeb(owner))
                {
                    _phase = Phase.Running;
                    _lastNotified = "";
                    _lastOwnerCheck = DateTime.Now;
                    _autoRestartAt = DateTime.MinValue;
                    _runningSince = DateTime.Now;
                    _restartAfterUpdate = false;
                    UpdateUi();
                    if (openBrowser) OpenBrowser();
                    return;
                }
                if (owner.IsUnknown || owner.IsPiWeb)
                {
                    if (TrayOwnsPort(owner))
                    {
                        _phase = Phase.Running;
                        _lastNotified = "";
                        _lastOwnerCheck = DateTime.Now;
                        _autoRestartAt = DateTime.MinValue;
                        _runningSince = DateTime.Now;
                        _restartAfterUpdate = false;
                        UpdateUi();
                        if (openBrowser) OpenBrowser();
                        return;
                    }
                    KillTrackedServer();
                    _phase = Phase.PortConflict;
                    UpdateUi();
                    NotifyPortConflict();
                    return;
                }
                // The listener disappeared between the check and the inspection:
                // continue and try to start the service.
            }

            // The port is not owned by a listener at this point. If an older tracked
            // wrapper is still alive, it is stale; terminate it before starting a new
            // process tree.
            KillTrackedServer();

            string script = ExpectedPiWebScript();

            if (string.IsNullOrEmpty(script) || !File.Exists(script))
            {
                _phase = Phase.NeedsDeps;
                UpdateUi();
                NotifyWarningOnce("no-script", L.T("notify.noPiwebTitle"), L.T("notify.noPiwebBody"));
                return;
            }

            string node = EffectiveNodePath();
            if (string.IsNullOrEmpty(node)) node = "node.exe";

            // A symlink at server.log must not redirect elevated service output. If the
            // configured log path is unsafe, run the service without a log instead of
            // following the link.
            bool safeLog = !PathSafety.ContainsReparsePoint(_config.LogFile);
            if (safeLog)
            {
                RotateLog();
                // The service output itself is raw Node/Pi Web output, so mark the start
                // of each run with a timestamp and the operation being performed.
                Updater.AppendLog(_config.LogFile,
                    "==== service start (port " + _port + ") ====");
            }
            else
            {
                Updater.AppendLog(_config.UpdateLogFile,
                    "unsafe server log path; service output is not logged: " + _config.LogFile);
            }
            try
            {
                // Pipe the output through a separate log-host process. cmd cannot rotate a
                // file it redirects itself, while the host owns its log and can rotate it at
                // any time. Neither the tray nor the pipe has to stay alive for the service.
                string command;
                if (!safeLog)
                {
                    command = Shell.CmdQuote(node) + " " + Shell.CmdQuote(script)
                        + " --no-open -p " + _port
                        + " >NUL 2>&1";
                }
                else if (LogHostAvailableCached())
                {
                    string logHost = Shell.CmdQuote(Application.ExecutablePath)
                        + " --log-host " + Shell.CmdQuote(_config.LogFile) + " "
                        + MaxLogBytes.ToString(CultureInfo.InvariantCulture);
                    command = Shell.CmdQuote(node) + " " + Shell.CmdQuote(script)
                        + " --no-open -p " + _port
                        + " 2>&1 | " + logHost;
                }
                else
                {
                    // Last-resort compatibility path: this loses live rotation and bypasses
                    // the log-host's redaction (npm credential forms would land in server.log
                    // as-is), but keeps the service startable when the log-host child cannot
                    // be launched. SECURITY.md documents this residual risk.
                    command = Shell.CmdQuote(node) + " " + Shell.CmdQuote(script)
                        + " --no-open -p " + _port
                        + " 1>>" + Shell.CmdQuote(_config.LogFile) + " 2>&1";
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Shell.ComSpec();
                psi.Arguments = Shell.CmdLine(command);
                psi.WorkingDirectory = _config.WorkingDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                // An elevated service must not inherit user-controlled Node/npm/.NET
                // injection variables (for example NODE_OPTIONS).
                ProcessRunner.HardenEnvironment(psi, _elevated);

                // Installing Node.js after the tray started updates the registry PATH, not
                // this process's environment. Make the detected Node/npm directories
                // visible to the service and its child processes.
                psi.EnvironmentVariables["PATH"] = ServicePath(node);

                // The Proxy setting is deliberately not exported here: it applies only to
                // Pi Web Tray update traffic. Do not change the Node/pi-web environment.
                // An elevated tray passes its token to the child, which is exactly what "run as admin" means here
                try
                {
                    if (_server != null && _server.HasExited)
                    {
                        _server.Dispose();
                        _server = null;
                    }
                }
                catch (Exception)
                {
                }
                _server = Process.Start(psi);
                ServiceProbe.Invalidate();

                lock (_readyLock)
                {
                    _readyGeneration++;
                    _readyResult = 0;
                    _readyCheckRunning = false;
                }
                _phase = Phase.Starting;
                _openWhenReady = openBrowser;
                _startDeadline = DateTime.Now.AddSeconds(StartTimeoutSeconds);
                UpdateUi();
            }
            catch (Exception ex)
            {
                // The failure can happen after Process.Start (for example while
                // updating the UI). Do not leave that process tree behind in a
                // Failed state; it would hold the port without a tray owner.
                KillTrackedServer();
                _openWhenReady = false;
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("notify.cannotStart"), ex.Message);
            }
        }

        private string ServicePath(string node)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string nodeDir = "";
            try { nodeDir = Path.GetDirectoryName(node); } catch (Exception) { }
            string nodePrefix = ProcessRunner.PathPrefix(nodeDir);
            if (nodePrefix.Length > 0)
                path = nodePrefix + ";" + path;

            // npm's global prefix also contains npm-installed command shims.
            if (_probe != null && !string.IsNullOrEmpty(_probe.NpmPrefix)
                && Directory.Exists(_probe.NpmPrefix))
            {
                string npmPrefix = ProcessRunner.PathPrefix(_probe.NpmPrefix);
                if (npmPrefix.Length > 0) path = npmPrefix + ";" + path;
            }
            return path;
        }

        private void StopServerFromUser()
        {
            // A user stop must survive an install that is already stopping or has
            // already restarted the service. StopServer still refuses to touch npm,
            // but the install's restore/start intent must be cancelled so the
            // service does not come back after the install finishes.
            _serviceStoppedByUser = true;
            // A stop that arrives while a restart is already in Stopping must
            // cancel the queued start; otherwise PollStopping brings the service
            // back after the user explicitly asked for it to stay down.
            _pendingRestart = false;
            _pendingOpenBrowser = false;
            _restartAfterUpdate = false;
            _autoRestartAt = DateTime.MinValue;
            _autoRestartAttempts = 0;
            _installRestartService = false;
            _installStartAfter = false;
            _installOpenAfter = false;

            // A stop requested while an environment probe is deciding whether to
            // start the service must also cancel that queued start.
            lock (_checkLock)
            {
                _checkThenStart = false;
                _checkOpenBrowser = false;
                _checkPendingThenStart = false;
                _checkPendingOpenBrowser = false;
                // A dependency install may be queued behind the probe. Keep the
                // install itself, but do not start the service after it.
                _installAfterCheckStart = false;
                _installAfterCheckOpen = false;
            }

            StopServer();
        }

        private void StopServer()
        {
            // A click during Starting can queue "open when ready"; if the start is
            // then stopped, that old open request must not fire on a later silent
            // start (autostart, update restart or install recovery).
            _openWhenReady = false;
            ClearPendingOwnerActions(_installAfterStop);
            InvalidateOwnerCheck();
            if (_phase == Phase.Stopping) return;
            _restartAfterUpdate = false;
            _autoRestartAt = DateTime.MinValue;
            _autoRestartAttempts = 0;
            _runningSince = DateTime.MinValue;

            // Do not touch the state machine while no service state has been reached
            if (_phase == Phase.Checking || _phase == Phase.NeedsNode || _phase == Phase.NeedsDeps
                || _phase == Phase.Installing || _phase == Phase.Failed)
            {
                if (!Net.IsPortOpen(_port, 200))
                {
                    KillTrackedServer();
                    return;
                }
            }

            if (_phase == Phase.Starting)
            {
                // Stop immediately instead of waiting up to three minutes for the startup
                // deadline. The tree kill is asynchronous; PollStopping confirms the port is gone.
                _pendingRestart = false;
                _pendingOpenBrowser = false;
                KillTrackedServer();
                InvalidateOwnerCheck();
                _phase = Phase.Stopping;
                _stopDeadline = DateTime.Now.AddSeconds(10);
                _stopEscalatedAt = DateTime.MinValue;
                _stopOwnerCheckFailures = 0;
                UpdateUi();
                return;
            }

            if (_phase == Phase.Installing)
            {
                NotifyWarningOnce("stop-during-install", L.T("notify.installBusyTitle"), L.T("notify.installBusyBody"));
                return;
            }

            if (!Net.IsPortOpen(_port, 300))
            {
                KillTrackedServer();
                _phase = Phase.Stopped;
                UpdateUi();
                if (_pendingRestart)
                {
                    _pendingRestart = false;
                    bool open = _pendingOpenBrowser;
                    _pendingOpenBrowser = false;
                    StartServer(open);
                }
                return;
            }

            // A listener that is not pi-web must never be stopped or killed. The only
            // exception is a service started by this tray (the tracked cmd child), whose
            // owner may be temporarily unreadable by the native process probe.
            ServiceProbe.Invalidate();
            InvalidateOwnerCheck();
            PortOwnerInfo owner = TakeOwnerCheck();
            if (owner == null)
            {
                // Deciding whether the listener may be killed must be based on fresh data,
                // but the probe can take seconds; continue when the result arrives instead
                // of freezing the UI thread.
                _stopAfterOwnerCheck = true;
                BeginOwnerCheck();
                return;
            }
            ContinueStopServer(owner);
        }

        private void ContinueStopServer(PortOwnerInfo owner)
        {
            // The phase may have changed while the owner check ran
            if (_phase == Phase.Stopping || _phase == Phase.Installing) return;

            if (owner != null && owner.CheckFailed)
            {
                // Never kill a listener that could not be identified. Preserve
                // the current service state and let the user retry once the
                // process probe is available again.
                _pendingRestart = false;
                _pendingOpenBrowser = false;
                HandleOwnerCheckFailure(owner);
                AbortInstallAfterStop(false);
                return;
            }

            bool ownedByTray = TrayOwnsPort(owner);
            if (!IsOurPiWeb(owner) && !ownedByTray)
            {
                _pendingRestart = false;
                _pendingOpenBrowser = false;
                // Our wrapper may still be alive (for example a failed bind while a
                // foreign program owns the port). It cannot be the service we want to
                // keep, so do not leave it behind.
                KillTrackedServer();
                _phase = (owner.IsUnknown || owner.IsPiWeb) ? Phase.PortConflict : Phase.Stopped;
                UpdateUi();
                if (_phase == Phase.PortConflict) NotifyPortConflict();
                // When the stop was dispatched through the background owner check, no
                // caller is left to abort a pending install after the decision; the
                // foreign-listener verdict is known here, so clean it up now.
                AbortInstallAfterStop(false);
                return;
            }

            _phase = Phase.Stopping;
            _stopDeadline = DateTime.Now.AddSeconds(20);
            _stopEscalatedAt = DateTime.MinValue;
            _stopOwnerCheckFailures = 0;
            UpdateUi();

            // pi-web's own stop command starts node and takes a while, so run it in the background.
            // In an elevated tray whose install directory is no longer protected, do
            // not execute the configured NodePath; kill the already-identified listener.
            bool unsafeStop = ElevatedInstallUnsafe();
            string script = ExpectedPiWebScript();

            if (!unsafeStop && !string.IsNullOrEmpty(script) && File.Exists(script))
            {
                // Hold a cross-process lock before publishing _stopWorkerRunning. The
                // CLI install path waits on this lock after the port closes, so it cannot
                // start npm while `pi-web stop` still holds the package directory.
                ReleaseStopWorkerLock();
                _stopWorkerLock = OperationLock.TryAcquireCrossThread(OperationLock.StopName);
                OperationLock stopLock = _stopWorkerLock;
                _stopWorkerRunning = true;
                Thread worker = new Thread(delegate ()
                {
                    try { StopWithCli(script); }
                    finally
                    {
                        _stopWorkerRunning = false;
                        // Release only the lock this worker owns. A later stop worker
                        // may already hold a new lock in _stopWorkerLock.
                        ReleaseStopWorkerLock(stopLock);
                    }
                });
                worker.IsBackground = true;
                try
                {
                    worker.Start();
                }
                catch (Exception)
                {
                    _stopWorkerRunning = false;
                    ReleaseStopWorkerLock(stopLock);
                }
            }
            else
            {
                if (owner.Pid > 4) Sys.KillTree(owner.Pid);
                ServiceProbe.Invalidate();
            }
        }

        private void StopWithCli(string script)
        {
            try
            {
                string node = EffectiveNodePath();
                if (string.IsNullOrEmpty(node)) node = "node.exe";
                ProcessRunner.Run(node, Shell.Quote(script) + " stop --port " + _port,
                    _config.WorkingDir, 20000, null, null);
            }
            catch (Exception)
            {
            }
        }

        private void ReleaseStopWorkerLock()
        {
            ReleaseStopWorkerLock(_stopWorkerLock);
            _stopWorkerLock = null;
        }

        private void ReleaseStopWorkerLock(OperationLock operationLock)
        {
            if (operationLock == null) return;
            // The worker may finish after a new stop worker has replaced the field.
            // Never clear or dispose the newer lock here; just release this one.
            if (ReferenceEquals(_stopWorkerLock, operationLock)) _stopWorkerLock = null;
            try { operationLock.Dispose(); } catch (Exception) { }
        }

        // Shared by the helper start mode and the post-update restart decision.
        // A user-requested stop during an update must win over AutoRestart. A service
        // that was already running when the update began must also be restored even
        // when AutoRestart is off; AutoRestart only governs crash recovery, not the
        // deliberate stop performed by the updater itself.
        internal static bool ShouldRestartServiceAfterUpdate(bool startedWithServiceRunning,
            bool stoppedByUser, bool restartAfterUpdate)
        {
            if (stoppedByUser) return false;
            return restartAfterUpdate || startedWithServiceRunning;
        }

        private void RestartServer(bool openBrowser)
        {
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;
            _pendingRestart = true;
            _pendingOpenBrowser = openBrowser;
            if (_phase != Phase.Running && _phase != Phase.RunningUnverified)
            {
                _pendingRestart = false;
                _pendingOpenBrowser = false;
                StartServer(openBrowser);
                return;
            }
            StopServer();
        }
    }
}
