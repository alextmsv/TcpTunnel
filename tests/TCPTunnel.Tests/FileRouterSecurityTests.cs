using System.Diagnostics;
using System.Net;
using TCPTunnel;

internal static class FileRouterSecurityTests
{
    private sealed class Link(Stream stream) : IChatConnection
    {
        public Stream Stream => stream;
        public IPAddress? RemoteIpAddress => null;
        public ChatTransport Transport => ChatTransport.Bluetooth;
        public void Dispose() => stream.Dispose();
    }
    private sealed class BlockedStream : MemoryStream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
    }
    internal static async Task<bool> FileRouterSlowRecipientIsolationSelfTest()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var goodLink = MemoryDuplexStream.CreatePair(); using var blocked = new BlockedStream();
        using var sender = new Client(new Link(new MemoryStream()));
        using var slow = new Client(new Link(blocked));
        using var good = new Client(new Link(goodLink.First));
        var clients = new[] { sender, slow, good };
        try
        {
            foreach (var peer in clients) { NetWorker.broadcaster.AddConnection(peer); peer.SupportsFileTransfer = true; }
            if (!NetWorker.broadcaster.TryAuthenticate(sender, "routeSender") || !NetWorker.broadcaster.TryAuthenticate(slow, "routeSlow") || !NetWorker.broadcaster.TryAuthenticate(good, "routeGood")) return false;
            var clock = Stopwatch.StartNew();
            await FileTransferRouter.RouteAsync(sender, FileTransferProtocol.Encode(new("hello")), stop.Token);
            await blocked.Started.Task.WaitAsync(stop.Token);
            string first = await MessageProtocol.ReadStringAsync(goodLink.Second, stop.Token);
            if (clock.Elapsed > TimeSpan.FromSeconds(1) || !FileTransferProtocol.TryParse(first, out var packet) || packet.Sender != "routeSender") return false;
            await FileTransferRouter.RouteAsync(sender, FileTransferProtocol.Encode(new("hello", Sender: "victim")), stop.Token);
            await FileTransferRouter.RouteAsync(sender, FileTransferProtocol.Encode(new("hello", Target: "routeGood")), stop.Token);
            string second = await MessageProtocol.ReadStringAsync(goodLink.Second, stop.Token);
            return sender.IsReady && FileTransferProtocol.TryParse(second, out packet) && packet.Sender == "routeSender" && packet.Target == "routeGood";
        }
        finally { stop.Cancel(); foreach (var peer in clients) NetWorker.broadcaster.RemoveClient(peer); goodLink.Second.Dispose(); }
    }
}
