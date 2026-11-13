// Pi Web Tray — identify the process that owns the configured port
//
// "A TCP listener exists" is not proof that pi-web is running. The tray uses this
// helper before it treats the port as Running and before it stops or kills anything,
// so another program on the same port is never mistaken for pi-web.

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace PiWebTray
{
    internal enum PortOwner
    {
        None,
        PiWeb,
        Unknown
    }

    internal sealed class PortOwnerInfo
    {
        internal PortOwner Owner;
        internal int Pid;
        internal string Detail = "";
        internal string ScriptPath = "";
        internal bool ForeignSession;
        internal bool ForeignUser;
        // The probe itself failed (for example a process API access error or a
        // worker start failure). This is not evidence that another program owns
        // the port; callers must not kill the tracked service in that case.
        internal bool CheckFailed;

        internal bool IsPiWeb { get { return Owner == PortOwner.PiWeb; } }
        internal bool IsUnknown { get { return Owner == PortOwner.Unknown; } }
    }

    internal static class ServiceProbe
    {
        private static readonly Regex QuotedArgument = new Regex("\"([^\"]+)\"", RegexOptions.Compiled);

        private static readonly object CacheLock = new object();
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

        private static int _cachePort = -1;
        private static string _cacheScript = "";
        private static PortOwnerInfo _cache;
        private static DateTime _cacheAt = DateTime.MinValue;
        private static int _cacheGeneration;
        private static string _currentUserSid;

        private static string CurrentUserSid()
        {
            if (_currentUserSid == null)
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        _currentUserSid = identity.User != null ? identity.User.Value : "";
                    }
                }
                catch (Exception)
                {
                    _currentUserSid = "";
                }
            }
            return _currentUserSid;
        }

        internal static PortOwnerInfo Inspect(int port)
        {
            return Inspect(port, "");
        }

        internal static PortOwnerInfo Inspect(int port, string expectedScript)
        {
            expectedScript = (expectedScript ?? "").Trim();
            int generation;
            lock (CacheLock)
            {
                if (_cache != null && _cachePort == port
                    && string.Equals(_cacheScript, expectedScript, StringComparison.OrdinalIgnoreCase)
                    && DateTime.Now - _cacheAt < CacheLifetime)
                    return _cache;
                generation = _cacheGeneration;
            }

            PortOwnerInfo result = InspectUncached(port, expectedScript);

            lock (CacheLock)
            {
                // A background probe can finish after Invalidate(). Do not let it
                // repopulate the cache with a result that was explicitly discarded.
                if (generation == _cacheGeneration)
                {
                    _cachePort = port;
                    _cacheScript = expectedScript;
                    _cache = result;
                    _cacheAt = DateTime.Now;
                }
            }
            return result;
        }

        internal static void Invalidate()
        {
            lock (CacheLock)
            {
                _cacheGeneration++;
                _cache = null;
                _cachePort = -1;
                _cacheScript = "";
                _cacheAt = DateTime.MinValue;
            }
        }

        private static PortOwnerInfo InspectUncached(int port, string expectedScript)
        {
            if (!Net.IsPortOpen(port, 250))
            {
                return new PortOwnerInfo { Owner = PortOwner.None, Detail = "port is not listening" };
            }

            int pid = FindListenerPid(port);

            // Another Windows session can run the same pi-web package on the same port.
            // It is never "our" service and must never be opened or stopped by this tray.
            int ownerSession = pid > 4 ? NativeProcess.SessionId(pid) : -1;
            if (ownerSession >= 0 && ownerSession != Process.GetCurrentProcess().SessionId)
            {
                return new PortOwnerInfo
                {
                    Owner = PortOwner.Unknown,
                    Pid = pid,
                    ForeignSession = true,
                    Detail = "another Windows session"
                };
            }

            // Same session but another user (for example runas / a shared global install)
            // is still not our service. If the token cannot be read, fall through to the
            // script-path check.
            string ownerSid = pid > 4 ? NativeProcess.UserSid(pid) : "";
            string currentSid = CurrentUserSid();
            if (ownerSid.Length > 0 && currentSid.Length > 0
                && !string.Equals(ownerSid, currentSid, StringComparison.OrdinalIgnoreCase))
            {
                return new PortOwnerInfo
                {
                    Owner = PortOwner.Unknown,
                    Pid = pid,
                    ForeignUser = true,
                    Detail = "another user"
                };
            }

            string commandLine = pid > 4 ? ReadCommandLine(pid) : "";

            string expectedPath = ExtractScriptPath(commandLine, expectedScript);
            if (IsPiWebCommandLine(commandLine) || expectedPath.Length > 0)
            {
                int listenerPid = pid;
                string scriptPath = expectedPath;
                if (scriptPath.Length == 0) scriptPath = ExtractScriptPath(commandLine);
                // pi-web can spawn a child `next start` process that owns the
                // listening socket. Its own command line has @agegr\pi-web but no
                // pi-web.js; the parent node process carries the actual script path.
                int parentPid = pid;
                for (int depth = 0; string.IsNullOrEmpty(scriptPath) && depth < 3; depth++)
                {
                    parentPid = NativeProcess.ParentPid(parentPid);
                    if (parentPid <= 4) break;
                    scriptPath = ExtractScriptPath(ReadCommandLine(parentPid), expectedScript);
                    if (scriptPath.Length == 0)
                        scriptPath = ExtractScriptPath(ReadCommandLine(parentPid));
                }
                return new PortOwnerInfo
                {
                    Owner = PortOwner.PiWeb,
                    Pid = listenerPid,
                    Detail = "Pi Web command line",
                    ScriptPath = scriptPath
                };
            }

            // Protected processes can deny command-line access. A local HTTP body check
            // cannot prove ownership, but it is a useful fallback when the command line
            // cannot be read and avoids a false port-conflict warning for pi-web.
            if (string.IsNullOrEmpty(commandLine) && Net.IsPiWebHttp(port, 1500))
            {
                return new PortOwnerInfo
                {
                    Owner = PortOwner.PiWeb,
                    Pid = pid,
                    Detail = "Pi Web HTTP response"
                };
            }

            string detail = string.IsNullOrEmpty(commandLine)
                ? "process details are unavailable"
                : "process is not Pi Web";
            string imagePath = pid > 4 ? NativeProcess.ImagePath(pid) : "";
            if (!string.IsNullOrEmpty(imagePath)) detail += " (" + imagePath + ")";

            return new PortOwnerInfo
            {
                Owner = PortOwner.Unknown,
                Pid = pid,
                Detail = detail
            };
        }

        internal static string OwnerText(PortOwnerInfo owner)
        {
            if (owner == null) return L.T("check.ownerNone");
            if (owner.ForeignSession) return L.T("check.ownerOtherSession");
            if (owner.ForeignUser) return L.T("check.ownerOtherUser");
            if (owner.IsPiWeb) return L.T("check.ownerPiweb");
            if (owner.IsUnknown) return L.T("check.ownerOther");
            return L.T("check.ownerNone");
        }

        internal static bool IsPiWebCommandLine(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return false;
            string text = commandLine.ToLowerInvariant();

            // The service command line contains the pi-web.js script:
            //   "node.exe" "...\@agegr\pi-web\bin\pi-web.js" --no-open -p 30141
            // Also accept the npm package path for renamed/launcher variants.
            if (text.IndexOf("pi-web.js", StringComparison.Ordinal) >= 0) return true;
            if (text.IndexOf("@agegr\\pi-web", StringComparison.Ordinal) >= 0) return true;
            if (text.IndexOf("@agegr/pi-web", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Extract the pi-web.js path from a command line so a different installation
        // (same session) is not mistaken for the service this tray manages.
        internal static string ExtractScriptPath(string commandLine)
        {
            return ExtractScriptPath(commandLine, "");
        }

        internal static string ExtractScriptPath(string commandLine, string expectedScript)
        {
            if (string.IsNullOrEmpty(commandLine)) return "";

            foreach (Match match in QuotedArgument.Matches(commandLine))
            {
                string value = match.Groups[1].Value;
                if (IsExpectedScript(value, expectedScript)
                    || value.IndexOf("pi-web.js", StringComparison.OrdinalIgnoreCase) >= 0)
                    return value;
            }
            foreach (string raw in commandLine.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = raw.Trim('"');
                if (IsExpectedScript(value, expectedScript)
                    || value.IndexOf("pi-web.js", StringComparison.OrdinalIgnoreCase) >= 0)
                    return value;
            }
            return "";
        }

        private static bool IsExpectedScript(string value, string expectedScript)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(expectedScript)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(value),
                    Path.GetFullPath(expectedScript),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool MatchesExpectedScript(PortOwnerInfo owner, string expectedScript)
        {
            if (owner == null || !owner.IsPiWeb) return false;
            // Ownership requires both paths. A listener whose command line does not expose
            // pi-web.js (including the HTTP fallback) is not proof that it is our service.
            if (string.IsNullOrEmpty(expectedScript) || string.IsNullOrEmpty(owner.ScriptPath))
                return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(owner.ScriptPath),
                    Path.GetFullPath(expectedScript),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // A path we cannot normalize is not proof of ownership.
                return false;
            }
        }

        internal static int ParseListenerPid(string netstatOutput, int port)
        {
            if (string.IsNullOrEmpty(netstatOutput) || port <= 0) return 0;

            string needle = ":" + port;
            foreach (string rawLine in netstatOutput.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                // netstat is localized; match the local endpoint instead of the English state text.

                string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!parts[1].EndsWith(needle, StringComparison.Ordinal)) continue;
                // A listener has a wildcard remote endpoint; established connections do not.
                if (!parts[2].EndsWith(":0", StringComparison.Ordinal)) continue;

                int pid;
                if (!int.TryParse(parts[4], out pid)) continue;
                if (pid > 0) return pid;
            }
            return 0;
        }

        private static int FindListenerPid(int port)
        {
            // The listener-only table keeps the owning PID even when Windows reports a
            // bogus state for a newly created listener. Use it before spawning netstat,
            // whose output is filtered on the (possibly wrong) LISTEN state.
            try
            {
                int tablePid = TcpListenerTable.ListenerPid(port);
                if (tablePid > 0) return tablePid;
            }
            catch (Exception)
            {
                // Fall back to netstat; a native table failure must not abort the
                // owner check or the tray timer.
            }

            try
            {
                // Never call ReadToEnd() before a timeout: a hung netstat would block
                // the owner-check worker forever. ProcessRunner enforces the timeout,
                // kills the child tree and lets us parse whatever output was captured.
                ProcessResult run = ProcessRunner.Run(
                    Sys.SystemExecutable("netstat.exe"), "-ano", Sys.SystemDirectory,
                    3000, null, null, null, 4 * 1024 * 1024);
                if (!run.Started || run.TimedOut) return 0;
                return ParseListenerPid(run.Output, port);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        internal static int ParentPid(int pid)
        {
            return NativeProcess.ParentPid(pid);
        }

        private static string ReadCommandLine(int pid)
        {
            return NativeProcess.CommandLine(pid);
        }
    }
}
