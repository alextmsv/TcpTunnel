using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private readonly IChatConnection connection;
        private double availableTokens = BurstCapacity;
        private double availableImageTokens = ImageBurstCapacity;
        private double availableControlTokens = 4;
        private long lastControlRefillTimestamp = Stopwatch.GetTimestamp();
        private long lastRefillTimestamp = Stopwatch.GetTimestamp();
        private long lastImageRefillTimestamp = Stopwatch.GetTimestamp();
        private SnakeProfile snakeProfile;
        private long snakeProfileTimestamp;
        private bool hasSnakeProfile;
        private int closed;
        internal volatile bool SupportsFileTransfer;
        internal volatile bool SupportsDuel;
        internal volatile bool SupportsDuelOptions;
        private double availableFileTokens = 256;
        private long lastFileRefill = Stopwatch.GetTimestamp();
        internal bool TryConsumeFileToken()
        {
            lock (rateLock)
            {
                long now = Stopwatch.GetTimestamp();
                availableFileTokens = Math.Min(256, availableFileTokens + (double)(now - lastFileRefill) / Stopwatch.Frequency * 512);
                lastFileRefill = now;
                if (availableFileTokens < 1) return false;
                availableFileTokens--; return true;
            }
        }
        private int ipDisclosureAllowed;
        internal bool IpDisclosureAllowed { get => Volatile.Read(ref ipDisclosureAllowed) != 0; set => Volatile.Write(ref ipDisclosureAllowed, value ? 1 : 0); }

        public Client(TcpClient tcpClient) : this(new TcpChatConnection(tcpClient)) { }

        internal Client(IChatConnection connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
            ipDisclosureAllowed = connection.Transport == ChatTransport.Local ? 1 : 0;
            Stream = connection.Stream;
            sendQueue = new SerialSendQueue(
                async (frame, token) =>
                {
                    await Stream.WriteAsync(frame, token).ConfigureAwait(false);
                    await Stream.FlushAsync(token).ConfigureAwait(false);
                }, Close);
        }

        public TcpClient TcpClient => (connection as TcpChatConnection)?.Client;
        public Stream Stream { get; internal set; }
        internal bool SecureWireEnabled { get; set; }
        internal bool HideClientIpAddresses { get; } = ServerInterface.Options.HideClientIpAddresses;
        internal string ObservedIpAddress => connection.RemoteIpAddress?.ToString() ?? String.Empty;
        internal ChatTransport Transport => connection.Transport;
        public string IpAddress { get; set; }
        public string Nickname { get; set; }
        internal string OriginalNickname { get; set; }
        public bool IsAuthenticated { get; set; }
        public bool IsReady { get; internal set; }
        internal bool SupportsHubStatus { get; set; }
        internal bool SupportsNicknameSnake { get; set; }
        internal bool InitialNicknameSnakeSync { get; set; }
        internal ConnectionAdmission.Lease Admission { get; set; }
        internal bool PromoteAdmission() => Admission?.Promote() ?? true;
        internal string AdmissionOrigin => connection.Transport == ChatTransport.Tcp ? "tcp:" + (connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown") :
            connection is BluetoothChatConnection bluetooth ? "bt:" + bluetooth.RemoteAddress.ToString("X12") : "bt:unknown";
        internal WhoisPeer Diagnostics { get; } = new WhoisPeer();

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

        internal bool TryConsumeControlToken()
        {
            lock (rateLock)
            {
                long now = Stopwatch.GetTimestamp();
                availableControlTokens = Math.Min(4, availableControlTokens +
                    (double)(now - lastControlRefillTimestamp) / Stopwatch.Frequency * 2);
                lastControlRefillTimestamp = now;
                if (availableControlTokens < 1) return false;
                availableControlTokens--;
                return true;
            }
        }

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            return SecureWireEnabled ? SendSecureAsync(1, System.Text.Encoding.UTF8.GetBytes(message), cancellationToken) : SendEncodedAsync(EncodedFrameBatch.Encode(new[] { message }), cancellationToken);
        }

        internal Task SendBatchAsync(IReadOnlyList<string> messages, CancellationToken cancellationToken)
        {
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));
            if (messages.Count == 0)
                return Task.CompletedTask;

            return SendEncodedAsync(EncodedFrameBatch.Encode(messages), cancellationToken);
        }

        internal Task SendEncodedAsync(EncodedFrameBatch frames, CancellationToken cancellationToken) =>
            sendQueue.Enqueue(frames, cancellationToken);

        internal async Task SendSecureAsync(byte kind, byte[] payload, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            await SendEncodedAsync(TCPTunnel.Cryptography.SecureWire.EncodeBatch(kind, payload), deadline.Token).ConfigureAwait(false);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0)
                return;

            sendQueue.Close();
            Admission?.Dispose();
            if (!Object.ReferenceEquals(Stream, connection.Stream)) { try { Stream.Dispose(); } catch { } }
            try { connection.Dispose(); } catch { }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
