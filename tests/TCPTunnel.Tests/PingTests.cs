using System.Globalization;
using System.Net;
using System.Net.Sockets;
using TCPTunnel;

internal static class PingTests
{
    public static async Task<bool> TcpPingSelfTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();
            var result = NetWorker.ping("127.0.0.1", port);
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            if (!result.Reachable || !long.TryParse(result.Milliseconds, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long elapsed) || elapsed < 0)
                return false;
        }
        finally { listener.Stop(); }
        foreach (var endpoint in new[] { ("", 9000, 100), ("localhost", 0, 100), ("localhost", 65536, 100), ("localhost", 9000, 0) })
        {
            var result = NetWorker.ping(endpoint.Item1, endpoint.Item2, endpoint.Item3);
            if (result.Reachable || result.Milliseconds != null) return false;
        }
        return true;
    }
}
