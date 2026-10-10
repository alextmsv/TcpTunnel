#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel.Cryptography;

internal static class SecureIntegrationTests
{
    private sealed class StreamConnection(Stream stream, ChatTransport transport) : IChatConnection
    {
        public Stream Stream => stream;
        public IPAddress? RemoteIpAddress => null;
        public ChatTransport Transport => transport;
        public void Dispose() => stream.Dispose();
    }
    private sealed class BlockedWriter(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => await Task.Delay(Timeout.Infinite, token);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class PausingStream(Stream inner) : Stream
    {
        internal readonly TaskCompletionSource Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int PauseNext;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref PauseNext, 0) == 1) { Paused.TrySetResult(); await Resume.Task.WaitAsync(token); }
            await inner.WriteAsync(buffer, token);
        }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { Resume.TrySetCanceled(); inner.Dispose(); } base.Dispose(disposing); }
    }
    internal static Task<bool> SecureTcpSelfTest() => RunAsync(false);
    internal static Task<bool> SecureBluetoothTransportSelfTest() => RunAsync(true);
    internal static Task<bool> SecureMixedTransportSelfTest() => RunAsync(true, true);
    private static async Task<bool> RunAsync(bool bluetooth, bool mixed = false)
    {
        string? previousDirectory = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-secure-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var owner = new NativeMlsSession(); using var hub = new SecureHubService("testOwner", owner);
        hub.SessionError += error => { if (error is not (IOException or OperationCanceledException or UnauthorizedAccessException)) Console.WriteLine("Secure server: " + error); };
        NativeMlsSession? aliceIdentity = new(); string aliceFingerprint = aliceIdentity.Fingerprint;
        byte[] aliceKey = aliceIdentity.ExportIdentityMaterial();
        NativeMlsSession? bobIdentity = new(); string bobFingerprint = bobIdentity.Fingerprint;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Task? aliceServer = null; Task? bobServer = null;
        SecureClientBridge? alice = null; SecureClientBridge? bob = null;
        int connectionIndex = 0;
        async Task<(PausingStream Network, Task Server, Client Peer)> ConnectionAsync()
        {
            if (bluetooth && (!mixed || ++connectionIndex != 1))
            {
                var pair = MemoryDuplexStream.CreatePair();
                var client = new Client(new StreamConnection(pair.First, ChatTransport.Bluetooth));
                NetWorker.broadcaster.AddConnection(client);
                return (new PausingStream(pair.Second), hub.RunAsync(client, deadline.Token), client);
            }
            var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            var peer = new Client(accepted); NetWorker.broadcaster.AddConnection(peer);
            return (new PausingStream(new DuplexStream(socket.GetStream(), socket.GetStream())), hub.RunAsync(peer, deadline.Token), peer);
        }
        async Task<SecureClientBridge> JoinAsync(Stream network, NativeMlsSession identity, string nickname, string fingerprint)
        {
            string greeting = await MessageProtocol.ReadStringAsync(network, deadline.Token);
            var join = SecureClientBridge.ConnectAsync(network, greeting, nickname, identity,
                boot => NativeMlsSession.FingerprintOf(boot.OwnerKey) == owner.Fingerprint, deadline.Token);
            while (!join.IsCompleted && !hub.PendingRequests.Contains(fingerprint, StringComparison.Ordinal)) await Task.Delay(10, deadline.Token);
            if (!join.IsCompleted) hub.Approve(nickname, fingerprint);
            var bridge = await join;
            if (await MessageProtocol.ReadStringAsync(bridge.Stream, deadline.Token) != NetWorker.DO_AUTH_MESSAGE) throw new IOException("Missing UI auth.");
            await MessageProtocol.WriteStringAsync(bridge.Stream, "REPLY:" + nickname, deadline.Token);
            if (await MessageProtocol.ReadStringAsync(bridge.Stream, deadline.Token) != NetWorker.AUTH_OK_MESSAGE) throw new IOException("Missing UI auth result.");
            return bridge;
        }
        async Task<string> ReadChatAsync(Stream stream)
        {
            while (true)
            {
                string frame = await MessageProtocol.ReadStringAsync(stream, deadline.Token);
                if (!SystemMessageProtocol.IsControlMessage(frame) && !SnakeProtocol.IsSnakeControlMessage(frame)) return frame;
            }
        }
        try
        {
            var first = await ConnectionAsync(); aliceServer = first.Server;
            alice = await JoinAsync(first.Network, aliceIdentity, "testAlice", aliceFingerprint); aliceIdentity = null;
            first.Network.PauseNext = 1;
            var epochAnimation = new AnimatedImagePacket
            {
                Width = ImageProtocol.MaxWireWidth, Height = ImageProtocol.MaxWireHeight,
                PackedFrames = Enumerable.Range(0, ImageAnimationProtocol.MaxFrames).Select(_ => new byte[ImageProtocol.MaxPackedBytes]).ToArray(),
                FrameDelays = Enumerable.Repeat((ushort)90, ImageAnimationProtocol.MaxFrames).ToArray()
            };
            string epochTransferId = ImageAnimationProtocol.CreateTransferId();
            await MessageProtocol.WriteStringAsync(alice.Stream, ImageAnimationProtocol.CreateClientBegin(epochTransferId, epochAnimation), deadline.Token);
            for (int index = 0; index < epochAnimation.FrameCount; index++) await MessageProtocol.WriteStringAsync(alice.Stream, ImageAnimationProtocol.CreateClientFrame(epochTransferId, index, epochAnimation.FrameDelays[index], epochAnimation.PackedFrames[index]), deadline.Token);
            await MessageProtocol.WriteStringAsync(alice.Stream, ImageAnimationProtocol.CreateClientEnd(epochTransferId), deadline.Token);
            await first.Network.Paused.Task.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            var second = await ConnectionAsync(); bobServer = second.Server;
            bob = await JoinAsync(second.Network, bobIdentity, "testBob", bobFingerprint); bobIdentity = null;
            first.Network.Resume.TrySetResult();
            var epochAssembler = new ImageAnimationAssembler(); AnimatedImagePacket? epochResult = null;
            while (epochResult == null)
            {
                string frame = await ReadChatAsync(bob.Stream);
                if (!ImageAnimationProtocol.TryParseServer(frame, out var control) || epochAssembler.Accept(control, out epochResult) == ImageAnimationAssemblyResult.Invalid) return false;
            }
            if (epochResult.Sender != "testAlice" || epochResult.FrameCount != epochAnimation.FrameCount || epochResult.PackedByteCount != 2_880_000) return false;
            await MessageProtocol.WriteStringAsync(alice.Stream, "Проверка TCP/Bluetooth E2E", deadline.Token);
            if (await ReadChatAsync(bob.Stream) != "[testAlice]: Проверка TCP/Bluetooth E2E") return false;
            await MessageProtocol.WriteStringAsync(bob.Stream, "answer", deadline.Token);
            if (await ReadChatAsync(alice.Stream) != "[testBob]: answer") return false;
            var image = new ImagePacket { Width = 3, Height = 2, PackedPixels = new byte[ImageProtocol.GetPackedLength(3, 2)] };
            await MessageProtocol.WriteStringAsync(alice.Stream, ImageProtocol.CreateClientFrame(image), deadline.Token);
            string imageFrame = await ReadChatAsync(bob.Stream);
            if (!ImageProtocol.TryParseServerFrame(imageFrame, out var received) || received.Sender != "testAlice" || !received.PackedPixels.SequenceEqual(image.PackedPixels)) return false;
            var animation = new AnimatedImagePacket { Width = 3, Height = 2, PackedFrames = new[] { image.PackedPixels, image.PackedPixels }, FrameDelays = new ushort[] { 90, 110 } };
            string transferId = ImageAnimationProtocol.CreateTransferId();
            await MessageProtocol.WriteStringAsync(bob.Stream, ImageAnimationProtocol.CreateClientBegin(transferId, animation), deadline.Token);
            for (int index = 0; index < animation.FrameCount; index++) await MessageProtocol.WriteStringAsync(bob.Stream, ImageAnimationProtocol.CreateClientFrame(transferId, index, animation.FrameDelays[index], animation.PackedFrames[index]), deadline.Token);
            await MessageProtocol.WriteStringAsync(bob.Stream, ImageAnimationProtocol.CreateClientEnd(transferId), deadline.Token);
            var assembler = new ImageAnimationAssembler(); AnimatedImagePacket? result = null;
            while (result == null)
            {
                string frame = await ReadChatAsync(alice.Stream);
                if (!ImageAnimationProtocol.TryParseServer(frame, out var control)) return false;
                if (assembler.Accept(control, out result) == ImageAnimationAssemblyResult.Invalid) return false;
            }
            if (result.Sender != "testBob" || result.FrameCount != 2) return false;
            alice.Dispose(); alice = null; await aliceServer.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            using (var impostor = new NativeMlsSession())
            {
                var attempt = await ConnectionAsync();
                string greeting = await MessageProtocol.ReadStringAsync(attempt.Network, deadline.Token);
                try
                {
                    using var invalid = await SecureClientBridge.ConnectAsync(attempt.Network, greeting, "testAlice", impostor, boot => NativeMlsSession.FingerprintOf(boot.OwnerKey) == owner.Fingerprint, deadline.Token);
                    return false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                await attempt.Server.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            }
            var returning = await ConnectionAsync(); aliceServer = returning.Server;
            alice = await JoinAsync(returning.Network, new NativeMlsSession(aliceKey), "testAlice", aliceFingerprint);
            await MessageProtocol.WriteStringAsync(alice.Stream, "returned", deadline.Token);
            if (await ReadChatAsync(bob.Stream) != "[testAlice]: returned") return false;
            Stream originalSender = returning.Peer.Stream;
            var pausedSender = new PausingStream(originalSender) { PauseNext = 1 };
            returning.Peer.Stream = pausedSender;
            try
            {
                await MessageProtocol.WriteStringAsync(alice.Stream, "blocked sender receipt", deadline.Token);
                await pausedSender.Paused.Task.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token);
                using var responsive = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                responsive.CancelAfter(TimeSpan.FromSeconds(2));
                while (await MessageProtocol.ReadStringAsync(bob.Stream, responsive.Token) != "[testAlice]: blocked sender receipt") { }
                await MessageProtocol.WriteStringAsync(bob.Stream, "room remains responsive", responsive.Token);
                await MessageProtocol.WriteStringAsync(bob.Stream, HubStatusProtocol.Hello, responsive.Token);
                while (await MessageProtocol.ReadStringAsync(bob.Stream, responsive.Token) != HubStatusProtocol.Capabilities) { }
            }
            finally { pausedSender.Resume.TrySetResult(); returning.Peer.Stream = originalSender; }
            if (await ReadChatAsync(alice.Stream) != "[testBob]: room remains responsive") return false;
            second.Peer.Stream = new BlockedWriter(second.Peer.Stream);
            await MessageProtocol.WriteStringAsync(alice.Stream, "slow recipient isolation", deadline.Token);
            await MessageProtocol.WriteStringAsync(alice.Stream, HubStatusProtocol.Hello, deadline.Token);
            using (var responsive = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                responsive.CancelAfter(TimeSpan.FromSeconds(2));
                while (await MessageProtocol.ReadStringAsync(alice.Stream, responsive.Token) != HubStatusProtocol.Capabilities) { }
            }
            if (!await hub.KickAsync("testBob", "test revocation")) return false;
            await MessageProtocol.WriteStringAsync(alice.Stream, "after removal", deadline.Token);
            bob.Dispose(); bob = null;
            await MessageProtocol.WriteStringAsync(alice.Stream, HubStatusProtocol.Hello, deadline.Token);
            while (await MessageProtocol.ReadStringAsync(alice.Stream, deadline.Token) != HubStatusProtocol.Capabilities) { }
            return true;
        }
        catch (Exception error) { Console.WriteLine("Secure integration: " + error); return false; }
        finally
        {
            CryptographicOperations.ZeroMemory(aliceKey);
            deadline.Cancel(); alice?.Dispose(); bob?.Dispose(); aliceIdentity?.Dispose(); bobIdentity?.Dispose();
            if (aliceServer != null) { try { await aliceServer.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            if (bobServer != null) { try { await bobServer.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", previousDirectory);
        }
    }
}
