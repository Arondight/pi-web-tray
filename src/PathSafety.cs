// Pi Web Tray — path safety helpers
//
// Elevated code must not follow a junction or symlink out of a directory that the
// caller was allowed to use. These helpers walk the existing components of a path
// and fail closed when the attributes cannot be read.

using System;
using System.Collections.Generic;
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

        // Recursive ACL repair must never walk through a junction. A standard user can
        // create a child junction in a user-writable installation directory and redirect
        // an elevated icacls /t operation into a protected directory. Fail closed if any
        // entry below the root is a reparse point, or if the tree cannot be inspected.
        internal static bool ContainsReparsePointInTree(string directory)
        {
            try
            {
                string root = Path.GetFullPath(directory);
                if (!Directory.Exists(root)) return true;

                Stack<string> pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    string current = pending.Pop();
                    FileAttributes currentAttributes = File.GetAttributes(current);
                    if ((currentAttributes & FileAttributes.ReparsePoint) != 0) return true;

                    foreach (string entry in Directory.GetFileSystemEntries(current))
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
