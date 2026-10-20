using System.Net;
using TCPTunnel;
using TCPTunnel.Cryptography;

internal static class MeshRelayCompatibilityTests
{
    private sealed class Link(Stream stream) : IChatConnection
    {
        public Stream Stream => stream;
        public IPAddress? RemoteIpAddress => null;
        public ChatTransport Transport => ChatTransport.Bluetooth;
        public void Dispose() => stream.Dispose();
    }
    public static async Task<bool> MeshRelayCurrentProtocolSelfTest()
    {
        string? previous = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-relay-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", directory);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var root = MemoryDuplexStream.CreatePair();
        var leaf = MemoryDuplexStream.CreatePair();
        long forwarded = 0;
        async Task Forward(Stream source, Stream target)
        {
            byte[] buffer = new byte[257];
            try
            {
                int count;
                while ((count = await source.ReadAsync(buffer, stop.Token)) != 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, count), stop.Token);
                    Interlocked.Add(ref forwarded, count);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
        Task outbound = Forward(leaf.First, root.Second), inbound = Forward(root.Second, leaf.First);
        try
        {
            using var owner = new NativeMlsSession(); using var hub = new SecureHubService("relayOwner", owner);
            string link = hub.CreateOneTimeInvitation();
            var invitation = SecureWire.Parse<SecureInvitation>(Convert.FromBase64String(link.Substring(5))) with { Port = 9091 };
            using var peer = new Client(new Link(root.First)); NetWorker.broadcaster.AddConnection(peer);
            Task server = hub.RunAsync(peer, stop.Token);
            SecureClientBridge? bridge = null;
            try
            {
                string greeting = await MessageProtocol.ReadStringAsync(leaf.Second, stop.Token);
                bridge = await SecureClientBridge.ConnectAsync(leaf.Second, greeting, "relayLeaf", new NativeMlsSession(),
                    invitation.Matches, stop.Token, invitationToken: invitation.Token);
                if (bridge.ProtocolVersion != 3 || await MessageProtocol.ReadStringAsync(bridge.Stream, stop.Token) != NetWorker.DO_AUTH_MESSAGE) return false;
                await MessageProtocol.WriteStringAsync(bridge.Stream, "REPLY:relayLeaf", stop.Token);
                if (await MessageProtocol.ReadStringAsync(bridge.Stream, stop.Token) != NetWorker.AUTH_OK_MESSAGE) return false;
                await MessageProtocol.WriteStringAsync(bridge.Stream, "opaque relay message", stop.Token);
                string id = Guid.NewGuid().ToString("N");
                await MessageProtocol.WriteStringAsync(bridge.Stream, PrivacyProtocol.Request(id), stop.Token);
                while (true)
                {
                    string frame = await MessageProtocol.ReadStringAsync(bridge.Stream, stop.Token);
                    if (PrivacyProtocol.TryReply(frame, id, out _)) return forwarded > 1024 && peer.Diagnostics.Messages == 1 && hub.PendingRequestsSnapshot().Length == 0;
                }
            }
            finally { bridge?.Dispose(); peer.Close(); await server; }
        }
        finally
        {
            stop.Cancel(); root.First.Dispose(); root.Second.Dispose(); leaf.First.Dispose(); leaf.Second.Dispose();
            await Task.WhenAll(outbound, inbound);
            Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", previous);
        }
    }
}
