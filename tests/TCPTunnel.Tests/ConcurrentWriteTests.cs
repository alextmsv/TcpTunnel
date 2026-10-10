using System.Net;
using System.Net.Sockets;
using TCPTunnel;

internal static class ConcurrentWriteTests
{
    public static async Task<bool> ConcurrentFramesSelfTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var writer = new TcpClient();
        try
        {
            await writer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var reader = await listener.AcceptTcpClientAsync(timeout.Token);
            var expected = Enumerable.Range(0, 64).Select(i => i + ":" + new string((char)('a' + i % 26), 12000)).ToHashSet();
            var receive = Task.Run(async () =>
            {
                for (int i = 0; i < 64; i++)
                    if (!expected.Remove(await MessageProtocol.ReadStringAsync(reader.GetStream(), timeout.Token))) return false;
                return expected.Count == 0;
            });
            string[] frames = Enumerable.Range(0, 64).Select(i => i + ":" + new string((char)('a' + i % 26), 12000)).ToArray();
            await Task.WhenAll(frames.Select(frame => MessageProtocol.WriteStringAsync(writer.GetStream(), frame, timeout.Token)));
            return await receive;
        }
        finally { listener.Stop(); }
    }
}
