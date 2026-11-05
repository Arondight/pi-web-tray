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
                if (_checkRunning)
                {
                    // A check is already in flight: remember the request instead of dropping it,
                    // otherwise "install finished -> check -> start" can silently lose the start.
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
            }

            if (_phase != Phase.Installing) _phase = Phase.Checking;
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
                catch (Exception)
                {
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
            worker.Start();
        }

        // The UI thread only consumes a flag set by WatchNodePresence. All FileInfo,
        // DirectoryInfo and registry PATH reads happen on the worker thread.
        private void PollNodePresence()
        {
            if (!_nodeWatching) return;
            // Leave the flag set while an update owns the process; restarting or installing
            // during an update could race with the helper that replaces the executable.
            if (UpdateBusy()) return;
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
            worker.Start();
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
            ProbeResult result;
            bool thenStart;
            bool openBrowser;
            bool suppressAutoInstall;
            int logHostState;
            lock (_checkLock)
            {
                if (!_checkDone) return;
                _checkDone = false;
                result = _checkResult;
                thenStart = _checkThenStart;
                openBrowser = _checkOpenBrowser;
                suppressAutoInstall = _checkSuppressAutoInstall;
                _checkSuppressAutoInstall = false;
                logHostState = _checkLogHostState;
            }

            if (logHostState >= 0) _logHostState = logHostState;

            ApplyCheckResult(result, thenStart, openBrowser, suppressAutoInstall);
            if (_exiting) return;

            // A menu click asked to install before the first probe had finished. The probe has
            // just supplied the paths/versions the installer needs, so run it now.
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
                _installAfterCheck = false;
                _installAfterCheckForce = false;
                _installAfterCheckStart = false;
                _installAfterCheckOpen = false;
            }
            if (installAfterCheck && _phase != Phase.Installing && _phase != Phase.NeedsNode && _probe != null)
            {
                BeginInstall(installAfterCheckForce, installAfterCheckStart, installAfterCheckOpen);
            }

            // A request arrived while this check was running (install finished, icon clicked, ...)
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
            if (pending && _phase != Phase.Installing)
                BeginEnvironmentCheck(pendingThenStart, pendingOpenBrowser, 0,
                    pendingSuppressAutoInstall);
        }

        private void ApplyCheckResult(ProbeResult result, bool thenStart, bool openBrowser,
            bool suppressAutoInstall)
        {
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
            _config.NodePath = result.NodePath;
            _nodePresenceKnown = true;
            _nodePresent = result.NodeFound;
            if (!result.NeedsNode) _nodePresenceNotified = false;

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
                if (_config.AutoInstall && !suppressAutoInstall)
                {
                    BeginInstall(false, thenStart, openBrowser);
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
            using (NodePromptForm prompt = new NodePromptForm(
                L.T("node.missingTitle"), body, true))
            {
                answer = prompt.ShowDialog();
            }
            if (answer == DialogResult.Ignore || answer == DialogResult.Yes)
            {
                // Downloading must not shut the tray down: the user may install Node.js
                // while it is running, and the background watcher will continue.
                if (answer == DialogResult.Yes) OpenUrl(url);
                return;
            }

            ExitWithoutStoppingService();
        }

        private void RunSelfCheck()
        {
            if (_selfCheckRunning) return;
            _selfCheckRunning = true;
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    _selfCheckResult = EnvironmentProbe.Detect(_config);
                }
                catch (Exception ex)
                {
                    _selfCheckError = ex.Message;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void PollSelfCheck()
        {
            ProbeResult result = _selfCheckResult;
            string error = _selfCheckError;
            if (result == null && error == null) return;
            _selfCheckRunning = false;
            _selfCheckResult = null;
            _selfCheckError = null;

            if (result != null)
            {
                ProbeResult latest = CheckForm.ShowReport(_config, result, _elevated);
                ApplyProbeResult(latest);
            }
            else
            {
                MessageBox.Show(L.T("notify.selfCheckError", error), L.T("sc.title"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
