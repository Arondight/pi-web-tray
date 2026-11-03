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
                ProbeResult result = null;
                try
                {
                    result = EnvironmentProbe.Detect(_config);
                }
                catch (Exception)
                {
                }
                lock (_checkLock)
                {
                    _checkResult = result;
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
            lock (_checkLock)
            {
                if (!_checkDone) return;
                _checkDone = false;
                result = _checkResult;
                thenStart = _checkThenStart;
                openBrowser = _checkOpenBrowser;
            }

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
            _firstCheck = false;

            _probe = result;
            _config.NodePath = result.NodePath;
            if (result.PiWebFound) _config.ServerScript = result.PiWebScript;

            if (result.NeedsNode)
            {
                _phase = Phase.NeedsNode;
                UpdateUi();
                if (firstCheck)
                {
                    // Nothing useful can run without Node.js, and a tray that stays behind
                    // keeps a stale environment (PATH/PATH from logon), so ask once, offer
                    // the download page and exit instead of sitting in a broken state.
                    ShowMissingNodeAndExit(result);
                    return;
                }
                NotifyWarningOnce("needs-node",
                    L.T("notify.needsNodeTitle", ProbeResult.NodeMinimum),
                    L.T("notify.needsNodeBody", result.NodeIssue()));
                return;
            }

            if (result.NeedsPiWeb)
            {
                if (_config.AutoInstall)
                {
                    BeginInstall(false, thenStart, openBrowser);
                    return;
                }
                _phase = Phase.NeedsDeps;
                UpdateUi();
                NotifyWarningOnce("needs-piweb",
                    L.T("notify.needsPiwebTitle"),
                    L.T("notify.needsPiwebBody"));
                return;
            }

            if (thenStart)
            {
                _phase = Phase.Stopped;
                StartServer(openBrowser);
                return;
            }

            _lastNotified = "";
            _phase = ResolveServicePhase();
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
