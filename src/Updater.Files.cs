// Pi Web Tray — file replacement and stale-file cleanup
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
            List<string> newManaged = ManagedFiles(staging, files);

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

            // The file list is written by the release package. On the first update from
            // an older release there is no list, so stale files can only be cleaned from
            // the next update onward. User data and logs are never in the list.
            CleanupStaleFiles(target, newManaged, logPath);
        }

        private static List<string> ManagedFiles(string staging, List<string> files)
        {
            List<string> result = new List<string>();
            foreach (string source in files)
            {
                string relative = RelativePath(staging, source);
                if (ShouldSkip(relative)) continue;
                result.Add(relative.Replace('\\', '/'));
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static void CleanupStaleFiles(string target, List<string> newManaged, string logPath)
        {
            string manifest = Path.Combine(target, ManifestName);
            List<string> oldManaged = ReadManifest(manifest);
            List<string> keep = new List<string>(newManaged);

            if (oldManaged.Count == 0)
            {
                AppendLog(logPath, "stale cleanup: no previous file manifest");
            }
            else
            {
                Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string relative in newManaged) current[relative] = relative;

                foreach (string relative in oldManaged)
                {
                    if (current.ContainsKey(relative)) continue;

                    string full = SafeTargetPath(target, relative);
                    if (full == null)
                    {
                        AppendLog(logPath, "stale cleanup: rejected path " + relative);
                        continue;
                    }

                    // Resolve dot segments before comparing. A manifest entry such as
                    // "sub/../pi-web-tray.exe" must not delete the executable that the
                    // current release just installed.
                    string canonical = TargetRelativePath(target, full);
                    if (string.IsNullOrEmpty(canonical)) continue;
                    if (current.ContainsKey(canonical)) continue;

                    try
                    {
                        if (File.Exists(full))
                        {
                            File.SetAttributes(full, FileAttributes.Normal);
                            File.Delete(full);
                            AppendLog(logPath, "remove stale " + canonical);
                        }
                        DeleteEmptyParents(target, full);
                    }
                    catch (Exception ex)
                    {
                        // Keep the failed entry in the manifest so a future update retries it.
                        keep.Add(relative);
                        AppendLog(logPath, "stale cleanup failed: " + relative + " (" + ex.Message + ")");
                    }
                }
            }

            try
            {
                WriteManifest(manifest, keep);
            }
            catch (Exception ex)
            {
                // The files themselves were copied successfully; a missing manifest only
                // means the next update cannot remove stale files.
                AppendLog(logPath, "write manifest failed: " + ex.Message);
            }
        }

        internal static List<string> ReadManifest(string path)
        {
            List<string> result = new List<string>();
            try
            {
                if (!File.Exists(path)) return result;
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string relative = NormalizeManifestPath(rawLine);
                    if (relative.Length == 0) continue;
                    if (string.Equals(relative, ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (result.FindIndex(delegate (string item)
                    {
                        return string.Equals(item, relative, StringComparison.OrdinalIgnoreCase);
                    }) < 0)
                    {
                        result.Add(relative);
                    }
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        internal static void WriteManifest(string path, IEnumerable<string> relativeFiles)
        {
            List<string> files = new List<string>(relativeFiles);
            files.Sort(StringComparer.OrdinalIgnoreCase);

            StringBuilder sb = new StringBuilder();
            foreach (string relative in files)
            {
                string normalized = NormalizeManifestPath(relative);
                if (normalized.Length == 0) continue;
                if (string.Equals(normalized, ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(normalized).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string TargetRelativePath(string target, string fullPath)
        {
            try
            {
                string root = Path.GetFullPath(target)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return fullPath.Substring(root.Length).Replace('\\', '/');
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string NormalizeManifestPath(string path)
        {
            string result = (path ?? "").Trim().Replace('\\', '/');
            while (result.StartsWith("./", StringComparison.Ordinal)) result = result.Substring(2);
            return result;
        }

        private static string SafeTargetPath(string target, string relative)
        {
            try
            {
                string root = Path.GetFullPath(target)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void DeleteEmptyParents(string target, string file)
        {
            try
            {
                string root = Path.GetFullPath(target)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string directory = Path.GetDirectoryName(file);
                while (!string.IsNullOrEmpty(directory)
                    && !string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), root,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(directory) || Directory.GetFileSystemEntries(directory).Length > 0) break;
                    Directory.Delete(directory, false);
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (Exception)
            {
            }
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
            if (string.Equals(relative, ManifestName, StringComparison.OrdinalIgnoreCase)) return true;

            string name = Path.GetFileName(relative);
            if (string.Equals(name, "state.ini", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, "diag.txt", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".log.1", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
