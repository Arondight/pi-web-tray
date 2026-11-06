// Pi Web Tray — path safety helpers
//
// Elevated code must not follow a junction or symlink out of a directory that the
// caller was allowed to use. These helpers walk the existing components of a path
// and fail closed when the attributes cannot be read.

using System;
using System.IO;

namespace PiWebTray
{
    internal static class PathSafety
    {
        internal static bool ContainsReparsePoint(string path)
        {
            try
            {
                string current = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(current))
                {
                    if (Directory.Exists(current) || File.Exists(current))
                    {
                        FileAttributes attributes = File.GetAttributes(current);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                    }

                    string parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent)
                        || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    current = parent;
                }
                return false;
            }
            catch (Exception)
            {
                // A path that cannot be inspected must not be used for an elevated write.
                return true;
            }
        }
    }
}
