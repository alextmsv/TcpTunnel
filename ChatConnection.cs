using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TCPTunnel
{
    internal enum ChatTransport { Tcp, Bluetooth, Local }

    internal interface IChatConnection : IDisposable
    {
        Stream Stream { get; }
        IPAddress RemoteIpAddress { get; }
        ChatTransport Transport => ChatTransport.Tcp;
    }

    internal sealed class TcpChatConnection : IChatConnection
    {
        private int disposed;
        internal TcpClient Client { get; }
        public Stream Stream { get; }
        public IPAddress RemoteIpAddress { get; }
        public ChatTransport Transport => ChatTransport.Tcp;

        internal TcpChatConnection(TcpClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            Client.NoDelay = true;
            Stream = Client.GetStream();
            RemoteIpAddress = (Client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Client.Dispose();
        }
    }

    internal sealed class LocalChatConnection : IChatConnection
    {
        private readonly MemoryDuplexStream stream;
        public Stream Stream => stream;
        public IPAddress RemoteIpAddress => null;
        public ChatTransport Transport => ChatTransport.Local;

        private LocalChatConnection(MemoryDuplexStream stream) => this.stream = stream;

        internal static (LocalChatConnection HubSide, LocalChatConnection ClientSide) CreatePair()
        {
            var (hub, client) = MemoryDuplexStream.CreatePair();
            return (new LocalChatConnection(hub), new LocalChatConnection(client));
        }

        public void Dispose() => stream.Dispose();
    }

    internal sealed class DuplexStream : Stream
    {
        private readonly Stream input;
        private readonly Stream output;
        private int disposed;

        internal DuplexStream(Stream input, Stream output)
        {
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            input.ReadAsync(buffer, offset, count, token);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            input.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            output.WriteAsync(buffer, offset, count, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            output.WriteAsync(buffer, token);
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken token) => output.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                try { input.Dispose(); } catch { }
                try { output.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class MemoryDuplexStream : Stream
    {
        private const int ChunkCapacity = 1024;
        private readonly Channel<byte[]> incoming;
        private readonly Channel<byte[]> outgoing;
        private byte[] current;
        private int currentOffset;

        private MemoryDuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing)
        {
            this.incoming = incoming;
            this.outgoing = outgoing;
        }

        internal static (MemoryDuplexStream First, MemoryDuplexStream Second) CreatePair()
        {
            var options = new BoundedChannelOptions(ChunkCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait };
            Channel<byte[]> toFirst = Channel.CreateBounded<byte[]>(options);
            Channel<byte[]> toSecond = Channel.CreateBounded<byte[]>(options);
            return (new MemoryDuplexStream(toFirst, toSecond), new MemoryDuplexStream(toSecond, toFirst));
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0)
                return 0;
            while (current == null || currentOffset >= current.Length)
            {
                if (!await incoming.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                    return 0;
                if (incoming.Reader.TryRead(out byte[] next))
                {
                    current = next;
                    currentOffset = 0;
                }
            }
            int count = Math.Min(buffer.Length, current.Length - currentOffset);
            current.AsMemory(currentOffset, count).CopyTo(buffer);
            currentOffset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0)
                return;
            try
            {
                await outgoing.Writer.WriteAsync(buffer.ToArray(), token).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new IOException("The local connection is closed.");
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                outgoing.Writer.TryComplete();
                incoming.Writer.TryComplete();
            }
            base.Dispose(disposing);
        }
    }
}
