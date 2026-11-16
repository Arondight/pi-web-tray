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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName,
            uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        private const uint FileShareReadWriteDelete = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;

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

        // Directory identity is used where a junction/8.3 alias should be accepted,
        // but a hard link to the executable in a different directory must not be.
        internal static bool SameDirectory(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            try
            {
                if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            if (IsRemoteOrDevice(left) || IsRemoteOrDevice(right)) return false;
            // Do not follow a junction to a network/device location just to
            // compare directory identities. Normal local directories are unaffected.
            if (ContainsReparsePoint(left) || ContainsReparsePoint(right)) return false;

            SafeFileHandle first = null;
            SafeFileHandle second = null;
            try
            {
                first = CreateFileW(left, 0, FileShareReadWriteDelete, IntPtr.Zero,
                    OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
                second = CreateFileW(right, 0, FileShareReadWriteDelete, IntPtr.Zero,
                    OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
                if (first.IsInvalid || second.IsInvalid) return false;

                ByHandleFileInformation a;
                ByHandleFileInformation b;
                if (!GetFileInformationByHandle(first, out a)
                    || !GetFileInformationByHandle(second, out b))
                {
                    return false;
                }
                return a.VolumeSerialNumber == b.VolumeSerialNumber
                    && a.FileIndexHigh == b.FileIndexHigh
                    && a.FileIndexLow == b.FileIndexLow;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (first != null) first.Dispose();
                if (second != null) second.Dispose();
            }
        }

        private static bool IsRemoteOrDevice(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                {
                    if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith(@"\\?\GLOBALROOT",
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                    return false;
                }
                if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
                if (full.StartsWith(@"\??\", StringComparison.Ordinal)) return true;
                try
                {
                    string root = Path.GetPathRoot(full);
                    if (!string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':')
                    {
                        DriveInfo drive = new DriveInfo(root);
                        if (drive.DriveType == DriveType.Network) return true;
                    }
                }
                catch (Exception)
                {
                    // An unreadable drive is not a safe local path.
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // Compares two names for the same file object. A process image path can be
        // reported through a short 8.3 name, a junction, or a UNC alias, so a plain
        // string comparison can mistake our own process for a foreign executable.
        internal static bool SameFile(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            try
            {
                if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            // A task or state file can supply a UNC/device path. Never turn a
            // comparison into an implicit network/device open.
            if (IsRemoteOrDevice(left) || IsRemoteOrDevice(right)) return false;

            try
            {
                using (FileStream first = new FileStream(left, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (FileStream second = new FileStream(right, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    ByHandleFileInformation a;
                    ByHandleFileInformation b;
                    if (!GetFileInformationByHandle(first.SafeFileHandle, out a) ||
                        !GetFileInformationByHandle(second.SafeFileHandle, out b))
                    {
                        return false;
                    }
                    return a.VolumeSerialNumber == b.VolumeSerialNumber &&
                        a.FileIndexHigh == b.FileIndexHigh &&
                        a.FileIndexLow == b.FileIndexLow;
                }
            }
            catch (Exception)
            {
                // The caller must fall back to a conservative path comparison.
                return false;
            }
        }

        // Like SameFile, but refuses reparse-point paths. Used for paths supplied by
        // task/state/command-line data where opening a junction could escape to a
        // network or device location.
        internal static bool SameLocalFile(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            try
            {
                if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            if (IsRemoteOrDevice(left) || IsRemoteOrDevice(right)) return false;
            if (ContainsReparsePoint(left) || ContainsReparsePoint(right)) return false;
            return SameFile(left, right);
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
