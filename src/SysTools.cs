// Pi Web Tray — system helpers
//
// Business-free helpers only: port probing, process control, elevation, quoting, version compare,
// console output. No dependency on business types, so they can be reused and tested in isolation.

using System;
using System.Collections;
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
        // Prefer the OS listener tables over opening a TCP connection on every 500 ms
        // tick. This is faster on Windows 7, leaves no SYN_SENT sockets behind when a
        // firewall drops probes, and still covers listeners bound to any local address.
        internal static bool IsPortOpen(int port, int timeoutMs)
        {
            if (port <= 0) return false;

            // The listener-only table is authoritative. Some Windows builds put a newly
            // created listener into the all-connections table with a bogus state (0 or
            // FIN_WAIT_1). GetActiveTcpListeners() and netstat filter on LISTEN, so they
            // miss a server that is actually accepting connections. The listener table
            // still carries the row and the owning PID, so ignore its state field.
            if (TcpListenerTable.IsListening(port)) return true;

            try
            {
                IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
                foreach (IPEndPoint endpoint in properties.GetActiveTcpListeners())
                {
                    if (endpoint != null && endpoint.Port == port) return true;
                }
            }
            catch (Exception)
            {
                // Fall back to a real connection on unusual systems where the table is unavailable
            }

            // Last resort: both tables said no. A real loopback connection also keeps
            // working when a security filter hides the socket from the tables.
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

        internal static bool WaitPortOpen(int port, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (IsPortOpen(port, 200)) return true;
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

    // ---------------- listener table ----------------

    // GetActiveTcpListeners() filters the all-connections table on TCP_STATE_LISTEN.
    // A broken state field therefore hides a healthy listener. TCP_TABLE_OWNER_PID_LISTENER
    // is populated by the kernel from the listener list itself, so every row there is a
    // listener regardless of the state value. It also carries the owning PID, which lets
    // ServiceProbe identify the process without spawning netstat.
    internal static class TcpListenerTable
    {
        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const int TcpTableOwnerPidListener = 3;
        private const uint ErrorInsufficientBuffer = 122;

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
            public uint OwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcp6RowOwnerPid
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] LocalAddress;
            public uint LocalScopeId;
            public uint LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] RemoteAddress;
            public uint RemoteScopeId;
            public uint RemotePort;
            public uint State;
            public uint OwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
            bool order, int addressFamily, int tableClass, uint reserved);

        internal static bool IsListening(int port)
        {
            int pid;
            return Find(port, out pid);
        }

        internal static int ListenerPid(int port)
        {
            int pid;
            return Find(port, out pid) ? pid : 0;
        }

        // A listener can have PID 0 (kernel-owned), so a found row must be reported
        // separately from a non-zero PID. Prefer an IPv4/IPv6 row with a real PID.
        private static bool Find(int port, out int pid)
        {
            pid = 0;
            if (port <= 0) return false;

            bool found = false;
            int candidate;
            if (FindV4(port, out candidate))
            {
                found = true;
                if (candidate > 0) { pid = candidate; return true; }
            }
            if (FindV6(port, out candidate))
            {
                found = true;
                if (candidate > 0) { pid = candidate; return true; }
            }
            return found;
        }

        private static bool FindV4(int port, out int pid)
        {
            pid = 0;
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet,
                TcpTableOwnerPidListener, 0);
            if (result != 0 && result != ErrorInsufficientBuffer) return false;
            if (size <= 0) return false;

            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(table, ref size, false, AfInet,
                    TcpTableOwnerPidListener, 0);
                if (result != 0) return false;

                int count = Marshal.ReadInt32(table);
                int rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
                if (count <= 0 || 4L + (long)count * rowSize > size) return false;

                for (int i = 0; i < count; i++)
                {
                    IntPtr row = new IntPtr(table.ToInt64() + 4 + (long)i * rowSize);
                    MibTcpRowOwnerPid entry = (MibTcpRowOwnerPid)Marshal.PtrToStructure(
                        row, typeof(MibTcpRowOwnerPid));
                    if (NetworkPort(entry.LocalPort) == port)
                    {
                        pid = (int)entry.OwningPid;
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        private static bool FindV6(int port, out int pid)
        {
            pid = 0;
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet6,
                TcpTableOwnerPidListener, 0);
            if (result != 0 && result != ErrorInsufficientBuffer) return false;
            if (size <= 0) return false;

            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(table, ref size, false, AfInet6,
                    TcpTableOwnerPidListener, 0);
                if (result != 0) return false;

                int count = Marshal.ReadInt32(table);
                int rowSize = Marshal.SizeOf(typeof(MibTcp6RowOwnerPid));
                if (count <= 0 || 4L + (long)count * rowSize > size) return false;

                for (int i = 0; i < count; i++)
                {
                    IntPtr row = new IntPtr(table.ToInt64() + 4 + (long)i * rowSize);
                    MibTcp6RowOwnerPid entry = (MibTcp6RowOwnerPid)Marshal.PtrToStructure(
                        row, typeof(MibTcp6RowOwnerPid));
                    if (NetworkPort(entry.LocalPort) == port)
                    {
                        pid = (int)entry.OwningPid;
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        private static int NetworkPort(uint value)
        {
            return (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
        }
    }

    // ---------------- processes ----------------

    internal static class Sys
    {
        // ShellExecute/runas cannot use ProcessStartInfo.EnvironmentVariables. Temporarily
        // remove profiler/startup-hook variables from this process so they are not inherited
        // by the elevated child. The lock prevents two elevated launches from racing the
        // save/restore; node/npm variables are intentionally left alone.
        private static readonly object ElevatedLaunchLock = new object();

        private static bool IsProfilerEnvironmentVariable(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool StartElevatedSanitized(ProcessStartInfo psi)
        {
            lock (ElevatedLaunchLock)
            {
                Dictionary<string, string> saved = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    try
                    {
                        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables(
                            EnvironmentVariableTarget.Process))
                        {
                            string name = entry.Key as string;
                            if (!IsProfilerEnvironmentVariable(name)) continue;
                            saved[name] = Convert.ToString(entry.Value) ?? "";
                        }
                    }
                    catch (Exception)
                    {
                        // If the environment cannot be enumerated, still attempt the
                        // elevation rather than turning a hardening step into a failure.
                        saved.Clear();
                    }

                    foreach (string name in saved.Keys)
                    {
                        try
                        {
                            Environment.SetEnvironmentVariable(name, null,
                            EnvironmentVariableTarget.Process);
                        }
                        catch (Exception) { }
                    }

                    using (Process child = Process.Start(psi))
                    {
                        return child != null;
                    }
                }
                finally
                {
                    foreach (KeyValuePair<string, string> pair in saved)
                    {
                        try
                        {
                            Environment.SetEnvironmentVariable(pair.Key, pair.Value,
                            EnvironmentVariableTarget.Process);
                        }
                        catch (Exception) { }
                    }
                }
            }
        }

        // Resolve Windows tools from the system directory instead of relying on the caller's
        // current directory or PATH. This blocks a local taskkill.exe/netstat.exe
        // in a writable working directory from being executed by the tray.
        internal static string SystemDirectory
        {
            get
            {
                try { return Environment.GetFolderPath(Environment.SpecialFolder.System); }
                catch (Exception) { return ""; }
            }
        }

        internal static string SystemExecutable(string name)
        {
            try
            {
                string directory = SystemDirectory;
                if (!string.IsNullOrEmpty(directory)) return Path.Combine(directory, name);
            }
            catch (Exception)
            {
            }
            return name;
        }

        internal static void KillTree(int pid)
        {
            if (pid <= 4) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = SystemExecutable("taskkill.exe");
                psi.Arguments = "/PID " + pid + " /T /F";
                psi.WorkingDirectory = SystemDirectory;
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

        internal static Process StartHidden(string fileName, string arguments, string workingDir)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            if (workingDir != null) psi.WorkingDirectory = workingDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            ProcessRunner.HardenEnvironment(psi, Elevation.IsElevated());
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
            return RunElevated(fileName, arguments, hidden, null);
        }

        internal static bool RunElevated(string fileName, string arguments, bool hidden,
            string workingDirectory)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = arguments;
                if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal;
                return StartElevatedSanitized(psi);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static string ExplorerExecutable()
        {
            try
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windows)) return Path.Combine(windows, "explorer.exe");
            }
            catch (Exception)
            {
            }
            return "explorer.exe";
        }

        // An elevated process must not resolve a user-controlled URL/file association
        // with its own token. Hand the target to the already-running medium-integrity
        // Explorer instead. Non-elevated launches keep the normal ShellExecute path.
        internal static void OpenExternal(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            ProcessStartInfo psi = new ProcessStartInfo();
            if (Elevation.IsElevated())
            {
                psi.FileName = ExplorerExecutable();
                psi.Arguments = Shell.QuoteArg(target);
                psi.WorkingDirectory = SystemDirectory;
                psi.UseShellExecute = true;
            }
            else
            {
                psi.FileName = target;
                psi.UseShellExecute = true;
            }
            using (Process child = Process.Start(psi))
            {
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
