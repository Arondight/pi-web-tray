// Pi Web Tray — command-line quoting and secret redaction.

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace PiWebTray
{
    internal static class Shell
    {
        // Wrap a value in double quotes only. This is safe for the file paths, script
        // paths and task/rule names it is used with today (Windows paths cannot contain
        // a double quote and these values never end in a backslash). It does NOT escape
        // a trailing backslash or an embedded quote; use QuoteArg for any value that may
        // contain those, or the closing quote can be escaped and the argument merged.
        internal static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        // Quote a path for a command line parsed by cmd.exe. A percent sign starts
        // environment expansion even inside double quotes, and ^% inside quotes is
        // literal. Split the value around percent signs: each segment stays quoted
        // (spaces and &|<> are safe) while ^% is emitted outside the quote, where
        // cmd.exe turns it into a literal percent.
        internal static string CmdQuote(string value)
        {
            if (value == null) value = "";
            if (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\0') >= 0)
                throw new ArgumentException("A cmd path cannot contain control characters.", "value");
            if (value.IndexOf('%') < 0) return "\"" + value + "\"";

            string[] parts = value.Split('%');
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) result.Append("^%");
                result.Append('"').Append(parts[i]).Append('"');
            }
            return result.ToString();
        }

        // Quote one argument using the CommandLineToArgvW rules. Backslashes are literal
        // unless they precede a quote, and a trailing run of backslashes must be doubled
        // before the closing quote. Used for paths passed to the update helper.
        internal static string QuoteArg(string value)
        {
            if (value == null) return "\"\"";

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append(c);
                    backslashes = 0;
                    continue;
                }
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }
                sb.Append(c);
            }
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        // cmd.exe /c needs the whole command wrapped in one more pair of quotes; inner quotes stay.
        // /v:off prevents !VAR! expansion in user-controlled paths; /d disables AutoRun.
        internal static string CmdLine(string command)
        {
            return "/d /v:off /s /c \"" + command + "\"";
        }

        internal static string ComSpec()
        {
            // Never use the user-controlled ComSpec variable here: this shell can be
            // started with the tray's elevated token.
            return Sys.SystemExecutable("cmd.exe");
        }

        internal static string Home()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) return home;
            return Environment.GetEnvironmentVariable("USERPROFILE");
        }

        internal static string AppDataRoaming()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        internal static string LocalAppData()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        // Never persist credentials embedded in registry URLs (or query tokens) in logs.
        private static readonly Regex UrlUserInfo = new Regex(
            @"((?:https?|socks(?:5h?)?)://)[^/@\s]+@", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex QuerySecret = new Regex(
            @"([?&](?:auth|token|password|_authToken|access_token|authToken|npm_token|apikey|api_key|private_token|client_secret)=)[^&\s""]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // npm's own authentication form as persisted in .npmrc ("//registry.example.com/:_authToken=SECRET")
        // and echoed by npm config output. It has no ? or & prefix and no URL userinfo, so
        // the patterns above never match it.
        private static readonly Regex NpmRegistrySecret = new Regex(
            @"(//[^\s""]+?/:(?:_authToken|_password|_auth|api_key|private_token|client_secret)=)[^\s""]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Bare .npmrc keys (api_key=..., client_secret=...) have no ?/& query marker.
        private static readonly Regex BareSecret = new Regex(
            @"(^|[\s;])((?:api_key|private_token|client_secret|_authToken|_password|_auth|token|password)=)[^\s"";]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string RedactSecrets(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = UrlUserInfo.Replace(text, "$1***@");
            text = QuerySecret.Replace(text, "$1***");
            text = NpmRegistrySecret.Replace(text, "$1***");
            text = BareSecret.Replace(text, "$1$2***");
            return text;
        }

        // NotifyIcon.Text is capped at 63 chars; longer values throw
        internal static string Clip(string text, int max)
        {
            if (text == null) return "";
            if (text.Length <= max) return text;
            if (max <= 3) return text.Substring(0, max);
            return text.Substring(0, max - 3) + "...";
        }

        // CJK characters take two console columns, everything else one. Shared by
        // the environment report and the help output so both align identically.
        internal static int VisualWidth(string text)
        {
            int width = 0;
            foreach (char c in text ?? "") width += c > 0x2E80 ? 2 : 1;
            return width;
        }
    }
}
