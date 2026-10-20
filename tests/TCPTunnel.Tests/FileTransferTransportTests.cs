using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TCPTunnel;
using TCPTunnel.Cryptography;

internal static class FileTransferTransportTests
{
    private sealed class Link(Stream stream, ChatTransport transport) : IChatConnection
    {
        public Stream Stream => stream;
        public IPAddress? RemoteIpAddress => null;
        public ChatTransport Transport => transport;
        public void Dispose() => stream.Dispose();
    }
    private sealed class Peer : IDisposable
    {
        internal required Stream Stream;
        internal required Client Server;
        internal required Task ServerTask;
        internal Task Reader = Task.CompletedTask;
        internal FileTransferSession? Files;
        internal SecureClientBridge? Bridge;
        internal TcpClient? Socket;
        internal readonly ConcurrentQueue<string> Notices = new();
        internal readonly ConcurrentQueue<string> Messages = new();
        internal volatile bool Available;
        internal Exception? Error;
        public void Dispose() { Files?.Dispose(); Bridge?.Dispose(); Stream.Dispose(); Socket?.Dispose(); Server.Close(); }
    }
    internal static Task<bool> FileTransferTcpRoutingSelfTest() => Run(false, false);
    internal static Task<bool> FileTransferEncryptedTcpSelfTest() => Run(true, false);
    internal static Task<bool> FileTransferEncryptedBluetoothStreamSelfTest() => Run(true, true);
    private static async Task<bool> Run(bool encrypted, bool bluetooth)
    {
        string? oldHome = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        string root = Path.Combine(Path.GetTempPath(), "TCPTunnel-file-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", root);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var identity = encrypted ? new NativeMlsSession() : null;
        string previousFingerprint = ApplicationSettings.Current.IdentityFingerprint;
        if (encrypted) ApplicationSettings.Current.IdentityFingerprint = identity!.Fingerprint;
        using var hub = encrypted ? new SecureHubService("fileOwner", identity!) : null;
        var peers = new List<Peer>();
        async Task Wait(Func<bool> condition)
        {
            while (!condition())
            {
                foreach (var peer in peers) if (peer.Error != null) throw new Exception("Transport reader failed", peer.Error);
                await Task.Delay(5, stop.Token);
            }
        }
        async Task<Peer> Join(string nickname, bool local = false, bool legacy = false)
        {
            Stream network; Client server; TcpClient? socket = null;
            if (bluetooth || local)
            {
                var pair = MemoryDuplexStream.CreatePair(); network = pair.Second;
                server = new Client(new Link(pair.First, local ? ChatTransport.Local : ChatTransport.Bluetooth));
            }
            else
            {
                socket = new TcpClient(); await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
                network = socket.GetStream(); server = new Client(await listener.AcceptTcpClientAsync(stop.Token));
            }
            NetWorker.broadcaster.AddConnection(server);
            Task serving = hub == null ? NetWorker.ServerClientLoopAsync(server, stop.Token) : hub.RunAsync(server, stop.Token);
            var peer = new Peer { Stream = network, Server = server, ServerTask = serving, Socket = socket }; peers.Add(peer);
            string greeting = await MessageProtocol.ReadStringAsync(network, stop.Token);
            if (encrypted && !local)
            {
                string encoded = hub!.CreateOneTimeInvitation();
                var invitation = SecureWire.Parse<SecureInvitation>(Convert.FromBase64String(encoded.Substring(5)));
                peer.Bridge = await SecureClientBridge.ConnectAsync(network, greeting, nickname, new NativeMlsSession(), invitation.Matches, stop.Token, invitationToken: invitation.Token);
                peer.Stream = peer.Bridge.Stream; greeting = await MessageProtocol.ReadStringAsync(peer.Stream, stop.Token);
            }
            if (greeting != NetWorker.DO_AUTH_MESSAGE) throw new Exception("Missing auth");
            await MessageProtocol.WriteStringAsync(peer.Stream, "REPLY:" + nickname, stop.Token);
            if (await MessageProtocol.ReadStringAsync(peer.Stream, stop.Token) != NetWorker.AUTH_OK_MESSAGE) throw new Exception("Auth failed");
            string desktop = Path.Combine(root, nickname); Directory.CreateDirectory(desktop);
            if (!legacy) peer.Files = new FileTransferSession(() => nickname, (frame, token) => MessageProtocol.WriteStringAsync(peer.Stream, frame, token), peer.Notices.Enqueue, stop.Token, desktop);
            peer.Reader = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        string frame = await MessageProtocol.ReadStringAsync(peer.Stream, stop.Token);
                        if (frame == FileTransferProtocol.Capabilities) { peer.Available = true; peer.Files?.SetAvailable(true); continue; }
                        if (frame == FileTransferProtocol.Unavailable) { peer.Available = false; peer.Files?.SetAvailable(false); continue; }
                        if (peer.Files != null && await peer.Files.HandleAsync(frame)) continue;
                        peer.Messages.Enqueue(frame);
                    }
                }
                catch (Exception ex) when (stop.IsCancellationRequested || ex is IOException or ObjectDisposedException or OperationCanceledException) { }
                catch (Exception ex) { peer.Error = ex; }
            });
            if (!legacy) await MessageProtocol.WriteStringAsync(peer.Stream, FileTransferProtocol.Probe, stop.Token);
            return peer;
        }
        try
        {
            if (encrypted) await Join("fileOwner", true);
            Peer alice = await Join("fileAlice"), bob = await Join("fileBob");
            await Wait(() => alice.Available && bob.Available);
            await Task.Delay(150, stop.Token);
            byte[] data = RandomNumberGenerator.GetBytes(96 * 1024 + 31);
            string source = Path.Combine(root, "документ.bin"); await File.WriteAllBytesAsync(source, data, stop.Token);
            string target = Path.Combine(root, "fileBob", "документ.bin");
            var clock = Stopwatch.StartNew();
            alice.Files!.HandleCommand("/send @fileBob '" + source + "'");
            await Wait(() => bob.Notices.Any(text => text.StartsWith("1. ")));
            if (File.Exists(target) || Directory.GetFiles(Path.GetDirectoryName(target)!).Length != 0) return false;
            bob.Files!.HandleCommand("/receive 1");
            await MessageProtocol.WriteStringAsync(alice.Stream, "chat alongside file", stop.Token);
            await Wait(() => File.Exists(target) && bob.Messages.Any(text => text.Contains("chat alongside file")));
            if (!(await File.ReadAllBytesAsync(target, stop.Token)).SequenceEqual(data)) return false;
            Console.WriteLine($"FILE WIRE {(encrypted ? "E2E3" : "plain")} {(bluetooth ? "BT-stream" : "TCP")}: {data.Length} bytes/{clock.ElapsedMilliseconds}ms, hash OK, concurrent chat OK");
            if (encrypted)
            {
                Peer legacy = await Join("fileLegacy", legacy: true);
                await Wait(() => !alice.Available && !bob.Available);
                alice.Files.HandleCommand("/send fileBob '" + source + "'");
                await MessageProtocol.WriteStringAsync(alice.Stream, "legacy barrier alive", stop.Token);
                await Wait(() => legacy.Messages.Any(frame => frame.Contains("legacy barrier alive")) && bob.Messages.Any(frame => frame.Contains("legacy barrier alive")));
                if (legacy.Messages.Any(FileTransferProtocol.IsControl) || !legacy.Server.IsReady) return false;
            }
            return peers.All(peer => peer.Error == null);
        }
        finally
        {
            stop.Cancel(); foreach (Peer peer in peers) peer.Dispose();
            try { await Task.WhenAll(peers.SelectMany(peer => new[] { peer.ServerTask, peer.Reader })).WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", oldHome);
            ApplicationSettings.Current.IdentityFingerprint = previousFingerprint;
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
}
