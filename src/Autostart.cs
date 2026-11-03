// Pi Web Tray — start at login
//
// Two mutually exclusive modes: normal permissions write HKCU\...\Run; admin rights use
// an on-logon scheduled task (no UAC prompt). Only one may exist, or login starts two trays.

using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace PiWebTray
{
    internal static class Autostart
    {
        internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string RunValueName = "PiWebTray";
        internal const string TaskName = "PiWebTray";

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
            try
            {
                string value = RunValue();
                if (value.Length == 0) return;
                if (value.IndexOf(Shell.Quote(executablePath), StringComparison.OrdinalIgnoreCase) >= 0) return;
                SetRunValue(executablePath);
            }
            catch (Exception)
            {
            }
        }

        internal static bool QueryTask()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Sys.SystemExecutable("schtasks.exe");
                psi.Arguments = "/query /tn " + Shell.Quote(TaskName);
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

        internal static void ClearRunValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    key.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception)
            {
            }
        }

        internal static bool CreateTask(bool elevated, string executablePath)
        {
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
            if (!QueryTask()) return true;

            if (!Launch(elevated, "/delete /f /tn " + Shell.Quote(TaskName))) return false;

            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(200);
                if (!QueryTask()) return true;
            }
            return !QueryTask();
        }

        // Run directly when already elevated; otherwise go through runas (one UAC prompt)
        private static bool Launch(bool elevated, string args)
        {
            try
            {
                if (elevated)
                {
                    Process p = Sys.StartHidden(Sys.SystemExecutable("schtasks.exe"), args, null);
                    if (p == null) return false;
                    p.Dispose();
                    return true;
                }
                return Sys.RunElevated(Sys.SystemExecutable("schtasks.exe"), args);
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
                ClearRunValue();
                message = L.T("auto.disabled");
                return true;
            }

            if (admin)
            {
                // Create the task before removing the old Run value. If UAC is cancelled, the
                // previous normal-permission autostart must keep working instead of vanishing.
                if (CreateTask(elevated, executablePath))
                {
                    ClearRunValue();
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
