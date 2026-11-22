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
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
                        string backup = PrepareBackupPath(backupRoot, relative);
                        try { File.SetAttributes(destination, FileAttributes.Normal); } catch (Exception) { }
                        // The main executable is replaced atomically: ReplaceFile
                        // moves the old image to the backup and installs the new one
                        // in one operation, so a helper crash cannot leave the
                        // installation without an executable. The staged source is
                        // inside the target, so ReplaceFile's same-volume requirement
                        // is met. Legacy sources outside the target keep the
                        // move-then-copy fallback below.
                        if (IsMainExecutable(relative) && IsInside(source, target))
                        {
                            bool replaced = false;
                            try
                            {
                                File.Replace(source, destination, backup, true);
                                replaced = true;
                            }
                            catch (Exception ex)
                            {
                                // If ReplaceFile failed after moving the old image,
                                // restore it through the normal rollback path. If the
                                // source is gone and both target and backup exist, the
                                // replacement actually happened; track it instead of
                                // reporting a half-completed copy.
                                if (!File.Exists(destination) && File.Exists(backup))
                                {
                                    backupSources.Add(backup);
                                    backupTargets.Add(destination);
                                    throw new IOException(
                                        "atomic executable replacement failed: " + relative, ex);
                                }
                                if (!File.Exists(source) && File.Exists(destination)
                                    && File.Exists(backup))
                                {
                                    replaced = true;
                                }
                                else
                                {
                                    // FAT and other filesystems can reject
                                    // ReplaceFile. A user-writable installation has no
                                    // protected image to lose, so retain the previous
                                    // move-and-copy behavior there. A protected
                                    // installation fails closed instead of reopening the
                                    // missing-executable crash window.
                                    bool canFallback = File.Exists(source)
                                        && File.Exists(destination)
                                        && !File.Exists(backup)
                                        && Autostart.IsUserWritableInstall(
                                            Path.Combine(target, "pi-web-tray.exe"));
                                    if (!canFallback)
                                        throw new IOException(
                                            "atomic executable replacement failed: " + relative, ex);
                                    AppendLog(logPath, "atomic replace unavailable; using copy fallback for " + relative);
                                }
                            }
                            if (replaced)
                            {
                                backupSources.Add(backup);
                                backupTargets.Add(destination);
                                // Track the new destination before owner repair; a
                                // repair failure must delete it and put the old image
                                // back.
                                attempted.Add(destination);
                                ProtectOwnerForUpdate(backup, false);
                                ProtectOwnerForUpdate(destination, false);
                                AppendLog(logPath, "replace " + relative);
                                continue;
                            }
                        }
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

        private static string PrepareBackupPath(string backupRoot, string relative)
        {
            string backup = Path.Combine(backupRoot, relative);
            string backupDir = Path.GetDirectoryName(backup);
            if (!string.IsNullOrEmpty(backupDir))
            {
                bool backupDirExisted = Directory.Exists(backupDir);
                Directory.CreateDirectory(backupDir);
                if (!backupDirExisted) ProtectOwnerForUpdate(backupDir, true);
            }
            return backup;
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

        // Data directories can be shared by multiple installations of the same
        // instance. Keep moved backups in a target-specific directory so one
        // installation never deletes another installation's rollback copy.
        internal static string TargetCleanupKey(string target)
        {
            string normalized;
            try { normalized = PathSafety.CanonicalPath(target); }
            catch (Exception) { normalized = target ?? ""; }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                StringBuilder text = new StringBuilder(32);
                for (int i = 0; i < 16 && i < hash.Length; i++)
                    text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        // A backup contains the helper's running executable, so the helper cannot
        // delete it. Move the retained backup into the data directory and grant
        // Builtin Users delete access on the moved tree. The replacement tray then
        // removes it after a stable state, without making the protected installation
        // directory user-writable. If the data directory is on another volume or is
        // not safe to use, use a sibling cleanup directory instead; the target's
        // parent is always on the same volume as the backup.
        internal static string TargetCleanupRoot(string target)
        {
            try
            {
                string canonical = PathSafety.CanonicalPath(target);
                string parent = Path.GetDirectoryName(canonical);
                if (string.IsNullOrEmpty(parent)) return "";
                return Path.Combine(parent,
                    ".pi-web-tray-cleanup-" + TargetCleanupKey(target));
            }
            catch (Exception)
            {
                return "";
            }
        }

        internal static bool MoveBackupForUserCleanup(string backupRoot, string dataDir,
            string logPath)
        {
            if (string.IsNullOrEmpty(backupRoot) || !Directory.Exists(backupRoot)) return false;
            try
            {
                string target = Path.GetDirectoryName(backupRoot);
                string targetCanonical = PathSafety.CanonicalPath(target);
                if (string.IsNullOrEmpty(target)) return false;
                // A keep/incomplete marker protects a rollback copy. If the marker
                // files were left behind, a cleanup store could mistake the moved
                // backup for an ordinary complete one and delete the only copy.
                if (File.Exists(KeepMarker(backupRoot))
                    || (File.Exists(IncompleteMarker(backupRoot))
                        && !File.Exists(CompleteMarker(backupRoot)))) return false;
                if (PathSafety.ContainsReparsePointInTree(backupRoot)) return false;

                bool elevated = Elevation.IsElevated();
                string dataRoot = null;
                if (!elevated && !string.IsNullOrEmpty(dataDir) && Path.IsPathRooted(dataDir)
                    && !PathSafety.ContainsReparsePoint(dataDir))
                {
                    string dataCanonical = PathSafety.CanonicalPath(dataDir);
                    string candidate = Path.Combine(dataDir, "update", "backups",
                        TargetCleanupKey(target));
                    string candidateCanonical = Path.Combine(dataCanonical, "update", "backups",
                        TargetCleanupKey(target));
                    // Do not move a protected backup back under the protected target.
                    // Canonical checks reject an 8.3 alias of the target, including
                    // aliases in the data path's parent components.
                    if (!PathSafety.ContainsReparsePoint(candidate)
                        && !IsInside(dataCanonical, targetCanonical)
                        && !PathSafety.SameDirectory(dataCanonical, targetCanonical)
                        && !IsInside(candidateCanonical, targetCanonical)
                        && !PathSafety.SameDirectory(candidateCanonical, targetCanonical)
                        && !IsInside(candidate, targetCanonical)
                        && !PathSafety.SameDirectory(candidate, targetCanonical))
                    {
                        dataRoot = candidate;
                    }
                }

                string siblingRoot = TargetCleanupRoot(target);
                bool siblingUsable = !string.IsNullOrEmpty(siblingRoot)
                    && !string.Equals(siblingRoot, dataRoot,
                        StringComparison.OrdinalIgnoreCase)
                    && !IsInside(siblingRoot, targetCanonical)
                    && !PathSafety.SameDirectory(siblingRoot, targetCanonical);

                if (elevated)
                {
                    // The data directory is normally user-writable. An elevated
                    // helper must not grant Builtin Users delete rights -- or move
                    // into a directory -- that a standard-user process can replace
                    // with a junction after the path check. The sibling store is
                    // under the installation's protected parent instead.
                    if (siblingUsable)
                    {
                        string cleanupParent = Path.GetDirectoryName(siblingRoot);
                        // A protected target can still live under a parent that lets
                        // standard users create new entries. That is enough to race
                        // the predictable sibling store, so require the parent to be
                        // create/delete-safe before granting an ACL there.
                        if (string.IsNullOrEmpty(cleanupParent)
                            || Autostart.IsUserWritableInstall(
                                Path.Combine(cleanupParent, "pi-web-tray.exe")))
                        {
                            AppendLog(logPath,
                                "backup cleanup parent is user-writable; backup retained in the protected installation");
                            return false;
                        }
                        if (TryMoveBackupToStore(backupRoot, siblingRoot, logPath))
                            return true;
                    }
                    if (!Directory.Exists(backupRoot)) return false;
                    AppendLog(logPath,
                        "backup retained in the protected installation; a later elevated update can remove it");
                    return false;
                }

                if (dataRoot != null && TryMoveBackupToStore(backupRoot, dataRoot, logPath))
                    return true;
                // If the first move failed after consuming the source, do not try a
                // second store: the backup is already at a recoverable location.
                if (!Directory.Exists(backupRoot)) return false;

                if (siblingUsable && TryMoveBackupToStore(backupRoot, siblingRoot, logPath))
                    return true;
                return false;
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "backup cleanup handoff failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryMoveBackupToStore(string backupRoot, string storeRoot,
            string logPath)
        {
            try
            {
                if (!Directory.Exists(backupRoot) || string.IsNullOrEmpty(storeRoot)
                    || PathSafety.ContainsReparsePoint(storeRoot)) return false;
                if (Elevation.IsElevated())
                {
                    // Create the store with its final DACL in one operation. Creating
                    // it first and repairing it afterwards leaves a window in which
                    // CREATOR OWNER makes the new directory writable by the standard
                    // token.
                    string createError;
                    if (!Autostart.CreateCleanupDirectory(storeRoot, out createError))
                    {
                        AppendLog(logPath, "backup cleanup store protection failed: " + createError);
                        return false;
                    }
                }
                else
                {
                    Directory.CreateDirectory(storeRoot);
                }
                // Re-check after creation: a standard user may have replaced a
                // user-writable parent with a junction before the elevated move.
                if (PathSafety.ContainsReparsePoint(storeRoot)) return false;
                if (Elevation.IsElevated() && Autostart.IsUnsafeWritableDirectory(storeRoot))
                {
                    AppendLog(logPath, "backup cleanup store is user-writable; backup retained");
                    return false;
                }
                // Install the same protected DACL on the source backup before it
                // moves. A moved directory keeps its ACL, so this also closes the
                // window between Directory.Move and the destination repair.
                if (Elevation.IsElevated())
                {
                    string sourceProtectError;
                    if (!Autostart.ProtectCleanupDirectory(backupRoot, false, out sourceProtectError))
                    {
                        AppendLog(logPath, "backup source protection failed: " + sourceProtectError);
                        return false;
                    }
                    if (Autostart.IsUnsafeWritableDirectory(backupRoot))
                    {
                        AppendLog(logPath, "backup source is user-writable; backup retained");
                        return false;
                    }
                }
                // The backup itself contains the helper's running image, so the
                // helper cannot delete it. Give the standard-user cleanup path an
                // explicit delete-only ACE on the store root and on the moved tree.
                GrantBackupDeleteAccess(storeRoot);
                string destination = Path.Combine(storeRoot,
                    ".pi-web-tray-backup-" + Guid.NewGuid().ToString("N"));
                MoveBackupDirectory(backupRoot, destination);
                try
                {
                    if (Elevation.IsElevated())
                    {
                        string protectError;
                        if (!Autostart.ProtectCleanupDirectory(destination, out protectError))
                            throw new UnauthorizedAccessException(protectError);
                        if (Autostart.IsUnsafeWritableDirectory(destination))
                            throw new UnauthorizedAccessException("moved backup is user-writable");
                    }
                    else
                    {
                        ProtectOwnerForUpdate(destination, true);
                    }
                    GrantBackupDeleteAccess(destination);
                }
                catch (Exception ex)
                {
                    try { MoveBackupDirectory(destination, backupRoot); } catch (Exception) { }
                    AppendLog(logPath, "backup cleanup handoff failed: " + ex.Message);
                    return false;
                }
                MoveBackupMarker(backupRoot + ".complete", destination + ".complete");
                MoveBackupMarker(backupRoot + ".incomplete", destination + ".incomplete");
                AppendLog(logPath, "backup moved for user cleanup: "
                    + Path.GetFileName(destination));
                return true;
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "backup cleanup handoff failed: " + ex.Message);
                return false;
            }
        }

        private static void MoveBackupDirectory(string source, string destination)
        {
            // The source contains the helper executable that is still running.
            // Windows normally permits renaming it, but an antivirus/indexer can
            // briefly hold the directory; retry only sharing-style IO failures.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Move(source, destination);
                    return;
                }
                catch (Exception ex)
                {
                    bool transient = ex is IOException
                        || ex is UnauthorizedAccessException;
                    if (attempt == 4 || !transient) throw;
                    Thread.Sleep(100);
                }
            }
        }

        private static void MoveBackupMarker(string source, string destination)
        {
            try
            {
                if (File.Exists(source)) File.Move(source, destination);
            }
            catch (Exception)
            {
                // The marker is advisory. The moved backup itself is retained; a
                // leftover marker in the target is harmless and never grants access.
            }
        }

        private static void GrantBackupDeleteAccess(string directory)
        {
            DirectoryInfo info = new DirectoryInfo(directory);
            DirectorySecurity security = info.GetAccessControl(AccessControlSections.Access);
            SecurityIdentifier users = new SecurityIdentifier(
                WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(users,
                FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }

        // A helper cannot delete the backup that contains its own running executable.
        // Remove such leftovers on a later tray start, but only after they are old enough
        // not to belong to an update that is still running.
        internal static void DeleteStaleBackups(string target, TimeSpan minimumAge)
        {
            try
            {
                // The data-directory handoff store is user-writable. Never follow a
                // junction planted there while the tray may be elevated.
                if (PathSafety.ContainsReparsePoint(target)) return;
                DateTime cutoff = DateTime.UtcNow - minimumAge;
                foreach (string old in Directory.GetDirectories(target, ".pi-web-tray-backup-*"))
                {
                    try
                    {
                        if (PathSafety.ContainsReparsePoint(old)) continue;
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
            string temp = null;
            string backup = null;
            bool keepBackup = false;
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

                // Do not truncate a manifest that was replaced by a reparse point
                // or another file between the target checks and this write. Write a
                // verified sibling and rename it into place instead.
                if (PathSafety.ContainsReparsePoint(path))
                {
                    error = "manifest path crosses a reparse point";
                    return false;
                }
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                backup = path + "." + Guid.NewGuid().ToString("N") + ".bak";
                if (PathSafety.ContainsReparsePoint(temp)
                    || PathSafety.ContainsReparsePoint(backup))
                {
                    error = "manifest temporary path crosses a reparse point";
                    return false;
                }

                FileStream stream = null;
                try
                {
                    stream = new FileStream(temp, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None);
                    if (!LogHost.HandleMatchesExpectedPath(stream, temp))
                    {
                        stream.Dispose();
                        stream = null;
                        try { File.Delete(temp); } catch (Exception) { }
                        error = "manifest temporary path changed";
                        return false;
                    }
                    using (StreamWriter writer = new StreamWriter(
                        stream, new UTF8Encoding(false)))
                    {
                        writer.Write(sb.ToString());
                    }
                }
                finally
                {
                    if (stream != null)
                    {
                        try { stream.Dispose(); } catch (Exception) { }
                    }
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, backup, true);
                    }
                    catch (Exception)
                    {
                        // ReplaceFile is unavailable on some file systems, or it
                        // failed after moving the old file aside. Move the old
                        // manifest only if it is still at the destination, then
                        // restore it if the replacement cannot be installed. Moving
                        // a hard link moves only that directory entry, not the
                        // linked target.
                        bool movedOld = false;
                        if (File.Exists(path))
                        {
                            File.Move(path, backup);
                            movedOld = true;
                        }
                        else if (File.Exists(backup))
                        {
                            movedOld = true;
                        }
                        if (!movedOld) throw;
                        try
                        {
                            File.Move(temp, path);
                        }
                        catch (Exception)
                        {
                            try
                            {
                                if (!File.Exists(path) && File.Exists(backup))
                                    File.Move(backup, path);
                            }
                            catch (Exception)
                            {
                                // Keep the old manifest if it could not be
                                // restored; the next cleanup must not delete the
                                // only recoverable copy.
                                if (!File.Exists(path) && File.Exists(backup))
                                    keepBackup = true;
                            }
                            throw;
                        }
                    }
                }
                else
                {
                    File.Move(temp, path);
                }

                // A manifest created by the elevated helper must not make the
                // protected installation look user-writable to the next start.
                if (Elevation.IsElevated()
                    && !Autostart.ProtectFileOwner(path, out error))
                {
                    // Restore the previous manifest instead of deleting the only
                    // copy when ownership repair fails.
                    bool restored = false;
                    if (File.Exists(backup))
                    {
                        try
                        {
                            if (File.Exists(path)) File.Delete(path);
                            File.Move(backup, path);
                            restored = true;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    if (!restored)
                    {
                        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
                        keepBackup = File.Exists(backup);
                    }
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (temp != null && File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                try { if (!keepBackup && backup != null && File.Exists(backup)) File.Delete(backup); } catch (Exception) { }
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
