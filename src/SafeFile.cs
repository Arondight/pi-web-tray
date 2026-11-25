// Pi Web Tray — verified local file I/O
//
// The configuration, runtime state, update manifest and diagnostics all live in
// user-writable directories. An elevated process must not read or write through a
// symlink/junction/hard link planted at a known name. These helpers centralize the
// shared open-and-verify dance so every caller applies the same checks; callers keep
// their own atomic-replace, backup-retention and owner-repair rules.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PiWebTray
{
    internal static class SafeFile
    {
        // Read lines through a verified handle. Returns null when the path is missing,
        // crosses a reparse point, is a hard link, or cannot be opened/verified.
        internal static string[] ReadVerifiedLines(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)
                    || PathSafety.ContainsReparsePoint(path)) return null;
                using (FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (!LogHost.HandleMatchesExpectedPath(stream, path)) return null;
                    if (PathSafety.HasMultipleLinks(stream.SafeFileHandle)) return null;
                    List<string> lines = new List<string>();
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) lines.Add(line);
                    }
                    return lines.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Read the whole text through a verified handle; null on any failure.
        internal static string ReadVerifiedText(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)
                    || PathSafety.ContainsReparsePoint(path)) return null;
                using (FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (!LogHost.HandleMatchesExpectedPath(stream, path)) return null;
                    if (PathSafety.HasMultipleLinks(stream.SafeFileHandle)) return null;
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Create a new file through a verified handle and write text. CreateNew (not
        // Create) is essential: Create would follow a link planted between the path
        // check and the open and truncate its target before the handle check runs.
        // On a handle/path mismatch the partial file is removed and false returned.
        internal static bool WriteVerifiedText(string path, string content, bool emitBom)
        {
            FileStream stream = null;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                if (!LogHost.HandleMatchesExpectedPath(stream, path))
                {
                    stream.Dispose();
                    stream = null;
                    try { File.Delete(path); } catch (Exception) { }
                    return false;
                }
                using (StreamWriter writer = new StreamWriter(
                    stream, new UTF8Encoding(emitBom)))
                {
                    writer.Write(content);
                }
            }
            finally
            {
                if (stream != null)
                {
                    try { stream.Dispose(); } catch (Exception) { }
                }
            }
            return true;
        }

        // Byte counterpart of WriteVerifiedText for non-text payloads (icons).
        internal static bool WriteVerifiedBytes(string path, byte[] bytes)
        {
            FileStream stream = null;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                if (!LogHost.HandleMatchesExpectedPath(stream, path))
                {
                    stream.Dispose();
                    stream = null;
                    try { File.Delete(path); } catch (Exception) { }
                    return false;
                }
                stream.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                if (stream != null)
                {
                    try { stream.Dispose(); } catch (Exception) { }
                }
            }
            return true;
        }
    }
}
