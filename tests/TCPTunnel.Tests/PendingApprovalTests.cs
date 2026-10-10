using TCPTunnel.Cryptography;
using System.Security.Cryptography;

internal static class PendingApprovalTests
{
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
    public static bool NumberedApprovalIdentityAndExpirySelfTest()
    {
        var clock = new Clock(); var pending = new PendingRoomApprovals(clock);
        using var owner = new NativeMlsSession();
        var identities = new RoomIdentityGate(RandomNumberGenerator.GetBytes(32), owner);
        string firstKey = new('A', 64), otherKey = new('B', 64);
        var first = pending.Add("tmsvalex", firstKey); var other = pending.Add("tmsvalex", otherKey);
        string[] card = first.Info.DisplayLines();
        if (card.Length != 3 || card[1] != firstKey || card[1].Contains('|') || card[1].Contains('\n') || card[2] != "/approve " + first.Info.Number) return false;
        if (first.Info.Number >= other.Info.Number || pending.Snapshot().Length != 2) return false;
        var approved = pending.Approve(first.Info.Number, identities.Approve);
        if (approved != first.Info || !first.Completion.Task.IsCompletedSuccessfully || other.Completion.Task.IsCompleted) return false;
        try { pending.Approve(other.Info.Number, identities.Approve); return false; }
        catch (InvalidOperationException) { }
        if (other.Completion.Task.IsCompleted || !identities.IsApproved("tmsvalex", firstKey)) return false;
        bool called = false;
        try { pending.Approve(first.Info.Number, (_, _) => called = true); return false; }
        catch (InvalidOperationException) { }
        if (called) return false;
        clock.Advance(TimeSpan.FromMinutes(2));
        try { pending.Approve(other.Info.Number, (_, _) => called = true); return false; }
        catch (InvalidOperationException) { }
        if (called || !other.Completion.Task.IsCanceled || pending.Snapshot().Length != 0) return false;
        var replacement = new PendingRoomApprovals(clock).Add("newUser", firstKey);
        return replacement.Info.Number > other.Info.Number;
    }
    public static bool ExplicitApprovalAndFailedSaveSelfTest()
    {
        var pending = new PendingRoomApprovals(); string key = new('C', 64);
        var first = pending.Add("firstUser", key); var third = pending.Add("otherUser", key);
        try { pending.Add("FIRSTUSER", key); return false; } catch (IOException) { }
        try { pending.Approve(first.Info.Number, (_, _) => throw new IOException("Simulated trust save failure.")); return false; }
        catch (IOException) { }
        if (first.Completion.Task.IsCompleted || pending.Snapshot().Length != 2) return false;
        string? nickname = null, fingerprint = null;
        pending.Approve("FIRSTUSER", key.ToLowerInvariant(), (name, fp) => { nickname = name; fingerprint = fp; });
        if (nickname != "FIRSTUSER" || fingerprint != key.ToLowerInvariant() || !first.Completion.Task.IsCompletedSuccessfully || third.Completion.Task.IsCompleted) return false;
        pending.Remove(third);
        bool called = false;
        try { pending.Approve(third.Info.Number, (_, _) => called = true); return false; }
        catch (InvalidOperationException) { }
        return !called && third.Completion.Task.IsCanceled && pending.Snapshot().Length == 0;
    }
}
