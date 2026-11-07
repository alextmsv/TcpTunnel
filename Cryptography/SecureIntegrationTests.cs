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
        using var owner = new NativeMlsSession(); using var hub = new SecureHubService("testOwner", owner, 3);
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
                if (!SystemMessageProtocol.IsControlMessage(frame) && !SnakeProtocol.IsSnakeControlMessage(frame) && !NicknameProtocol.IsControl(frame)) return frame;
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
            if (!await CheckDuelAsync(alice.Stream, bob.Stream, deadline.Token)) return false;
            if (!await CheckDuelOptionsAsync(alice.Stream, bob.Stream, deadline.Token, true)) return false;
            var nicknameProfile = new SnakeProfile { Enabled = true, DelayMilliseconds = 87, Color = ConsoleColor.Cyan, Glyph = '-', Nickname = true };
            await MessageProtocol.WriteStringAsync(alice.Stream, SnakeProtocol.CreateClientProfile(nicknameProfile), deadline.Token);
            bool nicknameReceived = false;
            while (!nicknameReceived)
            {
                string frame = await MessageProtocol.ReadStringAsync(bob.Stream, deadline.Token);
                if (SnakeProtocol.TryParseServerUpdate(frame, out var kind, out string sender, out var receivedProfile) && sender == "testAlice")
                    nicknameReceived = kind == SnakeUpdateKind.Set && receivedProfile.Nickname && receivedProfile.DelayMilliseconds == 87;
            }
            using (var extraIdentity = new NativeMlsSession())
            {
                var excess = await ConnectionAsync();
                string greeting = await MessageProtocol.ReadStringAsync(excess.Network, deadline.Token);
                try
                {
                    using var unexpected = await SecureClientBridge.ConnectAsync(excess.Network, greeting, "extraUser", extraIdentity,
                        boot => NativeMlsSession.FingerprintOf(boot.OwnerKey) == owner.Fingerprint, deadline.Token);
                    return false;
                }
                catch (UnauthorizedAccessException error) when (error.Message == Lang.Get(TextId.HubFull)) { }
                await excess.Server.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
                if (hub.ParticipantCount != 3 || hub.PendingRequestsSnapshot().Length != 0) return false;
            }
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
            byte[] membershipBeforeRename = owner.GroupIdentities();
            foreach (string next in new[] { "testAliceNew", "testAliceNext", "testAlice" })
            {
                string id = Guid.NewGuid().ToString("N");
                await MessageProtocol.WriteStringAsync(alice.Stream, NicknameProtocol.Request(id, next), deadline.Token);
                NicknameReply? reply = null;
                while (reply?.Id != id)
                {
                    string frame = await MessageProtocol.ReadStringAsync(alice.Stream, deadline.Token);
                    if (NicknameProtocol.TryReply(frame, out var parsed)) reply = parsed;
                }
                if (reply.Error != "OK" || reply.Change.Current != next || reply.Change.Original != "testAlice") return false;
                NicknameChange? notice = null;
                while (notice?.Current != next)
                {
                    string frame = await MessageProtocol.ReadStringAsync(bob.Stream, deadline.Token);
                    if (NicknameProtocol.TryChanged(frame, out var parsed)) notice = parsed;
                }
                if (notice.Original != "testAlice" || !owner.GroupIdentities().SequenceEqual(membershipBeforeRename) || returning.Peer.OriginalNickname != "testAlice") return false;
                await MessageProtocol.WriteStringAsync(alice.Stream, "renamed identity", deadline.Token);
                if (await ReadChatAsync(bob.Stream) != "[" + next + "]: renamed identity") return false;
            }
            await Task.Delay(600, deadline.Token);
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

    internal static async Task<bool> CheckDuelAsync(Stream alice, Stream bob, CancellationToken token, bool blitz = true, bool disconnect = false, int simulatedBobRtt = 0)
    {
        async Task Send(Stream stream, DuelPacket packet) =>
            await MessageProtocol.WriteStringAsync(stream, DuelProtocol.Encode(packet), token);
        async Task<DuelPacket> Read(Stream stream, string kind)
        {
            while (true)
            {
                var frame = await MessageProtocol.ReadStringAsync(stream, token);
                if (!DuelProtocol.TryDecode(frame, out var packet)) continue;
                if (packet.Kind == "ping")
                {
                    if (stream == bob && simulatedBobRtt > 0) await Task.Delay(simulatedBobRtt, token);
                    await Send(stream, new("pong", packet.Id, Nonce: packet.Nonce));
                    continue;
                }
                if (packet.Kind == kind) return packet;
            }
        }
        await Send(alice, new("hello")); await Send(bob, new("hello"));
        await Read(alice, "caps"); await Read(bob, "caps");
        await Send(alice, new("offer", Peer: "testBob", Level: "easy", Minutes: 1, Blitz: blitz));
        var invite = await Read(bob, "invite");
        if (invite.Peer != "testAlice" || invite.Blitz != blitz || (await Read(alice, "sent")).Id != invite.Id) return false;
        await Send(bob, new("accept", invite.Id));
        if ((await Read(alice, "start")).Id != invite.Id || (await Read(bob, "start")).Id != invite.Id) return false;
        async Task<(DuelPacket Packet, long Received)> Target(Stream stream) =>
            (await Read(stream, "target"), System.Diagnostics.Stopwatch.GetTimestamp());
        var targets = await Task.WhenAll(Target(alice), Target(bob));
        var a = targets[0].Packet; var b = targets[1].Packet;
        if (a.Slot != b.Slot || (blitz && (a.X != b.X || a.Y != b.Y))) return false;
        if (simulatedBobRtt > 0 &&
            System.Diagnostics.Stopwatch.GetElapsedTime(targets[1].Received, targets[0].Received).TotalMilliseconds < 20) return false;
        await Send(alice, new("end", a.Id, You: 999, Outcome: "win"));
        await Send(alice, new("shot", a.Id, Slot: a.Slot, X: a.X, Y: a.Y));
        if (simulatedBobRtt > 0) await Task.Delay(15, token);
        await Send(bob, new("shot", b.Id, Slot: b.Slot, X: b.X, Y: b.Y));
        var scoreA = await Read(alice, "score"); var scoreB = await Read(bob, "score");
        if (!blitz) { scoreA = await Read(alice, "score"); scoreB = await Read(bob, "score"); }
        int expected = blitz ? 1 : 2;
        if (scoreA.You + scoreB.You != expected || (blitz && (scoreA.X != 1 || scoreB.X != 1))) return false;
        if (simulatedBobRtt > 0 && (scoreA.You != 0 || scoreB.You != 1)) return false;
        if (disconnect)
        {
            bob.Dispose();
            var result = await Read(alice, "end");
            return result.Outcome == "opponent-left" && result.You + result.Them == expected;
        }
        await Send(bob, new("quit", b.Id));
        var endA = await Read(alice, "end"); var endB = await Read(bob, "end");
        return endA.Outcome == "opponent-left" && endB.Outcome == "forfeit" && endA.You + endB.You == expected;
    }

    internal static async Task<bool> CheckDuelOptionsAsync(Stream alice, Stream bob, CancellationToken token, bool blitz)
    {
        Task Send(Stream s, DuelPacket p) => MessageProtocol.WriteStringAsync(s, DuelProtocol.Encode(p), token);
        async Task<DuelPacket> Read(Stream s, string kind)
        {
            while (true)
            {
                string text = await MessageProtocol.ReadStringAsync(s, token);
                if (!DuelProtocol.TryDecode(text, out var p)) continue;
                if (p.Kind == "ping") { await Send(s, new("pong", p.Id, Nonce: p.Nonce)); continue; }
                if (p.Kind == kind) return p;
            }
        }
        await Send(alice,new("hello2")); await Send(bob,new("hello2"));
        await Read(alice,"caps2"); await Read(bob,"caps2");
        await Send(alice,new("offer", Peer:"testBob", Level:"normal", Minutes:1, Blitz:blitz, Version:2, Mode:"minefield", Size:"any", Count:3));
        var invite = await Read(bob,"invite");
        if (invite.Version != 2 || invite.Mode != "minefield" || invite.Size != "any" || invite.Count != 3) return false;
        await Read(alice,"sent");
        await Send(bob,new("accept", invite.Id));
        await Read(alice,"start"); await Read(bob,"start");
        async Task<DuelPacket[]> Field(Stream s)
        {
            var items = new DuelPacket[3];
            for (int i=0;i<items.Length;i++) items[i]=await Read(s,"target");
            return items;
        }
        var fields = await Task.WhenAll(Field(alice), Field(bob));
        if (fields.Any(items => items.Select(t => (t.X,t.Y)).Distinct().Count()!=3) ||
            (blitz && !fields[0].SequenceEqual(fields[1]))) return false;
        var target = fields[0][0];
        Task Hit(Stream s, DuelPacket t) => Send(s,new("shot",t.Id,Slot:t.Slot,X:t.X,Y:t.Y,Version:2));
        await Hit(alice,target);
        if (blitz) await Hit(bob, target);
        var a = await Read(alice,"score"); var b = await Read(bob,"score");
        if (a.You+b.You != 1 || a.Clear != 1 || b.Clear != (blitz ? 1 : 0)) return false;
        var replacement = await Read(alice,"target");
        if (replacement.Slot == target.Slot || fields[0].Any(t => t.X==replacement.X && t.Y==replacement.Y)) return false;
        if (!blitz)
        {
            await Task.Delay(45,token);
            await Hit(alice,replacement);
            a = await Read(alice,"score"); await Read(bob,"score");
            if (a.You != 3 || a.Streak != 2 || a.Delta != 2) return false;
            await Task.Delay(45,token);
            await Send(alice,new("shot",invite.Id,Slot:-1,X:0,Y:0,Version:2));
            a = await Read(alice,"score");
            if (a.You != 2 || a.Streak != 0 || a.Delta != -1) return false;
        }
        await Send(bob,new("quit",invite.Id));
        return (await Read(alice,"end")).Outcome == "opponent-left" && (await Read(bob,"end")).Outcome == "forfeit";
    }
}
