// Pi Web Tray — path safety helpers
//
// Elevated code must not follow a junction or symlink out of a directory that the
// caller was allowed to use. These helpers walk the existing components of a path
// and fail closed when the attributes cannot be read.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiWebTray
{
    internal static class PathSafety
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file,
            out ByHandleFileInformation information);

        internal static bool ContainsReparsePoint(string path)
        {
            try
            {
                string current = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(current))
                {
                    // Query attributes directly. File.Exists/Directory.Exists return
                    // false for some dangling reparse points, which would skip the
                    // link and let a later write follow it.
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(current);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                    }
                    catch (FileNotFoundException)
                    {
                        // This component does not exist yet; check its parent.
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // This component does not exist yet; check its parent.
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


        // A hard link is not a reparse point but is still another directory entry
        // for the same file. Refuse operations that could act on the other name.
        internal static bool HasMultipleLinks(SafeFileHandle handle)
        {
            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information)) return true;
                return information.NumberOfLinks > 1;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // A recursive icacls operation follows hard links because they are just
        // additional directory entries for the same file object. Such a link can
        // redirect an elevated ACL reset/owner change to a protected file, and it is
        // not visible as a reparse point. Refuse the repair when any file has more
        // than one link, or when the tree cannot be inspected.
        internal static bool ContainsHardLinkInTree(string directory)
        {
            try
            {
                string root = Path.GetFullPath(directory);
                if (!Directory.Exists(root)) return true;

                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    FileAttributes attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                    using (FileStream stream = new FileStream(file, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (HasMultipleLinks(stream.SafeFileHandle)) return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
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
