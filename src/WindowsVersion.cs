// Pi Web Tray — real OS version via RtlGetVersion.

using System;
using System.Runtime.InteropServices;

namespace PiWebTray
{
    internal static class WindowsVersion
    {
        // Environment.OSVersion can be affected by the application manifest. RtlGetVersion
        // reports the real kernel version, which also keeps Windows 8.1 (6.3) distinct from older 6.x versions.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfoEx
        {
            internal int Size;
            internal int Major;
            internal int Minor;
            internal int Build;
            internal int PlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string ServicePack;

            internal ushort ServicePackMajor;
            internal ushort ServicePackMinor;
            internal ushort SuiteMask;
            internal byte ProductType;
            internal byte Reserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OsVersionInfoEx versionInfo);

        internal static Version Current
        {
            get
            {
                try
                {
                    OsVersionInfoEx info = new OsVersionInfoEx();
                    info.Size = Marshal.SizeOf(typeof(OsVersionInfoEx));
                    if (RtlGetVersion(ref info) == 0 && info.Major > 0)
                        return new Version(info.Major, info.Minor, info.Build);
                }
                catch (Exception)
                {
                }
                return Environment.OSVersion.Version;
            }
        }
    }
}
