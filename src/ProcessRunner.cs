// Pi Web Tray — child processes
//
// Handles streaming output callbacks, timeouts, killing the tree on timeout and an output cap

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            Action<string> onLine, IDictionary<string, string> environment, Action<int> onStarted)
        {
            ProcessResult result = new ProcessResult();
            StringBuilder output = new StringBuilder();
            int outputLimit = 256 * 1024;

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

                if (environment != null)
                {
                    foreach (KeyValuePair<string, string> pair in environment)
                        psi.EnvironmentVariables[pair.Key] = pair.Value;
                }

                using (Process process = new Process())
                {
                    process.StartInfo = psi;

                    DataReceivedEventHandler handler = delegate (object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
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

        // Prepend a directory to PATH so npm's child processes find node
        internal static IDictionary<string, string> WithPathPrefix(string directory)
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string current = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (string.IsNullOrEmpty(directory)) return env;
            env["PATH"] = directory.TrimEnd('\\') + ";" + current;
            return env;
        }
    }
}
