using System;

namespace TCPTunnel
{
    internal static class OptionNavigation
    {
        internal static int ValueDirection(ConsoleKey key) => key switch
        {
            ConsoleKey.LeftArrow => -1,
            ConsoleKey.RightArrow or ConsoleKey.Enter or ConsoleKey.Spacebar => 1,
            _ => 0
        };

        internal static T Next<T>(T[] values, T current, int direction)
        {
            if (values == null || values.Length == 0) throw new ArgumentException("At least one value is required.", nameof(values));
            int index = Array.IndexOf(values, current);
            if (index < 0) return values[direction < 0 ? values.Length - 1 : 0];
            return values[(index + Math.Sign(direction) + values.Length) % values.Length];
        }
    }
}
