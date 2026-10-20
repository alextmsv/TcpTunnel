using System.Reflection;
using System.Runtime.InteropServices;
using TCPTunnel;

internal static class HubLimitTypingProbe
{
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int id, IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleInputW")]
    private static extern bool WriteConsoleInput(IntPtr handle, ConsoleInput.InputRecord[] records, uint count, out uint written);
    private static void Key(IntPtr input, ConsoleKey key, char text = '\0')
    {
        var records = new[] { new ConsoleInput.InputRecord { Type = 1, KeyDown = 1, Repeat = 1, VirtualKey = (ushort)key, Character = text }, new ConsoleInput.InputRecord { Type = 1, KeyDown = 0, VirtualKey = (ushort)key } };
        if (!WriteConsoleInput(input, records, 2, out _)) throw new Exception("Input injection failed.");
    }
    private static string[] Rows() => ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).Item1;
    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput(); ConsoleGraphic.Enabled = true; ConsoleGraphic.ConfigureConsole(100, 30); Lang.Set(AppLanguage.English);
        using var input = new FileStream("CONIN$", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); IntPtr handle = input.SafeFileHandle.DangerousGetHandle(); SetStdHandle(-10, handle);
        bool replaced = false;
        Task feeder = Task.Run(async () => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (!Rows().Any(row => row.Contains("Participant limit"))) await Task.Delay(20, timeout.Token);
            for (int i = 0; i < 4; i++) Key(handle, ConsoleKey.DownArrow);
            Key(handle, ConsoleKey.D1, '1');
            await Task.Delay(150, timeout.Token);
            replaced = Rows().Any(row => row.Contains("including owner): 1")) && !Rows().Any(row => row.Contains("including owner): 64"));
            Key(handle, ConsoleKey.D2, '2'); Key(handle, ConsoleKey.Enter, '\r'); Key(handle, ConsoleKey.DownArrow); Key(handle, ConsoleKey.Enter, '\r');
        });
        var menu = new Menu();
        var options = (HubOptions?)typeof(Menu).GetMethod("ShowHubOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(menu, null);
        feeder.GetAwaiter().GetResult();
        if (!replaced || options?.ParticipantLimit != 12) throw new Exception("Limit typing: replaced=" + replaced + ", final=" + options?.ParticipantLimit);
        feeder = Task.Run(async () => { await Task.Delay(150); Key(handle, ConsoleKey.DownArrow); Key(handle, ConsoleKey.DownArrow); Key(handle, ConsoleKey.Enter, '\r'); Key(handle, ConsoleKey.UpArrow); Key(handle, ConsoleKey.Enter, '\r'); });
        int mode = menu.ChooseConnectionMode(false); feeder.GetAwaiter().GetResult();
        if (mode != 2 || menu.ConnectionSecurity != ConnectionSecurityMode.AllowPlaintext) return false;
        feeder = Task.Run(async () => { await Task.Delay(150); Key(handle, ConsoleKey.Escape, (char)27); });
        mode = menu.ChooseConnectionMode(false); feeder.GetAwaiter().GetResult();
        return mode == -1 && menu.ConnectionSecurity == ConnectionSecurityMode.RequireE2E;
    }
}
