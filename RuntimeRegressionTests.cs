using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    internal static class RuntimeRegressionTests
    {
        internal static async Task<bool> RunAsync() =>
            await CompletedSessionsSelfTest().ConfigureAwait(false) &&
            await PartialFrameDeadlineSelfTest().ConfigureAwait(false) &&
            await AbandonedAnimationSelfTest().ConfigureAwait(false) &&
            await SharedBatchOwnershipSelfTest().ConfigureAwait(false) &&
            await SendQueueLimitsSelfTest().ConfigureAwait(false) &&
            await SlowRecipientIsolationSelfTest().ConfigureAwait(false) &&
            await WhoisPrivacySelfTest().ConfigureAwait(false);

        public static async Task<bool> CompletedSessionsSelfTest()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            object gate = typeof(ServerInterface).GetField("serverLock", flags).GetValue(null);
            var tasks = (ICollection<Task>)typeof(ServerInterface).GetField("clientTasks", flags).GetValue(null);
            int PendingCount() { lock (gate) return tasks.Count; }

            for (int restart = 0; restart < 2; restart++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                if (!ServerInterface.TryStartServer(port, new HubOptions(HubIpMode.LanOnly, false, EndToEndEncryption: false),
                    out _, "runtimeOwner", out _)) return false;
                try
                {
                    for (int index = 0; index < 20; index++)
                    {
                        using (IChatConnection peer = ServerInterface.CreateLocalConnection())
                        {
                            if (await MessageProtocol.ReadStringAsync(peer.Stream, stop.Token).ConfigureAwait(false) != NetWorker.DO_AUTH_MESSAGE)
                                return false;
                            await MessageProtocol.WriteStringAsync(peer.Stream, "REPLY:runtimeUser", stop.Token).ConfigureAwait(false);
                            if (await MessageProtocol.ReadStringAsync(peer.Stream, stop.Token).ConfigureAwait(false) != NetWorker.AUTH_OK_MESSAGE)
                                return false;
                        }
                        while (PendingCount() != 0 || NetWorker.broadcaster.ConnectionCount != 0)
                            await Task.Delay(5, stop.Token).ConfigureAwait(false);
                    }
                }
                finally { ServerInterface.StopServer(); }
                if (PendingCount() != 0) return false;
            }
            return true;
        }

        public static async Task<bool> PartialFrameDeadlineSelfTest()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            TimeSpan deadline = TimeSpan.FromMilliseconds(150);
            foreach (byte[] fragment in new[] { new byte[] { 0x81 }, new byte[] { 3, (byte)'a' } })
            {
                var (source, receiver) = MemoryDuplexStream.CreatePair();
                using (source)
                using (receiver)
                {
                    Task<string> read = MessageProtocol.ReadStringAsync(receiver, stop.Token, deadline);
                    await Task.Delay(250, stop.Token).ConfigureAwait(false);
                    if (read.IsCompleted) return false;
                    await source.WriteAsync(fragment, stop.Token).ConfigureAwait(false);
                    try { await read.WaitAsync(stop.Token).ConfigureAwait(false); return false; }
                    catch (TimeoutException) { }
                }
            }

            using var frame = new MemoryStream(MessageProtocol.EncodeFrame("Привет 🐍"));
            if (await MessageProtocol.ReadStringAsync(frame, stop.Token, deadline).ConfigureAwait(false) != "Привет 🐍")
                return false;

            foreach (bool started in new[] { false, true })
            {
                var (source, receiver) = MemoryDuplexStream.CreatePair();
                using (source)
                using (receiver)
                using (var cancel = new CancellationTokenSource())
                {
                    if (started) await source.WriteAsync(new byte[] { 0x81 }, stop.Token).ConfigureAwait(false);
                    Task<string> read = MessageProtocol.ReadStringAsync(receiver, cancel.Token, deadline);
                    cancel.Cancel();
                    try { await read.ConfigureAwait(false); return false; }
                    catch (OperationCanceledException) { }
                }
            }
            return true;
        }

        public static async Task<bool> AbandonedAnimationSelfTest()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (source, receiver) = MemoryDuplexStream.CreatePair();
            using (source)
            using (receiver)
            {
                var assembler = new ImageAnimationAssembler(TimeSpan.FromMilliseconds(150));
                string id = ImageAnimationProtocol.CreateTransferId();
                var begin = new ImageAnimationControlFrame { Kind = ImageAnimationControlKind.Begin,
                    TransferId = id, Width = 1, Height = 1, FrameCount = 2 };
                var frame = new ImageAnimationControlFrame { Kind = ImageAnimationControlKind.Frame,
                    TransferId = id, FrameIndex = 0, DelayMilliseconds = 100, PackedPixels = new byte[] { 0xa0 } };
                if (assembler.Accept(begin, out _) != ImageAnimationAssemblyResult.Accepted ||
                    assembler.Accept(frame, out _) != ImageAnimationAssemblyResult.Accepted) return false;
                var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<string> read = assembler.ReadNextAsync(receiver, stop.Token, () => expired.TrySetResult());
                await expired.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                var framesField = typeof(ImageAnimationAssembler).GetField("frames", BindingFlags.Instance | BindingFlags.NonPublic);
                if (assembler.IsActive || framesField.GetValue(assembler) != null || read.IsCompleted) return false;
                await MessageProtocol.WriteStringAsync(source, "after timeout", stop.Token).ConfigureAwait(false);
                if (await read.ConfigureAwait(false) != "after timeout") return false;

                if (assembler.Accept(begin, out _) != ImageAnimationAssemblyResult.Accepted ||
                    assembler.Accept(frame, out _) != ImageAnimationAssemblyResult.Accepted) return false;
                frame.FrameIndex = 1;
                if (assembler.Accept(frame, out _) != ImageAnimationAssemblyResult.Accepted) return false;
                var end = new ImageAnimationControlFrame { Kind = ImageAnimationControlKind.End, TransferId = id };
                return assembler.Accept(end, out var packet) == ImageAnimationAssemblyResult.Completed &&
                    packet.FrameCount == 2 && !assembler.IsActive;
            }
        }

        public static async Task<bool> SharedBatchOwnershipSelfTest()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var firstWrites = new List<ReadOnlyMemory<byte>>();
            var secondWrites = new List<ReadOnlyMemory<byte>>();
            var first = new SerialSendQueue((frame, token) => { firstWrites.Add(frame); return Task.CompletedTask; }, () => { });
            var second = new SerialSendQueue((frame, token) => { secondWrites.Add(frame); return Task.CompletedTask; }, () => { });
            try
            {
                string[] messages = { "BEGIN", "FRAME0", "FRAME1", "END" };
                EncodedFrameBatch batch = EncodedFrameBatch.Encode(messages);
                messages[0] = "changed by sender";
                Task one = first.Enqueue(batch, stop.Token);
                Task two = second.Enqueue(batch, stop.Token);
                EncodedFrameBatch following = EncodedFrameBatch.Encode(new[] { "next" });
                await Task.WhenAll(one, two, first.Enqueue(following, stop.Token), second.Enqueue(following, stop.Token))
                    .WaitAsync(stop.Token).ConfigureAwait(false);
                string[] expected = { "BEGIN", "FRAME0", "FRAME1", "END", "next" };
                if (firstWrites.Count != expected.Length || secondWrites.Count != expected.Length) return false;
                for (int index = 0; index < expected.Length; index++)
                {
                    if (!firstWrites[index].Equals(secondWrites[index])) return false;
                    using var wire = new MemoryStream(firstWrites[index].ToArray());
                    if (await MessageProtocol.ReadStringAsync(wire, stop.Token).ConfigureAwait(false) != expected[index]) return false;
                }
            }
            finally
            {
                first.Close(); second.Close();
                await Task.WhenAll(first.Completion, second.Completion).WaitAsync(stop.Token).ConfigureAwait(false);
            }

            byte[] mutable = MessageProtocol.EncodeFrame("original");
            ReadOnlyMemory<byte> captured = default;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = new SerialSendQueue(async (frame, token) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                captured = frame;
            }, () => { });
            try
            {
                Task delivery = queue.Enqueue(new[] { mutable }, stop.Token);
                await started.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                Array.Fill(mutable, (byte)0xff);
                release.TrySetResult();
                await delivery.WaitAsync(stop.Token).ConfigureAwait(false);
                using var wire = new MemoryStream(captured.ToArray());
                return await MessageProtocol.ReadStringAsync(wire, stop.Token).ConfigureAwait(false) == "original";
            }
            finally { queue.Close(); await queue.Completion.WaitAsync(stop.Token).ConfigureAwait(false); }
        }

        public static async Task<bool> SlowRecipientIsolationSelfTest()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var fastStream = new RecordingStream(blocked: false);
            using var slowStream = new RecordingStream(blocked: true);
            using var fast = new Client(new RecordingConnection(fastStream));
            using var slow = new Client(new RecordingConnection(slowStream));
            var hub = new Broadcaster();
            hub.AddConnection(fast); hub.AddConnection(slow);
            if (!hub.TryAuthenticate(fast, "fastUser") || !hub.TryAuthenticate(slow, "slowUser")) return false;
            try
            {
                string[] animation = { "BEGIN", "FRAME0", "FRAME1", "END" };
                Task broadcast = hub.BroadcastBatchAsync(null, animation, stop.Token);
                await slowStream.Started.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                await fastStream.WaitForWritesAsync(animation.Length, stop.Token).ConfigureAwait(false);
                if (broadcast.IsCompleted) return false;
                slow.Close();
                await broadcast.WaitAsync(stop.Token).ConfigureAwait(false);
                if (hub.AuthenticatedClientCount != 1) return false;
                await hub.BroadcastAsync(null, "after disconnect", stop.Token).ConfigureAwait(false);
                using var received = new MemoryStream(fastStream.Snapshot());
                foreach (string expected in animation)
                    if (await MessageProtocol.ReadStringAsync(received, stop.Token).ConfigureAwait(false) != expected) return false;
                return await MessageProtocol.ReadStringAsync(received, stop.Token).ConfigureAwait(false) == "after disconnect" &&
                    received.Position == received.Length;
            }
            finally { hub.DisconnectAll(); }
        }

        public static async Task<bool> SendQueueLimitsSelfTest()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int aborted = 0;
            var queue = new SerialSendQueue(async (frame, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }, () => Interlocked.Increment(ref aborted), maxPendingOperations: 2);
            try
            {
                EncodedFrameBatch batch = EncodedFrameBatch.Encode(new[] { "payload" });
                Task active = queue.Enqueue(batch, stop.Token);
                await started.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                Task pending = queue.Enqueue(batch, stop.Token);
                Task overflow = queue.Enqueue(batch, stop.Token);
                try { await Task.WhenAll(active, pending, overflow).WaitAsync(stop.Token).ConfigureAwait(false); return false; }
                catch (Exception ex) when (ex is IOException || ex is OperationCanceledException) { }
                if (aborted != 1 || !active.IsCompleted || !pending.IsFaulted || !overflow.IsFaulted ||
                    overflow.Exception.InnerException is not SendQueueFullException) return false;
                await queue.Completion.WaitAsync(stop.Token).ConfigureAwait(false);
            }
            finally { queue.Close(); }

            int written = 0;
            var byteLimited = new SerialSendQueue((frame, token) => { written++; return Task.CompletedTask; }, () => { }, maxPendingBytes: 3);
            try
            {
                Task tooLarge = byteLimited.Enqueue(EncodedFrameBatch.Encode(new[] { "abc" }), stop.Token);
                try { await tooLarge.ConfigureAwait(false); return false; }
                catch (SendQueueFullException) { }
                await byteLimited.Completion.WaitAsync(stop.Token).ConfigureAwait(false);
                return written == 0;
            }
            finally { byteLimited.Close(); }
        }

        public static async Task<bool> WhoisPrivacySelfTest()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            HubOptions savedOptions = ServerInterface.Options;
            var optionsProperty = typeof(ServerInterface).GetProperty("Options", BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                foreach (bool hidden in new[] { false, true })
                {
                    using var probe = new TcpListener(IPAddress.Loopback, 0);
                    probe.Start();
                    int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                    probe.Stop();
                    if (!ServerInterface.TryStartServer(port, new HubOptions(HubIpMode.LanOnly, false, hidden, false), out _, "privateAdministrator", out _)) return false;
                    using var target = new TcpClient();
                    using var requester = new TcpClient();
                    try
                    {
                        using (var impersonator = new TcpClient())
                        {
                            await impersonator.ConnectAsync(IPAddress.Loopback, port, stop.Token).ConfigureAwait(false);
                            Stream stream = impersonator.GetStream();
                            if (await MessageProtocol.ReadStringAsync(stream, stop.Token).ConfigureAwait(false) != NetWorker.DO_AUTH_MESSAGE) return false;
                            await MessageProtocol.WriteStringAsync(stream, "REPLY:privateAdministrator", stop.Token).ConfigureAwait(false);
                            if (await MessageProtocol.ReadStringAsync(stream, stop.Token).ConfigureAwait(false) != NetWorker.AUTH_ERROR_MESSAGE + ":NICKNAME_TAKEN") return false;
                        }
                        async Task Authenticate(TcpClient peer, string nick)
                        {
                            await peer.ConnectAsync(IPAddress.Loopback, port, stop.Token).ConfigureAwait(false);
                            Stream stream = peer.GetStream();
                            if (await MessageProtocol.ReadStringAsync(stream, stop.Token).ConfigureAwait(false) != NetWorker.DO_AUTH_MESSAGE) throw new IOException("Missing challenge");
                            await MessageProtocol.WriteStringAsync(stream, "REPLY:" + nick, stop.Token).ConfigureAwait(false);
                            if (await MessageProtocol.ReadStringAsync(stream, stop.Token).ConfigureAwait(false) != NetWorker.AUTH_OK_MESSAGE) throw new IOException("Auth failed");
                        }
                        async Task<string> Until(Func<string, bool> predicate)
                        {
                            while (true)
                            {
                                string message = await MessageProtocol.ReadStringAsync(requester.GetStream(), stop.Token).ConfigureAwait(false);
                                if (predicate(message)) return message;
                            }
                        }
                        await Authenticate(target, "privateOwner").ConfigureAwait(false);
                        await Authenticate(requester, "requester").ConfigureAwait(false);
                        Client victim = NetWorker.broadcaster.GetAuthenticatedClients().First(client => client.Nickname == "privateOwner");
                        victim.IpAddress = "203.0.113.53";
                        await MessageProtocol.WriteStringAsync(requester.GetStream(), WhoisProtocol.Hello, stop.Token).ConfigureAwait(false);
                        await Until(message => message == WhoisProtocol.Capabilities).ConfigureAwait(false);
                        string id = Guid.NewGuid().ToString("N");
                        if (hidden)
                            await MessageProtocol.WriteStringAsync(requester.GetStream(), WhoisProtocol.Request(id, "privateOwner") + "|includeIp=true", stop.Token).ConfigureAwait(false);
                        await MessageProtocol.WriteStringAsync(requester.GetStream(), WhoisProtocol.Request(id, "privateOwner"), stop.Token).ConfigureAwait(false);
                        string reply = await Until(message => WhoisProtocol.TryReply(message, out string replyId, out _) && replyId == id).ConfigureAwait(false);
                        if (!WhoisProtocol.TryReply(reply, out _, out WhoisInfo info) || info == null || info.AddressHidden != hidden ||
                            info.Address != (hidden ? String.Empty : "203.0.113.53")) return false;
                        if (hidden && (reply.Contains("203.0.113.53", StringComparison.Ordinal) || info.PublicAddressUnavailable)) return false;
                        string selfId = Guid.NewGuid().ToString("N");
                        await MessageProtocol.WriteStringAsync(requester.GetStream(), WhoisProtocol.Request(selfId, "requester"), stop.Token).ConfigureAwait(false);
                        string selfReply = await Until(message => WhoisProtocol.TryReply(message, out string replyId, out _) && replyId == selfId).ConfigureAwait(false);
                        if (!WhoisProtocol.TryReply(selfReply, out _, out WhoisInfo self) || self.AddressHidden != hidden ||
                            self.Address != (hidden ? String.Empty : "127.0.0.1")) return false;
                    }
                    finally { target.Close(); requester.Close(); ServerInterface.StopServer(); }
                }
                var context = new HubOptionContext { Options = new HubOptions(HubIpMode.Public, false, true), Bluetooth = BluetoothAvailability.Available };
                context.Options = HubOptionRegistry.All.First(option => option.Id == "mode").Change(context, 1);
                context.Options = HubOptionRegistry.All.First(option => option.Id == "bluetooth").Change(context, 1);
                return context.Options.HideClientIpAddresses && context.Options.IpMode == HubIpMode.LanOnly && context.Options.Bluetooth;
            }
            finally { ServerInterface.StopServer(); optionsProperty.SetValue(null, savedOptions); }
        }

        private sealed class RecordingConnection : IChatConnection
        {
            public Stream Stream { get; }
            public IPAddress RemoteIpAddress => null;
            internal RecordingConnection(Stream stream) => Stream = stream;
            public void Dispose() => Stream.Dispose();
        }

        private sealed class RecordingStream : MemoryStream
        {
            private readonly object gate = new object();
            private readonly TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private int writes;
            internal RecordingStream(bool blocked) { if (!blocked) release.SetResult(); }
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            {
                Started.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                lock (gate) { base.Write(buffer.Span); writes++; }
            }
            internal async Task WaitForWritesAsync(int count, CancellationToken token)
            {
                while (true)
                {
                    lock (gate) { if (writes >= count) return; }
                    await Task.Delay(5, token).ConfigureAwait(false);
                }
            }
            internal byte[] Snapshot() { lock (gate) return ToArray(); }
        }
    }
}
