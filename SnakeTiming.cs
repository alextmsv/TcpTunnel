using System;
using System.Diagnostics;

namespace TCPTunnel;

internal static class SnakeTiming
{
    internal static double ReferencePhase(int step, long anchored, int delay, long now)
    {
        double elapsed = Math.Max(0, now - anchored) * 1000.0 / Stopwatch.Frequency;
        double phase = (step + elapsed / delay) % SnakeProtocol.ReferencePerimeterLength;
        return phase < 0 ? phase + SnakeProtocol.ReferencePerimeterLength : phase;
    }
    internal static int LocalStep(int step, long anchored, int delay, long now, int perimeter) =>
        Math.Clamp((int)(ReferencePhase(step, anchored, delay, now) * perimeter / SnakeProtocol.ReferencePerimeterLength), 0, perimeter - 1);
    internal static long RemainingTicks(long anchored, int delay, long now, int perimeter)
    {
        double period = Math.Max(1, (double)Stopwatch.Frequency * delay * SnakeProtocol.ReferencePerimeterLength / (1000.0 * perimeter));
        return Math.Max(1, (long)Math.Ceiling(period - Math.Max(0, now - anchored) % period));
    }
}
