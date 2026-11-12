// Pi Web Tray — release metadata and version checks
//
// Partial of Updater. Kept separate so the update workflow stays readable.

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
    internal static partial class Updater
    {
        internal static ReleaseInfo FindLatest(out string error)
        {
            return FindLatest(null, null, out error);
        }

        internal static ReleaseInfo FindLatest(UpdateCancellation cancellation, out string error)
        {
            return FindLatest(null, cancellation, out error);
        }

        internal static ReleaseInfo FindLatest(Config config, UpdateCancellation cancellation,
            out string error)
        {
            error = "";
            if (!ProxyTransport.Configure(config == null ? "" : config.Proxy, out error))
                return null;
            string json = GetText(LatestReleaseUrl, 20000, true, cancellation, out error);
            if (json == null) return null;
            return ParseRelease(json, out error);
        }

        internal static ReleaseInfo ParseRelease(string json, out string error)
        {
            error = "";

            GitHubRelease release;
            try
            {
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
                    release = serializer.ReadObject(stream) as GitHubRelease;
                }
            }
            catch (Exception)
            {
                error = L.T("update.error.badResponse");
                return null;
            }

            if (release == null || string.IsNullOrEmpty(release.TagName))
            {
                error = L.T("update.error.badResponse");
                return null;
            }

            ReleaseInfo info = new ReleaseInfo();
            info.Tag = release.TagName;
            info.Version = NormalizeVersion(release.TagName);

            if (release.Assets != null)
            {
                foreach (GitHubAsset asset in release.Assets)
                {
                    if (asset == null || string.IsNullOrEmpty(asset.Name) || string.IsNullOrEmpty(asset.BrowserDownloadUrl))
                        continue;
                    if (!IsWindowsArchive(asset.Name)) continue;
                    info.ZipName = asset.Name;
                    info.ZipUrl = asset.BrowserDownloadUrl;
                    info.ZipSize = asset.Size;
                    break;
                }
            }

            if (info.ZipUrl.Length == 0)
            {
                error = L.T("update.error.noAsset");
                return null;
            }

            if (release.Assets != null)
            {
                foreach (GitHubAsset asset in release.Assets)
                {
                    if (asset == null || string.IsNullOrEmpty(asset.Name) || string.IsNullOrEmpty(asset.BrowserDownloadUrl))
                        continue;
                    if (!string.Equals(asset.Name, info.ZipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                        continue;
                    info.ChecksumName = asset.Name;
                    info.ChecksumUrl = asset.BrowserDownloadUrl;
                    break;
                }
            }

            if (info.ChecksumUrl.Length == 0)
            {
                error = L.T("update.error.noChecksum");
                return null;
            }

            if (!IsTrustedUrl(info.ZipUrl) || !IsTrustedUrl(info.ChecksumUrl))
            {
                error = L.T("update.error.untrustedUrl");
                return null;
            }

            return info;
        }

        internal static string NormalizeVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            string text = tag.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            return text;
        }

        internal static bool IsNewer(string latest, string current)
        {
            if (string.IsNullOrEmpty(latest)) return false;
            if (string.IsNullOrEmpty(current) || string.Equals(current, "dev", StringComparison.OrdinalIgnoreCase))
                return true;
            return VersionUtil.Compare(latest, current) > 0;
        }

        internal static bool IsWindowsArchive(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0) return false;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
            if (!name.StartsWith("pi-web-tray-", StringComparison.OrdinalIgnoreCase)) return false;
            return name.EndsWith("-win.zip", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsTrustedUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

            string host = uri.Host.ToLowerInvariant();
            if (host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal)) return true;
            if (host == "githubusercontent.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal))
                return true;
            if (host == "githubassets.com" || host.EndsWith(".githubassets.com", StringComparison.Ordinal))
                return true;
            return false;
        }

        internal static string ParseChecksum(string text)
        {
            return ParseChecksum(text, null);
        }

        // When the checksum file contains more than one entry, only accept the line
        // whose filename matches the archive the caller selected. A checksum for a
        // different asset must not be used as the archive's integrity check.
        internal static string ParseChecksum(string text, string expectedFileName)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (string rawLine in text.Replace("\r", "").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                int end = 0;
                while (end < line.Length && IsHex(line[end])) end++;
                if (end != 64) continue;
                if (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != '*') continue;

                string name = line.Substring(end).Trim();
                if (name.StartsWith("*", StringComparison.Ordinal))
                    name = name.Substring(1).Trim();
                if (!string.IsNullOrEmpty(expectedFileName) && name.Length == 0)
                {
                    // A caller that selected a specific archive requires the
                    // checksum line to name that archive. A bare hash is too
                    // ambiguous when the server returns several lines.
                    continue;
                }
                if (!string.IsNullOrEmpty(expectedFileName) && name.Length > 0
                    && !string.Equals(Path.GetFileName(name), expectedFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line.Substring(0, end).ToLowerInvariant();
            }
            return "";
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
