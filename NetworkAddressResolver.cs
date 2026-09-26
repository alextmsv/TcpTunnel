using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    internal sealed class HubAddressResolution
    {
        public HubAddressResolution(string address, bool isPublic)
        {
            Address = address;
            IsPublic = isPublic;
        }

        public string Address { get; }
        public bool IsPublic { get; }
    }

    internal static class NetworkAddressResolver
    {
        private static readonly Uri PublicIPv4Endpoint = new Uri("https://api.ipify.org/");
        private static readonly TimeSpan PublicIPv4Timeout = TimeSpan.FromSeconds(3);
        private static readonly HttpClient PublicIPv4Client = CreatePublicIPv4Client();

        public static async Task<HubAddressResolution> ResolveHubAddressAsync(
            CancellationToken cancellationToken)
        {
            string localAddress = GetLocalIPv4Address();
            if (!NetworkInterface.GetIsNetworkAvailable())
                return new HubAddressResolution(localAddress, false);

            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(PublicIPv4Timeout);
                    string response = await PublicIPv4Client
                        .GetStringAsync(PublicIPv4Endpoint, timeout.Token)
                        .ConfigureAwait(false);

                    IPAddress address;
                    if (IPAddress.TryParse(response.Trim(), out address) && IsPublicIPv4(address))
                        return new HubAddressResolution(address.ToString(), true);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Определение публичного адреса превысило собственный короткий таймаут.
            }
            catch (HttpRequestException)
            {
                // Интернет или внешний сервис определения адреса недоступен.
            }
            catch (SocketException)
            {
                // DNS либо сетевой маршрут недоступен.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new HubAddressResolution(localAddress, false);
        }

        public static bool IsLoopback(IPAddress address)
        {
            if (address == null)
                return false;

            return IPAddress.IsLoopback(NormalizeAddress(address));
        }

        public static string FormatEndpoint(IPEndPoint endpoint)
        {
            if (endpoint == null)
                return "?";

            IPAddress address = NormalizeAddress(endpoint.Address);
            string host = address.AddressFamily == AddressFamily.InterNetworkV6
                ? "[" + address + "]"
                : address.ToString();
            return host + ":" + endpoint.Port;
        }

        public static string NormalizeAddressText(IPAddress address)
        {
            return address == null ? "?" : NormalizeAddress(address).ToString();
        }

        public static bool RunSelfTest()
        {
            IPAddress mappedLoopback = IPAddress.Parse("::ffff:127.0.0.1");
            return IsLoopback(mappedLoopback) &&
                   NormalizeAddressText(mappedLoopback) == "127.0.0.1" &&
                   FormatEndpoint(new IPEndPoint(mappedLoopback, 9999)) == "127.0.0.1:9999" &&
                   FormatEndpoint(new IPEndPoint(IPAddress.IPv6Loopback, 9999)) == "[::1]:9999" &&
                   IsPublicIPv4(IPAddress.Parse("8.8.8.8")) &&
                   !IsPublicIPv4(IPAddress.Parse("192.168.1.1"));
        }

        private static HttpClient CreatePublicIPv4Client()
        {
            var client = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TCPTunnel/1.0");
            return client;
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        internal static string GetLocalIPv4Address()
        {
            try
            {
                using (var routeProbe = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp))
                {
                    routeProbe.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53));
                    var endpoint = routeProbe.LocalEndPoint as IPEndPoint;
                    if (endpoint != null && IsUsableLocalIPv4(endpoint.Address))
                        return endpoint.Address.ToString();
                }
            }
            catch (SocketException)
            {
            }

            try
            {
                IPAddress routedAddress = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                                      network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .Select(network => network.GetIPProperties())
                    .Where(properties => properties.GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !gateway.Address.Equals(IPAddress.Any)))
                    .SelectMany(properties => properties.UnicastAddresses)
                    .Select(unicast => unicast.Address)
                    .FirstOrDefault(IsUsableLocalIPv4);
                if (routedAddress != null)
                    return routedAddress.ToString();
            }
            catch (NetworkInformationException)
            {
            }

            try
            {
                IPAddress hostAddress = Dns.GetHostAddresses(Dns.GetHostName())
                    .FirstOrDefault(IsUsableLocalIPv4);
                if (hostAddress != null)
                    return hostAddress.ToString();
            }
            catch (SocketException)
            {
            }

            return IPAddress.Loopback.ToString();
        }

        private static bool IsUsableLocalIPv4(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(address))
                return false;

            byte[] bytes = address.GetAddressBytes();
            return !(bytes[0] == 169 && bytes[1] == 254);
        }

        private static bool IsPublicIPv4(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork)
                return false;

            byte[] bytes = address.GetAddressBytes();
            return bytes[0] != 0 &&
                   bytes[0] != 10 &&
                   bytes[0] != 127 &&
                   !(bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) &&
                   !(bytes[0] == 169 && bytes[1] == 254) &&
                   !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) &&
                   !(bytes[0] == 192 && bytes[1] == 168) &&
                   bytes[0] < 224;
        }
    }
}
