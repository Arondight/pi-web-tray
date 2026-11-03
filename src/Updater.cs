// Pi Web Tray — update from GitHub releases
//
// The tray cannot replace its own executable while it is running, so the update is split
// into two processes:
//
//   1. the tray checks the latest GitHub release, downloads the Windows archive, verifies
//      its SHA-256 checksum and extracts it to a staging directory;
//   2. a helper process (the staged executable) waits for the tray to exit, replaces the
//      application files and restarts the new tray.
//
// When the installation directory is not writable by the current user (Program Files),
// the helper is started through UAC. The user's pi-web-tray.ini and logs are skipped
// during the copy, so settings and diagnostics survive the update.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PiWebTray
{
    internal enum ApplyStartMode
    {
        Tray,
        TrayOnly,
        ServiceSilent
    }

    internal sealed class ReleaseInfo
    {
        internal string Tag = "";
        internal string Version = "";
        internal string ZipName = "";
        internal string ZipUrl = "";
        internal string ChecksumName = "";
        internal string ChecksumUrl = "";
        internal long ZipSize;
    }

    internal static class Updater
    {
        internal const string RepositoryOwner = "Arondight";
        internal const string RepositoryName = "pi-web-tray";

        private const string LatestReleaseUrl =
            "https://api.github.com/repos/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
        private const long MaxLogBytes = 4 * 1024 * 1024;

        private static readonly object LogLock = new object();

        [DataContract]
        private sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")]
            public string TagName { get; set; }

            [DataMember(Name = "assets")]
            public GitHubAsset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class GitHubAsset
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "browser_download_url")]
            public string BrowserDownloadUrl { get; set; }

            [DataMember(Name = "size")]
            public long Size { get; set; }
        }

        // ---------------- check ----------------

        internal static ReleaseInfo FindLatest(out string error)
        {
            error = "";
            string json = GetText(LatestReleaseUrl, 20000, true, out error);
            if (json == null) return null;
            return ParseRelease(json, out error);
        }

        // Separated from FindLatest so the response parsing can be tested offline.
        internal static ReleaseInfo ParseRelease(string json, out string error)
        {
            error = "";

            GitHubRelease release;
            try
            {
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
                    release = serializer.ReadObject(stream) as GitHubRelease;
                }
            }
            catch (Exception)
            {
                error = L.T("update.error.badResponse");
                return null;
            }

            if (release == null || string.IsNullOrEmpty(release.TagName))
            {
                error = L.T("update.error.badResponse");
                return null;
            }

            ReleaseInfo info = new ReleaseInfo();
            info.Tag = release.TagName;
            info.Version = NormalizeVersion(release.TagName);

            if (release.Assets != null)
            {
                foreach (GitHubAsset asset in release.Assets)
                {
                    if (asset == null || string.IsNullOrEmpty(asset.Name) || string.IsNullOrEmpty(asset.BrowserDownloadUrl))
                        continue;
                    if (!IsWindowsArchive(asset.Name)) continue;
                    info.ZipName = asset.Name;
                    info.ZipUrl = asset.BrowserDownloadUrl;
                    info.ZipSize = asset.Size;
                    break;
                }
            }

            if (info.ZipUrl.Length == 0)
            {
                error = L.T("update.error.noAsset");
                return null;
            }

            if (release.Assets != null)
            {
                foreach (GitHubAsset asset in release.Assets)
                {
                    if (asset == null || string.IsNullOrEmpty(asset.Name) || string.IsNullOrEmpty(asset.BrowserDownloadUrl))
                        continue;
                    if (!string.Equals(asset.Name, info.ZipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                        continue;
                    info.ChecksumName = asset.Name;
                    info.ChecksumUrl = asset.BrowserDownloadUrl;
                    break;
                }
            }

            if (info.ChecksumUrl.Length == 0)
            {
                error = L.T("update.error.noChecksum");
                return null;
            }

            if (!IsTrustedUrl(info.ZipUrl) || !IsTrustedUrl(info.ChecksumUrl))
            {
                error = L.T("update.error.untrustedUrl");
                return null;
            }

            return info;
        }

        internal static string NormalizeVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            string text = tag.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            return text;
        }

        internal static bool IsNewer(string latest, string current)
        {
            if (string.IsNullOrEmpty(latest)) return false;
            if (string.IsNullOrEmpty(current) || string.Equals(current, "dev", StringComparison.OrdinalIgnoreCase))
                return true;
            return VersionUtil.Compare(latest, current) > 0;
        }

        internal static bool IsWindowsArchive(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0) return false;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
            if (!name.StartsWith("pi-web-tray-", StringComparison.OrdinalIgnoreCase)) return false;
            return name.EndsWith("-win.zip", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsTrustedUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

            string host = uri.Host.ToLowerInvariant();
            if (host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal)) return true;
            if (host == "githubusercontent.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal))
                return true;
            if (host == "githubassets.com" || host.EndsWith(".githubassets.com", StringComparison.Ordinal))
                return true;
            return false;
        }

        internal static string ParseChecksum(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (string rawLine in text.Replace("\r", "").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                int end = 0;
                while (end < line.Length && IsHex(line[end])) end++;
                if (end != 64) continue;
                if (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != '*') continue;
                return line.Substring(0, end).ToLowerInvariant();
            }
            return "";
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        // ---------------- download / prepare ----------------

        internal static string Prepare(Config config, ReleaseInfo release, Action<int> progress, out string error)
        {
            error = "";
            try
            {
                string root = SafeFullPath(Path.Combine(config.DataDir, "update"));
                CleanupOld(root);

                string work = Path.Combine(root,
                    SafeFilePart(release.Version) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(work);

                AppendLog(config.UpdateLogFile, "==== update " + release.Tag + " ====");
                AppendLog(config.UpdateLogFile, "release : " + release.ZipName);
                AppendLog(config.UpdateLogFile, "work    : " + work);

                string zipPath = Path.Combine(work, release.ZipName);
                if (!Download(release.ZipUrl, zipPath, release.ZipSize, progress, out error))
                {
                    AppendLog(config.UpdateLogFile, "download failed: " + error);
                    return null;
                }

                string checksumText = GetText(release.ChecksumUrl, 20000, false, out error);
                if (checksumText == null)
                {
                    AppendLog(config.UpdateLogFile, "checksum download failed: " + error);
                    return null;
                }

                string expected = ParseChecksum(checksumText);
                if (expected.Length == 0)
                {
                    error = L.T("update.error.badChecksum");
                    AppendLog(config.UpdateLogFile, "checksum invalid");
                    return null;
                }

                string actual = ComputeSha256(zipPath);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    error = L.T("update.error.badHash");
                    AppendLog(config.UpdateLogFile,
                        "sha256 mismatch: expected " + expected + ", got " + actual);
                    return null;
                }
                AppendLog(config.UpdateLogFile, "sha256  : " + actual);

                string staging = Path.Combine(work, "staging");
                Directory.CreateDirectory(staging);
                try
                {
                    Extract(zipPath, staging);
                }
                catch (Exception ex)
                {
                    error = L.T("update.error.extract", ex.Message);
                    AppendLog(config.UpdateLogFile, "extract failed: " + ex);
                    return null;
                }

                if (!File.Exists(Path.Combine(staging, "pi-web-tray.exe")))
                {
                    error = L.T("update.error.noHelper");
                    AppendLog(config.UpdateLogFile, "staging has no pi-web-tray.exe");
                    return null;
                }

                AppendLog(config.UpdateLogFile, "staging : " + staging);
                try { File.Delete(zipPath); } catch (Exception) { }
                return staging;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppendLog(config.UpdateLogFile, "prepare failed: " + ex);
                return null;
            }
        }

        private static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); } catch (Exception) { return path; }
        }

        private static string SafeFilePart(string value)
        {
            if (string.IsNullOrEmpty(value)) return "update";

            StringBuilder sb = new StringBuilder();
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == '+') sb.Append(c);
            }
            string text = sb.ToString().Trim('.');
            if (text.Length == 0) return "update";
            if (text.Length > 64) text = text.Substring(0, 64);
            return text;
        }

        private static void CleanupOld(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (string directory in Directory.GetDirectories(root))
                {
                    try { Directory.Delete(directory, true); } catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool Download(string url, string path, long expectedSize, Action<int> progress, out string error)
        {
            error = "";
            try
            {
                HttpWebRequest request = CreateRequest(url, 60000, false);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream input = response.GetResponseStream())
                using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    long total = response.ContentLength > 0 ? response.ContentLength : expectedSize;
                    byte[] buffer = new byte[81920];
                    long received = 0;
                    int lastReported = -2;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        received += read;

                        int percent = total > 0 ? (int)(received * 100 / total) : -1;
                        if (percent != lastReported)
                        {
                            lastReported = percent;
                            if (progress != null)
                            {
                                try { progress(percent); } catch (Exception) { }
                            }
                        }
                    }
                }
                return true;
            }
            catch (WebException ex)
            {
                error = DescribeWebError(ex);
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) sb.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // Hash the set of staged files (relative path + per-file SHA-256). The tray
        // computes it before starting the helper; the helper recomputes it after the UAC
        // prompt, so replacing a staged file while the helper waits is detected.
        internal static string ComputeStagingHash(string staging)
        {
            List<string> files = new List<string>();
            foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                if (ShouldSkip(RelativePath(staging, file))) continue;
                files.Add(file);
            }
            files.Sort(delegate (string a, string b)
            {
                return string.Compare(
                    RelativePath(staging, a), RelativePath(staging, b),
                    StringComparison.OrdinalIgnoreCase);
            });

            using (SHA256 master = SHA256.Create())
            {
                foreach (string file in files)
                {
                    string relative = RelativePath(staging, file)
                        .Replace('\\', '/').ToLowerInvariant();
                    byte[] nameBytes = Encoding.UTF8.GetBytes(relative + "\n");
                    master.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);

                    string fileHash = ComputeSha256(file);
                    byte[] hashBytes = Encoding.ASCII.GetBytes(fileHash + "\n");
                    master.TransformBlock(hashBytes, 0, hashBytes.Length, null, 0);
                }
                master.TransformFinalBlock(new byte[0], 0, 0);

                StringBuilder sb = new StringBuilder(master.Hash.Length * 2);
                foreach (byte value in master.Hash) sb.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // Extract entries one by one and reject anything that would escape the target
        // directory. ZipFile.ExtractToDirectory on .NET Framework does not check this.
        private static void Extract(string zipPath, string targetDir)
        {
            string root = Path.GetFullPath(targetDir);
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.FullName)) continue;

                    string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string destination = Path.GetFullPath(Path.Combine(root, name));
                    if (!string.Equals(destination, root, StringComparison.OrdinalIgnoreCase)
                        && !destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("unsafe archive entry: " + entry.FullName);
                    }

                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    entry.ExtractToFile(destination, true);
                }
            }
        }

        // ---------------- start helper ----------------

        internal static bool RequiresElevation(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return true;

            string probe = Path.Combine(directory, ".pi-web-tray-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return false;
            }
            catch (Exception)
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch (Exception) { }
                return true;
            }
        }

        internal static bool StartHelper(Config config, string staging, ApplyStartMode mode, bool elevated, out string error)
        {
            error = "";
            string logPath = SafeFullPath(config.UpdateLogFile);
            try
            {
                string helper = Path.Combine(staging, "pi-web-tray.exe");
                if (!File.Exists(helper))
                {
                    error = L.T("update.error.noHelper");
                    return false;
                }

                string target = SafeFullPath(config.BaseDir);
                string dataDir = SafeFullPath(config.DataDir);
                string stagingHash = ComputeStagingHash(staging);
                string arguments = BuildApplyArguments(
                    staging, target, Process.GetCurrentProcess().Id,
                    mode, elevated, logPath, L.Current, dataDir, stagingHash);

                AppendLog(logPath, "launch helper: " + helper);
                AppendLog(logPath, "staging hash : " + stagingHash);
                AppendLog(logPath, "helper args : " + arguments);

                if (!elevated && RequiresElevation(config.BaseDir))
                {
                    AppendLog(logPath, "install directory needs administrator rights");
                    if (!Sys.RunElevated(helper, arguments, false))
                    {
                        error = L.T("update.error.elevationCancelled");
                        AppendLog(logPath, "elevation cancelled");
                        return false;
                    }
                    return true;
                }

                using (Process helperProcess = Sys.StartHidden(helper, arguments, staging))
                {
                    if (helperProcess == null)
                    {
                        error = L.T("update.error.startHelper");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppendLog(logPath, "start helper failed: " + ex);
                return false;
            }
        }

        internal static string BuildApplyArguments(string staging, string target, int parentPid,
            ApplyStartMode mode, bool elevated, string logPath, string language, string dataDir,
            string stagingHash)
        {
            return "--apply-update "
                + Shell.QuoteArg(staging) + " "
                + Shell.QuoteArg(target) + " "
                + parentPid.ToString(CultureInfo.InvariantCulture) + " "
                + ModeText(mode) + " "
                + (elevated ? "1" : "0") + " "
                + Shell.QuoteArg(logPath) + " "
                + Shell.QuoteArg(language ?? "") + " "
                + Shell.QuoteArg(dataDir) + " "
                + Shell.QuoteArg(stagingHash);
        }

        internal static string ModeText(ApplyStartMode mode)
        {
            if (mode == ApplyStartMode.TrayOnly) return "tray-only";
            if (mode == ApplyStartMode.ServiceSilent) return "service-silent";
            return "tray";
        }

        // ---------------- helper mode: replace files and restart ----------------

        internal static int Apply(string[] args)
        {
            string staging;
            string target;
            string logPath;
            string language;
            string dataDir;
            string stagingHash;
            int parentPid;
            ApplyStartMode mode;
            bool originalElevated;

            if (!TryParseApplyArgs(args, out staging, out target, out parentPid,
                out mode, out originalElevated, out logPath, out language,
                out dataDir, out stagingHash))
            {
                Application.EnableVisualStyles();
                MessageBox.Show(L.T("update.error.badArgs"), L.T("update.applyFailedTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            L.Use(language);
            Application.EnableVisualStyles();

            AppendLog(logPath, "apply start (pid " + Process.GetCurrentProcess().Id + ")");
            AppendLog(logPath, "staging: " + staging);
            AppendLog(logPath, "target : " + target);

            try
            {
                if (!WaitForParent(parentPid, 60000))
                {
                    AppendLog(logPath, "parent did not exit");
                    MessageBox.Show(L.T("update.error.parentTimeout"), L.T("update.applyFailedTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                if (!Directory.Exists(target))
                {
                    AppendLog(logPath, "target directory missing");
                    MessageBox.Show(L.T("update.error.targetMissing"), L.T("update.applyFailedTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                // Do not turn the elevated helper into a generic file copier: the target
                // must already contain a real Pi Web Tray installation. This does not
                // replace code signing, but it stops writes to unrelated protected
                // directories when the helper is invoked with forged arguments.
                string targetReason;
                if (!IsPiWebTrayInstallation(target, out targetReason))
                {
                    AppendLog(logPath, "target is not a Pi Web Tray installation: " + targetReason);
                    MessageBox.Show(L.T("update.error.notInstallation"), L.T("update.applyFailedTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                if (!File.Exists(Path.Combine(staging, "pi-web-tray.exe")))
                {
                    AppendLog(logPath, "staging executable missing");
                    MessageBox.Show(L.T("update.error.noHelper"), L.T("update.applyFailedTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                // The tray fingerprints the staged files before starting the helper.
                // Re-check after the UAC prompt and the parent exit so a local process
                // cannot replace the staged payload while the helper is waiting.
                if (!string.IsNullOrEmpty(stagingHash))
                {
                    string actualStagingHash;
                    try
                    {
                        actualStagingHash = ComputeStagingHash(staging);
                    }
                    catch (Exception ex)
                    {
                        AppendLog(logPath, "staging hash failed: " + ex);
                        MessageBox.Show(L.T("update.error.stagingChanged"), L.T("update.applyFailedTitle"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }

                    if (!string.Equals(actualStagingHash, stagingHash, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLog(logPath, "staging hash mismatch: expected " + stagingHash
                            + ", got " + actualStagingHash);
                        MessageBox.Show(L.T("update.error.stagingChanged"), L.T("update.applyFailedTitle"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }
                }
                else
                {
                    AppendLog(logPath, "staging hash absent (legacy tray)");
                }

                CopyFiles(staging, target, logPath);
                AppendLog(logPath, "copy complete");

                if (!Restart(target, mode, originalElevated, parentPid, logPath, dataDir))
                {
                    MessageBox.Show(L.T("update.error.restart"), L.T("update.applyFailedTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    OfferStartAfterFailure(target, mode, logPath);
                    return 1;
                }

                AppendLog(logPath, "restart requested");
                return 0;
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "apply failed: " + ex);
                MessageBox.Show(L.T("update.applyFailedBody", ex.Message, logPath),
                    L.T("update.applyFailedTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                OfferStartAfterFailure(target, mode, logPath);
                return 1;
            }
        }

        internal static bool IsPiWebTrayInstallation(string target, out string reason)
        {
            reason = "";
            try
            {
                string targetExe = Path.Combine(target, "pi-web-tray.exe");
                if (!File.Exists(targetExe))
                {
                    reason = "pi-web-tray.exe is missing";
                    return false;
                }

                FileVersionInfo info = FileVersionInfo.GetVersionInfo(targetExe);
                if (!string.Equals(info.ProductName, "Pi Web Tray", StringComparison.Ordinal))
                {
                    reason = "product name is '" + info.ProductName + "'";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static void OfferStartAfterFailure(string target, ApplyStartMode mode, string logPath)
        {
            try
            {
                string exe = Path.Combine(target, "pi-web-tray.exe");
                if (!File.Exists(exe)) return;

                DialogResult answer = MessageBox.Show(
                    L.T("update.startAfterFailureBody"),
                    L.T("update.applyFailedTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1);
                if (answer != DialogResult.Yes) return;

                string args = mode == ApplyStartMode.TrayOnly ? "--tray-only"
                    : mode == ApplyStartMode.ServiceSilent ? "--start-no-open" : "";
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = args;
                psi.WorkingDirectory = target;
                psi.UseShellExecute = true;
                using (Process tray = Process.Start(psi))
                {
                }
                AppendLog(logPath, "started after failure: " + exe + " " + args);
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "start after failure failed: " + ex);
            }
        }

        internal static bool TryParseApplyArgs(string[] args, out string staging, out string target,
            out int parentPid, out ApplyStartMode mode, out bool originalElevated,
            out string logPath, out string language, out string dataDir, out string stagingHash)
        {
            staging = "";
            target = "";
            logPath = "";
            language = "";
            dataDir = "";
            stagingHash = "";
            parentPid = 0;
            mode = ApplyStartMode.Tray;
            originalElevated = false;

            int index = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--apply-update", StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            // The hash argument is optional so a helper from a newer release can still
            // apply an update started by an older tray that used the nine-argument form.
            bool hasHash = args.Length == index + 10;
            if (index < 0 || (args.Length != index + 9 && !hasHash)) return false;

            staging = args[index + 1];
            target = args[index + 2];
            if (!int.TryParse(args[index + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out parentPid)
                || parentPid < 0)
            {
                return false;
            }

            string modeText = args[index + 4];
            if (string.Equals(modeText, "tray", StringComparison.OrdinalIgnoreCase)) mode = ApplyStartMode.Tray;
            else if (string.Equals(modeText, "tray-only", StringComparison.OrdinalIgnoreCase)) mode = ApplyStartMode.TrayOnly;
            else if (string.Equals(modeText, "service-silent", StringComparison.OrdinalIgnoreCase)) mode = ApplyStartMode.ServiceSilent;
            else return false;

            originalElevated = string.Equals(args[index + 5], "1", StringComparison.Ordinal);
            logPath = args[index + 6];
            language = args[index + 7];
            dataDir = args[index + 8];
            if (hasHash) stagingHash = args[index + 9];
            return staging.Length > 0 && target.Length > 0 && dataDir.Length > 0;
        }

        private static bool WaitForParent(int pid, int timeoutMs)
        {
            if (pid <= 0) return true;

            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                try
                {
                    using (Process parent = Process.GetProcessById(pid))
                    {
                        if (parent.HasExited) return true;
                    }
                }
                catch (ArgumentException)
                {
                    return true;
                }
                catch (Exception)
                {
                }
                Thread.Sleep(200);
            }
            return false;
        }

        internal static void CopyFiles(string staging, string target, string logPath)
        {
            string backupRoot = Path.Combine(target, ".pi-web-tray-backup-" + Guid.NewGuid().ToString("N"));
            List<string> copied = new List<string>();
            List<string> backupSources = new List<string>();
            List<string> backupTargets = new List<string>();

            List<string> files = new List<string>(Directory.GetFiles(staging, "*", SearchOption.AllDirectories));
            files.Sort(delegate (string a, string b)
            {
                return IsMainExecutable(a).CompareTo(IsMainExecutable(b));
            });

            try
            {
                // Remove stale backups from an earlier failed attempt. A failed copy is
                // rolled back immediately, so anything left here is no longer needed.
                try
                {
                    foreach (string old in Directory.GetDirectories(target, ".pi-web-tray-backup-*"))
                    {
                        try { Directory.Delete(old, true); } catch (Exception) { }
                    }
                }
                catch (Exception)
                {
                }

                Directory.CreateDirectory(backupRoot);

                foreach (string source in files)
                {
                    string relative = RelativePath(staging, source);
                    if (ShouldSkip(relative)) continue;

                    string destination = Path.Combine(target, relative);
                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    if (File.Exists(destination))
                    {
                        string backup = Path.Combine(backupRoot, relative);
                        string backupDir = Path.GetDirectoryName(backup);
                        if (!string.IsNullOrEmpty(backupDir)) Directory.CreateDirectory(backupDir);
                        try { File.SetAttributes(destination, FileAttributes.Normal); } catch (Exception) { }
                        File.Move(destination, backup);
                        backupSources.Add(backup);
                        backupTargets.Add(destination);
                    }

                    File.Copy(source, destination, true);
                    copied.Add(destination);
                    AppendLog(logPath, "copy " + relative);
                }
            }
            catch (Exception)
            {
                // Put the previous installation back so a failed copy does not leave a
                // half-updated application that cannot start.
                for (int i = copied.Count - 1; i >= 0; i--)
                {
                    try { File.Delete(copied[i]); } catch (Exception) { }
                }
                for (int i = backupSources.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        string parent = Path.GetDirectoryName(backupTargets[i]);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        File.Move(backupSources[i], backupTargets[i]);
                    }
                    catch (Exception)
                    {
                    }
                }
                try { Directory.Delete(backupRoot, true); } catch (Exception) { }
                throw;
            }

            try { Directory.Delete(backupRoot, true); } catch (Exception) { }
        }

        private static bool IsMainExecutable(string path)
        {
            return string.Equals(Path.GetFileName(path), "pi-web-tray.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string RelativePath(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("path outside staging: " + path);
            return fullPath.Substring(fullRoot.Length);
        }

        private static bool ShouldSkip(string relative)
        {
            if (string.Equals(relative, "pi-web-tray.ini", StringComparison.OrdinalIgnoreCase)) return true;

            string name = Path.GetFileName(relative);
            if (string.Equals(name, "state.ini", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, "diag.txt", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".log.1", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool Restart(string target, ApplyStartMode mode, bool originalElevated,
            int parentPid, string logPath, string dataDir)
        {
            string exe = Path.Combine(target, "pi-web-tray.exe");
            string arguments = mode == ApplyStartMode.TrayOnly ? "--tray-only"
                : mode == ApplyStartMode.ServiceSilent ? "--start-no-open" : "";
            bool helperElevated = Elevation.IsElevated();

            // An elevated helper would normally create an elevated child. In the common
            // case (no extra arguments) let Explorer launch it with the logged-on user's
            // medium token, then confirm that the new tray wrote its state file. If that
            // is not observable, fall back to a direct start.
            //
            // Explorer does not preserve PI_WEB_TRAY_* overrides, so skip this path when
            // the current instance uses a custom data directory, port or language.
            if (!originalElevated && helperElevated && !HasCustomEnvironment())
            {
                string cmdPath = null;
                try
                {
                    if (arguments.Length == 0)
                    {
                        AppendLog(logPath, "restart via explorer (de-elevate)");
                        ProcessStartInfo viaExplorer = new ProcessStartInfo();
                        viaExplorer.FileName = "explorer.exe";
                        viaExplorer.Arguments = Shell.QuoteArg(exe);
                        viaExplorer.WorkingDirectory = target;
                        viaExplorer.UseShellExecute = true;
                        using (Process explorer = Process.Start(viaExplorer))
                        {
                        }
                    }
                    else
                    {
                        cmdPath = LaunchViaExplorerCommand(dataDir, target, arguments, logPath);
                    }

                    if (arguments.Length == 0 || cmdPath != null)
                    {
                        if (WaitForNewTray(dataDir, parentPid, 10000))
                        {
                            AppendLog(logPath, "new tray observed after explorer restart");
                            return true;
                        }
                        AppendLog(logPath, "explorer restart not observed; falling back");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog(logPath, "explorer restart failed: " + ex.Message);
                }
                finally
                {
                    if (cmdPath != null)
                    {
                        try { File.Delete(cmdPath); } catch (Exception) { }
                    }
                }
            }

            try
            {
                AppendLog(logPath, "restart: " + exe + " " + arguments);
                ProcessStartInfo direct = new ProcessStartInfo();
                direct.FileName = exe;
                direct.Arguments = arguments;
                direct.WorkingDirectory = target;
                direct.UseShellExecute = true;
                using (Process tray = Process.Start(direct))
                {
                }
                return true;
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "restart failed: " + ex);
                return false;
            }
        }

        // Explorer cannot pass command-line arguments to the new tray. A temporary
        // batch file lets Explorer start the app with the same medium token and the
        // required --tray-only / --start-no-open flag.
        private static string LaunchViaExplorerCommand(string dataDir, string target,
            string arguments, string logPath)
        {
            try
            {
                string exe = Path.Combine(target, "pi-web-tray.exe");
                string safeTarget = target.Replace("%", "%%");
                string safeExe = exe.Replace("%", "%%");
                string cmdPath = Path.Combine(dataDir,
                    ".pi-web-tray-restart-" + Guid.NewGuid().ToString("N") + ".cmd");

                string content = "@echo off\r\n"
                    + "cd /d " + Shell.QuoteArg(safeTarget) + "\r\n"
                    + "start \"\" " + Shell.QuoteArg(safeExe) + " " + arguments + "\r\n"
                    + "del /f /q \"%~f0\" >nul 2>&1\r\n";

                Directory.CreateDirectory(dataDir);
                File.WriteAllText(cmdPath, content, Encoding.Default);
                AppendLog(logPath, "restart command: " + cmdPath);

                ProcessStartInfo viaExplorer = new ProcessStartInfo();
                viaExplorer.FileName = "explorer.exe";
                viaExplorer.Arguments = Shell.QuoteArg(cmdPath);
                viaExplorer.WorkingDirectory = target;
                viaExplorer.UseShellExecute = true;
                using (Process explorer = Process.Start(viaExplorer))
                {
                }
                return cmdPath;
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "explorer command failed: " + ex.Message);
                return null;
            }
        }

        private static bool HasCustomEnvironment()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_INSTANCE"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORT"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_LANG"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_NO_NODE"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_NO_PIWEB"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_WEB_TRAY_NO_PICLI"));
        }

        private static bool WaitForNewTray(string dataDir, int oldPid, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            string path = Path.Combine(dataDir, "state.ini");

            while (DateTime.Now < deadline)
            {
                Thread.Sleep(250);
                try
                {
                    if (!File.Exists(path)) continue;
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine.Trim();
                        if (!line.StartsWith("Pid=", StringComparison.OrdinalIgnoreCase)) continue;

                        int pid;
                        if (!int.TryParse(line.Substring(4).Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out pid))
                        {
                            continue;
                        }
                        if (pid > 0 && pid != oldPid && Sys.ProcessAlive(pid)) return true;
                    }
                }
                catch (Exception)
                {
                }
            }
            return false;
        }

        // ---------------- network / logging helpers ----------------

        private static HttpWebRequest CreateRequest(string url, int timeoutMs, bool githubApi)
        {
            // GitHub requires TLS 1.2. Windows 7 only enables it when the runtime and
            // system policy allow it; requesting it explicitly is harmless elsewhere.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "pi-web-tray/" + Program.Version;
            if (githubApi) request.Accept = "application/vnd.github+json";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.AllowAutoRedirect = true;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return request;
        }

        private static string GetText(string url, int timeoutMs, bool githubApi, out string error)
        {
            error = "";
            try
            {
                HttpWebRequest request = CreateRequest(url, timeoutMs, githubApi);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                error = DescribeWebError(ex);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static string DescribeWebError(WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                try
                {
                    int code = (int)response.StatusCode;
                    if (code == 403) return L.T("update.error.rateLimit");
                    if (code == 404) return L.T("update.error.notFound");
                    if (code >= 500) return L.T("update.error.server");
                    return L.T("update.error.http", code);
                }
                finally
                {
                    response.Close();
                }
            }

            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.ProxyNameResolutionFailure:
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.Timeout:
                    return L.T("update.error.network");
                default:
                    return ex.Message;
            }
        }

        internal static void AppendLog(string path, string line)
        {
            if (string.IsNullOrEmpty(path)) return;

            lock (LogLock)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        string backup = path + ".1";
                        try
                        {
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(path, backup);
                        }
                        catch (Exception)
                        {
                            // The other process may still have the log open. Keep
                            // appending instead of dropping this line.
                        }
                    }

                    // File.AppendAllText opens with FileShare.Read, which conflicts with
                    // the tray and the helper writing at the same time. FileShare.ReadWrite
                    // keeps the launch and apply lines from both processes.
                    using (FileStream stream = new FileStream(
                        path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
