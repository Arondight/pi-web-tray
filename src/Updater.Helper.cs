// Pi Web Tray — elevated apply helper
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

        internal static bool StartHelper(Config config, string staging, ApplyStartMode mode, bool elevated,
            bool waitForService, out string error)
        {
            return StartHelper(config, staging, "", "", mode, elevated, waitForService, "", out error);
        }

        internal static bool StartHelper(Config config, string staging, string zipPath, string zipChecksum,
            ApplyStartMode mode, bool elevated, bool waitForService, out string error)
        {
            return StartHelper(config, staging, zipPath, zipChecksum, mode, elevated,
                waitForService, "", out error);
        }

        internal static bool StartHelper(Config config, string staging, string zipPath, string zipChecksum,
            ApplyStartMode mode, bool elevated, bool waitForService, string gateEventName,
            out string error)
        {
            error = "";
            string logPath = SafeFullPath(config.UpdateLogFile);
            try
            {
                // The staged executable is payload, not a safe process to launch with
                // elevation: staging is user-writable and can be replaced after the hash
                // is computed. Run the already-installed helper instead. Production passes
                // the verified zip; the legacy staging path remains for old callers.
                bool useZip = !string.IsNullOrEmpty(zipPath) || !string.IsNullOrEmpty(zipChecksum);
                string stagedExe = Path.Combine(staging, "pi-web-tray.exe");
                if (!useZip && !File.Exists(stagedExe))
                {
                    error = L.T("update.error.noHelper");
                    return false;
                }
                string helper = Application.ExecutablePath;

                string target = SafeFullPath(config.BaseDir);
                string dataDir = SafeFullPath(config.DataDir);

                // Decide before launching anything whether this helper will run with an
                // elevated token. A user-writable installation must never be elevated,
                // even if the write probe below fails transiently (ACL, antivirus or
                // Controlled Folder Access). The same check is repeated in Apply().
                bool needsElevation = false;
                if (!elevated)
                {
                    try { needsElevation = RequiresElevation(config.BaseDir); }
                    catch (Exception) { needsElevation = true; }
                }
                if ((elevated || needsElevation)
                    && Autostart.IsUserWritableInstall(target))
                {
                    error = L.T("update.error.writableInstall");
                    AppendLog(logPath, "elevated update refused for a user-writable installation: " + target);
                    return false;
                }

                // Fail before ExitApp stops the tray if another installer/updater
                // already owns this installation. The helper re-checks the lock
                // before copying; this preflight only avoids a needless outage.
                using (OperationLock preflight = OperationLock.TryAcquire(
                    OperationLock.UpdateName(target)))
                {
                    if (preflight == null)
                    {
                        error = L.T("update.error.busy");
                        AppendLog(logPath, "another update/install owns the installation");
                        return false;
                    }
                }

                zipPath = SafeFullPath(zipPath);
                if (useZip)
                {
                    // The helper re-verifies this archive against the checksum from
                    // the GitHub release before extracting it. Never fall back to the
                    // user-writable staging tree when a zip was expected.
                    if (zipPath.Length == 0 || !File.Exists(zipPath)
                        || !IsHexHash(zipChecksum))
                    {
                        error = L.T("update.error.badHash");
                        AppendLog(logPath, "verified update archive is missing or invalid: " + zipPath);
                        return false;
                    }
                }

                // The staging hash is only used by the legacy staging path. For a
                // verified zip the helper ignores staging entirely, so a deleted or
                // tampered staging tree cannot block the update.
                string stagingHash = useZip
                    ? new string('0', 64)
                    : ComputeStagingHash(staging);
                string arguments = BuildApplyArguments(
                    staging, target, Process.GetCurrentProcess().Id,
                    mode, elevated, logPath, L.Current, dataDir, stagingHash,
                    waitForService ? config.Port : 0, zipPath, zipChecksum, gateEventName);

                AppendLog(logPath, "launch helper: " + helper);
                if (!useZip) AppendLog(logPath, "staging hash : " + stagingHash);
                if (useZip)
                {
                    AppendLog(logPath, "update archive: " + zipPath);
                    AppendLog(logPath, "archive hash : " + zipChecksum);
                }
                AppendLog(logPath, "helper args : " + arguments);

                if (needsElevation)
                {
                    AppendLog(logPath, "install directory needs administrator rights");
                    if (!Sys.RunElevated(helper, arguments, false, Sys.SystemDirectory))
                    {
                        error = L.T("update.error.elevationCancelled");
                        AppendLog(logPath, "elevation cancelled");
                        return false;
                    }
                    return true;
                }

                using (Process helperProcess = Sys.StartHidden(helper, arguments, Sys.SystemDirectory))
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
            string stagingHash, int port)
        {
            return BuildApplyArguments(staging, target, parentPid, mode, elevated, logPath,
                language, dataDir, stagingHash, port, "", "");
        }

        internal static string BuildApplyArguments(string staging, string target, int parentPid,
            ApplyStartMode mode, bool elevated, string logPath, string language, string dataDir,
            string stagingHash, int port, string zipPath, string zipChecksum,
            string gateEventName = null)
        {
            string arguments = "--apply-update "
                + Shell.QuoteArg(staging) + " "
                + Shell.QuoteArg(target) + " "
                + parentPid.ToString(CultureInfo.InvariantCulture) + " "
                + ModeText(mode) + " "
                + (elevated ? "1" : "0") + " "
                + Shell.QuoteArg(logPath) + " "
                + Shell.QuoteArg(language ?? "") + " "
                + Shell.QuoteArg(dataDir) + " "
                + Shell.QuoteArg(stagingHash) + " "
                + port.ToString(CultureInfo.InvariantCulture);

            if (!string.IsNullOrEmpty(zipPath) && !string.IsNullOrEmpty(zipChecksum))
            {
                arguments += " " + Shell.QuoteArg(zipPath)
                    + " " + Shell.QuoteArg(zipChecksum);
            }
            if (!string.IsNullOrEmpty(gateEventName))
                arguments += " " + Shell.QuoteArg(gateEventName);
            return arguments;
        }

        internal static string ModeText(ApplyStartMode mode)
        {
            if (mode == ApplyStartMode.TrayOnly) return "tray-only";
            if (mode == ApplyStartMode.ServiceSilent) return "service-silent";
            return "tray";
        }

        internal static int Apply(string[] args)
        {
            string staging;
            string target;
            string logPath;
            string language;
            string dataDir;
            string stagingHash;
            string zipPath;
            string zipChecksum;
            string gateEventName;
            int port;
            int parentPid;
            ApplyStartMode mode;
            bool originalElevated;

            if (!TryParseApplyArgs(args, out staging, out target, out parentPid,
                out mode, out originalElevated, out logPath, out language,
                out dataDir, out stagingHash, out port, out zipPath, out zipChecksum,
                out gateEventName))
            {
                Application.EnableVisualStyles();
                MessageBox.Show(L.T("update.error.badArgs"), L.T("update.applyFailedTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            L.Use(language);
            Application.EnableVisualStyles();

            // StartHelper always launches the installed helper, so its own directory
            // is the only installation it may replace. Check the target before any
            // file I/O, especially before writing the caller-supplied log path.
            string helperDirectory = SafeFullPath(Path.GetDirectoryName(Application.ExecutablePath))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string targetDirectory = target
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(helperDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(L.T("update.error.notInstallation"), L.T("update.applyFailedTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (!Directory.Exists(target))
            {
                MessageBox.Show(L.T("update.error.targetMissing"), L.T("update.applyFailedTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            // Defense in depth: Apply can also be invoked directly. Never let an
            // elevated helper replace files in a directory writable by standard users.
            if (Elevation.IsElevated() && Autostart.IsUserWritableInstall(target))
            {
                AppendLog(logPath, "elevated apply refused for a user-writable installation: " + target);
                MessageBox.Show(L.T("update.error.writableInstall"), L.T("update.applyFailedTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            // The log path is validated as a fixed update.log under dataDir or target
            // by TryParseApplyArgs. It is safe to write it only after the target has
            // been identified as the helper's own directory.
            AppendLog(logPath, "apply start (pid " + Process.GetCurrentProcess().Id + ")");
            AppendLog(logPath, "staging: " + staging);
            if (zipPath.Length > 0) AppendLog(logPath, "archive: " + zipPath);
            AppendLog(logPath, "target : " + target);

            // Two helpers must never replace the same installation concurrently. The
            // lock is held through the backup/restart phase and is released when this
            // process exits or the apply attempt finishes.
            using (OperationLock updateLock = OperationLock.TryAcquire(
                OperationLock.UpdateName(targetDirectory)))
            {
                if (updateLock == null)
                {
                    AppendLog(logPath, "another update is already applying to this installation");
                    MessageBox.Show(L.T("update.error.busy"), L.T("update.applyFailedTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    // The tray that launched this helper has already exited. Do not
                    // leave the user without a tray just because another helper won
                    // the race; offer to start the existing installation.
                    OfferStartAfterFailure(target, mode, logPath, originalElevated, dataDir);
                    return 1;
                }

                try
                {
                    if (!WaitForParent(parentPid, 60000, Path.Combine(target, "pi-web-tray.exe")))
                    {
                        AppendLog(logPath, "parent did not exit");
                        MessageBox.Show(L.T("update.error.parentTimeout"), L.T("update.applyFailedTitle"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }

                    if (!string.IsNullOrEmpty(gateEventName)
                        && !File.Exists(gateEventName))
                    {
                        AppendLog(logPath, "parent exited without confirming the update gate");
                        MessageBox.Show(L.T("update.error.notConfirmed"), L.T("update.applyFailedTitle"),
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
                    bool validInstallation = IsPiWebTrayInstallation(target, out targetReason);
                    if (!validInstallation && TryRecoverFromBackup(target, logPath))
                    {
                        AppendLog(logPath, "target recovered from a leftover backup");
                        validInstallation = IsPiWebTrayInstallation(target, out targetReason);
                    }
                    if (!validInstallation)
                    {
                        AppendLog(logPath, "target is not a Pi Web Tray installation: " + targetReason);
                        MessageBox.Show(L.T("update.error.notInstallation"), L.T("update.applyFailedTitle"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }

                    bool fromZip = zipPath.Length > 0;
                    if (!fromZip)
                    {
                        if (!Directory.Exists(staging))
                        {
                            AppendLog(logPath, "staging directory missing");
                            MessageBox.Show(L.T("update.error.noHelper"), L.T("update.applyFailedTitle"),
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            OfferStartAfterFailure(target, mode, logPath, originalElevated, dataDir);
                            return 1;
                        }

                        if (!File.Exists(Path.Combine(staging, "pi-web-tray.exe")))
                        {
                            AppendLog(logPath, "staging executable missing");
                            MessageBox.Show(L.T("update.error.noHelper"), L.T("update.applyFailedTitle"),
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            OfferStartAfterFailure(target, mode, logPath, originalElevated, dataDir);
                            return 1;
                        }
                    }

                    // A verified zip is extracted by the helper into a protected snapshot.
                    // The old staging path remains for compatibility with older helpers and
                    // tests; production starts the helper with the archive path/hash.
                    string snapshotError;
                    string snapshot = fromZip
                        ? SnapshotFromZip(zipPath, zipChecksum, target, logPath, out snapshotError)
                        : SnapshotStaging(staging, stagingHash, target, logPath, out snapshotError);
                    if (snapshot == null)
                    {
                        AppendLog(logPath, (fromZip ? "archive snapshot failed: " : "staging snapshot failed: ")
                            + (snapshotError ?? ""));
                        MessageBox.Show(fromZip
                                ? L.T("update.error.zipChanged")
                                : L.T("update.error.stagingChanged"),
                            L.T("update.applyFailedTitle"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        OfferStartAfterFailure(target, mode, logPath, originalElevated, dataDir);
                        return 1;
                    }

                    try
                    {
                        // If ExitApp could not stop the old listener (slow taskkill, elevated
                        // process, or a failed CLI stop), starting a new tray now would only
                        // hit a port conflict. Do not copy anything until the port is free.
                        if (port > 0 && !Net.WaitPortClosed(port, 30000))
                        {
                            AppendLog(logPath, "service still owns port " + port + "; update aborted");
                            MessageBox.Show(L.T("update.error.serviceStillRunning", port),
                                L.T("update.applyFailedTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                            OfferStartAfterFailure(target, mode, logPath, originalElevated, dataDir);
                            return 1;
                        }

                        UpdateCopyResult copy = CopyFiles(snapshot, target, logPath);
                        AppendLog(logPath, "copy complete");

                        if (!Restart(target, mode, originalElevated, parentPid, logPath, dataDir))
                        {
                            bool rolledBack = Rollback(copy.Backup, target, copy.Managed, logPath);
                            string message = L.T("update.error.restart");
                            if (!rolledBack)
                                message += Environment.NewLine + Environment.NewLine
                                    + L.T("update.rollbackBackup", copy.Backup);
                            MessageBox.Show(message, L.T("update.applyFailedTitle"),
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            OfferStartAfterFailure(target, mode, logPath, originalElevated, dataDir);
                            return 1;
                        }

                        CleanupStaleFiles(target, copy.Managed, logPath);
                        DeleteBackup(copy.Backup);
                        DeleteOldBackups(target);
                        AppendLog(logPath, "restart requested");
                        return 0;
                    }
                    finally
                    {
                        DeleteStagingSnapshot(snapshot);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog(logPath, "apply failed: " + ex);
                    MessageBox.Show(L.T("update.applyFailedBody", ex.Message, logPath),
                        L.T("update.applyFailedTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    OfferStartAfterFailure(target, mode, logPath, originalElevated, dataDir);
                    return 1;
                }
            }
        }

        // Legacy path: copy the staging directory into a protected snapshot under the
        // target while denying writes to the source files, then verify the snapshot
        // against the hash the tray computed. New callers use SnapshotFromZip instead,
        // because the hash is computed after a user-writable extraction.
        private static string SnapshotStaging(string staging, string expectedHash, string target,
            string logPath, out string error)
        {
            error = "";
            string snapshot = Path.Combine(target, ".pi-web-tray-stage-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (PathSafety.ContainsReparsePoint(target))
                    throw new InvalidDataException("update target is a reparse point");
                Directory.CreateDirectory(snapshot);
                if (PathSafety.ContainsReparsePoint(snapshot))
                    throw new InvalidDataException("update snapshot is a reparse point");

                foreach (string source in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                {
                    string relative = RelativePath(staging, source);
                    if (ShouldSkip(relative)) continue;
                    if (PathSafety.ContainsReparsePoint(source))
                        throw new InvalidDataException("staging contains a reparse point: " + relative);

                    string destination = Path.Combine(snapshot, relative);
                    if (PathSafety.ContainsReparsePoint(destination))
                        throw new InvalidDataException("staging destination is a reparse point: " + relative);
                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    using (FileStream input = new FileStream(source, FileMode.Open,
                        FileAccess.Read, FileShare.Read))
                    using (FileStream output = new FileStream(destination, FileMode.Create,
                        FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            output.Write(buffer, 0, read);
                    }
                }

                string actual = ComputeStagingHash(snapshot);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    error = L.T("update.error.stagingChanged");
                    AppendLog(logPath, "staging snapshot hash mismatch: expected " + expectedHash
                        + ", got " + actual);
                    DeleteStagingSnapshot(snapshot);
                    return null;
                }
                return snapshot;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppendLog(logPath, "staging snapshot failed: " + ex);
                DeleteStagingSnapshot(snapshot);
                return null;
            }
        }

        // Copy the verified zip into the protected target first, hash the copy, and
        // extract from that copy. The user-writable zip/staging cannot change the
        // bytes the helper authenticates and extracts.
        private static string SnapshotFromZip(string zipPath, string expectedHash, string target,
            string logPath, out string error)
        {
            error = "";
            string zipSnapshot = Path.Combine(target,
                ".pi-web-tray-stage-zip-" + Guid.NewGuid().ToString("N"));
            string snapshot = Path.Combine(target,
                ".pi-web-tray-stage-" + Guid.NewGuid().ToString("N"));
            bool success = false;
            try
            {
                if (PathSafety.ContainsReparsePoint(zipPath))
                    throw new InvalidDataException("update archive is a reparse point");
                if (PathSafety.ContainsReparsePoint(target))
                    throw new InvalidDataException("update target is a reparse point");

                Directory.CreateDirectory(target);
                if (PathSafety.ContainsReparsePoint(zipSnapshot))
                    throw new InvalidDataException("update archive snapshot is a reparse point");
                using (FileStream input = new FileStream(zipPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read))
                using (FileStream output = new FileStream(zipSnapshot, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        output.Write(buffer, 0, read);
                }

                string actual = ComputeSha256(zipSnapshot);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    error = L.T("update.error.zipChanged");
                    AppendLog(logPath, "zip snapshot hash mismatch: expected " + expectedHash
                        + ", got " + actual);
                    return null;
                }

                Directory.CreateDirectory(snapshot);
                if (PathSafety.ContainsReparsePoint(snapshot))
                    throw new InvalidDataException("update snapshot is a reparse point");
                Extract(zipSnapshot, snapshot);
                if (!File.Exists(Path.Combine(snapshot, "pi-web-tray.exe")))
                {
                    error = L.T("update.error.noHelper");
                    AppendLog(logPath, "update archive has no pi-web-tray.exe");
                    return null;
                }

                success = true;
                return snapshot;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppendLog(logPath, "zip snapshot failed: " + ex);
                return null;
            }
            finally
            {
                try { if (File.Exists(zipSnapshot)) File.Delete(zipSnapshot); } catch (Exception) { }
                if (!success) DeleteStagingSnapshot(snapshot);
            }
        }

        private static void DeleteStagingSnapshot(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)
                    && !PathSafety.ContainsReparsePoint(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception)
            {
            }
        }

        // A previous update can crash between moving pi-web-tray.exe into its backup and
        // copying the new one: the target then has no executable, every later update is
        // rejected by the installation check, and DeleteOldBackups would eventually remove
        // the leftover backup (the only surviving copy). Roll it back so this and future
        // updates work again without manual recovery.
        private static bool TryRecoverFromBackup(string target, string logPath)
        {
            try
            {
                // Only the known crash shape is safe to repair: the target lost its
                // executable between moving it to the backup and copying the new one.
                // Do not turn recovery into a generic "restore any backup" operation.
                if (File.Exists(Path.Combine(target, "pi-web-tray.exe"))) return false;

                string[] backups = Directory.GetDirectories(target, ".pi-web-tray-backup-*");
                Array.Sort(backups, StringComparer.OrdinalIgnoreCase);
                foreach (string backup in backups)
                {
                    string reason;
                    if (!IsPiWebTrayInstallation(backup, out reason)) continue;
                    AppendLog(logPath, "target incomplete; recovering from " + backup);
                    if (Rollback(backup, target, new List<string>(), logPath)) return true;
                }
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "backup recovery failed: " + ex.Message);
            }
            return false;
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

        private static void OfferStartAfterFailure(string target, ApplyStartMode mode, string logPath,
            bool originalElevated, string dataDir)
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

                // The helper usually runs elevated only because the installation
                // directory is protected. Do not silently leave an elevated tray
                // running after a failed update. Use Explorer's medium token exactly
                // like Updater.Restart; custom PI_WEB_TRAY_* overrides are copied into
                // the restart command.
                bool customEnvironment = HasCustomEnvironment();
                if (!originalElevated && Elevation.IsElevated())
                {
                    try
                    {
                        bool started = false;
                        if (args.Length == 0 && !customEnvironment)
                        {
                            ProcessStartInfo viaExplorer = new ProcessStartInfo();
                            viaExplorer.FileName = ExplorerExe();
                            viaExplorer.Arguments = Shell.QuoteArg(exe);
                            viaExplorer.WorkingDirectory = target;
                            viaExplorer.UseShellExecute = true;
                            using (Process tray = Process.Start(viaExplorer))
                            {
                                started = tray != null;
                            }
                        }
                        else
                        {
                            started = LaunchViaExplorerCommand(target, args, logPath) != null;
                        }

                        if (started)
                        {
                            // Explorer can accept the request and still fail to run the
                            // script. Observe the state file before trusting it; otherwise
                            // a failed update would leave no tray at all.
                            if (WaitForNewTrayPid(dataDir, 0, 5000, exe) > 0)
                            {
                                AppendLog(logPath, "started after failure via explorer: " + exe + " " + args);
                                return;
                            }
                            AppendLog(logPath, "explorer start after failure was not observed; falling back");
                        }
                        else
                        {
                            AppendLog(logPath, "explorer start after failure was not started; falling back");
                        }
                    }
                    catch (Exception explorerError)
                    {
                        // If Explorer is unavailable or refuses the launch, still make
                        // one direct attempt so a failed update cannot strand the user.
                        AppendLog(logPath, "explorer start after failure failed: " + explorerError);
                    }
                }

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
            int port;
            return TryParseApplyArgs(args, out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash,
                out port);
        }

        internal static bool TryParseApplyArgs(string[] args, out string staging, out string target,
            out int parentPid, out ApplyStartMode mode, out bool originalElevated,
            out string logPath, out string language, out string dataDir, out string stagingHash,
            out int port)
        {
            string zipPath;
            string zipChecksum;
            return TryParseApplyArgs(args, out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash,
                out port, out zipPath, out zipChecksum);
        }

        internal static bool TryParseApplyArgs(string[] args, out string staging, out string target,
            out int parentPid, out ApplyStartMode mode, out bool originalElevated,
            out string logPath, out string language, out string dataDir, out string stagingHash,
            out int port, out string zipPath, out string zipChecksum)
        {
            string gateEventName;
            return TryParseApplyArgs(args, out staging, out target, out parentPid, out mode,
                out originalElevated, out logPath, out language, out dataDir, out stagingHash,
                out port, out zipPath, out zipChecksum, out gateEventName);
        }

        internal static bool TryParseApplyArgs(string[] args, out string staging, out string target,
            out int parentPid, out ApplyStartMode mode, out bool originalElevated,
            out string logPath, out string language, out string dataDir, out string stagingHash,
            out int port, out string zipPath, out string zipChecksum, out string gateEventName)
        {
            staging = "";
            target = "";
            logPath = "";
            language = "";
            dataDir = "";
            stagingHash = "";
            zipPath = "";
            zipChecksum = "";
            gateEventName = "";
            port = 0;
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
            if (index < 0) return false;

            // The staging hash is mandatory. The port was added after the hash, the
            // verified zip path/checksum after the port, and the optional exit gate
            // after those. Older helper calls remain valid for compatibility.
            int tail = args.Length - index - 1;
            bool hasPort = tail == 10 || tail == 11 || tail == 12 || tail == 13;
            bool hasZip = tail == 12 || tail == 13;
            bool hasGate = tail == 11 || tail == 13;
            if (tail != 9 && tail != 10 && tail != 11 && tail != 12 && tail != 13)
                return false;

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
            stagingHash = args[index + 9];
            if (stagingHash.Length != 64 || !IsHexHash(stagingHash)) return false;
            if (hasPort
                && (!int.TryParse(args[index + 10], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out port)
                    || port < 0 || port > 65535))
            {
                return false;
            }
            if (hasZip)
            {
                zipPath = args[index + 11];
                zipChecksum = args[index + 12];
                if (zipPath.Length == 0 || zipChecksum.Length != 64
                    || !IsHexHash(zipChecksum))
                {
                    return false;
                }
            }
            if (hasGate)
            {
                gateEventName = args[index + (hasZip ? 13 : 11)];
                if (gateEventName.Length == 0) return false;
            }

            // The helper is elevated. Keep its log and staging inside the installation's
            // data/target roots so forged arguments cannot turn AppendLog into an
            // arbitrary elevated file writer.
            staging = SafeFullPath(staging);
            target = SafeFullPath(target);
            logPath = SafeFullPath(logPath);
            dataDir = SafeFullPath(dataDir);
            if (staging.Length == 0 || target.Length == 0 || dataDir.Length == 0
                || logPath.Length == 0)
            {
                return false;
            }

            // The helper writes the log before it can trust most of the payload. Do not
            // let a forged argument turn that into an arbitrary file writer: only the
            // fixed update.log name is accepted, and never in a system directory.
            if (!string.Equals(Path.GetFileName(logPath), "update.log",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (Config.IsSystemDirectory(dataDir)) return false;
            if (!IsPathUnder(logPath, dataDir) && !IsPathUnder(logPath, target))
                return false;

            // Staging must live under the data directory's update folder, which is the
            // layout Prepare always creates. Reject arbitrary staging roots and
            // reparse-point paths that could redirect an elevated write.
            string updateRoot = SafeFullPath(Path.Combine(dataDir, "update"));
            if (!IsPathUnder(staging, updateRoot)) return false;
            if (gateEventName.Length > 0)
            {
                gateEventName = SafeFullPath(gateEventName);
                if (gateEventName.Length == 0 || !IsPathUnder(gateEventName, updateRoot))
                    return false;
                if (PathSafety.ContainsReparsePoint(gateEventName)) return false;
            }
            if (zipPath.Length > 0)
            {
                zipPath = SafeFullPath(zipPath);
                if (zipPath.Length == 0 || !File.Exists(zipPath)) return false;
                if (!IsPathUnder(zipPath, updateRoot)) return false;
                if (PathSafety.ContainsReparsePoint(zipPath)) return false;
            }
            if (PathSafety.ContainsReparsePoint(dataDir)
                || PathSafety.ContainsReparsePoint(logPath)
                || PathSafety.ContainsReparsePoint(staging))
            {
                return false;
            }
            return true;
        }

        private static bool IsHexHash(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        private static bool IsPathUnder(string path, string root)
        {
            try
            {
                string fullPath = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool WaitForParent(int pid, int timeoutMs, string expectedExe)
        {
            if (pid <= 0) return true;

            try
            {
                // Open one handle to the original process. Polling by PID is wrong:
                // if the tray exits and Windows reuses its PID, a later lookup can
                // wait for an unrelated process and abort a valid update.
                using (Process parent = Process.GetProcessById(pid))
                {
                    string image = NativeProcess.ImagePath(pid);
                    if (!string.IsNullOrEmpty(expectedExe) && !string.IsNullOrEmpty(image)
                        && !string.Equals(Path.GetFullPath(image),
                            Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    return parent.WaitForExit(timeoutMs);
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
