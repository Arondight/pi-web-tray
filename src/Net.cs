// Pi Web Tray — port probing and local HTTP readiness helpers.

using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PiWebTray
{
    internal static class Net
    {
        // Prefer the OS listener tables over opening a TCP connection on every 500 ms
        // tick. This is faster on Windows 7, leaves no SYN_SENT sockets behind when a
        // firewall drops probes, and still covers listeners bound to any local address.
        internal static bool IsPortOpen(int port, int timeoutMs)
        {
            if (port <= 0) return false;

            // The listener-only table is authoritative. Some Windows builds put a newly
            // created listener into the all-connections table with a bogus state (0 or
            // FIN_WAIT_1). GetActiveTcpListeners() and netstat filter on LISTEN, so they
            // miss a server that is actually accepting connections. The listener table
            // still carries the row and the owning PID, so ignore its state field.
            try
            {
                if (TcpListenerTable.IsListening(port)) return true;
            }
            catch (Exception)
            {
                // Fall back to the managed tables/connect below. A native table
                // failure must not escape into the tray timer or CLI.
            }

            try
            {
                IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
                foreach (IPEndPoint endpoint in properties.GetActiveTcpListeners())
                {
                    if (endpoint != null && endpoint.Port == port) return true;
                }
            }
            catch (Exception)
            {
                // Fall back to a real connection on unusual systems where the table is unavailable
            }

            // Last resort: both tables said no. A real loopback connection also keeps
            // working when a security filter hides the socket from the tables.
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool WaitPortClosed(int port, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (!IsPortOpen(port, 200)) return true;
                Thread.Sleep(150);
                waited += 150;
            }
            return false;
        }

        internal static string LocalIPv4Address()
        {
            try
            {
                NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();

                // Prefer the physical Ethernet/Wi-Fi adapter that actually owns
                // the default gateway. A first-up adapter or the OS best-route
                // query can otherwise select a Hyper-V/VMware virtual switch,
                // whose address is not reachable from another device.
                foreach (NetworkInterface adapter in adapters)
                {
                    if (adapter == null || !IsUsableAdapter(adapter)) continue;
                    if (adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet
                        && adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                        continue;
                    if (!HasIPv4Gateway(adapter)) continue;
                    string address = IPv4AddressOf(adapter);
                    if (!string.IsNullOrEmpty(address)) return address;
                }

                // Some VPN/remote adapters expose a default gateway but are not
                // ordinary Ethernet/Wi-Fi; accept any usable gateway after the
                // physical candidates above.
                foreach (NetworkInterface adapter in adapters)
                {
                    if (adapter == null || !IsUsableAdapter(adapter)) continue;
                    if (!HasIPv4Gateway(adapter)) continue;
                    string address = IPv4AddressOf(adapter);
                    if (!string.IsNullOrEmpty(address)) return address;
                }

                // If there is no default gateway, still prefer a physical
                // Ethernet/Wi-Fi address over a virtual switch.
                foreach (NetworkInterface adapter in adapters)
                {
                    if (adapter == null || !IsUsableAdapter(adapter)) continue;
                    if (adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet
                        && adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                        continue;
                    string address = IPv4AddressOf(adapter);
                    if (!string.IsNullOrEmpty(address) && !IsLinkLocal(address)) return address;
                }

                foreach (NetworkInterface adapter in adapters)
                {
                    if (adapter == null || !IsUsableAdapter(adapter)) continue;
                    string address = IPv4AddressOf(adapter);
                    if (!string.IsNullOrEmpty(address) && !IsLinkLocal(address)) return address;
                }

                foreach (NetworkInterface adapter in adapters)
                {
                    if (adapter == null || !IsUsableAdapter(adapter)) continue;
                    string address = IPv4AddressOf(adapter);
                    if (!string.IsNullOrEmpty(address)) return address;
                }

                foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(address))
                        return address.ToString();
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        private static bool IsUsableAdapter(NetworkInterface adapter)
        {
            return adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel;
        }

        private static bool HasIPv4Gateway(NetworkInterface adapter)
        {
            try
            {
                foreach (GatewayIPAddressInformation gateway in
                    adapter.GetIPProperties().GatewayAddresses)
                {
                    if (gateway != null && gateway.Address != null
                        && gateway.Address.AddressFamily == AddressFamily.InterNetwork
                        && !gateway.Address.Equals(IPAddress.Any))
                        return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static bool IsLinkLocal(string address)
        {
            try
            {
                byte[] bytes = IPAddress.Parse(address).GetAddressBytes();
                return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string IPv4AddressOf(NetworkInterface adapter)
        {
            try
            {
                foreach (UnicastIPAddressInformation unicast in
                    adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast == null || unicast.Address == null) continue;
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;
                    return unicast.Address.ToString();
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        internal static bool WaitPortOpen(int port, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (IsPortOpen(port, 200)) return true;
                Thread.Sleep(150);
                waited += 150;
            }
            return false;
        }

        // An open port does not mean Next.js answers yet; used after install to confirm the service works
        internal static bool IsHttpOk(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Method = "GET";
                request.AllowAutoRedirect = true;
                // 127.0.0.1 is local; a corporate/system proxy must not intercept the readiness probe
                request.Proxy = null;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    return code >= 200 && code < 400;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    try
                    {
                        int code = (int)response.StatusCode;
                        return code >= 200 && code < 400;
                    }
                    finally
                    {
                        response.Close();
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        // Last-resort pi-web identity check when the native process probe cannot read
        // the owning process.
        // This is intentionally only a fallback; a mere HTTP 200 is not enough.
        internal static bool IsPiWebHttp(int port, int timeoutMs)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:" + port + "/");
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Method = "GET";
                request.AllowAutoRedirect = true;
                request.Proxy = null;
                request.UserAgent = "pi-web-tray/" + Program.Version;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return ContainsPiWebMarker(reader);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response == null) return false;
                try
                {
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return ContainsPiWebMarker(reader);
                    }
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    response.Close();
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ContainsPiWebMarker(StreamReader reader)
        {
            char[] buffer = new char[8192];
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) return false;
            string text = new string(buffer, 0, read);
            return text.IndexOf("pi-web", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
