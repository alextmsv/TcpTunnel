using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TCPTunnel;

internal static class MenuRenderProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputCharacterW", CharSet = CharSet.Unicode)]
    private static extern bool ReadCharacters(IntPtr output, StringBuilder buffer, uint count, Coord position, out uint read);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputAttribute")]
    private static extern bool ReadAttributes(IntPtr output, ushort[] buffer, uint count, Coord position, out uint read);

    internal static bool RunResize(string resultPath, bool exit = false, bool main = false)
    {
        PseudoConsoleTests.OpenConsoleOutput();
        using var input = new FileStream("CONIN$", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        SetStdHandle(-10, input.SafeFileHandle.DangerousGetHandle());
        Console.Clear();
        Console.SetCursorPosition(0, 0);
        Console.Write("PRESERVE-PRIMARY-HISTORY");
        bool cursorVisible = Console.CursorVisible;
        GetConsoleMode(GetStdHandle(-11), out uint originalMode);
        ConsoleGraphic.Enabled = true;
        var menu = new Menu();
        string[] choices = { "First", "Second", "Back" };
        var ready = Task.Run(async () =>
        {
            await Task.Delay(250);
            File.WriteAllText(resultPath + ".ready", "ready");
        });
        if (main)
        {
            ConsoleGraphic.SetMenuScreen(true);
            new ConsoleGraphic().TryClear(0, 0);
            typeof(Menu).GetField("mainMenuFrame", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(menu, true);
            typeof(Menu).GetMethod("DrawMenuFrame", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(menu, new object[] { choices, 1 });
            typeof(Menu).GetMethod("ReadMenuKey", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(menu, new object[] { choices, 1 });
        }
        else
        {
            var read = typeof(Menu).GetMethod("ReadOptionsChoice", BindingFlags.NonPublic | BindingFlags.Instance)!;
            read.Invoke(menu, new object?[] { "Resize", choices, 1, null, null, null, null });
        }
        ready.GetAwaiter().GetResult();
        var (rows, _) = Capture();
        if (main && rows.Count(row => row.Contains(Lang.Get(TextId.MenuTagline))) != 1)
            throw new Exception("Resizing duplicated or lost the main-menu header");
        if (rows.Count(row => row.Contains("First")) != 1 || rows.Count(row => row.Contains("Second")) != 1 ||
            rows.Count(row => row.Contains("Back")) != 1 || rows[0][0] != '+' || rows[^1][^1] != '+')
            throw new Exception("Resize left duplicate content or damaged the frame");
        if (exit)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                var (restored, _) = Capture();
                GetConsoleMode(GetStdHandle(-11), out uint mode);
                bool historyPreserved = !ConsoleGraphic.MenuScreenSupported || restored.Any(row => row.Contains("PRESERVE-PRIMARY-HISTORY"));
                File.WriteAllText(resultPath, historyPreserved &&
                    Console.CursorVisible == cursorVisible && mode == originalMode ? "PASS" : "FAIL exit cleanup");
            };
            Environment.Exit(0);
        }
        var leave = typeof(ConsoleGraphic).GetMethod("SetMenuScreen", BindingFlags.NonPublic | BindingFlags.Static);
        leave?.Invoke(null, new object[] { false });
        var (original, _) = Capture();
        if (ConsoleGraphic.MenuScreenSupported && !original.Any(row => row.Contains("PRESERVE-PRIMARY-HISTORY")))
            throw new Exception("Menu redraws share and overwrite the primary history buffer");
        return true;
    }

    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int kind, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput();
        ConsoleGraphic.Enabled = true;
        ConsoleTheme.MenuText = ConsoleColor.Yellow;
        ConsoleTheme.SelectionBackground = ConsoleColor.Cyan;
        ConsoleGraphic.BorderSnakeGlyph = '#';
        ConsoleGraphic.BorderSnakeColor = ConsoleColor.Red;
        var geometry = (Console.WindowWidth, Console.WindowHeight);
        var menu = new Menu();
        typeof(Menu).GetField("mainMenuFrame", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(menu, true);
        var draw = typeof(Menu).GetMethod("DrawMenuFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var move = typeof(Menu).GetMethod("AnimateSelection", BindingFlags.NonPublic | BindingFlags.Static)!;

        ConsoleTheme.SelectionStyle = MenuSelectionStyle.Fill;
        Console.ResetColor();
        ConsoleColor normalBackground = Console.BackgroundColor;
        foreach (ConsoleColor background in Enum.GetValues<ConsoleColor>())
        foreach (ConsoleColor textColor in new[] { ConsoleColor.Red, ConsoleColor.Green })
        {
            ConsoleTheme.SelectionBackground = background;
            ConsoleTheme.MenuText = textColor;
            ConsoleGraphic.DrawMenuOption("Visible", 0, 5, 1, true, false, 0);
            var (fillRows, fillAttributes) = Capture();
            int foreground = fillAttributes[geometry.Item1 + 7] & 15;
            if ((fillAttributes[geometry.Item1 + 7] >> 4) != (int)background ||
                foreground == (int)background ||
                (foreground != (int)ConsoleColor.Black && foreground != (int)ConsoleColor.White) ||
                !fillRows[1].Contains("Visible") ||
                (background == ConsoleColor.Blue && foreground != (int)ConsoleColor.White) ||
                (background == ConsoleColor.Yellow && foreground != (int)ConsoleColor.Black))
                throw new Exception("Fill must retain the configured background and contrast the text: " + background);
            ConsoleGraphic.DrawMenuOption("Visible", 0, 5, 1, false, false, 0);
            var (_, normalAttributes) = Capture();
            if (normalAttributes[geometry.Item1 + 7] != (ushort)(((int)normalBackground << 4) | (int)textColor))
                throw new Exception("Fill did not restore the unselected colors");
        }
        ConsoleTheme.MenuText = ConsoleColor.Yellow;
        ConsoleTheme.SelectionBackground = ConsoleColor.Cyan;

        foreach (ConsoleColor color in new[] { ConsoleColor.Black, ConsoleColor.Red })
        {
            ConsoleGraphic.DrawMenuOption("Color: sample", 0, 5, 1, true, false, 0, 7, color);
            var (_, previewFill) = Capture();
            ushort expectedPadding = (ushort)((int)ConsoleColor.Cyan << 4);
            ushort expectedPreview = color == ConsoleColor.Black
                ? (ushort)(((int)ConsoleColor.Gray << 4) | (int)ConsoleColor.Black)
                : (ushort)(((int)normalBackground << 4) | (int)color);
            if (previewFill[geometry.Item1 + 14] != expectedPreview)
                throw new Exception("Fill preview lost the configured preview color: " + color);
            if (previewFill[geometry.Item1 + 21] != expectedPadding)
                throw new Exception("Fill padding lost the configured highlight/contrast: " + color);
        }

        ConsoleTheme.SelectionStyle = MenuSelectionStyle.Brackets;
        new ConsoleGraphic().TryClear(0, 0);
        const string preview = "Border: black";
        ConsoleGraphic.DrawMenuOption(preview, 0, 5, 1, true, false, 0, 8, ConsoleColor.Black);
        var (previewRows, previewAttributes) = Capture();
        int close = previewRows[1].IndexOf(']');
        if (close < 0 || previewAttributes[geometry.Item1 + 5] != (ushort)ConsoleColor.Cyan ||
            previewAttributes[geometry.Item1 + close] != (ushort)ConsoleColor.Cyan)
            throw new Exception("Color preview leaked into selection brackets");

        foreach (MenuSelectionStyle instant in new[] { MenuSelectionStyle.Fill, MenuSelectionStyle.Brackets })
        {
            ConsoleTheme.SelectionStyle = instant;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            ConsoleGraphic.DrawMenuOption("Instant", 0, 5, 1, true, true, 60);
            if (timer.ElapsedMilliseconds >= 350) throw new Exception(instant + " still uses matrix delays");
        }

        ConsoleTheme.SelectionStyle = MenuSelectionStyle.Arrow;
        Menu.left = 5;
        Menu.top = 1;
        string[] animatedChoices = { new string('A', 55), "Next" };
        ConsoleGraphic.DrawMenuOption(animatedChoices[0], 0, 5, 1, true, false, 0);
        ConsoleGraphic.DrawMenuOption(animatedChoices[1], 1, 5, 1, false, false, 0);
        var earlyMarker = Task.Run(async () => { await Task.Delay(35); return Capture().Item1[2].Contains('>'); });
        move.Invoke(null, new object[] { animatedChoices, 0, 1 });
        if (!earlyMarker.GetAwaiter().GetResult()) throw new Exception("Arrow waits for previous matrix animation");

        foreach (AppLanguage language in Enum.GetValues<AppLanguage>())
        foreach (MenuSelectionStyle style in Enum.GetValues<MenuSelectionStyle>())
        {
            Lang.Set(language);
            ConsoleTheme.SelectionStyle = style;
            string[] choices = {
                Lang.Get(TextId.HostServer), Lang.Get(TextId.ConnectToHub), Lang.Get(TextId.ChangeNickname),
                Lang.Get(TextId.GraphicsOptions), Lang.Get(TextId.LanguageMenu), Lang.Get(TextId.Exit)
            };
            if (!new ConsoleGraphic().TryClear(0, 0) || draw.Invoke(menu, new object[] { choices, 1 }) is not true)
                throw new Exception("Menu frame did not complete");
            var layout = MenuPresentation.Layout(geometry.Item1, geometry.Item2, choices.Length);
            var (rows, attributes) = Capture();
            if (geometry.Item1 >= 71 && geometry.Item2 >= 16 &&
                !rows.Any(row => row.Contains(language == AppLanguage.Russian
                    ? "Лёгкий и истинный хаб мессенджер" : "Lightweight true hub-messenger")))
                throw new Exception("Missing localized subtitle");
            for (int index = 0; index < choices.Length; index++)
            {
                int row = layout.Top + index * layout.Spacing;
                if (row >= geometry.Item2 - 1 || !rows[row].Substring(7).StartsWith(choices[index]))
                    throw new Exception($"Missing/alignment: {language} {style} row {row}: {rows[Math.Min(row, rows.Length - 1)]}");
                ushort colors = attributes[row * geometry.Item1 + 7];
                int expected = index != 1 ? (int)ConsoleColor.Yellow : style == MenuSelectionStyle.Fill
                    ? (int)ConsoleColor.Cyan << 4 : (int)ConsoleColor.Cyan;
                if (colors != expected) throw new Exception($"Incorrect theme: {colors}, expected {expected}");
            }
            int selectedRow = layout.Top + layout.Spacing;
            if ((rows[selectedRow].Contains('>')) != (style == MenuSelectionStyle.Arrow) ||
                (rows[selectedRow].Contains('[')) != (style == MenuSelectionStyle.Brackets))
                throw new Exception("Selection decorations are mixed");

            move.Invoke(null, new object[] { choices, 1, 2 });
            var (moved, movedAttributes) = Capture();
            if (moved[selectedRow].Contains('>') || moved[selectedRow].Contains('[') ||
                movedAttributes[selectedRow * geometry.Item1 + 7] != (ushort)ConsoleColor.Yellow)
                throw new Exception("Stale selection decoration");
            if (ConsoleGraphic.BorderSnakeGlyph != '#' || ConsoleGraphic.BorderSnakeColor != ConsoleColor.Red ||
                geometry != (Console.WindowWidth, Console.WindowHeight)) throw new Exception("Changed existing customization or size");

            string? captureDirectory = Environment.GetEnvironmentVariable("TCPTUNNEL_MENU_CAPTURE");
            if (captureDirectory != null)
            {
                Directory.CreateDirectory(captureDirectory);
                File.WriteAllText(Path.Combine(captureDirectory, $"{geometry.Item1}x{geometry.Item2}-{language}-{style}.json"),
                    JsonSerializer.Serialize(new { Width = geometry.Item1, Height = geometry.Item2, Rows = rows, Attributes = attributes }));
            }
        }
        lock (ConsoleGraphic.borderAnimationLock)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var step = typeof(ConsoleGraphic).GetField("borderAnimationStep", flags)!;
            var render = typeof(ConsoleGraphic).GetMethod("RenderSnakeLayerLocked", flags)!;
            var coordinate = typeof(ConsoleGraphic).GetMethod("GetBorderCell", flags)!;
            var desiredChars = typeof(ConsoleGraphic).GetField("desiredBorderCharacters", flags)!;
            var desiredAttrs = typeof(ConsoleGraphic).GetField("desiredBorderAttributes", flags)!;
            foreach (char glyph in new[] { '+', '-', '~', '#' })
            {
                ConsoleGraphic.BorderSnakeGlyph = glyph;
                int perimeter = 2 * geometry.Item1 + 2 * (geometry.Item2 - 2);
                for (int phase = 0; phase < perimeter; phase += 3)
                {
                    step.SetValue(null, phase);
                    render.Invoke(null, null);
                    var (rows, attributes) = Capture();
                    char[] chars = (char[])desiredChars.GetValue(null)!;
                    ushort[] attrs = (ushort[])desiredAttrs.GetValue(null)!;
                    for (int index = 0; index < perimeter; index++)
                    {
                        object cell = coordinate.Invoke(null, new object[] { index, geometry.Item1, geometry.Item2 })!;
                        int x = (int)cell.GetType().GetField("X")!.GetValue(cell)!;
                        int y = (int)cell.GetType().GetField("Y")!.GetValue(cell)!;
                        if (rows[y][x] != chars[index] || attributes[y * geometry.Item1 + x] != attrs[index])
                            throw new Exception("Stale snake character or color at " + x + "," + y);
                    }
                }
            }
        }
        string? previousSession = Environment.GetEnvironmentVariable("WT_SESSION");
        try
        {
            Environment.SetEnvironmentVariable("WT_SESSION", "conpty-test");
            foreach (int background in new[] { 0xFFFFFF, 0x000000 })
            {
                ConsoleTheme.BackgroundMode = BackgroundColorMode.WindowsTerminal;
                ConsoleTheme.BackgroundCustomRgb = background;
                if (!WindowsTerminalTheme.TryApplyBackground(background, out string error)) throw new Exception(error);
                WindowsTerminalTheme.RefreshForActivation(false);
                TextWriter originalOutput = Console.Out;
                using (var output = new FrameWriter())
                {
                    try
                    {
                        Console.SetOut(output);
                        if (!WindowsTerminalTheme.RefreshForActivation(true) ||
                            output.Writes != 1 || !output.ToString().Contains(WindowsTerminalTheme.BackgroundSequence(background)))
                            throw new Exception("Returning focus must reapply the terminal background once");
                        output.Writes = 0;
                        if (WindowsTerminalTheme.RefreshForActivation(true) || output.Writes != 0)
                            throw new Exception("An active terminal must not repeatedly reset its theme");
                    }
                    finally { Console.SetOut(originalOutput); }
                }
                ConsoleGraphic.InvalidateVisualTheme();
                new ConsoleGraphic().TryClear(0, 0);
                foreach (ConsoleColor configured in Enum.GetValues<ConsoleColor>())
                {
                    ConsoleTheme.MenuText = configured;
                    ConsoleGraphic.DrawMenuOption("Configured color", 0, 5, 1, false, false, 0);
                    var (_, attrs) = Capture();
                    if ((attrs[geometry.Item1 + 7] & 15) != (int)configured)
                        throw new Exception("Terminal background replaced configured foreground: " + configured);
                }
                lock (ConsoleGraphic.borderAnimationLock)
                {
                    var flags = BindingFlags.NonPublic | BindingFlags.Static;
                    var resync = typeof(ConsoleGraphic).GetField("forceFullBorderResync", flags)!;
                    var render = typeof(ConsoleGraphic).GetMethod("RenderSnakeLayerLocked", flags)!;
                    resync.SetValue(null, true);
                    TextWriter savedOutput = Console.Out;
                    using var capture = new FrameWriter();
                    try
                    {
                        Console.SetOut(capture);
                        render.Invoke(null, null);
                        if (capture.Writes != 1) throw new Exception("Terminal border frame must use one write");
                        capture.GetStringBuilder().Clear(); capture.Writes = 0;
                        render.Invoke(null, null);
                        if (capture.Writes != 0) throw new Exception("Unchanged border must not redraw");
                    }
                    finally { Console.SetOut(savedOutput); resync.SetValue(null, true); }
                }
            }
        }
        finally
        {
            WindowsTerminalTheme.TryClearBackground(out _);
            ConsoleTheme.BackgroundMode = BackgroundColorMode.Off;
            Environment.SetEnvironmentVariable("WT_SESSION", previousSession);
        }
        return true;
    }

    private sealed class FrameWriter : StringWriter
    {
        public int Writes;
        public override void Write(string? value) { Writes++; base.Write(value); }
    }
    private static (string[], ushort[]) Capture()
    {
        int count = Console.BufferWidth * Console.WindowHeight;
        var text = new StringBuilder(count);
        var attributes = new ushort[count];
        IntPtr handle = GetStdHandle(-11);
        if (!ReadCharacters(handle, text, (uint)count, default, out uint read) || read != count ||
            !ReadAttributes(handle, attributes, (uint)count, default, out read) || read != count)
            throw new Exception("Unable to read console cells");
        string content = text.ToString();
        return (Enumerable.Range(0, Console.WindowHeight)
            .Select(row => content.Substring(row * Console.BufferWidth, Console.BufferWidth)).ToArray(), attributes);
    }
}
