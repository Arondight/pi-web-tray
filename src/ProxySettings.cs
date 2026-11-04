// Pi Web Tray — optional HTTP / SOCKS5 proxy for update traffic only
//
// The Proxy setting affects Pi Web Tray update checks/downloads only. It is not
// exported to Node, npm or pi-web child processes.
// .NET Framework's WebProxy cannot speak SOCKS5. For SOCKS we run a tiny local
// HTTP CONNECT bridge and point HttpWebRequest at that loopback endpoint.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PiWebTray
{
    internal enum ProxyKind
    {
        None,
        Http,
        Socks5
    }

    internal sealed class ProxySettings
    {
        internal ProxyKind Kind;
        internal string Host = "";
        internal int Port;
        internal string User = "";
        internal string Password = "";
        internal Uri Uri;

        internal string Display
        {
            get
            {
                if (Kind == ProxyKind.None) return "";
                return (Kind == ProxyKind.Http ? "http://" : "socks5://") + Host + ":" + Port;
            }
        }

        internal static bool TryParse(string text, out ProxySettings settings, out string error)
        {
            settings = new ProxySettings();
            error = "";
            text = (text ?? "").Trim();
            if (text.Length == 0) return true;
            if (text.IndexOf("://", StringComparison.Ordinal) < 0) text = "http://" + text;

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri))
            {
                error = L.T("settings.invalidProxy");
                return false;
            }

            string scheme = (uri.Scheme ?? "").ToLowerInvariant();
            if (scheme == "http") settings.Kind = ProxyKind.Http;
            else if (scheme == "socks5" || scheme == "socks5h" || scheme == "socks")
                settings.Kind = ProxyKind.Socks5;
            else
            {
                error = L.T("settings.invalidProxy");
                return false;
            }

            if (string.IsNullOrEmpty(uri.Host))
            {
                error = L.T("settings.invalidProxy");
                return false;
            }
            if (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/")
            {
                error = L.T("settings.invalidProxy");
                return false;
            }

            settings.Host = uri.Host;
            settings.Port = uri.Port;
            if (settings.Port <= 0 || settings.Port > 65535)
            {
                settings.Port = settings.Kind == ProxyKind.Socks5 ? 1080 : 80;
            }
            settings.Uri = uri;

            string userInfo = uri.UserInfo ?? "";
            if (userInfo.Length > 0)
            {
                int colon = userInfo.IndexOf(':');
                settings.User = Uri.UnescapeDataString(colon >= 0 ? userInfo.Substring(0, colon) : userInfo);
                settings.Password = colon >= 0 ? Uri.UnescapeDataString(userInfo.Substring(colon + 1)) : "";
            }
            return true;
        }

        internal WebProxy CreateWebProxy()
        {
            UriBuilder builder = new UriBuilder("http", Host, Port);
            WebProxy proxy = new WebProxy(builder.Uri);
            proxy.BypassProxyOnLocal = false;
            if (!string.IsNullOrEmpty(User))
            {
                proxy.Credentials = new NetworkCredential(User, Password);
                proxy.UseDefaultCredentials = false;
            }
            return proxy;
        }

        internal static bool Test(string text, out string error)
        {
            ProxySettings proxy;
            if (!TryParse(text, out proxy, out error)) return false;
            if (proxy.Kind == ProxyKind.None)
            {
                error = L.T("settings.noProxy");
                return false;
            }

            try
            {
                if (proxy.Kind == ProxyKind.Http) return TestHttp(proxy, out error);
                using (TcpClient client = Socks5Client.Connect(proxy, "api.github.com", 443, 10000))
                {
                    error = "";
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TestHttp(ProxySettings proxy, out string error)
        {
            error = "";
            using (TcpClient client = ProxyNetwork.Connect(proxy.Host, proxy.Port, 10000))
            using (NetworkStream stream = client.GetStream())
            {
                StringBuilder request = new StringBuilder();
                request.Append("CONNECT api.github.com:443 HTTP/1.1\r\n");
                request.Append("Host: api.github.com:443\r\n");
                if (!string.IsNullOrEmpty(proxy.User))
                {
                    string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        proxy.User + ":" + proxy.Password));
                    request.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
                }
                request.Append("Connection: close\r\n\r\n");
                byte[] bytes = Encoding.ASCII.GetBytes(request.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();

                string status = ProxyNetwork.ReadLine(stream, 8192);
                int code;
                string[] statusParts = status == null
                    ? new string[0]
                    : status.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (status != null && status.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
                    && statusParts.Length > 1
                    && int.TryParse(statusParts[1], out code) && code >= 200 && code < 300)
                {
                    return true;
                }

                error = status ?? L.T("settings.proxyNoResponse");
                return false;
            }
        }
    }

    internal static class ProxyNetwork
    {
        internal static TcpClient Connect(string host, int port, int timeoutMs)
        {
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult result = client.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs)) throw new TimeoutException();
                client.EndConnect(result);
                client.ReceiveTimeout = timeoutMs;
                client.SendTimeout = timeoutMs;
                return client;
            }
            catch (Exception)
            {
                try { client.Close(); } catch (Exception) { }
                throw;
            }
        }

        internal static string ReadLine(Stream stream, int maxBytes)
        {
            MemoryStream line = new MemoryStream();
            while (line.Length < maxBytes)
            {
                int value = stream.ReadByte();
                if (value < 0) break;
                if (value == '\n') break;
                if (value != '\r') line.WriteByte((byte)value);
            }
            return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
        }

        internal static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }
    }

    internal static class Socks5Client
    {
        internal static TcpClient Connect(ProxySettings proxy, string host, int port, int timeoutMs)
        {
            TcpClient client = ProxyNetwork.Connect(proxy.Host, proxy.Port, timeoutMs);
            try
            {
                NetworkStream stream = client.GetStream();
                bool useAuth = !string.IsNullOrEmpty(proxy.User);
                byte[] greeting = useAuth
                    ? new byte[] { 5, 2, 0, 2 }
                    : new byte[] { 5, 1, 0 };
                stream.Write(greeting, 0, greeting.Length);
                stream.Flush();

                byte[] answer = ProxyNetwork.ReadExact(stream, 2);
                if (answer[0] != 5 || answer[1] == 0xFF)
                    throw new InvalidOperationException("socks5 method rejected");
                if (answer[1] == 2)
                {
                    byte[] user = Encoding.UTF8.GetBytes(proxy.User ?? "");
                    byte[] password = Encoding.UTF8.GetBytes(proxy.Password ?? "");
                    if (user.Length > 255 || password.Length > 255)
                        throw new InvalidOperationException("socks5 credentials too long");

                    byte[] auth = new byte[3 + user.Length + password.Length];
                    auth[0] = 1;
                    auth[1] = (byte)user.Length;
                    Array.Copy(user, 0, auth, 2, user.Length);
                    auth[2 + user.Length] = (byte)password.Length;
                    Array.Copy(password, 0, auth, 3 + user.Length, password.Length);
                    stream.Write(auth, 0, auth.Length);
                    stream.Flush();
                    byte[] authAnswer = ProxyNetwork.ReadExact(stream, 2);
                    if (authAnswer[1] != 0)
                        throw new InvalidOperationException("socks5 authentication failed");
                }
                else if (answer[1] != 0)
                {
                    throw new InvalidOperationException("socks5 method unsupported");
                }

                List<byte> request = new List<byte>();
                request.Add(5);
                request.Add(1);
                request.Add(0);

                IPAddress address;
                if (IPAddress.TryParse(host, out address))
                {
                    byte[] raw = address.GetAddressBytes();
                    request.Add(address.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1);
                    request.AddRange(raw);
                }
                else
                {
                    byte[] name = Encoding.UTF8.GetBytes(host);
                    if (name.Length > 255) throw new InvalidOperationException("socks5 host too long");
                    request.Add(3);
                    request.Add((byte)name.Length);
                    request.AddRange(name);
                }

                request.Add((byte)(port >> 8));
                request.Add((byte)(port & 0xFF));
                stream.Write(request.ToArray(), 0, request.Count);
                stream.Flush();

                byte[] reply = ProxyNetwork.ReadExact(stream, 4);
                if (reply[0] != 5 || reply[1] != 0)
                    throw new InvalidOperationException("socks5 connect failed (" + reply[1] + ")");
                int addressType = reply[3];
                if (addressType == 1) ProxyNetwork.ReadExact(stream, 4 + 2);
                else if (addressType == 4) ProxyNetwork.ReadExact(stream, 16 + 2);
                else if (addressType == 3)
                {
                    int length = ProxyNetwork.ReadExact(stream, 1)[0];
                    ProxyNetwork.ReadExact(stream, length + 2);
                }
                else throw new InvalidOperationException("socks5 bad address type");

                return client;
            }
            catch (Exception)
            {
                try { client.Close(); } catch (Exception) { }
                throw;
            }
        }
    }

    internal sealed class SocksHttpBridge : IDisposable
    {
        private readonly ProxySettings _proxy;
        private readonly TcpListener _listener;
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _lock = new object();
        private volatile bool _closed;

        internal int Port { get; private set; }

        internal SocksHttpBridge(ProxySettings proxy)
        {
            _proxy = proxy;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Thread worker = new Thread(delegate ()
            {
                while (!_closed)
                {
                    try
                    {
                        TcpClient client = _listener.AcceptTcpClient();
                        lock (_lock) _clients.Add(client);
                        ThreadPool.QueueUserWorkItem(Handle, client);
                    }
                    catch (Exception)
                    {
                        if (_closed) return;
                    }
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void Handle(object state)
        {
            TcpClient client = state as TcpClient;
            if (client == null) return;
            TcpClient remote = null;
            try
            {
                NetworkStream clientStream = client.GetStream();
                clientStream.ReadTimeout = 60000;
                clientStream.WriteTimeout = 60000;

                MemoryStream headerBytes = new MemoryStream();
                int matched = 0;
                while (headerBytes.Length < 65536)
                {
                    int value = clientStream.ReadByte();
                    if (value < 0) return;
                    headerBytes.WriteByte((byte)value);
                    if ((matched == 0 && value == '\r') || (matched == 1 && value == '\n')
                        || (matched == 2 && value == '\r') || (matched == 3 && value == '\n'))
                    {
                        matched++;
                        if (matched == 4) break;
                    }
                    else
                    {
                        matched = value == '\r' ? 1 : 0;
                    }
                }

                string header = Encoding.ASCII.GetString(headerBytes.ToArray());
                string[] lines = header.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;
                string[] parts = lines[0].Split(' ');
                if (parts.Length < 2 || !string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    Write(clientStream, "HTTP/1.1 405 Method Not Allowed\r\n\r\n");
                    return;
                }

                string host;
                int port;
                if (!SplitAuthority(parts[1], out host, out port)) return;
                remote = Socks5Client.Connect(_proxy, host, port, 15000);
                remote.ReceiveTimeout = 600000;
                remote.SendTimeout = 600000;
                Write(clientStream, "HTTP/1.1 200 Connection established\r\n\r\n");
                Relay(clientStream, remote.GetStream());
            }
            catch (Exception)
            {
            }
            finally
            {
                try { client.Close(); } catch (Exception) { }
                if (remote != null) try { remote.Close(); } catch (Exception) { }
                lock (_lock) _clients.Remove(client);
            }
        }

        private static bool SplitAuthority(string value, out string host, out int port)
        {
            host = "";
            port = 0;
            if (string.IsNullOrEmpty(value)) return false;
            if (value[0] == '[')
            {
                int end = value.IndexOf(']');
                if (end < 0 || end + 2 >= value.Length || value[end + 1] != ':') return false;
                host = value.Substring(1, end - 1);
                return int.TryParse(value.Substring(end + 2), out port) && port > 0 && port <= 65535;
            }
            int colon = value.LastIndexOf(':');
            if (colon <= 0) return false;
            host = value.Substring(0, colon);
            return int.TryParse(value.Substring(colon + 1), out port) && port > 0 && port <= 65535;
        }

        private static void Write(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void Relay(Stream client, Stream remote)
        {
            Thread upload = new Thread(delegate ()
            {
                // Do not close remote when the client side becomes idle: a large
                // download may have no upload traffic for much longer than its timeout.
                try { Copy(client, remote); } catch (Exception) { }
            });
            Thread download = new Thread(delegate ()
            {
                try { Copy(remote, client); } catch (Exception) { }
                try { client.Close(); } catch (Exception) { }
            });
            upload.IsBackground = true;
            download.IsBackground = true;
            upload.Start();
            download.Start();
            // The download direction determines when the TLS/HTTP response is done.
            // Waiting on upload first would truncate large updates after a fixed timeout.
            download.Join(10 * 60 * 1000);
            try { client.Close(); } catch (Exception) { }
            try { remote.Close(); } catch (Exception) { }
            upload.Join(5000);
        }

        private static void Copy(Stream input, Stream output)
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                output.Flush();
            }
        }

        public void Dispose()
        {
            _closed = true;
            try { _listener.Stop(); } catch (Exception) { }
            lock (_lock)
            {
                foreach (TcpClient client in _clients)
                {
                    try { client.Close(); } catch (Exception) { }
                }
                _clients.Clear();
            }
        }
    }

    internal static class ProxyTransport
    {
        private static readonly object Sync = new object();
        private static ProxySettings _current;
        private static SocksHttpBridge _bridge;

        internal static bool Configure(string value, out string error)
        {
            ProxySettings next;
            if (!ProxySettings.TryParse(value, out next, out error)) return false;

            lock (Sync)
            {
                if (_current != null && _current.Display == next.Display
                    && _current.User == next.User && _current.Password == next.Password)
                {
                    return true;
                }

                if (_bridge != null)
                {
                    try { _bridge.Dispose(); } catch (Exception) { }
                    _bridge = null;
                }
                _current = next;
                if (next.Kind == ProxyKind.Socks5)
                {
                    try { _bridge = new SocksHttpBridge(next); }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        _current = null;
                        return false;
                    }
                }
                error = "";
                return true;
            }
        }

        internal static void Apply(HttpWebRequest request)
        {
            ProxySettings current;
            SocksHttpBridge bridge;
            lock (Sync)
            {
                current = _current;
                bridge = _bridge;
            }
            if (current == null || current.Kind == ProxyKind.None) return;
            if (current.Kind == ProxyKind.Http)
            {
                request.Proxy = current.CreateWebProxy();
                return;
            }
            if (bridge == null) throw new InvalidOperationException("socks proxy is not available");
            WebProxy local = new WebProxy(new Uri("http://127.0.0.1:" + bridge.Port));
            local.BypassProxyOnLocal = false;
            request.Proxy = local;
        }
    }
}
