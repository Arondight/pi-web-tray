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
                Detail = "port owner check failed"
            };
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
            Thread worker = new Thread(delegate ()
            {
                PortOwnerInfo result = null;
                try { result = ServiceProbe.Inspect(port); }
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
                // A newer action may have started while this check was in flight.
                // Never overwrite the state machine for that action with stale phase data.
                if (_phase == Phase.Installing || _phase == Phase.Starting
                    || _phase == Phase.Stopping || _phase == Phase.Checking
                    || _phase == Phase.NeedsNode || _phase == Phase.NeedsDeps)
                {
                    return;
                }
                _phase = PhaseFromOwner(owner);
                if (_phase == Phase.Running) _runningSince = DateTime.Now;
                UpdateUi();
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
            if (IsOurPiWeb(owner)) return Phase.Running;
            if ((owner.IsUnknown || owner.IsPiWeb) && TrayOwnsPort(owner)) return Phase.Running;
            if (owner.IsUnknown || owner.IsPiWeb) return Phase.PortConflict;
            return Phase.Stopped;
        }

        // ResolveServicePhase() without blocking the UI thread: the phase is applied when
        // the background owner check finishes (DispatchOwnerCheck).
        private void RequestResolveServicePhase()
        {
            if (!Net.IsPortOpen(_port, 200))
            {
                InvalidateOwnerCheck();
                _phase = Phase.Stopped;
                return;
            }

            // An environment check sets the phase to Checking; once its result is being
            // applied, the phase must not remain Checking while the owner probe runs.
            if (_phase == Phase.Checking) _phase = Phase.Stopped;

            // Resolve is a one-shot phase decision; never reuse a completed result from
            // an earlier action.
            ServiceProbe.Invalidate();
            InvalidateOwnerCheck();
            PortOwnerInfo owner = TakeOwnerCheck();
            if (owner == null)
            {
                _resolveAfterOwnerCheck = true;
                BeginOwnerCheck();
                return;
            }
            _phase = PhaseFromOwner(owner);
            if (_phase == Phase.Running) _runningSince = DateTime.Now;
        }

        // Must mirror StartServer: a configured script wins only while it still exists;
        // otherwise the probe result is what the service will actually launch.
        private string ExpectedPiWebScript()
        {
            string script = _config.ServerScript;
            if (!string.IsNullOrEmpty(script) && File.Exists(script)) return script;
            if (_probe != null && _probe.PiWebFound && !string.IsNullOrEmpty(_probe.PiWebScript))
                return _probe.PiWebScript;
            return script;
        }

        // Drop requests that belong to an older user action. Stop-for-install preserves
        // the queued install continuation; every other explicit action owns the state now.
        private void ClearPendingOwnerActions(bool keepInstall)
        {
            _startAfterOwnerCheck = false;
            _startAfterOwnerCheckOpen = false;
            _resolveAfterOwnerCheck = false;
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
                if (!IsOurPiWeb(owner) && !TrayOwnsPort(owner))
                {
                    if (DateTime.Now > _startDeadline)
                        FailStart(L.T("notify.startFailedTimeout", _port));
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

        private void FailStart(string reason)
        {
            ClearPendingOwnerActions(false);
            InvalidateOwnerCheck();
            _phase = Phase.Failed;
            _openWhenReady = false;
            _pendingRestart = false;
            try
            {
                if (_server != null && !_server.HasExited) Sys.KillTree(_server.Id);
            }
            catch (Exception)
            {
            }
            UpdateUi();
            NotifyError(L.T("notify.startFailedTitle"), L.T("notify.startFailedBody", reason),
                delegate { ShowLog(_config.LogFile, L.T("menu.serverLog")); });
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
                    if (IsOurPiWeb(owner) || TrayOwnsPort(owner))
                    {
                        _stopEscalatedAt = DateTime.Now;
                        Sys.KillByPort(_port);
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
                if (_phase == Phase.Running)
                {
                    _phase = Phase.Stopped;
                    UpdateUi();
                    NotifyWarningOnce("service-stopped",
                        L.T("notify.serviceStoppedTitle"),
                        L.T("notify.serviceStoppedBody"),
                        delegate { StartServer(false); });
                    if (UpdateBusy() && _config.AutoRestart && _probe != null && _probe.Ready)
                    {
                        // The update owns the busy flag; remember the crash and schedule
                        // the restart after the update check/download has finished.
                        _restartAfterUpdate = true;
                    }
                    ScheduleAutoRestart();
                }
                else if (_phase == Phase.PortConflict)
                {
                    _phase = Phase.Stopped;
                    _lastNotified = "";
                    UpdateUi();
                }
                return;
            }

            if (_phase == Phase.Running)
            {
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
                _lastOwnerCheck = DateTime.Now;
                if (IsOurPiWeb(current)) return;
                // The command line can be unreadable while cmd still owns the tree;
                // the tracked child is enough evidence in that case.
                if ((current.IsUnknown || current.IsPiWeb) && TrayOwnsPort(current)) return;

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
            if (IsOurPiWeb(owner))
            {
                _phase = Phase.Running;
                _lastNotified = "";
                _lastOwnerCheck = DateTime.Now;
                _runningSince = DateTime.Now;
                _restartAfterUpdate = false;
                UpdateUi();
            }
            else if (owner.IsUnknown || owner.IsPiWeb)
            {
                if (_phase != Phase.PortConflict)
                {
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

        private bool TrayOwnsPort(PortOwnerInfo owner)
        {
            if (_server == null) return false;
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

        private void ApplyProbeResult(ProbeResult result)
        {
            if (result == null) return;

            _probe = result;
            _config.NodePath = result.NodePath;
            if (result.PiWebFound) _config.ServerScript = result.PiWebScript;

            if (result.NeedsNode) _phase = Phase.NeedsNode;
            else if (result.NeedsPiWeb) _phase = Phase.NeedsDeps;
            else RequestResolveServicePhase();

            UpdateUi();
            if (_phase == Phase.PortConflict) NotifyPortConflict();
        }

        private void BeginReadyCheck()
        {
            lock (_readyLock)
            {
                if (_readyCheckRunning) return;
                _readyCheckRunning = true;
            }

            Thread worker = new Thread(delegate ()
            {
                bool ok = Net.IsHttpOk("http://127.0.0.1:" + _port + "/", 3000);
                lock (_readyLock)
                {
                    _readyResult = ok ? 1 : -1;
                    _readyCheckRunning = false;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void ScheduleAutoRestart()
        {
            if (!_config.AutoRestart || _exiting || UpdateBusy()) return;
            if (_probe == null || !_probe.Ready) return;
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
            try
            {
                ProcessResult check = ProcessRunner.Run(
                    Application.ExecutablePath,
                    "--log-host-check",
                    _config.WorkingDir,
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
            ClearPendingOwnerActions(false);
            InvalidateOwnerCheck();
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;

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
                        _runningSince = DateTime.Now;
                        _restartAfterUpdate = false;
                        UpdateUi();
                        if (openBrowser) OpenBrowser();
                        return;
                    }
                    _phase = Phase.PortConflict;
                    UpdateUi();
                    NotifyPortConflict();
                    return;
                }
                // The listener disappeared between the check and the inspection:
                // continue and try to start the service.
            }

            string script = ExpectedPiWebScript();

            if (string.IsNullOrEmpty(script) || !File.Exists(script))
            {
                _phase = Phase.NeedsDeps;
                UpdateUi();
                NotifyWarningOnce("no-script", L.T("notify.noPiwebTitle"), L.T("notify.noPiwebBody"));
                return;
            }

            string node = string.IsNullOrEmpty(_config.NodePath) ? "node.exe" : _config.NodePath;

            RotateLog();
            try
            {
                // Pipe the output through a separate log-host process. cmd cannot rotate a
                // file it redirects itself, while the host owns its log and can rotate it at
                // any time. Neither the tray nor the pipe has to stay alive for the service.
                string command;
                if (LogHostAvailableCached())
                {
                    string logHost = Shell.Quote(Application.ExecutablePath)
                        + " --log-host " + Shell.Quote(_config.LogFile) + " "
                        + MaxLogBytes.ToString(CultureInfo.InvariantCulture);
                    command = Shell.Quote(node) + " " + Shell.Quote(script)
                        + " --no-open -p " + _port
                        + " 2>&1 | " + logHost;
                }
                else
                {
                    // Last-resort compatibility path: this loses live rotation and bypasses
                    // the log-host's redaction (npm credential forms would land in server.log
                    // as-is), but keeps the service startable when the log-host child cannot
                    // be launched. SECURITY.md documents this residual risk.
                    command = Shell.Quote(node) + " " + Shell.Quote(script)
                        + " --no-open -p " + _port
                        + " 1>>" + Shell.Quote(_config.LogFile) + " 2>&1";
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Shell.ComSpec();
                psi.Arguments = Shell.CmdLine(command);
                psi.WorkingDirectory = _config.WorkingDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                // The Proxy setting is deliberately not exported here: it applies only to
                // Pi Web Tray update traffic. Do not change the Node/pi-web environment.
                // An elevated tray passes its token to the child, which is exactly what "run as admin" means here
                _server = Process.Start(psi);
                ServiceProbe.Invalidate();

                lock (_readyLock)
                {
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
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("notify.cannotStart"), ex.Message);
            }
        }

        private void StopServer()
        {
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
                if (!Net.IsPortOpen(_port, 200)) return;
            }

            if (_phase == Phase.Starting)
            {
                // Stop immediately instead of waiting up to three minutes for the startup
                // deadline. The tree kill is asynchronous; PollStopping confirms the port is gone.
                _pendingRestart = false;
                _pendingOpenBrowser = false;
                try
                {
                    if (_server != null && !_server.HasExited) Sys.KillTree(_server.Id);
                }
                catch (Exception)
                {
                }
                InvalidateOwnerCheck();
                _phase = Phase.Stopping;
                _stopDeadline = DateTime.Now.AddSeconds(10);
                _stopEscalatedAt = DateTime.MinValue;
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

            bool ownedByTray = TrayOwnsPort(owner);
            if (!IsOurPiWeb(owner) && !ownedByTray)
            {
                _pendingRestart = false;
                _pendingOpenBrowser = false;
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
            UpdateUi();

            // pi-web's own stop command starts node and takes a while, so run it in the background
            string script = ExpectedPiWebScript();

            if (!string.IsNullOrEmpty(script) && File.Exists(script))
            {
                _stopWorkerRunning = true;
                Thread worker = new Thread(delegate ()
                {
                    try { StopWithCli(script); }
                    finally { _stopWorkerRunning = false; }
                });
                worker.IsBackground = true;
                try
                {
                    worker.Start();
                }
                catch (Exception)
                {
                    _stopWorkerRunning = false;
                }
            }
            else
            {
                Sys.KillByPort(_port);
                ServiceProbe.Invalidate();
            }
        }

        private void StopWithCli(string script)
        {
            try
            {
                string node = string.IsNullOrEmpty(_config.NodePath) ? "node.exe" : _config.NodePath;
                ProcessRunner.Run(node, Shell.Quote(script) + " stop --port " + _port,
                    _config.WorkingDir, 20000, null, null);
            }
            catch (Exception)
            {
            }
        }

        private void RestartServer(bool openBrowser)
        {
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;
            _pendingRestart = true;
            _pendingOpenBrowser = openBrowser;
            if (_phase != Phase.Running)
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
