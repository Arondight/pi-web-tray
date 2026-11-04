// Pi Web Tray — update from GitHub releases
//
// The tray cannot replace its own executable while it is running, so the update is split
// into two processes:
//
//   1. the tray checks the latest GitHub release, downloads the Windows archive, verifies
//      its SHA-256 checksum and extracts it to a staging directory;
//   2. a helper process (the staged executable) waits for the tray to exit, replaces the
//      application files and restarts the new tray.
//
// When the installation directory is not writable by the current user (Program Files),
// the helper is started through UAC. The user's pi-web-tray.ini and logs are skipped
// during the copy, so settings and diagnostics survive the update. The partial files
// (Updater.Prepare.cs, Updater.Helper.cs, ...) split the workflow; this file keeps the
// shared types, constants and release metadata.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PiWebTray
{
    internal enum ApplyStartMode
    {
        Tray,
        TrayOnly,
        ServiceSilent
    }

    internal sealed class UpdateCopyResult
    {
        internal string Backup = "";
        internal System.Collections.Generic.List<string> Managed = new System.Collections.Generic.List<string>();
    }

    internal sealed class ReleaseInfo
    {
        internal string Tag = "";
        internal string Version = "";
        internal string ZipName = "";
        internal string ZipUrl = "";
        internal string ChecksumName = "";
        internal string ChecksumUrl = "";
        internal long ZipSize;
    }

    internal static partial class Updater
    {
        internal const string RepositoryOwner = "Arondight";
        internal const string RepositoryName = "pi-web-tray";

        private const string LatestReleaseUrl =
            "https://api.github.com/repos/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
        private const long MaxLogBytes = 4 * 1024 * 1024;
        internal const string ManifestName = "pi-web-tray.files.txt";

        private static readonly object LogLock = new object();

        [DataContract]
        private sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")]
            public string TagName { get; set; }

            [DataMember(Name = "assets")]
            public GitHubAsset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class GitHubAsset
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "browser_download_url")]
            public string BrowserDownloadUrl { get; set; }

            [DataMember(Name = "size")]
            public long Size { get; set; }
        }
    }
}
