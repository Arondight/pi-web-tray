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
        private static string IncompleteMarker(string backupRoot)
        {
            return backupRoot + ".incomplete";
        }

        // Written only after every file has been copied. A backup with .incomplete
        // but no .complete was interrupted while being written and may be the only
        // recoverable copy of some previous files; automatic cleanup must preserve it.
        private static string CompleteMarker(string backupRoot)
        {
            return backupRoot + ".complete";
        }

        // This marker is written only when Rollback cannot fully restore the previous
        // installation. Such a backup may be the only recoverable copy and must never
        // be removed by automatic cleanup.
        private static string KeepMarker(string backupRoot)
        {
            return backupRoot + ".keep";
        }

        private static void ProtectOwnerForUpdate(string path, bool directory)
        {
            if (!Elevation.IsElevated()) return;
            string error;
            bool ok = directory
                ? Autostart.ProtectDirectoryOwner(path, out error)
                : Autostart.ProtectFileOwner(path, out error);
            if (!ok) throw new UnauthorizedAccessException(error);
        }

        private static bool IsInside(string path, string root)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string prefix = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static UpdateCopyResult CopyFiles(string staging, string target, string logPath)
        {
            string backupRoot = Path.Combine(target, ".pi-web-tray-backup-" + Guid.NewGuid().ToString("N"));
            List<string> attempted = new List<string>();
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
                // Never delete an older backup here: it may be the only copy left after a
                // crash during a previous update. Successful updates remove it explicitly.
                Directory.CreateDirectory(backupRoot);
                ProtectOwnerForUpdate(backupRoot, true);
                File.WriteAllText(IncompleteMarker(backupRoot), "",
                    new UTF8Encoding(false));
                ProtectOwnerForUpdate(IncompleteMarker(backupRoot), false);

                // Keep old-release files that are about to become stale in the backup
                // as well. CleanupStaleFiles runs only after a successful restart; the
                // retained backup is therefore a complete rollback source rather than
                // only a copy of files that happened to exist in the new release.
                // Files that are replaced by a directory (or whose parent becomes a
                // file) are moved now so the new layout can be created; other old-only
                // files are copied and remain in the target until stale cleanup.
                BackupOldOnlyFiles(target, backupRoot, newManaged, backupSources,
                    backupTargets, logPath);

                foreach (string source in files)
                {
                    string relative = RelativePath(staging, source);
                    if (ShouldSkip(relative)) continue;

                    string destination = Path.Combine(target, relative);
                    if (PathSafety.ContainsReparsePoint(destination))
                        throw new InvalidDataException("update destination crosses a reparse point: " + relative);
                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        bool parentExisted = Directory.Exists(parent);
                        Directory.CreateDirectory(parent);
                        if (!parentExisted) ProtectOwnerForUpdate(parent, true);
                    }

                    if (File.Exists(destination))
                    {
                        string backup = Path.Combine(backupRoot, relative);
                        string backupDir = Path.GetDirectoryName(backup);
                        if (!string.IsNullOrEmpty(backupDir))
                        {
                            bool backupDirExisted = Directory.Exists(backupDir);
                            Directory.CreateDirectory(backupDir);
                            if (!backupDirExisted) ProtectOwnerForUpdate(backupDir, true);
                        }
                        try { File.SetAttributes(destination, FileAttributes.Normal); } catch (Exception) { }
                        File.Move(destination, backup);
                        // Track the move before the owner repair. If ProtectOwnerForUpdate
                        // fails, the catch block must put this file back before deleting
                        // the backup directory.
                        backupSources.Add(backup);
                        backupTargets.Add(destination);
                        ProtectOwnerForUpdate(backup, false);
                    }

                    // Remember every destination before copying. A failed File.Copy can
                    // leave a partial file; the rollback must delete it before restoring
                    // the previous file or File.Move will refuse to overwrite it.
                    attempted.Add(destination);
                    bool moved = false;
                    // The helper's verified snapshot lives inside the installation
                    // directory. Moving from it preserves the trusted owner that was
                    // assigned when the snapshot was extracted; a plain File.Copy
                    // would create a new file owned by the elevated helper's user.
                    if (IsInside(source, target))
                    {
                        try
                        {
                            File.Move(source, destination);
                            moved = true;
                        }
                        catch (IOException)
                        {
                            // Fall back to a copy below (for example a cross-volume
                            // alias); the destination owner is repaired explicitly.
                        }
                    }
                    if (!moved) File.Copy(source, destination, true);
                    ProtectOwnerForUpdate(destination, false);
                    AppendLog(logPath, "copy " + relative);
                }
            }
            catch (Exception)
            {
                // Put the previous installation back so a failed copy does not leave a
                // half-updated application that cannot start. If a restore fails, keep the
                // backup directory for manual recovery instead of deleting the only copy.
                bool restored = true;
                for (int i = attempted.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (File.Exists(attempted[i]))
                        {
                            File.SetAttributes(attempted[i], FileAttributes.Normal);
                            File.Delete(attempted[i]);
                        }
                    }
                    catch (Exception) { restored = false; }
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
                        restored = false;
                    }
                }
                if (restored)
                {
                    DeleteBackup(backupRoot);
                }
                else
                {
                    // This backup may be the only recoverable copy. Mark it so
                    // automatic cleanup does not remove it after it becomes old.
                    try { File.WriteAllText(KeepMarker(backupRoot), "", new UTF8Encoding(false)); }
                    catch (Exception) { }
                    AppendLog(logPath, "copy rollback incomplete; backup kept: " + backupRoot);
                }
                throw;
            }

            try
            {
                // Mark the snapshot as complete. If this marker cannot be written,
                // the backup remains protected by .incomplete and is never deleted
                // automatically.
                File.WriteAllText(CompleteMarker(backupRoot), "", new UTF8Encoding(false));
                ProtectOwnerForUpdate(CompleteMarker(backupRoot), false);
            }
            catch (Exception)
            {
            }

            UpdateCopyResult result = new UpdateCopyResult();
            result.Backup = backupRoot;
            result.Managed = newManaged;
            return result;
        }

        private static void BackupOldOnlyFiles(string target, string backupRoot,
            List<string> newManaged, List<string> backupSources, List<string> backupTargets,
            string logPath)
        {
            string manifest = Path.Combine(target, ManifestName);
            List<string> oldManaged = ReadManifest(manifest);
            if (oldManaged.Count == 0) return;

            HashSet<string> current = new HashSet<string>(newManaged,
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string oldRelative in oldManaged)
            {
                string full = SafeTargetPath(target, oldRelative);
                if (full == null) continue;

                // Resolve dot segments and "./" before comparing with the new list.
                string relative = TargetRelativePath(target, full);
                if (string.IsNullOrEmpty(relative) || ShouldSkip(relative)) continue;
                if (current.Contains(relative)) continue;
                if (!handled.Add(relative)) continue;
                if (PathSafety.ContainsReparsePoint(full)) continue;
                if (!File.Exists(full)) continue;

                bool replacedByDirectory = false;
                bool replacedByFile = false;
                foreach (string newRelative in newManaged)
                {
                    if (newRelative.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        replacedByDirectory = true;
                        break;
                    }
                    if (relative.StartsWith(newRelative + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        replacedByFile = true;
                        break;
                    }
                }

                string backup = Path.Combine(backupRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                string backupDir = Path.GetDirectoryName(backup);
                if (!string.IsNullOrEmpty(backupDir))
                {
                    bool existed = Directory.Exists(backupDir);
                    Directory.CreateDirectory(backupDir);
                    if (!existed) ProtectOwnerForUpdate(backupDir, true);
                }

                if (replacedByDirectory || replacedByFile)
                {
                    // CreateDirectory cannot replace an old file that becomes a
                    // directory, and File.Copy cannot replace an old directory that
                    // becomes a file. Move this file out of the way now; Rollback
                    // restores it from the tracked backup entry if the update fails.
                    try { File.SetAttributes(full, FileAttributes.Normal); } catch (Exception) { }
                    File.Move(full, backup);
                    // Track the move before the owner repair so a repair failure
                    // restores the old-only file instead of deleting the backup.
                    backupSources.Add(backup);
                    backupTargets.Add(full);
                    ProtectOwnerForUpdate(backup, false);
                    if (replacedByFile) DeleteEmptyParents(target, full);
                }
                else
                {
                    // Leave the old target file in place until the post-restart stale
                    // cleanup. Keeping it in the backup makes the backup complete
                    // without risking the old executable before the restart.
                    File.Copy(full, backup, false);
                    ProtectOwnerForUpdate(backup, false);
                }
                AppendLog(logPath, "backup stale " + relative);
            }
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

        internal static void CleanupStaleFiles(string target, List<string> newManaged, string logPath)
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
                    // Old manifests from a previous buggy version could contain a
                    // runtime log, the user's ini, or a state file. Those are never
                    // release-managed and must not be deleted by stale cleanup.
                    if (ShouldSkip(relative))
                    {
                        keep.Add(relative);
                        continue;
                    }

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
                        // A manifest entry can be redirected by a junction below the
                        // target. Never follow it during elevated stale-file cleanup.
                        if (PathSafety.ContainsReparsePoint(full))
                        {
                            keep.Add(relative);
                            AppendLog(logPath, "stale cleanup rejected reparse path " + relative);
                            continue;
                        }

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

            string manifestError;
            if (!WriteManifest(manifest, keep, out manifestError))
            {
                // The files themselves were copied successfully; a missing manifest only
                // means the next update cannot remove stale files.
                AppendLog(logPath, "write manifest failed: " + manifestError);
            }
        }

        internal static void DeleteBackup(string backupRoot)
        {
            if (string.IsNullOrEmpty(backupRoot)) return;
            bool directoryGone = true;
            try
            {
                if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
            }
            catch (Exception)
            {
                directoryGone = false;
            }
            // Keep the incomplete marker while the backup still exists. It prevents
            // a later cleanup from deleting a backup that is still being written.
            // The .keep marker, created only by a failed Rollback, is the marker that
            // makes automatic cleanup permanent.
            if (!directoryGone) return;
            try { if (File.Exists(IncompleteMarker(backupRoot))) File.Delete(IncompleteMarker(backupRoot)); } catch (Exception) { }
            try { if (File.Exists(CompleteMarker(backupRoot))) File.Delete(CompleteMarker(backupRoot)); } catch (Exception) { }
            try { if (File.Exists(KeepMarker(backupRoot))) File.Delete(KeepMarker(backupRoot)); } catch (Exception) { }
        }

        internal static void DeleteOldBackups(string target)
        {
            DeleteOldBackupsExcept(target, null, null);
        }

        // Called by the already-elevated helper after a successful restart. The
        // current backup is retained for recovery, but older complete backups can be
        // removed even when the replacement tray will run as a standard user and
        // cannot delete files in a protected installation directory itself.
        internal static void DeleteOldBackupsExcept(string target, string currentBackup,
            string logPath)
        {
            try
            {
                string keepPath = "";
                if (!string.IsNullOrEmpty(currentBackup))
                {
                    try { keepPath = Path.GetFullPath(currentBackup); } catch (Exception) { }
                }

                foreach (string old in Directory.GetDirectories(target, ".pi-web-tray-backup-*"))
                {
                    if (keepPath.Length > 0)
                    {
                        string full;
                        try { full = Path.GetFullPath(old); } catch (Exception) { full = old; }
                        if (string.Equals(full, keepPath, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    // A backup with a keep marker may be the only copy of the
                    // previous release after a failed/partial rollback. An incomplete
                    // marker without a complete marker means CopyFiles did not finish
                    // the backup either. Neither may be removed automatically.
                    if (File.Exists(KeepMarker(old))
                        || (File.Exists(IncompleteMarker(old))
                            && !File.Exists(CompleteMarker(old)))) continue;
                    DeleteBackup(old);
                    if (!string.IsNullOrEmpty(logPath))
                        AppendLog(logPath, "removed old update backup " + Path.GetFileName(old));
                }
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(logPath))
                {
                    try { AppendLog(logPath, "cleanup of old update backups failed: " + ex.Message); }
                    catch (Exception) { }
                }
            }
        }

        // A helper cannot delete the backup that contains its own running executable.
        // Remove such leftovers on a later tray start, but only after they are old enough
        // not to belong to an update that is still running.
        internal static void DeleteStaleBackups(string target, TimeSpan minimumAge)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - minimumAge;
                foreach (string old in Directory.GetDirectories(target, ".pi-web-tray-backup-*"))
                {
                    try
                    {
                        if (File.Exists(KeepMarker(old))
                            || (File.Exists(IncompleteMarker(old))
                                && !File.Exists(CompleteMarker(old)))) continue;
                        if (Directory.GetLastWriteTimeUtc(old) >= cutoff) continue;
                        DeleteBackup(old);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // A helper that crashes while copying leaves a protected staging snapshot behind.
        // It contains only release files, never the previous installation, so it is safe
        // to remove after the update lock has been free for a while.
        internal static void DeleteStaleStagingSnapshots(string target, TimeSpan minimumAge)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - minimumAge;
                foreach (string old in Directory.GetDirectories(target, ".pi-web-tray-stage-*"))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(old) >= cutoff) continue;
                        if (PathSafety.ContainsReparsePoint(old)) continue;
                        Directory.Delete(old, true);
                    }
                    catch (Exception)
                    {
                    }
                }

                // SnapshotFromZip deletes its temporary archive file on normal paths;
                // a crash can leave one behind. It is not a directory, so the loop above
                // does not see it.
                foreach (string old in Directory.GetFiles(target, ".pi-web-tray-stage-zip-*"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(old) >= cutoff) continue;
                        if (PathSafety.ContainsReparsePoint(old)) continue;
                        File.Delete(old);
                    }
                    catch (Exception)
                    {
                    }
                }
                // A de-elevating restart script in the installation directory can
                // survive if cmd could not delete itself. Remove only old ones; a
                // current helper may still be waiting for the new tray to start.
                foreach (string script in Directory.GetFiles(target,
                    ".pi-web-tray-restart-*.cmd"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(script) >= cutoff) continue;
                        File.SetAttributes(script, FileAttributes.Normal);
                        File.Delete(script);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // If the new executable does not come up, put the previous release back. Stale-file
        // cleanup is deferred until after a successful restart, so the old installation is
        // still complete when this runs.
        internal static bool Rollback(string backupRoot, string target, List<string> newManaged, string logPath)
        {
            if (string.IsNullOrEmpty(backupRoot) || !Directory.Exists(backupRoot)) return false;
            try
            {
                List<string> files = new List<string>(Directory.GetFiles(backupRoot, "*", SearchOption.AllDirectories));
                HashSet<string> oldFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files) oldFiles.Add(RelativePath(backupRoot, file).Replace('\\', '/'));

                bool complete = true;
                foreach (string relative in newManaged)
                {
                    if (oldFiles.Contains(relative)) continue;
                    string destination = SafeTargetPath(target, relative);
                    if (destination == null || !File.Exists(destination)) continue;
                    try { File.SetAttributes(destination, FileAttributes.Normal); File.Delete(destination); }
                    catch (Exception) { complete = false; }
                    DeleteEmptyParents(target, destination);
                }

                foreach (string file in files)
                {
                    string relative = RelativePath(backupRoot, file);
                    string destination = Path.Combine(target, relative);
                    if (PathSafety.ContainsReparsePoint(destination))
                    {
                        complete = false;
                        AppendLog(logPath, "rollback rejected reparse path: " + relative);
                        continue;
                    }
                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    try
                    {
                        if (File.Exists(destination))
                        {
                            File.SetAttributes(destination, FileAttributes.Normal);
                            File.Delete(destination);
                        }
                        File.Move(file, destination);
                    }
                    catch (Exception ex)
                    {
                        complete = false;
                        AppendLog(logPath, "rollback restore failed: " + relative + " (" + ex.Message + ")");
                    }
                }
                if (complete)
                {
                    DeleteBackup(backupRoot);
                    AppendLog(logPath, "rollback complete");
                    return true;
                }

                // This backup may be the only recoverable copy; mark it so automatic
                // cleanup never removes it, even after it becomes old.
                try { File.WriteAllText(KeepMarker(backupRoot), "", new UTF8Encoding(false)); }
                catch (Exception) { }
                AppendLog(logPath, "rollback incomplete; backup kept: " + backupRoot);
                return false;
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "rollback failed: " + ex.Message);
                return false;
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

        internal static bool WriteManifest(string path, IEnumerable<string> relativeFiles,
            out string error)
        {
            error = "";
            try
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

                // A manifest created by the elevated helper must not make the
                // protected installation look user-writable to the next start.
                if (Elevation.IsElevated()
                    && !Autostart.ProtectFileOwner(path, out error))
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
                return false;
            }
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
            if (name.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, "diag.txt", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".log.1", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
