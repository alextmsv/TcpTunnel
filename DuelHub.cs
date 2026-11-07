using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel;

internal static class DuelHub
{
    private const int Warmup = 1500, MaxOneWay = 60;
    private sealed record Offer(Client From, Client To, DuelPacket Rules, long Created);
    private sealed class Round(Offer offer)
    {
        internal readonly Client[] Players = [offer.From, offer.To];
        internal readonly DuelPacket Rules = offer.Rules;
        internal readonly Guid Id = offer.Rules.Id;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly int[] Scores = new int[2], X = new int[2], Y = new int[2];
        internal readonly long[] LastShot = [-1000, -1000];
        internal readonly bool[] Hit = new bool[2];
        internal readonly bool[] Delivered = new bool[2];
        internal readonly int[] Samples = new int[2], Rtt = [Int32.MaxValue, Int32.MaxValue], Half = new int[2];
        internal readonly long[] ProbeAt = new long[2];
        internal readonly Guid[] Probes = new Guid[2];
        internal int Slot = -1;
        internal int Candidate = -1;
        internal long CandidateTime, DecideAt = long.MaxValue, NextWake = long.MaxValue;
        internal long Deadline => Warmup + Rules.Minutes * 60000L;
        internal Timer Timer;
        internal readonly DuelScore[] Points = [new(), new()];
        internal readonly Dictionary<int, TargetState> Targets = new();
        internal int NextTarget;
    }
    private sealed class TargetState(DuelPacket packet, int owner, int cell, long start)
    {
        internal readonly DuelPacket Packet = packet;
        internal readonly int Owner = owner, Cell = cell;
        internal readonly long Start = start;
        internal readonly long[] Sent = [-1, -1];
        internal int Candidate = -1;
        internal long Estimated, DecideAt;
    }

    private static readonly object Gate = new();
    private static readonly List<Offer> Offers = new();
    private static readonly Dictionary<Client, Round> Rounds = new();

    internal static bool Handle(Client client, string message)
    {
        if (!DuelProtocol.IsControl(message)) return false;
        if (!client.IsReady || !DuelProtocol.TryDecode(message, out var packet)) return true;
        lock (Gate)
        {
            if (!client.IsReady) return true;
            if (packet.Kind is "hello" or "hello2")
            {
                if (!client.SupportsDuel) { client.SupportsDuel = true; Send(client, new("caps")); }
                if (packet.Kind == "hello2" && !client.SupportsDuelOptions)
                { client.SupportsDuelOptions = true; Send(client, new("caps2")); }
                return true;
            }
            if (!client.SupportsDuel) return true;
            if (packet.Kind is "offer" or "accept")
            {
                if (!client.TryConsumeControlToken()) return true;
                Offers.RemoveAll(o => Stopwatch.GetElapsedTime(o.Created).TotalSeconds >= 45 || !o.From.IsReady || !o.To.IsReady);
                if (packet.Kind == "offer") Invite(client, packet);
                else Accept(client, packet.Id);
            }
            else if (Rounds.TryGetValue(client, out var round) && packet.Id == round.Id)
            {
                int player = round.Players[0] == client ? 0 : 1;
                if (round.Clock.ElapsedMilliseconds >= round.Deadline) Finish(round, -1);
                else if (packet.Kind == "quit") Finish(round, player);
                else if (packet.Kind == "shot") { if (round.Rules.Version == 2) ShootNew(round, player, packet); else Shoot(round, player, packet); }
                else if (packet.Kind == "pong" && round.Slot < 0 && packet.Nonce != Guid.Empty && packet.Nonce == round.Probes[player])
                {
                    int rtt = (int)(round.Clock.ElapsedMilliseconds - round.ProbeAt[player]);
                    round.Rtt[player] = Math.Min(round.Rtt[player], rtt);
                    round.Samples[player]++;
                    round.Probes[player] = Guid.Empty;
                    if (round.Samples[player] < 3) Probe(round, player);
                }
            }
        }
        return true;
    }

    private static void Invite(Client client, DuelPacket packet)
    {
        if (!DuelProtocol.ValidOptions(packet) || !NetWorker.IsNicknameValid(packet.Peer))
        { Send(client, new("error", Outcome: "invalid")); return; }
        var other = NetWorker.broadcaster.GetAuthenticatedClients(client).FirstOrDefault(p =>
            p.SupportsDuel && p.Nickname.Equals(packet.Peer, StringComparison.OrdinalIgnoreCase));
        if (other == null) { Send(client, new("error", Outcome: "unavailable")); return; }
        if (packet.Version == 2 && (!client.SupportsDuelOptions || !other.SupportsDuelOptions))
        { Send(client, new("error", Outcome: "update")); return; }
        if (Rounds.ContainsKey(client) || Rounds.ContainsKey(other) || Offers.Count >= 32 ||
            Offers.Any(o => o.From == client || o.To == client || o.From == other || o.To == other))
        { Send(client, new("error", Outcome: "busy")); return; }
        var rules = new DuelPacket("invite", Guid.NewGuid(), Peer: client.Nickname, Level: packet.Level, Minutes: packet.Minutes, Blitz: packet.Blitz,
            Version: packet.Version, Mode: packet.Mode, Size: packet.Size, Count: packet.Count);
        Offers.Add(new(client, other, rules, Stopwatch.GetTimestamp()));
        Send(other, rules);
        Send(client, new("sent", rules.Id, Peer: other.Nickname));
    }

    private static void Accept(Client client, Guid id)
    {
        var offer = Offers.Find(o => o.To == client && o.Rules.Id == id);
        if (offer == null) { Send(client, new("error", Outcome: "no-invite")); return; }
        Offers.Remove(offer);
        if (!offer.From.IsReady || Rounds.ContainsKey(client) || Rounds.ContainsKey(offer.From))
        { Send(client, new("error", Outcome: "unavailable")); return; }
        var round = new Round(offer);
        for (int i = 0; i < 2; i++)
        {
            Rounds.Add(round.Players[i], round);
            Send(round.Players[i], round.Rules with { Kind = "start", Id = round.Id, Peer = round.Players[1 - i].Nickname });
            if (round.Rules.Blitz || round.Rules.Version == 2) Probe(round, i);
        }
        round.Timer = new Timer(_ => Tick(round), null, Timeout.Infinite, Timeout.Infinite);
        Schedule(round, 0, Warmup);
    }

    private static void Probe(Round round, int player)
    {
        Guid nonce = round.Probes[player] = Guid.NewGuid();
        round.ProbeAt[player] = round.Clock.ElapsedMilliseconds;
        Send(round.Players[player], new("ping", round.Id, Nonce: nonce));
    }

    private static void Schedule(Round round, long now, long at)
    {
        if (at >= round.NextWake) return;
        round.NextWake = at;
        round.Timer.Change((int)Math.Max(1, at - now), Timeout.Infinite);
    }

    private static void Tick(Round round)
    {
        lock (Gate)
        {
            if (!Rounds.TryGetValue(round.Players[0], out var active) || active != round) return;
            long elapsed = round.Clock.ElapsedMilliseconds;
            round.NextWake = long.MaxValue;
            if (elapsed >= round.Deadline) { Finish(round, -1); return; }
            if (elapsed < Warmup) { Schedule(round, elapsed, Warmup); return; }
            if (round.Rules.Version == 2) { TickNew(round, elapsed); return; }
            var rules = DuelProtocol.Rules(round.Rules.Level);
            int slot = (int)((elapsed - Warmup) / rules.Interval);
            long start = Warmup + (long)slot * rules.Interval;
            Schedule(round, elapsed, Math.Min(start + rules.Interval, round.Deadline));
            if (round.Candidate >= 0 && (elapsed >= round.DecideAt || slot != round.Slot))
                Resolve(round);
            if (slot != round.Slot)
            {
                if (round.Slot < 0 && round.Samples[0] > 0 && round.Samples[1] > 0)
                    for (int i = 0; i < 2; i++) round.Half[i] = Math.Min(MaxOneWay, round.Rtt[i] / 2);
                round.Slot = slot;
                for (int i = 0; i < 2; i++)
                {
                    bool shared = i == 1 && round.Rules.Blitz;
                    round.X[i] = shared ? round.X[0] : Random.Shared.Next(rules.Radius, DuelProtocol.Width - rules.Radius);
                    round.Y[i] = shared ? round.Y[0] : Random.Shared.Next(45 + rules.Radius, DuelProtocol.Height - rules.Radius);
                    round.Hit[i] = round.Delivered[i] = false;
                }
            }
            int slowest = Math.Max(round.Half[0], round.Half[1]);
            for (int i = 0; i < 2; i++)
            {
                if (round.Delivered[i]) continue;
                long dispatch = start + slowest - round.Half[i];
                if (elapsed < dispatch) { Schedule(round, elapsed, dispatch); continue; }
                round.Delivered[i] = true;
                Send(round.Players[i], new("target", round.Id, Slot: slot, X: round.X[i], Y: round.Y[i]));
            }
            if (round.Candidate >= 0) Schedule(round, elapsed, round.DecideAt);
        }
    }

    private static void TickNew(Round round, long now)
    {
        bool mine = round.Rules.Mode == "minefield";
        var timing = DuelProtocol.Rules(round.Rules.Level);
        int wave = mine ? 0 : (int)((now - Warmup) / timing.Interval);
        if (round.Slot < 0 && round.Samples[0] > 0 && round.Samples[1] > 0)
            for (int i = 0; i < 2; i++) round.Half[i] = Math.Min(MaxOneWay, round.Rtt[i] / 2);
        Schedule(round, now, round.Deadline);
        if (!mine) Schedule(round, now, Math.Min(round.Deadline, Warmup + (wave + 1L) * timing.Interval));
        foreach (var target in round.Targets.Values.ToArray())
        {
            if (target.Candidate >= 0 && (now >= target.DecideAt || wave != round.Slot))
                AwardNew(round, target, target.Candidate, now);
            else if (!mine && wave != round.Slot)
            {
                if (target.Owner >= 0) round.Points[target.Owner].Expire();
                round.Targets.Remove(target.Packet.Slot);
            }
        }
        if (wave != round.Slot)
        {
            if (!mine && round.Slot >= 0 && wave > round.Slot + 1)
                foreach (var score in round.Points) score.Expire();
            round.Slot = wave;
            int count = mine ? round.Rules.Count : 1;
            for (int i = 0; i < count; i++)
            {
                AddNewTarget(round, round.Rules.Blitz ? -1 : 0, now);
                if (!round.Rules.Blitz) AddNewTarget(round, 1, now);
            }
        }
        foreach (var target in round.Targets.Values)
        {
            DispatchNew(round, target, now);
            if (target.Candidate >= 0) Schedule(round, now, target.DecideAt);
        }
    }

    private static void AddNewTarget(Round round, int owner, long now, int avoid = -1)
    {
        int cell = -1;
        if (round.Rules.Mode == "minefield")
        {
            var occupied = round.Targets.Values.Where(t => t.Owner == owner).Select(t => t.Cell).ToHashSet();
            var free = Enumerable.Range(0, DuelArena.Grid.Length).Where(i => i != avoid && !occupied.Contains(i)).ToArray();
            if (free.Length == 0) return;
            cell = free[Random.Shared.Next(free.Length)];
        }
        var packet = DuelArena.Target(round.Rules, round.NextTarget++, cell);
        round.Targets.Add(packet.Slot, new TargetState(packet, owner, cell, now));
    }

    private static void DispatchNew(Round round, TargetState target, long now)
    {
        int slowest = Math.Max(round.Half[0], round.Half[1]);
        for (int i = 0; i < 2; i++)
        {
            if (target.Sent[i] >= 0 || (target.Owner >= 0 && target.Owner != i)) continue;
            long dispatch = target.Start + (round.Rules.Blitz ? slowest - round.Half[i] : 0);
            if (now < dispatch) { Schedule(round, now, dispatch); continue; }
            target.Sent[i] = now;
            Send(round.Players[i], target.Packet);
        }
    }

    private static bool TargetAvailable(Round round, TargetState target, int player, long now) =>
        target.Sent[player] >= 0 && (round.Rules.Mode == "minefield" ||
        now < target.Sent[player] + DuelProtocol.Rules(round.Rules.Level).Lifetime + 2 * round.Half[player]);

    private static void ShootNew(Round round, int player, DuelPacket packet)
    {
        long now = round.Clock.ElapsedMilliseconds;
        if ((uint)packet.X >= DuelProtocol.Width || (uint)packet.Y >= DuelProtocol.Height || now - round.LastShot[player] < 35) return;
        round.LastShot[player] = now;
        round.Targets.TryGetValue(packet.Slot, out var target);
        if (target == null || !TargetAvailable(round, target, player, now))
        {
            if (packet.Slot == -1 && round.Targets.Values.Any(t => TargetAvailable(round, t, player, now))) MissNew(round, player);
            return;
        }
        long age = Math.Max(0, now - target.Sent[player] - 2 * round.Half[player]);
        var pos = DuelArena.Position(target.Packet, round.Rules.Level, age);
        bool hit = DuelProtocol.Hits(packet.X, packet.Y, pos.X, pos.Y, target.Packet.Radius);
        if (!hit && target.Packet.Motion != 0)
        {
            foreach (int shift in new[] { -30, 30 })
            {
                pos = DuelArena.Position(target.Packet, round.Rules.Level, Math.Max(0, age + shift));
                if (DuelProtocol.Hits(packet.X, packet.Y, pos.X, pos.Y, target.Packet.Radius)) { hit = true; break; }
            }
        }
        if (!hit) { MissNew(round, player); return; }
        if (!round.Rules.Blitz) { AwardNew(round, target, player, now); return; }
        if (target.Candidate == player) return;
        long estimated = now - round.Half[player];
        if (target.Candidate >= 0)
        {
            int winner = now < target.DecideAt && estimated < target.Estimated ? player : target.Candidate;
            AwardNew(round, target, winner, now);
            return;
        }
        int wait = Math.Max(0, round.Half[1 - player] - round.Half[player]);
        if (wait == 0) { AwardNew(round, target, player, now); return; }
        target.Candidate = player; target.Estimated = estimated;
        target.DecideAt = Math.Min(round.Deadline, now + wait);
        Schedule(round, now, target.DecideAt);
    }

    private static void MissNew(Round round, int player)
    {
        if (round.Rules.Blitz) return;
        round.Points[player].Miss(false);
        SendScores(round, player, -1, -1);
    }

    private static void AwardNew(Round round, TargetState target, int player, long now)
    {
        if (!round.Targets.Remove(target.Packet.Slot)) return;
        int delta = round.Points[player].Hit(round.Rules.Blitz);
        SendScores(round, player, target.Packet.Slot, delta);
        if (round.Rules.Mode != "minefield" || now >= round.Deadline) return;
        int id = round.NextTarget;
        AddNewTarget(round, target.Owner, now, target.Cell);
        if (round.Targets.TryGetValue(id, out var replacement)) DispatchNew(round, replacement, now);
    }

    private static void SendScores(Round round, int player, int slot, int delta)
    {
        for (int i = 0; i < 2; i++) round.Scores[i] = round.Rules.Blitz ? round.Points[i].Hits : round.Points[i].Points;
        for (int i = 0; i < 2; i++)
            Send(round.Players[i], new("score", round.Id, Slot: slot, You: round.Scores[i], Them: round.Scores[1-i], Version: 2,
                Clear: slot >= 0 && (i == player || round.Rules.Blitz) ? 1 : 0,
                Delta: i == player ? delta : 0, Streak: round.Points[i].Streak));
    }

    private static void Shoot(Round round, int player, DuelPacket packet)
    {
        long elapsed = round.Clock.ElapsedMilliseconds;
        if (elapsed - round.LastShot[player] < 35) return;
        round.LastShot[player] = elapsed;
        var rules = DuelProtocol.Rules(round.Rules.Level);
        int slowest = Math.Max(round.Half[0], round.Half[1]);
        if (round.Slot < 0 || !round.Delivered[player] || packet.Slot != round.Slot || round.Hit[player] ||
            elapsed >= Warmup + (long)round.Slot * rules.Interval + rules.Lifetime + slowest + round.Half[player] ||
            !DuelProtocol.Hits(packet.X, packet.Y, round.X[player], round.Y[player], rules.Radius)) return;
        if (round.Rules.Blitz)
        {
            if (round.Candidate >= 0 && elapsed >= round.DecideAt) { Resolve(round); return; }
            if (round.Candidate == player) return;
            long estimated = elapsed - round.Half[player];
            if (round.Candidate >= 0)
            {
                if (estimated < round.CandidateTime) round.Candidate = player;
                Resolve(round);
                return;
            }
            round.Candidate = player;
            round.CandidateTime = estimated;
            int wait = Math.Max(0, round.Half[1 - player] - round.Half[player]);
            if (wait == 0) { Resolve(round); return; }
            round.DecideAt = Math.Min(elapsed + wait, round.Deadline);
            Schedule(round, elapsed, round.DecideAt);
            return;
        }
        Award(round, player);
    }

    private static void Resolve(Round round)
    {
        int winner = round.Candidate;
        round.Candidate = -1;
        round.DecideAt = long.MaxValue;
        if (winner >= 0) Award(round, winner);
    }

    private static void Award(Round round, int player)
    {
        round.Hit[player] = true;
        if (round.Rules.Blitz) round.Hit[1 - player] = true;
        round.Scores[player]++;
        for (int i = 0; i < 2; i++)
            Send(round.Players[i], new("score", round.Id, Slot: round.Slot, X: round.Hit[i] ? 1 : 0,
                You: round.Scores[i], Them: round.Scores[1 - i]));
    }

    internal static void Disconnected(Client client)
    {
        lock (Gate)
        {
            Offers.RemoveAll(o => o.From == client || o.To == client);
            if (Rounds.TryGetValue(client, out var round))
                Finish(round, round.Clock.ElapsedMilliseconds >= round.Deadline ? -1 : round.Players[0] == client ? 0 : 1);
        }
    }

    private static void Finish(Round round, int forfeiting)
    {
        round.Timer?.Dispose();
        if (round.Rules.Version == 2)
            foreach (var target in round.Targets.Values.Where(t => t.Candidate >= 0).ToArray())
            {
                round.Targets.Remove(target.Packet.Slot);
                SendScores(round, target.Candidate, target.Packet.Slot, round.Points[target.Candidate].Hit(true));
            }
        if (round.Candidate >= 0) Resolve(round);
        for (int i = 0; i < 2; i++) Rounds.Remove(round.Players[i]);
        for (int i = 0; i < 2; i++)
        {
            string outcome = forfeiting >= 0 ? (forfeiting == i ? "forfeit" : "opponent-left") :
                round.Scores[i] == round.Scores[1 - i] ? "draw" : round.Scores[i] > round.Scores[1 - i] ? "win" : "loss";
            Send(round.Players[i], new("end", round.Id, You: round.Scores[i], Them: round.Scores[1 - i], Outcome: outcome));
        }
    }

    private static void Send(Client client, DuelPacket packet)
    {
        if (client.IsReady) _ = ObserveAsync(client.SendAsync(DuelProtocol.Encode(packet), CancellationToken.None));
    }

    private static async Task ObserveAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); } catch (Exception) { }
    }
}
