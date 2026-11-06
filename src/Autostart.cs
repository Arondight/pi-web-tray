// Pi Web Tray — start at login
//
// Two mutually exclusive modes: normal permissions write HKCU\...\Run; admin rights use
// an on-logon scheduled task. Only one may exist, or login starts two trays.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PiWebTray
{
    internal static class Autostart
    {
        internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
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
        }

        internal static bool ClearRunValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return !RunValueExists();
                    key.DeleteValue(RunValueName, false);
                }
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
                string directory = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return true;
                if (HasWriteAccess(directory, true)) return true;
                if (File.Exists(full) && HasWriteAccess(full, false)) return true;
                return false;
            }
            catch (Exception)
            {
                // If the ACL cannot be evaluated, fail closed for this security check.
                return true;
            }
        }

        // The account (not merely the current filtered token) must be a member of the
        // Administrators group. Otherwise over-the-shoulder UAC would create a task for
        // the entered administrator account, not for the user who enabled autostart.
        internal static bool IsAdministratorAccount()
        {
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

        private static bool HasWriteAccess(string path, bool directory)
        {
            FileSystemSecurity security = directory
                ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : (FileSystemSecurity)new FileInfo(path).GetAccessControl(AccessControlSections.Access);

            List<SecurityIdentifier> identities = new List<SecurityIdentifier>();
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null) return true;
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

            const FileSystemRights writeMask = FileSystemRights.WriteData
                | FileSystemRights.AppendData
                | FileSystemRights.WriteExtendedAttributes
                | FileSystemRights.Delete
                | FileSystemRights.DeleteSubdirectoriesAndFiles
                | FileSystemRights.ChangePermissions
                | FileSystemRights.TakeOwnership;

            bool allow = false;
            bool deny = false;
            AuthorizationRuleCollection rules = security.GetAccessRules(
                true, true, typeof(SecurityIdentifier));
            if (rules == null || rules.Count == 0) return true;

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
                if ((rule.FileSystemRights & writeMask) == 0) continue;

                if (rule.AccessControlType == AccessControlType.Deny) deny = true;
                else allow = true;
            }

            return allow && !deny;
        }

        internal static bool CreateTask(bool elevated, string executablePath)
        {
            // Remove the legacy machine-wide task first so login cannot start both.
            // Named instances must leave the default installation's legacy task alone.
            if (Instance.Suffix.Length == 0
                && QueryTask(LegacyTaskName) && !DeleteTaskByName(elevated, LegacyTaskName)) return false;

            string taskCommand = "\\\"" + executablePath + "\\\" --autostart";
            string args = "/create /f /tn " + Shell.Quote(TaskName)
                + " /sc onlogon /rl highest /tr " + Shell.Quote(taskCommand);

            if (!Launch(elevated, args)) return false;

            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(250);
                if (QueryTask()) return true;
            }
            return QueryTask();
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
