using System.Collections;
using System.Reflection;
using TCPTunnel;

internal static class MenuMatrixProbe
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput(); ConsoleGraphic.Enabled = true; ConsoleGraphic.ConfigureConsole(100, 30); Lang.Set(AppLanguage.English);
        ConsoleTheme.SelectionStyle = MenuSelectionStyle.Brackets; ApplicationSettings.Current.MenuMatrixText = true;
        typeof(ConsoleGraphic).GetField("borderAnimationStep", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 40);
        if (!ConsoleGraphic.BorderSnakePaused) ConsoleGraphic.ToggleBorderSnakePause();
        var menu = new Menu(); string[] choices = { "First concurrent button", "Second concurrent button", "Back" };
        typeof(Menu).GetField("mainMenuFrame", Flags)!.SetValue(menu, true); typeof(Menu).GetField("animateNextMenuFrame", Flags)!.SetValue(menu, true);
        ConsoleGraphic.SetMenuScreen(true); new ConsoleGraphic().Clear(0, 0);
        typeof(Menu).GetMethod("DrawMenuFrame", Flags)!.Invoke(menu, new object[] { choices, 0 });
        var elements = ((IEnumerable)typeof(Menu).GetField("menuReveals", Flags)!.GetValue(menu)!).Cast<object>().ToArray();
        if (elements.Length <= choices.Length) throw new Exception("Elements=" + elements.Length);
        int[] delays = elements.Select(element => (int)element.GetType().GetField("Delay", Flags)!.GetValue(element)!).ToArray();
        if (delays.Max() - delays.Min() > 20) throw new Exception("Delay spread");
        Thread.Sleep(70); typeof(Menu).GetMethod("TickMenuReveal", Flags)!.Invoke(menu, new object[] { 0, false });
        if (elements.Any(element => (int)element.GetType().GetField("Shown", Flags)!.GetValue(element)! == 0)) throw new Exception("An element did not start");
        var rows = ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).Item1;
        if (rows[0][0] != '+' || !rows[Menu.top].Contains('[') || !rows[Menu.top].Contains(']')) throw new Exception("Frame: " + rows[0][0] + ", row=" + rows[Menu.top]);
        typeof(Menu).GetMethod("TickMenuReveal", Flags)!.Invoke(menu, new object[] { 0, true });
        rows = ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).Item1;
        if (!choices.All(choice => rows.Any(row => row.Contains(choice)))) throw new Exception("Final labels: " + String.Join("\n", rows));
        var prepare = typeof(Menu).GetMethod("PrepareOptionsFrame", Flags)!;
        var continuation = typeof(Menu).GetMethod("ContinueOptionsFrame", Flags)!;
        var tick = typeof(Menu).GetMethod("TickMenuReveal", Flags)!;
        string[] options = { "Speed: slow", "Unchanged button", "Back" };
        typeof(Menu).GetField("mainMenuFrame", Flags)!.SetValue(menu, false);
        prepare.Invoke(menu, new object[] { "Settings", options, 0 });
        tick.Invoke(menu, new object[] { 0, true });
        int surface = ConsoleGraphic.SurfaceRevision;
        var unchanged = Capture()[Menu.top + 1];
        continuation.Invoke(menu, new object[] { 0 });
        options[0] = "Speed: fast";
        prepare.Invoke(menu, new object[] { "Settings", options, 0 });
        elements = ((IEnumerable)typeof(Menu).GetField("menuReveals", Flags)!.GetValue(menu)!).Cast<object>().ToArray();
        if (ConsoleGraphic.SurfaceRevision != surface || elements.Length != 1 ||
            (int)elements[0].GetType().GetField("Start", Flags)!.GetValue(elements[0])! != 7 ||
            Capture()[Menu.top + 1] != unchanged)
            throw new Exception("An option toggle repainted the screen or restarted an unchanged label");
        tick.Invoke(menu, new object[] { 0, true });
        if (!Capture()[Menu.top].Contains("Speed: fast")) throw new Exception("Updated value missing");
        continuation.Invoke(menu, new object[] { 0 }); options[0] = "Speed: x";
        prepare.Invoke(menu, new object[] { "Settings", options, 0 }); tick.Invoke(menu, new object[] { 0, true });
        string shortened = Capture()[Menu.top];
        if (!shortened.Contains("[ Speed: x ]") || shortened.Contains("ast")) throw new Exception("Shrinking a value left stale text: " + shortened);
        continuation.Invoke(menu, new object[] { 0 }); ConsoleTheme.MenuText = ConsoleColor.Yellow;
        prepare.Invoke(menu, new object[] { "Settings", options, 0 });
        if (((IEnumerable)typeof(Menu).GetField("menuReveals", Flags)!.GetValue(menu)!).Cast<object>().Any()) throw new Exception("Color change replayed menu animation");
        TestNicknameOverlap();
        return true;
    }

    private static string[] Capture() => ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).Item1;

    private static void TestNicknameOverlap()
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var reset = typeof(ConsoleGraphic).GetMethod("ResetBorderAttributeCacheLocked", statics)!;
        var overlay = typeof(ConsoleGraphic).GetMethod("OverlaySnakeLocked", statics)!;
        var chars = typeof(ConsoleGraphic).GetField("desiredBorderCharacters", statics)!;
        var mask = typeof(ConsoleGraphic).GetField("nicknameBorderCells", statics)!;
        const int width = 100, height = 30, perimeter = 2 * width + 2 * (height - 2);
        char[] nickname = "alextmsv".ToCharArray();
        lock (ConsoleGraphic.borderAnimationLock)
        {
            reset.Invoke(null, new object[] { width, height });
            for (int step = 0; step < perimeter; step++)
            foreach (bool nicknameFirst in new[] { false, true })
            {
                Array.Clear((bool[])mask.GetValue(null)!);
                object?[] named = { step, ConsoleColor.Green, '-', perimeter, nickname };
                object?[] decorative = { step, ConsoleColor.Green, '-', perimeter, null };
                overlay.Invoke(null, nicknameFirst ? named : decorative);
                overlay.Invoke(null, nicknameFirst ? decorative : named);
                char[] actual = (char[])chars.GetValue(null)!;
                for (int offset = 0; offset < nickname.Length; offset++)
                    if (actual[(step - offset + perimeter) % perimeter] != nickname[nickname.Length - 1 - offset])
                        throw new Exception("A decorative snake replaced a nickname at step " + step);
            }
        }
        new ConsoleGraphic().Clear(0, 0);
    }
}
