using System.Reflection;
using System.Runtime.InteropServices;
using TCPTunnel;

internal static class ConsoleMenuProbe
{
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public short Type;
        [FieldOffset(4)] public int Down;
        [FieldOffset(8)] public short Repeat;
        [FieldOffset(10)] public short Key;
        [FieldOffset(14)] public char Character;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int kind, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true)]
    private static extern bool WriteConsoleInput(IntPtr input, InputRecord[] records, uint length, out uint written);

    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput();
        IntPtr input = CreateFile("CONIN$", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (input == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        SetStdHandle(-10, input);
        try
        {
            var menu = new Menu();
            var method = typeof(Menu).GetMethod("ReadOptionsChoice", BindingFlags.Instance | BindingFlags.NonPublic)!;
            (int, int) Choose(int selected, params ConsoleKey[] keys)
            {
                var records = keys.SelectMany(key => new[]
                {
                    new InputRecord { Type = 1, Down = 1, Repeat = 1, Key = (short)key },
                    new InputRecord { Type = 1, Down = 0, Repeat = 1, Key = (short)key }
                }).ToArray();
                if (!WriteConsoleInput(input, records, (uint)records.Length, out uint written) || written != records.Length)
                    throw new System.ComponentModel.Win32Exception();
                return ((int, int))method.Invoke(menu, new object?[]
                {
                    "Test options", new[] { "First value", "Second value", "Back" }, selected,
                    null, null, (Func<int, bool>)(index => index < 2), null
                })!;
            }
            foreach (bool graphics in new[] { true, false })
            {
                ConsoleGraphic.Enabled = graphics;
                if (Choose(0, ConsoleKey.DownArrow, ConsoleKey.LeftArrow) != (1, -1) ||
                    Choose(1, ConsoleKey.RightArrow) != (1, 1) ||
                    Choose(2, ConsoleKey.LeftArrow, ConsoleKey.RightArrow, ConsoleKey.Enter) != (2, 1) ||
                    Choose(0, ConsoleKey.UpArrow, ConsoleKey.Enter) != (2, 1) ||
                    Choose(0, ConsoleKey.Escape) != (-1, 0)) return false;
            }
            return Console.WindowWidth == 100 && Console.WindowHeight == 30;
        }
        finally { CloseHandle(input); }
    }
}
