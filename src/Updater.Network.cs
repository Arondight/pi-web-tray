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
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                error = DescribeWebError(ex);
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
            if (string.IsNullOrEmpty(path)) return;

            lock (LogLock)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        string backup = path + ".1";
                        try
                        {
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(path, backup);
                        }
                        catch (Exception)
                        {
                            // The other process may still have the log open. Keep
                            // appending instead of dropping this line.
                        }
                    }

                    // File.AppendAllText opens with FileShare.Read, which conflicts with
                    // the tray and the helper writing at the same time. FileShare.ReadWrite
                    // keeps the launch and apply lines from both processes.
                    using (FileStream stream = new FileStream(
                        path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + Shell.RedactSecrets(line));
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
