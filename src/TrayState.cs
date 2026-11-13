// Pi Web Tray — instance identity and runtime state
//
// One instance with fixed names by default. To run several configurations on one machine, set
// PI_WEB_TRAY_INSTANCE=dev and mutex, signals and data dir all get a suffix and stay independent.
//
// PI_WEB_TRAY_HOME overrides the data directory entirely (default %LOCALAPPDATA%\pi-web-tray).

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace PiWebTray
{
    internal static class Instance
    {
        private const string BaseName = "PiWebTray";

        internal static string Suffix
        {
            get
            {
                string raw = Environment.GetEnvironmentVariable("PI_WEB_TRAY_INSTANCE");
                if (string.IsNullOrEmpty(raw)) return "";

                // Filesystem directories, HKCU Run values and Task Scheduler names
                // are case-insensitive. Normalize so Dev/dev cannot share a data
                // directory or autostart entry while getting separate mutex/signal
                // objects.
                string normalized = raw.Trim().ToLowerInvariant();
                StringBuilder sb = new StringBuilder();
                foreach (char c in normalized)
                {
                    if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                }
                string clean = sb.ToString();
                bool changed = !string.Equals(clean, normalized, StringComparison.Ordinal);
                if (clean.Length > 32)
                {
                    clean = clean.Substring(0, 32);
                    changed = true;
                }
                if (clean.Length == 0)
                {
                    clean = "i";
                    changed = true;
                }
                // If sanitising or truncating lost information, append a short hash
                // of the original name. Without it "a b" and "ab" would silently
                // become the same instance and could stop each other's service.
                if (changed) clean += "-" + ShortHash(normalized);
                return "." + clean;
            }
        }

        private static string ShortHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                StringBuilder text = new StringBuilder(8);
                for (int i = 0; i < 4 && i < hash.Length; i++)
                    text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        internal static string MutexName { get { return @"Local\" + BaseName + ".C2B1B0" + Suffix; } }
        internal static string OpenSignalName { get { return @"Local\" + BaseName + ".Open" + Suffix; } }
        internal static string StartSignalName { get { return @"Local\" + BaseName + ".Start" + Suffix; } }
        internal static string StopSignalName { get { return @"Local\" + BaseName + ".Stop" + Suffix; } }
        internal static string QuitSignalName { get { return @"Local\" + BaseName + ".Quit" + Suffix; } }
        internal static string QuitKeepServiceSignalName
        {
            get { return @"Local\" + BaseName + ".QuitKeepService" + Suffix; }
        }

        internal static string DataFolder
        {
            get
            {
                try
                {
                    string home = (Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME") ?? "").Trim();
                    if (!string.IsNullOrEmpty(home))
                    {
                        string expanded = Environment.ExpandEnvironmentVariables(home);
                        // A named instance must not share its ini, logs, state file or
                        // update staging with another instance just because HOME is set.
                        // Keep the suffix inside HOME so parallel testing remains isolated.
                        if (Suffix.Length > 0)
                            expanded = Path.Combine(expanded, "pi-web-tray" + Suffix);
                        if (!Path.IsPathRooted(expanded))
                            expanded = Path.Combine(Application.StartupPath, expanded);
                        return Path.GetFullPath(expanded);
                    }
                    return Path.Combine(Shell.LocalAppData(), "pi-web-tray" + Suffix);
                }
                catch (Exception)
                {
                    // A malformed HOME must not prevent the process from starting.
                    return Path.Combine(Shell.LocalAppData(), "pi-web-tray" + Suffix);
                }
            }
        }
    }

    // Runtime state file, used by the launcher to decide whether an elevated takeover is needed
    internal sealed class TrayState
    {
        internal static string Folder { get { return Instance.DataFolder; } }

        internal static string StatePath()
        {
            return Path.Combine(Folder, "state.ini");
        }

        // A user stop can arrive while the update helper is already waiting to
        // restart the tray. state.ini is deleted on exit, so this marker must live
        // independently until a service is started again or an update helper reads
        // it. It is a user-intent marker, not security-sensitive state.
        internal const string ServiceStopRequestName = "service.stop.requested";

        internal static string ServiceStopRequestPath()
        {
            return ServiceStopRequestPath(Folder);
        }

        internal static string ServiceStopRequestPath(string folder)
        {
            return Path.Combine(folder ?? Folder, ServiceStopRequestName);
        }

        internal static void RequestServiceStop()
        {
            RequestServiceStop(Folder);
        }

        internal static void RequestServiceStop(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string path = ServiceStopRequestPath(folder);
                // The marker is one-shot: if it already exists the stop intent is
                // already recorded. Never delete and recreate it, which would leave a
                // window in which an update helper could miss the marker.
                if (File.Exists(path)) return;
                if (PathSafety.ContainsReparsePoint(path)) return;
                // CreateNew cannot follow a reparse point planted at the marker name
                // between the check and the write. The marker is user intent, but an
                // elevated tray must not turn it into an arbitrary file write.
                using (FileStream stream = new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    byte[] data = new UTF8Encoding(false).GetBytes("1");
                    stream.Write(data, 0, data.Length);
                }
            }
            catch (Exception)
            {
            }
        }

        internal static void ClearServiceStopRequest()
        {
            ClearServiceStopRequest(Folder);
        }

        internal static void ClearServiceStopRequest(string folder)
        {
            try
            {
                string path = ServiceStopRequestPath(folder);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        internal static bool ServiceStopRequested()
        {
            return ServiceStopRequested(Folder);
        }

        internal static bool ServiceStopRequested(string folder)
        {
            try
            {
                string path = ServiceStopRequestPath(folder);
                return File.Exists(path) && !PathSafety.ContainsReparsePoint(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void Save(int pid, bool elevated, int port)
        {
            string path = StatePath();
            try
            {
                Directory.CreateDirectory(Folder);
                // Never follow a symlink planted at the known state-file name.
                if (PathSafety.ContainsReparsePoint(path)) return;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Pid=" + pid);
                sb.AppendLine("Elevated=" + (elevated ? "1" : "0"));
                sb.AppendLine("Port=" + port);
                sb.AppendLine("Url=http://127.0.0.1:" + port);
                sb.AppendLine("Session=" + Process.GetCurrentProcess().SessionId);

                // Write beside the destination and replace it atomically. A CLI process
                // can read this file at any time; a partially written Pid/Port line must
                // not make it fall back to the wrong port.
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                string backup = path + "." + Guid.NewGuid().ToString("N") + ".bak";
                if (PathSafety.ContainsReparsePoint(temp)
                    || PathSafety.ContainsReparsePoint(backup)) return;
                try
                {
                    File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(true));
                    if (File.Exists(path))
                    {
                        try { File.Replace(temp, path, backup, true); }
                        catch (Exception)
                        {
                            if (File.Exists(path)) File.Delete(path);
                            File.Move(temp, path);
                        }
                    }
                    else
                    {
                        File.Move(temp, path);
                    }
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                    try { if (File.Exists(backup)) File.Delete(backup); } catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }

        internal static void Delete()
        {
            try
            {
                // The data directory is shared by all sessions of this user. Do not
                // delete another session's state when this tray exits.
                int pid;
                if (int.TryParse(ReadValue("Pid="), out pid) && pid > 0
                    && pid != Process.GetCurrentProcess().Id)
                {
                    return;
                }
                File.Delete(StatePath());
            }
            catch (Exception)
            {
            }
        }

        // The tray may run on a port overridden by PI_WEB_TRAY_PORT while the
        // CLI was started from a shell without that variable. When a live tray
        // in this session wrote state.ini, use its actual port instead of the
        // CLI's own configuration.
        internal static bool TryGetRunningPort(out int port)
        {
            port = 0;
            try
            {
                string path = StatePath();
                if (!File.Exists(path)) return false;

                int pid;
                if (!int.TryParse(ReadValue("Pid="), out pid) || pid <= 0) return false;
                if (!Sys.ProcessAlive(pid)) return false;

                int currentSession = Process.GetCurrentProcess().SessionId;
                int stateSession;
                if (int.TryParse(ReadValue("Session="), out stateSession)
                    && stateSession != currentSession)
                {
                    return false;
                }
                int ownerSession = NativeProcess.SessionId(pid);
                if (ownerSession >= 0 && ownerSession != currentSession) return false;

                try
                {
                    string image = NativeProcess.ImagePath(pid);
                    if (!string.IsNullOrEmpty(image)
                        && !string.Equals(Path.GetFullPath(image),
                            Path.GetFullPath(Application.ExecutablePath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                catch (Exception)
                {
                }

                int value;
                if (!int.TryParse(ReadValue("Port="), out value)
                    || value <= 0 || value > 65535)
                {
                    return false;
                }
                port = value;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool InstanceIsElevated()
        {
            if (ReadValue("Elevated=") != "1") return false;

            // The state file can be left over from a crash: if the pid is gone it is ignored
            int pid;
            if (!int.TryParse(ReadValue("Pid="), out pid) || pid <= 0) return false;
            if (!Sys.ProcessAlive(pid)) return false;

            // The state file is per user, not per session. Another session's tray must
            // not make this session believe it has an elevated single instance.
            int currentSession = Process.GetCurrentProcess().SessionId;
            int stateSession;
            if (int.TryParse(ReadValue("Session="), out stateSession)
                && stateSession != currentSession)
            {
                return false;
            }
            int ownerSession = NativeProcess.SessionId(pid);
            if (ownerSession >= 0 && ownerSession != currentSession) return false;

            // A same-user process can edit state.ini; an unrelated PID must not be
            // reported as an elevated tray, and the image path is a cheap sanity check.
            try
            {
                string image = NativeProcess.ImagePath(pid);
                if (!string.IsNullOrEmpty(image)
                    && !string.Equals(Path.GetFullPath(image),
                        Path.GetFullPath(Application.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        private static string ReadValue(string prefix)
        {
            try
            {
                string path = StatePath();
                if (!File.Exists(path) || PathSafety.ContainsReparsePoint(path)) return "";
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(prefix.Length).Trim();
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

    }
}
