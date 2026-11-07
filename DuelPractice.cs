using System;
using System.Threading;

namespace TCPTunnel;

internal sealed class DuelPracticeRound(DuelPacket rules)
{
    private readonly (int Radius, int Interval, int Lifetime) timing = DuelProtocol.Rules(rules.Level);
    private int slot = -1;
    private long lastShot = -1000;
    internal long Duration => rules.Minutes * 60000L;
    private readonly DuelScore score = new();
    internal int Score => score.Points;
    internal int Hits => score.Hits;
    internal int Streak => score.Streak;
    internal int BestStreak => score.BestStreak;
    internal DuelPacket Target { get; private set; }

    internal bool Advance(long elapsed)
    {
        int next = (int)(elapsed / timing.Interval);
        if (elapsed >= Duration || elapsed % timing.Interval >= timing.Lifetime)
        {
            slot = next;
            if (Target == null) return false;
            score.Expire();
            Target = null;
            return true;
        }
        if (slot == next) return false;
        if (Target != null || (slot >= 0 && next > slot + 1)) score.Expire();
        slot = next;
        Target = new("target", rules.Id, Slot: slot,
            X: Random.Shared.Next(timing.Radius, DuelProtocol.Width - timing.Radius),
            Y: Random.Shared.Next(116 + timing.Radius, DuelProtocol.Height - timing.Radius));
        return true;
    }

    internal bool Shoot(DuelPacket shot, long elapsed)
    {
        if (Target == null || shot.Id != rules.Id || shot.Slot != Target.Slot || elapsed >= Duration ||
            elapsed < (long)slot * timing.Interval || elapsed >= (long)slot * timing.Interval + timing.Lifetime ||
            (uint)shot.X >= DuelProtocol.Width || (uint)shot.Y >= DuelProtocol.Height || elapsed - lastShot < 35) return false;
        lastShot = elapsed;
        if (!DuelProtocol.Hits(shot.X, shot.Y, Target.X, Target.Y, timing.Radius)) { score.Miss(false); return false; }
        score.Hit(false);
        Target = null;
        return true;
    }
}

internal static class DuelPractice
{
    private static int active;
    internal static bool Active => Volatile.Read(ref active) != 0;

    internal static bool Start(DuelPacket options)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return false;
        try
        {
            var profile = ApplicationSettings.GetPracticeProfile();
            var round = options with { Id = Guid.NewGuid() };
            var best = ApplicationSettings.ReadPracticeStats(profile, round.Level, round.Minutes);
            var practice = new DuelPracticeRound(round);
            DuelWindow window = null;
            window = new DuelWindow(round, _ => Volatile.Write(ref active, 0), practice, best.Points ?? 0,
                score =>
                {
                    var (record, saved) = ApplicationSettings.SavePracticeStats(profile, round.Level, round.Minutes, score, practice.Hits, practice.BestStreak);
                    window.SetPracticeRecord(record.Points ?? 0, saved, record.Hits, record.Streak);
                });
            window.SetPracticeRecord(best.Points ?? 0, true, best.Hits, best.Streak);
            window.Start();
            return true;
        }
        catch
        {
            Volatile.Write(ref active, 0);
            throw;
        }
    }
}
