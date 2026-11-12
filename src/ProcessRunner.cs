// Pi Web Tray — child processes
//
// Handles streaming output callbacks, timeouts, killing the tree on timeout and an output cap

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PiWebTray
{
    internal sealed class ProcessResult
    {
        internal int ExitCode;
        internal int Pid;
        internal string Output = "";
        internal bool TimedOut;
        internal bool Started;
    }

    internal static class ProcessRunner
    {
        internal static ProcessResult Run(string fileName, string arguments, string workingDir, int timeoutMs,
            Action<string> onLine, IDictionary<string, string> environment)
        {
            return Run(fileName, arguments, workingDir, timeoutMs, onLine, environment, null);
        }

        // onStarted receives the pid right after the child is created, so callers can kill it on exit
        internal static ProcessResult Run(string fileName, string arguments, string workingDir, int timeoutMs,
            Action<string> onLine, IDictionary<string, string> environment, Action<int> onStarted,
            int outputLimit = 256 * 1024)
        {
            ProcessResult result = new ProcessResult();
            StringBuilder output = new StringBuilder();
            if (outputLimit <= 0) outputLimit = 256 * 1024;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = arguments;
                if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                // Elevated node/npm children must not inherit user-controlled
                // loader/injection variables such as NODE_OPTIONS or npm_config_*.
                HardenEnvironment(psi, Elevation.IsElevated());

                if (environment != null)
                {
                    foreach (KeyValuePair<string, string> pair in environment)
                        psi.EnvironmentVariables[pair.Key] = pair.Value;
                }

                using (Process process = new Process())
                {
                    process.StartInfo = psi;

                    int eofCount = 0;
                    DataReceivedEventHandler handler = delegate (object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null)
                        {
                            Interlocked.Increment(ref eofCount);
                            return;
                        }
                        lock (output)
                        {
                            if (output.Length < outputLimit) output.AppendLine(e.Data);
                        }
                        if (onLine != null)
                        {
                            try { onLine(e.Data); }
                            catch (Exception) { }
                        }
                    };

                    process.OutputDataReceived += handler;
                    process.ErrorDataReceived += handler;

                    if (!process.Start()) return result;
                    result.Started = true;
                    result.Pid = process.Id;

                    if (onStarted != null)
                    {
                        try { onStarted(process.Id); } catch (Exception) { }
                    }

                    // npm waits on stdin in some cases; close it so we never hang
                    try { process.StandardInput.Close(); } catch (Exception) { }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        result.TimedOut = true;
                        try { Sys.KillTree(process.Id); } catch (Exception) { }
                        try { process.WaitForExit(5000); } catch (Exception) { }
                    }
                    else
                    {
                        // Let the async output callbacks flush what is left, but bounded: a
                        // grandchild that inherited the pipe and never exits keeps EOF away
                        // forever, which would hang the caller even though the child exited.
                        // Losing the tail of the output beats never returning.
                        try { process.WaitForExit(5000); } catch (Exception) { }
                    }

                    // Wait for both output streams to reach EOF, but never longer than
                    // two seconds. A grandchild can keep the pipe open after the child
                    // exits; losing the tail is better than hanging the caller.
                    DateTime flushDeadline = DateTime.Now.AddSeconds(2);
                    while (Volatile.Read(ref eofCount) < 2 && DateTime.Now < flushDeadline)
                        Thread.Sleep(20);

                    try { result.ExitCode = process.ExitCode; }
                    catch (Exception) { result.ExitCode = -1; }
                }
            }
            catch (Exception ex)
            {
                result.Started = false;
                result.ExitCode = -1;
                lock (output) output.AppendLine("[failed to start] " + ex.Message);
            }

            lock (output) result.Output = output.ToString();
            return result;
        }

        // Remove environment variables that can turn node/npm/.NET child processes
        // into code execution when the parent runs elevated. Callers can still add
        // safe values (PATH, npm_config_registry) after this method.
        internal static void HardenEnvironment(ProcessStartInfo psi, bool elevated)
        {
            if (!elevated || psi == null) return;

            List<string> remove = new List<string>();
            foreach (string key in psi.EnvironmentVariables.Keys)
            {
                if (IsUnsafeEnvironmentVariable(key)) remove.Add(key);
            }
            foreach (string key in remove)
            {
                try { psi.EnvironmentVariables.Remove(key); } catch (Exception) { }
            }

            // Do not let a user-controlled ComSpec or extension list be used by npm
            // when it spawns a shell for lifecycle scripts.
            try
            {
                string cmd = Sys.SystemExecutable("cmd.exe");
                if (!string.IsNullOrEmpty(cmd)) psi.EnvironmentVariables["ComSpec"] = cmd;
                psi.EnvironmentVariables["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            }
            catch (Exception)
            {
            }
        }

        private static bool IsUnsafeEnvironmentVariable(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("npm_config_", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)) return true;

            // These make a spawned shell/npm script load a file before the command.
            if (string.Equals(key, "BASH_ENV", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(key, "ENV", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Prepend a directory to PATH so npm's child processes find node. Never put
        // an empty or relative entry in PATH: Windows treats an empty entry as the
        // current directory and a relative entry may resolve differently in the
        // service's WorkingDir.
        internal static string PathPrefix(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return "";
            try
            {
                string full = Path.GetFullPath(directory);
                if (!Path.IsPathRooted(full)) return "";
                string prefix = full.TrimEnd('\\', '/');
                if (prefix.Length == 0) return full;
                if (prefix.Length == 2 && prefix[1] == ':') prefix += "\\";
                return prefix;
            }
            catch (Exception)
            {
                return "";
            }
        }

        internal static IDictionary<string, string> WithPathPrefix(string directory)
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string prefix = PathPrefix(directory);
            if (prefix.Length == 0) return env;
            string current = Environment.GetEnvironmentVariable("PATH") ?? "";
            env["PATH"] = prefix + ";" + current;
            return env;
        }
    }
}
