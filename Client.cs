using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public sealed class Client : IDisposable
    {
        private const double MessagesPerSecond = 5.0;
        private const double BurstCapacity = 20.0;
        private const double ImagesPerSecond = 0.2;
        private const double ImageBurstCapacity = 2.0;

        private readonly object rateLock = new object();
        private readonly object snakeProfileLock = new object();
        private readonly SerialSendQueue sendQueue;
        private double availableTokens = BurstCapacity;
        private double availableImageTokens = ImageBurstCapacity;
        private long lastRefillTimestamp = Stopwatch.GetTimestamp();
        private long lastImageRefillTimestamp = Stopwatch.GetTimestamp();
        private SnakeProfile snakeProfile;
        private long snakeProfileTimestamp;
        private bool hasSnakeProfile;
        private int closed;

        public Client(TcpClient tcpClient)
        {
            TcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
            TcpClient.NoDelay = true;
            Stream = TcpClient.GetStream();
            sendQueue = new SerialSendQueue(
                (frame, token) => Stream.WriteAsync(frame, token).AsTask(), Close);
        }

        public TcpClient TcpClient { get; }
        public NetworkStream Stream { get; }
        public string IpAddress { get; set; }
        public string Nickname { get; set; }
        public bool IsAuthenticated { get; set; }
        public bool IsReady { get; internal set; }

        internal void UpdateSnakeProfile(SnakeProfile profile)
        {
            lock (snakeProfileLock)
            {
                snakeProfile = profile;
                snakeProfileTimestamp = Stopwatch.GetTimestamp();
                hasSnakeProfile = true;
            }
        }

        internal bool TryGetSnakeProfile(out SnakeProfile profile)
        {
            lock (snakeProfileLock)
            {
                profile = snakeProfile;
                if (!hasSnakeProfile)
                    return false;

                if (!profile.Enabled || profile.Paused)
                    return true;

                long delayTicks = Math.Max(1L, Stopwatch.Frequency * profile.DelayMilliseconds / 1000L);
                long elapsedTicks = Stopwatch.GetTimestamp() - snakeProfileTimestamp;
                long moves = elapsedTicks / delayTicks;
                profile.Step = (int)((profile.Step + moves) % SnakeProtocol.ReferencePerimeterLength);
                return true;
            }
        }

        public bool TryConsumeMessageToken()
        {
            lock (rateLock)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsedSeconds = (double)(now - lastRefillTimestamp) / Stopwatch.Frequency;
                availableTokens = Math.Min(BurstCapacity, availableTokens + elapsedSeconds * MessagesPerSecond);
                lastRefillTimestamp = now;

                if (availableTokens < 1.0)
                    return false;

                availableTokens -= 1.0;
                return true;
            }
        }

        public bool TryConsumeImageToken()
        {
            lock (rateLock)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsedSeconds = (double)(now - lastImageRefillTimestamp) / Stopwatch.Frequency;
                availableImageTokens = Math.Min(
                    ImageBurstCapacity,
                    availableImageTokens + elapsedSeconds * ImagesPerSecond);
                lastImageRefillTimestamp = now;

                if (availableImageTokens < 1.0)
                    return false;

                availableImageTokens -= 1.0;
                return true;
            }
        }

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            return sendQueue.Enqueue(new[] { MessageProtocol.EncodeFrame(message) }, cancellationToken);
        }

        internal Task SendBatchAsync(IReadOnlyList<string> messages, CancellationToken cancellationToken)
        {
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));
            if (messages.Count == 0)
                return Task.CompletedTask;

            var frames = new byte[messages.Count][];
            for (int index = 0; index < messages.Count; index++)
                frames[index] = MessageProtocol.EncodeFrame(messages[index] ?? throw new ArgumentException("A message batch cannot contain null values.", nameof(messages)));
            return sendQueue.Enqueue(frames, cancellationToken);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0)
                return;

            sendQueue.Close();
            try { TcpClient.Close(); } catch { }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
