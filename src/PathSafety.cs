// Pi Web Tray — path safety helpers
//
// Elevated code must not follow a junction or symlink out of a directory that the
// caller was allowed to use. These helpers walk the existing components of a path
// and fail closed when the attributes cannot be read.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,
            StringBuilder path, uint pathLength, uint flags);

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


        // Resolve a local path to the spelling Windows uses for the same file
        // object. This expands 8.3 names and follows junctions, so two aliases of
        // one installation share an update lock and a system-directory decision.
        // Missing or remote paths fall back to Path.GetFullPath: opening a network
        // path here could hang Explorer or contact a server unexpectedly.
        internal static string CanonicalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return path; }
            try
            {
                if (IsRemoteOrDevice(full)) return full;
                using (SafeFileHandle handle = CreateFileW(full, 0,
                    FileShareReadWriteDelete, IntPtr.Zero, OpenExisting,
                    FileFlagBackupSemantics, IntPtr.Zero))
                {
                    if (handle.IsInvalid) return full;
                    StringBuilder buffer = new StringBuilder(1024);
                    uint length = GetFinalPathNameByHandleW(handle, buffer,
                        (uint)buffer.Capacity, 0);
                    if (length == 0) return full;
                    if (length >= buffer.Capacity)
                    {
                        buffer = new StringBuilder((int)length + 1);
                        length = GetFinalPathNameByHandleW(handle, buffer,
                            (uint)buffer.Capacity, 0);
                        if (length == 0) return full;
                    }
                    string final = buffer.ToString();
                    if (final.StartsWith(@"\\?\UNC\",
                            StringComparison.OrdinalIgnoreCase))
                        final = @"\\" + final.Substring(8);
                    else if (final.StartsWith(@"\\?\",
                                 StringComparison.OrdinalIgnoreCase))
                        final = final.Substring(4);
                    return Path.GetFullPath(final);
                }
            }
            catch (Exception)
            {
                return full;
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

        // Directory identity is used where an 8.3 alias or the same local spelling
        // should be accepted. Junction aliases are rejected conservatively by the
        // reparse check below; a hard link to the executable in a different directory
        // must not be accepted either.
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

        // Resolve the parent directory and keep the final file name. This is the
        // fallback for a newly created file opened with FileShare.None, where a
        // second handle cannot be opened for the identity check but the parent
        // directory can still be resolved through a subst drive or an 8.3 alias.
        internal static string CanonicalParentPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string parent = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(parent)) return full;
                string canonical = CanonicalPath(parent);
                if (string.IsNullOrEmpty(canonical)) return full;
                return Path.Combine(canonical, Path.GetFileName(full));
            }
            catch (Exception)
            {
                return "";
            }
        }

        // Compares an already-opened handle with a path. This is used where the
        // caller has proved that the path is the intended name (for example a subst
        // drive or an 8.3 alias) and only the spelling differs from the final path.
        // The caller must still reject reparse points and hard links where those
        // matter; this helper only establishes file identity.
        internal static bool SameFile(SafeFileHandle handle, string path)
        {
            if (handle == null || handle.IsInvalid || string.IsNullOrEmpty(path)) return false;
            try
            {
                using (FileStream other = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return SameFile(handle, other.SafeFileHandle);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool SameFile(SafeFileHandle first, SafeFileHandle second)
        {
            if (first == null || first.IsInvalid || second == null || second.IsInvalid)
                return false;
            try
            {
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

        // A task command is a trust identity, not merely a check that two names
        // resolve to one file object. A hard link is another directory entry a user
        // can replace while the original file remains trusted, so reject it here.
        internal static bool SameSingleLinkLocalFile(string left, string right)
        {
            if (!SameLocalFile(left, right)) return false;
            return HasSingleLink(left) && HasSingleLink(right);
        }

        private static bool HasSingleLink(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return !HasMultipleLinks(stream.SafeFileHandle);
                }
            }
            catch (Exception)
            {
                return false;
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
        // Recursively delete a directory without ever following a reparse point.
        // A junction/symlink entry is removed as a link; its target is untouched.
        // Directory.Delete(recursive) aborts on a child junction, so a planted link
        // would otherwise block cleanup of that tree forever. Returns false when
        // anything could not be removed; the caller keeps any recovery markers and
        // retries on a later cleanup pass.
        internal static bool DeleteTreeSafe(string directory)
        {
            try
            {
                if (string.IsNullOrEmpty(directory)) return false;
                string root = Path.GetFullPath(directory);
                FileAttributes rootAttributes = File.GetAttributes(root);
                if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    // The root itself is a link: remove only the link entry.
                    if ((rootAttributes & FileAttributes.Directory) != 0)
                        Directory.Delete(root, false);
                    else
                        File.Delete(root);
                    return true;
                }

                List<string> directories = new List<string>();
                Stack<string> pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    string current = pending.Pop();
                    directories.Add(current);
                    foreach (string entry in Directory.GetFileSystemEntries(current))
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            // Remove the link itself; never recurse into its target.
                            if ((attributes & FileAttributes.Directory) != 0)
                                Directory.Delete(entry, false);
                            else
                                File.Delete(entry);
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else File.Delete(entry);
                    }
                }

                // Children were discovered after their parents; delete in reverse so
                // every directory is empty before it is removed. Re-check attributes
                // at delete time: a link planted after the walk must not be followed.
                for (int i = directories.Count - 1; i >= 0; i--)
                {
                    FileAttributes attributes = File.GetAttributes(directories[i]);
                    if ((attributes & FileAttributes.ReparsePoint) != 0
                        && (attributes & FileAttributes.Directory) == 0)
                    {
                        File.Delete(directories[i]);
                    }
                    else
                    {
                        Directory.Delete(directories[i], false);
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
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
