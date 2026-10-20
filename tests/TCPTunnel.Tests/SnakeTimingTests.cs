using System.Diagnostics;
using TCPTunnel;

internal static class SnakeTimingTests
{
    public static bool NormalizedSnakeResizeAndDriftSelfTest()
    {
        const int delay = 75, step = 30; long anchored = Stopwatch.GetTimestamp();
        for (int seconds = 0; seconds <= 120; seconds++)
        {
            long now = anchored + seconds * Stopwatch.Frequency;
            double phase = SnakeTiming.ReferencePhase(step, anchored, delay, now) / SnakeProtocol.ReferencePerimeterLength;
            foreach (int perimeter in new[] { 100, 340, 680 })
            {
                int local = SnakeTiming.LocalStep(step, anchored, delay, now, perimeter);
                if (Math.Abs(local / (double)perimeter - phase) > 1.0 / perimeter || SnakeTiming.RemainingTicks(anchored, delay, now, perimeter) < 1) return false;
            }
        }
        return true;
    }
    public static bool MenuMatrixProfileSelfTest()
    {
        var profile = new AppProfile { MenuMatrixText = true, MatrixText = false };
        var loaded = ApplicationSettings.ParseProfile(ApplicationSettings.ParseLines(ApplicationSettings.Serialize(profile)), new());
        return loaded.MenuMatrixText && !loaded.MatrixText && !ApplicationSettings.ParseProfile(new(), new AppProfile { MenuMatrixText = true }).MenuMatrixText;
    }
}
