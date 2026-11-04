// Pi Web Tray — instance identity and runtime state
//
// One instance with fixed names by default. To run several configurations on one machine, set
// PI_WEB_TRAY_INSTANCE=dev and mutex, signals and data dir all get a suffix and stay independent.
//
// PI_WEB_TRAY_HOME overrides the data directory entirely (default %LOCALAPPDATA%\pi-web-tray).

using System;
using System.IO;
using System.Text;

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
                StringBuilder sb = new StringBuilder();
                foreach (char c in raw.Trim())
                {
                    if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                }
                string clean = sb.ToString();
                if (clean.Length == 0) return "";
                if (clean.Length > 32) clean = clean.Substring(0, 32);
                return "." + clean;
            }
        }

        internal static string MutexName { get { return @"Local\" + BaseName + ".C2B1B0" + Suffix; } }
        internal static string OpenSignalName { get { return @"Local\" + BaseName + ".Open" + Suffix; } }
        internal static string StartSignalName { get { return @"Local\" + BaseName + ".Start" + Suffix; } }
        internal static string StopSignalName { get { return @"Local\" + BaseName + ".Stop" + Suffix; } }
        internal static string QuitSignalName { get { return @"Local\" + BaseName + ".Quit" + Suffix; } }

        internal static string DataFolder
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME");
                if (!string.IsNullOrEmpty(home))
                    return Environment.ExpandEnvironmentVariables(home.Trim());
                return Path.Combine(Shell.LocalAppData(), "pi-web-tray" + Suffix);
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

        internal static void Save(int pid, bool elevated, int port)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Pid=" + pid);
                sb.AppendLine("Elevated=" + (elevated ? "1" : "0"));
                sb.AppendLine("Port=" + port);
                sb.AppendLine("Url=http://127.0.0.1:" + port);
                File.WriteAllText(StatePath(), sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
        }

        internal static void Delete()
        {
            try { File.Delete(StatePath()); } catch (Exception) { }
        }

        internal static bool InstanceIsElevated()
        {
            if (ReadValue("Elevated=") != "1") return false;

            // The state file can be left over from a crash: if the pid is gone it is ignored
            int pid;
            if (!int.TryParse(ReadValue("Pid="), out pid) || pid <= 0) return false;
            return Sys.ProcessAlive(pid);
        }

        private static string ReadValue(string prefix)
        {
            try
            {
                string path = StatePath();
                if (!File.Exists(path)) return "";
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
