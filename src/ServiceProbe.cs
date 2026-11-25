// Pi Web Tray — identify the process that owns the configured port
//
// "A TCP listener exists" is not proof that pi-web is running. The tray uses this
// helper before it treats the port as Running and before it stops or kills anything,
// so another program on the same port is never mistaken for pi-web.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

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
        internal DateTime StartTime = DateTime.MinValue;
        internal string Detail = "";
        internal string ScriptPath = "";
        internal bool ForeignSession;
        internal bool ForeignUser;
        // The probe itself failed (for example a process API access error or a
        // worker start failure). This is not evidence that another program owns
        // the port; callers must not kill the tracked service in that case.
        internal bool CheckFailed;
        // The listener exists, but its command line could not be read. It may be
        // our service or a foreign process, so ownership is not proven. Callers
        // must not stop/kill it or replace files while it runs.
        internal bool ProbeIncomplete;

        internal bool IsPiWeb { get { return Owner == PortOwner.PiWeb; } }
        internal bool IsUnknown { get { return Owner == PortOwner.Unknown; } }
    }

    internal static class ServiceProbe
    {
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
            if (string.IsNullOrEmpty(_currentUserSid))
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        string sid = identity.User != null ? identity.User.Value : "";
                        // Cache only a real value. A transient failure or a missing
                        // token user must not poison every later owner probe.
                        if (!string.IsNullOrEmpty(sid)) _currentUserSid = sid;
                    }
                }
                catch (Exception)
                {
                    return "";
                }
            }
            return _currentUserSid ?? "";
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
            // Capture the creation time immediately after the listener PID is known.
            // Waiting until the rest of the probe (including the HTTP fallback) has
            // completed leaves a window in which the PID can exit and be reused.
            DateTime listenerStart = pid > 4 ? Sys.ProcessStartTime(pid) : DateTime.MinValue;

            // Another Windows session can run the same pi-web package on the same port.
            // It is never "our" service and must never be opened or stopped by this tray.
            int ownerSession = pid > 4 ? NativeProcess.SessionId(pid) : -1;
            if (ownerSession >= 0 && ownerSession != Process.GetCurrentProcess().SessionId)
            {
                return new PortOwnerInfo
                {
                    Owner = PortOwner.Unknown,
                    Pid = pid,
                    StartTime = listenerStart,
                    ForeignSession = true,
                    Detail = "another Windows session"
                };
            }

            // Same session but another user (for example runas / a shared global install)
            // is still not our service. If the token cannot be read, fall through to the
            // script-path check.
            string ownerSid = pid > 4 ? NativeProcess.UserSid(pid) : "";
            string currentSid = CurrentUserSid();
            bool userUnknown = pid > 4
                && (ownerSid.Length == 0 || currentSid.Length == 0);
            if (ownerSid.Length > 0 && currentSid.Length > 0
                && !string.Equals(ownerSid, currentSid, StringComparison.OrdinalIgnoreCase))
            {
                return new PortOwnerInfo
                {
                    Owner = PortOwner.Unknown,
                    Pid = pid,
                    StartTime = listenerStart,
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
                    StartTime = listenerStart,
                    Detail = "Pi Web command line",
                    ScriptPath = scriptPath,
                    // A matching script path is not proof that this is our
                    // account's process when the token SID could not be read.
                    ProbeIncomplete = userUnknown
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
                    StartTime = listenerStart,
                    Detail = "Pi Web HTTP response",
                    ProbeIncomplete = true
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
                StartTime = listenerStart,
                Detail = detail,
                ProbeIncomplete = string.IsNullOrEmpty(commandLine) || userUnknown
            };
        }

        internal static string OwnerText(PortOwnerInfo owner)
        {
            if (owner == null) return L.T("check.ownerNone");
            if (owner.CheckFailed || owner.ProbeIncomplete)
                return L.T("check.ownerUnavailable");
            if (owner.ForeignSession) return L.T("check.ownerOtherSession");
            if (owner.ForeignUser) return L.T("check.ownerOtherUser");
            if (owner.IsPiWeb) return L.T("check.ownerPiweb");
            if (owner.IsUnknown) return L.T("check.ownerOther");
            return L.T("check.ownerNone");
        }

        internal static bool IsPiWebCommandLine(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return false;

            // Only the actual node script argument identifies the service. A
            // command-line option or config value that happens to contain "pi-web.js"
            // must not make an unrelated listener look like this installation.
            string script = FirstScriptArgument(commandLine);
            if (!string.IsNullOrEmpty(script))
            {
                if (IsPiWebScriptPath(script)) return true;
                // A normal .js/.cjs/.mjs script is enough to prove this is not the
                // pi-web package. Do not fall back to scanning unrelated argument
                // values for @agegr.
                if (LooksLikeScript(script)) return false;
            }

            // A pi-web child (for example the next start process) may show the package
            // path in one of its module arguments instead of naming pi-web.js as its
            // first script. Keep that parent-discovery fallback for a non-script
            // launcher, but never use it when a concrete standard script was found.
            return ContainsPiWebPackageMarker(commandLine);
        }

        private static bool IsPiWebScriptPath(string path)
        {
            return path.IndexOf("pi-web.js", StringComparison.OrdinalIgnoreCase) >= 0
                || ContainsPiWebPackageMarker(path);
        }

        private static bool ContainsPiWebPackageMarker(string text)
        {
            return text.IndexOf("@agegr\\pi-web", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("@agegr/pi-web", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Extract the script argument of the node process. Scanning every token for
        // "pi-web.js" lets a benign process such as
        //   node listener.js --config C:\...\pi-web.js
        // pass the ownership check. The first non-option argument after the executable
        // is the script node actually runs; later tokens are application arguments.
        internal static string ExtractScriptPath(string commandLine)
        {
            return ExtractScriptPath(commandLine, "");
        }

        internal static string ExtractScriptPath(string commandLine, string expectedScript)
        {
            string script = FirstScriptArgument(commandLine);
            if (string.IsNullOrEmpty(script)) return "";
            if (IsExpectedScript(script, expectedScript)
                || IsPiWebScriptPath(script))
            {
                return script;
            }
            return "";
        }

        private static string FirstScriptArgument(string commandLine)
        {
            List<string> arguments = SplitCommandLine(commandLine);
            if (arguments.Count < 2) return "";

            bool afterOptionTerminator = false;
            for (int i = 1; i < arguments.Count; i++)
            {
                string value = arguments[i];
                if (!afterOptionTerminator)
                {
                    if (string.Equals(value, "--", StringComparison.Ordinal))
                    {
                        afterOptionTerminator = true;
                        continue;
                    }
                    // Node options precede the script. A value that is not an option
                    // is the candidate script; do not scan later application arguments.
                    if (value.Length > 0 && value[0] == '-')
                    {
                        // Some node options consume the next argument as a module/file
                        // value (node --require helper.js app.js). That value is not the
                        // script the process runs; skip it too.
                        if (OptionTakesSeparateValue(value)) i++;
                        continue;
                    }
                }
                return value;
            }
            return "";
        }

        private static bool OptionTakesSeparateValue(string option)
        {
            // The inline "--require=x" form is a single token and is not listed here.
            return string.Equals(option, "--require", StringComparison.Ordinal)
                || string.Equals(option, "-r", StringComparison.Ordinal)
                || string.Equals(option, "--import", StringComparison.Ordinal)
                || string.Equals(option, "--loader", StringComparison.Ordinal)
                || string.Equals(option, "--experimental-loader", StringComparison.Ordinal);
        }

        private static bool LooksLikeScript(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            try
            {
                string name = Path.GetFileName(value);
                if (name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return ContainsPiWebPackageMarker(value);
        }

        private static List<string> SplitCommandLine(string commandLine)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(commandLine)) return result;

            IntPtr argv = CommandLineToArgvW(commandLine, out int count);
            if (argv == IntPtr.Zero) return result;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr value = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                    string text = value == IntPtr.Zero ? "" : Marshal.PtrToStringUni(value);
                    if (!string.IsNullOrEmpty(text)) result.Add(text);
                }
            }
            finally
            {
                LocalFree(argv);
            }
            return result;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

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
            // A probe that could not read the command line/owner is not proof of
            // ownership, even if a partial script path was recovered.
            if (owner.ProbeIncomplete) return false;
            // Ownership requires both paths. A listener whose command line does not expose
            // pi-web.js (including the HTTP fallback) is not proof that it is our service.
            if (string.IsNullOrEmpty(expectedScript) || string.IsNullOrEmpty(owner.ScriptPath))
                return false;
            // Compare the file identity for local paths so an 8.3 spelling of the
            // same script is not reported as a foreign install. A different junction
            // alias is rejected conservatively by SameLocalFile.
            return PathSafety.SameLocalFile(owner.ScriptPath, expectedScript);
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
