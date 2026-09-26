using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;

namespace TCPTunnel
{
    internal sealed class WhoisPeer
    {
        private readonly object gate = new object();
        private string probeId;
        private long sentAt, receivedAt, messages;
        private int? ping;
        private readonly Dictionary<string, long> notifications = new(StringComparer.OrdinalIgnoreCase);
        internal bool Supported { get; set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal long Messages => Interlocked.Read(ref messages);
        internal void CountMessage() => Interlocked.Increment(ref messages);
        internal bool ShouldNotify(string requester)
        {
            lock (gate)
            {
                long now = Environment.TickCount64;
                if (notifications.TryGetValue(requester, out long previous) && now - previous < 5000) return false;
                if (notifications.Count >= ServerInterface.MaxConnectedClients && !notifications.ContainsKey(requester))
                {
                    string oldest = null;
                    long oldestAt = long.MaxValue;
                    foreach (var item in notifications)
                        if (item.Value < oldestAt) { oldest = item.Key; oldestAt = item.Value; }
                    if (oldest != null) notifications.Remove(oldest);
                }
                notifications[requester] = now;
                return true;
            }
        }
        internal void SetSize(int width, int height)
        {
            lock (gate)
            {
                Width = width; Height = height;
            }
        }
        internal (int Width, int Height) GetSize() { lock (gate) return (Width, Height); }
        private int? signal;
        private long signalAt;
        internal void SetSignal(int dbm)
        {
            lock (gate)
            {
                signal = dbm;
                signalAt = Stopwatch.GetTimestamp();
            }
        }
        internal int? SignalDbm
        {
            get { lock (gate) return signal.HasValue && Stopwatch.GetElapsedTime(signalAt).TotalSeconds <= 30 ? signal : null; }
        }
        internal int? PingMilliseconds
        {
            get { lock (gate) return Stopwatch.GetElapsedTime(receivedAt).TotalSeconds <= 30 ? ping : null; }
        }
        internal string BeginProbe()
        {
            lock (gate) { probeId = Guid.NewGuid().ToString("N"); sentAt = Stopwatch.GetTimestamp(); return probeId; }
        }
        internal void ReceivePong(string id)
        {
            lock (gate)
            {
                if (id != probeId || probeId == null) return;
                long elapsed = (long)Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
                probeId = null;
                if (elapsed > 60000) return;
                ping = (int)elapsed;
                receivedAt = Stopwatch.GetTimestamp();
            }
        }
    }
}
