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
            BeginEnvironmentCheck(thenStart, openBrowser, 0);
        }

        // delayMs gives a login-time probe a moment: antivirus may still be scanning
        // node.exe and the profile/registry may not be fully ready right after logon.
        private void BeginEnvironmentCheck(bool thenStart, bool openBrowser, int delayMs)
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
                    return;
                }
                _checkRunning = true;
                _checkDone = false;
                _checkResult = null;
                _checkThenStart = thenStart;
                _checkOpenBrowser = openBrowser;
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

        private void PollEnvironmentCheck()
        {
            ProbeResult result;
            bool thenStart;
            bool openBrowser;
            int logHostState;
            lock (_checkLock)
            {
                if (!_checkDone) return;
                _checkDone = false;
                result = _checkResult;
                thenStart = _checkThenStart;
                openBrowser = _checkOpenBrowser;
                logHostState = _checkLogHostState;
            }

            if (logHostState >= 0) _logHostState = logHostState;

            ApplyCheckResult(result, thenStart, openBrowser);
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
            lock (_checkLock)
            {
                pending = _checkPending;
                pendingThenStart = _checkPendingThenStart;
                pendingOpenBrowser = _checkPendingOpenBrowser;
                _checkPending = false;
                _checkPendingThenStart = false;
                _checkPendingOpenBrowser = false;
            }
            if (pending && _phase != Phase.Installing)
                BeginEnvironmentCheck(pendingThenStart, pendingOpenBrowser);
        }

        private void ApplyCheckResult(ProbeResult result, bool thenStart, bool openBrowser)
        {
            if (result == null)
            {
                _phase = Phase.Failed;
                UpdateUi();
                NotifyError(L.T("notify.envCheckFailed"), L.T("notify.envCheckFailedBody"));
                return;
            }

            bool firstCheck = _firstCheck;

            _probe = result;
            _config.NodePath = result.NodePath;

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
                    // before telling the user to install it and exiting.
                    _firstCheckRetries++;
                    _phase = Phase.Checking;
                    UpdateUi();
                    BeginEnvironmentCheck(thenStart, openBrowser,
                        _firstCheckRetries == 1 ? 2000 : 4000);
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
                    // without Node.js, so ask once, offer the download page and exit.
                    ShowMissingNodeAndExit(result);
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
                if (_config.AutoInstall)
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

        private void ShowMissingNodeAndExit(ProbeResult result)
        {
            Version version = WindowsVersion.Current;
            bool community = NodeDownloads.UsesCommunityBuild(version);

            // The community build is 64-bit only, so a 32-bit legacy system has nothing
            // to install; explain that and exit instead of opening a page without binaries.
            if (community && !Environment.Is64BitOperatingSystem)
            {
                MessageBox.Show(
                    L.T("node.unsupportedBody"),
                    L.T("node.unsupportedTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                ExitWithoutStoppingService();
                return;
            }

            string url = NodeDownloads.UrlFor(version);
            string body = L.T("node.missingBody", result.NodeIssue(), ProbeResult.NodeMinimum, url);
            if (community) body += "\r\n\r\n" + L.T("node.communityNote");

            DialogResult answer = MessageBox.Show(
                body,
                L.T("node.missingTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (answer == DialogResult.Yes) OpenUrl(url);

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
