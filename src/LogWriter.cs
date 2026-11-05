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

            lock (Sync)
            {
                try
                {
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
                    using (FileStream stream = new FileStream(
                        path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(FormatLine(line));
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
