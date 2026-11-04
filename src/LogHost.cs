// Pi Web Tray — rotating log host for the service
//
// cmd keeps its redirected file handle open for the whole life of node, so the tray
// cannot rotate server.log while the service runs. The service output is piped to this
// mode instead: it owns the file handle and can rotate the log at any time.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

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
            if (!IsAllowedPath(path)) return 2;

            long maxBytes = 4 * 1024 * 1024;
            if (index + 2 < args.Length)
            {
                if (!long.TryParse(args[index + 2], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out maxBytes)
                    || maxBytes <= 0)
                {
                    return 2;
                }
            }

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
                        // Redact credentials before they reach server.log. npm can echo its
                        // registry URL (including userinfo or tokens) in error output.
                        // Use the Windows line ending: the tray's TextBox and Notepad only
                        // treat CRLF as a line break.
                        byte[] bytes = Encoding.UTF8.GetBytes(
                            Shell.RedactSecrets(line) + Environment.NewLine);
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

        private static bool IsAllowedPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string data = Path.GetFullPath(TrayState.Folder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string baseDir = Path.GetFullPath(Path.GetDirectoryName(Application.ExecutablePath))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return full.StartsWith(data, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static FileStream Open(string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            }
            catch (Exception)
            {
                return null;
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
