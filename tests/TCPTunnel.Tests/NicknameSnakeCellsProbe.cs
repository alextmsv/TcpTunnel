using System.Reflection;
using TCPTunnel;

internal static class NicknameSnakeCellsProbe
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
    internal static bool Run(string resultPath)
    {
        PseudoConsoleTests.OpenConsoleOutput();
        if (!ConsoleGraphic.UseBatchedBorderFrames) throw new Exception("Batched border frames must be the default");
        ConsoleGraphic.Enabled = true; ConsoleGraphic.ConfigureConsole(100, 30);
        NetWorker.nickname = "alextmsv"; NetWorker.connected = false;
        ApplicationSettings.Current.NicknameSnake = true;
        ConsoleGraphic.BorderSnakeColor = ConsoleColor.Green;
        ConsoleGraphic.ClearRemoteSnakes();
        if (!ConsoleGraphic.BorderSnakePaused) ConsoleGraphic.ToggleBorderSnakePause();
        ConsoleGraphic.SetMenuScreen(true); new ConsoleGraphic().Clear(0, 0);
        var render = typeof(ConsoleGraphic).GetMethod("RenderSnakeLayerLocked", Flags)!;
        var stepField = typeof(ConsoleGraphic).GetField("borderAnimationStep", Flags)!;
        var capture = typeof(MenuRenderProbe).GetMethod("Capture", Flags)!;
        lock (ConsoleGraphic.borderAnimationLock)
        {
            var resync = typeof(ConsoleGraphic).GetField("forceFullBorderResync", Flags)!;
            resync.SetValue(null, true);
            TextWriter original = Console.Out;
            using var writer = new CountingWriter();
            try
            {
                Console.SetOut(writer); render.Invoke(null, null);
                if (writer.Writes != 1) throw new Exception("Default border must be emitted in one write");
                render.Invoke(null, null);
                if (writer.Writes != 1) throw new Exception("An unchanged frame must not be emitted again");
            }
            finally { Console.SetOut(original); resync.SetValue(null, true); render.Invoke(null, null); }
        }
        if (!ConsoleGraphic.TryCaptureConsoleGeometry(out var geometry)) throw new Exception("No console geometry");
        int width = geometry.DrawableWidth;
        int height = geometry.DrawableHeight;
        var cells = new List<(int X, int Y)>();
        for (int x = 0; x < width; x++) cells.Add((x, 0));
        for (int y = 1; y < height; y++) cells.Add((width - 1, y));
        for (int x = width - 2; x >= 0; x--) cells.Add((x, height - 1));
        for (int y = height - 2; y > 0; y--) cells.Add((0, y));
        string? oldSession = Environment.GetEnvironmentVariable("WT_SESSION");
        try
        {
        SnakeFrameDiagnostics.Start(resultPath + ".jsonl");
        ConsoleGraphic.WriteTextRun(4, 10, "NeverLogChatText", 0, 16, ConsoleColor.White, ConsoleColor.Black);
        foreach (int mode in new[] { 0, 1, 2 })
        {
            ConsoleGraphic.UseBatchedBorderFrames = mode == 1 && ConsoleGraphic.EnableVirtualTerminalOutput();
            if (mode == 2)
            {
                Environment.SetEnvironmentVariable("WT_SESSION", "conpty-test");
                ConsoleTheme.BackgroundMode = BackgroundColorMode.WindowsTerminal;
                ConsoleTheme.BackgroundCustomRgb = 0;
                if (!WindowsTerminalTheme.TryApplyBackground(0, out string error)) throw new Exception(error);
            }
        Thread.Sleep(110);
        lock (ConsoleGraphic.borderAnimationLock)
        {
            int cursorLeft = Console.CursorLeft, cursorTop = Console.CursorTop;
            foreach (string nickname in new[] { "alextmsv", "tmsvalex" })
            {
            NetWorker.nickname = nickname;
            for (int step = 0; step < cells.Count * 2; step++)
            {
                stepField.SetValue(null, step % cells.Count); render.Invoke(null, null);
                if (Console.CursorLeft != cursorLeft || Console.CursorTop != cursorTop) throw new Exception("Border rendering moved the cursor");
                var (rows, attrs) = ((string[], ushort[]))capture.Invoke(null, null)!;
                for (int offset = 0; offset < NetWorker.nickname.Length; offset++)
                {
                    var cell = cells[(step - offset + cells.Count) % cells.Count];
                    char expected = NetWorker.nickname[NetWorker.nickname.Length - 1 - offset];
                    if (rows[cell.Y][cell.X] != expected || (attrs[cell.Y * Console.BufferWidth + cell.X] & 15) != (int)ConsoleColor.Green)
                        throw new Exception($"step={step}, offset={offset}, cell={cell.X},{cell.Y}, expected={expected}, actual={rows[cell.Y][cell.X]}, attr={attrs[cell.Y * Console.BufferWidth + cell.X]}");
                }
            }
            }
        }
        }
        lock (ConsoleGraphic.borderAnimationLock)
        {
            NetWorker.nickname = "alextmsv";
            int step = width + height + 30;
            stepField.SetValue(null, step); render.Invoke(null, null);
            var tail = cells[(step - NetWorker.nickname.Length + 1 + cells.Count) % cells.Count];
            ConsoleGraphic.WriteTextRun(tail.X, tail.Y, "-", 0, 1, ConsoleColor.Green, ConsoleColor.Black);
            Thread.Sleep(110); render.Invoke(null, null);
            typeof(ConsoleGraphic).GetField("forceFullBorderResync", Flags)!.SetValue(null, true);
            render.Invoke(null, null);
        }
        }
        finally { SnakeFrameDiagnostics.Stop(); ConsoleGraphic.UseBatchedBorderFrames = false; WindowsTerminalTheme.TryClearBackground(out _); ConsoleTheme.BackgroundMode = BackgroundColorMode.Off; Environment.SetEnvironmentVariable("WT_SESSION", oldSession); }
        if (File.ReadAllText(resultPath + ".jsonl").Contains("NeverLogChatText")) throw new Exception("Trace included interior content");
        var frames = File.ReadAllLines(resultPath + ".jsonl").Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            if (!frames.Any(frame => frame.RootElement.TryGetProperty("Phase", out var phase) && phase.GetString() == "after" &&
                frame.RootElement.GetProperty("Cells").EnumerateArray().Any(cell => cell.GetProperty("Cached").GetString() == "a" && cell.GetProperty("Actual").GetString() == "-")))
                throw new Exception("Trace did not detect an external overwrite of the first nickname character");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
        return true;
    }
    private sealed class CountingWriter : StringWriter
    {
        internal int Writes;
        public override void Write(string? value) { Writes++; base.Write(value); }
    }
}
