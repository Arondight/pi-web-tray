// Pi Web Tray — start at login
//
// Two mutually exclusive modes: normal permissions write HKCU\...\Run; admin rights use
// an on-logon scheduled task. Only one may exist, or login starts two trays.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace PiWebTray
{
    internal static class Autostart
    {
        internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string StartupApprovedRunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        internal const string BaseRunValueName = "PiWebTray";
        internal const string LegacyTaskName = "PiWebTray";

        // Named instances must not read, overwrite or delete the default installation's
        // start-at-login entries. Keep each instance's Run value and task names scoped by
        // the same suffix as its mutex and data directory.
        internal static string ScopedName(string baseName, string suffix)
        {
            return baseName + (suffix ?? "");
        }

        internal static string RunValueName
        {
            get { return ScopedName(BaseRunValueName, Instance.Suffix); }
        }

        // A machine-wide task name lets two users overwrite each other's admin autostart.
        // Keep one task per user and per named instance; the legacy "PiWebTray" task is
        // removed by the default instance when possible.
        private static string _taskName;
        internal static string TaskName
        {
            get
            {
                if (_taskName == null)
                    _taskName = ScopedName("PiWebTray-" + CurrentUserKey(), Instance.Suffix);
                return _taskName;
            }
        }

        private static string CurrentUserKey()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity.User != null)
                    {
                        string sid = identity.User.Value;
                        if (sid.Length > 0) return sid;
                    }
                }
            }
            catch (Exception)
            {
            }

            StringBuilder sb = new StringBuilder();
            foreach (char c in Environment.UserName ?? "user")
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            }
            return sb.Length == 0 ? "user" : sb.ToString();
        }

        // The principal/trigger UserId must be a SID or account name. The sanitized
        // name used for the task name is not a valid account name when it contains
        // non-ASCII characters, so keep the real SID (or a DOMAIN\user fallback).
        private static string CurrentUserPrincipal()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity.User != null)
                    {
                        string sid = identity.User.Value;
                        if (!string.IsNullOrEmpty(sid)) return sid;
                    }
                }
            }
            catch (Exception)
            {
            }

            string user = Environment.UserName ?? "";
            if (user.Length == 0) return "";
            string domain = Environment.UserDomainName ?? "";
            return domain.Length == 0 ? user : domain + "\\" + user;
        }

        internal static string Describe(bool task, bool run)
        {
            if (task) return L.T("auto.onAdmin");
            if (run) return L.T("auto.onUser");
            return L.T("auto.off");
        }

        internal static bool RunValueExists()
        {
            return RunValue().Length > 0;
        }

        internal static string RunValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return "";
                    return Convert.ToString(key.GetValue(RunValueName)) ?? "";
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        // If the executable was moved, the old Run value points at a missing file.
        // Repair it silently for the normal (non-admin) autostart mode.
        internal static void RepairRunValue(string executablePath)
        {
            // Each instance has its own scoped Run value, so repairing a stale target here
            // cannot redirect the default installation's autostart.
            try
            {
                string value = RunValue();
                if (value.Length == 0) return;
                if (value.IndexOf(Shell.Quote(executablePath), StringComparison.OrdinalIgnoreCase) >= 0) return;

                string oldTarget = RunValueTarget(value);
                if (string.IsNullOrEmpty(oldTarget)) return;
                // Never redirect a valid existing installation. Repair only the stale
                // case the comment describes: the old target file no longer exists.
                if (File.Exists(oldTarget)) return;

                SetRunValue(executablePath);
            }
            catch (Exception)
            {
            }
        }

        internal static bool RunValueTargetsExecutable(string executablePath)
        {
            string target = RunValueTarget(RunValue());
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(executablePath)) return false;
            try
            {
                target = Environment.ExpandEnvironmentVariables(target);
                return string.Equals(Path.GetFullPath(target), Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Task Manager can disable a Run entry without deleting it. The binary
        // StartupApproved value starts with 0x03 for the common disabled state;
        // no value means "never changed" and is treated as enabled.
        internal static bool RunValueApproved()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    StartupApprovedRunKeyPath, false))
                {
                    if (key == null) return true;
                    byte[] data = key.GetValue(RunValueName) as byte[];
                    if (data == null || data.Length == 0) return true;
                    return data[0] != 0x03;
                }
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void ClearRunApproval()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    StartupApprovedRunKeyPath, true))
                {
                    if (key != null) key.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception)
            {
            }
        }

        internal static string RunValueTarget(string value)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) return "";

            if (text[0] == '"')
            {
                int end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : "";
            }

            int flag = text.IndexOf(" --autostart", StringComparison.OrdinalIgnoreCase);
            if (flag > 0) return text.Substring(0, flag).Trim();

            int space = text.IndexOfAny(new char[] { ' ', '\t' });
            return space > 0 ? text.Substring(0, space).Trim() : text;
        }

        internal static bool QueryTask()
        {
            return QueryTask(TaskName);
        }

        internal static bool QueryLegacyTask()
        {
            return Instance.Suffix.Length == 0 && QueryTask(LegacyTaskName);
        }

        // A disabled or relocated legacy task must not suppress the current Run
        // value. Existence alone is not enough for the menu/startup decision.
        internal static bool QueryLegacyTaskState(out bool enabled, out bool matchesExecutable)
        {
            enabled = false;
            matchesExecutable = false;
            if (Instance.Suffix.Length != 0) return false;
            return QueryTaskState(LegacyTaskName, out enabled, out matchesExecutable);
        }

        // Existence plus the state that the menu needs: a task can exist but be
        // disabled or point at an older installation. QueryTask itself remains
        // existence-only because delete/replace operations must still find it.
        internal static bool QueryTaskState(out bool enabled, out bool matchesExecutable)
        {
            return QueryTaskState(TaskName, out enabled, out matchesExecutable);
        }

        private static bool QueryTaskState(string name, out bool enabled,
            out bool matchesExecutable)
        {
            enabled = false;
            // Do not claim an unknown task belongs to this installation. A false
            // positive can make PollTaskRefresh remove a working Run value.
            matchesExecutable = false;
            try
            {
                ProcessResult run = ProcessRunner.Run(
                    Sys.SystemExecutable("schtasks.exe"),
                    "/query /tn " + Shell.Quote(name) + " /xml",
                    Sys.SystemDirectory, 5000, null, null);
                if (!run.Started || run.TimedOut || run.ExitCode != 0) return false;

                string xml = run.Output.TrimStart('\uFEFF');
                int xmlStart = xml.IndexOf('<');
                if (xmlStart > 0) xml = xml.Substring(xmlStart);
                XmlDocument document = new XmlDocument();
                document.LoadXml(xml);

                enabled = true;
                XmlNodeList enabledNodes = document.GetElementsByTagName("Enabled", "*");
                if (enabledNodes.Count > 0 && enabledNodes[0] != null
                    && string.Equals(enabledNodes[0].InnerText.Trim(), "false",
                        StringComparison.OrdinalIgnoreCase))
                {
                    enabled = false;
                }

                XmlNodeList commandNodes = document.GetElementsByTagName("Command", "*");
                if (commandNodes.Count > 0 && commandNodes[0] != null)
                {
                    string command = commandNodes[0].InnerText.Trim().Trim('"');
                    if (command.Length > 0)
                    {
                        try
                        {
                            matchesExecutable = string.Equals(
                                Path.GetFullPath(Environment.ExpandEnvironmentVariables(command)),
                                Path.GetFullPath(Application.ExecutablePath),
                                StringComparison.OrdinalIgnoreCase);
                        }
                        catch (Exception)
                        {
                            matchesExecutable = false;
                        }
                    }
                }
                return true;
            }
            catch (Exception)
            {
                // XML output can be unavailable on unusual systems. Fall back to
                // the old existence check, but do not claim that the task points at
                // this executable: treating another installation's task as enabled
                // would allow PollTaskRefresh to remove the current Run value.
                bool exists = QueryTask(name);
                enabled = exists;
                matchesExecutable = false;
                return exists;
            }
        }

        private static bool QueryTask(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Sys.SystemExecutable("schtasks.exe");
                psi.Arguments = "/query /tn " + Shell.Quote(name);
                psi.WorkingDirectory = Sys.SystemDirectory;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit(5000);
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void SetRunValue(string executablePath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) throw new InvalidOperationException("cannot open the Run registry key");
                key.SetValue(RunValueName, Shell.Quote(executablePath) + " --autostart");
            }
            // A user who disabled the entry in Task Manager gets a working entry
            // again when the tray deliberately enables/repairs it.
            ClearRunApproval();
        }

        internal static bool ClearRunValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null) key.DeleteValue(RunValueName, false);
                }
                ClearRunApproval();
                return !RunValueExists();
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Administrator start-at-login runs the executable with a full token and no
        // UAC prompt. Refuse it when a standard user could replace that executable:
        // otherwise any process running as the same user could turn a normal file into
        // an elevated logon task.
        internal static bool IsUserWritableInstall(string executablePath)
        {
            try
            {
                string full = Path.GetFullPath(executablePath);
                if (PathSafety.ContainsReparsePoint(full)) return true;

                string directory = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return true;

                DirectorySecurity directorySecurity = new DirectoryInfo(directory)
                    .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                if (IsOwnedByStandardUser(directorySecurity)) return true;
                if (HasWriteAccess(directorySecurity)) return true;

                // A protected leaf directory is still replaceable when a standard user
                // can rename or delete one of its ancestors, or delete children there.
                if (HasUnsafeAncestor(directory)) return true;

                if (File.Exists(full))
                {
                    FileSecurity fileSecurity = new FileInfo(full)
                        .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                    if (IsOwnedByStandardUser(fileSecurity)) return true;
                    if (HasWriteAccess(fileSecurity)) return true;
                }
                return false;
            }
            catch (Exception)
            {
                // If the ACL cannot be evaluated, fail closed for this security check.
                return true;
            }
        }

        // Repair the installation directory so the existing administrator logon task
        // cannot be hijacked by a standard user. This needs one UAC prompt unless the
        // tray is already elevated. icacls exists on Windows 7 through 11.
        internal static bool SecureInstallDirectory(string executablePath, out string message)
        {
            message = "";
            string directory;
            try
            {
                string full = Path.GetFullPath(executablePath);
                directory = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    message = L.T("auto.repairFailed", L.T("auto.repairNoDirectory"));
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = L.T("auto.repairFailed", ex.Message);
                return false;
            }

            if (!IsUserWritableInstall(executablePath))
            {
                message = L.T("auto.repairAlreadySafe");
                return true;
            }

            // Never rewrite the ACL of a drive root; install there is not supported.
            string pathRoot = Path.GetPathRoot(directory);
            if (!string.IsNullOrEmpty(pathRoot)
                && string.Equals(
                    directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                message = L.T("auto.repairParentUnsafe");
                return false;
            }

            // Repairing the leaf ACL cannot protect a path whose parent can be
            // renamed/replaced. Do not change the user's files in that case; the
            // administrator task would still be hijackable.
            if (PathSafety.ContainsReparsePoint(executablePath) || HasUnsafeAncestor(directory))
            {
                message = L.T("auto.repairParentUnsafe");
                return false;
            }

            int exitCode;
            string command = BuildSecureDirectoryCommand(directory);
            if (!RunRepairCommand(command, out exitCode))
            {
                if (exitCode == -2)
                    message = L.T("auto.repairCancelled");
                else if (exitCode == -3)
                    message = L.T("auto.repairTimeout");
                else
                    message = L.T("auto.repairFailed", L.T("auto.repairLaunchFailed"));
                return false;
            }
            if (exitCode != 0)
            {
                message = L.T("auto.repairFailed", L.T("auto.repairCommandFailed", exitCode));
                return false;
            }

            // ACL changes propagate asynchronously. Re-run the same check the tray uses
            // before reporting success, instead of trusting the tool exit code alone.
            for (int i = 0; i < 20; i++)
            {
                if (!IsUserWritableInstall(executablePath))
                {
                    message = L.T("auto.repairSuccess");
                    return true;
                }
                Thread.Sleep(250);
            }

            message = L.T("auto.repairFailed", L.T("auto.repairStillUnsafe"));
            return false;
        }

        internal static string BuildSecureDirectoryCommand(string directory)
        {
            string icacls = Shell.Quote(Sys.SystemExecutable("icacls.exe"));
            // These are Microsoft well-known SIDs, not machine-specific values:
            // S-1-5-18 = LOCAL SYSTEM, S-1-5-32-544 = BUILTIN\Administrators,
            // S-1-5-32-545 = BUILTIN\Users. Their display names change with the
            // Windows language, but the SIDs do not. Resolve them through .NET
            // instead of embedding the numbers in the command.
            string systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
            string administratorsSid = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid, null).Value;
            string usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;

            StringBuilder command = new StringBuilder();
            command.Append(icacls);
            command.Append(" ").Append(Shell.Quote(directory));
            command.Append(" /setowner \"*").Append(administratorsSid)
                .Append("\" /t /l && ");
            command.Append(icacls);
            command.Append(" ").Append(Shell.Quote(directory));
            // Reset the root first: an explicit standard-user ACE from an older
            // installation is not removed by /inheritance:r.
            command.Append(" /reset && ");
            command.Append(icacls);
            command.Append(" ").Append(Shell.Quote(directory));
            command.Append(" /inheritance:r /grant:r");
            command.Append(" \"*").Append(systemSid).Append(":(OI)(CI)F\"");
            command.Append(" \"*").Append(administratorsSid).Append(":(OI)(CI)F\"");
            command.Append(" \"*").Append(usersSid).Append(":(OI)(CI)RX\"");
            command.Append(" && ");
            command.Append(icacls);
            command.Append(" ").Append(Shell.Quote(directory + "\\*"));
            command.Append(" /reset /t /l");
            return command.ToString();
        }

        private static bool RunRepairCommand(string command, out int exitCode)
        {
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Sys.SystemExecutable("cmd.exe");
                psi.Arguments = Shell.CmdLine(command);
                psi.WorkingDirectory = Sys.SystemDirectory;
                if (Elevation.IsElevated())
                {
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    ProcessRunner.HardenEnvironment(psi, true);
                }
                else
                {
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }

                using (Process process = Process.Start(psi))
                {
                    if (process == null) return false;
                    if (!process.WaitForExit(120000))
                    {
                        try { Sys.KillTree(process.Id); }
                        catch (Exception)
                        {
                            try { process.Kill(); } catch (Exception) { }
                        }
                        try { process.WaitForExit(5000); } catch (Exception) { }
                        exitCode = -3;
                        return false;
                    }
                    exitCode = process.ExitCode;
                    return true;
                }
            }
            catch (Win32Exception ex)
            {
                // ERROR_CANCELLED: the user declined the UAC prompt.
                exitCode = ex.NativeErrorCode == 1223 ? -2 : -1;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsOwnedByStandardUser(FileSystemSecurity security)
        {
            try
            {
                SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier))
                    as SecurityIdentifier;
                if (owner == null) return true;

                SecurityIdentifier administrators = new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid, null);
                // Administrators ownership is safe only when the path's ancestors are
                // protected too; the caller checks those separately.
                if (owner.Equals(administrators)) return false;

                // These owners are writable by standard users even if the token
                // groups below do not explicitly list them.
                SecurityIdentifier users = new SecurityIdentifier(
                    WellKnownSidType.BuiltinUsersSid, null);
                SecurityIdentifier authenticated = new SecurityIdentifier(
                    WellKnownSidType.AuthenticatedUserSid, null);
                SecurityIdentifier world = new SecurityIdentifier(
                    WellKnownSidType.WorldSid, null);
                if (owner.Equals(users) || owner.Equals(authenticated) || owner.Equals(world))
                    return true;

                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity.User != null && owner.Equals(identity.User)) return true;
                    if (identity.Groups != null)
                    {
                        foreach (IdentityReference group in identity.Groups)
                        {
                            SecurityIdentifier sid = group as SecurityIdentifier;
                            if (sid == null || sid.Equals(administrators)) continue;
                            if (owner.Equals(sid)) return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                return true;
            }
            return false;
        }

        // The account (not merely the current filtered token) must be a member of the
        // Administrators group. Otherwise over-the-shoulder UAC would create a task for
        // the entered administrator account, not for the user who enabled autostart.
        internal static bool IsAdministratorAccount()
        {
            // A UAC-filtered administrator token does not expose BUILTIN\Administrators
            // through WindowsIdentity.Groups. Ask for the elevation type first so a
            // normal tray can still enable the administrator task after the UAC prompt.
            if (Elevation.IsAdministratorAccount()) return true;

            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity.Groups == null) return false;
                    SecurityIdentifier administrators = new SecurityIdentifier(
                        WellKnownSidType.BuiltinAdministratorsSid, null);
                    foreach (IdentityReference group in identity.Groups)
                    {
                        SecurityIdentifier sid = group as SecurityIdentifier;
                        if (sid != null && sid.Equals(administrators)) return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private const FileSystemRights WriteRights = FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;

        // Rights that let a standard user rename/delete an ancestor directory or
        // change its permissions. AppendData/WriteData alone only create new entries;
        // they do not replace an existing protected child.
        private const FileSystemRights DangerousAncestorRights = FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;

        private static bool HasWriteAccess(FileSystemSecurity security)
        {
            return HasAccess(security, WriteRights);
        }

        private static bool HasUnsafeAncestor(string directory)
        {
            try
            {
                string current = Path.GetFullPath(directory);
                while (!string.IsNullOrEmpty(current))
                {
                    string parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent)
                        || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    DirectoryInfo info = new DirectoryInfo(parent);
                    if (!info.Exists) return true;
                    DirectorySecurity security = info.GetAccessControl(
                        AccessControlSections.Access | AccessControlSections.Owner);

                    if (IsOwnedByStandardUser(security)) return true;
                    if (HasAccess(security, DangerousAncestorRights)) return true;
                    // This right on the parent allows deleting or renaming its child.
                    if (HasAccess(security, FileSystemRights.DeleteSubdirectoriesAndFiles)) return true;

                    current = parent;
                }
            }
            catch (Exception)
            {
                return true;
            }
            return false;
        }

        private static bool HasAccess(FileSystemSecurity security, FileSystemRights mask)
        {
            List<SecurityIdentifier> identities = CurrentStandardIdentities();
            if (identities == null) return true;

            AuthorizationRuleCollection rules = security.GetAccessRules(
                true, true, typeof(SecurityIdentifier));
            if (rules == null || rules.Count == 0) return true;

            bool allow = false;
            bool deny = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                SecurityIdentifier sid = rule.IdentityReference as SecurityIdentifier;
                if (sid == null) continue;
                bool interested = false;
                foreach (SecurityIdentifier identity in identities)
                {
                    if (sid.Equals(identity)) { interested = true; break; }
                }
                if (!interested) continue;
                // Inherit-only ACEs apply to children, not to this object. Ignoring
                // this flag would treat C:\'s child-only Authenticated Users ACE as
                // write access to C:\ itself and reject Program Files.
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if ((rule.FileSystemRights & mask) == 0) continue;

                if (rule.AccessControlType == AccessControlType.Deny) deny = true;
                else allow = true;
            }

            return allow && !deny;
        }

        private static List<SecurityIdentifier> CurrentStandardIdentities()
        {
            try
            {
                List<SecurityIdentifier> identities = new List<SecurityIdentifier>();
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity.User == null) return null;
                    identities.Add(identity.User);
                    SecurityIdentifier administrators = new SecurityIdentifier(
                        WellKnownSidType.BuiltinAdministratorsSid, null);
                    if (identity.Groups != null)
                    {
                        foreach (IdentityReference group in identity.Groups)
                        {
                            SecurityIdentifier sid = group as SecurityIdentifier;
                            if (sid == null || sid.Equals(administrators)) continue;
                            identities.Add(sid);
                        }
                    }
                }
                identities.Add(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
                identities.Add(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
                identities.Add(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
                return identities;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool CreateTask(bool elevated, string executablePath)
        {
            // Remove the legacy machine-wide task first so login cannot start both.
            // Named instances must leave the default installation's legacy task alone.
            if (Instance.Suffix.Length == 0
                && QueryTask(LegacyTaskName) && !DeleteTaskByName(elevated, LegacyTaskName)) return false;

            string principal = CurrentUserPrincipal();
            if (string.IsNullOrEmpty(principal)) return false;

            // schtasks /create /sc onlogon creates an "any user" trigger and leaves
            // the default battery policy in place (do not start on batteries, stop
            // when switching to batteries). Build a user-scoped XML definition
            // instead. Keep the file open with FileShare.Read while schtasks reads
            // it, so another process cannot replace the elevated task definition.
            string xmlPath = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-task-" + Guid.NewGuid().ToString("N") + ".xml");
            FileStream write = null;
            FileStream guard = null;
            try
            {
                byte[] xml = BuildTaskXml(executablePath, principal);
                write = new FileStream(xmlPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read);
                write.Write(xml, 0, xml.Length);
                write.Flush();
                // schtasks opens the XML for read with a share mode that does not
                // permit our write handle. Close the writer and hold a read-only
                // handle instead: schtasks can read it, but another process still
                // cannot replace or modify the elevated task definition while it is
                // being registered.
                write.Dispose();
                write = null;
                guard = new FileStream(xmlPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read);

                string args = "/create /f /tn " + Shell.Quote(TaskName)
                    + " /xml " + Shell.Quote(xmlPath);
                if (!Launch(elevated, args)) return false;

                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(250);
                    if (QueryTask()) return true;
                }
                return QueryTask();
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (write != null)
                {
                    try { write.Dispose(); } catch (Exception) { }
                }
                if (guard != null)
                {
                    try { guard.Dispose(); } catch (Exception) { }
                }
                try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch (Exception) { }
            }
        }

        internal static byte[] BuildTaskXml(string executablePath, string principal)
        {
            // The Task Scheduler accepts UTF-16LE with a BOM; schtasks rejects an
            // encoding declaration that does not match the file bytes.
            using (MemoryStream memory = new MemoryStream())
            {
                XmlWriterSettings settings = new XmlWriterSettings();
                settings.Encoding = new UnicodeEncoding(false, true);
                settings.Indent = true;
                settings.OmitXmlDeclaration = false;
                using (XmlWriter writer = XmlWriter.Create(memory, settings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("Task",
                        "http://schemas.microsoft.com/windows/2004/02/mit/task");
                    writer.WriteAttributeString("version", "1.2");

                    // A user-scoped logon trigger. Without <UserId> the task fires
                    // for every account that logs on, and the administrator service
                    // can start in response to another user's logon.
                    writer.WriteStartElement("Triggers");
                    writer.WriteStartElement("LogonTrigger");
                    writer.WriteElementString("Enabled", "true");
                    writer.WriteElementString("UserId", principal);
                    writer.WriteEndElement();
                    writer.WriteEndElement();

                    writer.WriteStartElement("Principals");
                    writer.WriteStartElement("Principal");
                    writer.WriteAttributeString("id", "Author");
                    writer.WriteElementString("UserId", principal);
                    writer.WriteElementString("LogonType", "InteractiveToken");
                    writer.WriteElementString("RunLevel", "HighestAvailable");
                    writer.WriteEndElement();
                    writer.WriteEndElement();

                    // Do not inherit schtasks' defaults: a laptop on battery must
                    // still start the tray, and unplugging it must not stop it.
                    writer.WriteStartElement("Settings");
                    writer.WriteElementString("MultipleInstancesPolicy", "IgnoreNew");
                    writer.WriteElementString("DisallowStartIfOnBatteries", "false");
                    writer.WriteElementString("StopIfGoingOnBatteries", "false");
                    writer.WriteElementString("AllowHardTerminate", "true");
                    writer.WriteElementString("StartWhenAvailable", "false");
                    writer.WriteElementString("RunOnlyIfNetworkAvailable", "false");
                    writer.WriteStartElement("IdleSettings");
                    writer.WriteElementString("StopOnIdleEnd", "false");
                    writer.WriteElementString("RestartOnIdle", "false");
                    writer.WriteEndElement();
                    writer.WriteElementString("AllowStartOnDemand", "true");
                    writer.WriteElementString("Enabled", "true");
                    writer.WriteElementString("Hidden", "false");
                    writer.WriteElementString("RunOnlyIfIdle", "false");
                    writer.WriteElementString("WakeToRun", "false");
                    writer.WriteElementString("ExecutionTimeLimit", "PT0S");
                    writer.WriteElementString("Priority", "7");
                    writer.WriteEndElement();

                    writer.WriteStartElement("Actions");
                    writer.WriteAttributeString("Context", "Author");
                    writer.WriteStartElement("Exec");
                    writer.WriteElementString("Command", executablePath);
                    writer.WriteElementString("Arguments", "--autostart");
                    writer.WriteEndElement();
                    writer.WriteEndElement();

                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                }
                return memory.ToArray();
            }
        }

        internal static bool DeleteTask(bool elevated)
        {
            bool current = QueryTask();
            bool legacy = Instance.Suffix.Length == 0 && QueryTask(LegacyTaskName);
            if (!current && !legacy) return true;

            if (current && !DeleteTaskByName(elevated, TaskName)) return false;
            if (legacy && !DeleteTaskByName(elevated, LegacyTaskName)) return false;
            return true;
        }

        private static bool DeleteTaskByName(bool elevated, string name)
        {
            if (!QueryTask(name)) return true;
            if (!Launch(elevated, "/delete /f /tn " + Shell.Quote(name))) return false;

            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(200);
                if (!QueryTask(name)) return true;
            }
            return !QueryTask(name);
        }

        // Run directly when already elevated; otherwise go through runas (one UAC prompt)
        private static bool Launch(bool elevated, string args)
        {
            try
            {
                if (elevated)
                {
                    Process p = Sys.StartHidden(Sys.SystemExecutable("schtasks.exe"), args,
                        Sys.SystemDirectory);
                    if (p == null) return false;
                    p.Dispose();
                    return true;
                }
                return Sys.RunElevated(Sys.SystemExecutable("schtasks.exe"), args, true,
                    Sys.SystemDirectory);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Returns true on success; message is shown to the user either way
        internal static bool Apply(bool enable, bool admin, bool elevated, string executablePath, out string message)
        {
            if (!enable)
            {
                // Remove the scheduled task first: if UAC is cancelled, the existing Run value
                // must stay so the menu still reports the real state.
                if (!DeleteTask(elevated))
                {
                    message = L.T("auto.taskDeleteFailed");
                    return false;
                }
                if (!ClearRunValue())
                {
                    message = L.T("auto.runKeyFailed");
                    return false;
                }
                message = L.T("auto.disabled");
                return true;
            }

            if (admin)
            {
                if (IsUserWritableInstall(executablePath))
                {
                    message = L.T("auto.adminWritableDir");
                    return false;
                }
                if (!IsAdministratorAccount())
                {
                    message = L.T("auto.adminAccountRequired");
                    return false;
                }

                // Create the task before removing the old Run value. If UAC is cancelled, the
                // previous normal-permission autostart must keep working instead of vanishing.
                if (CreateTask(elevated, executablePath))
                {
                    if (!ClearRunValue())
                    {
                        message = L.T("auto.enabledAdminRunLeft");
                        return true;
                    }
                    message = L.T("auto.enabledAdmin");
                    return true;
                }
                message = L.T("auto.taskFailed");
                return false;
            }

            // Remove the scheduled task first, or both modes fire at login and start two trays
            if (!DeleteTask(elevated))
            {
                message = L.T("auto.oldTaskFailed");
                return false;
            }
            try
            {
                SetRunValue(executablePath);
            }
            catch (Exception)
            {
                message = L.T("auto.runKeyFailed");
                return false;
            }
            message = L.T("auto.enabledUser");
            return true;
        }
    }
}
