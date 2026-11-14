// Pi Web Tray — shared timestamped log writer
//
// Update, install and helper processes append through this writer so every line
// has the same timestamp, redaction and rotation rules. The log host keeps its
// own open stream for performance but formats each service line through the
// same helper.

using System;
using System.IO;
using System.Text;

namespace PiWebTray
{
    internal static class LogWriter
    {
        internal const long MaxBytes = 4 * 1024 * 1024;
        private static readonly object Sync = new object();

        internal static string FormatLine(string line)
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " "
                + Shell.RedactSecrets(line ?? "");
        }

        internal static void Append(string path, string line)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                AppendRaw(path, FormatLine(line) + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging must never take down the caller.
            }
        }

        // Append text without adding a timestamp. Crash reporting uses this to keep
        // its own header while sharing the path/handle/rotation checks.
        internal static void AppendRaw(string path, string text)
        {
            if (string.IsNullOrEmpty(path)) return;

            lock (Sync)
            {
                try
                {
                    // This writer is also called from an elevated tray and helper. A
                    // symlink at a known log name must not turn a log append into an
                    // elevated write to a protected file.
                    if (PathSafety.ContainsReparsePoint(path)) return;

                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string backup = path + ".1";
                        try
                        {
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(path, backup);
                        }
                        catch (Exception)
                        {
                            // A viewer or another process may still hold the file.
                            // Keep appending instead of dropping this line.
                        }
                    }

                    // FileShare.ReadWrite lets the tray and an elevated helper append
                    // concurrently without either side truncating the other's line.
                    // Verify the opened handle's final path as well: a reparse point can
                    // replace the path between the check above and FileStream's open.
                    using (FileStream stream = new FileStream(
                        path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        if (!LogHost.HandleMatchesExpectedPath(stream, path)) return;
                        using (StreamWriter writer = new StreamWriter(
                            stream, new UTF8Encoding(false)))
                        {
                            writer.Write(text ?? "");
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
