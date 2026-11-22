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

        // Principals that may own or control installation files. Any other
        // principal with write/delete/ACL rights is treated as untrusted, even
        // when it is not the current user. An administrator task must not be
        // hijackable by another local standard user with an explicit ACE.
        private const string TrustedInstallerSidValue =
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        private static readonly SecurityIdentifier LocalSystemSid =
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier AdministratorsSid =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier TrustedInstallerSid =
            new SecurityIdentifier(TrustedInstallerSidValue);

        // The ACL repair form runs outside TrayApp's normal autostart worker. This
        // flag lets the tray keep Exit/Stop/Restart disabled while icacls/UAC runs,
        // even when the modal form itself is not the active window.
        private static volatile bool _repairRunning;

        internal static bool RepairRunning
        {
            get { return _repairRunning; }
        }

        internal static void BeginRepair()
        {
            _repairRunning = true;
        }

        internal static void EndRepair()
        {
            _repairRunning = false;
        }

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

        internal static string CurrentUserKey()
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
                // Compare the file object, not a spelling of the path. An 8.3 name
                // or a junction must count as the current executable instead of
                // being treated as another installation.
                if (RunValueTargetsExecutable(executablePath)) return;

                string oldTarget = RunValueTarget(value);
                if (string.IsNullOrEmpty(oldTarget)) return;
                // Registry values commonly store %LOCALAPPDATA% / %ProgramFiles%.
                // Resolve them before deciding that the old installation is gone.
                oldTarget = Environment.ExpandEnvironmentVariables(oldTarget);
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
                if (PathSafety.SameFile(target, executablePath)) return true;
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

        // A Run value can contain arguments. The old check looked only at the
        // path, so an entry such as "...\pi-web-tray.exe --tray-only" was shown as
        // enabled even though it never starts the service. Parse the same limited
        // argument set used by the scheduled task; an empty argument list still
        // means a normal start (legacy entries remain valid).
        internal static bool RunValueStartsService(string value)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) return false;
            string target = RunValueTarget(text);
            if (string.IsNullOrEmpty(target)) return false;
            string arguments;
            if (text[0] == '"')
            {
                int end = text.IndexOf('"', 1);
                if (end <= 1) return false;
                arguments = text.Substring(end + 1).Trim();
            }
            else
            {
                int index = text.IndexOf(target, StringComparison.OrdinalIgnoreCase);
                if (index < 0) return false;
                arguments = text.Substring(index + target.Length).Trim();
            }
            return TaskArgumentsStartService(arguments);
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
            return QueryLegacyTaskDetails(out enabled, out matchesExecutable, out _);
        }

        internal static bool QueryLegacyTaskDetails(out bool enabled,
            out bool matchesExecutable, out string commandPath)
        {
            enabled = false;
            matchesExecutable = false;
            commandPath = "";
            if (Instance.Suffix.Length != 0) return false;
            return QueryTaskDetails(LegacyTaskName, out enabled, out matchesExecutable,
                out commandPath);
        }

        // Existence plus the state that the menu needs: a task can exist but be
        // disabled, point at an older installation, or use arguments that do not
        // start the service. QueryTask itself remains existence-only because
        // delete/replace operations must still find it.
        internal static bool QueryTaskState(out bool enabled, out bool matchesExecutable)
        {
            return QueryTaskDetails(out enabled, out matchesExecutable, out _);
        }

        internal static bool QueryTaskDetails(out bool enabled,
            out bool matchesExecutable, out string commandPath)
        {
            return QueryTaskDetails(TaskName, out enabled, out matchesExecutable,
                out commandPath);
        }

        // schtasks writes its XML using the console/OEM code page even though the
        // embedded declaration may claim UTF-16. Try the encodings that Windows
        // actually uses before falling back to an existence check. The first
        // successful match wins; for an ASCII-only task this is a single process.
        private static bool QueryTaskDetails(string name, out bool enabled,
            out bool matchesExecutable, out string commandPath)
        {
            bool userMatches;
            bool xmlParsed;
            return QueryTaskDetails(name, out enabled, out matchesExecutable,
                out commandPath, out userMatches, out xmlParsed);
        }

        // Ownership is separate from the executable/argument match: DeleteTask must
        // be able to remove a stale current-user task without ever deleting another
        // user's legacy task.
        internal static bool QueryTaskUserBelongsToCurrentUser(string name, out bool parsed)
        {
            bool enabled;
            bool matches;
            string command;
            bool userMatches;
            bool explicitUser;
            bool xmlParsed;
            bool adminSettingsSafe;
            bool queried = QueryTaskDetails(name, out enabled, out matches,
                out command, out userMatches, out explicitUser, out xmlParsed,
                out adminSettingsSafe);
            parsed = queried && xmlParsed;
            // A no-user task is a machine-wide legacy task. Leave it alone: it
            // cannot be attributed to this account, and another user may rely on it.
            return parsed && userMatches && explicitUser;
        }

        private static bool QueryTaskDetails(string name, out bool enabled,
            out bool matchesExecutable, out string commandPath,
            out bool userMatches, out bool xmlParsed)
        {
            bool explicitUser;
            bool adminSettingsSafe;
            return QueryTaskDetails(name, out enabled, out matchesExecutable,
                out commandPath, out userMatches, out explicitUser, out xmlParsed,
                out adminSettingsSafe);
        }

        internal sealed class TaskXmlEvaluation
        {
            internal bool Enabled;
            internal bool UserMatches;
            internal bool ExplicitUser;
            internal string Command = "";
            internal string Arguments = "";
            internal string CommandPath = "";
            internal bool TargetsExecutable;
            internal bool MatchesExecutable;
            internal bool AdminSettingsSafe;
        }

        // A task definition is evaluated independently of where it came from:
        // schtasks' XML output, or the task file the Task Scheduler keeps.
        internal static TaskXmlEvaluation EvaluateTaskXml(string name, XmlDocument document)
        {
            if (document == null) return null;
            TaskXmlEvaluation result = new TaskXmlEvaluation();
            result.Enabled = IsTaskXmlEnabled(document);
            result.UserMatches = TaskXmlUserMatchesCurrentUser(document,
                out result.ExplicitUser);
            result.Command = ReadTaskElement(document, "Command");
            result.Arguments = ReadTaskElement(document, "Arguments");
            string expandedCommand = ExpandTaskCommand(result.Command);
            result.CommandPath = result.Command.Length > 0 ? expandedCommand : "";

            bool pathMatches = false;
            bool pathTargets = false;
            if (expandedCommand.Length > 0)
            {
                try
                {
                    // The strict match rejects a hard-link alias for the task's
                    // identity. A separate loose match is needed when deciding
                    // whether an unowned task still targets this executable for
                    // deletion/reporting purposes.
                    pathMatches = PathSafety.SameSingleLinkLocalFile(
                        expandedCommand, Application.ExecutablePath);
                    pathTargets = PathSafety.SameLocalFile(
                        expandedCommand, Application.ExecutablePath);
                }
                catch (Exception)
                {
                }
            }
            result.TargetsExecutable = pathTargets
                && TaskArgumentsStartService(result.Arguments);

            // Whether the task starts this executable is independent from whether
            // it meets the stronger current administrator-task settings. The
            // legacy any-user task must still be recognised so the tray does not
            // add a second Run value or claim a disable succeeded while the task
            // remains active. Keep the stricter match for the current task name:
            // a weak current task should still be offered for repair/recreation.
            bool legacyTaskName = string.Equals(name, LegacyTaskName,
                StringComparison.OrdinalIgnoreCase);
            // A current-user task must say which user it belongs to. A no-user
            // definition is the legacy machine-wide form and is only accepted for
            // the legacy task name. Also require exactly one Exec action: a task
            // with an extra elevated action must not be treated as our task.
            bool userScopeMatches = result.UserMatches
                && (legacyTaskName
                    || (result.ExplicitUser && HasExplicitTaskUsers(document)));
            bool startsAtLogin = pathMatches && HasSingleExecAction(document)
                && TaskArgumentsStartService(result.Arguments) && userScopeMatches;
            bool adminSafe = startsAtLogin && TaskXmlSupportsAdminAutostart(document);
            result.AdminSettingsSafe = adminSafe;
            result.MatchesExecutable = startsAtLogin
                && (legacyTaskName || adminSafe);
            return result;
        }

        internal static bool HasExplicitTaskUsers(XmlDocument document)
        {
            if (document == null) return false;
            XmlNodeList principals = document.SelectNodes(
                "//*[local-name()='Principal']/*[local-name()='UserId']");
            XmlNodeList triggers = document.SelectNodes(
                "//*[local-name()='LogonTrigger']/*[local-name()='UserId']");
            return principals != null && principals.Count == 1
                && triggers != null && triggers.Count == 1;
        }

        internal static bool HasSingleExecAction(XmlDocument document)
        {
            if (document == null) return false;
            XmlNodeList actionsNodes = document.SelectNodes(
                "/*[local-name()='Task']/*[local-name()='Actions']");
            if (actionsNodes == null || actionsNodes.Count != 1) return false;
            XmlNode actions = actionsNodes[0];
            int count = 0;
            foreach (XmlNode child in actions.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                count++;
                if (count != 1
                    || !string.Equals(child.LocalName, "Exec",
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return count == 1;
        }

        private static string TaskFileDirectory()
        {
            try
            {
                string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                return string.IsNullOrEmpty(system) ? "" : Path.Combine(system, "Tasks");
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static bool TryGetTaskFilePath(string name, out string path)
        {
            path = "";
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0
                || name.IndexOf(':') >= 0 || name == "." || name == "..")
                return false;
            string directory = TaskFileDirectory();
            if (string.IsNullOrEmpty(directory)) return false;
            path = Path.Combine(directory, name);
            return true;
        }

        // true = state known (exists false/true); false = cannot tell.
        private static bool TryGetTaskFileState(string name, out bool exists)
        {
            exists = false;
            string path;
            if (!TryGetTaskFilePath(name, out path)) return false;
            try
            {
                if (File.Exists(path))
                {
                    exists = true;
                    return true;
                }
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    // A directory with the task name is a task folder, not a task.
                    exists = (attributes & FileAttributes.Directory) == 0;
                    return true;
                }
                catch (FileNotFoundException)
                {
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
                catch (Exception)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryLoadTaskFileXml(string name, out XmlDocument document)
        {
            document = null;
            string path;
            if (!TryGetTaskFilePath(name, out path)) return false;
            try
            {
                if (!File.Exists(path)) return false;
                if (PathSafety.ContainsReparsePoint(path)) return false;
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > 1024 * 1024)
                    return false;
                using (FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    XmlDocument loaded = new XmlDocument();
                    // Task definitions are local files; do not resolve external
                    // entities from a file that an administrator could modify.
                    loaded.XmlResolver = null;
                    loaded.Load(stream);
                    document = loaded;
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool QueryTaskDetails(string name, out bool enabled,
            out bool matchesExecutable, out string commandPath,
            out bool userMatches, out bool explicitUser, out bool xmlParsed,
            out bool adminSettingsSafe)
        {
            enabled = false;
            // Do not claim an unknown task belongs to this installation. A false
            // positive can make PollTaskRefresh remove a working Run value.
            matchesExecutable = false;
            commandPath = "";
            userMatches = false;
            explicitUser = false;
            xmlParsed = false;
            adminSettingsSafe = false;

            // The Task Scheduler stores a root task as one XML file. Reading that
            // file avoids schtasks' localized column resources (missing on some
            // Windows installations) and is the same definition the service uses.
            bool fileExists;
            if (TryGetTaskFileState(name, out fileExists))
            {
                if (!fileExists) return false;

                XmlDocument fileDocument;
                if (TryLoadTaskFileXml(name, out fileDocument))
                {
                    TaskXmlEvaluation fileEvaluation = EvaluateTaskXml(name, fileDocument);
                    if (fileEvaluation != null)
                    {
                        enabled = fileEvaluation.Enabled;
                        matchesExecutable = fileEvaluation.MatchesExecutable;
                        commandPath = fileEvaluation.CommandPath;
                        userMatches = fileEvaluation.UserMatches;
                        explicitUser = fileEvaluation.ExplicitUser;
                        xmlParsed = true;
                        adminSettingsSafe = fileEvaluation.AdminSettingsSafe;
                        return true;
                    }
                }
                // The file exists but could not be read or parsed. Fall through to
                // schtasks; if that also fails, QueryTaskOrUnknown still reports the
                // task as present, but it remains unverified.
            }

            bool parsed = false;
            bool parsedEnabled = false;
            bool parsedUserMatches = false;
            bool parsedExplicitUser = false;
            string parsedCommand = "";
            foreach (Encoding encoding in TaskXmlEncodings())
            {
                ProcessResult run = ProcessRunner.Run(
                    Sys.SystemExecutable("schtasks.exe"),
                    "/query /tn " + Shell.Quote(name) + " /xml",
                    Sys.SystemDirectory, 5000, null, null, null,
                    256 * 1024, encoding);
                if (!run.Started || run.TimedOut || run.ExitCode != 0)
                {
                    // A failed /xml query does not prove that the task is absent.
                    // A plain /query or the task file can still distinguish "task
                    // exists but XML is unavailable" from a genuine "task not found".
                    bool? taskExists = QueryTaskOrUnknown(name);
                    if (taskExists == true)
                    {
                        enabled = true;
                        return true;
                    }
                    return false;
                }

                string xml = run.Output.TrimStart('\uFEFF');
                int xmlStart = xml.IndexOf('<');
                if (xmlStart > 0) xml = xml.Substring(xmlStart);
                // A replacement character means this encoding is wrong; try the next.
                if (xml.IndexOf('\uFFFD') >= 0) continue;

                XmlDocument document;
                try
                {
                    document = new XmlDocument();
                    document.LoadXml(xml);
                }
                catch (Exception)
                {
                    continue;
                }

                TaskXmlEvaluation evaluation = EvaluateTaskXml(name, document);
                if (evaluation == null) continue;
                parsed = true;
                xmlParsed = true;
                parsedEnabled = evaluation.Enabled;
                parsedUserMatches = evaluation.UserMatches;
                parsedExplicitUser = evaluation.ExplicitUser;
                if (evaluation.Command.Length > 0) parsedCommand = evaluation.CommandPath;

                if (evaluation.MatchesExecutable)
                {
                    enabled = evaluation.Enabled;
                    matchesExecutable = true;
                    commandPath = evaluation.CommandPath;
                    userMatches = evaluation.UserMatches;
                    explicitUser = evaluation.ExplicitUser;
                    adminSettingsSafe = evaluation.AdminSettingsSafe;
                    return true;
                }

                // ASCII data is unaffected by the code page. No other encoding can
                // turn a genuine path/argument mismatch into a match.
                if (IsAscii(evaluation.Command) && IsAscii(evaluation.Arguments))
                {
                    enabled = evaluation.Enabled;
                    matchesExecutable = false;
                    commandPath = evaluation.CommandPath;
                    userMatches = evaluation.UserMatches;
                    explicitUser = evaluation.ExplicitUser;
                    adminSettingsSafe = false;
                    return true;
                }

                if (evaluation.Command.Length == 0) break;
            }

            if (parsed)
            {
                enabled = parsedEnabled;
                matchesExecutable = false;
                commandPath = parsedCommand;
                userMatches = parsedUserMatches;
                explicitUser = parsedExplicitUser;
                xmlParsed = true;
                adminSettingsSafe = false;
                return true;
            }

            // XML output can be unavailable on unusual systems. Fall back to
            // the old existence check, but do not claim that the task points at
            // this executable: treating another installation's task as enabled
            // would allow PollTaskRefresh to remove the current Run value.
            bool exists = QueryTask(name);
            enabled = exists;
            userMatches = false;
            explicitUser = false;
            xmlParsed = false;
            return exists;
        }

        internal static bool TaskXmlUserMatchesCurrentUser(XmlDocument document)
        {
            bool explicitUser;
            return TaskXmlUserMatchesCurrentUser(document, out explicitUser);
        }

        // explicitUser distinguishes an actual Principal/LogonTrigger UserId from
        // the legacy any-user task. The latter applies to every account and must
        // not be treated as proof that this user owns the task for deletion.
        internal static bool TaskXmlUserMatchesCurrentUser(XmlDocument document,
            out bool explicitUser)
        {
            explicitUser = false;
            if (document == null) return false;

            // A task with no explicit user is the legacy any-user task and applies
            // to the current logon. An explicit different user must never be treated
            // as this user's autostart entry: doing so lets a legacy task belonging
            // to another user suppress the current user's Run value or be deleted.
            foreach (XmlNode node in document.SelectNodes(
                "//*[local-name()='Principal']/*[local-name()='UserId']"))
            {
                if (!string.IsNullOrEmpty((node.InnerText ?? "").Trim())) explicitUser = true;
                if (!TaskUserIdMatchesCurrentUser(node.InnerText)) return false;
            }
            foreach (XmlNode node in document.SelectNodes(
                "//*[local-name()='LogonTrigger']/*[local-name()='UserId']"))
            {
                if (!string.IsNullOrEmpty((node.InnerText ?? "").Trim())) explicitUser = true;
                if (!TaskUserIdMatchesCurrentUser(node.InnerText)) return false;
            }
            return true;
        }

        // A task that exists and points at this executable is not enough for an
        // administrator service: it must also run highest-available, use the
        // interactive token, and keep running on battery. A weaker task is treated
        // as not enabled so the tray can offer to recreate it.
        internal static bool TaskXmlSupportsAdminAutostart(XmlDocument document)
        {
            if (document == null || !HasSingleExecAction(document)
                || !HasExplicitTaskUsers(document)) return false;
            if (!string.Equals(ReadTaskElement(document, "LogonType"),
                    "InteractiveToken", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(ReadTaskElement(document, "RunLevel"),
                    "HighestAvailable", StringComparison.OrdinalIgnoreCase))
                return false;
            // These settings default to true when omitted, so require an explicit false.
            if (!string.Equals(ReadTaskElement(document, "DisallowStartIfOnBatteries"),
                    "false", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(ReadTaskElement(document, "StopIfGoingOnBatteries"),
                    "false", StringComparison.OrdinalIgnoreCase))
                return false;
            // A scheduled task must not terminate a long-running tray after the
            // default three-day execution limit.
            if (!string.Equals(ReadTaskElement(document, "ExecutionTimeLimit"),
                    "PT0S", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        internal static bool TaskUserIdMatchesCurrentUser(string userId)
        {
            string value = (userId ?? "").Trim();
            if (value.Length == 0) return true;

            string sid = CurrentUserPrincipal();
            if (!string.IsNullOrEmpty(sid)
                && string.Equals(value, sid, StringComparison.OrdinalIgnoreCase))
                return true;

            string identityName = "";
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    identityName = identity.Name ?? "";
                }
            }
            catch (Exception)
            {
            }
            if (identityName.Length > 0
                && string.Equals(value, identityName, StringComparison.OrdinalIgnoreCase))
                return true;

            string domain = Environment.UserDomainName ?? "";
            string user = Environment.UserName ?? "";
            if (domain.Length > 0 && user.Length > 0
                && string.Equals(value, domain + "\\" + user, StringComparison.OrdinalIgnoreCase))
                return true;

            // An unqualified name is ambiguous on a domain-joined machine: a local
            // account and a domain account can share it. Resolve it to a SID and
            // compare identities instead of accepting every account whose display
            // name happens to equal Environment.UserName.
            if (value.IndexOf('\\') >= 0) return false;
            try
            {
                NTAccount account = new NTAccount(value);
                SecurityIdentifier translated = account.Translate(
                    typeof(SecurityIdentifier)) as SecurityIdentifier;
                string currentSidText = CurrentUserPrincipal();
                if (translated == null || string.IsNullOrEmpty(currentSidText)) return false;
                SecurityIdentifier current = new SecurityIdentifier(currentSidText);
                return translated.Equals(current);
            }
            catch (Exception)
            {
                // An unresolvable or ambiguous name must never be treated as the
                // current user just because its final component matches.
                return false;
            }
        }

        private static List<Encoding> TaskXmlEncodings()
        {
            List<Encoding> encodings = new List<Encoding>();
            HashSet<int> seen = new HashSet<int>();
            AddEncoding(encodings, seen, ProcessRunner.OemEncoding());
            AddEncoding(encodings, seen, Encoding.Default);
            AddEncoding(encodings, seen, Encoding.UTF8);
            AddEncoding(encodings, seen, Encoding.Unicode);
            return encodings;
        }

        private static void AddEncoding(List<Encoding> encodings, HashSet<int> seen,
            Encoding encoding)
        {
            if (encoding == null || !seen.Add(encoding.CodePage)) return;
            encodings.Add(encoding);
        }

        private static string ReadTaskElement(XmlDocument document, string name)
        {
            XmlNodeList nodes = document.GetElementsByTagName(name, "*");
            if (nodes.Count == 0 || nodes[0] == null) return "";
            return nodes[0].InnerText.Trim().Trim('"');
        }

        private static string ExpandTaskCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return "";
            try { return Environment.ExpandEnvironmentVariables(command); }
            catch (Exception) { return command; }
        }

        private static bool IsAscii(string value)
        {
            foreach (char c in value ?? "")
            {
                if (c > 0x7F) return false;
            }
            return true;
        }

        // A task with no arguments performs a normal start and therefore starts the
        // service. --autostart and --start-no-open are the explicit service-start
        // modes. Anything else (notably --tray-only) does not satisfy start-at-login.
        internal static bool TaskArgumentsStartService(string arguments)
        {
            string text = (arguments ?? "").Trim();
            if (text.Length == 0) return true;
            return string.Equals(text, "--autostart", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "--start-no-open", StringComparison.OrdinalIgnoreCase);
        }

        // Exposed to the test suite so the task-settings/trigger distinction is
        // checked without invoking schtasks (which needs an interactive task service).
        internal static bool IsTaskXmlEnabled(XmlDocument document)
        {
            if (document == null) return false;

            bool enabled = true;
            XmlNode settingsEnabled = document.SelectSingleNode(
                "/*[local-name()='Task']/*[local-name()='Settings']/*[local-name()='Enabled']");
            if (settingsEnabled != null && string.Equals(
                    settingsEnabled.InnerText.Trim(), "false",
                    StringComparison.OrdinalIgnoreCase))
            {
                enabled = false;
            }

            // A disabled logon trigger also prevents the task from starting. A task
            // with no logon trigger at all (for example a boot/daily task) is not the
            // start-at-login entry this tray manages.
            bool hasLogonTrigger = false;
            foreach (XmlNode trigger in document.GetElementsByTagName("LogonTrigger", "*"))
            {
                hasLogonTrigger = true;
                XmlNode triggerEnabled = trigger.SelectSingleNode("*[local-name()='Enabled']");
                if (triggerEnabled != null && string.Equals(
                        triggerEnabled.InnerText.Trim(), "false",
                        StringComparison.OrdinalIgnoreCase))
                {
                    enabled = false;
                    break;
                }
            }
            if (!hasLogonTrigger) enabled = false;
            return enabled;
        }

        // null means "could not determine"; false means the task is known to be
        // absent. Mutation paths must fail closed on null instead of creating a
        // second autostart mechanism. The Task Scheduler stores a root task as one
        // XML file, so its presence/absence is definitive without schtasks' MUI
        // resources. When the file state cannot be inspected, fall back to the
        // localized schtasks result; only the "file not found" OS message proves
        // absence there, every other non-zero result is unknown.
        private static bool? QueryTaskOrUnknown(string name)
        {
            bool exists;
            if (TryGetTaskFileState(name, out exists)) return exists;

            try
            {
                ProcessResult run = ProcessRunner.Run(
                    Sys.SystemExecutable("schtasks.exe"),
                    "/query /tn " + Shell.Quote(name),
                    Sys.SystemDirectory, 5000, null, null, null,
                    256 * 1024, ProcessRunner.OemEncoding());
                if (!run.Started || run.TimedOut) return null;
                if (run.ExitCode == 0) return true;
                return IsTaskNotFoundOutput(run.Output) ? (bool?)false : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // schtasks writes its errors through the console code page and localizes
        // them. Compare against the OS error text for FILE_NOT_FOUND / PATH_NOT_FOUND
        // instead of hard-coding English or treating every non-zero exit as absent.
        internal static bool IsTaskNotFoundOutput(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            foreach (int code in new int[] { 2, 3 })
            {
                try
                {
                    string message = new Win32Exception(code).Message;
                    if (!string.IsNullOrEmpty(message)
                        && output.IndexOf(message, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                }
            }
            return false;
        }

        private static bool QueryTask(string name)
        {
            return QueryTaskOrUnknown(name) == true;
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
                if (IsUntrustedOwner(directorySecurity)) return true;
                if (HasWriteAccess(directorySecurity)) return true;

                // A protected leaf directory is still replaceable when a standard user
                // can rename or delete one of its ancestors, or delete children there.
                if (HasUnsafeAncestor(directory)) return true;

                if (File.Exists(full))
                {
                    // The executable is not the whole trusted computing base. The
                    // elevated tray also loads .config, satellite resource DLLs, and
                    // other files from this directory. Check every child, and fail
                    // closed on reparse points.
                    if (PathSafety.ContainsReparsePointInTree(directory)) return true;
                    if (HasUnsafeChildAcl(directory)) return true;

                    FileSecurity fileSecurity = new FileInfo(full)
                        .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                    if (IsUntrustedOwner(fileSecurity)) return true;
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

        // Files created by an elevated process are owned by that process's user,
        // even when they are created inside a protected directory. An update helper
        // must restore the trusted owner before it reports success; otherwise its
        // own files fail IsUserWritableInstall on the next administrator start.
        // Keep the existing DACL (which the protected target directory supplies)
        // and only replace an untrusted owner.
        internal static bool ProtectFileOwner(string path, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = "file is missing";
                    return false;
                }
                // Following a link while changing ownership could affect a file
                // outside the installation. Refuse before opening the path.
                if (PathSafety.ContainsReparsePoint(path))
                {
                    error = "file path crosses a reparse point";
                    return false;
                }
                // Changing the owner through a hard link changes the owner of the
                // underlying file, which may be outside the installation. Refuse;
                // the caller treats this like any other ownership repair failure.
                using (FileStream stream = new FileStream(path, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (PathSafety.HasMultipleLinks(stream.SafeFileHandle))
                    {
                        error = "file has multiple hard links";
                        return false;
                    }
                }

                FileInfo info = new FileInfo(path);
                FileSecurity security = info.GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner);
                SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier))
                    as SecurityIdentifier;
                if (owner != null && IsTrustedPrincipal(owner)) return true;

                security.SetOwner(AdministratorsSid);
                info.SetAccessControl(security);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Directory counterpart used while the update helper creates a backup or
        // destination directory. Kept separate from ProtectInstallArtifacts so the
        // ownership is repaired immediately, before a crash can leave a user-owned
        // directory in the protected installation tree.
        internal static bool ProtectDirectoryOwner(string path, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    error = "directory is missing";
                    return false;
                }
                if (PathSafety.ContainsReparsePoint(path))
                {
                    error = "directory path crosses a reparse point";
                    return false;
                }
                DirectoryInfo info = new DirectoryInfo(path);
                DirectorySecurity security = info.GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner);
                SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier))
                    as SecurityIdentifier;
                if (owner != null && IsTrustedPrincipal(owner)) return true;
                security.SetOwner(AdministratorsSid);
                info.SetAccessControl(security);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Create a known cleanup DACL for a directory that will hold a moved
        // backup. A directory created by an elevated process is owned by that
        // user, and inheritance from Program Files can resolve CREATOR OWNER into
        // a concrete allow ACE for the same standard-user token. Replace the
        // inherited rules with trusted full control and read/delete-only access for
        // Builtin Users; the replacement tray needs no write access there.
        internal static bool CreateCleanupDirectory(string directory, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrEmpty(directory))
                {
                    error = "directory is missing";
                    return false;
                }
                if (!Directory.Exists(directory))
                {
                    if (PathSafety.ContainsReparsePoint(directory))
                    {
                        error = "directory path crosses a reparse point";
                        return false;
                    }
                    DirectorySecurity security = new DirectorySecurity();
                    security.SetOwner(AdministratorsSid);
                    security.SetAccessRuleProtection(true, false);
                    AddCleanupRules(security, true);
                    Directory.CreateDirectory(directory, security);
                }
                return ProtectCleanupDirectory(directory, true, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool ProtectCleanupDirectory(string directory, out string error)
        {
            return ProtectCleanupDirectory(directory, true, out error);
        }

        // allowUserDelete is false for the source backup still inside the protected
        // installation. If the move fails, that directory must not make the
        // installation look user-writable to the next elevated start.
        internal static bool ProtectCleanupDirectory(string directory, bool allowUserDelete,
            out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    error = "directory is missing";
                    return false;
                }
                if (PathSafety.ContainsReparsePoint(directory))
                {
                    error = "directory path crosses a reparse point";
                    return false;
                }
                // Changing a directory DACL with inheritance can propagate to
                // existing child objects. A planted junction or hard link could
                // therefore retarget the repair at a protected file.
                if (PathSafety.ContainsReparsePointInTree(directory))
                {
                    error = "directory tree contains a reparse point";
                    return false;
                }
                if (PathSafety.ContainsHardLinkInTree(directory))
                {
                    error = "directory tree contains a hard link";
                    return false;
                }

                DirectoryInfo info = new DirectoryInfo(directory);
                DirectorySecurity security = info.GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner);
                security.SetOwner(AdministratorsSid);
                security.SetAccessRuleProtection(true, false);
                AddCleanupRules(security, allowUserDelete);
                info.SetAccessControl(security);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void AddCleanupRules(DirectorySecurity security, bool allowUserDelete)
        {
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit
                | InheritanceFlags.ObjectInherit;
            SecurityIdentifier users = new SecurityIdentifier(
                WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(LocalSystemSid,
                FileSystemRights.FullControl, inherit, PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid,
                FileSystemRights.FullControl, inherit, PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users,
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None,
                AccessControlType.Allow));
            if (allowUserDelete)
            {
                security.AddAccessRule(new FileSystemAccessRule(users,
                    FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                    inherit, PropagationFlags.None, AccessControlType.Allow));
            }
        }

        // Protect every file and directory the update helper created under the
        // installation directory, including its backup and staging leftovers.
        // This deliberately preserves DACLs and only repairs file ownership.
        internal static bool ProtectInstallArtifacts(string directory, out string error)
        {
            error = "";
            try
            {
                string root = Path.GetFullPath(directory);
                if (!Directory.Exists(root))
                {
                    error = "directory is missing";
                    return false;
                }

                // Check reparse points before the hard-link walk so that a planted
                // junction cannot make the walk inspect a directory outside the
                // installation. The traversal below repeats the check per entry.
                if (PathSafety.ContainsReparsePointInTree(root))
                {
                    error = "reparse point in installation tree";
                    return false;
                }
                // A hard link is another name for a file object and can redirect an
                // owner change outside the installation. Refuse the whole repair if
                // the tree contains one, rather than changing the link target's owner.
                if (PathSafety.ContainsHardLinkInTree(root))
                {
                    error = "hard link in installation tree";
                    return false;
                }

                Stack<string> pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    string current = pending.Pop();
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "reparse point: " + current;
                        return false;
                    }

                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    FileSystemSecurity security = isDirectory
                        ? (FileSystemSecurity)new DirectoryInfo(current).GetAccessControl(
                            AccessControlSections.Access | AccessControlSections.Owner)
                        : (FileSystemSecurity)new FileInfo(current).GetAccessControl(
                            AccessControlSections.Access | AccessControlSections.Owner);

                    SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier))
                        as SecurityIdentifier;
                    if (owner == null || !IsTrustedPrincipal(owner))
                    {
                        security.SetOwner(AdministratorsSid);
                        if (isDirectory)
                            new DirectoryInfo(current).SetAccessControl((DirectorySecurity)security);
                        else
                            new FileInfo(current).SetAccessControl((FileSecurity)security);
                    }

                    // Ownership alone is not enough. A newly inherited DACL must
                    // not grant write/delete rights to an untrusted principal.
                    if (HasWriteAccess(security))
                    {
                        error = "untrusted ACL: " + current;
                        return false;
                    }

                    if (isDirectory)
                    {
                        foreach (string entry in Directory.GetFileSystemEntries(current))
                            pending.Push(entry);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Portable mode keeps the ini and logs beside the executable. Repairing the
        // installation ACL would leave Users with RX only, making those runtime files
        // read-only for the same standard user who has to run the tray. Refuse the
        // repair instead of silently breaking configuration and logging; the user can
        // disable portable mode or move the program to Program Files.
        private static bool UsesPortableBaseDirectory(string directory)
        {
            string home = (Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME") ?? "").Trim();
            if (home.Length > 0) return false;
            if (Instance.Suffix.Length != 0) return false;
            if (!Config.IsEnabled(Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORTABLE")))
                return false;
            return !Config.IsSystemDirectory(directory);
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

            if (UsesPortableBaseDirectory(directory))
            {
                message = L.T("auto.repairPortableUnsafe");
                return false;
            }

            // icacls interprets wildcards in a path. A wildcard in an installation
            // directory could make an elevated recursive repair touch sibling
            // directories, so refuse the repair instead of guessing.
            if (directory.IndexOf('*') >= 0 || directory.IndexOf('?') >= 0)
            {
                message = L.T("auto.repairParentUnsafe");
                return false;
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

            // icacls /t follows child junctions. A standard user can create a junction
            // below a writable install directory and redirect the elevated reset into a
            // protected directory. Refuse to start the repair if any child is a reparse
            // point or cannot be inspected.
            if (PathSafety.ContainsReparsePointInTree(directory))
            {
                message = L.T("auto.repairParentUnsafe");
                return false;
            }

            // icacls /t also acts on the underlying file behind a hard link. The
            // link is not a reparse point, so reject it explicitly before changing
            // ownership or ACLs.
            if (PathSafety.ContainsHardLinkInTree(directory))
            {
                message = L.T("auto.repairHardLinkUnsafe");
                return false;
            }

            int exitCode;
            string command;
            try
            {
                command = BuildSecureDirectoryCommand(directory);
            }
            catch (Exception ex)
            {
                message = L.T("auto.repairFailed", ex.Message);
                return false;
            }

            List<AclSnapshotEntry> aclSnapshot;
            try
            {
                aclSnapshot = CaptureAclSnapshot(directory);
            }
            catch (Exception ex)
            {
                message = L.T("auto.repairFailed", ex.Message);
                return false;
            }

            // Re-check both link forms immediately before the elevated command. This
            // does not make the operation atomic, but it shortens the window for a link
            // added while the snapshot was being captured. Checking only hard links
            // here would let a junction inserted after CaptureAclSnapshot redirect the
            // recursive icacls operation into a protected directory.
            if (PathSafety.ContainsReparsePointInTree(directory))
            {
                message = L.T("auto.repairParentUnsafe");
                return false;
            }
            if (PathSafety.ContainsHardLinkInTree(directory))
            {
                message = L.T("auto.repairHardLinkUnsafe");
                return false;
            }

            if (!RunRepairCommand(command, out exitCode))
            {
                if (exitCode == -3)
                {
                    // The command ran but timed out; it may have changed part of
                    // the tree. Put the original ACLs back.
                    message = RepairFailure(L.T("auto.repairTimeout"), aclSnapshot);
                }
                else if (exitCode == -2)
                    message = L.T("auto.repairCancelled");
                else
                    message = L.T("auto.repairFailed", L.T("auto.repairLaunchFailed"));
                return false;
            }
            if (exitCode != 0)
            {
                message = RepairFailure(
                    L.T("auto.repairCommandFailed", exitCode), aclSnapshot);
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

            message = RepairFailure(L.T("auto.repairStillUnsafe"), aclSnapshot);
            return false;
        }

        private sealed class AclSnapshotEntry
        {
            internal string Path;
            internal bool IsDirectory;
            internal FileSystemSecurity Security;
        }

        private static List<AclSnapshotEntry> CaptureAclSnapshot(string directory)
        {
            List<AclSnapshotEntry> snapshot = new List<AclSnapshotEntry>();
            Stack<string> pending = new Stack<string>();
            pending.Push(Path.GetFullPath(directory));
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("repair path contains a reparse point");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DirectorySecurity security = new DirectoryInfo(current).GetAccessControl(
                        AccessControlSections.Access | AccessControlSections.Owner);
                    AclSnapshotEntry entry = new AclSnapshotEntry();
                    entry.Path = current;
                    entry.IsDirectory = true;
                    entry.Security = security;
                    snapshot.Add(entry);
                    foreach (string child in Directory.GetFileSystemEntries(current))
                    {
                        FileAttributes childAttributes = File.GetAttributes(child);
                        if ((childAttributes & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidOperationException("repair path contains a reparse point");
                        if ((childAttributes & FileAttributes.Directory) != 0)
                            pending.Push(child);
                        else
                        {
                            FileSecurity childSecurity = new FileInfo(child).GetAccessControl(
                                AccessControlSections.Access | AccessControlSections.Owner);
                            AclSnapshotEntry childEntry = new AclSnapshotEntry();
                            childEntry.Path = child;
                            childEntry.IsDirectory = false;
                            childEntry.Security = childSecurity;
                            snapshot.Add(childEntry);
                        }
                    }
                }
            }
            return snapshot;
        }

        private static bool RestoreAclSnapshot(List<AclSnapshotEntry> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0) return false;
            // Never restore through a link. If the tree gained a hard link after the
            // repair check, changing the snapshot's ACLs could still affect a
            // protected file through that link; leave the ACL repair for manual review.
            if (PathSafety.ContainsHardLinkInTree(snapshot[0].Path)) return false;

            bool restored = true;
            // Restore children first, then the root that owns inheritance.
            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                AclSnapshotEntry entry = snapshot[i];
                try
                {
                    if (PathSafety.ContainsReparsePoint(entry.Path))
                    {
                        restored = false;
                        continue;
                    }
                    if (entry.IsDirectory)
                    {
                        new DirectoryInfo(entry.Path).SetAccessControl(
                            (DirectorySecurity)entry.Security);
                    }
                    else
                    {
                        new FileInfo(entry.Path).SetAccessControl((FileSecurity)entry.Security);
                    }
                }
                catch (Exception)
                {
                    restored = false;
                }
            }
            return restored;
        }

        private static string RepairFailure(string reason, List<AclSnapshotEntry> snapshot)
        {
            if (!RestoreAclSnapshot(snapshot))
                reason += " " + L.T("auto.repairRollbackFailed");
            return L.T("auto.repairFailed", reason);
        }

        internal static string BuildSecureDirectoryCommand(string directory)
        {
            string icacls = Shell.CmdQuote(Sys.SystemExecutable("icacls.exe"));
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
            command.Append(" ").Append(Shell.CmdQuote(directory));
            command.Append(" /setowner \"*").Append(administratorsSid)
                .Append("\" /t /l && ");
            command.Append(icacls);
            command.Append(" ").Append(Shell.CmdQuote(directory));
            // Reset the root first: an explicit standard-user ACE from an older
            // installation is not removed by /inheritance:r.
            command.Append(" /reset && ");
            command.Append(icacls);
            command.Append(" ").Append(Shell.CmdQuote(directory));
            command.Append(" /inheritance:r /grant:r");
            command.Append(" \"*").Append(systemSid).Append(":(OI)(CI)F\"");
            command.Append(" \"*").Append(administratorsSid).Append(":(OI)(CI)F\"");
            command.Append(" \"*").Append(usersSid).Append(":(OI)(CI)RX\"");
            command.Append(" && ");
            command.Append(icacls);
            command.Append(" ").Append(Shell.CmdQuote(directory + "\\*"));
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
                        try { Sys.KillTree(process.Id, Sys.ProcessStartTime(process), process); }
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

        internal static bool IsUntrustedOwner(FileSystemSecurity security)
        {
            try
            {
                SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier))
                    as SecurityIdentifier;
                if (owner == null) return true;
                return !IsTrustedPrincipal(owner);
            }
            catch (Exception)
            {
                // An owner that cannot be inspected must not be trusted.
                return true;
            }
        }

        private static bool IsTrustedPrincipal(SecurityIdentifier sid)
        {
            if (sid == null) return false;
            return sid.Equals(LocalSystemSid)
                || sid.Equals(AdministratorsSid)
                || sid.Equals(TrustedInstallerSid);
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

        // A cleanup store intentionally grants standard users delete rights so the
        // replacement tray can remove an old backup. Rights that let them create,
        // replace or re-permission entries are still unsafe.
        private const FileSystemRights StoreWriteRights = FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;

        // Check a directory that will receive a helper migration rather than an
        // installation. Delete-only rights are allowed here for the cleanup store.
        internal static bool IsUnsafeWritableDirectory(string directory)
        {
            try
            {
                string full = Path.GetFullPath(directory);
                if (PathSafety.ContainsReparsePoint(full)) return true;
                DirectoryInfo info = new DirectoryInfo(full);
                if (!info.Exists) return true;
                DirectorySecurity security = info.GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner);
                if (IsUntrustedOwner(security)) return true;
                return HasUnsafeAccess(security, StoreWriteRights);
            }
            catch (Exception)
            {
                return true;
            }
        }

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

        // Check the files the elevated tray can load, not just pi-web-tray.exe. An
        // explicit ACE on app.config or a satellite resource DLL is enough for code
        // execution in the elevated process. Reparse points are always unsafe here.
        // Files the elevated process actually loads or executes. Runtime output
        // (server.log, install.log, update.log, diag.txt, state*) is deliberately
        // excluded: those files are created by the tray's normal user token and a
        // user-owned log must not make an otherwise protected installation look
        // replaceable. The ini is included because it controls runtime settings,
        // Node path and registry/proxy settings for the elevated service.
        internal static bool IsElevatedLoadedFile(string path)
        {
            string name = Path.GetFileName(path ?? "");
            if (name.Equals("pi-web-tray.ini", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.Equals("pi-web-tray.files.txt", StringComparison.OrdinalIgnoreCase))
                return true;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasUnsafeChildAcl(string directory)
        {
            try
            {
                Stack<string> pending = new Stack<string>();
                pending.Push(Path.GetFullPath(directory));
                while (pending.Count > 0)
                {
                    string current = pending.Pop();
                    foreach (string entry in Directory.GetFileSystemEntries(current))
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) return true;

                        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                        // Files that are never loaded by the elevated process are
                        // runtime data. They are still covered by the reparse-point
                        // check above, but their owner/DACL must not turn the install
                        // into a false "user-writable" result.
                        if (!isDirectory && !IsElevatedLoadedFile(entry)) continue;

                        if (!isDirectory)
                        {
                            using (FileStream stream = new FileStream(entry, FileMode.Open,
                                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            {
                                if (PathSafety.HasMultipleLinks(stream.SafeFileHandle))
                                    return true;
                            }
                        }

                        FileSystemSecurity security = isDirectory
                            ? (FileSystemSecurity)new DirectoryInfo(entry).GetAccessControl(
                                AccessControlSections.Access | AccessControlSections.Owner)
                            : new FileInfo(entry).GetAccessControl(
                                AccessControlSections.Access | AccessControlSections.Owner);
                        if (IsUntrustedOwner(security)) return true;
                        if (HasWriteAccess(security)) return true;

                        if (isDirectory) pending.Push(entry);
                    }
                }
                return false;
            }
            catch (Exception)
            {
                // If any child ACL cannot be inspected, fail closed.
                return true;
            }
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

                    if (IsUntrustedOwner(security)) return true;
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
            return HasUnsafeAccess(security, mask);
        }

        // Return true when any untrusted principal can perform an operation covered
        // by mask. Checking only the current user's SID/groups missed an explicit ACE
        // for another standard user who could replace the executable or a resource.
        internal static bool HasUnsafeAccess(FileSystemSecurity security, FileSystemRights mask)
        {
            if (security == null) return true;

            AuthorizationRuleCollection rules;
            try
            {
                rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            }
            catch (Exception)
            {
                return true;
            }
            if (rules == null || rules.Count == 0) return true;

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                // Inherit-only ACEs apply to children, not to this object.
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if ((rule.FileSystemRights & mask) == 0) continue;

                SecurityIdentifier sid = rule.IdentityReference as SecurityIdentifier;
                if (sid == null || !IsTrustedPrincipal(sid)) return true;
            }

            return false;
        }

        // A no-user legacy task cannot be removed by this account. If it already
        // starts this executable, creating a second administrator entry would race
        // two trays at logon; refuse instead of leaving both entries.
        internal static bool LegacyTaskBlocksAdminCreation(bool legacyExists,
            bool legacyOwned, bool legacyMatchesCurrentExecutable)
        {
            return legacyExists && !legacyOwned && legacyMatchesCurrentExecutable;
        }

        internal static bool CreateTask(bool elevated, string executablePath,
            out bool legacyRemovalFailed)
        {
            legacyRemovalFailed = false;
            // Remove the legacy machine-wide task only after the replacement has
            // been created. Removing it first would leave the user with no autostart
            // entry if the second UAC prompt is cancelled.
            bool legacyExisted = Instance.Suffix.Length == 0 && QueryTask(LegacyTaskName);
            if (legacyExisted)
            {
                // A machine-wide legacy task with no explicit user cannot be safely
                // removed by this account. If it already starts this executable,
                // creating the new administrator task would leave two login entries
                // and race two trays. Fail instead of adding a duplicate; the user can
                // remove the legacy task manually.
                bool legacyOwned = QueryTaskUserBelongsToCurrentUser(LegacyTaskName,
                    out _);
                if (LegacyTaskBlocksAdminCreation(legacyExisted, legacyOwned,
                        QueryTaskMatchesCurrentExecutable(LegacyTaskName)))
                {
                    legacyRemovalFailed = true;
                    return false;
                }
            }

            string principal = CurrentUserPrincipal();
            if (string.IsNullOrEmpty(principal)) return false;

            // A stale task with the same name must not make a failed /create look
            // successful merely because QueryTask() still finds it.
            bool existedBefore = QueryTask();

            // schtasks /create /sc onlogon creates an "any user" trigger and leaves
            // the default battery policy in place (do not start on batteries, stop
            // when switching to batteries). Build a user-scoped XML definition
            // instead. Keep the file open with FileShare.Read while schtasks reads
            // it, so another process cannot replace the elevated task definition.
            // An elevated process can write beside the protected executable. A
            // non-elevated process cannot, so it uses the user temp directory and the
            // byte comparison below guards that file against modification.
            string xmlDirectory = elevated
                ? (Path.GetDirectoryName(Application.ExecutablePath) ?? Path.GetTempPath())
                : Path.GetTempPath();
            string xmlPath = Path.Combine(xmlDirectory,
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

                // This file can live in the protected installation directory.
                // Restore a trusted owner before schtasks reads it; otherwise the
                // next admin start would see a user-owned file in the install tree.
                // Do this before opening the read guard: the guard shares only read
                // access, so correcting the owner afterwards would be denied.
                if (elevated)
                {
                    string protectError;
                    if (!ProtectFileOwner(xmlPath, out protectError))
                    {
                        LogTask("task XML owner repair failed: " + protectError);
                        return false;
                    }
                }

                guard = new FileStream(xmlPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read);

                // The writer was closed and the read guard was opened in two steps. A
                // same-user process can win that tiny window and modify the XML. The
                // guard prevents further writes, so compare the bytes read through the
                // guard with the XML we built. This turns the former TOCTOU into a
                // failed task creation instead of registering attacker-controlled XML.
                if (!BytesEqual(xml, File.ReadAllBytes(xmlPath)))
                {
                    LogTask("task XML changed before registration");
                    return false;
                }

                string args = "/create /f /tn " + Shell.Quote(TaskName)
                    + " /xml " + Shell.Quote(xmlPath);
                if (!Launch(elevated, args)) return false;

                int verificationAttempts = 0;
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(250);
                    if (!QueryTask()) continue;

                    bool enabled;
                    bool matchesExecutable;
                    bool userMatches;
                    bool explicitUser;
                    bool xmlParsed;
                    bool adminSettingsSafe;
                    if (QueryTaskDetails(TaskName, out enabled, out matchesExecutable,
                            out _, out userMatches, out explicitUser, out xmlParsed,
                            out adminSettingsSafe)
                        && enabled && matchesExecutable && userMatches && explicitUser
                        && adminSettingsSafe)
                    {
                        FinishTaskCreation(elevated, legacyExisted, out legacyRemovalFailed);
                        return true;
                    }

                    // If no task existed before, observing one now is enough only when
                    // XML parsing is unavailable (for example on an unusual Windows
                    // installation). If XML was parsed but points at another command,
                    // another user, or has no logon trigger, do not call it success.
                    if (!existedBefore && !xmlParsed)
                    {
                        FinishTaskCreation(elevated, legacyExisted, out legacyRemovalFailed);
                        return true;
                    }
                    if (++verificationAttempts >= 4) return false;
                }
                LogTask("task creation was not observed: " + TaskName);
                return false;
            }
            catch (Exception ex)
            {
                LogTask("task creation failed: " + ex.Message);
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

        private static void FinishTaskCreation(bool elevated, bool legacyExisted,
            out bool legacyRemovalFailed)
        {
            legacyRemovalFailed = false;
            if (!legacyExisted || !QueryTask(LegacyTaskName)) return;
            // Never delete another user's machine-wide legacy task just because
            // it has the historical name.
            legacyRemovalFailed = !DeleteTaskIfOwned(elevated, LegacyTaskName);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        internal static byte[] BuildTaskXml(string executablePath, string principal)
        {
            return BuildTaskXml(executablePath, principal, WindowsVersion.Current);
        }

        internal static byte[] BuildTaskXml(string executablePath, string principal,
            Version windows)
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
                    // Windows 7's schtasks rejects version="1.1" for this definition
                    // (and version="1.0" rejects the logon trigger); omit the optional
                    // attribute there. Windows 8 and newer accept the 1.1 task version,
                    // so keep the existing definition on those systems.
                    if (windows == null
                        || !(windows.Major == 6 && windows.Minor == 1))
                        writer.WriteAttributeString("version", "1.1");

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
            bool? current = QueryTaskOrUnknown(TaskName);
            bool? legacy = Instance.Suffix.Length == 0
                ? QueryTaskOrUnknown(LegacyTaskName) : (bool?)false;
            if (current == null || legacy == null) return false;
            if (current == false && legacy == false) return true;

            if (current == true && !DeleteTaskIfOwned(elevated, TaskName)) return false;
            if (legacy == true && !DeleteTaskIfOwned(elevated, LegacyTaskName)) return false;
            return true;
        }

        // Delete only a task that belongs to this user. A no-user legacy
        // "PiWebTray" task is machine-wide and cannot be attributed to this
        // account, so it is reported but left untouched.
        private static bool DeleteTaskIfOwned(bool elevated, string name)
        {
            bool? exists = QueryTaskOrUnknown(name);
            if (exists == false) return true;
            if (exists == null) return false;
            bool parsed;
            bool owned = QueryTaskUserBelongsToCurrentUser(name, out parsed);
            if (!parsed) return false;
            if (!owned)
            {
                // A no-user task is not ours to delete. Report that it still exists
                // when it targets this installation, so the caller does not claim a
                // successful disable while the startup entry remains active.
                return !QueryTaskTargetsCurrentExecutable(name);
            }
            return DeleteTaskByName(elevated, name);
        }

        private static bool QueryTaskMatchesCurrentExecutable(string name)
        {
            bool enabled;
            bool matchesExecutable;
            string commandPath;
            return QueryTaskDetails(name, out enabled, out matchesExecutable, out commandPath)
                && matchesExecutable;
        }

        // DeleteTaskIfOwned uses the identity check separately from the full
        // current-user/admin-settings match. A no-user task that still launches this
        // executable must not be reported as successfully removed just because its
        // user scope is invalid for the current task name.
        private static bool QueryTaskTargetsCurrentExecutable(string name)
        {
            XmlDocument document;
            if (TryLoadTaskFileXml(name, out document))
            {
                TaskXmlEvaluation evaluation = EvaluateTaskXml(name, document);
                if (evaluation != null) return evaluation.TargetsExecutable;
            }
            // Cannot safely prove that an existing task is unrelated; fail closed
            // for an unknown/null result too.
            return QueryTaskOrUnknown(name) != false;
        }

        private static bool DeleteTaskByName(bool elevated, string name)
        {
            bool? exists = QueryTaskOrUnknown(name);
            if (exists == false) return true;
            if (exists == null) return false;
            if (!Launch(elevated, "/delete /f /tn " + Shell.Quote(name))) return false;

            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(200);
                bool? after = QueryTaskOrUnknown(name);
                if (after == false) return true;
            }
            bool removed = QueryTaskOrUnknown(name) == false;
            if (!removed) LogTask("task removal was not observed: " + name);
            return removed;
        }

        // Run directly when already elevated; otherwise go through runas (one UAC prompt).
        // When already elevated, capture schtasks' output and exit code so a failure is
        // written to update.log instead of disappearing. The runas path cannot capture
        // the child's output, but creation/removal is still verified by the task file.
        private static bool Launch(bool elevated, string args)
        {
            try
            {
                if (elevated)
                {
                    ProcessResult run = ProcessRunner.Run(
                        Sys.SystemExecutable("schtasks.exe"), args, Sys.SystemDirectory,
                        30000, null, null, null, 256 * 1024, ProcessRunner.OemEncoding());
                    if (!run.Started || run.TimedOut)
                    {
                        LogTask("schtasks did not complete: " + args
                            + (run.TimedOut ? " (timeout)" : ""));
                        return false;
                    }
                    if (run.ExitCode != 0)
                    {
                        LogTask("schtasks failed (exit " + run.ExitCode + "): " + args
                            + " " + (run.Output ?? "").Trim());
                        return false;
                    }
                    return true;
                }
                bool started = Sys.RunElevated(Sys.SystemExecutable("schtasks.exe"), args,
                    true, Sys.SystemDirectory);
                if (!started)
                    LogTask("schtasks elevation was cancelled or failed: " + args);
                return started;
            }
            catch (Exception ex)
            {
                LogTask("schtasks launch failed: " + ex.Message + " (" + args + ")");
                return false;
            }
        }

        private static void LogTask(string message)
        {
            try
            {
                string folder = TrayState.Folder;
                if (string.IsNullOrEmpty(folder)) return;
                Updater.AppendLog(Path.Combine(folder, "update.log"),
                    "autostart: " + message);
            }
            catch (Exception)
            {
            }
        }

        // Returns true on success; message is shown to the user either way
        internal static bool Apply(bool enable, bool admin, bool elevated, string executablePath, out string message)
        {
            // A Run value or scheduled task cannot carry PI_WEB_TRAY_INSTANCE (and the
            // user may have HOME/PORTABLE overrides too). Launching it would start the
            // default instance instead of the named one. Refuse instead of installing
            // a misleading, wrong-instance autostart entry.
            if (enable && Instance.Suffix.Length != 0)
            {
                message = L.T("auto.namedInstance");
                return false;
            }

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
                bool legacyRemovalFailed;
                if (CreateTask(elevated, executablePath, out legacyRemovalFailed))
                {
                    if (!ClearRunValue())
                    {
                        message = L.T("auto.enabledAdminRunLeft");
                        return true;
                    }
                    message = legacyRemovalFailed
                        ? L.T("auto.enabledAdminLegacyLeft")
                        : L.T("auto.enabledAdmin");
                    return true;
                }
                // A rejected no-user legacy task is a removal problem, not a UAC
                // cancellation; report that autostart may still be active.
                message = legacyRemovalFailed
                    ? L.T("auto.taskDeleteFailed")
                    : L.T("auto.taskFailed");
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
