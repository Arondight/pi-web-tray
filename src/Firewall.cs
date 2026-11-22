// Pi Web Tray — Windows Firewall support
//
// Network access is opt-in. The tray binds Pi Web to all interfaces only while
// this mode is enabled, and keeps one inbound TCP rule for the service port.
// Standard-user trays ask for UAC to run the system netsh tool; an already
// elevated tray updates the rule directly.

using System;
using System.Diagnostics;

namespace PiWebTray
{
    internal static class Firewall
    {
        // Firewall rules are machine-wide. Keep one rule per Windows user and
        // named instance so one user cannot overwrite or delete another user's
        // network-access rule.
        internal static string RuleName()
        {
            return Autostart.ScopedName(
                "Pi Web Tray-" + Autostart.CurrentUserKey(), Instance.Suffix);
        }

        internal static string BuildAddRuleArguments(int port)
        {
            return "advfirewall firewall add rule name=" + Shell.Quote(RuleName())
                + " dir=in action=allow protocol=TCP localport=" + port
                + " profile=domain,private,public";
        }

        internal static string BuildSetRuleArguments(int port)
        {
            return "advfirewall firewall set rule name=" + Shell.Quote(RuleName())
                + " new dir=in action=allow protocol=TCP localport=" + port
                + " profile=domain,private,public enable=yes";
        }

        internal static string BuildDeleteRuleArguments()
        {
            return "advfirewall firewall delete rule name=" + Shell.Quote(RuleName());
        }

        internal static string BuildShowRuleArguments()
        {
            return "advfirewall firewall show rule name=" + Shell.Quote(RuleName());
        }

        // Apply the rule without launching another process. Used by a tray that
        // already has the administrator token.
        internal static bool ApplyDirect(bool enabled, int port, out string error)
        {
            error = "";
            if (enabled && (port <= 0 || port > 65535))
            {
                error = L.T("network.invalidPort");
                return false;
            }

            if (!enabled)
            {
                // Try the delete even when the query says there is no rule. A
                // failed query must not turn a stale allow rule into a false
                // success; the follow-up query distinguishes "no rule" from a
                // delete that did not happen.
                if (RunNetsh(BuildDeleteRuleArguments(), out error)) return true;
                if (!RuleExists())
                {
                    error = "";
                    return true;
                }
                if (string.IsNullOrEmpty(error)) error = L.T("network.firewallFailed");
                return false;
            }

            // Update an existing rule in place instead of deleting it first. If
            // netsh fails, the previous rule remains in force and a port change
            // can be rolled back without losing network access.
            if (RuleExists())
            {
                if (!RunNetsh(BuildSetRuleArguments(port), out error)) return false;
            }
            else if (!RunNetsh(BuildAddRuleArguments(port), out error))
            {
                // Do not leave a half-applied rule behind. There is no previous
                // rule in this branch, so deleting the new one is safe.
                RunNetsh(BuildDeleteRuleArguments(), out error);
                return false;
            }
            if (!RuleExists())
            {
                RunNetsh(BuildDeleteRuleArguments(), out error);
                error = L.T("network.firewallFailed");
                return false;
            }
            return true;
        }

        // A standard-user tray cannot change Windows Firewall directly. Run the
        // system netsh executable through UAC instead of elevating the tray
        // executable itself, so portable installs work and no application code
        // from a user-writable folder is loaded with an administrator token.
        internal static bool Configure(bool enabled, int port, bool elevated, out string error)
        {
            error = "";
            if (enabled && (port <= 0 || port > 65535))
            {
                error = L.T("network.invalidPort");
                return false;
            }
            if (elevated) return ApplyDirect(enabled, port, out error);

            bool exists = RuleExists();
            if (!enabled)
            {
                if (!exists) return true;
                if (!RunNetshElevated(BuildDeleteRuleArguments(), out error))
                {
                    // The rule may have disappeared between the query and the
                    // elevated command. Only report failure if it still exists.
                    if (!RuleExists())
                    {
                        error = "";
                        return true;
                    }
                    return false;
                }
                if (RuleExists())
                {
                    error = L.T("network.firewallFailed");
                    return false;
                }
                return true;
            }

            string arguments = exists
                ? BuildSetRuleArguments(port)
                : BuildAddRuleArguments(port);
            if (!RunNetshElevated(arguments, out error)) return false;
            if (!RuleExists())
            {
                error = L.T("network.firewallFailed");
                return false;
            }
            return true;
        }

        private static bool RunNetshElevated(string arguments, out string error)
        {
            error = "";
            string netsh;
            try { netsh = Sys.SystemExecutable("netsh.exe"); }
            catch (Exception)
            {
                error = L.T("network.firewallFailed");
                return false;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = netsh;
                psi.Arguments = arguments;
                psi.WorkingDirectory = Sys.SystemDirectory;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process child = Process.Start(psi))
                {
                    if (child == null)
                    {
                        error = L.T("network.firewallFailed");
                        return false;
                    }
                    child.WaitForExit();
                    if (child.ExitCode == 0) return true;
                    error = L.T("network.firewallFailed");
                    return false;
                }
            }
            catch (Exception)
            {
                error = L.T("network.elevationCanceled");
                return false;
            }
        }

        private static bool RuleExists()
        {
            string error;
            return RunNetsh(BuildShowRuleArguments(), out error);
        }

        private static bool RunNetsh(string arguments, out string error)
        {
            error = "";
            string netsh;
            try { netsh = Sys.SystemExecutable("netsh.exe"); }
            catch (Exception) { netsh = ""; }
            if (string.IsNullOrEmpty(netsh))
            {
                error = L.T("network.firewallFailed");
                return false;
            }

            ProcessResult result;
            try
            {
                result = ProcessRunner.Run(netsh, arguments, Sys.SystemDirectory,
                    15000, null, null);
            }
            catch (Exception)
            {
                error = L.T("network.firewallFailed");
                return false;
            }
            if (result == null || !result.Started || result.ExitCode != 0)
            {
                error = L.T("network.firewallFailed");
                return false;
            }
            return true;
        }
    }
}
