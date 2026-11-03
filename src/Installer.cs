// Pi Web Tray — dependency install
//
// Three jobs:
//   1. Build the npm command line (.cmd cannot be CreateProcess'd, so node + npm-cli.js)
//   2. Run it streaming in the background: log to install.log, publish the last line/step to the tray
//   3. Re-probe afterwards; only a verified install counts as success
//
// pi-web and the pi CLI need different script policies: pi-web's postinstall and node-pty's build
// scripts are required, while the pi CLI is installed with --ignore-scripts as its own docs recommend.
// npm 12 blocks dependency scripts by default, so the pi-web step opts in with a one-off
// --allow-scripts allowlist. We never write the user's global npm config.

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

    // One npm invocation: the two packages need different install-script policies.
    internal sealed class InstallStep
    {
        internal List<string> Packages = new List<string>();
        internal bool IgnoreScripts;
        internal bool AllowScripts;
    }

    internal static class Installer
    {
        // pi-web's postinstall prepares the terminal; node-pty's install/postinstall scripts fetch
        // or build its native addon. Both must be allowed on npm 12. Names are matched by npm
        // against the resolved dependency identity.
        internal const string PiWebScriptPackages = "@agegr/pi-web,node-pty";

        private static readonly object Sync = new object();
        private static InstallProgress _progress = new InstallProgress();

        // pid of the running npm process (0 = idle), so "exit" can abort a half-finished install
        private static int _runningPid;
        private static bool _aborted;
        private static readonly object PidLock = new object();

        internal static int RunningPid
        {
            get { lock (PidLock) return _runningPid; }
        }

        private static void SetRunningPid(int pid)
        {
            lock (PidLock) _runningPid = pid;
        }

        internal static void ResetAbort()
        {
            lock (PidLock) _aborted = false;
        }

        internal static bool Aborted
        {
            get { lock (PidLock) return _aborted; }
        }

        internal static void Abort()
        {
            int pid;
            lock (PidLock)
            {
                _aborted = true;
                pid = _runningPid;
            }
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
            foreach (InstallStep step in StepsToInstall(probe, config, force))
                packages.AddRange(step.Packages);
            return packages;
        }

        // pi-web and the pi CLI must be installed separately: pi-web needs its lifecycle scripts,
        // while the pi CLI is documented to be installed with --ignore-scripts. Combining them in
        // one npm command cannot express that difference.
        internal static List<InstallStep> StepsToInstall(ProbeResult probe, Config config, bool force)
        {
            List<InstallStep> steps = new List<InstallStep>();
            string tag = SafeTag(config.InstallTag);

            if (force || !probe.PiWebFound)
            {
                InstallStep web = new InstallStep();
                web.Packages.Add(Config.InstallerPackage + "@" + tag);
                web.AllowScripts = true;
                steps.Add(web);
            }

            // When pi-web is being installed, also update the pi CLI even if an older
            // version is already present. First-run auto-install must not leave pi stale.
            if (config.InstallPiCli && (force || !probe.PiWebFound || !probe.PiCliFound))
            {
                InstallStep cli = new InstallStep();
                cli.Packages.Add(Config.PiCliPackage + "@" + tag);
                cli.IgnoreScripts = true;
                steps.Add(cli);
            }

            return steps;
        }

        // npm 12 introduced the allow-scripts policy; older npm rejects --allow-scripts.
        internal static int NpmMajor(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            int major = 0;
            int digits = 0;
            foreach (char c in version.Trim())
            {
                if (c < '0' || c > '9') break;
                major = major * 10 + (c - '0');
                digits++;
            }
            return digits > 0 ? major : 0;
        }

        // Build the full node + npm-cli.js command line (no cmd.exe, no quoting hell)
        internal static string BuildArguments(Config config, string npmCliPath, List<string> packages,
            string npmVersion, bool ignoreScripts, string allowScripts)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Shell.Quote(npmCliPath));
            sb.Append(" install -g ").Append(string.Join(" ", packages.ToArray()));
            sb.Append(" --no-fund --no-audit --loglevel=http");

            if (ignoreScripts) sb.Append(" --ignore-scripts");
            else if (!string.IsNullOrEmpty(allowScripts) && NpmMajor(npmVersion) >= 12)
                sb.Append(" --allow-scripts ").Append(Shell.Quote(allowScripts));

            if (!string.IsNullOrEmpty(config.NpmRegistry))
                sb.Append(" --registry ").Append(Shell.Quote(config.NpmRegistry));

            return sb.ToString();
        }

        // npm accepts only package names and optional version selectors here. Reject anything
        // else so a malformed .npmrc value cannot inject extra command-line arguments.
        private static bool IsSafeAllowScriptEntry(string name)
        {
            if (name.Length == 0) return false;
            foreach (char c in name)
            {
                bool ascii = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (ascii) continue;
                if ("@/._-+^~*=<>|:".IndexOf(c) >= 0) continue;
                return false;
            }
            return true;
        }

        private static List<string> ParseAllowScripts(string value)
        {
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (value ?? "").Split(','))
            {
                string name = part.Trim();
                if (name == "undefined" || name == "null") continue;
                if (!IsSafeAllowScriptEntry(name)) continue;
                if (seen.Add(name)) names.Add(name);
            }
            return names;
        }

        // A command-line --allow-scripts value replaces the user's .npmrc allowlist, so keep the
        // entries the user already approved and add the ones pi-web needs.
        internal static string MergeAllowScripts(string configured, string required)
        {
            List<string> names = ParseAllowScripts(configured);
            HashSet<string> seen = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            foreach (string name in ParseAllowScripts(required))
            {
                if (seen.Add(name)) names.Add(name);
            }
            return string.Join(",", names.ToArray());
        }

        // Read the effective npm allowlist so a one-off --allow-scripts does not drop packages the
        // user already approved. Best effort: an unreadable config falls back to our required list.
        private static string ReadConfiguredAllowScripts(ProbeResult probe, IDictionary<string, string> env,
            string workingDir)
        {
            try
            {
                ProcessResult run = ProcessRunner.Run(
                    probe.NodePath,
                    Shell.Quote(probe.NpmCliPath) + " config get allow-scripts",
                    workingDir, 20000, null, env);
                if (!run.Started || run.ExitCode != 0) return "";

                string[] lines = run.Output.Replace("\r", "").Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("npm warn", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("npm error", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("npm notice", StringComparison.OrdinalIgnoreCase)) continue;
                    return line;
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        // Best effort list for npm 12: keep the user's approved packages and add pi-web's required
        // ones. Returns an empty string on older npm, where --allow-scripts does not exist.
        private static string AllowScriptsFor(List<InstallStep> steps, ProbeResult probe,
            IDictionary<string, string> env, string workingDir)
        {
            if (NpmMajor(probe.NpmVersion) < 12) return "";
            if (!steps.Exists(delegate (InstallStep step) { return step.AllowScripts; })) return "";
            return MergeAllowScripts(ReadConfiguredAllowScripts(probe, env, workingDir), PiWebScriptPackages);
        }

        // Synchronous install; the caller runs it on a background thread
        internal static InstallResult Install(Config config, ProbeResult probe, bool force, Action<string> onLine)
        {
            InstallResult result = new InstallResult();
            result.LogPath = config.InstallLogFile;
            DateTime started = DateTime.Now;

            List<InstallStep> steps = StepsToInstall(probe, config, force);
            if (steps.Count == 0)
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

            if (Aborted)
            {
                result.Success = false;
                result.Message = L.T("install.cancelledBody");
                return result;
            }

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

            ProcessResult last = null;
            bool ok = true;
            int lastStepNo = 0;
            string allowScripts = AllowScriptsFor(steps, probe, env, config.WorkingDir);

            for (int i = 0; i < steps.Count; i++)
            {
                InstallStep step = steps[i];
                int stepNo = i + 1;
                lastStepNo = stepNo;
                string arguments = BuildArguments(config, probe.NpmCliPath, step.Packages,
                    probe.NpmVersion, step.IgnoreScripts, step.AllowScripts ? allowScripts : "");

                AppendLog(config, "==== " + started.ToString("yyyy-MM-dd HH:mm:ss") + " install start (step "
                    + stepNo + "/" + steps.Count + ") ====");
                AppendLog(config, "node    : " + probe.NodePath + " " + probe.NodeVersion);
                AppendLog(config, "npm     : " + probe.NpmCliPath + " " + probe.NpmVersion);
                AppendLog(config, "packages: " + string.Join(", ", step.Packages.ToArray()));
                AppendLog(config, "command : " + Shell.Quote(probe.NodePath) + " " + arguments);
                if (step.AllowScripts && allowScripts.Length > 0) AppendLog(config, "scripts : " + allowScripts);

                Update(stepNo, steps.Count, L.T("install.stepTitle"), "", DateTime.Now - started, true);

                if (Aborted)
                {
                    result.Success = false;
                    result.Message = L.T("install.cancelledBody");
                    result.Elapsed = DateTime.Now - started;
                    return result;
                }

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
                            Update(stepNo, steps.Count, L.T("install.stepTitle"), line, DateTime.Now - started, true);
                            if (onLine != null)
                            {
                                try { onLine(line); } catch (Exception) { }
                            }
                        },
                        env,
                        delegate (int pid)
                        {
                            SetRunningPid(pid);
                            if (Aborted) Sys.KillTree(pid);
                        });
                }
                finally
                {
                    SetRunningPid(0);
                }

                last = run;

                if (run.TimedOut)
                {
                    AppendLog(config, "==== timed out, aborted ====");
                    result.Success = false;
                    result.Message = L.T("install.timeout");
                    Update(stepNo, steps.Count, L.T("install.timeoutTitle"), "", DateTime.Now - started, false);
                    result.Elapsed = DateTime.Now - started;
                    return result;
                }

                AppendLog(config, "==== npm exit code " + run.ExitCode + " ====");

                if (!run.Started || run.ExitCode != 0)
                {
                    ok = false;
                    break;
                }
            }

            // Exit code 0 does not prove anything (npm 12 can exit 0 while blocking install scripts), so re-probe
            ProbeResult after = EnvironmentProbe.Detect(config);
            string tag = SafeTag(config.InstallTag);
            bool piWebOk = !steps.Exists(delegate (InstallStep step) { return step.Packages.Contains(Config.InstallerPackage + "@" + tag); }) || after.PiWebFound;
            bool piCliOk = !steps.Exists(delegate (InstallStep step) { return step.Packages.Contains(Config.PiCliPackage + "@" + tag); }) || after.PiCliFound;

            if (ok && piWebOk && piCliOk)
            {
                result.Success = true;
                string piWebVersion = after.PiWebVersion.Length > 0 ? after.PiWebVersion : L.T("install.ready");
                string piCliPart = after.PiCliFound
                    ? L.T("install.donePi", after.PiCliVersion.Length > 0 ? after.PiCliVersion : L.T("install.ready"))
                    : "";
                result.Message = L.T("install.doneBody", piWebVersion, piCliPart);
                Update(lastStepNo, steps.Count, L.T("install.okTitle"), "", DateTime.Now - started, false);
            }
            else if (ok && !piWebOk)
            {
                result.Success = false;
                result.Message = L.T("install.unverifiedBody", config.InstallLogFile);
                Update(lastStepNo, steps.Count, L.T("install.unverifiedTitle"), "", DateTime.Now - started, false);
            }
            else if (ok && !piCliOk)
            {
                result.Success = false;
                result.Message = L.T("install.unverifiedPiBody", config.InstallLogFile);
                Update(lastStepNo, steps.Count, L.T("install.unverifiedTitle"), "", DateTime.Now - started, false);
            }
            else
            {
                result.Success = false;
                int exitCode = last != null && last.Started ? last.ExitCode : -1;
                result.Message = L.T("install.failedBody", exitCode, config.InstallLogFile);
                Update(lastStepNo, steps.Count, L.T("install.failedTitle"), "", DateTime.Now - started, false);
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
            ResetAbort();
            ProbeResult probe = EnvironmentProbe.Detect(config);
            ConsoleHost.WriteLine(L.T("install.envHeader"));
            ConsoleHost.WriteLine(probe.Summary());

            List<InstallStep> steps = StepsToInstall(probe, config, false);
            if (steps.Count == 0)
            {
                ConsoleHost.WriteLine(L.T("install.none"));
                return 0;
            }

            if (probe.NeedsNode)
            {
                ConsoleHost.WriteError(L.T("install.cannot", probe.NodeIssue()));
                ConsoleHost.WriteLine(L.T("install.installNode", ProbeResult.NodeMinimum, NodeDownloads.Url()));
                return 2;
            }

            ConsoleHost.WriteLine(L.T("install.willRun"));
            IDictionary<string, string> env = ProcessRunner.WithPathPrefix(Path.GetDirectoryName(probe.NodePath));
            string allowScripts = AllowScriptsFor(steps, probe, env, config.WorkingDir);
            foreach (InstallStep step in steps)
            {
                string arguments = BuildArguments(config, probe.NpmCliPath, step.Packages,
                    probe.NpmVersion, step.IgnoreScripts, step.AllowScripts ? allowScripts : "");
                ConsoleHost.WriteLine("  " + Shell.Quote(probe.NodePath) + " " + arguments);
            }

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
