using System.Reflection;
using System.Runtime.InteropServices;
using TCPTunnel;

internal static class MouseMenuProbe
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int id, IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleInputW")]
    private static extern bool WriteConsoleInput(IntPtr handle, ConsoleInput.InputRecord[] records, uint count, out uint written);
    private static void Feed(params ConsoleInput.InputRecord[] records)
    {
        if (!WriteConsoleInput(GetStdHandle(-10), records, (uint)records.Length, out uint written) || written != records.Length) throw new Exception("Console event injection failed.");
    }
    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput(); ConsoleGraphic.Enabled = true; ConsoleGraphic.ConfigureConsole(100, 30);
        using var input = new FileStream("CONIN$", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        SetStdHandle(-10, input.SafeFileHandle.DangerousGetHandle());
        var menu = new Menu(); string[] choices = { "First", "Second", "Back" };
        var read = typeof(Menu).GetMethod("ReadOptionsChoice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool returned = false, premature = false;
        Task feeder = Task.Run(async () => {
            await Task.Delay(200);
            Feed(new() { Type = 2, X = 6, Y = 2, Buttons = 1 }, new() { Type = 2, X = 6, Y = 2, Buttons = 0 },
                new() { Type = 2, X = 6, Y = 2, Buttons = 1, MouseFlags = 2 }, new() { Type = 2, X = 6, Y = 2, Buttons = 0 });
            await Task.Delay(200); premature = returned;
            Feed(new ConsoleInput.InputRecord { Type = 2, X = 6, Y = 2, Buttons = 120u << 16, MouseFlags = 4 });
        });
        var selected = ((int, int))read.Invoke(menu, new object?[] { "Mouse probe", choices, 0, null, null, (Func<int, bool>)(i => i == 1), null })!;
        returned = true; feeder.GetAwaiter().GetResult();
        if (premature || selected != (1, -1) || !ConsoleInput.MouseMode) return false;
        feeder = Task.Run(async () => {
            await Task.Delay(100);
            Feed(new() { Type = 1, KeyDown = 1, Repeat = 1, VirtualKey = (ushort)ConsoleKey.LeftArrow },
                new() { Type = 1, KeyDown = 0, VirtualKey = (ushort)ConsoleKey.LeftArrow },
                new() { Type = 1, KeyDown = 1, Repeat = 1, VirtualKey = (ushort)ConsoleKey.RightArrow },
                new() { Type = 1, KeyDown = 0, VirtualKey = (ushort)ConsoleKey.RightArrow },
                new() { Type = 1, KeyDown = 1, Repeat = 1, VirtualKey = (ushort)ConsoleKey.Escape, Character = (char)27 });
        });
        selected = ((int, int))read.Invoke(menu, new object?[] { "Keyboard probe", choices, 0, null, null, null, null })!;
        feeder.GetAwaiter().GetResult();
        if (selected.Item1 != -1 || ConsoleInput.MouseMode) return false;
        Feed(new() { Type = 1, KeyDown = 0, VirtualKey = 18, Character = '#' },
            new() { Type = 1, KeyDown = 1, Repeat = 1, VirtualKey = (ushort)ConsoleKey.LeftArrow, Character = '%' });
        var hash = ConsoleInput.ReadKey(); var percent = ConsoleInput.ReadKey();
        return hash.KeyChar == '#' && hash.Key != ConsoleKey.End && percent.KeyChar == '%' && percent.Key != ConsoleKey.LeftArrow;
    }
}
