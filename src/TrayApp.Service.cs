// Pi Web Tray — service lifecycle and port ownership
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
                // being marked as a successful start.
                PortOwnerInfo owner = ServiceProbe.Inspect(_port);
                if (!owner.IsPiWeb && !TrayOwnsPort())
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
                _autoRestartAttempts = 0;
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
                ServiceProbe.Invalidate();
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

            if (DateTime.Now > _stopDeadline)
            {
                // The CLI had its chance and the service is still up. Only force-kill a
                // confirmed pi-web process (or the process this tray started); never kill
                // an unknown listener that took the port.
                if (_stopEscalatedAt == DateTime.MinValue)
                {
                    ServiceProbe.Invalidate();
                    PortOwnerInfo owner = ServiceProbe.Inspect(_port);
                    if (owner.IsPiWeb || TrayOwnsPort())
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
                        UpdateUi();
                        NotifyPortConflict();
                    }
                }
                else if (DateTime.Now > _stopEscalatedAt.AddSeconds(6))
                {
                    _phase = Phase.Running;
                    _pendingRestart = false;
                    _pendingOpenBrowser = false;
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
                if (DateTime.Now - _lastOwnerCheck < TimeSpan.FromSeconds(30)) return;
                _lastOwnerCheck = DateTime.Now;
                ServiceProbe.Invalidate();
                PortOwnerInfo current = ServiceProbe.Inspect(_port);
                if (current.IsPiWeb) return;
                if (current.IsUnknown && TrayOwnsPort()) return;

                _phase = current.IsUnknown ? Phase.PortConflict : Phase.Stopped;
                UpdateUi();
                if (_phase == Phase.PortConflict) NotifyPortConflict();
                return;
            }
            if (_phase != Phase.Stopped && _phase != Phase.Failed && _phase != Phase.NeedsDeps
                && _phase != Phase.NeedsNode && _phase != Phase.PortConflict)
            {
                return;
            }

            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            if (owner.IsPiWeb)
            {
                _phase = Phase.Running;
                _lastNotified = "";
                _lastOwnerCheck = DateTime.Now;
                _restartAfterUpdate = false;
                UpdateUi();
            }
            else if (owner.IsUnknown)
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

        private bool TrayOwnsPort()
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

            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            if (owner.IsPiWeb) return true;
            if (owner.Pid <= 0) return false;
            if (owner.Pid == _server.Id) return true;
            return ServiceProbe.ParentPid(owner.Pid) == _server.Id;
        }

        private Phase ResolveServicePhase()
        {
            if (!Net.IsPortOpen(_port, 200)) return Phase.Stopped;

            ServiceProbe.Invalidate();
            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            if (owner.IsPiWeb) return Phase.Running;
            if (owner.IsUnknown) return Phase.PortConflict;
            return Phase.Stopped;
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
            else _phase = ResolveServicePhase();

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
            if (_autoRestartAt != DateTime.MinValue) return;
            if (_autoRestartAttempts >= 3) return;

            _autoRestartAttempts++;
            int delay = _autoRestartAttempts == 1 ? 5 : (_autoRestartAttempts == 2 ? 15 : 30);
            _autoRestartAt = DateTime.Now.AddSeconds(delay);
            NotifyWarning(L.T("notify.autoRestartTitle"), L.T("notify.autoRestartBody", delay));
        }

        private void StartServer(bool openBrowser)
        {
            if (_phase == Phase.Starting || _phase == Phase.Stopping || _phase == Phase.Installing) return;

            ServiceProbe.Invalidate();
            if (Net.IsPortOpen(_port, 300))
            {
                PortOwnerInfo owner = ServiceProbe.Inspect(_port);
                if (owner.IsPiWeb)
                {
                    _phase = Phase.Running;
                    _lastNotified = "";
                    _lastOwnerCheck = DateTime.Now;
                    _autoRestartAt = DateTime.MinValue;
                    _autoRestartAttempts = 0;
                    _restartAfterUpdate = false;
                    UpdateUi();
                    if (openBrowser) OpenBrowser();
                    return;
                }
                if (owner.IsUnknown)
                {
                    if (TrayOwnsPort())
                    {
                        _phase = Phase.Running;
                        _lastNotified = "";
                        _lastOwnerCheck = DateTime.Now;
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

            string script = _config.ServerScript;
            if (string.IsNullOrEmpty(script) || !File.Exists(script))
            {
                if (_probe != null && _probe.PiWebFound) script = _probe.PiWebScript;
            }

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
                // Redirect output to the log through cmd: the tray is not in the pipe chain, so
                // the service does not die when the tray exits and the pipe closes.
                string command = Shell.Quote(node) + " " + Shell.Quote(script)
                    + " --no-open -p " + _port
                    + " 1>>" + Shell.Quote(_config.LogFile) + " 2>&1";

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Shell.ComSpec();
                psi.Arguments = Shell.CmdLine(command);
                psi.WorkingDirectory = _config.WorkingDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

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
            if (_phase == Phase.Stopping) return;
            _restartAfterUpdate = false;
            _autoRestartAt = DateTime.MinValue;
            _autoRestartAttempts = 0;

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
            PortOwnerInfo owner = ServiceProbe.Inspect(_port);
            bool ownedByTray = TrayOwnsPort();
            if (!owner.IsPiWeb && !ownedByTray)
            {
                _pendingRestart = false;
                _pendingOpenBrowser = false;
                _phase = owner.IsUnknown ? Phase.PortConflict : Phase.Stopped;
                UpdateUi();
                if (_phase == Phase.PortConflict) NotifyPortConflict();
                return;
            }

            _phase = Phase.Stopping;
            _stopDeadline = DateTime.Now.AddSeconds(20);
            _stopEscalatedAt = DateTime.MinValue;
            UpdateUi();

            // pi-web's own stop command starts node and takes a while, so run it in the background
            string script = _config.ServerScript;
            if (string.IsNullOrEmpty(script)) script = _probe != null ? _probe.PiWebScript : null;

            if (!string.IsNullOrEmpty(script) && File.Exists(script))
            {
                Thread worker = new Thread(delegate () { StopWithCli(script); });
                worker.IsBackground = true;
                worker.Start();
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
