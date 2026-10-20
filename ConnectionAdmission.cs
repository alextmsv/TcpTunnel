using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TCPTunnel
{
    internal sealed class ConnectionAdmission
    {
        internal const int MaximumPending = 24;
        internal const int MaximumPendingPerOrigin = 4;
        private sealed class Origin { internal int Pending; internal double Tokens = 8; internal long Last = Stopwatch.GetTimestamp(); }
        internal sealed class Lease : IDisposable
        {
            private readonly ConnectionAdmission owner;
            internal readonly string origin;
            internal int state;
            internal Lease(ConnectionAdmission owner, string origin) { this.owner = owner; this.origin = origin; }
            internal bool Promote() => owner.Promote(this);
            public void Dispose() => owner.Release(this);
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Origin> origins = new Dictionary<string, Origin>(StringComparer.Ordinal);
        private int pending, active;
        private readonly int maximum;
        private readonly bool reserveOwner;
        private int localActive;
        internal ConnectionAdmission(int maximum = ServerInterface.MaxConnectedClients, bool reserveOwner = false)
        {
            if (maximum is < 1 or > ServerInterface.MaxConnectedClients) throw new ArgumentOutOfRangeException(nameof(maximum));
            this.maximum = maximum; this.reserveOwner = reserveOwner;
        }
        internal Lease TryAcquire(string origin, bool local)
        {
            lock (gate)
            {
                if (local) return new Lease(this, null);
                long now = Stopwatch.GetTimestamp();
                if (!origins.TryGetValue(origin, out Origin entry))
                {
                    if (origins.Count >= 1024)
                        foreach (string expired in origins.Where(item => item.Value.Pending == 0 && (double)(now - item.Value.Last) / Stopwatch.Frequency > 60).Select(item => item.Key).ToArray()) origins.Remove(expired);
                    if (origins.Count >= 1024) return null;
                    origins.Add(origin, entry = new Origin());
                }
                entry.Tokens = Math.Min(8, entry.Tokens + (double)(now - entry.Last) / Stopwatch.Frequency); entry.Last = now;
                if (pending >= MaximumPending || entry.Pending >= MaximumPendingPerOrigin || entry.Tokens < 1) return null;
                entry.Tokens--; entry.Pending++; pending++;
                return new Lease(this, origin);
            }
        }
        private bool Promote(Lease lease)
        {
            lock (gate)
            {
                if (lease.state == 1) return true;
                if (lease.state != 0) return false;
                if (reserveOwner && lease.origin == null)
                {
                    if (localActive != 0) return false;
                    localActive++; lease.state = 1; return true;
                }
                if (active >= maximum - (reserveOwner ? 1 : 0)) return false;
                if (lease.origin != null) { origins[lease.origin].Pending--; pending--; }
                active++; lease.state = 1; return true;
            }
        }
        private void Release(Lease lease)
        {
            lock (gate)
            {
                if (lease.state == 2) return;
                if (lease.state == 1) { if (reserveOwner && lease.origin == null) localActive--; else active--; }
                else if (lease.origin != null) { origins[lease.origin].Pending--; pending--; }
                lease.state = 2;
            }
        }
    }
}
