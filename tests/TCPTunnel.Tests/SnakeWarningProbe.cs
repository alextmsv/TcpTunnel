using System.Reflection;
using System.Diagnostics;
using TCPTunnel;

internal static class SnakeWarningProbe
{
    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput(); ConsoleGraphic.Enabled = true;
        ConsoleGraphic.ConfigureConsole(100, 30); Lang.Set(AppLanguage.Russian);
        ConsoleGraphic.BorderAnimationDelayMilliseconds = 87;
        if (!Menu.HasCustomSnakeSettings()) return false;
        var menu = new Menu(); const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Menu).GetField("snakeCustomizationActive", instance)!.SetValue(menu, true);
        typeof(Menu).GetField("snakeWarningStarted", instance)!.SetValue(menu, Stopwatch.GetTimestamp());
        var draw = typeof(Menu).GetMethod("DrawMenuFrame", instance)!;
        draw.Invoke(menu, new object[] { new[] { "Speed", "Color", "Symbol", "Back" }, 0 });
        var capture = typeof(MenuRenderProbe).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!;
        var visible = ((string[], ushort[]))capture.Invoke(null, null)!;
        int row = (ConsoleGraphic.ContentTop + ConsoleGraphic.ContentBottom) / 2;
        int column = visible.Item1[row].IndexOf("... грязный хакер ...", StringComparison.Ordinal);
        if (column < 0 || (visible.Item2[row * Console.BufferWidth + column] & 15) != (int)ConsoleColor.DarkRed) return false;
        Thread.Sleep(550);
        typeof(Menu).GetMethod("DrawSnakeWarning", instance)!.Invoke(menu, new object[] { true });
        var hidden = ((string[], ushort[]))capture.Invoke(null, null)!;
        if (hidden.Item1.Any(line => line.Contains("грязный хакер"))) return false;
        Thread.Sleep(500);
        typeof(Menu).GetMethod("DrawSnakeWarning", instance)!.Invoke(menu, new object[] { true });
        var repeated = ((string[], ushort[]))capture.Invoke(null, null)!;
        ConsoleGraphic.BorderAnimationDelayMilliseconds = 75;
        return repeated.Item1[row].Contains("грязный хакер") && !Menu.HasCustomSnakeSettings();
    }
}
