// Pi Web Tray — environment probe
//
// Answers four questions:
//   1. Is Node.js installed, and is it new enough (pi-web requires 22.19+)?
//   2. Where is npm? (.cmd files cannot be CreateProcess'd, so we run node + npm-cli.js)
//   3. Is pi-web installed, and where?
//   4. Is the pi command line installed? (optional when InstallPi=0)
//
// Results are not cached: re-probing after an install or upgrade shows the new state.
//
// Test hooks for offline automation:
//   PI_WEB_TRAY_NO_NODE=1    pretend Node.js is missing
//   PI_WEB_TRAY_NO_PIWEB=1   pretend pi-web is missing
//   PI_WEB_TRAY_NO_PICLI=1   pretend pi is missing

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PiWebTray
{
    internal sealed class ProbeResult
    {
        internal const string NodeMinimum = "22.19.0";

        internal bool NodeFound;
        internal bool NodeRunnable;
        internal string NodePath = "";
        internal string NodeVersion = "";

        internal bool NpmFound;
        internal string NpmCliPath = "";
        internal string NpmVersion = "";
        internal string NpmPrefix = "";

        internal bool PiWebFound;
        internal string PiWebScript = "";
        internal string PiWebVersion = "";

        internal bool PiCliFound;
        internal string PiCliPath = "";
        internal string PiCliVersion = "";
        // False when InstallPi=0. Pi Web runs without the separate Pi, so a
        // missing Pi must not keep the environment from being ready in that mode.
        internal bool PiCliRequired = true;

        internal string ForcedReason = "";

        internal bool NodeVersionOk
        {
            get { return NodeFound && VersionUtil.AtLeast(NodeVersion, NodeMinimum); }
        }

        // Missing Node, too-old Node or missing npm: all three need the user to act
        internal bool NeedsNode
        {
            get { return !NodeFound || !NodeRunnable || !NodeVersionOk || !NpmFound; }
        }

        internal bool NeedsPiWeb
        {
            get { return !PiWebFound; }
        }

        internal bool NeedsPiCli
        {
            get { return PiCliRequired && !PiCliFound; }
        }

        internal bool Ready
        {
            get { return !NeedsNode && !NeedsPiWeb && !NeedsPiCli; }
        }

        internal string NodeIssue()
        {
            if (!string.IsNullOrEmpty(ForcedReason)) return ForcedReason;
            if (!NodeFound) return L.T("probe.issue.noNode");
            if (!NodeRunnable) return L.T("probe.issue.nodeBroken", NodePath);
            if (!NodeVersionOk) return L.T("probe.issue.oldNode", NodeVersion, NodeMinimum);
            if (!NpmFound) return L.T("probe.issue.noNpm");
            return "";
        }

        internal string Summary()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Row(L.T("probe.node"), NodeFound ? NodePath + "  (" + NodeVersion + ")" : L.T("probe.notInstalled")));
            sb.AppendLine(new string(' ', RowWidth + 2) + L.T("probe.requirement", NodeMinimum, NodeVersionOk ? L.T("bool.on") : L.T("bool.off")));
            sb.AppendLine(Row(L.T("probe.npm"), NpmFound ? NpmVersion + "  " + NpmCliPath : L.T("probe.notFound")));
            sb.AppendLine(Row(L.T("probe.npmGlobal"), NpmPrefix.Length > 0 ? NpmPrefix : L.T("probe.unknown")));
            sb.AppendLine(Row(L.T("probe.piweb"), PiWebFound ? PiWebVersion + "  " + PiWebScript : L.T("probe.notInstalled")));
            sb.AppendLine(Row(L.T("probe.picli"), PiCliFound
                ? PiCliVersion + "  " + PiCliPath
                : PiCliRequired ? L.T("probe.notInstalled") : L.T("probe.notInstalledOptional")));
            string verdict = Ready ? L.T("probe.ready")
                : NeedsNode ? NodeIssue()
                : NeedsPiWeb ? L.T("probe.needPiweb")
                : L.T("probe.needPiCli");
            sb.AppendLine(Row(L.T("probe.verdict"), verdict));
            // Only list the test-hook note when the verdict does not already include it
            if (!string.IsNullOrEmpty(ForcedReason) && ForcedReason != verdict)
                sb.AppendLine(Row(L.T("probe.note"), ForcedReason));
            return sb.ToString();
        }

        // Align key/value in both languages (CJK labels are wider; pad to a fixed column)
        private const int RowWidth = 14;

        private static string Row(string label, string value)
        {
            int pad = RowWidth - VisualWidth(label);
            if (pad < 1) pad = 1;
            return label + new string(' ', pad) + ": " + value;
        }

        // Count CJK characters as two columns, everything else as one
        private static int VisualWidth(string text)
        {
            int width = 0;
            foreach (char c in text)
                width += c > 0x2E80 ? 2 : 1;
            return width;
        }
    }

    internal static class EnvironmentProbe
    {
        internal static ProbeResult Detect(Config config)
        {
            ProbeResult result = new ProbeResult();

            DetectNode(config, result);
            DetectNpm(config, result);
            DetectNpmPrefix(config, result);
            DetectPiWeb(result);
            DetectPiCli(result, config);
            result.PiCliRequired = config == null || config.InstallPi;

            if (IsTruthy(Environment.GetEnvironmentVariable("PI_WEB_TRAY_NO_NODE")))
            {
                result.ForcedReason = L.T("probe.forced.noNode");
                result.NodeFound = false;
                result.NodePath = "";
                result.NodeVersion = "";
                result.NpmFound = false;
            }
            if (IsTruthy(Environment.GetEnvironmentVariable("PI_WEB_TRAY_NO_PIWEB")))
            {
                result.PiWebFound = false;
                result.PiWebScript = "";
                result.PiWebVersion = "";
            }
            if (IsTruthy(Environment.GetEnvironmentVariable("PI_WEB_TRAY_NO_PICLI")))
            {
                result.PiCliFound = false;
                result.PiCliPath = "";
                result.PiCliVersion = "";
            }

            return result;
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value == "1"
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- Node ----------------

        internal static List<string> NodeCandidates(Config config)
        {
            List<string> candidates = new List<string>();

            if (!string.IsNullOrEmpty(config.NodePath)) candidates.Add(config.NodePath);

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFiles)) candidates.Add(Path.Combine(programFiles, @"nodejs\node.exe"));
            if (!string.IsNullOrEmpty(programFilesX86)) candidates.Add(Path.Combine(programFilesX86, @"nodejs\node.exe"));
            string localAppData = Shell.LocalAppData();
            if (!string.IsNullOrEmpty(localAppData))
                candidates.Add(Path.Combine(localAppData, @"Programs\nodejs\node.exe"));

            // nvm-windows keeps the active version behind NVM_SYMLINK; NVM_HOME is the store
            string nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
            if (!string.IsNullOrEmpty(nvmSymlink)) candidates.Add(Path.Combine(nvmSymlink, "node.exe"));
            string nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
            if (!string.IsNullOrEmpty(nvmHome)) candidates.Add(Path.Combine(nvmHome, "node.exe"));

            return candidates;
        }

        // A cheap fingerprint used by the background Node.js watcher. It reads local files
        // and the registry PATH, but never starts node/npm. Keep it empty when no candidate
        // exists so the caller can distinguish "nothing installed" from "installed".
        internal static string NodePresenceSignature(Config config)
        {
            StringBuilder signature = new StringBuilder();
            List<string> candidates = NodeCandidates(config);
            string fromPath = FindOnPath("node.exe");
            if (fromPath != null) candidates.Add(fromPath);

            foreach (string candidate in candidates)
            {
                AppendNodeSignature(signature, candidate);
            }
            return signature.ToString();
        }

        private static void AppendNodeSignature(StringBuilder signature, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                FileInfo file = new FileInfo(path);
                if (!file.Exists) return;

                signature.Append(path).Append('|')
                    .Append(file.Length).Append('|')
                    .Append(file.LastWriteTimeUtc.Ticks).Append('\n');

                // Node installers write node.exe before npm and its files. Include the
                // directory timestamp so the watcher retries after that second write.
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    DirectoryInfo info = new DirectoryInfo(directory);
                    signature.Append("d|").Append(path).Append('|')
                        .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
                }
            }
            catch (Exception)
            {
            }
        }

        // A missing configured path must not suppress PATH/NVM detection. Keeping the
        // rule in one function also makes the recovery behavior directly testable.
        internal static bool HasUsableConfiguredNodePath(Config config)
        {
            return config != null
                && !string.IsNullOrEmpty(config.NodePath)
                && File.Exists(config.NodePath);
        }

        private static void DetectNode(Config config, ProbeResult result)
        {
            // A configured NodePath that exists is authoritative. A path that no
            // longer exists is treated as invalid so automatic detection can recover,
            // including PATH/NVM installations that are not in the fixed candidate list.
            // For automatic detection, do not stop at the first node.exe that merely
            // exists: an old Program Files copy must not shadow a newer PATH/NVM
            // installation.
            bool explicitPath = HasUsableConfiguredNodePath(config);
            List<string> candidates = NodeCandidates(config);
            if (!explicitPath)
            {
                string fromPath = FindOnPath("node.exe");
                if (!string.IsNullOrEmpty(fromPath)) candidates.Add(fromPath);
            }

            string fallbackPath = null;
            bool fallbackRunnable = false;
            string fallbackVersion = "";
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;

                ProcessResult version = ProcessRunner.Run(candidate, "--version", null, 15000, null, null);
                string output = version.Output.Replace("\r", "").Replace("\n", " ").Trim();
                bool runnable = version.Started && version.ExitCode == 0 && output.Length > 0;

                if (fallbackPath == null)
                {
                    fallbackPath = candidate;
                    fallbackRunnable = runnable;
                    fallbackVersion = runnable ? output : "";
                }

                // Honor the explicit path even when it is old or broken: the user
                // chose it, and silently running another Node.js would be
                // surprising. Automatic detection may continue to a valid candidate.
                if (explicitPath)
                {
                    result.NodePath = candidate;
                    result.NodeFound = true;
                    result.NodeRunnable = runnable;
                    result.NodeVersion = runnable ? output : "";
                    return;
                }

                if (runnable && VersionUtil.AtLeast(output, ProbeResult.NodeMinimum))
                {
                    result.NodePath = candidate;
                    result.NodeFound = true;
                    result.NodeRunnable = true;
                    result.NodeVersion = output;
                    return;
                }
            }

            if (fallbackPath == null) return;
            result.NodePath = fallbackPath;
            result.NodeFound = true;
            result.NodeRunnable = fallbackRunnable;
            result.NodeVersion = fallbackVersion;
        }

        // ---------------- npm ----------------

        // npm reads its configuration from the user profile .npmrc and, when
        // the working directory is a project with a package.json, from that
        // project's .npmrc too. Run dependency commands from the user's home
        // directory: this is npm's normal user-config location, but a separate
        // project WorkingDir cannot inject project-level registry, prefix,
        // cache or script policy into a global install.
        internal static string NpmWorkingDirectory(Config config)
        {
            try
            {
                string home = Shell.Home();
                if (!string.IsNullOrEmpty(home) && Directory.Exists(home)) return home;
            }
            catch (Exception)
            {
            }

            try { return Path.GetTempPath(); } catch (Exception) { return null; }
        }

        private static void DetectNpm(Config config, ProbeResult result)
        {
            if (!result.NodeFound || !result.NodeRunnable) return;

            string nodeDir = Path.GetDirectoryName(result.NodePath);
            List<string> candidates = new List<string>();

            if (!string.IsNullOrEmpty(nodeDir))
            {
                candidates.Add(Path.Combine(nodeDir, @"node_modules\npm\bin\npm-cli.js"));
            }
            string roaming = Shell.AppDataRoaming();
            if (!string.IsNullOrEmpty(roaming))
                candidates.Add(Path.Combine(roaming, @"npm\node_modules\npm\bin\npm-cli.js"));
            if (!string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)))
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\node_modules\npm\bin\npm-cli.js"));

            // npm.cmd on PATH: work out which installation it belongs to
            string npmCmd = FindOnPath("npm.cmd");
            if (npmCmd != null)
            {
                string cmdDir = Path.GetDirectoryName(npmCmd);
                if (!string.IsNullOrEmpty(cmdDir))
                {
                    candidates.Add(Path.Combine(cmdDir, @"node_modules\npm\bin\npm-cli.js"));
                    string parent = Path.GetDirectoryName(cmdDir);
                    if (!string.IsNullOrEmpty(parent))
                        candidates.Add(Path.Combine(parent, @"lib\node_modules\npm\bin\npm-cli.js"));
                }
            }

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    result.NpmCliPath = candidate;
                    break;
                }
            }

            if (result.NpmCliPath.Length == 0) return;

            IDictionary<string, string> env = nodeDir == null ? null : ProcessRunner.WithPathPrefix(nodeDir);
            ProcessResult version = ProcessRunner.Run(
                result.NodePath,
                Shell.Quote(result.NpmCliPath) + " --version",
                NpmWorkingDirectory(config), 20000, null, env);

            // A file on disk is not enough: a broken npm must be treated like a missing npm,
            // otherwise the tray claims the environment is ready and every install fails.
            // npm writes warnings to stderr; ProcessRunner merges both streams, so use the
            // last meaningful line instead of the whole merged text.
            string versionLine = LastMeaningfulLine(version.Output);
            result.NpmFound = version.Started && version.ExitCode == 0
                && versionLine.Length > 0;
            if (result.NpmFound) result.NpmVersion = versionLine;
        }

        private static void DetectNpmPrefix(Config config, ProbeResult result)
        {
            if (!result.NpmFound) return;

            string nodeDir = Path.GetDirectoryName(result.NodePath);
            IDictionary<string, string> env = nodeDir == null ? null : ProcessRunner.WithPathPrefix(nodeDir);
            ProcessResult prefix = ProcessRunner.Run(
                result.NodePath,
                Shell.Quote(result.NpmCliPath) + " prefix -g",
                NpmWorkingDirectory(config), 20000, null, env);

            if (prefix.Started && prefix.ExitCode == 0)
            {
                string text = LastExistingDirectory(prefix.Output);
                if (text.Length > 0) result.NpmPrefix = text;
            }

            if (result.NpmPrefix.Length == 0)
            {
                string fallback = Path.Combine(Shell.AppDataRoaming(), "npm");
                if (Directory.Exists(fallback)) result.NpmPrefix = fallback;
            }
        }

        // ---------------- pi-web ----------------

        private static void DetectPiWeb(ProbeResult result)
        {
            List<string> candidates = new List<string>();

            foreach (string prefix in GlobalRoots(result))
                candidates.Add(Path.Combine(prefix, @"node_modules\@agegr\pi-web\bin\pi-web.js"));

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    result.PiWebScript = candidate;
                    break;
                }
            }

            if (result.PiWebScript.Length == 0) return;

            result.PiWebFound = true;
            result.PiWebVersion = ReadVersionFromScript(result.PiWebScript);
        }

        // ---------------- pi command line ----------------

        private static void DetectPiCli(ProbeResult result, Config config)
        {
            List<string> candidates = new List<string>();
            foreach (string prefix in GlobalRoots(result))
            {
                // Prefer the actual bundle over the generated shim. A stale
                // pi.cmd left behind after a partial uninstall must not make the
                // environment look ready.
                candidates.Add(Path.Combine(prefix, @"node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js"));
                candidates.Add(Path.Combine(prefix, "pi.cmd"));
            }

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;
                if (string.Equals(Path.GetFileName(candidate), "pi.cmd",
                        StringComparison.OrdinalIgnoreCase)
                    && !HasPiCliPackage(Path.GetDirectoryName(candidate)))
                {
                    continue;
                }
                result.PiCliPath = candidate;
                break;
            }

            if (result.PiCliPath.Length == 0) return;

            result.PiCliFound = true;
            foreach (string root in GlobalRoots(result))
            {
                string candidate = Path.Combine(root, @"node_modules\@earendil-works\pi-coding-agent\package.json");
                if (File.Exists(candidate))
                {
                    result.PiCliVersion = ReadPackageVersion(candidate);
                    break;
                }
            }
        }

        // A generated pi.cmd without its package is a stale shim, not an
        // installed command line. The bundle is the authoritative candidate; this
        // check only protects the shim fallback.
        private static bool HasPiCliPackage(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;
            try
            {
                return File.Exists(Path.Combine(prefix,
                    @"node_modules\@earendil-works\pi-coding-agent\package.json"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Candidate global dirs: the probed prefix first, then a few common locations
        private static IEnumerable<string> GlobalRoots(ProbeResult result)
        {
            List<string> roots = new List<string>();
            if (!string.IsNullOrEmpty(result.NpmPrefix)) roots.Add(result.NpmPrefix);
            string roaming = Shell.AppDataRoaming();
            if (!string.IsNullOrEmpty(roaming)) roots.Add(Path.Combine(roaming, "npm"));

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
                roots.Add(Path.Combine(programFiles, "nodejs"));
            return roots;
        }

        // ---------------- helpers ----------------

        // npm writes version/prefix output together with warnings on stderr. The last
        // non-empty, non-diagnostic line is the actual value; using the merged text would
        // turn "npm warn ..." into the npm version or the global prefix.
        internal static string LastMeaningfulLine(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";
            string[] lines = output.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("npm warn", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("npm error", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("npm notice", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return line;
            }
            return "";
        }

        private static string LastExistingDirectory(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";
            string[] lines = output.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim().Trim('"');
                if (line.Length == 0) continue;
                if (line.StartsWith("npm warn", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("npm error", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("npm notice", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    if (Directory.Exists(line)) return Path.GetFullPath(line);
                }
                catch (Exception)
                {
                }
            }
            return "";
        }

        // Installing Node.js updates the registry PATH, not this process (or the Explorer
        // that launched it). Read all three sources so a custom install directory is found
        // without signing out and in again.
        private static string FindOnPath(string fileName)
        {
            List<string> directories = PathDirectories();
            foreach (string dir in directories)
            {
                try
                {
                    // A relative PATH entry changes meaning between the tray and
                    // the service working directory; do not trust it for detection
                    // or execution.
                    if (!Path.IsPathRooted(dir)) continue;
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        private static List<string> PathDirectories()
        {
            List<string> directories = new List<string>();
            AddPathDirectories(directories, EnvironmentVariableTarget.Process);
            AddPathDirectories(directories, EnvironmentVariableTarget.User);
            AddPathDirectories(directories, EnvironmentVariableTarget.Machine);
            return directories;
        }

        private static void AddPathDirectories(List<string> directories, EnvironmentVariableTarget target)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH", target);
                if (string.IsNullOrEmpty(path)) return;
                foreach (string dir in SplitPath(path))
                {
                    if (!ContainsDirectory(directories, dir)) directories.Add(dir);
                }
            }
            catch (Exception)
            {
                // A restricted process may not be allowed to read the registry PATH.
            }
        }

        // Split a PATH value: trim spaces and quotes, expand %ENV% and drop duplicates.
        internal static List<string> SplitPath(string value)
        {
            List<string> directories = new List<string>();
            if (string.IsNullOrEmpty(value)) return directories;
            foreach (string raw in value.Split(';'))
            {
                string dir = Environment.ExpandEnvironmentVariables(raw).Trim().Trim('"');
                if (dir.Length == 0) continue;
                if (ContainsDirectory(directories, dir)) continue;
                directories.Add(dir);
            }
            return directories;
        }

        private static bool ContainsDirectory(List<string> directories, string dir)
        {
            return directories.Exists(delegate (string item)
            {
                return string.Equals(item, dir, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Walk up from the script path to find package.json and read its version
        internal static string ReadVersionFromScript(string scriptPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(scriptPath);
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    string candidate = Path.Combine(dir, "package.json");
                    if (File.Exists(candidate))
                    {
                        string version = ReadPackageVersion(candidate);
                        if (version.Length > 0) return version;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        // Extract version from package.json without pulling in a JSON library
        internal static string ReadPackageVersion(string packageJsonPath)
        {
            try
            {
                if (!File.Exists(packageJsonPath)) return "";
                string text = File.ReadAllText(packageJsonPath);
                Match match = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success) return match.Groups[1].Value;
            }
            catch (Exception)
            {
            }
            return "";
        }
    }
}
