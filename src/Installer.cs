// Pi Web Tray — dependency install
//
// Three jobs:
//   1. Build the npm command line (.cmd cannot be CreateProcess'd, so node + npm-cli.js)
//   2. Run it streaming in the background: log to install.log, publish the last line/step to the tray
//   3. Re-probe afterwards; only a verified install counts as success
//
// npm 12 blocks dependency install scripts by default (node-pty and friends). We do not touch the
// user's global npm config; we verify for real instead: file present, version readable, or fail loudly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PiWebTray
{
    internal sealed class InstallProgress
    {
        internal int Step;              // step number (1-based)
        internal int StepCount;
        internal string Title = "";     // current step name, e.g. "installing pi-web"
        internal string LastLine = "";  // last npm output line
        internal TimeSpan Elapsed;
        internal bool Running;
    }

    internal sealed class InstallResult
    {
        internal bool Success;
        internal string Message = "";
        internal string LogPath = "";
        internal TimeSpan Elapsed;
    }

    internal static class Installer
    {
        private static readonly object Sync = new object();
        private static InstallProgress _progress = new InstallProgress();

        // pid of the running npm process (0 = idle), so "exit" can abort a half-finished install
        private static int _runningPid;
        private static readonly object PidLock = new object();

        internal static int RunningPid
        {
            get { lock (PidLock) return _runningPid; }
        }

        private static void SetRunningPid(int pid)
        {
            lock (PidLock) _runningPid = pid;
        }

        internal static void Abort()
        {
            int pid;
            lock (PidLock) pid = _runningPid;
            if (pid > 0) Sys.KillTree(pid);
        }

        internal static InstallProgress Snapshot()
        {
            lock (Sync)
            {
                InstallProgress copy = new InstallProgress();
                copy.Step = _progress.Step;
                copy.StepCount = _progress.StepCount;
                copy.Title = _progress.Title;
                copy.LastLine = _progress.LastLine;
                copy.Elapsed = _progress.Elapsed;
                copy.Running = _progress.Running;
                return copy;
            }
        }

        private static void Update(int step, int stepCount, string title, string lastLine, TimeSpan elapsed, bool running)
        {
            lock (Sync)
            {
                _progress.Step = step;
                _progress.StepCount = stepCount;
                _progress.Title = title;
                _progress.LastLine = lastLine;
                _progress.Elapsed = elapsed;
                _progress.Running = running;
            }
        }

        // Guard against typos or stray arguments in the ini: a tag may only contain these characters
        // (npm dist-tags and semver ranges)
        internal static string SafeTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "latest";
            string trimmed = tag.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 64) return "latest";
            foreach (char c in trimmed)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '.' || c == '-' || c == '_' || c == '+' || c == '^' || c == '~';
                if (!ok) return "latest";
            }
            return trimmed;
        }

        // Which packages need installing. force = reinstall/update even when the package is
        // already present (the "Install / update dependencies" menu item).
        internal static List<string> PackagesToInstall(ProbeResult probe, Config config, bool force)
        {
            List<string> packages = new List<string>();
            if (force || !probe.PiWebFound) packages.Add(Config.InstallerPackage + "@" + SafeTag(config.InstallTag));
            if (config.InstallPiCli && (force || !probe.PiCliFound))
                packages.Add(Config.PiCliPackage + "@" + SafeTag(config.InstallTag));
            return packages;
        }

        // Build the full node + npm-cli.js command line (no cmd.exe, no quoting hell)
        internal static string BuildArguments(Config config, string npmCliPath, List<string> packages)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Shell.Quote(npmCliPath));
            sb.Append(" install -g ").Append(string.Join(" ", packages.ToArray()));
            sb.Append(" --no-fund --no-audit --loglevel=http");

            if (!string.IsNullOrEmpty(config.NpmRegistry))
                sb.Append(" --registry ").Append(Shell.Quote(config.NpmRegistry));

            return sb.ToString();
        }

        // Synchronous install; the caller runs it on a background thread
        internal static InstallResult Install(Config config, ProbeResult probe, bool force, Action<string> onLine)
        {
            InstallResult result = new InstallResult();
            result.LogPath = config.InstallLogFile;
            DateTime started = DateTime.Now;

            List<string> packages = PackagesToInstall(probe, config, force);
            if (packages.Count == 0)
            {
                result.Success = true;
                result.Message = L.T("install.none");
                return result;
            }

            if (!probe.NodeFound || !probe.NpmFound)
            {
                result.Success = false;
                result.Message = probe.NodeIssue();
                return result;
            }

            string arguments = BuildArguments(config, probe.NpmCliPath, packages);

            AppendLog(config, "==== " + started.ToString("yyyy-MM-dd HH:mm:ss") + " install start ====");
            AppendLog(config, "node    : " + probe.NodePath + " " + probe.NodeVersion);
            AppendLog(config, "npm     : " + probe.NpmCliPath + " " + probe.NpmVersion);
            AppendLog(config, "packages: " + string.Join(", ", packages.ToArray()));
            AppendLog(config, "command : " + Shell.Quote(probe.NodePath) + " " + arguments);

            string nodeDir = Path.GetDirectoryName(probe.NodePath);
            IDictionary<string, string> env = ProcessRunner.WithPathPrefix(nodeDir);
            try
            {
                string npmPrefix = probe.NpmPrefix;
                if (!string.IsNullOrEmpty(npmPrefix) && Directory.Exists(npmPrefix))
                    env["PATH"] = nodeDir + ";" + npmPrefix + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
            }
            catch (Exception)
            {
            }

            Update(1, 1, L.T("install.stepTitle"), "", TimeSpan.Zero, true);

            ProcessResult run;
            try
            {
                run = ProcessRunner.Run(
                    probe.NodePath,
                    arguments,
                    config.WorkingDir,
                    45 * 60 * 1000,
                    delegate (string line)
                    {
                        AppendLog(config, line);
                        Update(1, 1, L.T("install.stepTitle"), line, DateTime.Now - started, true);
                        if (onLine != null)
                        {
                            try { onLine(line); } catch (Exception) { }
                        }
                    },
                    env,
                    delegate (int pid) { SetRunningPid(pid); });
            }
            finally
            {
                SetRunningPid(0);
            }

            bool ok = run.Started && !run.TimedOut && run.ExitCode == 0;

            if (run.TimedOut)
            {
                AppendLog(config, "==== timed out, aborted ====");
                result.Success = false;
                result.Message = L.T("install.timeout");
                Update(1, 1, L.T("install.timeoutTitle"), "", DateTime.Now - started, false);
                result.Elapsed = DateTime.Now - started;
                return result;
            }

            AppendLog(config, "==== npm exit code " + run.ExitCode + " ====");

            // Exit code 0 does not prove anything (npm 12 can exit 0 while blocking install scripts), so re-probe
            ProbeResult after = EnvironmentProbe.Detect(config);
            bool piWebOk = !packages.Contains(Config.InstallerPackage + "@" + SafeTag(config.InstallTag)) || after.PiWebFound;
            bool piCliOk = !config.InstallPiCli || !packages.Contains(Config.PiCliPackage + "@" + SafeTag(config.InstallTag)) || after.PiCliFound;

            if (ok && piWebOk && piCliOk)
            {
                result.Success = true;
                string piWebVersion = after.PiWebVersion.Length > 0 ? after.PiWebVersion : L.T("install.ready");
                string piCliPart = after.PiCliFound
                    ? L.T("install.donePi", after.PiCliVersion.Length > 0 ? after.PiCliVersion : L.T("install.ready"))
                    : "";
                result.Message = L.T("install.doneBody", piWebVersion, piCliPart);
                Update(1, 1, L.T("install.okTitle"), "", DateTime.Now - started, false);
            }
            else if (ok && !piWebOk)
            {
                result.Success = false;
                result.Message = L.T("install.unverifiedBody", config.InstallLogFile);
                Update(1, 1, L.T("install.unverifiedTitle"), "", DateTime.Now - started, false);
            }
            else if (ok && !piCliOk)
            {
                result.Success = false;
                result.Message = L.T("install.unverifiedPiBody", config.InstallLogFile);
                Update(1, 1, L.T("install.unverifiedTitle"), "", DateTime.Now - started, false);
            }
            else
            {
                result.Success = false;
                result.Message = L.T("install.failedBody", run.ExitCode, config.InstallLogFile);
                Update(1, 1, L.T("install.failedTitle"), "", DateTime.Now - started, false);
            }

            result.Elapsed = DateTime.Now - started;
            return result;
        }

        // stdout and stderr are read by two different threads; without the lock two appends
        // can collide and silently lose log lines (File.AppendAllText uses a share-denying open).
        private static readonly object LogLock = new object();

        internal static void AppendLog(Config config, string line)
        {
            lock (LogLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(config.InstallLogFile));
                    FileInfo info = new FileInfo(config.InstallLogFile);
                    if (info.Exists && info.Length > 4 * 1024 * 1024)
                    {
                        string backup = config.InstallLogFile + ".1";
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(config.InstallLogFile, backup);
                    }
                    File.AppendAllText(config.InstallLogFile, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                }
            }
        }

        // For --install: run synchronously in this console and print the progress
        internal static int RunInConsole(Config config, bool dryRun)
        {
            ProbeResult probe = EnvironmentProbe.Detect(config);
            ConsoleHost.WriteLine(L.T("install.envHeader"));
            ConsoleHost.WriteLine(probe.Summary());

            List<string> packages = PackagesToInstall(probe, config, false);
            if (packages.Count == 0)
            {
                ConsoleHost.WriteLine(L.T("install.none"));
                return 0;
            }

            if (probe.NeedsNode)
            {
                ConsoleHost.WriteError(L.T("install.cannot", probe.NodeIssue()));
                ConsoleHost.WriteLine(L.T("install.installNode", ProbeResult.NodeMinimum));
                return 2;
            }

            string arguments = BuildArguments(config, probe.NpmCliPath, packages);
            ConsoleHost.WriteLine(L.T("install.willRun"));
            ConsoleHost.WriteLine("  " + Shell.Quote(probe.NodePath) + " " + arguments);

            if (dryRun)
            {
                ConsoleHost.WriteLine(L.T("install.dryRun"));
                return 0;
            }

            InstallResult result = Install(config, probe, false, delegate (string line) { ConsoleHost.WriteLine("  " + line); });
            ConsoleHost.WriteLine("");
            ConsoleHost.WriteLine((result.Success ? L.T("install.donePrefix") : L.T("install.failedPrefix")) + result.Message);
            return result.Success ? 0 : 1;
        }
    }
}
