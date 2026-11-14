// Pi Web Tray — dependency install
//
// Three jobs:
//   1. Build the npm command line (.cmd cannot be CreateProcess'd, so node + npm-cli.js)
//   2. Run it streaming in the background: log to install.log, publish the last line/step to the tray
//   3. Re-probe afterwards; only a verified install counts as success
//
// pi-web and pi need different script policies: pi-web's postinstall and node-pty's build
// scripts are required, while pi is installed with --ignore-scripts as its own docs recommend.
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

        // The install form lives longer than a single attempt in the tray process.
        // Clear the last step/output so a new attempt never starts by rendering the
        // previous run's progress and npm output.
        internal static void ResetProgress()
        {
            lock (Sync) _progress = new InstallProgress();
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

        // Pi Web and Pi are independent packages with independent versions.
        // A single exact version/range cannot be valid for both, so only simple dist
        // tags (latest, next, beta, ...) are reused for Pi. An exact Pi Web pin
        // keeps Pi on latest instead of failing the whole update.
        internal static string PiCliTag(Config config)
        {
            string tag = SafeTag(config == null ? "latest" : config.InstallTag);
            if (string.IsNullOrEmpty(tag) || IsExactVersionTag(tag)) return "latest";

            foreach (char c in tag)
            {
                bool simple = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || c == '-' || c == '_';
                if (!simple) return "latest";
            }
            return tag;
        }

        // Used by the console --install path before stopping a running service: an
        // up-to-date installation must not be stopped or restarted just to print "none".
        internal static bool HasInstallWork(Config config)
        {
            try
            {
                ProbeResult probe = EnvironmentProbe.Detect(config);
                if (probe.NeedsNode) return false;
                return StepsToInstall(probe, config, HasPending(config)).Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
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

        // pi-web and pi must be installed separately: pi-web needs its lifecycle scripts,
        // while pi is documented to be installed with --ignore-scripts. Combining them in
        // one npm command cannot express that difference. Installing Pi is optional: Pi Web runs
        // without it, so InstallPi=0 skips the Pi step entirely.
        internal static List<InstallStep> StepsToInstall(ProbeResult probe, Config config, bool force)
        {
            List<InstallStep> steps = new List<InstallStep>();
            string webTag = SafeTag(config.InstallTag);
            string cliTag = PiCliTag(config);

            if (force || !probe.PiWebFound)
            {
                InstallStep web = new InstallStep();
                web.Packages.Add(Config.InstallerPackage + "@" + webTag);
                web.AllowScripts = true;
                steps.Add(web);
            }

            // When pi-web is being installed, also update pi even if an older
            // version is already present. First-run auto-install must not leave pi stale.
            // Exact Pi Web versions/ranges are not applied to the independent Pi package.
            if (config.InstallPi && (force || !probe.PiWebFound || !probe.PiCliFound))
            {
                InstallStep cli = new InstallStep();
                cli.Packages.Add(Config.PiCliPackage + "@" + cliTag);
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
            else
            {
                // User/global .npmrc files often set ignore-scripts=true. Pi Web's
                // postinstall and node-pty's build are required, so opt in
                // explicitly on every supported npm version. npm 12 additionally
                // needs the allowlist to permit dependency scripts.
                sb.Append(" --ignore-scripts=false");
                if (!string.IsNullOrEmpty(allowScripts) && NpmMajor(npmVersion) >= 12)
                    sb.Append(" --allow-scripts ").Append(Shell.Quote(allowScripts));
            }

            return sb.ToString();
        }

        // Registry credentials must not appear in the child's command line. npm reads this
        // standard environment variable just like --registry, but process viewers cannot
        // scrape it from the command line.
        internal static void ApplyRegistryEnvironment(Config config, IDictionary<string, string> env)
        {
            if (config == null || env == null || string.IsNullOrEmpty(config.NpmRegistry)) return;
            env["npm_config_registry"] = config.NpmRegistry;
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

        // A marker survives a crash or a failed npm run. The next install attempt is then
        // forced to reinstall the enabled packages instead of trusting a half-written directory.
        internal static string PendingMarker(Config config)
        {
            return Path.Combine(config.OutputDir, "install.pending");
        }

        internal static bool HasPending(Config config)
        {
            try { return File.Exists(PendingMarker(config)); }
            catch (Exception) { return false; }
        }

        private static void MarkPending(Config config)
        {
            try
            {
                Directory.CreateDirectory(config.OutputDir);
                string marker = PendingMarker(config);
                if (PathSafety.ContainsReparsePoint(marker)) return;
                File.WriteAllText(marker,
                    DateTime.UtcNow.ToString("o"), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }

        internal static void ClearPending(Config config)
        {
            try
            {
                if (File.Exists(PendingMarker(config))) File.Delete(PendingMarker(config));
            }
            catch (Exception)
            {
            }
        }

        // Synchronous install; the caller runs it on a background thread
        internal static InstallResult Install(Config config, ProbeResult probe, bool force, Action<string> onLine)
        {
            ResetProgress();
            InstallResult result = new InstallResult();
            result.LogPath = config.InstallLogFile;
            DateTime started = DateTime.Now;

            List<InstallStep> steps = StepsToInstall(probe, config, force);

            // A missing/broken Node.js or an unsupported version is a failed install
            // even when both npm packages happen to be present; the service cannot run
            // safely and "none" would be a lie.
            if (probe.NeedsNode)
            {
                result.Success = false;
                result.Message = probe.NodeIssue();
                return result;
            }

            if (steps.Count == 0)
            {
                ClearPending(config);
                result.Success = true;
                result.Message = L.T("install.none");
                return result;
            }

            if (Aborted)
            {
                result.Success = false;
                result.Message = L.T("install.cancelledBody");
                return result;
            }

            // The tray, the CLI and parallel instances all share the same global npm
            // prefix. Reject a second npm run instead of allowing two installs to
            // overwrite the same package directory at the same time.
            OperationLock operationLock = OperationLock.TryAcquire(OperationLock.InstallName);
            if (operationLock == null)
            {
                result.Success = false;
                result.Message = L.T("install.busyBody");
                result.Elapsed = DateTime.Now - started;
                return result;
            }

            try
            {
                string nodeDir = Path.GetDirectoryName(probe.NodePath);
                string npmWorkingDir = EnvironmentProbe.NpmWorkingDirectory(config);
                IDictionary<string, string> env = ProcessRunner.WithPathPrefix(nodeDir);
                try
                {
                    string npmPrefix = probe.NpmPrefix;
                    if (!string.IsNullOrEmpty(npmPrefix) && Directory.Exists(npmPrefix))
                    {
                        string prefix = ProcessRunner.PathPrefix(npmPrefix);
                        if (prefix.Length > 0)
                        {
                            string current = env.ContainsKey("PATH")
                                ? env["PATH"]
                                : (Environment.GetEnvironmentVariable("PATH") ?? "");
                            env["PATH"] = prefix + ";" + current;
                        }
                    }
                }
                catch (Exception)
                {
                }

                ApplyRegistryEnvironment(config, env);
                MarkPending(config);
                ProcessResult last = null;
                bool ok = true;
                bool piCliFailed = false;
                int lastStepNo = 0;
                string allowScripts = AllowScriptsFor(steps, probe, env, npmWorkingDir);

                for (int i = 0; i < steps.Count; i++)
                {
                    InstallStep step = steps[i];
                    int stepNo = i + 1;
                    lastStepNo = stepNo;
                    string arguments = BuildArguments(config, probe.NpmCliPath, step.Packages,
                        probe.NpmVersion, step.IgnoreScripts, step.AllowScripts ? allowScripts : "");

                    AppendLog(config, "==== install start (step "
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
                            npmWorkingDir,
                            45 * 60 * 1000,
                            delegate (string line)
                            {
                                // Never expose registry credentials through the progress UI or
                                // the CLI, not just in install.log.
                                string safeLine = Shell.RedactSecrets(line);
                                AppendLog(config, safeLine);
                                Update(stepNo, steps.Count, L.T("install.stepTitle"), safeLine, DateTime.Now - started, true);
                                if (onLine != null)
                                {
                                    try { onLine(safeLine); } catch (Exception) { }
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
                        piCliFailed = IsPiCliStep(step, config);
                        break;
                    }
                }

                // Exit code 0 does not prove anything (npm 12 can exit 0 while blocking install scripts), so re-probe
                ProbeResult after = EnvironmentProbe.Detect(config);
                string tag = SafeTag(config.InstallTag);
                string cliTag = PiCliTag(config);
                bool piWebOk = !steps.Exists(delegate (InstallStep step) { return step.Packages.Contains(Config.InstallerPackage + "@" + tag); })
                    || (after.PiWebFound && VersionMatchesTag(after.PiWebVersion, tag));
                bool piCliRequested = steps.Exists(delegate (InstallStep step) { return step.Packages.Contains(Config.PiCliPackage + "@" + cliTag); });
                bool piCliOk = !piCliRequested
                    || (after.PiCliFound && VersionMatchesTag(after.PiCliVersion, cliTag));

                if (ok && piWebOk && piCliOk)
                {
                    ClearPending(config);
                    result.Success = true;
                    string piWebVersion = after.PiWebVersion.Length > 0 ? after.PiWebVersion : L.T("install.ready");
                    string piCliPart = piCliRequested && after.PiCliFound
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
                else if (!ok && piCliFailed)
                {
                    // A running pi keeps its package files locked, which is the most common
                    // failure after the pi-web step succeeded. Say so instead of "npm failed".
                    result.Success = false;
                    result.Message = L.T("install.piLockedBody", config.InstallLogFile);
                    Update(lastStepNo, steps.Count, L.T("install.failedTitle"), "", DateTime.Now - started, false);
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
            finally
            {
                operationLock.Dispose();
            }
        }

        // An exact npm version can be verified after the install. Dist-tags and
        // ranges intentionally fall back to the presence check.
        internal static bool IsExactVersionTag(string tag)
        {
            string text = (tag ?? "").Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(1);
            if (text.Length == 0) return false;

            // npm treats one- or two-component versions ("1" / "1.2") as ranges,
            // so only a full major.minor.patch triple can be verified exactly.
            int parts = 0;
            bool digit = false;
            foreach (char c in text)
            {
                if (c == '.')
                {
                    if (!digit) return false;
                    parts++;
                    digit = false;
                    continue;
                }
                if (c < '0' || c > '9') return false;
                digit = true;
            }
            return digit && parts == 2;
        }

        internal static bool VersionMatchesTag(string installed, string tag)
        {
            if (!IsExactVersionTag(tag)) return true;
            // An exact tag with an unreadable/missing installed version is not proof of
            // success. Only a non-exact dist tag/range can fall back to presence alone.
            if (string.IsNullOrEmpty(installed)) return false;
            // An exact stable tag must not accept a pre-release package just because
            // VersionUtil compares the numeric prefix.
            if (installed.IndexOf('-') >= 0) return false;
            return VersionUtil.Compare(installed, tag) == 0;
        }

        // True when this step only installs pi. A failure there usually means a
        // running pi process still holds the package files.
        internal static bool IsPiCliStep(InstallStep step, Config config)
        {
            if (step == null || step.AllowScripts) return false;
            string tag = PiCliTag(config);
            return step.Packages.Count == 1
                && string.Equals(step.Packages[0], Config.PiCliPackage + "@" + tag, StringComparison.Ordinal);
        }

        // stdout and stderr are read by two different threads; without the lock two appends
        // can collide and silently lose log lines (File.AppendAllText uses a share-denying open).
        internal static void AppendLog(Config config, string line)
        {
            LogWriter.Append(config.InstallLogFile, line);
        }

        // For --install: run synchronously in this console and print the progress
        internal static int RunInConsole(Config config, bool dryRun)
        {
            return RunInConsole(config, dryRun, null);
        }

        // beforeInstall runs after the probe has proved that npm work is needed,
        // but before npm starts. This lets the CLI stop a running service without
        // relying on an earlier, possibly stale, probe.
        internal static int RunInConsole(Config config, bool dryRun, Func<bool> beforeInstall)
        {
            ResetAbort();
            ProbeResult probe = EnvironmentProbe.Detect(config);
            ConsoleHost.WriteLine(L.T("install.envHeader"));
            ConsoleHost.WriteLine(probe.Summary());

            bool force = HasPending(config);
            List<InstallStep> steps = StepsToInstall(probe, config, force);

            // Check the runtime before reporting "nothing to do": an old machine can
            // still have both npm packages installed while node.exe is gone. Returning
            // 0 in that case would tell a script that the environment is ready.
            if (probe.NeedsNode)
            {
                ConsoleHost.WriteError(L.T("install.cannot", probe.NodeIssue()));
                ConsoleHost.WriteLine(L.T("install.installNode", ProbeResult.NodeMinimum, NodeDownloads.Url()));
                return 2;
            }

            if (steps.Count == 0)
            {
                ClearPending(config);
                ConsoleHost.WriteLine(L.T("install.none"));
                return 0;
            }

            // The service must be stopped only after this probe has established
            // that npm will actually run. A failed or stale HasInstallWork probe
            // must never make the caller skip this step.
            if (!dryRun && beforeInstall != null && !beforeInstall())
                return 1;

            ConsoleHost.WriteLine(L.T("install.willRun"));
            IDictionary<string, string> env = ProcessRunner.WithPathPrefix(Path.GetDirectoryName(probe.NodePath));
            ApplyRegistryEnvironment(config, env);
            if (!string.IsNullOrEmpty(config.NpmRegistry))
                ConsoleHost.WriteLine("  npm_config_registry=" + Shell.RedactSecrets(config.NpmRegistry));
            string allowScripts = AllowScriptsFor(steps, probe, env,
                EnvironmentProbe.NpmWorkingDirectory(config));
            foreach (InstallStep step in steps)
            {
                string arguments = BuildArguments(config, probe.NpmCliPath, step.Packages,
                    probe.NpmVersion, step.IgnoreScripts, step.AllowScripts ? allowScripts : "");
                ConsoleHost.WriteLine("  " + Shell.Quote(probe.NodePath) + " "
                    + Shell.RedactSecrets(arguments));
            }

            if (dryRun)
            {
                ConsoleHost.WriteLine(L.T("install.dryRun"));
                return 0;
            }

            InstallResult result = Install(config, probe, HasPending(config),
                delegate (string line) { ConsoleHost.WriteLine("  " + line); });
            ConsoleHost.WriteLine("");
            ConsoleHost.WriteLine((result.Success ? L.T("install.donePrefix") : L.T("install.failedPrefix")) + result.Message);
            return result.Success ? 0 : 1;
        }
    }
}
