// Pi Web Tray — network and log helpers
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
        private static HttpWebRequest CreateRequest(string url, int timeoutMs, bool githubApi)
        {
            // GitHub requires TLS 1.2. Windows 7 only enables it when the runtime and
            // system policy allow it; requesting it explicitly is harmless elsewhere.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "pi-web-tray/" + Program.Version;
            if (githubApi) request.Accept = "application/vnd.github+json";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.AllowAutoRedirect = true;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            ProxyTransport.Apply(request);
            return request;
        }

        private static string GetText(string url, int timeoutMs, bool githubApi,
            UpdateCancellation cancellation, out string error)
        {
            error = "";
            HttpWebRequest request = null;
            try
            {
                request = CreateRequest(url, timeoutMs, githubApi);
                if (cancellation != null) cancellation.Register(request);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    // AllowAutoRedirect is enabled so GitHub's CDN can serve release
                    // assets, but the final host must still be a trusted GitHub host.
                    if (!IsTrustedUrl(response.ResponseUri.AbsoluteUri))
                    {
                        error = L.T("update.error.untrustedUrl");
                        return null;
                    }

                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return ReadLimited(reader, 1024 * 1024);
                    }
                }
            }
            catch (WebException ex)
            {
                error = DescribeWebError(ex);
                return null;
            }
            catch (InvalidDataException)
            {
                error = L.T("update.error.badResponse");
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                if (cancellation != null) cancellation.Release(request);
            }
        }

        private static string ReadLimited(StreamReader reader, int maxChars)
        {
            StringBuilder text = new StringBuilder();
            char[] buffer = new char[8192];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (text.Length + read > maxChars)
                    throw new InvalidDataException("response is too large");
                text.Append(buffer, 0, read);
            }
            return text.ToString();
        }

        private static string DescribeWebError(WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                try
                {
                    int code = (int)response.StatusCode;
                    if (code == 403) return L.T("update.error.rateLimit");
                    if (code == 404) return L.T("update.error.notFound");
                    if (code >= 500) return L.T("update.error.server");
                    return L.T("update.error.http", code);
                }
                catch (Exception)
                {
                    return L.T("update.error.network");
                }
                finally
                {
                    try { response.Close(); } catch (Exception) { }
                }
            }

            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.ProxyNameResolutionFailure:
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.Timeout:
                    return L.T("update.error.network");
                default:
                    return ex.Message;
            }
        }

        internal static void AppendLog(string path, string line)
        {
            LogWriter.Append(path, line);
        }
    }
}
