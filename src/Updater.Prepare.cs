// Pi Web Tray — download and verify a release archive
//
// The verified zip is kept for the elevated helper, which extracts it into a
// protected target snapshot. Partial of Updater.

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
    internal static partial class Updater
    {
        // A cancelled update worker may still be hashing/inspecting while the user
        // starts another download. Serialize Prepare so the new worker cannot delete
        // the old worker's session directory (and vice versa).
        private static readonly object PrepareLock = new object();

        internal static string Prepare(Config config, ReleaseInfo release, Action<int> progress, out string error)
        {
            return Prepare(config, release, progress, null, out error);
        }

        internal static string Prepare(Config config, ReleaseInfo release, Action<int> progress,
            UpdateCancellation cancellation, out string error)
        {
            lock (PrepareLock)
            {
                return PrepareCore(config, release, progress, cancellation, out error);
            }
        }

        // Keep the current work directory free of reparse points before writing the
        // verified zip. The helper repeats the check after elevation.
        private static string PrepareCore(Config config, ReleaseInfo release, Action<int> progress,
            UpdateCancellation cancellation, out string error)
        {
            error = "";
            try
            {
                if (!ProxyTransport.Configure(config.Proxy, out error))
                {
                    AppendLog(config.UpdateLogFile, "proxy configuration failed: " + error);
                    return null;
                }

                // Keep parallel sessions of the same user from deleting each other's
                // download/staging work. The helper still accepts this layout because
                // it is under dataDir\update.
                string root = SafeFullPath(Path.Combine(config.DataDir, "update",
                    "session-" + Process.GetCurrentProcess().SessionId));
                string unsafePathError = L.T("update.error.unsafePath");
                if (PathSafety.ContainsReparsePoint(root))
                {
                    error = unsafePathError;
                    AppendLog(config.UpdateLogFile, "unsafe update directory: " + root);
                    return null;
                }
                CleanupOld(root);

                string work = Path.Combine(root,
                    SafeFilePart(release.Version) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(work);
                if (PathSafety.ContainsReparsePoint(work))
                {
                    error = unsafePathError;
                    AppendLog(config.UpdateLogFile, "unsafe update work directory: " + work);
                    return null;
                }

                AppendLog(config.UpdateLogFile, "==== update " + release.Tag + " ====");
                AppendLog(config.UpdateLogFile, "release : " + release.ZipName);
                AppendLog(config.UpdateLogFile, "work    : " + work);

                string zipPath = Path.Combine(work, release.ZipName);
                if (!Download(release.ZipUrl, zipPath, release.ZipSize, progress, cancellation, out error))
                {
                    AppendLog(config.UpdateLogFile, "download failed: " + error);
                    return null;
                }

                string checksumText = GetText(release.ChecksumUrl, 20000, false, cancellation, out error);
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
                release.ZipChecksum = expected;
                AppendLog(config.UpdateLogFile, "sha256  : " + actual);

                // Do not extract user-writable staging here. The helper copies the
                // verified zip into the protected target, re-hashes that copy and
                // extracts it. Only inspect the zip now so an obviously invalid archive
                // fails before any UAC prompt or tray exit.
                try
                {
                    using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                    {
                        if (archive.GetEntry("pi-web-tray.exe") == null)
                        {
                            error = L.T("update.error.noHelper");
                            AppendLog(config.UpdateLogFile, "archive has no pi-web-tray.exe");
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = L.T("update.error.extract", ex.Message);
                    AppendLog(config.UpdateLogFile, "archive check failed: " + ex);
                    return null;
                }

                // Kept as a stable path for the helper's data-directory validation.
                // The helper extracts into its own protected snapshot instead.
                string staging = Path.Combine(work, "staging");
                AppendLog(config.UpdateLogFile, "staging : " + staging + " (extracted by helper)");
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

                // Never delete a directory that another process may still be using.
                // A failed/cancelled download is cleaned on a later attempt.
                DateTime cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (string directory in Directory.GetDirectories(root))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) >= cutoff) continue;
                        Directory.Delete(directory, true);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool Download(string url, string path, long expectedSize, Action<int> progress,
            UpdateCancellation cancellation, out string error)
        {
            error = "";
            HttpWebRequest request = null;
            try
            {
                request = CreateRequest(url, 60000, false);
                if (cancellation != null) cancellation.Register(request);
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
            finally
            {
                if (cancellation != null) cancellation.Release(request);
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

        // Security-critical component (zip-slip defence); internal so the test suite can
        // exercise it directly.
        internal static void Extract(string zipPath, string targetDir)
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
    }
}
