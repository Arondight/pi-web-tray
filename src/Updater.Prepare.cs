// Pi Web Tray — download, verify and extract a release
//
// Partial of Updater. Kept separate so the update workflow stays readable.

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
        internal static string Prepare(Config config, ReleaseInfo release, Action<int> progress, out string error)
        {
            return Prepare(config, release, progress, null, out error);
        }

        internal static string Prepare(Config config, ReleaseInfo release, Action<int> progress,
            UpdateCancellation cancellation, out string error)
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
    }
}
