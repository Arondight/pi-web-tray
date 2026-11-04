// Pi Web Tray — system helpers
//
// Business-free helpers only: port probing, process control, elevation, quoting, version compare,
// console output. No dependency on business types, so they can be reused and tested in isolation.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PiWebTray
{
    // ---------------- ports / HTTP ----------------

    internal static class Net
    {
        // Ask the OS for its listener table instead of opening a TCP connection on every
        // 500 ms tick. This is faster on Windows 7, leaves no SYN_SENT sockets behind when a
        // firewall drops probes, and still covers listeners bound to any local address.
        internal static bool IsPortOpen(int port, int timeoutMs)
        {
            try
            {
                IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
                foreach (IPEndPoint endpoint in properties.GetActiveTcpListeners())
                {
                    if (endpoint != null && endpoint.Port == port) return true;
                }
                return false;
            }
            catch (Exception)
            {
                // Fall back to a real connection on unusual systems where the table is unavailable
            }

            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool WaitPortClosed(int port, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (!IsPortOpen(port, 200)) return true;
                Thread.Sleep(150);
                waited += 150;
            }
            return false;
        }

        // An open port does not mean Next.js answers yet; used after install to confirm the service works
        internal static bool IsHttpOk(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Method = "GET";
                request.AllowAutoRedirect = true;
                // 127.0.0.1 is local; a corporate/system proxy must not intercept the readiness probe
                request.Proxy = null;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    return code >= 200 && code < 400;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    try
                    {
                        int code = (int)response.StatusCode;
                        return code >= 200 && code < 400;
                    }
                    finally
                    {
                        response.Close();
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        // Last-resort pi-web identity check when the native process probe cannot read
        // the owning process.
        // This is intentionally only a fallback; a mere HTTP 200 is not enough.
        internal static bool IsPiWebHttp(int port, int timeoutMs)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:" + port + "/");
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Method = "GET";
                request.AllowAutoRedirect = true;
                request.Proxy = null;
                request.UserAgent = "pi-web-tray/" + Program.Version;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return ContainsPiWebMarker(reader);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response == null) return false;
                try
                {
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return ContainsPiWebMarker(reader);
                    }
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    response.Close();
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ContainsPiWebMarker(StreamReader reader)
        {
            char[] buffer = new char[8192];
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) return false;
            string text = new string(buffer, 0, read);
            return text.IndexOf("pi-web", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // ---------------- processes ----------------

    internal static class Sys
    {
        // Resolve Windows tools from the system directory instead of relying on the caller's
        // current directory or PATH. This blocks a local taskkill.exe/netstat.exe
        // in a writable working directory from being executed by the tray.
        internal static string SystemExecutable(string name)
        {
            try
            {
                string directory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) return path;
            }
            catch (Exception)
            {
            }
            return name;
        }

        internal static void KillTree(int pid)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = SystemExecutable("taskkill.exe");
                psi.Arguments = "/PID " + pid + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process taskkill = Process.Start(psi))
                {
                    // taskkill is detached; the using only releases the Process handle.
                }
            }
            catch (Exception)
            {
            }
        }

        internal static void KillByPort(int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = SystemExecutable("netstat.exe");
                // No protocol filter: an IPv6-only listener appears only in the TCPv6 table.
                psi.Arguments = "-ano";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;

                using (Process netstat = Process.Start(psi))
                {
                    if (netstat == null) return;
                    string output = netstat.StandardOutput.ReadToEnd();
                    netstat.WaitForExit(3000);

                    string needle = ":" + port;
                    foreach (string line in output.Split('\n'))
                    {
                        // netstat is localized; match the local endpoint instead of the English state text.
                        string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5) continue;
                        if (!parts[1].EndsWith(needle, StringComparison.Ordinal)) continue;
                        if (!parts[2].EndsWith(":0", StringComparison.Ordinal)) continue;
                        int pid;
                        if (!int.TryParse(parts[4], out pid) || pid <= 4) continue;
                        KillTree(pid);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        internal static Process StartHidden(string fileName, string arguments, string workingDir)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            if (workingDir != null) psi.WorkingDirectory = workingDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            return Process.Start(psi);
        }

        // Start elevated (one UAC prompt); false means the user cancelled or it failed
        internal static bool RunElevated(string fileName, string arguments)
        {
            return RunElevated(fileName, arguments, true);
        }

        // The update helper needs a visible error dialog, so it must not be started hidden
        internal static bool RunElevated(string fileName, string arguments, bool hidden)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = arguments;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal;
                Process.Start(psi);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool ProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // ---------------- OS version ----------------

    internal static class WindowsVersion
    {
        // Environment.OSVersion can be affected by the application manifest. RtlGetVersion
        // reports the real kernel version, which also keeps Windows 8.1 (6.3) distinct from 8 (6.2).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfoEx
        {
            internal int Size;
            internal int Major;
            internal int Minor;
            internal int Build;
            internal int PlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string ServicePack;

            internal ushort ServicePackMajor;
            internal ushort ServicePackMinor;
            internal ushort SuiteMask;
            internal byte ProductType;
            internal byte Reserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OsVersionInfoEx versionInfo);

        internal static Version Current
        {
            get
            {
                try
                {
                    OsVersionInfoEx info = new OsVersionInfoEx();
                    info.Size = Marshal.SizeOf(typeof(OsVersionInfoEx));
                    if (RtlGetVersion(ref info) == 0 && info.Major > 0)
                        return new Version(info.Major, info.Minor, info.Build);
                }
                catch (Exception)
                {
                }
                return Environment.OSVersion.Version;
            }
        }
    }

    // ---------------- elevation state ----------------

    internal static class Elevation
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        internal static bool IsElevated()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out token)) return false;
                int elevation;
                int size;
                if (!GetTokenInformation(token, 20 /*TokenElevation*/, out elevation, 4, out size)) return false;
                return elevation != 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }
    }

    // ---------------- command line ----------------

    internal static class Shell
    {
        internal static string Quote(string value)
        {
            return "\"" + value + "\"";
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

        // cmd.exe /c needs the whole command wrapped in one more pair of quotes; inner quotes stay
        internal static string CmdLine(string command)
        {
            return "/d /s /c \"" + command + "\"";
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
            @"(https?://)[^/@\s]+@", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
    }

    // ---------------- version compare ----------------

    internal static class VersionUtil
    {
        // "v22.23.1" -> [22,23,1]; anything unparsable is ignored
        internal static int[] Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new int[0];
            string text = raw.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);

            List<int> numbers = new List<int>();
            foreach (string part in text.Split('.'))
            {
                StringBuilder digits = new StringBuilder();
                int consumed = 0;
                foreach (char c in part)
                {
                    if (c < '0' || c > '9') break;
                    digits.Append(c);
                    consumed++;
                }

                int value;
                if (digits.Length == 0
                    || !int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    break;
                }

                numbers.Add(value);

                // "1-pre": the numeric part is the patch level, a pre-release suffix ends parsing
                if (consumed < part.Length) break;
            }
            return numbers.ToArray();
        }

        // a < b -> -1, equal -> 0, a > b -> 1
        internal static int Compare(string a, string b)
        {
            int[] left = Parse(a);
            int[] right = Parse(b);
            int length = Math.Max(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                int l = i < left.Length ? left[i] : 0;
                int r = i < right.Length ? right[i] : 0;
                if (l < r) return -1;
                if (l > r) return 1;
            }
            return 0;
        }

        internal static bool AtLeast(string actual, string minimum)
        {
            return Compare(actual, minimum) >= 0;
        }
    }

    // ---------------- console output (works from a winexe too) ----------------

    internal static class ConsoleHost
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        private static bool _attached;

        // Attach to the parent console when started from cmd/PowerShell; otherwise return false
        internal static bool TryAttach()
        {
            if (_attached) return true;
            try
            {
                if (AttachConsole(AttachParentProcess))
                {
                    _attached = true;
                    // The console defaults to a local code page such as 936, so raw UTF-8 output is mojibake.
                    // Switching to 65001 makes CJK render correctly (the same thing pi itself does on Windows).
                    try { Console.OutputEncoding = new UTF8Encoding(false); }
                    catch (Exception) { }
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        internal static void WriteLine(string text)
        {
            try { Console.Out.WriteLine(text); } catch (Exception) { }
        }

        internal static void WriteError(string text)
        {
            try { Console.Error.WriteLine(text); } catch (Exception) { }
        }
    }
}
