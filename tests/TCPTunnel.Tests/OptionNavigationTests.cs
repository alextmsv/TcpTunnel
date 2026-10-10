using TCPTunnel;

internal static class OptionNavigationTests
{
    public static bool CyclicOptionSelfTest()
    {
        int[] values = { 35, 75, 125, 200 };
        foreach (int value in values)
            if (OptionNavigation.Next(values, OptionNavigation.Next(values, value, 1), -1) != value) return false;
        return OptionNavigation.Next(values, 35, -1) == 200 && OptionNavigation.Next(values, 200, 1) == 35 &&
            OptionNavigation.Next(new[] { true }, true, -1) &&
            !OptionNavigation.Next(new[] { false, true }, true, -1);
    }

    public static bool OptionKeysSelfTest()
    {
        return OptionNavigation.ValueDirection(ConsoleKey.LeftArrow) == -1 &&
            OptionNavigation.ValueDirection(ConsoleKey.RightArrow) == 1 &&
            OptionNavigation.ValueDirection(ConsoleKey.Enter) == 1 &&
            OptionNavigation.ValueDirection(ConsoleKey.UpArrow) == 0 &&
            OptionNavigation.ValueDirection(ConsoleKey.DownArrow) == 0;
    }
}
