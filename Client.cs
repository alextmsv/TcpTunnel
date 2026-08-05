using System;
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
        private const int SendTimeoutMilliseconds = 5000;

        private readonly object rateLock = new object();
        private readonly object snakeProfileLock = new object();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private double availableTokens = BurstCapacity;
        private long lastRefillTimestamp = Stopwatch.GetTimestamp();
        private SnakeProfile snakeProfile;
        private long snakeProfileTimestamp;
        private bool hasSnakeProfile;
        private int closed;

        public Client(TcpClient tcpClient)
        {
            TcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
            TcpClient.NoDelay = true;
            Stream = TcpClient.GetStream();
        }

        public TcpClient TcpClient { get; }
        public NetworkStream Stream { get; }
        public string IpAddress { get; set; }
        public string Nickname { get; set; }
        public bool IsAuthenticated { get; set; }

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

        public async Task SendAsync(string message, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref closed) != 0)
                throw new ObjectDisposedException(nameof(Client));

            await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Task writeTask = MessageProtocol.WriteStringAsync(Stream, message, cancellationToken);
                using (var timeoutCancellation = new CancellationTokenSource())
                {
                    Task timeoutTask = Task.Delay(SendTimeoutMilliseconds, timeoutCancellation.Token);
                    Task completed = await Task.WhenAny(writeTask, timeoutTask).ConfigureAwait(false);
                    if (completed != writeTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Close();
                        try { await writeTask.ConfigureAwait(false); } catch { }
                        throw new TimeoutException("Клиент не принимает сообщения.");
                    }

                    timeoutCancellation.Cancel();
                    await writeTask.ConfigureAwait(false);
                }
            }
            finally
            {
                sendLock.Release();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0)
                return;

            try { TcpClient.Close(); } catch { }
        }

        public void Dispose()
        {
            Close();
            sendLock.Dispose();
        }
    }
}
