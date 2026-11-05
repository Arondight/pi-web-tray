// Pi Web Tray — environment probe
//
// Answers four questions:
//   1. Is Node.js installed, and is it new enough (pi-web requires 22.19+)?
//   2. Where is npm? (.cmd files cannot be CreateProcess'd, so we run node + npm-cli.js)
//   3. Is pi-web installed, and where?
//   4. Is the pi command line installed?
//
// Results are not cached: re-probing after an install or upgrade shows the new state.
//
// Test hooks for offline automation:
//   PI_WEB_TRAY_NO_NODE=1    pretend Node.js is missing
//   PI_WEB_TRAY_NO_PIWEB=1   pretend pi-web is missing
//   PI_WEB_TRAY_NO_PICLI=1   pretend the pi CLI is missing

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
            get { return !PiCliFound; }
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
            sb.AppendLine(Row(L.T("probe.picli"), PiCliFound ? PiCliVersion + "  " + PiCliPath : L.T("probe.notInstalled")));
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
            DetectNpmPrefix(result);
            DetectPiWeb(result);
            DetectPiCli(result, config);

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

        private static void DetectNode(Config config, ProbeResult result)
        {
            List<string> candidates = new List<string>();

            if (!string.IsNullOrEmpty(config.NodePath)) candidates.Add(config.NodePath);

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFiles)) candidates.Add(Path.Combine(programFiles, @"nodejs\node.exe"));
            if (!string.IsNullOrEmpty(programFilesX86)) candidates.Add(Path.Combine(programFilesX86, @"nodejs\node.exe"));
            candidates.Add(Path.Combine(Shell.LocalAppData(), @"Programs\nodejs\node.exe"));

            // nvm-windows keeps the active version behind NVM_SYMLINK; NVM_HOME is the store
            string nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
            if (!string.IsNullOrEmpty(nvmSymlink)) candidates.Add(Path.Combine(nvmSymlink, "node.exe"));
            string nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
            if (!string.IsNullOrEmpty(nvmHome)) candidates.Add(Path.Combine(nvmHome, "node.exe"));

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    result.NodePath = candidate;
                    break;
                }
            }

            if (result.NodePath.Length == 0)
            {
                string fromPath = FindOnPath("node.exe");
                if (fromPath != null) result.NodePath = fromPath;
            }

            if (result.NodePath.Length == 0) return;

            result.NodeFound = true;
            ProcessResult version = ProcessRunner.Run(result.NodePath, "--version", null, 15000, null, null);
            string output = version.Output.Replace("\r", "").Replace("\n", " ").Trim();
            if (version.Started && version.ExitCode == 0 && output.Length > 0)
            {
                result.NodeRunnable = true;
                result.NodeVersion = output;
            }
        }

        // ---------------- npm ----------------

        private static void DetectNpm(Config config, ProbeResult result)
        {
            if (!result.NodeFound || !result.NodeRunnable) return;

            string nodeDir = Path.GetDirectoryName(result.NodePath);
            List<string> candidates = new List<string>();

            if (!string.IsNullOrEmpty(nodeDir))
            {
                candidates.Add(Path.Combine(nodeDir, @"node_modules\npm\bin\npm-cli.js"));
            }
            candidates.Add(Path.Combine(Shell.AppDataRoaming(), @"npm\node_modules\npm\bin\npm-cli.js"));
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
                null, 20000, null, env);

            // A file on disk is not enough: a broken npm must be treated like a missing npm,
            // otherwise the tray claims the environment is ready and every install fails.
            string output = version.Output.Replace("\r", "").Replace("\n", " ").Trim();
            result.NpmFound = version.Started && version.ExitCode == 0 && output.Length > 0;
            if (result.NpmFound) result.NpmVersion = output;
        }

        private static void DetectNpmPrefix(ProbeResult result)
        {
            if (!result.NpmFound) return;

            string nodeDir = Path.GetDirectoryName(result.NodePath);
            IDictionary<string, string> env = nodeDir == null ? null : ProcessRunner.WithPathPrefix(nodeDir);
            ProcessResult prefix = ProcessRunner.Run(
                result.NodePath,
                Shell.Quote(result.NpmCliPath) + " prefix -g",
                null, 20000, null, env);

            if (prefix.Started && prefix.ExitCode == 0)
            {
                string text = prefix.Output.Replace("\r", "").Trim();
                if (text.Length > 0 && Directory.Exists(text)) result.NpmPrefix = text;
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
                candidates.Add(Path.Combine(prefix, "pi.cmd"));
                candidates.Add(Path.Combine(prefix, @"node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js"));
            }

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    result.PiCliPath = candidate;
                    break;
                }
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

        // Candidate global dirs: the probed prefix first, then a few common locations
        private static IEnumerable<string> GlobalRoots(ProbeResult result)
        {
            List<string> roots = new List<string>();
            if (!string.IsNullOrEmpty(result.NpmPrefix)) roots.Add(result.NpmPrefix);
            roots.Add(Path.Combine(Shell.AppDataRoaming(), "npm"));

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
                roots.Add(Path.Combine(programFiles, @"nodejs\node_modules"));
            return roots;
        }

        // ---------------- helpers ----------------

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
