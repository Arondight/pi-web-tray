// Pi Web Tray — Node.js download links for legacy Windows
//
// pi-web currently requires Node.js 22.19+. Windows 8.1 can use the official
// Node.js 22 build (Experimental), while Windows 7/8 need the community-patched
// build pinned below. The OS check lives in SysTools.cs (WindowsVersion).

using System;

namespace PiWebTray
{
    internal static class NodeDownloads
    {
        // Pinned to the newest Node.js 22 build that runs on both targets: the official
        // Windows 8.1 build (Experimental) and the community-patched Windows 7/8 build.
        // It may be newer than ProbeResult.NodeMinimum; raising the minimum (for example
        // to Node.js 24) must update this pin and the compatibility notes together.
        internal const string PinnedVersion = "22.23.4";

        internal const string DefaultUrl = "https://nodejs.org/en/download";
        internal const string Windows7Or8Url =
            "https://github.com/DeftKing/nodejs-win7/releases/tag/v" + PinnedVersion;
        internal const string Windows81Url = "https://nodejs.org/dist/v" + PinnedVersion + "/";

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
}
