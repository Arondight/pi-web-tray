// Pi Web Tray — environment and self checks
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
        private bool IsServiceActionInProgress()
        {
            return _phase == Phase.Starting || _phase == Phase.Stopping
                || _phase == Phase.Installing;
        }

        private void BeginEnvironmentCheck(bool thenStart, bool openBrowser)
        {
            BeginEnvironmentCheck(thenStart, openBrowser, 0, false);
        }

        // delayMs gives a login-time probe a moment: antivirus may still be scanning
        // node.exe and the profile/registry may not be fully ready right after logon.
        private void BeginEnvironmentCheck(bool thenStart, bool openBrowser, int delayMs)
        {
            BeginEnvironmentCheck(thenStart, openBrowser, delayMs, false);
        }

        private void BeginEnvironmentCheck(bool thenStart, bool openBrowser, int delayMs,
            bool suppressAutoInstall)
        {
            lock (_checkLock)
            {
                if (_checkRunning || _checkDone)
                {
                    // A check is already in flight or its result is waiting for the
                    // poll loop: remember the request instead of dropping the result,
                    // otherwise "install finished -> check -> start" can silently lose the start.
                    _checkPending = true;
                    _checkPendingThenStart = _checkPendingThenStart || thenStart;
                    _checkPendingOpenBrowser = _checkPendingOpenBrowser || openBrowser;
                    _checkPendingSuppressAutoInstall =
                        _checkPendingSuppressAutoInstall || suppressAutoInstall;
                    // The already-running check owns the result that will be consumed
                    // first. If Node.js was detected while it was in flight, that
                    // check must also respect the manual-install decision.
                    _checkSuppressAutoInstall =
                        _checkSuppressAutoInstall || suppressAutoInstall;
                    return;
                }

                // Do not overwrite a service action. A check requested during
                // Starting/Stopping/Installing is queued and consumed once the
                // action has reached a stable phase.
                if (IsServiceActionInProgress())
                {
                    _checkPending = true;
                    _checkPendingThenStart = _checkPendingThenStart || thenStart;
                    _checkPendingOpenBrowser = _checkPendingOpenBrowser || openBrowser;
                    _checkPendingSuppressAutoInstall =
                        _checkPendingSuppressAutoInstall || suppressAutoInstall;
                    return;
                }

                _checkRunning = true;
                _checkDone = false;
                _checkResult = null;
                _checkThenStart = thenStart;
                _checkOpenBrowser = openBrowser;
                _checkSuppressAutoInstall = suppressAutoInstall;
                _phase = Phase.Checking;
            }

            UpdateUi();

            Thread worker = new Thread(delegate ()
            {
                if (delayMs > 0)
                {
                    try { Thread.Sleep(delayMs); } catch (Exception) { }
                }

                ProbeResult result = null;
                try
                {
                    result = EnvironmentProbe.Detect(_config);
                }
                catch (Exception ex)
                {
                    // The notification tells the user to inspect update.log; make
                    // sure the probe failure is actually recorded there.
                    try
                    {
                        Updater.AppendLog(_config.UpdateLogFile,
                            "environment probe failed: " + ex);
                    }
                    catch (Exception)
                    {
                    }
                }

                // Probe the log-host child once, here in the background: StartServer needs
                // the answer, and the 3 s self-probe must not run on the UI thread.
                int logHostState = _logHostState;
                if (logHostState == -1)
                {
                    try { logHostState = LogHostAvailable() ? 1 : 0; }
                    catch (Exception) { }
                }

                lock (_checkLock)
                {
                    _checkResult = result;
                    _checkLogHostState = logHostState;
                    _checkRunning = false;
                    _checkDone = true;
                }
            });
            worker.IsBackground = true;
            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                try
                {
                    Updater.AppendLog(_config.UpdateLogFile,
                        "could not start environment check worker: " + ex);
                }
                catch (Exception) { }
                lock (_checkLock)
                {
                    _checkRunning = false;
                    _checkResult = null;
                    _checkDone = true;
                }
            }
        }

        // The UI thread only consumes a flag set by WatchNodePresence. All FileInfo,
        // DirectoryInfo and registry PATH reads happen on the worker thread.
        private void PollNodePresence()
        {
            if (!_nodeWatching) return;
            if (_modalDepth > 0) return;
            // Leave the flag set while an update owns the process; restarting or installing
            // during an update could race with the helper that replaces the executable.
            if (UpdateBusy()) return;
            if (_nodePromptOpen) return;
            if (Interlocked.Exchange(ref _nodePresenceChanged, 0) == 0) return;
            if (_exiting) return;

            RefreshNodeHealth();
            UpdateUi();
            if (_nodePresent && _phase == Phase.NeedsNode)
            {
                if (!_nodePresenceNotified)
                {
                    _nodePresenceNotified = true;
                    Notify(L.T("notify.nodeDetectedTitle"),
                        L.T("notify.nodeDetectedBody", _installItem.Text));
                }
                // Do not install dependencies behind the user's back after they
                // installed Node.js themselves. Refresh the state and wait for an
                // explicit install/update menu action.
                BeginEnvironmentCheck(_autoStartService, _initialOpenBrowser, 0, true);
            }
            else if (_nodePresent && _phase == Phase.Running)
            {
                // A live service keeps working with the already-loaded Node.js.
                // Do not put it into Checking just to refresh paths; re-probe
                // before the next start instead.
                _nodeProbeStale = true;
            }
            else if (_nodePresent && _phase != Phase.Checking
                && DateTime.Now - _lastNodeReprobeAt > TimeSpan.FromSeconds(30))
            {
                // A present fingerprint can change without disappearing (Node.js
                // upgrade/reinstall or a different npm prefix). Refresh the cached
                // NodePath/PiWebScript while the service is not running.
                _lastNodeReprobeAt = DateTime.Now;
                BeginEnvironmentCheck(false, false, 0, true);
            }
        }

        // Keep a transient Node.js removal from looking like an uninstall while a running
        // service is still perfectly usable. The fingerprint watcher confirms absence over
        // several samples before raising the state.
        private void RefreshNodeHealth()
        {
            if (!_nodePresenceKnown || _exiting) return;

            if (_nodePresent)
            {
                if (_nodeMissingNotified)
                {
                    _nodeMissingNotified = false;
                    if (_phase != Phase.NeedsNode)
                        Notify(L.T("notify.nodeRestoredTitle"), L.T("notify.nodeRestoredBody"));
                }
                return;
            }

            if (_phase == Phase.Running) NotifyNodeMissingWhileRunning();
        }

        private void NotifyNodeMissingWhileRunning()
        {
            if (_nodeMissingNotified || _exiting) return;
            _nodeMissingNotified = true;
            NotifyWarning(L.T("notify.nodeMissingTitle"), L.T("notify.nodeMissingBody"));
        }

        private void StartNodePresenceMonitor()
        {
            StopNodePresenceMonitor();
            _nodeWatching = true;
            int generation = Interlocked.Increment(ref _nodeWatchGeneration);

            Thread worker = new Thread(delegate () { WatchNodePresence(generation); });
            worker.IsBackground = true;
            worker.Name = "pi-web-tray node watcher";
            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                _nodeWatching = false;
                try
                {
                    Updater.AppendLog(_config.UpdateLogFile,
                        "could not start Node.js watcher: " + ex.Message);
                }
                catch (Exception) { }
            }
        }

        private void StopNodePresenceMonitor()
        {
            _nodeWatching = false;
            Interlocked.Increment(ref _nodeWatchGeneration);
            Interlocked.Exchange(ref _nodePresenceChanged, 0);
        }

        private bool NodeWatchActive(int generation)
        {
            return generation == Volatile.Read(ref _nodeWatchGeneration);
        }

        private void WatchNodePresence(int generation)
        {
            string lastSignature = null;
            while (NodeWatchActive(generation))
            {
                string signature = "";
                try
                {
                    signature = EnvironmentProbe.NodePresenceSignature(_config);
                }
                catch (Exception)
                {
                }

                bool presenceChanged = _nodePresenceState.Update(signature);
                _nodePresent = _nodePresenceState.Present;
                _nodePresenceKnown = _nodePresenceState.Known;

                // A present fingerprint can change without disappearing (Node.js upgrade or
                // a different installation). Re-check it while waiting for Node.js, but a
                // first missing sample is only a debounce step, not a warning.
                bool presentNow = signature.Length > 0;
                bool signatureChanged = lastSignature != null
                    && !string.Equals(signature, lastSignature, StringComparison.Ordinal);
                if (presenceChanged || (signatureChanged && presentNow))
                    Interlocked.Exchange(ref _nodePresenceChanged, 1);
                lastSignature = signature;

                for (int i = 0; i < 10; i++)
                {
                    if (!NodeWatchActive(generation)) return;
                    try { Thread.Sleep(200); } catch (Exception) { return; }
                }
            }
        }

        private void PollEnvironmentCheck()
        {
            bool done;
            ProbeResult result = null;
            bool thenStart = false;
            bool openBrowser = false;
            bool suppressAutoInstall = false;
            int logHostState = -1;
            lock (_checkLock)
            {
                done = _checkDone;
                if (done)
                {
                    _checkDone = false;
                    result = _checkResult;
                    thenStart = _checkThenStart;
                    openBrowser = _checkOpenBrowser;
                    suppressAutoInstall = _checkSuppressAutoInstall;
                    _checkSuppressAutoInstall = false;
                    logHostState = _checkLogHostState;
                }
            }

            if (done)
            {
                if (logHostState >= 0) _logHostState = logHostState;

                ApplyCheckResult(result, thenStart, openBrowser, suppressAutoInstall);
                if (_exiting) return;
                // The result was deferred (or started a service/install). Leave the
                // queued install request in place for the next stable poll.
                if (IsServiceActionInProgress()) return;
            }

            // A request can be queued while no worker is running (for example when the
            // request arrived during Starting/Stopping). Process it once the state
            // machine is stable. The old early return on !_checkDone stranded such a
            // request forever.
            if (IsServiceActionInProgress()) return;

            // A menu click asked to install before the first probe had finished. The
            // request may also have been queued while a service action was in progress,
            // so evaluate it on every stable poll and keep it until a probe is available.
            bool installAfterCheck;
            bool installAfterCheckForce;
            bool installAfterCheckStart;
            bool installAfterCheckOpen;
            lock (_checkLock)
            {
                installAfterCheck = _installAfterCheck;
                installAfterCheckForce = _installAfterCheckForce;
                installAfterCheckStart = _installAfterCheckStart;
                installAfterCheckOpen = _installAfterCheckOpen;
            }
            if (installAfterCheck)
            {
                if (_phase == Phase.NeedsNode)
                {
                    lock (_checkLock)
                    {
                        _installAfterCheck = false;
                        _installAfterCheckForce = false;
                        _installAfterCheckStart = false;
                        _installAfterCheckOpen = false;
                    }
                }
                else if (_probe == null)
                {
                    bool busy;
                    lock (_checkLock) busy = _checkRunning || _checkDone;
                    if (!busy)
                        BeginEnvironmentCheck(installAfterCheckStart, installAfterCheckOpen);
                    return;
                }
                else
                {
                    lock (_checkLock)
                    {
                        _installAfterCheck = false;
                        _installAfterCheckForce = false;
                        _installAfterCheckStart = false;
                        _installAfterCheckOpen = false;
                    }
                    BeginInstall(installAfterCheckForce, installAfterCheckStart, installAfterCheckOpen);
                    if (IsServiceActionInProgress()) return;
                }
            }

            bool pending;
            bool pendingThenStart;
            bool pendingOpenBrowser;
            bool pendingSuppressAutoInstall;
            lock (_checkLock)
            {
                pending = _checkPending;
                pendingThenStart = _checkPendingThenStart;
                pendingOpenBrowser = _checkPendingOpenBrowser;
                pendingSuppressAutoInstall = _checkPendingSuppressAutoInstall;
                _checkPending = false;
                _checkPendingThenStart = false;
                _checkPendingOpenBrowser = false;
                _checkPendingSuppressAutoInstall = false;
            }
            if (pending)
                BeginEnvironmentCheck(pendingThenStart, pendingOpenBrowser, 0,
                    pendingSuppressAutoInstall);
        }

        private void ApplyCheckResult(ProbeResult result, bool thenStart, bool openBrowser,
            bool suppressAutoInstall)
        {
            // A service action started while the probe was in flight. Do not overwrite
            // Starting/Stopping/Installing; re-probe after the action reaches a stable
            // phase instead. This also prevents a second StartServer from launching a
            // competing node process.
            if (IsServiceActionInProgress())
            {
                lock (_checkLock)
                {
                    _checkPending = true;
                    _checkPendingThenStart = _checkPendingThenStart || thenStart;
                    _checkPendingOpenBrowser = _checkPendingOpenBrowser || openBrowser;
                    _checkPendingSuppressAutoInstall =
                        _checkPendingSuppressAutoInstall || suppressAutoInstall;
                }
                return;
            }

            if (result == null)
            {
                _nodePresenceNotified = false;
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("notify.envCheckFailed"), L.T("notify.envCheckFailedBody"));
                return;
            }

            bool firstCheck = _firstCheck;

            _probe = result;
            // Keep NodePath as the user's configured value. The effective executable
            // discovered by the probe is used for starting/stopping the service and
            // must not be persisted by the settings dialog as an explicit override.
            _nodePresenceKnown = true;
            _nodePresent = result.NodeFound;
            if (!result.NeedsNode) _nodePresenceNotified = false;

            // A background probe must not downgrade a service that is still
            // listening. Pi CLI is install-time only, and Node.js removal while a
            // service runs is reported separately by the presence watcher.
            if (KeepRunningWhenListening()) return;

            if (result.NeedsNode)
            {
                // An install queued behind this probe cannot run; do not execute it later
                // with stale force/start flags after Node.js has been installed.
                lock (_checkLock)
                {
                    _installAfterCheck = false;
                    _installAfterCheckForce = false;
                    _installAfterCheckStart = false;
                    _installAfterCheckOpen = false;
                }

                if (firstCheck && _firstCheckRetries < 2)
                {
                    // A login-time probe can lose a race with antivirus or profile setup
                    // and report a perfectly installed Node.js as missing. Re-probe twice
                    // before telling the user to install it.
                    _firstCheckRetries++;
                    _phase = Phase.Checking;
                    UpdateUi();
                    BeginEnvironmentCheck(thenStart, openBrowser,
                        _firstCheckRetries == 1 ? 2000 : 4000, suppressAutoInstall);
                    return;
                }

                _firstCheck = false;
                _firstCheckRetries = 0;

                _phase = Phase.NeedsNode;
                UpdateUi();
                if (firstCheck && _autoStartService)
                {
                    // A tray-only instance intentionally does not start Pi Web; keep it
                    // available for Settings and a later retry when Node.js is installed.
                    // A normal/start-no-open/autostart instance cannot do useful work
                    // without Node.js, so ask once and offer the download page.
                    ShowMissingNodePrompt(result);
                    return;
                }
                NotifyWarningOnce("needs-node",
                    L.T("notify.needsNodeTitle", ProbeResult.NodeMinimum),
                    L.T("notify.needsNodeBody", result.NodeIssue()));
                return;
            }

            _firstCheck = false;
            _firstCheckRetries = 0;

            if (result.NeedsPiWeb || result.NeedsPiCli)
            {
                // Auto-install is a startup behavior. A --tray-only instance must
                // not run npm or install packages just because it refreshed the
                // environment; the user can still choose Install/update service.
                if (_config.AutoInstall && !suppressAutoInstall && _autoStartService)
                {
                    // A manual install may already be queued behind this probe. It is
                    // satisfied by the auto-install start below, so consume its flags
                    // and carry its force/start/open intent into this install instead of
                    // running a second install after the follow-up check.
                    bool queuedForce;
                    bool queuedStart;
                    bool queuedOpen;
                    lock (_checkLock)
                    {
                        queuedForce = _installAfterCheckForce;
                        queuedStart = _installAfterCheckStart;
                        queuedOpen = _installAfterCheckOpen;
                        _installAfterCheck = false;
                        _installAfterCheckForce = false;
                        _installAfterCheckStart = false;
                        _installAfterCheckOpen = false;
                    }
                    BeginInstall(queuedForce, thenStart || queuedStart,
                        openBrowser || queuedOpen);
                    return;
                }
                // pi-web can run without the separately-installed Pi CLI. If the
                // caller explicitly asked to start (or is restoring a service after
                // a cancelled install), do not strand a runnable service in
                // NeedsDeps just because Pi CLI is absent.
                bool installQueued;
                lock (_checkLock) installQueued = _installAfterCheck;
                if (thenStart && !result.NeedsPiWeb && !installQueued
                    && CanStartServiceFromProbe(result, File.Exists))
                {
                    _phase = Phase.Stopped;
                    StartServer(openBrowser);
                    return;
                }
                _phase = Phase.NeedsDeps;
                UpdateUi();
                if (result.NeedsPiWeb)
                {
                    NotifyWarningOnce("needs-piweb",
                        L.T("notify.needsPiwebTitle"),
                        L.T("notify.needsPiwebBody"));
                }
                else
                {
                    NotifyWarningOnce("needs-picli",
                        L.T("notify.needsPiCliTitle"),
                        L.T("notify.needsPiCliBody"));
                }
                return;
            }

            if (thenStart)
            {
                _phase = Phase.Stopped;
                StartServer(openBrowser);
                return;
            }

            _lastNotified = "";
            RequestResolveServicePhase();
            UpdateUi();
            if (_phase == Phase.PortConflict) NotifyPortConflict();
        }

        private void ShowMissingNodePrompt(ProbeResult result)
        {
            Version version = WindowsVersion.Current;
            bool community = NodeDownloads.UsesCommunityBuild(version);

            string url = NodeDownloads.UrlFor(version);
            // ForcedReason is only for the PI_WEB_TRAY_NO_NODE test hook. Do not
            // expose test-hook wording in the release prompt; show the normal
            // missing-Node explanation instead.
            string issue = string.IsNullOrEmpty(result.ForcedReason)
                ? result.NodeIssue()
                : L.T("probe.issue.noNode");
            string body = L.T("node.missingBody", issue, ProbeResult.NodeMinimum, url);
            if (community) body += "\r\n" + L.T("node.communityNote");

            DialogResult answer;
            _nodePromptOpen = true;
            BeginModal();
            try
            {
                using (NodePromptForm prompt = new NodePromptForm(
                    L.T("node.missingTitle"), body, true))
                {
                    answer = prompt.ShowDialog();
                }
            }
            finally
            {
                EndModal();
                _nodePromptOpen = false;
            }
            // Esc and the title-bar close button are not a request to exit the tray.
            // Only the explicit Exit button (No) stops the tray.
            if (answer == DialogResult.Ignore || answer == DialogResult.Yes
                || answer == DialogResult.Cancel || answer == DialogResult.None)
            {
                // Downloading must not shut the tray down: the user may install Node.js
                // while it is running, and the background watcher will continue.
                if (answer == DialogResult.Yes) OpenUrl(url);
                return;
            }

            // Stop only the process tree this tray started. Another pi-web instance
            // or a service from before the prompt is left alone.
            ExitWithoutStoppingService(true);
        }

        private void RunSelfCheck()
        {
            if (_selfCheckRunning) return;
            _selfCheckRunning = true;
            UpdateUi();
            try
            {
                // Show the report immediately and let it probe on its own background
                // thread. Previously the menu action stayed visually idle until the
                // whole environment probe had finished.
                ProbeResult initial = _probe ?? new ProbeResult();
                ProbeResult latest;
                BeginModal();
                try
                {
                    latest = CheckForm.ShowReport(_config, initial, _elevated, true);
                }
                finally
                {
                    EndModal();
                }
                ApplyProbeResult(latest);
            }
            catch (Exception ex)
            {
                BeginModal();
                try
                {
                    MessageBox.Show(L.T("notify.selfCheckError", ex.Message), L.T("sc.title"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    EndModal();
                }
            }
            finally
            {
                _selfCheckRunning = false;
                UpdateUi();
            }
        }
    }
}
