// Pi Web Tray — process control, elevation launch and foreground helpers.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PiWebTray
{
    internal static class Sys
    {
        // ShellExecute/runas cannot use ProcessStartInfo.EnvironmentVariables. Temporarily
        // remove profiler/startup-hook variables from this process so they are not inherited
        // by the elevated child. The lock prevents two elevated launches from racing the
        // save/restore; node/npm variables are intentionally left alone.
        private static readonly object ElevatedLaunchLock = new object();

        private static bool IsProfilerEnvironmentVariable(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool StartElevatedSanitized(ProcessStartInfo psi)
        {
            lock (ElevatedLaunchLock)
            {
                Dictionary<string, string> saved = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    try
                    {
                        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables(
                            EnvironmentVariableTarget.Process))
                        {
                            string name = entry.Key as string;
                            if (!IsProfilerEnvironmentVariable(name)) continue;
                            saved[name] = Convert.ToString(entry.Value) ?? "";
                        }
                    }
                    catch (Exception)
                    {
                        // If the environment cannot be enumerated, still attempt the
                        // elevation rather than turning a hardening step into a failure.
                        saved.Clear();
                    }

                    foreach (string name in saved.Keys)
                    {
                        try
                        {
                            Environment.SetEnvironmentVariable(name, null,
                            EnvironmentVariableTarget.Process);
                        }
                        catch (Exception) { }
                    }

                    using (Process child = Process.Start(psi))
                    {
                        return child != null;
                    }
                }
                finally
                {
                    foreach (KeyValuePair<string, string> pair in saved)
                    {
                        try
                        {
                            Environment.SetEnvironmentVariable(pair.Key, pair.Value,
                            EnvironmentVariableTarget.Process);
                        }
                        catch (Exception) { }
                    }
                }
            }
        }

        // Resolve Windows tools from the system directory instead of relying on the caller's
        // current directory or PATH. This blocks a local taskkill.exe/netstat.exe
        // in a writable working directory from being executed by the tray.
        internal static string SystemDirectory
        {
            get
            {
                try { return Environment.GetFolderPath(Environment.SpecialFolder.System); }
                catch (Exception) { return ""; }
            }
        }

        internal static string SystemExecutable(string name)
        {
            try
            {
                string directory = SystemDirectory;
                if (!string.IsNullOrEmpty(directory)) return Path.Combine(directory, name);
            }
            catch (Exception)
            {
            }
            // Never fall back to a bare executable name: CreateProcess would search
            // the current directory and PATH. An empty path makes the caller fail
            // closed instead of executing an unrelated file with the tray's token.
            return "";
        }

        // Capture the process creation time when a handle or owner probe is
        // available. Windows reuses PIDs aggressively; comparing the creation time
        // before taskkill prevents killing an unrelated process after a reap.
        internal static DateTime ProcessStartTime(int pid)
        {
            if (pid <= 0) return DateTime.MinValue;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                    return process.StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        internal static DateTime ProcessStartTime(Process process)
        {
            if (process == null) return DateTime.MinValue;
            try { return process.StartTime.ToUniversalTime(); }
            catch (Exception) { return DateTime.MinValue; }
        }

        private static void KillProcessHandle(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception)
            {
            }
        }

        internal static void KillTree(int pid, DateTime? expectedStart = null,
            Process fallback = null)
        {
            if (pid <= 4) return;
            try
            {
                // A caller that supplied a start-time slot but could not read it
                // must not fall back to a bare PID kill: the process may have exited
                // and Windows may have reused the PID. A handle the caller already
                // owns is still safe to kill, so use it when available.
                if (expectedStart.HasValue)
                {
                    if (expectedStart.Value == DateTime.MinValue)
                    {
                        KillProcessHandle(fallback);
                        return;
                    }
                    DateTime actual;
                    try
                    {
                        using (Process process = Process.GetProcessById(pid))
                            actual = process.StartTime.ToUniversalTime();
                    }
                    catch (Exception)
                    {
                        KillProcessHandle(fallback);
                        return;
                    }
                    if (actual != expectedStart.Value.ToUniversalTime()) return;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = SystemExecutable("taskkill.exe");
                psi.Arguments = "/PID " + pid + " /T /F";
                psi.WorkingDirectory = SystemDirectory;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                bool launched = false;
                try
                {
                    using (Process taskkill = Process.Start(psi))
                    {
                        if (taskkill != null)
                        {
                            launched = true;
                            // Wait briefly for taskkill's own result. The caller may
                            // then check the port; killing the tracked handle below
                            // also covers a taskkill that failed or timed out.
                            try { taskkill.WaitForExit(5000); } catch (Exception) { }
                        }
                    }
                }
                catch (Exception)
                {
                }

                // taskkill can fail because of permissions, a missing system tool, or
                // a transient process error. The caller supplied a handle for exactly
                // this reason; never report a kill without trying it.
                if (!launched || (fallback != null && !HasExited(fallback)))
                    KillProcessHandle(fallback);
            }
            catch (Exception)
            {
                // A failure before taskkill (for example StartTime access) still has
                // the caller-owned handle as a safe fallback. The handle kill is
                // PID-reuse safe because it refers to the original process object.
                KillProcessHandle(fallback);
            }
        }

        private static bool HasExited(Process process)
        {
            try { return process == null || process.HasExited; }
            catch (Exception) { return false; }
        }

        internal static Process StartHidden(string fileName, string arguments, string workingDir)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            if (workingDir != null) psi.WorkingDirectory = workingDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            ProcessRunner.HardenEnvironment(psi, Elevation.IsElevated());
            return Process.Start(psi);
        }

        // Start elevated (one UAC prompt); false means the user cancelled or it failed
        internal static bool RunElevated(string fileName, string arguments)
        {
            return RunElevated(fileName, arguments, true);
        }

        // The update helper needs a visible error dialog, so it must not be started hidden
        internal static bool RunElevated(string fileName, string arguments, bool hidden)
        {
            return RunElevated(fileName, arguments, hidden, null);
        }

        internal static bool RunElevated(string fileName, string arguments, bool hidden,
            string workingDirectory)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = arguments;
                if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal;
                return StartElevatedSanitized(psi);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static string ExplorerExecutable()
        {
            try
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windows)) return Path.Combine(windows, "explorer.exe");
            }
            catch (Exception)
            {
            }
            // Do not let ShellExecute resolve a bare explorer.exe from the current
            // directory or PATH, especially when the tray is elevated.
            return "";
        }

        // An elevated process must not resolve a user-controlled URL/file association
        // with its own token. Hand the target to the already-running medium-integrity
        // Explorer instead. Non-elevated launches keep the normal ShellExecute path.
        internal static void OpenExternal(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            ProcessStartInfo psi = new ProcessStartInfo();
            if (Elevation.IsElevated())
            {
                psi.FileName = ExplorerExecutable();
                psi.Arguments = Shell.QuoteArg(target);
                psi.WorkingDirectory = SystemDirectory;
                psi.UseShellExecute = true;
            }
            else
            {
                psi.FileName = target;
                psi.UseShellExecute = true;
            }
            using (Process child = Process.Start(psi))
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr window);

        // Balloon clicks are delivered by the shell, so the tray process does not
        // automatically receive foreground rights. Attach to the foreground input
        // queue before showing a dialog opened from a notification.
        internal static void BringToForeground(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            uint foregroundThread = 0;
            uint currentThread = GetCurrentThreadId();
            bool attached = false;
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground != IntPtr.Zero)
                    GetWindowThreadProcessId(foreground, out foregroundThread);

                if (foregroundThread != 0 && foregroundThread != currentThread)
                {
                    attached = AttachThreadInput(foregroundThread, currentThread, true);
                }

                const int Restore = 9;
                ShowWindow(window, Restore);
                BringWindowToTop(window);
                SetForegroundWindow(window);
                SetActiveWindow(window);
            }
            catch (Exception)
            {
            }
            finally
            {
                if (attached)
                {
                    try { AttachThreadInput(foregroundThread, currentThread, false); }
                    catch (Exception) { }
                }
            }
        }

        internal static bool ProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // GetCurrentProcess() opens a real process handle. The finalizer reclaims
        // it eventually, but high-frequency callers (session checks, the
        // per-connection bridge check) must not pile up handles between
        // collections.
        internal static int CurrentProcessId
        {
            get { using (Process process = Process.GetCurrentProcess()) return process.Id; }
        }

        internal static int CurrentSessionId
        {
            get { using (Process process = Process.GetCurrentProcess()) return process.SessionId; }
        }
    }
}
