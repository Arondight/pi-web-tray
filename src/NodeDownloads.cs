// Pi Web Tray — Node.js download links for legacy Windows
//
// pi-web currently requires Node.js 22.19+. Windows 8.1 can use the official
// Node.js 22.23.4 build, while Windows 7/8 need the community-patched build.

using System;
using System.Runtime.InteropServices;

namespace PiWebTray
{
    internal static class NodeDownloads
    {
        internal const string DefaultUrl = "https://nodejs.org/en/download";
        internal const string Windows7Or8Url = "https://github.com/DeftKing/nodejs-win7/releases/tag/v22.23.4";
        internal const string Windows81Url = "https://nodejs.org/dist/v22.23.4/";

        internal static string Url()
        {
            return UrlFor(WindowsVersion.Current);
        }

        internal static string UrlFor(Version version)
        {
            if (version == null) return DefaultUrl;
            if (version.Major != 6) return DefaultUrl;
            if (version.Minor == 1 || version.Minor == 2) return Windows7Or8Url;
            if (version.Minor == 3) return Windows81Url;
            return DefaultUrl;
        }
    }

    internal static class WindowsVersion
    {
        // Environment.OSVersion can be affected by the application manifest. RtlGetVersion
        // reports the real kernel version, which also keeps Windows 8.1 (6.3) distinct from 8 (6.2).
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
