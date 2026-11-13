// Pi Web Tray — rotating log host for the service
//
// cmd keeps its redirected file handle open for the whole life of node, so the tray
// cannot rotate server.log while the service runs. The service output is piped to this
// mode instead: it owns the file handle and can rotate the log at any time.

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace PiWebTray
{
    internal static class LogHost
    {
        internal static int Run(string[] args)
        {
            int index = Array.FindIndex(args, delegate (string item)
            {
                return string.Equals(item, "--log-host", StringComparison.OrdinalIgnoreCase);
            });
            if (index < 0 || index + 1 >= args.Length) return 2;

            string path = args[index + 1];
            if (string.IsNullOrEmpty(path)) return 2;

            // This process can be started by an elevated tray. Never let a forged
            // --log-host argument append to an arbitrary protected file.
            if (!IsExpectedLogPath(path)) return 2;

            long maxBytes = 4 * 1024 * 1024;
            if (index + 2 < args.Length)
            {
                if (!long.TryParse(args[index + 2], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out maxBytes)
                    || maxBytes < 1024 * 1024 || maxBytes > 64L * 1024 * 1024)
                {
                    return 2;
                }
            }

            // Do not pretend to host a log that cannot be opened. The tray's
            // --log-host-check already probes this, but a direct invocation must
            // fail visibly instead of silently discarding service output.
            if (!CanOpenForAppend(path)) return 2;
            try
            {
                using (Stream input = Console.OpenStandardInput())
                {
                    Append(input, path, maxBytes);
                }
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        // The log file may be locked by a viewer, so a failed rotation is ignored and the
        // host keeps consuming its input instead of stalling the service.
        internal static void Append(Stream input, string path, long maxBytes)
        {
            FileStream output = Open(path);
            using (StreamReader reader = new StreamReader(input, Encoding.UTF8, true, 81920, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (output == null)
                    {
                        output = Open(path);
                        if (output == null) continue;
                    }

                    try
                    {
                        // Format service output through the same timestamp/redaction
                        // rules as the tray and helper logs. Use the Windows line ending:
                        // the tray's TextBox and Notepad only treat CRLF as a line break.
                        byte[] bytes = Encoding.UTF8.GetBytes(
                            LogWriter.FormatLine(line) + Environment.NewLine);
                        if (output.Length > 0 && output.Length + bytes.Length > maxBytes)
                        {
                            output.Dispose();
                            output = null;
                            Rotate(path);
                            output = Open(path);
                        }
                        if (output == null) continue;
                        output.Write(bytes, 0, bytes.Length);
                        output.Flush();
                    }
                    catch (Exception)
                    {
                        try { output.Dispose(); } catch (Exception) { }
                        output = null;
                    }
                }
            }

            if (output != null)
            {
                try { output.Dispose(); } catch (Exception) { }
            }
        }

        // Only the two locations the tray actually configures are accepted, and only
        // for the fixed server.log name. This prevents --log-host from creating or
        // rotating arbitrary files even when the process is elevated.
        internal static bool IsExpectedLogPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetFileName(full), "server.log",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if (PathSafety.ContainsReparsePoint(full)) return false;

                string dataLog = Path.Combine(
                    Path.GetFullPath(TrayState.Folder).TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    "server.log");
                string baseLog = Path.Combine(
                    Path.GetFullPath(Path.GetDirectoryName(Application.ExecutablePath))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    "server.log");
                return string.Equals(full, dataLog, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, baseLog, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The log path can be swapped for a reparse point after the initial check
        // (or between rotations). Revalidate every open so an elevated log host never
        // follows a link planted in the user-writable data directory.
        internal static bool CanOpenForAppend(string path)
        {
            using (FileStream stream = Open(path))
            {
                return stream != null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,
            StringBuilder path, uint pathLength, uint flags);

        private static FileStream Open(string path)
        {
            FileStream stream = null;
            try
            {
                if (!IsExpectedLogPath(path)) return null;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                // The path can be replaced by a reparse point between the check above
                // and CreateFile. Ask the opened handle for its final path: a handle
                // cannot be redirected afterwards, and an elevated log host must not
                // append to a target outside the expected user log directory.
                if (!HandleMatchesExpectedPath(stream, path))
                {
                    stream.Dispose();
                    return null;
                }
                return stream;
            }
            catch (Exception)
            {
                try { if (stream != null) stream.Dispose(); } catch (Exception) { }
                return null;
            }
        }

        private static bool HandleMatchesExpectedPath(FileStream stream, string expected)
        {
            try
            {
                StringBuilder buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer,
                    (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return false;
                string final = buffer.ToString();
                if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    final = @"\\" + final.Substring(8);
                else if (final.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    final = final.Substring(4);
                final = Path.GetFullPath(final);
                expected = Path.GetFullPath(expected);
                return string.Equals(final, expected, StringComparison.OrdinalIgnoreCase)
                    && !PathSafety.ContainsReparsePoint(final);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Rotate(string path)
        {
            try
            {
                string backup = path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                if (File.Exists(path)) File.Move(path, backup);
            }
            catch (Exception)
            {
            }
        }
    }
}
