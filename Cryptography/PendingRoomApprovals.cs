#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel.Cryptography;

internal sealed record PendingRoomRequest(long Number, string Nickname, string Fingerprint)
{
    internal string[] DisplayLines() => new[] { "[" + Number + "] " + Nickname, Fingerprint, "/approve " + Number };
}

internal sealed class PendingRoomApprovals(TimeProvider? clock = null)
{
    internal sealed class Request(PendingRoomRequest info, long created)
    {
        internal PendingRoomRequest Info { get; } = info;
        internal long Created { get; } = created;
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static long nextNumber;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<long, Request> requests = new();

    internal Request Add(string nickname, string fingerprint)
    {
        lock (gate)
        {
            Prune();
            if (requests.Values.Any(item => item.Info.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase) && item.Info.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)))
                throw new System.IO.IOException("This identity already has a pending join request.");
            if (requests.Count >= 64) throw new InvalidOperationException("Too many admission requests.");
            long number = Interlocked.Increment(ref nextNumber);
            if (number <= 0) throw new InvalidOperationException("Admission request number limit reached.");
            var request = new Request(new PendingRoomRequest(number, nickname, fingerprint), time.GetTimestamp());
            requests.Add(number, request); return request;
        }
    }
    internal PendingRoomRequest[] Snapshot()
    {
        lock (gate) { Prune(); return requests.Values.OrderBy(item => item.Info.Number).Select(item => item.Info).ToArray(); }
    }
    internal PendingRoomRequest Get(long number)
    {
        lock (gate) { Prune(); return Find(number).Info; }
    }
    internal PendingRoomRequest Approve(long number, Action<string, string> authorize)
    {
        lock (gate)
        {
            Prune(); Request request = Find(number);
            authorize(request.Info.Nickname, request.Info.Fingerprint);
            requests.Remove(number); request.Completion.TrySetResult(true); return request.Info;
        }
    }
    internal void Approve(string nickname, string fingerprint, Action<string, string> authorize)
    {
        lock (gate)
        {
            Prune(); var matching = requests.Values.Where(item => item.Info.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase) && item.Info.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 0) throw MissingRequest();
            authorize(nickname, fingerprint);
            foreach (var request in matching) { requests.Remove(request.Info.Number); request.Completion.TrySetResult(true); }
        }
    }
    internal void Remove(Request request)
    {
        lock (gate) if (requests.Remove(request.Info.Number)) request.Completion.TrySetCanceled();
    }
    internal void CancelAll()
    {
        lock (gate) { foreach (var request in requests.Values) request.Completion.TrySetCanceled(); requests.Clear(); }
    }
    private Request Find(long number) => requests.TryGetValue(number, out var request) ? request : throw MissingRequest();
    private static InvalidOperationException MissingRequest() => new(Lang.Get(TextId.ApprovalRequestGone));
    private void Prune()
    {
        foreach (var number in requests.Where(item => time.GetElapsedTime(item.Value.Created) >= Lifetime).Select(item => item.Key).ToArray())
        { requests[number].Completion.TrySetCanceled(); requests.Remove(number); }
    }
}
