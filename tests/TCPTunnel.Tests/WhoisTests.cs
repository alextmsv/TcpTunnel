using TCPTunnel;
using System.Net;
using System.Net.Sockets;

internal static class WhoisTests
{
    public static async Task<bool> WhoisOwnerPublicAddressSelfTest()
    {
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var owner = typeof(ServerInterface).GetField("<AdministratorNickname>k__BackingField", flags)!;
        var address = typeof(ServerInterface).GetField("displayAddress", flags)!;
        var isPublic = typeof(ServerInterface).GetField("displayAddressIsPublic", flags)!;
        object?[] saved = { owner.GetValue(null), address.GetValue(null), isPublic.GetValue(null) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var client = new Client(await listener.AcceptTcpClientAsync(timeout.Token)) { Nickname = "testOwner" };
            client.IpAddress = ((IPEndPoint)client.TcpClient.Client.RemoteEndPoint!).Address.ToString();
            owner.SetValue(null, "TESTOWNER");
            address.SetValue(null, "203.0.113.4");
            isPublic.SetValue(null, true);
            var confirmed = WhoisServer.Snapshot(client);
            if (confirmed.Address != "203.0.113.4" || confirmed.PublicAddressUnavailable) return false;
            isPublic.SetValue(null, false);
            address.SetValue(null, "192.168.1.2");
            var unavailable = WhoisServer.Snapshot(client);
            if (unavailable.Address != "" || !unavailable.PublicAddressUnavailable) return false;
            client.Nickname = "otherUser";
            var ordinary = WhoisServer.Snapshot(client);
            return ordinary.Address == "127.0.0.1" && !ordinary.PublicAddressUnavailable;
        }
        finally
        {
            listener.Stop();
            owner.SetValue(null, saved[0]); address.SetValue(null, saved[1]); isPublic.SetValue(null, saved[2]);
        }
    }

    public static async Task<bool> WhoisMediaCountSelfTest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            await using var first = await TestPeer.Connect(listener, "whoisFirst", timeout.Token);
            await using var second = await TestPeer.Connect(listener, "whoisSecond", timeout.Token);
            await first.Send(WhoisProtocol.Hello, timeout.Token);
            await second.Send(WhoisProtocol.Hello, timeout.Token);
            await second.Send(ImageProtocol.CreateClientFrame(new ImagePacket { Width = 1, Height = 1, PackedPixels = new byte[] { 0xA0 } }), timeout.Token);
            var animation = new AnimatedImagePacket { Width = 1, Height = 1,
                PackedFrames = new[] { new byte[] { 0xA0 }, new byte[] { 0xB0 } }, FrameDelays = new ushort[] { 100, 100 } };
            string transfer = ImageAnimationProtocol.CreateTransferId();
            await second.Send(ImageAnimationProtocol.CreateClientBegin(transfer, animation), timeout.Token);
            for (int i = 0; i < 2; i++) await second.Send(ImageAnimationProtocol.CreateClientFrame(transfer, i, 100, animation.PackedFrames[i]), timeout.Token);
            await second.Send(ImageAnimationProtocol.CreateClientEnd(transfer), timeout.Token);
            await second.Send("media marker", timeout.Token);
            await first.MediaMarker.Task.WaitAsync(timeout.Token);
            var result = await first.Session.QueryAsync("whoisSecond", first.Send, timeout.Token);
            return result?.Info?.Messages == 3;
        }
        finally { listener.Stop(); }
    }

    public static async Task<bool> WhoisOverTcpSelfTest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var first = await TestPeer.Connect(listener, "whoisFirst", timeout.Token);
            await using var second = await TestPeer.Connect(listener, "whoisSecond", timeout.Token);
            await second.Send(PrivacyProtocol.Ack, timeout.Token);
            await first.Send(WhoisProtocol.Hello, timeout.Token);
            await second.Send(WhoisProtocol.Hello, timeout.Token);
            await second.Send(WhoisProtocol.Size(1024, 768), timeout.Token);
            await second.Send("first message", timeout.Token);
            await second.Send("second message", timeout.Token);
            await first.TwoMessages.Task.WaitAsync(timeout.Token);
            var result = await first.Session.QueryAsync("WHOISSECOND", first.Send, timeout.Token);
            string caller = await second.Notice.Task.WaitAsync(timeout.Token);
            if (result?.Info is not WhoisInfo info || info.Messages != 2 || info.WindowWidth != 1024 || info.WindowHeight != 768 ||
                info.Address != "127.0.0.1" || caller != "whoisFirst") return false;
            var self = await second.Session.QueryAsync("whoisSecond", second.Send, timeout.Token);
            return self?.Info?.Messages == 2 && second.Notices == 1;
        }
        finally { listener.Stop(); }
    }

    public static async Task<bool> WhoisConsentGateSelfTest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            await using var first = await TestPeer.Connect(listener, "whoisFirst", timeout.Token);
            await using var second = await TestPeer.Connect(listener, "whoisSecond", timeout.Token);
            await first.Send(WhoisProtocol.Hello, timeout.Token);
            await second.Send(WhoisProtocol.Hello, timeout.Token);
            await first.Send(PrivacyProtocol.Ack, timeout.Token);
            var before = await first.Session.QueryAsync("whoisSecond", first.Send, timeout.Token);
            if (before?.Info is not WhoisInfo hidden || !hidden.AddressHidden || hidden.Address != "") return false;
            await second.Send(PrivacyProtocol.Ack, timeout.Token);
            await second.Send("media marker", timeout.Token);
            await first.MediaMarker.Task.WaitAsync(timeout.Token);
            var after = await first.Session.QueryAsync("whoisSecond", first.Send, timeout.Token);
            return after?.Info is WhoisInfo disclosed && !disclosed.AddressHidden && disclosed.Address == "127.0.0.1";
        }
        finally { listener.Stop(); }
    }

    private sealed class TestPeer : IAsyncDisposable
    {
        private readonly TcpClient tcp;
        private readonly Client server;
        private readonly CancellationTokenSource stop;
        private readonly Task serverLoop;
        private readonly Task reader;
        internal readonly WhoisSession Session = new();
        internal readonly TaskCompletionSource<string> Notice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource TwoMessages = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource MediaMarker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Notices;

        private TestPeer(TcpClient tcp, Client server, CancellationTokenSource stop, Task serverLoop)
        {
            this.tcp = tcp; this.server = server; this.stop = stop; this.serverLoop = serverLoop;
            reader = Read();
        }
        internal static async Task<TestPeer> Connect(TcpListener listener, string nick, CancellationToken token)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
            var server = new Client(await listener.AcceptTcpClientAsync(token));
            var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            NetWorker.broadcaster.AddConnection(server);
            Task loop = NetWorker.ServerClientLoopAsync(server, stop.Token);
            var stream = tcp.GetStream();
            if (await MessageProtocol.ReadStringAsync(stream, token) != NetWorker.DO_AUTH_MESSAGE) throw new Exception("No auth challenge");
            await MessageProtocol.WriteStringAsync(stream, "REPLY:" + nick, token);
            if (await MessageProtocol.ReadStringAsync(stream, token) != NetWorker.AUTH_OK_MESSAGE) throw new Exception("Auth failed");
            return new TestPeer(tcp, server, stop, loop);
        }
        internal Task Send(string frame, CancellationToken token) => MessageProtocol.WriteStringAsync(tcp.GetStream(), frame, token);
        private async Task Read()
        {
            int messages = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    string frame = await MessageProtocol.ReadStringAsync(tcp.GetStream(), stop.Token);
                    if (await Session.ReceiveAsync(frame, Send, caller => { Interlocked.Increment(ref Notices); Notice.TrySetResult(caller); }, stop.Token)) continue;
                    if (frame.StartsWith("[whoisSecond]: ") && ++messages == 2) TwoMessages.TrySetResult();
                    if (frame == "[whoisSecond]: media marker") MediaMarker.TrySetResult();
                }
            }
            catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); tcp.Close(); server.Close();
            await Task.WhenAll(serverLoop, reader); stop.Dispose();
        }
    }

    public static async Task<bool> WhoisSizeIndependentLimitSelfTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var client = new Client(await listener.AcceptTcpClientAsync());
            client.Diagnostics.Supported = true;
            for (int i = 0; i < 100; i++) client.TryConsumeControlToken();
            await WhoisServer.HandleAsync(client, WhoisProtocol.Size(1024, 768), CancellationToken.None);
            if (client.Diagnostics.GetSize() != (1024, 768)) return false;
            await WhoisServer.HandleAsync(client, WhoisProtocol.Size(1280, 720), CancellationToken.None);
            return client.Diagnostics.GetSize() == (1280, 720);
        }
        finally { listener.Stop(); }
    }

    public static bool WhoisProtocolSelfTest()
    {
        string id = Guid.NewGuid().ToString("N");
        var source = new WhoisInfo("tester", "203.0.113.4", false, 12, 1024, 768, true, false, 75, 10, 45, 7);
        string frame = WhoisProtocol.Reply(id, source);
        if (!LegacyEventProtocol.IsControlMessage(frame) || !WhoisProtocol.TryReply(frame, out var parsedId, out var parsed) ||
            parsedId != id || parsed != source) return false;
        return !WhoisProtocol.TryReply(WhoisProtocol.Reply(id, source with { SnakeGlyph = 65581 }), out _, out _) &&
            !WhoisProtocol.TryReply(WhoisProtocol.Reply(id, source with { Messages = -1 }), out _, out _) &&
            !WhoisProtocol.TryReply(WhoisProtocol.Reply(id, source with { Address = "\u001b[0m" }), out _, out _) &&
            WhoisProtocol.TryRequest(WhoisProtocol.Request(id, "tester"), out var requestId, out var nick) &&
            requestId == id && nick == "tester";
    }

    public static bool WhoisRttCorrelationSelfTest()
    {
        var peer = new WhoisPeer();
        string probe = peer.BeginProbe();
        peer.ReceivePong(Guid.NewGuid().ToString("N"));
        if (peer.PingMilliseconds != null) return false;
        peer.ReceivePong(probe);
        int? measured = peer.PingMilliseconds;
        peer.ReceivePong(probe);
        return measured >= 0 && peer.PingMilliseconds == measured;
    }
}
