using System.Net;
using System.Text;
using TCPTunnel;

internal static class BluetoothModeTests
{
    public static async Task<bool> MemoryDuplexStreamSelfTest()
    {
        var (left, right) = MemoryDuplexStream.CreatePair();
        byte[] payload = Encoding.UTF8.GetBytes("привет по памяти");
        await left.WriteAsync(payload);
        await left.WriteAsync(payload);
        byte[] buffer = new byte[payload.Length * 2];
        int total = 0;
        while (total < buffer.Length)
            total += await right.ReadAsync(buffer.AsMemory(total, Math.Min(7, buffer.Length - total)));
        bool roundTrip = buffer.AsSpan(0, payload.Length).SequenceEqual(payload) &&
                         buffer.AsSpan(payload.Length).SequenceEqual(payload);

        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await right.ReadExactlyAsync(new byte[4], cancel.Token); return false; }
            catch (OperationCanceledException) { }
        }

        left.Dispose();
        byte[] eofBuffer = new byte[4];
        bool eof;
        try { await right.ReadExactlyAsync(eofBuffer.AsMemory(0, 1)); eof = false; }
        catch (EndOfStreamException) { eof = true; }
        bool writeAfterPeerClosed;
        try { await right.WriteAsync(payload); writeAfterPeerClosed = false; }
        catch (IOException) { writeAfterPeerClosed = true; }
        right.Dispose();
        return roundTrip && eof && writeAfterPeerClosed;
    }

    public static async Task<bool> LocalAndBluetoothHubSessionSelfTest()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var (hubSide, clientSide) = LocalChatConnection.CreatePair();
        var (btHub, btPeer) = MemoryDuplexStream.CreatePair();
        var btConnection = new FakeBluetoothConnection(btHub);
        using var ownerClient = new Client(hubSide);
        using var btClient = new Client(btConnection);
        NetWorker.broadcaster.AddConnection(ownerClient);
        NetWorker.broadcaster.AddConnection(btClient);
        Task ownerLoop = NetWorker.ServerClientLoopAsync(ownerClient, stop.Token);
        Task btLoop = NetWorker.ServerClientLoopAsync(btClient, stop.Token);
        Stream owner = clientSide.Stream;
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

            await Authenticate(owner, "ownerUser");
            await Authenticate(btPeer, "radioUser");
            await MessageProtocol.WriteStringAsync(btPeer, "по радио", stop.Token);
            await Until(owner, frame => frame == "[radioUser]: по радио");
            await MessageProtocol.WriteStringAsync(owner, "локально", stop.Token);
            await Until(btPeer, frame => frame == "[ownerUser]: локально");

            await MessageProtocol.WriteStringAsync(btPeer, WhoisProtocol.Hello, stop.Token);
            await Until(btPeer, frame => frame == WhoisProtocol.Capabilities);
            await MessageProtocol.WriteStringAsync(owner, WhoisProtocol.Hello, stop.Token);
            await Until(owner, frame => frame == WhoisProtocol.Capabilities);
            await MessageProtocol.WriteStringAsync(btPeer, WhoisProtocol.Signal(-61), stop.Token);
            await MessageProtocol.WriteStringAsync(owner, WhoisProtocol.Signal(-20), stop.Token);

            async Task<WhoisInfo?> Whois(string target)
            {
                string id = Guid.NewGuid().ToString("N");
                await MessageProtocol.WriteStringAsync(owner, WhoisProtocol.Request(id, target), stop.Token);
                string reply = await Until(owner, frame => WhoisProtocol.TryReply(frame, out var replyId, out _) && replyId == id);
                WhoisProtocol.TryReply(reply, out _, out var info);
                return info;
            }

            WhoisInfo? radio = await Whois("radioUser");
            WhoisInfo? local = await Whois("ownerUser");
            return radio?.Transport == WhoisProtocol.TransportBluetooth && radio.SignalDbm == -61 && radio.Address == "" &&
                   local?.Transport == WhoisProtocol.TransportLocal && local.SignalDbm == null &&
                   ownerClient.Transport == ChatTransport.Local && btClient.Transport == ChatTransport.Bluetooth;
        }
        finally
        {
            stop.Cancel();
            clientSide.Dispose();
            btPeer.Dispose();
            ownerClient.Close();
            btClient.Close();
            await Task.WhenAll(ownerLoop, btLoop).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    public static bool HubAccessDescriptionSelfTest() =>
        UserInterface.DescribeHubAccess(new HubOptions(HubIpMode.Public, false)) == "WWW" &&
        UserInterface.DescribeHubAccess(new HubOptions(HubIpMode.LanOnly, true)) == "LAN + Bluetooth" &&
        UserInterface.DescribeHubAccess(new HubOptions(HubIpMode.Off, true)) == "Bluetooth";

    private sealed class FakeBluetoothConnection(Stream stream) : IChatConnection
    {
        public Stream Stream => stream;
        public IPAddress? RemoteIpAddress => null;
        public ChatTransport Transport => ChatTransport.Bluetooth;
        public void Dispose() => stream.Dispose();
    }
}
