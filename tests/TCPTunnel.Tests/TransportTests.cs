using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using TCPTunnel;

internal static class TransportTests
{
    public static async Task<bool> StreamReceiverKickSelfTest()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string name = "tcptunnel-receive-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var peer = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task accept = server.WaitForConnectionAsync(stop.Token);
        await peer.ConnectAsync(stop.Token); await accept;
        var connection = new TestConnection(peer);
        var end = new SessionEndState();
        NetWorker.connected = true;
        Task receive = UserInterface.ReceiveConnectionMessagesAsync(connection, peer, stop.Token, end, new HubStatusSession());
        try
        {
            await MessageProtocol.WriteStringAsync(server, SystemMessageProtocol.Create(SystemMessageKind.Kicked, "pipe kick"), stop.Token);
            await receive.WaitAsync(stop.Token);
            return end.Kind == SessionEndKind.ServerReason && end.Message.Contains("pipe kick") && connection.CloseCount == 1 && !NetWorker.connected;
        }
        finally
        {
            stop.Cancel(); peer.Close(); server.Close();
            await receive.WaitAsync(TimeSpan.FromSeconds(2));
            NetWorker.connected = false;
        }
    }

    public static async Task<bool> StreamFramesSelfTest()
    {
        using var stream = new MemoryStream();
        await MessageProtocol.WriteStringAsync(stream, "Привет 🐍", default);
        await MessageProtocol.WriteStringAsync(stream, "second", default);
        using var reference = new MemoryStream();
        using (var writer = new BinaryWriter(reference, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("Привет 🐍"); writer.Write("second");
        }
        if (!stream.ToArray().SequenceEqual(reference.ToArray())) return false;
        stream.Position = 0;
        if (await MessageProtocol.ReadStringAsync(stream, default) != "Привет 🐍" ||
            await MessageProtocol.ReadStringAsync(stream, default) != "second") return false;
        try { await MessageProtocol.WriteStringAsync(stream, new string('x', 16385), default); return false; }
        catch (InvalidDataException) { return true; }
    }

    public static async Task<bool> StreamCancelledWriteClosesSelfTest()
    {
        using var stream = new PartialCancelledWriteStream();
        try { await MessageProtocol.WriteStringAsync(stream, "partial frame", default); return false; }
        catch (OperationCanceledException) { return stream.Closed && stream.LengthWritten == 1; }
    }

    public static async Task<bool> StreamMalformedFrameSelfTest()
    {
        foreach (byte[] bytes in new[] { new byte[] { 1, 0xff }, new byte[] { 0xff, 0xff, 0xff, 0xff, 0x7f }, new byte[] { 5, 65 } })
        {
            using var stream = new MemoryStream(bytes);
            try { await MessageProtocol.ReadStringAsync(stream, default); return false; }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.DecoderFallbackException or EndOfStreamException) { }
        }
        return true;
    }

    public static async Task<bool> MixedPipeTcpHubSelfTest()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string pipeName = "tcptunnel-test-" + Guid.NewGuid().ToString("N");
        using var accepted = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task wait = accepted.WaitForConnectionAsync(stop.Token);
        await pipe.ConnectAsync(stop.Token); await wait;
        var connection = new TestConnection(accepted);
        using var pipeClient = new Client(connection);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
        using var tcpClient = new Client(await listener.AcceptTcpClientAsync(stop.Token));
        NetWorker.broadcaster.AddConnection(pipeClient); NetWorker.broadcaster.AddConnection(tcpClient);
        Task pipeLoop = NetWorker.ServerClientLoopAsync(pipeClient, stop.Token);
        Task tcpLoop = NetWorker.ServerClientLoopAsync(tcpClient, stop.Token);
        try
        {
            async Task Authenticate(Stream stream, string nick)
            {
                if (await MessageProtocol.ReadStringAsync(stream, stop.Token) != NetWorker.DO_AUTH_MESSAGE) throw new IOException("No auth challenge");
                await MessageProtocol.WriteStringAsync(stream, "REPLY:" + nick, stop.Token);
                if (await MessageProtocol.ReadStringAsync(stream, stop.Token) != NetWorker.AUTH_OK_MESSAGE) throw new IOException("Authentication failed");
            }
            async Task<string> Until(Stream stream, Func<string, bool> predicate)
            {
                while (true)
                {
                    string frame = await MessageProtocol.ReadStringAsync(stream, stop.Token);
                    if (predicate(frame)) return frame;
                }
            }
            await Authenticate(pipe, "pipeUser"); await Authenticate(tcp.GetStream(), "tcpUser");
            await MessageProtocol.WriteStringAsync(tcp.GetStream(), "to pipe", stop.Token);
            await Until(pipe, f => f == "[tcpUser]: to pipe");
            await MessageProtocol.WriteStringAsync(pipe, "to tcp", stop.Token);
            await Until(tcp.GetStream(), f => f == "[pipeUser]: to tcp");
            await MessageProtocol.WriteStringAsync(pipe, HubStatusProtocol.Hello, stop.Token);
            await Until(pipe, f => f == HubStatusProtocol.Capabilities);
            string id = Guid.NewGuid().ToString("N");
            await MessageProtocol.WriteStringAsync(pipe, HubStatusProtocol.CreateRequest(id), stop.Token);
            string reply = await Until(pipe, f => HubStatusProtocol.TryParseReply(f, out _, out _, out _));
            if (!HubStatusProtocol.TryParseReply(reply, out var receivedId, out _, out int count) || receivedId != id || count != 2) return false;
            await MessageProtocol.WriteStringAsync(pipe, ImageProtocol.CreateClientFrame(new ImagePacket { Width = 1, Height = 1, PackedPixels = new byte[] { 0xa0 } }), stop.Token);
            await Until(tcp.GetStream(), f => ImageProtocol.TryParseServerFrame(f, out var image) && image.Sender == "pipeUser");
            var animation = new AnimatedImagePacket { Width = 1, Height = 1,
                PackedFrames = new[] { new byte[] { 0xa0 }, new byte[] { 0xb0 } }, FrameDelays = new ushort[] { 100, 100 } };
            string transfer = ImageAnimationProtocol.CreateTransferId();
            await MessageProtocol.WriteStringAsync(pipe, ImageAnimationProtocol.CreateClientBegin(transfer, animation), stop.Token);
            for (int i = 0; i < 2; i++) await MessageProtocol.WriteStringAsync(pipe, ImageAnimationProtocol.CreateClientFrame(transfer, i, 100, animation.PackedFrames[i]), stop.Token);
            await MessageProtocol.WriteStringAsync(pipe, ImageAnimationProtocol.CreateClientEnd(transfer), stop.Token);
            var assembler = new ImageAnimationAssembler();
            await Until(tcp.GetStream(), f => ImageAnimationProtocol.TryParseServer(f, out var control) &&
                assembler.Accept(control, out var result) != ImageAnimationAssemblyResult.Invalid && result != null && result.PackedFrames.Length == 2);
            await MessageProtocol.WriteStringAsync(pipe, WhoisProtocol.Hello, stop.Token);
            await Until(pipe, f => f == WhoisProtocol.Capabilities);
            await MessageProtocol.WriteStringAsync(pipe, WhoisProtocol.Request(id, "pipeUser"), stop.Token);
            string whois = await Until(pipe, f => WhoisProtocol.TryReply(f, out _, out _));
            return WhoisProtocol.TryReply(whois, out var whoisId, out var info) && whoisId == id &&
                info?.Messages == 3 && info.Address == "" && tcpClient.IpAddress == "127.0.0.1";
        }
        finally
        {
            stop.Cancel(); pipe.Close(); tcp.Close(); pipeClient.Close(); tcpClient.Close();
            await Task.WhenAll(pipeLoop, tcpLoop).WaitAsync(TimeSpan.FromSeconds(3));
            if (connection.CloseCount != 1) throw new Exception("Connection ownership disposed more than once");
        }
    }

    private sealed class PartialCancelledWriteStream : MemoryStream
    {
        internal bool Closed;
        internal long LengthWritten;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            WriteByte(buffer.Span[0]); LengthWritten = Length;
            return ValueTask.FromCanceled(new CancellationToken(canceled: true));
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    private sealed class TestConnection(Stream stream) : IChatConnection
    {
        public Stream Stream => stream;
        public IPAddress? RemoteIpAddress => null;
        internal int CloseCount;
        public void Dispose() { CloseCount++; stream.Dispose(); }
    }
}
