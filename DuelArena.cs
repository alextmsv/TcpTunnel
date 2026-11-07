using System;
using System.Collections.Generic;

namespace TCPTunnel;

internal static class DuelArena
{
    internal static readonly (int X, int Y)[] Grid = MakeGrid();

    private static (int, int)[] MakeGrid()
    {
        var cells = new List<(int, int)>();
        for (int y = 145; y <= DuelProtocol.Height - 38; y += 72)
            for (int x = 38; x <= DuelProtocol.Width - 38; x += 72) cells.Add((x, y));
        return cells.ToArray();
    }

    internal static DuelPacket Target(DuelPacket rules, int id, int cell = -1)
    {
        int radius = rules.Size == "any" ? Random.Shared.Next(3) switch { 0 => 12, 1 => 21, _ => 30 } : DuelProtocol.Radius(rules.Size);
        var (x, y) = cell >= 0 ? Grid[cell] : (Random.Shared.Next(radius, DuelProtocol.Width - radius),
            Random.Shared.Next(118 + radius, DuelProtocol.Height - radius));
        return new("target", rules.Id, Slot: id, X: x, Y: y, Version: 2, Radius: radius,
            Motion: rules.Mode == "moving" ? Random.Shared.Next(1, 4) : 0, Seed: Random.Shared.Next(1, 1000000));
    }

    internal static (int X, int Y) Position(DuelPacket target, string level, long age)
    {
        if (target.Motion == 0) return (target.X, target.Y);
        double angle = target.Seed % 360 * Math.PI / 180;
        double speed = (level == "hard" ? 0.34 : level == "easy" ? 0.16 : 0.24) * (0.8 + target.Seed % 41 / 100.0);
        double x, y;
        if (target.Motion == 3)
        {
            angle += age * speed / 36;
            x = Math.Clamp(target.X, target.Radius + 36, DuelProtocol.Width - target.Radius - 36) + Math.Cos(angle) * 36;
            y = Math.Clamp(target.Y, 110 + target.Radius + 36, DuelProtocol.Height - target.Radius - 36) + Math.Sin(angle) * 36;
        }
        else
        {
            long reverse = DuelProtocol.Rules(level).Lifetime * (24 + target.Seed % 47) / 100;
            double distance = (target.Motion == 2 && age > reverse ? 2 * reverse - age : age) * speed;
            x = target.X + distance * Math.Cos(angle);
            y = target.Y + distance * Math.Sin(angle);
        }
        return (Reflect(x, target.Radius, DuelProtocol.Width - target.Radius),
            Reflect(y, 110 + target.Radius, DuelProtocol.Height - target.Radius));
    }

    private static int Reflect(double value, int min, int max)
    {
        int span = max - min;
        double step = ((value - min) % (2 * span) + 2 * span) % (2 * span);
        return (int)Math.Round(min + Math.Min(step, 2 * span - step));
    }
}

internal sealed class DuelScore
{
    internal int Hits, Bonus, Misses, Streak, BestStreak;
    internal int Points => Hits + Bonus - Misses;
    internal int Hit(bool blitz)
    {
        Hits++;
        if (blitz) return 1;
        Streak++;
        BestStreak = Math.Max(BestStreak, Streak);
        if (Streak < 2) return 1;
        Bonus++;
        return 2;
    }
    internal void Miss(bool blitz) { if (!blitz) { Misses++; Streak = 0; } }
    internal void Expire() => Streak = 0;
}
